using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed record EditorSourceSelection(string Path);

public sealed partial class EditorSourcePickerWindow : Window
{
    public EditorSourcePickerWindow()
    {
        InitializeComponent();
        DataContext = this;
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
    }

    public EditorSourcePickerWindow(IEnumerable<LocalAsset> assets)
        : this()
    {
        foreach (var asset in assets
                     .Where(asset => asset.IsAvailable &&
                                     (asset.MediaType is LocalMediaType.Video or LocalMediaType.Audio) &&
                                     File.Exists(asset.FullPath))
                     .OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase))
        {
            Choices.Add(new EditorSourceChoice(asset.FullPath, asset.FileName, asset.Extension));
        }
    }

    public ObservableCollection<EditorSourceChoice> Choices { get; } = [];

    private async void ChooseComputerFile_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Text("打开视频或音频"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(UiLocalization.Text("视频文件"))
                {
                    Patterns = MediaExtensionClassifier.VideoExtensions.Select(extension => $"*{extension}").ToArray()
                },
                new FilePickerFileType(UiLocalization.Text("音频文件"))
                {
                    Patterns = MediaExtensionClassifier.AudioExtensions.Select(extension => $"*{extension}").ToArray()
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            Close(new EditorSourceSelection(path));
        }
    }

    private void LocalVideoList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        ConfirmButton.IsEnabled = LocalVideoList.SelectedItem is EditorSourceChoice choice && File.Exists(choice.Path);

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (LocalVideoList.SelectedItem is EditorSourceChoice choice && File.Exists(choice.Path))
        {
            Close(new EditorSourceSelection(choice.Path));
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();
}

public sealed record EditorSourceChoice(string Path, string DisplayName, string Extension);
