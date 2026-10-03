using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed partial class CloudDownloadDestinationWindow : Window
{
    private CloudDownloadFolderOption? _selectedFolder;
    private string? _selectedDirectory;

    public CloudDownloadDestinationWindow()
    {
        InitializeComponent();
        DataContext = this;
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
    }

    public CloudDownloadDestinationWindow(IEnumerable<LocalFolderNodeViewModel> roots)
        : this()
    {
        foreach (var root in roots)
        {
            FolderNodes.Add(Clone(root));
        }
    }

    public CloudDownloadDestinationWindow(
        IEnumerable<LocalFolderNodeViewModel> roots,
        bool useDefaultForFuture)
        : this(roots)
    {
        UseDefaultForFutureCheckBox.IsChecked = useDefaultForFuture;
    }

    public ObservableCollection<CloudDownloadFolderOption> FolderNodes { get; } = [];

    private async void ChooseComputerFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Text("选择本次批量下载的保存目录"),
            AllowMultiple = false
        });
        if (folders.Count > 0 && !string.IsNullOrWhiteSpace(folders[0].Path.LocalPath))
        {
            _selectedFolder = null;
            _selectedDirectory = folders[0].Path.LocalPath;
            ConfirmButton.IsEnabled = true;
            StatusText.Text = _selectedDirectory;
        }
    }

    private void FolderTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        _selectedFolder = FolderTree.SelectedItem as CloudDownloadFolderOption;
        _selectedDirectory = _selectedFolder is { IsOffline: false } selected && Directory.Exists(selected.FullPath)
            ? selected.FullPath
            : null;
        ConfirmButton.IsEnabled = _selectedDirectory is not null;
        StatusText.Text = _selectedFolder is null
            ? string.Empty
            : _selectedFolder.IsOffline
                ? UiLocalization.Text("该目录当前不可用，请重新连接存储设备后再试。")
                : _selectedFolder.FullPath;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedDirectory is not null && Directory.Exists(_selectedDirectory))
        {
            Close(new CloudDownloadDestination(
                _selectedDirectory,
                SetAsDefaultCheckBox.IsChecked == true,
                UseDefaultForFutureCheckBox.IsChecked == true));
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private static CloudDownloadFolderOption Clone(LocalFolderNodeViewModel source)
    {
        var result = new CloudDownloadFolderOption(
            source.FullPath,
            source.DisplayName,
            source.IsOffline);
        foreach (var child in source.Children)
        {
            result.Children.Add(Clone(child));
        }

        return result;
    }
}

public sealed record CloudDownloadDestination(
    string DirectoryPath,
    bool SetAsDefault = false,
    bool UseDefaultForFuture = true);

public sealed class CloudDownloadFolderOption(
    string fullPath,
    string displayName,
    bool isOffline)
{
    public string FullPath { get; } = fullPath;

    public string DisplayName { get; } = displayName;

    public bool IsOffline { get; } = isOffline;

    public ObservableCollection<CloudDownloadFolderOption> Children { get; } = [];
}
