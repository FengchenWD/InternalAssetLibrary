using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InternalAssetLibrary.Client;

public sealed partial class AdminPasswordResetWindow : Window
{
    public AdminPasswordResetWindow() => InitializeComponent();

    private void Save_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var password = PasswordBox.Text ?? string.Empty;
        if (password.Length is < 8 or > 128)
        {
            ValidationText.Text = "密码长度应为 8 至 128 位。";
            return;
        }

        Close(password);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
