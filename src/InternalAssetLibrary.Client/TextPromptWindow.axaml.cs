using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
    }

    public TextPromptWindow(string title, string description, string? initialValue = null, bool isPassword = false)
        : this()
    {
        var localizedTitle = UiLocalization.Text(title);
        Title = localizedTitle;
        PromptTitle.Text = localizedTitle;
        PromptDescription.Text = UiLocalization.Text(description);
        ValueBox.Text = initialValue ?? string.Empty;
        ValueBox.PasswordChar = isPassword ? '*' : '\0';
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(ValueBox.Text);

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        Close(null);

    private void ValueBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Close(ValueBox.Text);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            Close(null);
            eventArgs.Handled = true;
        }
    }
}
