using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class CloseActionPromptWindow : Window
{
    public CloseActionPromptWindow()
        : this(ApplicationCloseAction.MinimizeToTray)
    {
    }

    public CloseActionPromptWindow(ApplicationCloseAction selectedAction)
    {
        if (!Enum.IsDefined(selectedAction))
        {
            throw new ArgumentOutOfRangeException(nameof(selectedAction));
        }

        InitializeComponent();
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
        MinimizeToTrayOption.IsChecked = selectedAction == ApplicationCloseAction.MinimizeToTray;
        ExitApplicationOption.IsChecked = selectedAction == ApplicationCloseAction.ExitApplication;
        Opened += (_, _) => ConfirmButton.Focus();
        KeyDown += OnKeyDown;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var action = ExitApplicationOption.IsChecked == true
            ? ApplicationCloseAction.ExitApplication
            : ApplicationCloseAction.MinimizeToTray;
        Close(new ClosePromptSelection(action, DoNotAskAgainCheckBox.IsChecked == true));
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void Chrome_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Confirm_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            Close(null);
            eventArgs.Handled = true;
        }
    }
}
