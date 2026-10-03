using System.Collections.ObjectModel;
using System.ComponentModel;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class CloudFolderNodeViewModel : INotifyPropertyChanged
{
    private bool _isCompactSelected;

    public CloudFolderNodeViewModel(ApiAssetFolder folder, Guid? currentUserId, bool isAdmin)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        CanMoveOrDelete = isAdmin || folder.CreatedBy.Id == currentUserId;
        UiLocalization.Register(this, static value => value.RefreshLocalization());
    }

    public ApiAssetFolder Folder { get; }

    public Guid Id => Folder.Id;

    public Guid? ParentId => Folder.ParentId;

    public string Name => Folder.Name;

    public string CreatorLabel => UiLocalization.Format(
        "创建者：{0}",
        string.IsNullOrWhiteSpace(Folder.CreatedBy.DisplayName)
            ? $"@{Folder.CreatedBy.Username}"
            : Folder.CreatedBy.DisplayName);

    public bool CanMoveOrDelete { get; }

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompactSelected)));
        }
    }

    public ObservableCollection<CloudFolderNodeViewModel> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshLocalization() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CreatorLabel)));
}

public sealed record CloudFolderChoiceViewModel(Guid? FolderId, string DisplayPath, bool IsRoot);
