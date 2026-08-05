using System.ComponentModel;
using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Models;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>
/// Windows에서 물리 디스크를 열거하고 여는 구현.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskService(ILogger<WindowsDiskService>? logger = null) : IDiskService
{
    private readonly ILogger _logger = logger ?? NullLogger<WindowsDiskService>.Instance;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public Task<IReadOnlyList<DiskInfo>> EnumerateDisksAsync(CancellationToken ct = default) =>
        Task.Run(Enumerate, ct);

    private IReadOnlyList<DiskInfo> Enumerate()
    {
        var inspector = new VolumeInspector(_logger);

        // 볼륨 → 디스크 매핑을 먼저 만들어 둡니다. 파티션에 드라이브 문자와 파일 시스템을
        // 붙이려면 필요합니다.
        var volumes = inspector.EnumerateVolumes();
        var volumesByDisk = volumes
            .SelectMany(v => v.Extents.Select(e => (Extent: e, Volume: v)))
            .GroupBy(x => x.Extent.DiskNumber)
            .ToDictionary(g => g.Key, g => g.ToList());

        int? systemDiskNumber = inspector.GetSystemDiskNumber();
        int? bootDiskNumber = inspector.GetBootDiskNumber();
        var pageFileDisks = GetPageFileDiskNumbers(volumes);

        _logger.LogInformation(
            "시스템 디스크: {System}, 부팅 디스크: {Boot}, 페이지 파일 디스크: [{PageFile}]",
            systemDiskNumber?.ToString() ?? "판별 실패",
            bootDiskNumber?.ToString() ?? "판별 실패",
            string.Join(", ", pageFileDisks));

        if (systemDiskNumber is null)
        {
            // 시스템 디스크를 모르면 대상 검증의 핵심 근거가 사라집니다.
            // 조용히 넘어가면 사용자의 부팅 디스크를 지울 수 있으므로 크게 경고합니다.
            _logger.LogError(
                "시스템 디스크를 판별하지 못했습니다. 안전장치가 약해지므로 이 상태로 클론을 " +
                "진행해서는 안 됩니다.");
        }

        var wmiInfo = ReadWmiDiskInfo();
        var disks = new List<DiskInfo>();

        foreach (int number in EnumerateDiskNumbers(wmiInfo))
        {
            try
            {
                var disk = BuildDiskInfo(
                    number, wmiInfo.GetValueOrDefault(number), volumesByDisk.GetValueOrDefault(number) ?? [],
                    systemDiskNumber, bootDiskNumber, pageFileDisks);

                if (disk is not null) disks.Add(disk);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "디스크 {Number} 정보를 읽지 못해 목록에서 제외합니다.", number);
            }
        }

        return disks.OrderBy(d => d.DeviceNumber).ToList();
    }

    private DiskInfo? BuildDiskInfo(
        int number,
        WmiDiskInfo? wmi,
        List<(VolumeExtent Extent, VolumeDetails Volume)> diskVolumes,
        int? systemDiskNumber,
        int? bootDiskNumber,
        IReadOnlySet<int> pageFileDisks)
    {
        string devicePath = $@"\\.\PhysicalDrive{number}";

        using var handle = NativeMethods.CreateFile(
            devicePath,
            0, // 조회에는 접근 권한이 필요 없습니다. 권한 없이도 목록은 보여줄 수 있습니다.
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

        if (handle.IsInvalid)
        {
            _logger.LogDebug("디스크 {Number} 을(를) 열 수 없습니다.", number);
            return null;
        }

        if (!DiskIoctl.TryQuery<DISK_GEOMETRY_EX>(
                handle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, out var geometry))
        {
            _logger.LogDebug("디스크 {Number} 의 지오메트리를 읽지 못했습니다.", number);
            return null;
        }

        int logicalSectorSize = (int)geometry.Geometry.BytesPerSector;
        if (logicalSectorSize <= 0) return null;

        long size = DiskIoctl.TryQuery<GET_LENGTH_INFORMATION>(
            handle, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO, out var lengthInfo)
            ? lengthInfo.Length
            : geometry.DiskSize;

        var descriptor = StorageDescriptorReader.Read(handle);
        int? physicalSectorSize = StorageDescriptorReader.ReadPhysicalSectorSize(handle);
        var layout = DriveLayoutReader.Read(handle);

        // IOCTL_DISK_IS_WRITABLE 이 실패하면 쓰기 방지 상태입니다.
        bool isWritable = DiskIoctl.TryControl(handle, NativeMethods.IOCTL_DISK_IS_WRITABLE);

        // 오프라인 디스크는 열리고 파티션 테이블도 읽히지만 볼륨이 마운트되지 않습니다.
        // 부팅 검사가 "ESP를 찾지 못했다"고 할 때, 디스크가 오프라인이라 그런 것인지
        // 정말 ESP가 없는 것인지는 이 값으로만 갈립니다. 조회에 실패하면(구형 드라이버 등)
        // 온라인으로 봅니다 — 파티션을 읽어 온 시점에서 접근은 되고 있으므로.
        bool isOffline =
            DiskIoctl.TryQuery<GET_DISK_ATTRIBUTES>(
                handle, NativeMethods.IOCTL_DISK_GET_DISK_ATTRIBUTES, out var attributes) &&
            (attributes.Attributes & NativeMethods.DISK_ATTRIBUTE_OFFLINE) != 0;

        var partitions = BuildPartitions(layout, diskVolumes);

        string model = ResolveModel(wmi, descriptor, number);

        return new DiskInfo
        {
            DeviceNumber = number,
            Model = model,
            SerialNumber = Clean(wmi?.SerialNumber) ?? Clean(descriptor?.SerialNumber),
            FirmwareRevision = Clean(wmi?.FirmwareRevision) ?? Clean(descriptor?.ProductRevision),
            SizeBytes = size,
            LogicalSectorSize = logicalSectorSize,
            PhysicalSectorSize = physicalSectorSize ?? logicalSectorSize,
            BusType = descriptor?.BusType ?? DiskBusType.Unknown,
            PartitionStyle = layout.Style,
            DiskGuid = layout.GptDiskId,
            MbrSignature = layout.MbrSignature,
            HasExtendedPartition = layout.HasExtendedPartition,
            IsRemovable = descriptor?.RemovableMedia
                          ?? geometry.Geometry.MediaType == MEDIA_TYPE.RemovableMedia,
            IsReadOnly = !isWritable,
            IsOffline = isOffline,
            IsSystemDisk = systemDiskNumber == number,
            IsBootDisk = bootDiskNumber == number,
            HasPageFile = pageFileDisks.Contains(number),
            Partitions = partitions,
        };
    }

    private static List<PartitionInfo> BuildPartitions(
        DriveLayout layout,
        List<(VolumeExtent Extent, VolumeDetails Volume)> diskVolumes)
    {
        var partitions = new List<PartitionInfo>();

        foreach (var raw in layout.Partitions)
        {
            // 볼륨과 파티션은 시작 오프셋으로 맞춥니다. 커널이 주는 같은 좌표계라 정확히 일치합니다.
            var match = diskVolumes.FirstOrDefault(v => v.Extent.StartingOffset == raw.StartingOffset);
            var volume = match.Volume;

            partitions.Add(new PartitionInfo
            {
                Number = raw.Number,
                StartingOffset = raw.StartingOffset,
                LengthBytes = raw.Length,
                DriveLetter = volume?.DriveLetter,
                VolumeGuidPath = volume?.VolumeGuidPath,
                FileSystem = volume?.FileSystem,
                VolumeLabel = volume?.Label,
                FreeSpaceBytes = volume?.FreeSpaceBytes,
                GptPartitionType = raw.GptType,
                MbrPartitionType = raw.MbrType,
                IsEfiSystemPartition = raw.GptType == GptTypes.EfiSystem ||
                                       raw.MbrType == 0xEF,
                IsActive = raw.IsActive,
            });
        }

        return partitions;
    }

    /// <summary>
    /// 사용자에게 보여줄 모델명. 이 문자열을 사용자가 그대로 타이핑해서 확인하므로,
    /// 사람이 읽고 옮겨 적을 수 있는 이름이어야 합니다.
    /// </summary>
    private static string ResolveModel(WmiDiskInfo? wmi, StorageDescriptor? descriptor, int number)
    {
        if (Clean(wmi?.Model) is { } wmiModel) return wmiModel;

        string combined = string.Join(" ", new[] { descriptor?.VendorId, descriptor?.ProductId }
            .Select(Clean)
            .Where(s => s is not null));

        return string.IsNullOrWhiteSpace(combined) ? $"디스크 {number}" : combined;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>WMI가 아는 디스크 번호와, 혹시 WMI가 놓친 번호까지 훑습니다.</summary>
    private static IEnumerable<int> EnumerateDiskNumbers(Dictionary<int, WmiDiskInfo> wmiInfo)
    {
        var numbers = new SortedSet<int>(wmiInfo.Keys);

        // WMI가 실패했거나 일부 장치를 빠뜨린 경우를 대비해 앞쪽 번호를 직접 훑습니다.
        // 열리지 않는 번호는 BuildDiskInfo에서 null로 걸러집니다.
        for (int i = 0; i < 16; i++) numbers.Add(i);

        return numbers;
    }

    private sealed record WmiDiskInfo(
        int Index, string? Model, string? SerialNumber, string? FirmwareRevision);

    /// <summary>
    /// WMI에서 모델명·시리얼을 읽습니다. IOCTL보다 사람이 읽기 좋은 모델명을 주는 경우가 많습니다.
    /// </summary>
    private Dictionary<int, WmiDiskInfo> ReadWmiDiskInfo()
    {
        var result = new Dictionary<int, WmiDiskInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, SerialNumber, FirmwareRevision FROM Win32_DiskDrive");

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    if (item["Index"] is not uint index) continue;

                    result[(int)index] = new WmiDiskInfo(
                        (int)index,
                        item["Model"] as string,
                        item["SerialNumber"] as string,
                        item["FirmwareRevision"] as string);
                }
            }
        }
        catch (Exception ex)
        {
            // WMI가 죽어 있어도 IOCTL만으로 목록을 만들 수 있습니다. 치명적이지 않습니다.
            _logger.LogWarning(ex, "WMI에서 디스크 정보를 읽지 못했습니다. IOCTL 정보만 사용합니다.");
        }

        return result;
    }

    /// <summary>
    /// 페이지 파일이 올라가 있는 디스크 번호들.
    /// </summary>
    /// <remarks>
    /// 페이지 파일이 있는 디스크는 커널이 계속 쓰고 있으므로 대상이 될 수 없습니다.
    /// 볼륨을 잠그는 것도 불가능합니다.
    /// </remarks>
    private IReadOnlySet<int> GetPageFileDiskNumbers(IReadOnlyList<VolumeDetails> volumes)
    {
        var disks = new HashSet<int>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");

            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    // Name은 "C:\pagefile.sys" 형식입니다.
                    if (item["Name"] is not string name || name.Length < 2) continue;

                    string letter = name[0].ToString().ToUpperInvariant();

                    var volume = volumes.FirstOrDefault(v =>
                        string.Equals(v.DriveLetter, letter, StringComparison.OrdinalIgnoreCase));

                    if (volume is null) continue;

                    foreach (var extent in volume.Extents) disks.Add(extent.DiskNumber);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "페이지 파일 위치를 확인하지 못했습니다.");
        }

        return disks;
    }

    public IBlockDevice OpenRead(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return RawDiskDevice.OpenRead(disk.DevicePath);
    }

    public IBlockDevice OpenWriteExclusive(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        // 대상 디스크를 오프라인으로 내리는 것이 정합성의 핵심입니다. 이게 없으면 클론이
        // 파티션 테이블을 쓰는 순간 대상에 NTFS 볼륨이 생기고, Windows가 자동 마운트해
        // 로그를 재생하며 우리가 쓴 데이터를 덮어씁니다(실기에서 확인한 불일치의 원인).
        DiskOfflineScope? offline = null;
        VolumeLock? volumeLock = null;

        try
        {
            offline = DiskOfflineScope.Take(disk, _logger);
        }
        catch (Exception ex)
        {
            // 동적 디스크나 일부 특수 구성에서는 오프라인 전환이 실패할 수 있습니다.
            // 그때는 볼륨 잠금으로 물러섭니다. 오프라인만큼 강하지는 않지만,
            // 이미 마운트된 볼륨의 즉각적인 쓰기는 막습니다.
            _logger.LogWarning(ex,
                "대상 디스크를 오프라인으로 내리지 못했습니다. 볼륨 잠금으로 대체합니다. " +
                "빈 디스크가 아니라면 클론 중 자동 마운트로 정합성이 깨질 수 있으니 주의하십시오.");
        }

        try
        {
            // 오프라인 전환에 성공했으면 그 디스크의 볼륨은 이미 전부 사라졌으므로,
            // 볼륨 잠금을 또 시도하면 오래된 경로를 열려다 실패합니다. 오프라인이 강한
            // 보장이므로 그 경우엔 잠금을 건너뜁니다. 오프라인에 실패했을 때만 잠급니다.
            if (offline is null)
            {
                volumeLock = VolumeLock.LockDisk(disk, logger: _logger);
            }

            var device = RawDiskDevice.OpenWrite(disk.DevicePath);
            return new LockedDiskDevice(device, volumeLock, offline, disk, this, _logger);
        }
        catch
        {
            volumeLock?.Dispose();
            offline?.Dispose();
            throw;
        }
    }

    public void RefreshDiskProperties(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        try
        {
            using var handle = NativeMethods.CreateFile(
                disk.DevicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

            if (handle.IsInvalid)
            {
                _logger.LogWarning("디스크 {Disk} 속성 갱신을 위해 열지 못했습니다.", disk.DeviceNumber);
                return;
            }

            if (DiskIoctl.TryControl(handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES))
            {
                _logger.LogInformation("디스크 {Disk} 의 파티션 테이블을 다시 읽도록 알렸습니다.", disk.DeviceNumber);
            }
        }
        catch (Exception ex)
        {
            // 실패해도 데이터는 이미 정상적으로 쓰였습니다. 재부팅하면 반영됩니다.
            _logger.LogWarning(ex, "디스크 {Disk} 속성 갱신에 실패했습니다.", disk.DeviceNumber);
        }
    }

    public Task<SafeRemoveResult> SafeRemoveAsync(DiskInfo disk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return Task.Run(() => SafeRemove(disk), ct);
    }

    /// <summary>
    /// 대상 디스크를 오프라인으로 내립니다. 오프라인 전환은 그 디스크의 볼륨을 강제로
    /// 디스마운트하며(캐시를 비우고 열린 핸들을 무효화), 이는 디스크 관리의 "오프라인" 및
    /// 상용 클론 도구의 안전 제거와 같은 동작입니다. 오프라인이 된 뒤에는 마운트된 볼륨이
    /// 없으므로 사용자가 USB를 뽑아도 데이터가 손상되지 않습니다.
    /// </summary>
    private SafeRemoveResult SafeRemove(DiskInfo disk)
    {
        try
        {
            using var handle = NativeMethods.CreateFile(
                disk.DevicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                string detail = new Win32Exception(error).Message;
                _logger.LogWarning(
                    "디스크 {Disk} 안전 제거를 위해 열지 못했습니다: {Detail}", disk.DeviceNumber, detail);
                return new SafeRemoveResult(false, detail);
            }

            // 오프라인으로 내려 볼륨을 디스마운트하고, 실제 상태에 반영시킵니다.
            SetDiskOffline(handle);
            DiskIoctl.TryControl(handle, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES);

            _logger.LogInformation(
                "디스크 {Disk} 을(를) 오프라인으로 내렸습니다 — 이제 안전하게 뽑을 수 있습니다.",
                disk.DeviceNumber);

            return new SafeRemoveResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "디스크 {Disk} 안전 제거에 실패했습니다.", disk.DeviceNumber);
            return new SafeRemoveResult(false, ex.Message);
        }
    }

    /// <summary>디스크에 오프라인 속성을 설정합니다(재부팅 후에는 유지하지 않음).</summary>
    private static unsafe void SetDiskOffline(SafeFileHandle handle)
    {
        var request = new SET_DISK_ATTRIBUTES
        {
            Version = (uint)Unsafe.SizeOf<SET_DISK_ATTRIBUTES>(),
            Persist = 0,
            Attributes = NativeMethods.DISK_ATTRIBUTE_OFFLINE,
            AttributesMask = NativeMethods.DISK_ATTRIBUTE_OFFLINE,
        };

        int size = Unsafe.SizeOf<SET_DISK_ATTRIBUTES>();

        if (!NativeMethods.DeviceIoControl(
                handle, NativeMethods.IOCTL_DISK_SET_DISK_ATTRIBUTES,
                (nint)(&request), (uint)size, 0, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "디스크 오프라인 전환(IOCTL_DISK_SET_DISK_ATTRIBUTES)에 실패했습니다.");
        }
    }
}
