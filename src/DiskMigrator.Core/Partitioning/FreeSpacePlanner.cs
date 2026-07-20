using System.Globalization;
using DiskMigrator.Core.Engine;

namespace DiskMigrator.Core.Partitioning;

/// <summary>
/// 남는 공간 선택을 클론 엔진이 받는 형태로 옮긴 결과.
/// <see cref="Error"/>가 있으면 시작할 수 없는 상태이며, 그 문구를 그대로 사용자에게 보여 줍니다.
/// </summary>
public sealed record FreeSpacePlan(FreeSpaceMode Mode, PartitionGrowRequest? Grow, string? Error);

/// <summary>
/// 화면의 라디오·드롭다운·크기 입력을 <see cref="FreeSpaceMode"/>와
/// <see cref="PartitionGrowRequest"/>로 옮깁니다.
/// </summary>
/// <remarks>
/// "복제가 끝나면 이렇게 됩니다" 막대와 실제 실행이 <b>반드시 같은 판정</b>을 써야 합니다.
/// 예전에는 화면 코드 두 곳에서 따로 계산해서, 크기 입력이 잘못됐을 때 미리보기는 "남는 공간을
/// 전부 채운" 막대를 그려 놓고 시작을 누르면 거부했습니다 — 그림이 거짓말을 한 셈입니다.
/// 순수 함수로 떼어내 한 곳에서만 판정하고, 단위 테스트로 고정합니다.
/// </remarks>
public static class FreeSpacePlanner
{
    /// <summary>화면의 모든 크기가 1024 기준(<see cref="Util.SizeFormatter"/>)이므로 입력도 같은 기준입니다.</summary>
    /// <remarks>
    /// 1000 기준으로 읽으면 범례의 "930.49 GB"를 그대로 입력한 사용자가 7% 작은 파티션을
    /// 얻습니다. 화면이 보여 준 숫자와 입력이 뜻하는 값은 같아야 합니다.
    /// </remarks>
    public const long BytesPerGb = 1024L * 1024 * 1024;

    /// <param name="source">
    /// 원본 파티션 배치. 주면 <see cref="ResizePlanner"/>로 <b>실제로 그 배치가 되는지</b>까지
    /// 확인해, 엔진이 던질 거부 사유를 시작 전에 그대로 보여 줍니다.
    /// </param>
    /// <param name="targetSizeBytes">대상 디스크 크기. <paramref name="source"/>와 함께 줍니다.</param>
    public static FreeSpacePlan Resolve(
        bool hasFreeSpace,
        bool canResize,
        bool expandLast,
        bool growPartition,
        int? growPartitionNumber,
        bool fillRemaining,
        string? sizeText,
        IReadOnlyList<Models.PartitionInfo>? source = null,
        long targetSizeBytes = 0)
    {
        var mode = FreeSpaceMode.Leave;
        if (hasFreeSpace)
        {
            if (growPartition && canResize) mode = FreeSpaceMode.GrowPartition;
            else if (expandLast) mode = FreeSpaceMode.ExpandLast;
        }

        if (mode != FreeSpaceMode.GrowPartition) return new FreeSpacePlan(mode, null, null);

        // 넓히기를 골랐는데 파티션이 없는 상태로는 시작하지 않습니다. 그냥 두면 아무 일도
        // 안 하면서 사용자는 넓혀졌다고 믿게 됩니다.
        if (growPartitionNumber is not { } number)
        {
            return new FreeSpacePlan(mode, null,
                "'파티션 조정'을 선택했다면 어떤 파티션을 넓힐지도 골라야 합니다.");
        }

        if (fillRemaining) return Feasible(mode, new PartitionGrowRequest(number, null), source, targetSizeBytes);

        // '새 총 크기' 모드인데 입력이 숫자가 아니면 조용히 null(= 남는 공간 전부)로 넘어가면
        // 안 됩니다. 사용자가 지정한 것과 전혀 다른 크기로 파티션이 커져 버립니다.
        if (!TryParseSizeGb(sizeText, out double gb) || gb <= 0)
        {
            return new FreeSpacePlan(mode, null,
                $"'새 총 크기(GB)'에 0보다 큰 숫자를 입력하십시오(입력값: \"{sizeText}\"). " +
                "남는 공간을 모두 쓰려면 '남는 공간 전부'를 고르십시오.");
        }

        return Feasible(mode, new PartitionGrowRequest(number, (long)(gb * BytesPerGb)), source, targetSizeBytes);
    }

    /// <summary>
    /// 이 요청이 실제로 배치가 되는지 <see cref="ResizePlanner"/>에게 물어보고, 안 되면 그
    /// 이유를 그대로 사용자에게 돌려줍니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 여기서 걸리는 요청(예: 930 GB 파티션에 "600" 입력)이 숫자로는 읽히므로 통과했고,
    /// 미리보기만 소리 없이 사라진 뒤 '클론 시작'을 눌러야 이유를 볼 수 있었습니다. 화면에서
    /// 그림이 사라지는 것은 사용자에게 아무 말도 해 주지 않습니다.
    /// </remarks>
    private static FreeSpacePlan Feasible(
        FreeSpaceMode mode, PartitionGrowRequest grow,
        IReadOnlyList<Models.PartitionInfo>? source, long targetSizeBytes)
    {
        if (source is null || source.Count == 0 || targetSizeBytes <= 0)
            return new FreeSpacePlan(mode, grow, null);

        try
        {
            ResizePlanner.Plan(source, targetSizeBytes, grow);
            return new FreeSpacePlan(mode, grow, null);
        }
        catch (Exception ex)
        {
            return new FreeSpacePlan(mode, null, ex.Message);
        }
    }

    /// <summary>
    /// 소수점 기호는 지역 설정마다 다릅니다. 한국어 윈도우에서 "1.5"를, 유럽 설정에서 "1,5"를
    /// 치는 사람이 모두 있으므로 현재 문화권과 불변 문화권을 모두 시도합니다.
    /// </summary>
    public static bool TryParseSizeGb(string? text, out double gb)
    {
        gb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out gb)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out gb);
    }
}
