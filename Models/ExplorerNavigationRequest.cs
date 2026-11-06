namespace StorageLens.Models;

public sealed class ExplorerNavigationRequest
{
    public string? DriveLetter { get; init; }

    public string? Category { get; init; }

    public string? FolderPath { get; init; }

    public string? SelectedItemPath { get; init; }

    public string? SearchText { get; init; }
}
