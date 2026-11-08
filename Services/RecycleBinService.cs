using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace StorageLens.Services;

public static partial class RecycleBinService
{
    private const uint ClassContextAll = 23;
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxWantNukeWarning = 0x00002000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint FofxEarlyFailure = 0x00100000;
    private static readonly Guid FileOperationClassId =
        new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid FileOperationInterfaceId =
        new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid ShellItemInterfaceId =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public static void Recycle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath) ??
                       throw new ArgumentException("The path does not have a drive root.", nameof(path));
        if (!CanRecycle(rootPath))
        {
            throw new InvalidOperationException(
                "Recycle Bin is unavailable for this volume. The item was not deleted.");
        }

        var shellItem = CreateShellItem(fullPath);
        var fileOperation = CreateFileOperation();
        var progress = new DeleteProgressSink();
        try
        {
            ThrowIfFailed(
                fileOperation.SetOperationFlags(
                    FofSilent |
                    FofNoConfirmation |
                    FofNoErrorUi |
                    FofxRecycleOnDelete |
                    FofxWantNukeWarning |
                    FofxEarlyFailure),
                "configure the Recycle Bin operation");
            ThrowIfFailed(
                fileOperation.DeleteItem(shellItem, progress),
                "queue the item for recycling");
            ThrowIfFailed(
                fileOperation.PerformOperations(),
                "move the item to the Recycle Bin");
            ThrowIfFailed(
                fileOperation.GetAnyOperationsAborted(out var aborted),
                "verify the Recycle Bin operation");
            if (aborted || File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new Win32Exception(
                    1223,
                    "The item was not confirmed in the Recycle Bin operation.");
            }

            if (!progress.Recycled)
            {
                throw new InvalidOperationException(
                    "Windows did not report a Recycle Bin destination. The item was not treated as safely recycled.");
            }
        }
        finally
        {
            (fileOperation as IDisposable)?.Dispose();
            (shellItem as IDisposable)?.Dispose();
        }
    }

    public static bool CanRecycle(string driveRoot)
    {
        if (!Path.IsPathRooted(driveRoot))
        {
            return false;
        }

        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            return false;
        }

        var information = new ShQueryRecycleBinInfo
        {
            StructureSize = (uint)Marshal.SizeOf<ShQueryRecycleBinInfo>(),
        };
        return SHQueryRecycleBin(driveRoot, ref information) == 0;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var shellItemId = ShellItemInterfaceId;
        ThrowIfFailed(
            SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                in shellItemId,
                out var item),
            "resolve the file for the Shell");
        return item;
    }

    private static IFileOperation CreateFileOperation()
    {
        var classId = FileOperationClassId;
        var interfaceId = FileOperationInterfaceId;
        ThrowIfFailed(
            CoCreateInstance(
                in classId,
                IntPtr.Zero,
                ClassContextAll,
                in interfaceId,
                out var operation),
            "create the Shell file operation");
        return operation;
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new Win32Exception(
                hresult,
                $"Windows could not {operation} (HRESULT 0x{hresult:X8}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRecycleBinInfo
    {
        public uint StructureSize;

        public ulong Size;

        public ulong NumberOfItems;
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal partial interface IShellItem;

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    internal partial interface IFileOperation
    {
        [PreserveSig]
        int Advise(IntPtr operationProgressSink, out uint cookie);

        [PreserveSig]
        int Unadvise(uint cookie);

        [PreserveSig]
        int SetOperationFlags(uint operationFlags);

        [PreserveSig]
        int SetProgressMessage(string message);

        [PreserveSig]
        int SetProgressDialog(IntPtr progressDialog);

        [PreserveSig]
        int SetProperties(IntPtr propertyChangeArray);

        [PreserveSig]
        int SetOwnerWindow(IntPtr ownerWindow);

        [PreserveSig]
        int ApplyPropertiesToItem(IShellItem item);

        [PreserveSig]
        int ApplyPropertiesToItems(IntPtr items);

        [PreserveSig]
        int RenameItem(IShellItem item, string newName, IntPtr operationProgressSink);

        [PreserveSig]
        int RenameItems(IntPtr items, string newName);

        [PreserveSig]
        int MoveItem(
            IShellItem item,
            IShellItem destinationFolder,
            string newName,
            IntPtr operationProgressSink);

        [PreserveSig]
        int MoveItems(IntPtr items, IShellItem destinationFolder);

        [PreserveSig]
        int CopyItem(
            IShellItem item,
            IShellItem destinationFolder,
            string newName,
            IntPtr operationProgressSink);

        [PreserveSig]
        int CopyItems(IntPtr items, IShellItem destinationFolder);

        [PreserveSig]
        int DeleteItem(
            IShellItem item,
            IFileOperationProgressSink operationProgressSink);

        [PreserveSig]
        int DeleteItems(IntPtr items);

        [PreserveSig]
        int NewItem(
            IShellItem destinationFolder,
            uint fileAttributes,
            string name,
            string templateName,
            IntPtr operationProgressSink);

        [PreserveSig]
        int PerformOperations();

        [PreserveSig]
        int GetAnyOperationsAborted(
            [MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    internal partial interface IFileOperationProgressSink
    {
        [PreserveSig]
        int StartOperations();

        [PreserveSig]
        int FinishOperations(int result);

        [PreserveSig]
        int PreRenameItem(
            uint flags,
            IShellItem item,
            string newName);

        [PreserveSig]
        int PostRenameItem(
            uint flags,
            IShellItem item,
            string newName,
            int result,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName);

        [PreserveSig]
        int PostMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName,
            int result,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName);

        [PreserveSig]
        int PostCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName,
            int result,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreDeleteItem(uint flags, IShellItem item);

        [PreserveSig]
        int PostDeleteItem(
            uint flags,
            IShellItem item,
            int result,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreNewItem(
            uint flags,
            IShellItem destinationFolder,
            string newName);

        [PreserveSig]
        int PostNewItem(
            uint flags,
            IShellItem destinationFolder,
            string newName,
            string? templateName,
            uint attributes,
            int result,
            IShellItem? newlyCreated);

        [PreserveSig]
        int UpdateProgress(uint workTotal, uint workSoFar);

        [PreserveSig]
        int ResetTimer();

        [PreserveSig]
        int PauseTimer();

        [PreserveSig]
        int ResumeTimer();
    }

    [GeneratedComClass]
    internal sealed partial class DeleteProgressSink :
        IFileOperationProgressSink
    {
        public bool Recycled { get; private set; }

        public int StartOperations() => 0;

        public int FinishOperations(int result) => 0;

        public int PreRenameItem(uint flags, IShellItem item, string newName) => 0;

        public int PostRenameItem(
            uint flags,
            IShellItem item,
            string newName,
            int result,
            IShellItem? newlyCreated) => 0;

        public int PreMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName) => 0;

        public int PostMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName,
            int result,
            IShellItem? newlyCreated) => 0;

        public int PreCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName) => 0;

        public int PostCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName,
            int result,
            IShellItem? newlyCreated) => 0;

        public int PreDeleteItem(uint flags, IShellItem item) => 0;

        public int PostDeleteItem(
            uint flags,
            IShellItem item,
            int result,
            IShellItem? newlyCreated)
        {
            if (result >= 0)
            {
                Recycled = true;
            }

            return 0;
        }

        public int PreNewItem(
            uint flags,
            IShellItem destinationFolder,
            string newName) => 0;

        public int PostNewItem(
            uint flags,
            IShellItem destinationFolder,
            string newName,
            string? templateName,
            uint attributes,
            int result,
            IShellItem? newlyCreated) => 0;

        public int UpdateProgress(uint workTotal, uint workSoFar) => 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;
    }

    [LibraryImport(
        "shell32.dll",
        EntryPoint = "SHQueryRecycleBinW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(
        string rootPath,
        ref ShQueryRecycleBinInfo information);

    [LibraryImport(
        "shell32.dll",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        in Guid interfaceId,
        out IShellItem item);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        IntPtr outerUnknown,
        uint classContext,
        in Guid interfaceId,
        out IFileOperation operation);
}
