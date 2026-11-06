namespace StorageLens.Models;

public sealed class StoragePathSafety
{
    public bool IsAllowed { get; init; }

    public string Reason { get; init; } = string.Empty;
}
