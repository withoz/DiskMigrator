using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>마지막 파티션 확장 결과.</summary>
/// <param name="Attempted">확장을 시도했는지.</param>
/// <param name="Success">파티션이 실제로 커졌는지(재열거로 검증).</param>
/// <param name="Message">사람이 읽을 요약.</param>
public sealed record PartitionExpandResult(bool Attempted, bool Success, string Message);

/// <summary>
/// 작은 디스크를 큰 디스크로 클론한 뒤, 남는 미할당 공간을 <b>마지막 파티션에 합칩니다</b>.
/// </summary>
/// <remarks>
/// GPT 보정(<c>GptRepair</c>)이 백업 헤더를 디스크 끝으로 옮겨 남는 공간을 미할당으로 만든 뒤,
/// 여기서 <c>diskpart extend</c>로 그 공간까지 파티션과 NTFS를 함께 늘립니다. diskpart는
/// 파티션 테이블과 파일시스템을 한 번에 정합적으로 확장하므로(중간에 깨진 상태가 없음),
/// 하이브를 직접 편집하는 것보다 안전합니다. 대상이 반드시 온라인이어야 하며, 실패해도
/// 미할당 공간은 그대로 남아 사용자가 디스크 관리에서 수동 확장할 수 있습니다(항상 복구 가능).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PartitionExtender(IDiskService diskService, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>대상 디스크의 마지막 파티션을 남는 공간까지 확장합니다.</summary>
    public async Task<PartitionExpandResult> TryExpandLastAsync(int diskNumber, CancellationToken ct = default)
    {
        var disks = await diskService.EnumerateDisksAsync(ct);
        var disk = disks.FirstOrDefault(d => d.DeviceNumber == diskNumber);
        if (disk is null)
            return new(true, false, L.T(
                "대상 디스크를 다시 찾지 못해 파티션을 확장하지 못했습니다.",
                "Could not find the target disk again, so the partition was not extended."));

        var last = disk.Partitions.OrderByDescending(p => p.StartingOffset).FirstOrDefault();
        if (last is null)
            return new(true, false, L.T("확장할 파티션이 없습니다.", "There is no partition to extend."));

        return await TryExpandPartitionAsync(diskNumber, last.Number, ct);
    }

    /// <summary>
    /// 대상 디스크의 <b>지정한 파티션</b>을 바로 뒤의 연속된 미할당 공간까지 확장합니다.
    /// </summary>
    /// <remarks>
    /// 리사이즈 클론에서 뒤 파티션을 오른쪽으로 밀어 확보한 공간이 확대할 파티션 바로 뒤에
    /// 놓이므로, 크기 없이 <c>extend</c>하면 정확히 그 공간만큼 파티션과 NTFS가 함께 커집니다.
    /// </remarks>
    public async Task<PartitionExpandResult> TryExpandPartitionAsync(
        int diskNumber, int partitionNumber, CancellationToken ct = default)
    {
        var disks = await diskService.EnumerateDisksAsync(ct);
        var disk = disks.FirstOrDefault(d => d.DeviceNumber == diskNumber);
        if (disk is null)
            return new(true, false, L.T(
                "대상 디스크를 다시 찾지 못해 파티션을 확장하지 못했습니다.",
                "Could not find the target disk again, so the partition was not extended."));

        var part = disk.Partitions.FirstOrDefault(p => p.Number == partitionNumber);
        if (part is null)
            return new(true, false, L.T(
                $"파티션 {partitionNumber}을(를) 찾지 못했습니다.",
                $"Partition {partitionNumber} was not found."));

        long beforeLen = part.LengthBytes;

        // diskpart는 선택한 파티션을 바로 뒤의 연속된 미할당 공간까지 확장하고, NTFS면
        // 파일시스템도 함께 늘립니다. 볼륨이 마운트 안 돼 있어도 파티션 번호로 선택합니다.
        RunDiskpart(
            $"select disk {diskNumber}\r\n" +
            $"select partition {partitionNumber}\r\n" +
            "extend\r\n");

        // 언어에 의존하지 않도록, 재열거해서 파티션이 실제로 커졌는지로 성공을 판정합니다.
        var after = (await diskService.EnumerateDisksAsync(ct)).FirstOrDefault(d => d.DeviceNumber == diskNumber);
        var partAfter = after?.Partitions.FirstOrDefault(p => p.Number == partitionNumber);
        long afterLen = partAfter?.LengthBytes ?? beforeLen;

        if (afterLen > beforeLen + 1_000_000) // 1MB 넘게 커졌으면 성공
        {
            _logger.LogInformation(
                "파티션 {Num} 확장: {Before} → {After}.",
                partitionNumber, SizeFormatter.Format(beforeLen), SizeFormatter.Format(afterLen));
            return new(true, true, L.T(
                $"파티션 {partitionNumber}을(를) {SizeFormatter.Format(beforeLen)} → {SizeFormatter.Format(afterLen)}로 확장했습니다.",
                $"Extended partition {partitionNumber} from {SizeFormatter.Format(beforeLen)} to {SizeFormatter.Format(afterLen)}."));
        }

        _logger.LogWarning("파티션 {Num}이(가) 확장되지 않았습니다({Before}).",
            partitionNumber, SizeFormatter.Format(beforeLen));
        return new(true, false, L.T(
            "파티션을 자동 확장하지 못했습니다(대상이 접근 가능한 상태여야 합니다). " +
            "남는 미할당 공간은 그대로 있으니, 대상을 단독 연결한 뒤 디스크 관리의 '볼륨 확장'으로 마무리하세요.",
            "Automatic partition extension failed (the target must be accessible). " +
            "The unallocated space is still there — connect the target on its own and finish with 'Extend Volume' in Disk Management."));
    }

    private void RunDiskpart(string script)
    {
        string tmp = Path.GetTempFileName();
        try
        {
            // diskpart는 ANSI 스크립트 파일을 기대합니다.
            File.WriteAllText(tmp, script, Encoding.ASCII);

            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{tmp}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException(L.T(
                    "diskpart를 시작하지 못했습니다.", "Failed to start diskpart."));
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            _logger.LogInformation("diskpart extend (종료코드 {Code}):\n{Output}", p.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "diskpart 실행 중 오류.");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 정리 실패 무시 */ }
        }
    }
}
