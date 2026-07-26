using System.Runtime.InteropServices;

namespace DiskMigrator.Core.Util;

/// <summary>
/// 섹터 경계에 정렬된 네이티브 메모리 버퍼.
/// </summary>
/// <remarks>
/// Windows에서 원시 디스크를 FILE_FLAG_NO_BUFFERING으로 열면 읽기/쓰기 버퍼의
/// <b>주소</b>까지 섹터 크기의 배수여야 합니다. GC 힙의 byte[]는 이를 보장하지 않으므로
/// 정렬된 네이티브 메모리를 직접 할당합니다. 중간 복사(bounce buffer)를 없애
/// NVMe 같은 고속 장치에서 대역폭 손실을 피하는 목적도 있습니다.
/// </remarks>
public sealed unsafe class AlignedBuffer : IDisposable
{
    private void* _pointer;

    public int Length { get; }

    public AlignedBuffer(int length, int alignment = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        _pointer = NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        if (_pointer is null)
        {
            throw new OutOfMemoryException(DiskMigrator.Core.Localization.L.T($"{length}바이트 정렬 버퍼를 할당하지 못했습니다.", $"Failed to allocate a {length}-byte aligned buffer."));
        }

        Length = length;
        NativeMemory.Clear(_pointer, (nuint)length);
    }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pointer is null, this);
            return new Span<byte>(_pointer, Length);
        }
    }

    /// <summary>버퍼 앞쪽 <paramref name="count"/>바이트만 잘라낸 뷰.</summary>
    public Span<byte> SpanOf(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
        return Span[..count];
    }

    public void Clear() => Span.Clear();

    public void Dispose()
    {
        if (_pointer is not null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer() => Dispose();
}
