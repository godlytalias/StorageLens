namespace StorageLens.Services;

public static class StorageReparseTags
{
    public const uint MountPoint = 0xA0000003;
    public const uint SymbolicLink = 0xA000000C;
    public const uint Dfs = 0x8000000A;
    public const uint Dfsr = 0x80000012;
    public const uint CloudFiles = 0x9000001A;
    public const uint ProjectedFileSystem = 0x9000001C;
    public const uint OneDrive = 0x9000001A;
    public const uint Wof = 0x80000017;
}

public enum ReparseAction
{
    Traverse,
    RecordOnly,
    ExcludeVirtualized,
}

public static class StorageReparsePolicy
{
    public static ReparseAction GetAction(
        bool isDirectory,
        uint reparseTag)
    {
        if (reparseTag == 0)
        {
            return isDirectory ? ReparseAction.Traverse : ReparseAction.RecordOnly;
        }

        return reparseTag switch
        {
            StorageReparseTags.CloudFiles => isDirectory
                ? ReparseAction.Traverse
                : ReparseAction.RecordOnly,
            StorageReparseTags.ProjectedFileSystem => ReparseAction.ExcludeVirtualized,
            StorageReparseTags.MountPoint or
                StorageReparseTags.SymbolicLink or
                StorageReparseTags.Dfs or
                StorageReparseTags.Dfsr => ReparseAction.RecordOnly,
            _ => isDirectory
                ? ReparseAction.RecordOnly
                : ReparseAction.RecordOnly,
        };
    }
}
