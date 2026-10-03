using Avalonia.Input;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public enum PlaybackShortcutAction
{
    PlayPause,
    SeekBackwardFiveSeconds,
    SeekForwardFiveSeconds,
    SeekBackwardOneSecond,
    SeekForwardOneSecond,
    SetInPoint,
    SetOutPoint,
    AddMarker,
    OpenFiles,
    OpenFolder,
    Export,
    VolumeUp,
    VolumeDown,
    ToggleFullscreen,
    ExitFullscreen
}

internal sealed record PlaybackShortcutDefinition(
    PlaybackShortcutAction Action,
    string ChineseName,
    Func<PlaybackShortcutSettings, string> GetGesture,
    Func<PlaybackShortcutSettings, string, PlaybackShortcutSettings> SetGesture);

internal static class PlaybackShortcutCatalog
{
    public static IReadOnlyList<PlaybackShortcutDefinition> Definitions { get; } =
    [
        new(PlaybackShortcutAction.PlayPause, "播放/暂停", value => value.PlayPause,
            (value, gesture) => value with { PlayPause = gesture }),
        new(PlaybackShortcutAction.SeekBackwardFiveSeconds, "后退 5 秒", value => value.SeekBackwardFiveSeconds,
            (value, gesture) => value with { SeekBackwardFiveSeconds = gesture }),
        new(PlaybackShortcutAction.SeekForwardFiveSeconds, "前进 5 秒", value => value.SeekForwardFiveSeconds,
            (value, gesture) => value with { SeekForwardFiveSeconds = gesture }),
        new(PlaybackShortcutAction.SeekBackwardOneSecond, "后退 1 秒", value => value.SeekBackwardOneSecond,
            (value, gesture) => value with { SeekBackwardOneSecond = gesture }),
        new(PlaybackShortcutAction.SeekForwardOneSecond, "前进 1 秒", value => value.SeekForwardOneSecond,
            (value, gesture) => value with { SeekForwardOneSecond = gesture }),
        new(PlaybackShortcutAction.SetInPoint, "设置入点", value => value.SetInPoint,
            (value, gesture) => value with { SetInPoint = gesture }),
        new(PlaybackShortcutAction.SetOutPoint, "设置出点", value => value.SetOutPoint,
            (value, gesture) => value with { SetOutPoint = gesture }),
        new(PlaybackShortcutAction.AddMarker, "在当前位置标记", value => value.AddMarker,
            (value, gesture) => value with { AddMarker = gesture }),
        new(PlaybackShortcutAction.OpenFiles, "打开媒体文件", value => value.OpenFiles,
            (value, gesture) => value with { OpenFiles = gesture }),
        new(PlaybackShortcutAction.OpenFolder, "打开媒体文件夹", value => value.OpenFolder,
            (value, gesture) => value with { OpenFolder = gesture }),
        new(PlaybackShortcutAction.Export, "导出当前媒体", value => value.Export,
            (value, gesture) => value with { Export = gesture }),
        new(PlaybackShortcutAction.VolumeUp, "提高总音量", value => value.VolumeUp,
            (value, gesture) => value with { VolumeUp = gesture }),
        new(PlaybackShortcutAction.VolumeDown, "降低总音量", value => value.VolumeDown,
            (value, gesture) => value with { VolumeDown = gesture }),
        new(PlaybackShortcutAction.ToggleFullscreen, "切换全屏", value => value.ToggleFullscreen,
            (value, gesture) => value with { ToggleFullscreen = gesture }),
        new(PlaybackShortcutAction.ExitFullscreen, "退出全屏", value => value.ExitFullscreen,
            (value, gesture) => value with { ExitFullscreen = gesture })
    ];

    public static bool TryMatch(
        PlaybackShortcutSettings settings,
        KeyEventArgs eventArgs,
        out PlaybackShortcutAction action)
    {
        foreach (var definition in Definitions)
        {
            if (PlaybackShortcutGesture.TryParse(definition.GetGesture(settings), out var gesture) &&
                gesture.Matches(eventArgs))
            {
                action = definition.Action;
                return true;
            }
        }

        action = default;
        return false;
    }

    public static PlaybackShortcutDefinition Get(PlaybackShortcutAction action) =>
        Definitions.Single(definition => definition.Action == action);
}

internal readonly record struct PlaybackShortcutGesture(Key Key, KeyModifiers Modifiers)
{
    private const KeyModifiers SupportedModifiers =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    public bool Matches(KeyEventArgs eventArgs) =>
        eventArgs.Key == Key && eventArgs.KeyModifiers == Modifiers;

    public string Serialize()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }

    public string ToDisplayText()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(Key switch
        {
            Key.Space => UiLocalization.Text("空格"),
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Escape => "Esc",
            Key.Return => "Enter",
            _ => Key.ToString()
        });
        return string.Join(" + ", parts);
    }

    public static bool TryCreate(KeyEventArgs eventArgs, out PlaybackShortcutGesture gesture)
    {
        var modifiers = eventArgs.KeyModifiers & SupportedModifiers;
        var keyName = eventArgs.Key.ToString();
        if (eventArgs.Key == Key.None ||
            keyName.EndsWith("Ctrl", StringComparison.Ordinal) ||
            keyName.EndsWith("Shift", StringComparison.Ordinal) ||
            keyName.EndsWith("Alt", StringComparison.Ordinal) ||
            keyName.EndsWith("Win", StringComparison.Ordinal))
        {
            gesture = default;
            return false;
        }

        gesture = new PlaybackShortcutGesture(eventArgs.Key, modifiers);
        return true;
    }

    public static bool TryParse(string value, out PlaybackShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None)
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "SHIFT" => KeyModifiers.Shift,
                "ALT" => KeyModifiers.Alt,
                _ => (KeyModifiers)(-1)
            };
            if ((int)modifiers < 0)
            {
                return false;
            }
        }

        gesture = new PlaybackShortcutGesture(key, modifiers);
        return true;
    }
}
