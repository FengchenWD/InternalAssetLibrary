using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.MediaAnalysis;

namespace InternalAssetLibrary.Client;

public sealed partial class AudioExportPresetWindow : Window
{
    public AudioExportPresetWindow()
    {
        InitializeComponent();
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(WavOption.IsChecked == true
            ? AudioExportPreset.Wav
            : AudioExportPreset.Flac);

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
