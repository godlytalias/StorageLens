using StorageLens.Models;
using StorageLens.Localization;

namespace StorageLens.Services;

public static class DriveDiscoveryService
{
    public static IReadOnlyList<DriveSummary> GetAvailableDrives()
    {
        var drives = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                drives.Add(new DriveSummary
                {
                    Name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? GetDefaultDriveName(drive.DriveType)
                        : drive.VolumeLabel,
                    Letter = drive.Name[..2],
                    FileSystem = drive.DriveFormat,
                    VolumeId = VolumeIdentityService.TryGetVolumeSerial(
                                   drive.Name[..2]) ??
                               drive.Name[..2],
                    TotalBytes = drive.TotalSize,
                    UsedBytes = drive.TotalSize - drive.AvailableFreeSpace,
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return drives;
    }

    private static string GetDefaultDriveName(DriveType driveType) =>
        driveType switch
        {
            DriveType.Removable => AppText.Get("DriveNameRemovable"),
            DriveType.Network => AppText.Get("DriveNameNetwork"),
            _ => AppText.Get("DriveNameLocal"),
        };
}
