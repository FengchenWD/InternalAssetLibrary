using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace InternalAssetLibrary.Client.Core.Platform;

public sealed record FileOpenActivationRequest
{
    public FileOpenActivationRequest(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        FilePaths = filePaths.ToArray();
    }

    public IReadOnlyList<string> FilePaths { get; }

    public bool HasFiles => FilePaths.Count > 0;
}

public static class FileOpenCommandLine
{
    public const string OpenOption = "--open";
    public const int MaximumFileCount = 256;

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out FileOpenActivationRequest request,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var paths = new List<string>();
        var acceptingPaths = false;

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, OpenOption, StringComparison.OrdinalIgnoreCase))
            {
                acceptingPaths = true;
                continue;
            }

            if (!acceptingPaths || argument.StartsWith("--", StringComparison.Ordinal))
            {
                request = new FileOpenActivationRequest([]);
                error = $"不支持的启动参数：{argument}";
                return false;
            }

            if (paths.Count >= MaximumFileCount)
            {
                request = new FileOpenActivationRequest([]);
                error = $"一次最多打开 {MaximumFileCount} 个文件。";
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(argument);
                if (!DefaultPlayerFileAssociations.SupportsPath(fullPath))
                {
                    request = new FileOpenActivationRequest([]);
                    error = $"不支持的媒体格式：{Path.GetExtension(fullPath)}";
                    return false;
                }

                if (!paths.Contains(fullPath, PathComparer))
                {
                    paths.Add(fullPath);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                request = new FileOpenActivationRequest([]);
                error = $"无效的文件路径：{argument}";
                return false;
            }
        }

        if (acceptingPaths && paths.Count == 0)
        {
            request = new FileOpenActivationRequest([]);
            error = $"{OpenOption} 后至少需要一个文件路径。";
            return false;
        }

        request = new FileOpenActivationRequest(paths);
        error = null;
        return true;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed class FileOpenActivationBroker
{
    private readonly ConcurrentQueue<FileOpenActivationRequest> _pending = new();

    public event Action? ActivationAvailable;

    public void Publish(FileOpenActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _pending.Enqueue(request);
        ActivationAvailable?.Invoke();
    }

    public bool TryTake([NotNullWhen(true)] out FileOpenActivationRequest? request) =>
        _pending.TryDequeue(out request);

    public IReadOnlyList<FileOpenActivationRequest> Drain()
    {
        var requests = new List<FileOpenActivationRequest>();
        while (_pending.TryDequeue(out var request))
        {
            requests.Add(request);
        }

        return requests;
    }
}
