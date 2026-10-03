using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class AudioCategoryWindow : Window
{
    public AudioCategoryWindow()
    {
        InitializeComponent();
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
    }

    public AudioCategoryWindow(ApiAssetCategory currentCategory)
        : this()
    {
        CategoryPicker.SelectedIndex = currentCategory == ApiAssetCategory.SoundEffect ? 1 : 0;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(CategoryPicker.SelectedIndex == 1
            ? ApiAssetCategory.SoundEffect
            : ApiAssetCategory.Bgm);

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();
}
