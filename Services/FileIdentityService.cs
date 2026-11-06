using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace StorageLens.Services;

public static partial class FileIdentityService
{
    private const uint ReadAttributes = 0x80;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint ShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;

    public static FileIdentity? TryGetIdentity(string path)
    {
        using var handle = CreateFile(
            ToExtendedPath(path),
            ReadAttributes,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var fileIdInfoBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<FileIdInfo>());
        try
        {
            if (GetFileInformationByHandleEx(
                    handle,
                    FileIdInfoClass,
                    fileIdInfoBuffer,
                    Marshal.SizeOf<FileIdInfo>()))
            {
                var fileIdInfo = Marshal.PtrToStructure<FileIdInfo>(fileIdInfoBuffer);
                return new FileIdentity(
                    unchecked((long)fileIdInfo.VolumeSerialNumber),
                    fileIdInfo.FileId0,
                    fileIdInfo.FileId1);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileIdInfoBuffer);
        }

        return GetFileInformationByHandle(handle, out var information)
            ? new FileIdentity(
                unchecked((long)information.VolumeSerialNumber),
                information.FileIndexHigh,
                information.FileIndexLow)
            : null;
    }

    public static bool Matches(string path, StorageFileIdentity expected)
    {
        var current = TryGetIdentity(path);
        return current is not null &&
               current.VolumeSerialNumber == expected.VolumeSerialNumber &&
               string.Equals(
                   current.FileId128,
                   expected.FileId128,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(
        string path,
        long volumeSerialNumber,
        string fileId128,
        long legacyFileIndexHigh,
        long legacyFileIndexLow)
    {
        var current = TryGetIdentity(path);
        if (current is null || current.VolumeSerialNumber != volumeSerialNumber)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(fileId128))
        {
            return string.Equals(
                current.FileId128,
                fileId128,
                StringComparison.OrdinalIgnoreCase);
        }

        return current.FileIndexHigh == legacyFileIndexHigh &&
               current.FileIndexLow == legacyFileIndexLow;
    }

    public static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        return @"\\?\" + fullPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileId0;
        public ulong FileId1;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out FileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);
}

public sealed record FileIdentity(
    long VolumeSerialNumber,
    ulong FileIdPart0,
    ulong FileIdPart1)
{
    public FileIdentity(
        long volumeSerialNumber,
        long fileIndexHigh,
        long fileIndexLow)
        : this(volumeSerialNumber, unchecked((ulong)fileIndexHigh), unchecked((ulong)fileIndexLow))
    {
        FileIndexHigh = fileIndexHigh;
        FileIndexLow = fileIndexLow;
        FileId128 = string.Empty;
    }

    public long FileIndexHigh { get; init; }

    public long FileIndexLow { get; init; }

    public string FileId128 { get; init; } =
        FileIdPart0.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) +
        FileIdPart1.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct StorageFileIdentity(
    long VolumeSerialNumber,
    string FileId128);
