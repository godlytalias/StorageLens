using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageLens.Services;

internal static partial class NativeDirectoryReader
{
    private const int FileListDirectory = 0x0001;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileIdExtdDirectoryInfoClass = 0x13;
    private const int FileIdExtdDirectoryRestartInfoClass = 0x14;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreFiles = 18;
    private const int BufferSize = 1024 * 1024;

    public static IReadOnlyList<NativeDirectoryEntry> Enumerate(string directoryPath)
    {
        using var handle = CreateFileW(
            FileIdentityService.ToExtendedPath(directoryPath),
            FileListDirectory,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open directory '{directoryPath}'.");
        }

        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var entries = new List<NativeDirectoryEntry>();
            var informationClass = FileIdExtdDirectoryRestartInfoClass;
            while (true)
            {
                if (!GetFileInformationByHandleEx(
                        handle,
                        informationClass,
                        buffer,
                        BufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                    {
                        break;
                    }

                    if (error == ErrorMoreData)
                    {
                        informationClass = FileIdExtdDirectoryRestartInfoClass;
                        continue;
                    }

                    throw new Win32Exception(
                        error,
                        $"Could not enumerate directory '{directoryPath}'.");
                }

                ParseBuffer(buffer, entries);
                informationClass = FileIdExtdDirectoryInfoClass;
            }

            return entries;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe void ParseBuffer(
        IntPtr buffer,
        ICollection<NativeDirectoryEntry> entries)
    {
        var record = buffer;
        while (record != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<FileIdExtdDirectoryInfo>(record);
            var nameLength = checked((int)info.FileNameLength / sizeof(char));
            var namePointer = (char*)(record + Marshal.SizeOf<FileIdExtdDirectoryInfo>());
            var name = nameLength == 0
                ? string.Empty
                : new string(namePointer, 0, nameLength);
            if (name is not "." and not "..")
            {
                entries.Add(new NativeDirectoryEntry(
                    name,
                    info.EndOfFile,
                    info.AllocationSize,
                    info.FileAttributes,
                    info.ReparsePointTag,
                    info.FileId0.ToString(
                        "X16",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    info.FileId1.ToString(
                        "X16",
                        System.Globalization.CultureInfo.InvariantCulture),
                    FileTimeToUtc(info.CreationTime),
                    FileTimeToUtc(info.LastWriteTime),
                    FileTimeToUtc(info.LastAccessTime),
                    info.ChangeTime));
            }

            if (info.NextEntryOffset == 0)
            {
                break;
            }

            record = record + checked((int)info.NextEntryOffset);
        }
    }

    private static DateTimeOffset FileTimeToUtc(long fileTime) =>
        fileTime > 0
            ? DateTimeOffset.FromFileTime(fileTime)
            : DateTimeOffset.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdExtdDirectoryInfo
    {
        public uint NextEntryOffset;
        public uint FileIndex;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public long EndOfFile;
        public long AllocationSize;
        public uint FileAttributes;
        public uint FileNameLength;
        public uint EaSize;
        public uint ReparsePointTag;
        public ulong FileId0;
        public ulong FileId1;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        int desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);
}

internal sealed record NativeDirectoryEntry(
    string Name,
    long LogicalSize,
    long AllocatedSize,
    uint Attributes,
    uint ReparsePointTag,
    string FileId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset AccessedUtc,
    long ChangeTime);
