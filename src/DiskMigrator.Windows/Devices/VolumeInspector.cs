using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMigrator.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Devices;

/// <summary>볼륨이 어느 물리 디스크의 어느 위치를 차지하는지.</summary>
internal sealed record VolumeExtent(int DiskNumber, long StartingOffset, long Length);

/// <summary>볼륨 하나에 대해 알아낸 정보.</summary>
internal sealed record VolumeDetails
{
    /// <summary>\\?\Volume{GUID}\ 형식 (후행 백슬래시 포함).</summary>
    public required string VolumeGuidPath { get; init; }

    /// <summary>드라이브 문자 (예: "C"). 마운트되지 않았으면 null.</summary>
    public string? DriveLetter { get; init; }

    public string? FileSystem { get; init; }
    public string? Label { get; init; }
    public long? FreeSpaceBytes { get; init; }

    /// <summary>이 볼륨이 걸쳐 있는 물리 디스크 구간들. 보통 1개입니다.</summary>
    public required IReadOnlyList<VolumeExtent> Extents { get; init; }
}

/// <summary>
/// 시스템의 모든 볼륨을 열거하고, 각 볼륨이 어느 물리 디스크에 있는지 알아냅니다.
/// </summary>
/// <remarks>
/// 시스템 디스크 판별의 근거가 되는 클래스입니다. WMI의 파티션 매핑에 의존하지 않고
/// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS로 커널에 직접 묻습니다 — 여기서 틀리면
/// 사용자의 부팅 디스크를 지우게 되므로 가장 신뢰할 수 있는 출처를 씁니다.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class VolumeInspector(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>시스템의 모든 볼륨을 조사합니다. 개별 볼륨 조회 실패는 건너뜁니다.</summary>
    public IReadOnlyList<VolumeDetails> EnumerateVolumes()
    {
        var results = new List<VolumeDetails>();
        var buffer = new char[VolumeApi.MAX_PATH];

        using var find = VolumeApi.FindFirstVolume(buffer, (uint)buffer.Length);
        if (find.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "볼륨 열거를 시작하지 못했습니다.");
        }

        do
        {
            string volumeName = new string(buffer).TrimEnd('\0');
            if (string.IsNullOrEmpty(volumeName)) continue;

            try
            {
                var details = Inspect(volumeName);
                if (details is not null) results.Add(details);
            }
            catch (Exception ex)
            {
                // 광학 드라이브, 비어 있는 카드 리더 등은 조회가 실패합니다. 치명적이지 않습니다.
                _logger.LogDebug(ex, "볼륨 {Volume} 조회를 건너뜁니다.", volumeName);
            }

            Array.Clear(buffer);
        }
        while (VolumeApi.FindNextVolume(find, buffer, (uint)buffer.Length));

        return results;
    }

    private VolumeDetails? Inspect(string volumeGuidPath)
    {
        var extents = GetExtents(volumeGuidPath);
        if (extents.Count == 0)
        {
            // 물리 디스크에 대응되지 않는 볼륨(네트워크, 가상 등)은 관심 대상이 아닙니다.
            return null;
        }

        string? driveLetter = GetDriveLetter(volumeGuidPath);
        var (fileSystem, label) = GetVolumeInformation(volumeGuidPath);
        long? freeSpace = GetFreeSpace(volumeGuidPath);

        return new VolumeDetails
        {
            VolumeGuidPath = volumeGuidPath,
            DriveLetter = driveLetter,
            FileSystem = fileSystem,
            Label = label,
            FreeSpaceBytes = freeSpace,
            Extents = extents,
        };
    }

    /// <summary>볼륨이 차지하는 물리 디스크 구간을 커널에 묻습니다.</summary>
    public IReadOnlyList<VolumeExtent> GetExtents(string volumeGuidPath)
    {
        // CreateFile은 후행 백슬래시가 없는 볼륨 경로만 받습니다.
        string path = volumeGuidPath.TrimEnd('\\');

        using var handle = NativeMethods.CreateFile(
            path,
            0, // 쿼리에는 접근 권한이 필요 없습니다 — 관리자가 아니어도 됩니다.
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            0,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            0);

        if (handle.IsInvalid)
        {
            return [];
        }

        return GetExtents(handle);
    }

    private IReadOnlyList<VolumeExtent> GetExtents(SafeFileHandle handle)
    {
        // 볼륨이 여러 디스크에 걸쳐 있을 수 있어(스팬/스트라이프) 가변 길이입니다.
        byte[] raw;
        try
        {
            raw = DiskIoctl.QueryVariable(
                handle,
                NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                initialSize: 8 + (Unsafe.SizeOf<DISK_EXTENT>() * 4));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "볼륨 구간(extent) 조회 실패.");
            return [];
        }

        if (raw.Length < sizeof(uint)) return [];

        uint count = BitConverter.ToUInt32(raw, 0);
        if (count == 0) return [];

        var extents = new List<VolumeExtent>((int)count);

        // VOLUME_DISK_EXTENTS는 { ULONG NumberOfDiskExtents; DISK_EXTENT Extents[1]; } 이고
        // DISK_EXTENT가 8바이트 정렬을 요구하므로 첫 항목은 오프셋 8에서 시작합니다.
        int extentSize = Unsafe.SizeOf<DISK_EXTENT>();
        int offset = 8;

        for (int i = 0; i < count; i++)
        {
            if (offset + extentSize > raw.Length) break;

            var extent = MemoryMarshal.Read<DISK_EXTENT>(raw.AsSpan(offset, extentSize));
            extents.Add(new VolumeExtent((int)extent.DiskNumber, extent.StartingOffset, extent.ExtentLength));
            offset += extentSize;
        }

        return extents;
    }

    private string? GetDriveLetter(string volumeGuidPath)
    {
        var buffer = new char[VolumeApi.MAX_PATH * 4];

        if (!VolumeApi.GetVolumePathNamesForVolumeName(
                volumeGuidPath, buffer, (uint)buffer.Length, out uint returned) || returned == 0)
        {
            return null;
        }

        // REG_MULTI_SZ 형태: "C:\\\0D:\\\0\0" — 첫 번째 항목만 씁니다.
        string first = new string(buffer, 0, (int)returned).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";

        // "C:\" → "C". 마운트 지점 폴더 경로("D:\mount\")는 드라이브 문자가 아니므로 제외합니다.
        return first.Length >= 2 && first[1] == ':' && char.IsLetter(first[0])
            ? first[0].ToString().ToUpperInvariant()
            : null;
    }

    private (string? FileSystem, string? Label) GetVolumeInformation(string volumeGuidPath)
    {
        var labelBuffer = new char[VolumeApi.MAX_PATH + 1];
        var fsBuffer = new char[VolumeApi.MAX_PATH + 1];

        if (!VolumeApi.GetVolumeInformation(
                volumeGuidPath, labelBuffer, (uint)labelBuffer.Length,
                out _, out _, out _, fsBuffer, (uint)fsBuffer.Length))
        {
            // 포맷되지 않은 파티션(예: MSR)이나 인식 못 하는 파일 시스템입니다.
            return (null, null);
        }

        string fs = new string(fsBuffer).TrimEnd('\0');
        string label = new string(labelBuffer).TrimEnd('\0');

        return (
            string.IsNullOrWhiteSpace(fs) ? null : fs,
            string.IsNullOrWhiteSpace(label) ? null : label);
    }

    private long? GetFreeSpace(string volumeGuidPath)
    {
        return VolumeApi.GetDiskFreeSpaceEx(volumeGuidPath, out _, out _, out ulong totalFree)
            ? (long)totalFree
            : null;
    }

    /// <summary>
    /// 현재 실행 중인 Windows가 설치된 물리 디스크 번호.
    /// </summary>
    public int? GetSystemDiskNumber()
    {
        try
        {
            // Environment.SystemDirectory 는 "C:\Windows\system32" — 여기서 볼륨 루트를 얻습니다.
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return null;

            string devicePath = $@"\\.\{root.TrimEnd('\\')}";

            using var handle = NativeMethods.CreateFile(
                devicePath, 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

            if (handle.IsInvalid)
            {
                _logger.LogWarning(
                    "시스템 볼륨 {Path} 을(를) 열지 못해 시스템 디스크를 판별할 수 없습니다. (Win32 오류 {Error})",
                    devicePath, Marshal.GetLastWin32Error());
                return null;
            }

            var extents = GetExtents(handle);
            return extents.Count > 0 ? extents[0].DiskNumber : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "시스템 디스크 번호를 알아내지 못했습니다.");
            return null;
        }
    }

    /// <summary>
    /// 이 PC가 실제로 부팅에 사용하는 시스템(EFI/활성) 파티션이 있는 디스크 번호.
    /// </summary>
    /// <remarks>
    /// "EFI 파티션이 있는 디스크"로 판별하면 안 됩니다. 예전에 Windows가 깔려 있던 디스크를
    /// 클론 대상으로 재활용하는 건 아주 흔한 일이고, 그 디스크에도 EFI 파티션이 남아 있어
    /// 멀쩡한 대상이 차단되어 버립니다.
    ///
    /// 대신 Windows 설치 시 기록되는 HKLM\SYSTEM\Setup\SystemPartition 값을 씁니다.
    /// 이 값은 "이 설치가 부팅에 쓰는 파티션"을 \Device\HarddiskVolumeN 형태로 정확히 가리킵니다.
    /// </remarks>
    public int? GetBootDiskNumber()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
            if (key?.GetValue("SystemPartition") is not string systemPartition ||
                string.IsNullOrWhiteSpace(systemPartition))
            {
                _logger.LogDebug("HKLM\\SYSTEM\\Setup\\SystemPartition 값이 없습니다.");
                return null;
            }

            // "\Device\HarddiskVolume1" → "\\?\GLOBALROOT\Device\HarddiskVolume1"
            string devicePath = $@"\\?\GLOBALROOT{systemPartition}";

            using var handle = NativeMethods.CreateFile(
                devicePath, 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                0, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, 0);

            if (handle.IsInvalid)
            {
                _logger.LogWarning(
                    "부팅 파티션 {Path} 을(를) 열지 못했습니다. (Win32 오류 {Error})",
                    devicePath, Marshal.GetLastWin32Error());
                return null;
            }

            var extents = GetExtents(handle);
            return extents.Count > 0 ? extents[0].DiskNumber : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "부팅 디스크 번호를 알아내지 못했습니다.");
            return null;
        }
    }
}
