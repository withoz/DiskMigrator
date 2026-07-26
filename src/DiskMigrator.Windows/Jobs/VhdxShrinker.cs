using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DiskMigrator.Core.Abstractions;
using DiskMigrator.Core.Localization;
using DiskMigrator.Core.Models;
using DiskMigrator.Core.Util;
using DiskMigrator.Windows.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskMigrator.Windows.Jobs;

/// <summary>이미지 파티션 축소 결과.</summary>
/// <param name="Success">파티션이 실제로 줄었는지(재열거로 검증).</param>
/// <param name="ChildImagePath">축소를 적용한 차등 자식 이미지 경로(복원은 이걸 읽습니다).</param>
/// <param name="RequestedBytes">요청한 새 파티션 크기.</param>
/// <param name="AchievedPartitionBytes">실제로 줄어든 파티션 크기(복원 배치 계산은 이 값을 씁니다).</param>
/// <param name="Message">사람이 읽을 요약.</param>
public sealed record VhdxShrinkResult(
    bool Success, string ChildImagePath, long RequestedBytes, long AchievedPartitionBytes, string Message);

/// <summary>
/// 백업 이미지(VHDX) 안의 NTFS 파티션을 <b>파일을 안 옮겨도 되는 안전 크기</b>로 축소합니다.
/// </summary>
/// <remarks>
/// 축소 리사이즈의 핵심 실행부입니다. 확정된 설계대로:
/// <list type="number">
/// <item><b>원본 이미지는 절대 건드리지 않습니다.</b> 부모(백업 이미지)에 얇은 <see
///   cref="VirtualDisk.CreateDifferencingAndAttach">차등 자식</see>을 만들어, 축소 쓰기는 모두
///   자식으로만 갑니다. 축소가 잘못돼도 부모는 그대로라 재백업이 필요 없습니다.</item>
/// <item><b>NTFS 축소기를 직접 만들지 않습니다.</b> 부착한 자식 볼륨에 Windows의 검증된 축소
///   (<c>diskpart shrink</c>)를 겁니다 — $MFT·이동불가 파일·부트섹터를 Microsoft가 정합적으로
///   처리합니다(<see cref="PartitionExtender"/>의 <c>extend</c>와 대칭).</item>
/// </list>
///
/// <para>이 단계는 파일시스템만 줄입니다. 줄인 파티션 <b>뒤</b> 파티션을 왼쪽으로 당기는
/// 재배치(compaction)와 GPT 재작성은 복원 단계가 <see cref="Core.Partitioning.ResizePlanner.PlanShrink"/>
/// 배치로 수행합니다 — 여기서는 실제 줄어든 크기(<see cref="VhdxShrinkResult.AchievedPartitionBytes"/>)를
/// 돌려줘 그 배치가 현실과 맞도록 합니다.</para>
///
/// <para>목표 크기는 <see cref="NtfsUsageProbe"/>가 준 값(마지막 사용 클러스터 + 여유)을 쓰면,
/// diskpart의 축소 한계보다 항상 위라 축소가 안전하게 성공합니다.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VhdxShrinker(IDiskService diskService, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// <paramref name="parentImagePath"/>의 차등 자식을 만들어, 그 안의 파티션
    /// <paramref name="partitionNumber"/>를 약 <paramref name="newPartitionBytes"/> 크기로 줄입니다.
    /// </summary>
    /// <param name="parentImagePath">부모 백업 이미지(그대로 유지됨).</param>
    /// <param name="childImagePath">만들 차등 자식 경로(이미 있으면 실패).</param>
    /// <param name="partitionNumber">줄일 파티션 번호(NTFS여야 함 — 호출자가 보장).</param>
    /// <param name="newPartitionBytes">목표 새 크기. diskpart 한계 위여야 안전(NtfsUsageProbe 제안값 권장).</param>
    public async Task<VhdxShrinkResult> ShrinkInDifferencingChildAsync(
        string parentImagePath, string childImagePath, int partitionNumber,
        long newPartitionBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(childImagePath);

        _logger.LogInformation(
            "차등 자식 생성: {Child} (부모 {Parent}) — 파티션 {Part}을(를) 약 {New:N0} 바이트로 축소.",
            childImagePath, parentImagePath, partitionNumber, newPartitionBytes);

        using var vhd = VirtualDisk.CreateDifferencingAndAttach(childImagePath, parentImagePath);
        return await ShrinkAttachedAsync(vhd.DiskNumber, childImagePath, partitionNumber, newPartitionBytes, ct);
    }

    private async Task<VhdxShrinkResult> ShrinkAttachedAsync(
        int diskNumber, string childImagePath, int partitionNumber, long newPartitionBytes, CancellationToken ct)
    {
        var before = await FindPartitionAsync(diskNumber, partitionNumber, ct);
        if (before is null)
        {
            return new VhdxShrinkResult(false, childImagePath, newPartitionBytes, 0, L.T(
                $"부착된 이미지(디스크 {diskNumber})에서 파티션 {partitionNumber}을(를) 찾지 못했습니다.",
                $"Partition {partitionNumber} was not found on the attached image (disk {diskNumber})."));
        }

        long currentBytes = before.LengthBytes;
        if (newPartitionBytes >= currentBytes)
        {
            return new VhdxShrinkResult(false, childImagePath, newPartitionBytes, currentBytes, L.T(
                $"새 크기({SizeFormatter.Format(newPartitionBytes)})가 현재 크기" +
                $"({SizeFormatter.Format(currentBytes)}) 이상이라 축소할 것이 없습니다.",
                $"The new size ({SizeFormatter.Format(newPartitionBytes)}) is not smaller than the current size " +
                $"({SizeFormatter.Format(currentBytes)}) — nothing to shrink."));
        }

        // diskpart shrink는 '줄일 양'(MB)을 받습니다. 목표 크기까지 줄이도록 감소량을 계산하고,
        // minimum을 살짝 낮춰(정렬·오버헤드 여유) 근소한 차이로 실패하지 않게 합니다.
        long reduceMb = (currentBytes - newPartitionBytes) / (1024 * 1024);
        if (reduceMb <= 0)
        {
            return new VhdxShrinkResult(false, childImagePath, newPartitionBytes, currentBytes, L.T(
                "축소량이 1MB 미만입니다. 더 작은 목표 크기를 지정하십시오.",
                "The shrink amount is under 1 MB. Specify a smaller target size."));
        }
        long minimumMb = Math.Max(1, reduceMb - 64);

        _logger.LogInformation(
            "이미지 파티션 축소: 디스크 {Disk} 파티션 {Part} {Cur} → 목표 {New} (감소 {Mb} MB, 최소 {Min} MB).",
            diskNumber, partitionNumber, SizeFormatter.Format(currentBytes),
            SizeFormatter.Format(newPartitionBytes), reduceMb, minimumMb);

        RunDiskpart(
            $"select disk {diskNumber}\r\n" +
            $"select partition {partitionNumber}\r\n" +
            $"shrink desired={reduceMb} minimum={minimumMb}\r\n");

        // 언어에 의존하지 않도록 재열거해서 실제로 줄었는지로 성공을 판정합니다.
        var after = await FindPartitionAsync(diskNumber, partitionNumber, ct);
        long achieved = after?.LengthBytes ?? currentBytes;

        if (achieved < currentBytes - 1_000_000) // 1MB 넘게 줄었으면 성공
        {
            _logger.LogInformation("파티션 축소 완료: {Cur} → {Ach}.",
                SizeFormatter.Format(currentBytes), SizeFormatter.Format(achieved));
            return new VhdxShrinkResult(true, childImagePath, newPartitionBytes, achieved, L.T(
                $"파티션 {partitionNumber}을(를) {SizeFormatter.Format(currentBytes)} → " +
                $"{SizeFormatter.Format(achieved)}로 축소했습니다.",
                $"Shrunk partition {partitionNumber} from {SizeFormatter.Format(currentBytes)} to " +
                $"{SizeFormatter.Format(achieved)}."));
        }

        _logger.LogWarning("파티션 {Part}이(가) 축소되지 않았습니다({Cur}).",
            partitionNumber, SizeFormatter.Format(currentBytes));
        return new VhdxShrinkResult(false, childImagePath, newPartitionBytes, achieved, L.T(
            "파티션이 축소되지 않았습니다(축소 한계·볼륨 잠금·이동불가 파일 때문일 수 있습니다). " +
            "더 큰 목표 크기로 다시 시도하십시오.",
            "The partition was not shrunk (possibly due to the shrink limit, a volume lock, or unmovable files). " +
            "Try again with a larger target size."));
    }

    private async Task<PartitionInfo?> FindPartitionAsync(int diskNumber, int partitionNumber, CancellationToken ct)
    {
        var disks = await diskService.EnumerateDisksAsync(ct);
        return disks.FirstOrDefault(d => d.DeviceNumber == diskNumber)?
            .Partitions.FirstOrDefault(p => p.Number == partitionNumber);
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
            p.WaitForExit(180_000);

            _logger.LogInformation("diskpart shrink (종료코드 {Code}):\n{Output}", p.ExitCode, output.Trim());
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
