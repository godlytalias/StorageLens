using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.ApplicationModel.Background;
using WinRT;

namespace StorageLens.BackgroundTasks;

internal static partial class ComServer
{
    private const uint ClassContextLocalServer = 4;
    private const uint RegisterMultipleUse = 1;
    private const uint Success = 0;
    private const uint ClassENoAggregation = 0x80040110;
    private const uint ErrorNoInterface = 0x80004002;
    private const string IUnknownId = "00000000-0000-0000-C000-000000000046";
    private const string IClassFactoryId = "00000001-0000-0000-C000-000000000046";

    [LibraryImport("ole32.dll")]
    internal static partial int CoRegisterClassObject(
        ref Guid classId,
        [MarshalAs(UnmanagedType.Interface)] IClassFactory classFactory,
        uint executionContext,
        uint flags,
        out uint registrationToken);

    [LibraryImport("ole32.dll")]
    internal static partial int CoRevokeClassObject(uint registrationToken);

    internal static uint RegisterStorageMonitorFactory()
    {
        var classId = typeof(StorageMonitorBackgroundTask).GUID;
        var result = CoRegisterClassObject(
            ref classId,
            new StorageMonitorTaskFactory(),
            ClassContextLocalServer,
            RegisterMultipleUse,
            out var token);
        Marshal.ThrowExceptionForHR(result);
        return token;
    }

    internal static uint RegisterStorageIndexFactory()
    {
        var classId = typeof(StorageIndexBackgroundTask).GUID;
        var result = CoRegisterClassObject(
            ref classId,
            new StorageIndexTaskFactory(),
            ClassContextLocalServer,
            RegisterMultipleUse,
            out var token);
        Marshal.ThrowExceptionForHR(result);
        return token;
    }

    internal static void Unregister(uint registrationToken)
    {
        if (registrationToken != 0)
        {
            CoRevokeClassObject(registrationToken);
        }
    }

    [GeneratedComInterface]
    [Guid(IClassFactoryId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        uint CreateInstance(IntPtr outer, in Guid interfaceId, out IntPtr objectPointer);

        [PreserveSig]
        uint LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [GeneratedComClass]
    private sealed partial class StorageMonitorTaskFactory : IClassFactory
    {
        public uint CreateInstance(IntPtr outer, in Guid interfaceId, out IntPtr objectPointer)
        {
            if (outer != IntPtr.Zero)
            {
                objectPointer = IntPtr.Zero;
                return ClassENoAggregation;
            }

            if (interfaceId != typeof(IBackgroundTask).GUID &&
                interfaceId != new Guid(IUnknownId))
            {
                objectPointer = IntPtr.Zero;
                return ErrorNoInterface;
            }

            objectPointer = MarshalInterface<IBackgroundTask>.FromManaged(new StorageMonitorBackgroundTask());
            return Success;
        }

        public uint LockServer(bool lockServer) => Success;
    }

    [GeneratedComClass]
    private sealed partial class StorageIndexTaskFactory : IClassFactory
    {
        public uint CreateInstance(IntPtr outer, in Guid interfaceId, out IntPtr objectPointer)
        {
            if (outer != IntPtr.Zero)
            {
                objectPointer = IntPtr.Zero;
                return ClassENoAggregation;
            }

            if (interfaceId != typeof(IBackgroundTask).GUID &&
                interfaceId != new Guid(IUnknownId))
            {
                objectPointer = IntPtr.Zero;
                return ErrorNoInterface;
            }

            objectPointer = MarshalInterface<IBackgroundTask>.FromManaged(new StorageIndexBackgroundTask());
            return Success;
        }

        public uint LockServer(bool lockServer) => Success;
    }
}
