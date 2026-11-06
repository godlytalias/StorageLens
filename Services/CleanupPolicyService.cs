using StorageLens.Models;

namespace StorageLens.Services;

public sealed class CleanupPolicyDefinition
{
    public required CleanupPolicyId Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Glyph { get; init; }

    public required CleanupSafety Safety { get; init; }

    public bool AutomaticEligible { get; init; }
}

public static class CleanupPolicyService
{
    private static readonly IReadOnlyList<string> TemporaryRoots =
        CreateKnownTemporaryRoots();
    private static readonly IReadOnlyList<string> PackageCacheRoots =
        CreateKnownPackageCacheRoots();

    public static IReadOnlyList<CleanupPolicyDefinition> Policies { get; } =
    [
        new()
        {
            Id = CleanupPolicyId.TemporaryFiles,
            Name = "Temporary app files",
            Description = "Known temporary and rebuildable cache files older than 24 hours.",
            Glyph = "\uE81C",
            Safety = CleanupSafety.Safe,
            AutomaticEligible = true,
        },
        new()
        {
            Id = CleanupPolicyId.OldDownloads,
            Name = "Downloads older than 90 days",
            Description = "Old installers, archives, and documents in the user's Downloads folder.",
            Glyph = "\uE896",
            Safety = CleanupSafety.Review,
        },
        new()
        {
            Id = CleanupPolicyId.PackageCaches,
            Name = "Package caches",
            Description = "Rebuildable user caches older than 24 hours. Close apps and builds first; packages may need to be restored or downloaded again.",
            Glyph = "\uE7B8",
            Safety = CleanupSafety.Review,
            AutomaticEligible = false,
        },
        new()
        {
            Id = CleanupPolicyId.LargeDuplicates,
            Name = "Large duplicate files",
            Description = "Content-verified duplicate files; the original path is retained.",
            Glyph = "\uE8C8",
            Safety = CleanupSafety.Review,
        },
        new()
        {
            Id = CleanupPolicyId.PreviousWindowsInstallation,
            Name = "Previous Windows installation",
            Description = "Windows.old can consume substantial space, but it affects rollback and should be removed through Windows tools.",
            Glyph = "\uE770",
            Safety = CleanupSafety.Caution,
        },
    ];

    public static StoragePathSafety Evaluate(
        StorageFileRecord record,
        CleanupPolicyId policy,
        DateTimeOffset now,
        bool automatic)
    {
        if ((record.Attributes & (long)(FileAttributes.System | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            return NotAllowed("System, device, and reparse-point entries are never cleaned.");
        }

        if (record.IsCloudOnly)
        {
            return NotAllowed("Cloud-only files are excluded from cleanup.");
        }

        if (record.IsHardLinkAlias)
        {
            return NotAllowed(
                "Hard-link aliases do not reclaim physical storage and are excluded from cleanup.");
        }

        if (policy == CleanupPolicyId.PreviousWindowsInstallation)
        {
            return NotAllowed(
                "Use Windows Storage Sense to review Windows.old safely.");
        }

        if (StorageCategoryClassifier.IsSystemPath(record.FullPath))
        {
            return NotAllowed("Protected operating-system locations are excluded.");
        }

        if (record.SizeBytes < 0)
        {
            return NotAllowed("The indexed file size is invalid.");
        }

        var definition = Policies.First(item => item.Id == policy);
        if (automatic && !definition.AutomaticEligible)
        {
            return NotAllowed("This category requires manual review.");
        }

        return policy switch
        {
            CleanupPolicyId.TemporaryFiles when
                IsKnownTemporaryFile(record.FullPath) &&
                record.ModifiedUtc <= now.AddHours(-24) => Allowed(),
            CleanupPolicyId.TemporaryFiles => NotAllowed("Only known temporary files older than 24 hours are eligible."),
            CleanupPolicyId.OldDownloads when
                IsInsideDownloads(record.FullPath) &&
                record.ModifiedUtc <= now.AddDays(-90) => Allowed(),
            CleanupPolicyId.OldDownloads => NotAllowed("Only files in Downloads older than 90 days are eligible."),
            CleanupPolicyId.PackageCaches when
                IsKnownPackageCache(Path.GetFullPath(record.FullPath)) &&
                record.ModifiedUtc <= now.AddHours(-24) => Allowed(),
            CleanupPolicyId.PackageCaches =>
                NotAllowed("Only allowlisted user caches older than 24 hours can be recycled after manual review."),
            CleanupPolicyId.LargeDuplicates => automatic
                ? NotAllowed("Duplicate cleanup always requires manual review.")
                : Allowed(),
            CleanupPolicyId.PreviousWindowsInstallation =>
                NotAllowed("Use Windows Storage Sense or DISM to review Windows.old."),
            _ => NotAllowed("The cleanup category is not recognized."),
        };
    }

    public static bool IsKnownTemporaryFile(string path)
    {
        var roots = GetTemporaryRoots();
        return roots.Any(root => StorageCategoryClassifier.IsInside(path, root));
    }

    public static bool IsKnownPackageCache(string path)
    {
        var roots = GetPackageCacheRoots();
        return roots.Any(root => StorageCategoryClassifier.IsInside(path, root));
    }

    public static bool IsInsideDownloads(string path)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        return StorageCategoryClassifier.IsInside(path, downloads);
    }

    public static bool RequiresScanFileIdentity(string path, string category) =>
        IsInsideDownloads(path) ||
        (category == "Cache & temporary" &&
         (IsKnownTemporaryFile(path) || IsKnownPackageCache(path)));

    public static CleanupPolicyDefinition GetDefinition(CleanupPolicyId id) =>
        Policies.First(item => item.Id == id);

    public static CleanupPolicyId? ParsePolicy(string category)
    {
        if (string.Equals(category, "Cache & temporary", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupPolicyId.TemporaryFiles;
        }

        if (string.Equals(category, "Downloads", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupPolicyId.OldDownloads;
        }

        if (string.Equals(category, "Duplicates", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupPolicyId.LargeDuplicates;
        }

        if (string.Equals(category, "Package caches", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupPolicyId.PackageCaches;
        }

        return Policies
            .FirstOrDefault(item => string.Equals(item.Name, category, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    public static IReadOnlyList<string> GetTemporaryRoots() => TemporaryRoots;

    public static IReadOnlyList<string> GetPackageCacheRoots() => PackageCacheRoots;

    private static IReadOnlyList<string> CreateKnownTemporaryRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Path.GetTempPath(),
            Path.Combine(localAppData, "Temp"),
            Path.Combine(localAppData, "D3DSCache"),
            Path.Combine(localAppData, "CrashDumps"),
        ];
    }

    private static IReadOnlyList<string> CreateKnownPackageCacheRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(localAppData, "NuGet", "v3-cache"),
            Path.Combine(localAppData, "npm-cache"),
            Path.Combine(userProfile, ".nuget", "packages"),
            Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"),
            Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"),
        ];
    }

    private static StoragePathSafety Allowed() =>
        new() { IsAllowed = true, Reason = "Eligible" };

    private static StoragePathSafety NotAllowed(string reason) =>
        new() { IsAllowed = false, Reason = reason };
}
