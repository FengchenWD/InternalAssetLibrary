using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

internal sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);

public sealed partial class ChangePasswordWindow : Window
{
    private bool _isBusy;

    public ChangePasswordWindow()
    {
        InitializeComponent();
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
        Opened += (_, _) => CurrentPasswordBox.Focus();
    }

    private void Save_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isBusy)
        {
            return;
        }

        var currentPassword = CurrentPasswordBox.Text ?? string.Empty;
        var newPassword = NewPasswordBox.Text ?? string.Empty;
        if (currentPassword.Length == 0)
        {
            StatusText.Text = UiLocalization.Text("请输入当前密码。");
            return;
        }

        if (!IsAcceptablePassword(newPassword))
        {
            StatusText.Text = UiLocalization.Text(
                "密码必须为 8 至 128 位，且至少包含大写字母、小写字母、数字、特殊符号中的两种。");
            return;
        }

        if (!string.Equals(newPassword, ConfirmPasswordBox.Text, StringComparison.Ordinal))
        {
            StatusText.Text = UiLocalization.Text("两次输入的新密码不一致。");
            return;
        }

        _isBusy = true;
        DialogContent.IsEnabled = false;
        Close(new ChangePasswordInput(currentPassword, newPassword));
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isBusy)
        {
            Close(null);
        }
    }

    private void ConfirmPasswordBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Save_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            Cancel_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
    }

    private static bool IsAcceptablePassword(string password)
    {
        if (password.Length is < 8 or > 128)
        {
            return false;
        }

        var classes = 0;
        classes += password.Any(character => character is >= 'A' and <= 'Z') ? 1 : 0;
        classes += password.Any(character => character is >= 'a' and <= 'z') ? 1 : 0;
        classes += password.Any(character => character is >= '0' and <= '9') ? 1 : 0;
        classes += password.Any(character =>
            !char.IsWhiteSpace(character) &&
            character is not (>= 'A' and <= 'Z') and
            not (>= 'a' and <= 'z') and
            not (>= '0' and <= '9')) ? 1 : 0;
        return classes >= 2;
    }
}
