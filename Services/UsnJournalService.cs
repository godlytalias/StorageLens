using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageLens.Services;

public static partial class UsnJournalService
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const int BufferSize = 1024 * 1024;
    private const uint ReasonMask = 0xFFFFFFFF;
    private const int ErrorHandleEof = 38;
    private const int MaximumReturnedChanges = 250_000;

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
        int maximumReturnedChanges = MaximumReturnedChanges)
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
                var changes = new Dictionary<ulong, UsnChangeAccumulator>();
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

                        var fileReference =
                            unchecked((ulong)Marshal.ReadInt64(
                                output,
                                offset + 8));
                        var parentReference =
                            unchecked((ulong)Marshal.ReadInt64(
                                output,
                                offset + 16));
                        var reason = unchecked((uint)Marshal.ReadInt32(
                            output,
                            offset + 40));
                        var attributes = unchecked((uint)Marshal.ReadInt32(
                            output,
                            offset + 52));
                        var nameLength =
                            Marshal.ReadInt16(output, offset + 56) &
                            0xFFFF;
                        var nameOffset =
                            Marshal.ReadInt16(output, offset + 58) &
                            0xFFFF;
                        var name = nameLength >= 2
                            ? Marshal.PtrToStringUni(
                                output + offset + nameOffset,
                                nameLength / 2)
                            : string.Empty;
                        returnedChanges++;
                        if (returnedChanges > maximumReturnedChanges)
                        {
                            return new UsnReadResult(
                                false,
                                info.NextUsn,
                                info.JournalId,
                                [],
                                "The USN delta is too large; a verification scan is required.");
                        }

                        if (!changes.TryGetValue(parentReference, out var change))
                        {
                            change = new UsnChangeAccumulator(
                                parentReference);
                            changes.Add(parentReference, change);
                        }

                        change.Files.Add(new UsnFileChange(
                            fileReference,
                            name,
                            reason,
                            attributes));
                        offset += recordLength;
                    }
                }

                return new UsnReadResult(
                    true,
                    nextUsn,
                    info.JournalId,
                    changes.Values
                        .OrderBy(item => item.ParentReference)
                        .Select(item => new UsnDirectoryChange(
                            item.ParentReference,
                            item.Files))
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
        var root = FileIdentityService.ToExtendedPath(
            StorageRepository.NormalizeFolderPath(volumeRoot));
        var handle = CreateFile(
            root.TrimEnd('\\'),
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

    private sealed class UsnChangeAccumulator
    {
        public UsnChangeAccumulator(ulong parentReference)
        {
            ParentReference = parentReference;
        }

        public ulong ParentReference { get; }

        public List<UsnFileChange> Files { get; } = [];
    }
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
    ulong ParentReference,
    IReadOnlyList<UsnFileChange> Files);

public sealed record UsnFileChange(
    ulong FileReference,
    string FileName,
    uint Reason,
    uint FileAttributes);
