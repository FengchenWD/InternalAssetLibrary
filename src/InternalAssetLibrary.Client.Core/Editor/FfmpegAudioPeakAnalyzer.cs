using System.Buffers.Binary;
using System.Diagnostics;
using InternalAssetLibrary.Client.Core.MediaAnalysis;

namespace InternalAssetLibrary.Client.Core.Editor;

public sealed record EditorAudioPeakEnvelope(
    double IntervalSeconds,
    float[] Left,
    float[] Right)
{
    public (double LeftDb, double RightDb) Read(double sourceTimeSeconds, double gainDb = 0)
    {
        if (Left.Length == 0 || Right.Length == 0 || sourceTimeSeconds < 0)
        {
            return (-60, -60);
        }

        var index = Math.Clamp((int)(sourceTimeSeconds / IntervalSeconds), 0, Left.Length - 1);
        var gain = Math.Pow(10, Math.Clamp(gainDb, -120, 12) / 20);
        return (AmplitudeToDb(Left[index] * gain), AmplitudeToDb(Right[index] * gain));
    }

    private static double AmplitudeToDb(double value) => value <= 0.000001
        ? -60
        : Math.Clamp(20 * Math.Log10(value), -60, 6);
}

public sealed class FfmpegAudioPeakAnalyzer(string ffmpegExecutable)
{
    private const int SampleRate = 1000;
    private const int FramesPerBucket = 100;
    private readonly string _ffmpegExecutable = ResolveFfmpeg(ffmpegExecutable);

    public async Task<EditorAudioPeakEnvelope> AnalyzeAsync(
        string sourcePath,
        int streamIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The audio source does not exist.", fullPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-i", fullPath, "-map", $"0:{streamIndex}",
                     "-vn", "-sn", "-dn", "-ac", "2", "-ar", SampleRate.ToString(),
                     "-f", "f32le", "pipe:1"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start FFmpeg for the editor audio meter.");
        }

        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var left = new List<float>();
            var right = new List<float>();
            var buffer = new byte[32 * 1024];
            var pending = new byte[buffer.Length + 8];
            var pendingCount = 0;
            var bucketFrames = 0;
            var leftPeak = 0f;
            var rightPeak = 0f;
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                Buffer.BlockCopy(buffer, 0, pending, pendingCount, read);
                pendingCount += read;
                var completeBytes = pendingCount - pendingCount % 8;
                for (var offset = 0; offset < completeBytes; offset += 8)
                {
                    var leftValue = Math.Abs(BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(pending.AsSpan(offset, 4))));
                    var rightValue = Math.Abs(BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(pending.AsSpan(offset + 4, 4))));
                    if (float.IsFinite(leftValue))
                    {
                        leftPeak = Math.Max(leftPeak, leftValue);
                    }

                    if (float.IsFinite(rightValue))
                    {
                        rightPeak = Math.Max(rightPeak, rightValue);
                    }

                    bucketFrames++;
                    if (bucketFrames < FramesPerBucket)
                    {
                        continue;
                    }

                    left.Add(leftPeak);
                    right.Add(rightPeak);
                    bucketFrames = 0;
                    leftPeak = 0;
                    rightPeak = 0;
                }

                pendingCount -= completeBytes;
                if (pendingCount > 0)
                {
                    Buffer.BlockCopy(pending, completeBytes, pending, 0, pendingCount);
                }
            }

            if (bucketFrames > 0)
            {
                left.Add(leftPeak);
                right.Add(rightPeak);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"FFmpeg exited with code {process.ExitCode}."
                    : error.Trim());
            }

            return new EditorAudioPeakEnvelope(
                FramesPerBucket / (double)SampleRate,
                left.ToArray(),
                right.ToArray());
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

    private static string ResolveFfmpeg(string executable)
    {
        var availability = MediaToolLocator.Locate(executable);
        return availability.ResolvedExecutable
            ?? throw new FileNotFoundException(availability.Diagnostic ?? "FFmpeg was not found.");
    }
}
