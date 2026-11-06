using CommunityToolkit.Mvvm.ComponentModel;
using StorageLens.Localization;

namespace StorageLens.Models;

public sealed class CleanupCategorySelection : ObservableObject
{
    private bool _isSelected;

    public CleanupCategorySelection(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public string DisplayName => StorageText.PolicyCategory(Name);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
