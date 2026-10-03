using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed record LocalBatchRenameResult(IReadOnlyList<string> FileNames);

public sealed partial class LocalBatchRenameWindow : Window
{
    private const int MaximumPreviewItems = 6;
    private string[] _originalFileNames = [];
    private LocalBatchRenamePlan? _currentPlan;
    private bool _isReady;

    public LocalBatchRenameWindow()
    {
        InitializeComponent();
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
    }

    public LocalBatchRenameWindow(IReadOnlyList<string> originalFileNames)
        : this()
    {
        ArgumentNullException.ThrowIfNull(originalFileNames);
        _originalFileNames = originalFileNames.ToArray();
        _isReady = true;
        UiLocalization.SetText(
            SelectionSummaryText,
            "已选择 {0:N0} 个素材。",
            _originalFileNames.Length);
        RefreshPreview();
        Opened += (_, _) => PrefixBox.Focus();
    }

    private void NamePart_OnTextChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (_isReady)
        {
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        try
        {
            _currentPlan = LocalBatchRenamePlanner.Create(
                _originalFileNames,
                PrefixBox.Text,
                SuffixBox.Text);
            PreviewItems.ItemsSource = _currentPlan.Items.Take(MaximumPreviewItems).ToArray();
            var hiddenCount = _currentPlan.Items.Count - MaximumPreviewItems;
            PreviewOverflowText.IsVisible = hiddenCount > 0;
            if (hiddenCount > 0)
            {
                UiLocalization.SetText(
                    PreviewOverflowText,
                    "另有 {0:N0} 个结果未显示。",
                    hiddenCount);
            }

            StatusText.Text = string.Empty;
            ConfirmButton.IsEnabled = true;
        }
        catch (ArgumentException exception)
        {
            _currentPlan = null;
            PreviewItems.ItemsSource = null;
            PreviewOverflowText.IsVisible = false;
            StatusText.Text = UiLocalization.Text(exception.Message);
            ConfirmButton.IsEnabled = false;
        }
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        RefreshPreview();
        if (_currentPlan is null)
        {
            return;
        }

        DialogContent.IsEnabled = false;
        Close(new LocalBatchRenameResult(
            _currentPlan.Items.Select(item => item.NewFileName).ToArray()));
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void Window_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Confirm_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            Cancel_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
    }
}
