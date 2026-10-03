using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed record CloudFolderSelection(Guid? FolderId);

public sealed partial class CloudFolderPickerWindow : Window
{
    public CloudFolderPickerWindow()
        : this([], null, new HashSet<Guid>())
    {
    }

    public CloudFolderPickerWindow(
        IReadOnlyList<ApiAssetFolder> folders,
        Guid? selectedFolderId,
        IReadOnlySet<Guid> excludedFolderIds)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(excludedFolderIds);
        InitializeComponent();
        DataContext = this;

        Choices.Add(new CloudFolderChoiceViewModel(null, UiLocalization.Text("根目录"), true));
        var availableFolders = folders
            .Where(folder => !excludedFolderIds.Contains(folder.Id))
            .ToArray();
        AddChildren(availableFolders, parentId: null, parentPath: string.Empty, new HashSet<Guid>());

        FolderList.SelectedItem = Choices.FirstOrDefault(choice => choice.FolderId == selectedFolderId)
                                  ?? Choices[0];
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
    }

    public ObservableCollection<CloudFolderChoiceViewModel> Choices { get; } = [];

    private void AddChildren(
        IReadOnlyList<ApiAssetFolder> folders,
        Guid? parentId,
        string parentPath,
        HashSet<Guid> visited)
    {
        foreach (var folder in folders
                     .Where(item => item.ParentId == parentId)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!visited.Add(folder.Id))
            {
                continue;
            }

            var path = string.IsNullOrEmpty(parentPath)
                ? folder.Name
                : $"{parentPath} / {folder.Name}";
            Choices.Add(new CloudFolderChoiceViewModel(folder.Id, path, false));
            AddChildren(folders, folder.Id, path, visited);
        }
    }

    private void FolderList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        ConfirmButton.IsEnabled = FolderList.SelectedItem is CloudFolderChoiceViewModel;

    private void FolderList_OnDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (FolderList.SelectedItem is CloudFolderChoiceViewModel)
        {
            Confirm_OnClick(sender, new RoutedEventArgs());
        }
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (FolderList.SelectedItem is CloudFolderChoiceViewModel choice)
        {
            Close(new CloudFolderSelection(choice.FolderId));
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
