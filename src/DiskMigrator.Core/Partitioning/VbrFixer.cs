using System.Buffers.Binary;
using System.Text;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Core.Partitioning;

/// <summary>
/// 옮겨진 파티션의 볼륨 부트 레코드(VBR)에 적힌 <b>자기 시작 위치</b>를 새 위치로 고칩니다.
/// </summary>
/// <remarks>
/// <para>
/// NTFS·FAT32의 부트 섹터에는 "이 볼륨이 디스크의 몇 번째 섹터에서 시작하는가"가
/// <c>HiddenSectors</c>(오프셋 0x1C)로 적혀 있습니다. 리사이즈로 파티션을 뒤로 밀면 실제
/// 위치는 달라지는데 이 값은 복사된 그대로 남아 옛 위치를 가리킵니다.
/// </para>
/// <para>
/// Windows는 볼륨을 마운트할 때 파티션 테이블을 보므로 데이터 접근에는 문제가 없습니다.
/// 하지만 <b>VBR의 부트 코드는 이 값으로 자기 위치를 찾습니다</b> — 옮겨진 파티션에서
/// 부팅하려 하면 엉뚱한 곳을 읽습니다. 틀린 값을 남겨 둘 이유가 없습니다.
/// </para>
/// <para>
/// 제자리에서 커지기만 한 파티션(확대 대상)은 시작 위치가 그대로라 손댈 것이 없습니다.
/// </para>
/// </remarks>
public static class VbrFixer
{
    private const int HiddenSectorsOffset = 0x1C;

    /// <summary>고친 파티션 수. 알아볼 수 없는 파일시스템은 건드리지 않고 건너뜁니다.</summary>
    public static int FixMovedPartitions(
        IBlockDevice target, IReadOnlyList<PartitionRemap> remaps, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(remaps);

        var log = logger ?? NullLogger.Instance;
        int sectorSize = target.SectorSize;
        int fixedCount = 0;

        foreach (var remap in remaps)
        {
            if (remap.NewStartLba == remap.OldStartLba) continue;      // 제자리
            if (remap.NewStartLba > uint.MaxValue) continue;           // 32비트 필드에 안 들어감

            using var buffer = new AlignedBuffer(Math.Max(4096, sectorSize));
            var vbr = buffer.SpanOf(sectorSize);

            long offset = remap.NewStartLba * sectorSize;
            if (target.Read(offset, vbr) < sectorSize)
            {
                log.LogWarning("VBR을 읽지 못해 건너뜁니다 (LBA {Lba}).", remap.NewStartLba);
                continue;
            }

            if (!LooksLikeKnownVbr(vbr))
            {
                log.LogInformation(
                    "LBA {Lba}의 볼륨은 NTFS·FAT32가 아니라 시작 위치를 고치지 않습니다.", remap.NewStartLba);
                continue;
            }

            uint current = BinaryPrimitives.ReadUInt32LittleEndian(vbr.Slice(HiddenSectorsOffset, 4));
            if (current == remap.NewStartLba) continue;

            BinaryPrimitives.WriteUInt32LittleEndian(
                vbr.Slice(HiddenSectorsOffset, 4), (uint)remap.NewStartLba);
            target.Write(offset, vbr);
            fixedCount++;

            log.LogInformation(
                "VBR 시작 위치 갱신: LBA {Old} → {New}.", current, remap.NewStartLba);
        }

        if (fixedCount > 0) target.Flush();
        return fixedCount;
    }

    /// <summary>
    /// 0x1C를 고쳐도 되는 부트 섹터인지. NTFS와 FAT32만 봅니다.
    /// </summary>
    /// <remarks>
    /// 두 형식 모두 그 자리가 HiddenSectors입니다. 알아보지 못한 볼륨에 4바이트를 덮어쓰면
    /// 그 파일시스템을 망가뜨릴 수 있으므로, 확신할 때만 손댑니다.
    /// </remarks>
    private static bool LooksLikeKnownVbr(ReadOnlySpan<byte> vbr)
    {
        if (vbr.Length < 512 || vbr[510] != 0x55 || vbr[511] != 0xAA) return false;

        // NTFS는 OEM 이름이 "NTFS    ".
        if (Encoding.ASCII.GetString(vbr.Slice(3, 8)) == "NTFS    ") return true;

        // FAT32는 0x52에 파일시스템 종류 문자열이 있습니다.
        return Encoding.ASCII.GetString(vbr.Slice(0x52, 8)) == "FAT32   ";
    }
}
