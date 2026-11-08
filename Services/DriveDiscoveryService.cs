using StorageLens.Models;
using StorageLens.Localization;
using System.Security;

namespace StorageLens.Services;

public static class DriveDiscoveryService
{
    public static IReadOnlyList<DriveSummary> GetAvailableDrives()
    {
        var roots = new SortedSet<string>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                AddRoot(roots, drive.Name);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }

        try
        {
            foreach (var root in Environment.GetLogicalDrives())
            {
                AddRoot(roots, root);
            }
        }
        catch (SecurityException)
        {
        }

        var drives = new List<DriveSummary>();
        foreach (var root in roots)
        {
            if (TryCreateSummary(root) is { } summary)
            {
                drives.Add(summary);
            }
        }

        return drives;
    }

    private static DriveSummary? TryCreateSummary(string root)
    {
        var drive = new DriveInfo(root);
        var driveType = TryGetMetadata(() => drive.DriveType);
        if (driveType is not (DriveType.Fixed or DriveType.Removable))
        {
            return null;
        }

        if (TryGetMetadata(() => drive.IsReady) is false)
        {
            return null;
        }

        var letter = root.Length >= 2 ? root[..2] : string.Empty;
        if (string.IsNullOrWhiteSpace(letter))
        {
            return null;
        }

        var totalBytes = Math.Max(
            0,
            TryGetMetadata(() => drive.TotalSize) ?? 0);
        var availableBytes = Math.Max(
            0,
            TryGetMetadata(() => drive.AvailableFreeSpace) ?? 0);
        if (totalBytes > 0 && availableBytes > totalBytes)
        {
            availableBytes = totalBytes;
        }

        var name = TryGetMetadata(() => drive.VolumeLabel);
        return new DriveSummary
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? GetDefaultDriveName(driveType.Value)
                : name,
            Letter = letter,
            FileSystem =
                TryGetMetadata(() => drive.DriveFormat) ?? string.Empty,
            VolumeId =
                TryGetMetadata(() =>
                    VolumeIdentityService.TryGetVolumeSerial(letter)) ??
                letter,
            TotalBytes = totalBytes,
            UsedBytes = totalBytes - availableBytes,
        };
    }

    private static void AddRoot(
        ISet<string> roots,
        string root)
    {
        if (root.Length >= 2 &&
            char.IsLetter(root[0]) &&
            root[1] == ':')
        {
            roots.Add(root[..2] + "\\");
        }
    }

    private static T? TryGetMetadata<T>(Func<T> accessor)
        where T : struct
    {
        try
        {
            return accessor();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private static string? TryGetMetadata(Func<string?> accessor)
    {
        try
        {
            return accessor();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private static string GetDefaultDriveName(DriveType driveType) =>
        driveType switch
        {
            DriveType.Removable => AppText.Get("DriveNameRemovable"),
            DriveType.Network => AppText.Get("DriveNameNetwork"),
            _ => AppText.Get("DriveNameLocal"),
        };
}
