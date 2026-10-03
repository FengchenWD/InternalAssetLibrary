using System.Diagnostics;

namespace InternalAssetLibrary.Client.Core.MediaAnalysis;

public sealed record MediaToolAvailability(
    bool IsAvailable,
    string RequestedExecutable,
    string? ResolvedExecutable,
    string? Diagnostic);

public sealed record MediaToolCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public MediaToolCommand Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
        ArgumentNullException.ThrowIfNull(Arguments);
        if (Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("Media command arguments cannot contain null values.", nameof(Arguments));
        }

        return this;
    }
}

public sealed record MediaToolExecutionResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IMediaToolRunner
{
    Task<MediaToolExecutionResult> RunAsync(
        MediaToolCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessMediaToolRunner : IMediaToolRunner
{
    public async Task<MediaToolExecutionResult> RunAsync(
        MediaToolCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = command.Validate();
        var startInfo = new ProcessStartInfo
        {
            FileName = normalized.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in normalized.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start media tool '{normalized.Executable}'.");
        }

        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new MediaToolExecutionResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }
}

public static class MediaToolLocator
{
    public static MediaToolAvailability Locate(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var requested = executable.Trim();
        if (Path.IsPathFullyQualified(requested) ||
            requested.Contains(Path.DirectorySeparatorChar) ||
            requested.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(requested);
            return File.Exists(fullPath)
                ? new MediaToolAvailability(true, requested, fullPath, null)
                : new MediaToolAvailability(false, requested, null, $"Media tool was not found at '{fullPath}'.");
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        var hasExtension = Path.HasExtension(requested);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in hasExtension ? [string.Empty] : extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), requested + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return new MediaToolAvailability(true, requested, Path.GetFullPath(candidate), null);
                }
            }
        }

        return new MediaToolAvailability(false, requested, null, $"Media tool '{requested}' was not found on PATH.");
    }
}
