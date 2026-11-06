using StorageLens.Models;

namespace StorageLens.Services;

public static class StorageCategoryClassifier
{
    private static readonly string[] CategoryNames =
    [
        "Apps & games",
        "Videos",
        "Photos",
        "System",
        "Documents",
        "Cache & temporary",
        "Other",
    ];

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff", ".webp",
        ".raw", ".cr2", ".cr3", ".nef", ".dng", ".arw",
    };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm", ".mpeg", ".mpg", ".mts", ".mxf",
    };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".md",
        ".csv", ".zip", ".7z", ".rar", ".tar", ".gz", ".pst", ".ost", ".one",
    };
    private static readonly HashSet<string> AppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".msix", ".appx", ".cab", ".iso", ".vhd", ".vhdx",
    };

    private static readonly string WindowsPath =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/');
    private static readonly string UserProfilePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
    private static readonly string LocalAppDataPath =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\', '/');
    private static readonly string DownloadsPath = Path.Combine(UserProfilePath, "Downloads");
    private static readonly string WindowsOldPath =
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty, "Windows.old");
    private static readonly string[] CacheRoots = CreateCacheRoots();
    private static readonly string[] ProgramRoots = CreateProgramRoots();

    public static string ClassifyPath(string path, string extension, FileAttributes attributes)
    {
        var normalizedExtension = extension ?? string.Empty;
        if (IsInside(path, WindowsPath) ||
            (attributes & FileAttributes.System) != 0)
        {
            return "System";
        }

        if (IsCachePath(path) ||
            IsInside(path, Path.GetTempPath()))
        {
            return "Cache & temporary";
        }

        if (IsProgramFilesPath(path) ||
            AppExtensions.Contains(normalizedExtension))
        {
            return "Apps & games";
        }

        if (PhotoExtensions.Contains(normalizedExtension) ||
            HasSegment(path, "Pictures") ||
            HasSegment(path, "Photos"))
        {
            return "Photos";
        }

        if (VideoExtensions.Contains(normalizedExtension) ||
            HasSegment(path, "Videos") ||
            HasSegment(path, "Movies"))
        {
            return "Videos";
        }

        if (DocumentExtensions.Contains(normalizedExtension) ||
            HasSegment(path, "Documents"))
        {
            return "Documents";
        }

        return "Other";
    }

    public static string TagPath(string path, string category)
    {
        if (category == "System")
        {
            return "System";
        }

        if (category == "Cache & temporary")
        {
            return "Cache";
        }

        if (IsInside(path, DownloadsPath))
        {
            return "Review";
        }

        if (IsInside(path, UserProfilePath))
        {
            return "Personal";
        }

        if (IsProgramFilesPath(path))
        {
            return "Apps";
        }

        return category == "Other" ? "Other" : category;
    }

    public static int CategoryId(string category)
    {
        var index = Array.IndexOf(CategoryNames, category);
        return index >= 0 ? index : CategoryNames.Length - 1;
    }

    public static string CategoryName(int categoryId) =>
        categoryId >= 0 && categoryId < CategoryNames.Length
            ? CategoryNames[categoryId]
            : CategoryNames[^1];

    public static int TagId(string tag) =>
        tag switch
        {
            "System" => 1,
            "Cache" => 2,
            "Review" => 3,
            "Personal" => 4,
            "Apps" => 5,
            _ => 0,
        };

    public static string TagName(int tagId) =>
        tagId switch
        {
            1 => "System",
            2 => "Cache",
            3 => "Review",
            4 => "Personal",
            5 => "Apps",
            _ => "Other",
        };

    public static string DescriptionFor(string category) =>
        category switch
        {
            "Apps & games" => "Installed apps, executables, virtual disks, and game libraries",
            "Videos" => "Recordings, clips, exports, and media caches",
            "Photos" => "Camera roll, screenshots, and image libraries",
            "System" => "Windows, servicing, and recovery files",
            "Documents" => "Work files, archives, mail, and documents",
            "Cache & temporary" => "Temporary, package, and rebuildable cache data",
            _ => "Development tools and uncategorized files",
        };

    public static string GlyphFor(string category) =>
        category switch
        {
            "Apps & games" => "\uE7FC",
            "Videos" => "\uE714",
            "Photos" => "\uEB9F",
            "System" => "\uE770",
            "Documents" => "\uE8A5",
            "Cache & temporary" => "\uE81C",
            _ => "\uE8F2",
        };

    public static bool IsSystemPath(string path) =>
        IsInside(path, WindowsPath) ||
        IsInside(path, WindowsOldPath);

    public static bool IsCachePath(string path)
    {
        if (string.IsNullOrWhiteSpace(LocalAppDataPath))
        {
            return false;
        }

        if (CacheRoots.Any(root => IsInside(path, root)))
        {
            return true;
        }

        return false;
    }

    public static bool IsInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string normalizedPath;
        string normalizedRoot;
        try
        {
            normalizedPath = Path.IsPathFullyQualified(path)
                ? path.TrimEnd('\\', '/')
                : Path.GetFullPath(path).TrimEnd('\\', '/');
            normalizedRoot = Path.IsPathFullyQualified(root)
                ? root.TrimEnd('\\', '/')
                : Path.GetFullPath(root).TrimEnd('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        return IsInsideNormalized(normalizedPath, normalizedRoot);
    }

    private static bool IsProgramFilesPath(string path) =>
        ProgramRoots.Any(root => IsInside(path, root));

    private static bool HasSegment(string path, string segment)
    {
        var searchOffset = 0;
        while (searchOffset < path.Length)
        {
            var index = path.IndexOf(
                segment,
                searchOffset,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var beforeIsBoundary = index == 0 ||
                                   path[index - 1] is '\\' or '/';
            var end = index + segment.Length;
            var afterIsBoundary = end == path.Length ||
                                  path[end] is '\\' or '/';
            if (beforeIsBoundary && afterIsBoundary)
            {
                return true;
            }

            searchOffset = end;
        }

        return false;
    }

    private static bool IsInsideNormalized(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > root.Length &&
         path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
         path[root.Length] is '\\' or '/');

    private static string[] CreateCacheRoots() =>
        [
            Path.Combine(LocalAppDataPath, "Temp"),
            Path.Combine(LocalAppDataPath, "CrashDumps"),
            Path.Combine(LocalAppDataPath, "D3DSCache"),
            Path.Combine(LocalAppDataPath, "NVIDIA"),
            Path.Combine(LocalAppDataPath, "NuGet", "v3-cache"),
            Path.Combine(LocalAppDataPath, "npm-cache"),
            Path.Combine(LocalAppDataPath, "Microsoft", "Edge", "User Data"),
            Path.Combine(LocalAppDataPath, "Google", "Chrome", "User Data"),
            Path.Combine(LocalAppDataPath, "Microsoft", "Windows", "INetCache"),
            Path.Combine(LocalAppDataPath, "Microsoft", "Windows", "WebCache"),
        ];

    private static string[] CreateProgramRoots() =>
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(LocalAppDataPath, "Programs"),
        ];
}
