using System.Runtime.InteropServices;
using System.Text;

namespace StorageLens.Services;

internal static partial class VolumeIdentityService
{
    public static string? TryGetVolumeSerial(string driveLetter)
    {
        var root = StorageRepository.NormalizeFolderPath(
            driveLetter.TrimEnd('\\') + "\\");
        var identity = FileIdentityService.TryGetIdentity(root);
        if (identity is not null)
        {
            return identity.VolumeSerialNumber.ToString(
                "X16",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var volumeName = new StringBuilder(261);
        var fileSystemName = new StringBuilder(261);
        if (!GetVolumeInformation(
                FileIdentityService.ToExtendedPath(root),
                volumeName,
                volumeName.Capacity,
                out var serial,
                out _,
                out _,
                fileSystemName,
                fileSystemName.Capacity))
        {
            return null;
        }

        return serial.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}
