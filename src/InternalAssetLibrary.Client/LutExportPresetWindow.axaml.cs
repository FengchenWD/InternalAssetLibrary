using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.MediaAnalysis;

namespace InternalAssetLibrary.Client;

public sealed partial class LutExportPresetWindow : Window
{
    public LutExportPresetWindow()
    {
        InitializeComponent();
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(H264Option.IsChecked == true
            ? LutExportPreset.HighQualityH264
            : LutExportPreset.ProRes422Hq);

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
