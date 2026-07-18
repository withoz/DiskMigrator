using System.IO.Hashing;

namespace DiskMigrator.Core.Engine;

/// <summary>
/// 복제하면서 각 블록에 실제로 쓴 데이터의 해시를 기록합니다.
/// </summary>
/// <remarks>
/// 검증의 목적은 "대상 디스크가 우리가 보낸 바이트를 그대로 저장했는가"(쓰기 오류 탐지)입니다.
/// 그런데 복제 후 원본(스냅샷)을 다시 읽어 비교하면, 클라이언트 Windows에서 VSS 섀도 저장소가
/// 스냅샷 대상 볼륨 자신에 놓이고 그 영역이 스냅샷에서 제외되어, 시간이 지나면 스냅샷 값이
/// 바뀝니다(드리프트). 그 드리프트는 VSS 임시 저장소 스크래치라 무해하지만, 원본 재읽기
/// 방식 검증은 이걸 가짜 불일치로 잡습니다.
///
/// 그래서 복제 시점에 우리가 <b>쓴 데이터</b>의 해시를 기록해 두고, 검증 때는 대상만 다시
/// 읽어 그 해시와 비교합니다. 이러면 드리프트에 영향받지 않고 쓰기 오류만 정확히 잡습니다.
///
/// 해시는 XxHash128(128비트)입니다. 암호학적 강도는 필요 없고(악의적 위조가 아니라 매체
/// 오류 탐지가 목적), 속도가 중요하므로 GB/s급의 빠른 해시를 씁니다.
/// </remarks>
public sealed class CopyHashLog
{
    /// <summary>기록된 블록 하나. 대상 오프셋과 길이, 그리고 쓴 데이터의 해시.</summary>
    public readonly record struct Block(long TargetOffset, int Length, UInt128 Hash);

    private readonly List<Block> _blocks = [];

    public IReadOnlyList<Block> Blocks => _blocks;

    /// <summary>대상 <paramref name="targetOffset"/>에 쓴 데이터의 해시를 기록합니다.</summary>
    public void Record(long targetOffset, ReadOnlySpan<byte> written)
    {
        _blocks.Add(new Block(targetOffset, written.Length, XxHash128.HashToUInt128(written)));
    }

    /// <summary>주어진 데이터가 기록된 해시와 일치하는지.</summary>
    public static bool Matches(in Block block, ReadOnlySpan<byte> actual)
    {
        return actual.Length == block.Length && XxHash128.HashToUInt128(actual) == block.Hash;
    }
}
