using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskMigrator.Windows.Interop;

/// <summary>볼륨 열거와 볼륨 정보 조회용 Win32 API.</summary>
[SupportedOSPlatform("windows")]
internal static partial class VolumeApi
{
    private const string Kernel32 = "kernel32.dll";

    internal const int MAX_PATH = 260;

    [LibraryImport(Kernel32, EntryPoint = "FindFirstVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFindVolumeHandle FindFirstVolume(
        [Out] char[] lpszVolumeName, uint cchBufferLength);

    [LibraryImport(Kernel32, EntryPoint = "FindNextVolumeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextVolume(
        SafeFindVolumeHandle hFindVolume, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [LibraryImport(Kernel32, EntryPoint = "FindVolumeClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(nint hFindVolume);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumePathNamesForVolumeNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        [Out] char[] lpszVolumePathNames,
        uint cchBufferLength,
        out uint lpcchReturnLength);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(
        string lpRootPathName,
        [Out] char[] lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [LibraryImport(Kernel32, EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}

/// <summary>FindFirstVolume 핸들을 확실히 닫기 위한 SafeHandle.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeFindVolumeHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle() => VolumeApi.FindVolumeClose(handle);
}
