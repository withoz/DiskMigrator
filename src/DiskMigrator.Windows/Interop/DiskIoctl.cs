using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Interop;

/// <summary>
/// DeviceIoControl 호출을 타입 안전하게 감싸는 헬퍼.
/// </summary>
/// <remarks>
/// 구조체 크기는 항상 <see cref="Unsafe.SizeOf{T}"/>로 구합니다. Marshal.SizeOf는
/// 마샬링된 네이티브 레이아웃을 계산하는데, 우리는 원시 바이트를 MemoryMarshal로
/// 직접 해석하므로 관리 레이아웃 기준 크기가 맞습니다. 두 값이 다른 구조체를
/// 섞어 쓰면 필드가 어긋나 읽힙니다.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DiskIoctl
{
    /// <summary>출력 구조체 하나를 받는 IOCTL을 호출합니다.</summary>
    internal static unsafe T Query<T>(SafeFileHandle handle, uint controlCode) where T : unmanaged
    {
        T result = default;
        int size = Unsafe.SizeOf<T>();

        if (!NativeMethods.DeviceIoControl(
                handle, controlCode, 0, 0, (nint)(&result), (uint)size, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"DeviceIoControl(0x{controlCode:X8}) 호출이 실패했습니다.");
        }

        return result;
    }

    /// <summary>출력 구조체 하나를 받되, 실패해도 예외 대신 false를 반환합니다.</summary>
    internal static bool TryQuery<T>(SafeFileHandle handle, uint controlCode, out T result) where T : unmanaged
    {
        try
        {
            result = Query<T>(handle, controlCode);
            return true;
        }
        catch (Win32Exception)
        {
            result = default;
            return false;
        }
    }

    /// <summary>입출력 버퍼가 없는 제어 코드(FSCTL_LOCK_VOLUME 등)를 호출합니다.</summary>
    internal static bool TryControl(SafeFileHandle handle, uint controlCode)
    {
        return NativeMethods.DeviceIoControl(handle, controlCode, 0, 0, 0, 0, out _, 0);
    }

    internal static void Control(SafeFileHandle handle, uint controlCode, string operationName)
    {
        if (!TryControl(handle, controlCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operationName} 이(가) 실패했습니다.");
        }
    }

    /// <summary>
    /// 가변 길이 출력을 받는 IOCTL을 호출합니다. 버퍼가 모자라면 크기를 두 배로 늘려 재시도합니다.
    /// (파티션 개수나 문자열 풀 길이를 미리 알 수 없는 IOCTL에 필요합니다.)
    /// </summary>
    internal static byte[] QueryVariable(
        SafeFileHandle handle,
        uint controlCode,
        nint inBuffer = 0,
        uint inBufferSize = 0,
        int initialSize = 1024,
        int maxSize = 1024 * 1024)
    {
        int size = initialSize;

        while (true)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.DeviceIoControl(
                        handle, controlCode, inBuffer, inBufferSize, buffer, (uint)size, out uint returned, 0))
                {
                    var result = new byte[returned];
                    Marshal.Copy(buffer, result, 0, (int)returned);
                    return result;
                }

                int error = Marshal.GetLastWin32Error();
                if (error is not (NativeMethods.ERROR_INSUFFICIENT_BUFFER or NativeMethods.ERROR_MORE_DATA))
                {
                    throw new Win32Exception(error,
                        $"DeviceIoControl(0x{controlCode:X8}) 호출이 실패했습니다.");
                }

                size *= 2;
                if (size > maxSize)
                {
                    throw new InvalidOperationException(
                        $"DeviceIoControl(0x{controlCode:X8}) 출력 버퍼가 {maxSize}바이트를 넘었습니다.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
