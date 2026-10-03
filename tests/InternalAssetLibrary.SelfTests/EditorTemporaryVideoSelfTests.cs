using InternalAssetLibrary.Client.Core.Editor;
using InternalAssetLibrary.Client.Core.MediaAnalysis;

internal static class EditorTemporaryVideoSelfTests
{
    public static void ExportCommandKeepsSingleVideoWorkflow()
    {
        var command = EditorTemporaryVideoService.BuildExportCommand(
            @"C:\input.mp4",
            @"C:\output.mp4",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(8),
            -3,
            "ffmpeg");

        var args = string.Join(' ', command.Arguments);
        if (!args.Contains("-map 0:v:0", StringComparison.Ordinal) ||
            !args.Contains("-map 0:a?", StringComparison.Ordinal) ||
            !args.Contains("-t 6", StringComparison.Ordinal) ||
            !args.Contains("volume=-3dB", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The temporary editor export command is missing trim or volume processing.");
        }

        if (args.Contains("filter_complex", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("lut3d", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("concat", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The temporary editor must not build a multi-track or LUT render graph.");
        }
    }

    public static void AudioExportCommandKeepsTrimAndGain()
    {
        var command = EditorTemporaryVideoService.BuildAudioExportCommand(
            @"C:\input.flac",
            @"C:\output.wav",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4),
            -6,
            AudioExportPreset.Wav,
            "ffmpeg");

        var args = string.Join(' ', command.Arguments);
        if (!args.Contains("-map 0:a:0", StringComparison.Ordinal) ||
            !args.Contains("-t 3", StringComparison.Ordinal) ||
            !args.Contains("volume=-6dB", StringComparison.Ordinal) ||
            !args.Contains("pcm_s24le", StringComparison.Ordinal) ||
            args.Contains("-map 0:v:0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The temporary editor audio export command is missing audio trim or gain processing.");
        }

        var mp3 = EditorTemporaryVideoService.BuildAudioExportCommand(
            @"C:\input.flac",
            @"C:\output.mp3",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            0,
            AudioExportPreset.Mp3,
            256,
            "ffmpeg");
        var mp3Args = string.Join(' ', mp3.Arguments);
        if (!mp3Args.Contains("libmp3lame", StringComparison.Ordinal) ||
            !mp3Args.Contains("256k", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The temporary editor audio export must preserve the selected MP3 bitrate.");
        }
    }

    public static void ExportOptionsIgnoreXamlSelectionEventsDuringInitialization()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "EditorExportOptionsWindow.axaml.cs"));

        Contains("_isInitializing = true;", source);
        Contains("if (_isInitializing || UseSourceSettingsBox is null || RatioBox is null || WidthBox is null || HeightBox is null)", source);
        Contains("private void UseSourceSettings_OnChanged", source);
        Contains("if (_isInitializing)", source);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void Contains(string expected, string source)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }
}
