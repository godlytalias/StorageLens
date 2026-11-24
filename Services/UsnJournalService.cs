using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StorageLens.Services;

public static partial class UsnJournalService
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000903AB;
    private const int BufferSize = 1024 * 1024;
    private const uint ReasonMask = 0xFFFFFFFF;
    private const int ErrorHandleEof = 38;
    internal const int MaximumChangedDirectories = 25_000;
    private const int MaximumReturnedChanges = 1_000_000;

    public static UsnJournalInfo? TryGetInfo(string volumeRoot)
    {
        try
        {
            using var handle = OpenVolume(volumeRoot);
            if (!DeviceIoControl(
                    handle,
                    FsctlQueryUsnJournal,
                    IntPtr.Zero,
                    0,
                    out var data,
                    (uint)Marshal.SizeOf<UsnJournalData>(),
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            return new UsnJournalInfo(
                data.UsnJournalId,
                data.FirstUsn,
                data.NextUsn,
                data.LowestValidUsn,
                data.MaximumSize);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public static UsnReadResult TryReadChanges(
        string volumeRoot,
        long journalId,
        long startUsn,
        CancellationToken cancellationToken = default,
        int maximumChangedDirectories = MaximumChangedDirectories)
    {
        var info = TryGetInfo(volumeRoot);
        if (info is null ||
            unchecked((long)info.JournalId) != journalId ||
            startUsn < info.FirstUsn)
        {
            return new UsnReadResult(
                false,
                info?.NextUsn ?? startUsn,
                info?.JournalId ?? 0,
                [],
                "The USN journal is unavailable or wrapped.");
        }

        try
        {
            using var handle = OpenVolume(volumeRoot);
            var request = new ReadUsnJournalData
            {
                StartUsn = startUsn,
                ReasonMask = ReasonMask,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 1,
                UsnJournalId = info.JournalId,
            };
            var input = Marshal.AllocHGlobal(Marshal.SizeOf<ReadUsnJournalData>());
            var output = Marshal.AllocHGlobal(BufferSize);
            try
            {
                Marshal.StructureToPtr(request, input, false);
                var changedDirectories = new HashSet<ulong>();
                var nextUsn = startUsn;
                var returnedChanges = 0;
                while (nextUsn < info.NextUsn)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!DeviceIoControl(
                            handle,
                            FsctlReadUsnJournal,
                            input,
                            (uint)Marshal.SizeOf<ReadUsnJournalData>(),
                            output,
                            BufferSize,
                            out var bytesReturned,
                            IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorHandleEof)
                        {
                            break;
                        }

                        throw new Win32Exception(error);
                    }

                    if (bytesReturned < 8)
                    {
                        break;
                    }

                    nextUsn = Marshal.ReadInt64(output);
                    Marshal.WriteInt64(input, 0, nextUsn);
                    var offset = 8;
                    while (offset + 60 <= bytesReturned)
                    {
                        returnedChanges++;
                        if (returnedChanges > MaximumReturnedChanges)
                        {
                            return new UsnReadResult(
                                false,
                                info.NextUsn,
                                info.JournalId,
                                [],
                                "The USN delta contains too many records; a verification scan is required.");
                        }

                        var recordLength = Marshal.ReadInt32(output, offset);
                        if (recordLength < 60 ||
                            offset + recordLength > bytesReturned)
                        {
                            break;
                        }

                        var majorVersion =
                            Marshal.ReadInt16(output, offset + 4);
                        if (majorVersion != 2)
                        {
                            return new UsnReadResult(
                                false,
                                info.NextUsn,
                                info.JournalId,
                                [],
                                $"USN record version {majorVersion} requires a verification scan.");
                        }

                        var parentReference =
                            unchecked((ulong)Marshal.ReadInt64(
                                output,
                                offset + 16));
                        if (changedDirectories.Add(parentReference) &&
                            changedDirectories.Count >
                            maximumChangedDirectories)
                        {
                            return new UsnReadResult(
                                false,
                                info.NextUsn,
                                info.JournalId,
                                [],
                                "The USN delta changed too many directories; a verification scan is required.");
                        }
                        offset += recordLength;
                    }
                }

                return new UsnReadResult(
                    true,
                    nextUsn,
                    info.JournalId,
                    changedDirectories
                        .Order()
                        .Select(item => new UsnDirectoryChange(item))
                        .ToList(),
                    string.Empty);
            }
            finally
            {
                Marshal.FreeHGlobal(input);
                Marshal.FreeHGlobal(output);
            }
        }
        catch (Win32Exception exception)
        {
            return new UsnReadResult(
                false,
                info.NextUsn,
                info.JournalId,
                [],
                exception.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        var handle = CreateFile(
            GetVolumeHandlePath(volumeRoot),
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    internal static string GetVolumeHandlePath(string volumeRoot)
    {
        var root = FileIdentityService.ToExtendedPath(
            StorageRepository.NormalizeFolderPath(volumeRoot));
        return root.EndsWith('\\') ? root : root + "\\";
    }

    internal static string? TryGetPathByFileId(
        string volumeRoot,
        ulong fileReference) =>
        TryResolvePathByFileId(
            volumeRoot,
            fileReference).Path;

    internal static UsnPathResolution TryResolvePathByFileId(
        string volumeRoot,
        ulong fileReference)
    {
        try
        {
            using var volume = OpenVolume(volumeRoot);
            var descriptor = new FileIdDescriptor
            {
                Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
                Type = 0,
                FileId = fileReference,
            };
            using var handle = OpenFileById(
                volume,
                ref descriptor,
                FileReadAttributes,
                FileShareAll,
                IntPtr.Zero,
                FileFlagBackupSemantics);
            if (handle.IsInvalid)
            {
                return new UsnPathResolution(
                    null,
                    Marshal.GetLastWin32Error());
            }

            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0)
            {
                return new UsnPathResolution(
                    null,
                    Marshal.GetLastWin32Error());
            }

            var path = buffer.ToString(0, checked((int)length));
            if (path.StartsWith(
                    @"\\?\UNC\",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UsnPathResolution(
                    @"\\" + path[8..],
                    0);
            }

            return new UsnPathResolution(
                path.StartsWith(
                        @"\\?\",
                        StringComparison.OrdinalIgnoreCase)
                    ? path[4..]
                    : path,
                0);
        }
        catch (Win32Exception exception)
        {
            return new UsnPathResolution(
                null,
                exception.NativeErrorCode);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalData
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public int Type;

        [FieldOffset(8)]
        public ulong FileId;
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
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out UsnJournalData outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);

}

public sealed record UsnJournalInfo(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    ulong MaximumSize);

public sealed record UsnReadResult(
    bool Success,
    long NextUsn,
    ulong JournalId,
    IReadOnlyList<UsnDirectoryChange> Directories,
    string Reason);

public sealed record UsnDirectoryChange(
    ulong ParentReference);

internal sealed record UsnPathResolution(
    string? Path,
    int ErrorCode);
