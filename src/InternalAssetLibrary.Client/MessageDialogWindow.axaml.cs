using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class MessageDialogWindow : Window
{
    private bool _returnsConfirmation;

    public MessageDialogWindow()
    {
        InitializeComponent();
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
        KeyDown += OnKeyDown;
    }

    public MessageDialogWindow(string title, string message)
        : this()
    {
        var localizedTitle = UiLocalization.Text(title);
        Title = localizedTitle;
        DialogTitle.Text = localizedTitle;
        DialogMessage.Text = UiLocalization.Text(message);
        Opened += (_, _) => ConfirmButton.Focus();
    }

    public MessageDialogWindow(
        string title,
        string message,
        string confirmLabel,
        string cancelLabel)
        : this(title, message)
    {
        _returnsConfirmation = true;
        ConfirmButton.Content = UiLocalization.Text(confirmLabel);
        CancelButton.Content = UiLocalization.Text(cancelLabel);
        CancelButton.IsVisible = true;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_returnsConfirmation)
        {
            Close(true);
        }
        else
        {
            Close();
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Confirm_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            if (_returnsConfirmation)
            {
                Close(false);
            }
            else
            {
                Close();
            }

            eventArgs.Handled = true;
        }
    }
}
