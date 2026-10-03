using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class LocalFolderNodeViewModel : INotifyPropertyChanged
{
    private bool _isCompactSelected;

    public LocalFolderNodeViewModel(
        Guid? folderId,
        string relativePath,
        string fullPath,
        string displayName,
        int assetCount,
        bool isOffline)
    {
        FolderId = folderId;
        RelativePath = relativePath;
        FullPath = fullPath;
        DisplayName = displayName;
        AssetCountLabel = assetCount.ToString("N0");
        IsOffline = isOffline;
    }

    public Guid? FolderId { get; }

    public string RelativePath { get; }

    public string FullPath { get; }

    public string DisplayName { get; }

    public string AssetCountLabel { get; }

    public bool IsOffline { get; }

    public bool CanRemoveFromCatalog => FolderId.HasValue && RelativePath.Length == 0;

    public ObservableCollection<LocalFolderNodeViewModel> Children { get; } = [];

    public bool IsCompactSelected
    {
        get => _isCompactSelected;
        set
        {
            if (_isCompactSelected == value)
            {
                return;
            }

            _isCompactSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
