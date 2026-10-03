namespace InternalAssetLibrary.Client.Core.Settings;

public enum CloseRequestDecision
{
    ShowPrompt,
    MinimizeToTray,
    ExitApplication
}

public sealed record ClosePromptSelection(
    ApplicationCloseAction Action,
    bool DoNotAskAgain);

public sealed record ClosePromptResolution(
    CloseRequestDecision Decision,
    ClientSettings Settings,
    bool ShouldSaveSettings);

public static class ClientCloseBehavior
{
    public static CloseRequestDecision Decide(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AskOnClose
            ? CloseRequestDecision.ShowPrompt
            : ToDecision(settings.CloseAction);
    }

    public static ClosePromptResolution ResolvePrompt(
        ClientSettings settings,
        ClosePromptSelection selection)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Action))
        {
            throw new ArgumentOutOfRangeException(nameof(selection), selection.Action, null);
        }

        var updatedSettings = selection.DoNotAskAgain
            ? settings with
            {
                CloseAction = selection.Action,
                AskOnClose = false
            }
            : settings;

        return new ClosePromptResolution(
            ToDecision(selection.Action),
            updatedSettings,
            selection.DoNotAskAgain);
    }

    private static CloseRequestDecision ToDecision(ApplicationCloseAction action) => action switch
    {
        ApplicationCloseAction.MinimizeToTray => CloseRequestDecision.MinimizeToTray,
        ApplicationCloseAction.ExitApplication => CloseRequestDecision.ExitApplication,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };
}
