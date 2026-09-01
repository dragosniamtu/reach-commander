using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal interface IMediaTranscodeRunner
{
    Task RunAsync(
        string inputPhysicalPath,
        string outputDirectory,
        Action ready,
        CancellationToken cancellationToken);
}

internal sealed class MediaTranscodeRunner(
    IOptions<MediaPreviewOptions> options,
    ILogger<MediaTranscodeRunner> logger) : IMediaTranscodeRunner
{
    private readonly MediaPreviewOptions _options = options.Value;

    public async Task RunAsync(
        string inputPhysicalPath,
        string outputDirectory,
        Action ready,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.MaximumTranscodeDuration);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(_options.FfmpegPath, inputPhysicalPath, outputDirectory),
        };

        try
        {
            if (!process.Start())
            {
                throw MediaPreviewException.MediaToolsUnavailable();
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw MediaPreviewException.MediaToolsUnavailable();
        }

        using var cancellationRegistration = timeoutSource.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        var stdout = new BoundedProcessOutput(_options.MaximumProcessOutputCharacters);
        var stderr = new BoundedProcessOutput(_options.MaximumProcessOutputCharacters);
        var stdoutTask = MediaProcessExecution.ReadAsync(
            process.StandardOutput,
            stdout,
            timeoutSource.Token);
        var stderrTask = MediaProcessExecution.ReadAsync(
            process.StandardError,
            stderr,
            timeoutSource.Token);
        var exitTask = process.WaitForExitAsync(timeoutSource.Token);
        var announcedReady = false;

        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(
                    exitTask,
                    Task.Delay(TimeSpan.FromMilliseconds(250), timeoutSource.Token));
                EnsureWithinSizeLimit(outputDirectory);
                if (!announcedReady && HasPlayableOutput(outputDirectory))
                {
                    ready();
                    announcedReady = true;
                }
            }

            await exitTask;
            await Task.WhenAll(stdoutTask, stderrTask);
            EnsureWithinSizeLimit(outputDirectory);
            if (process.ExitCode != 0 || !HasPlayableOutput(outputDirectory))
            {
                logger.LogWarning(
                    "Media transcode failed with exit code {ExitCode}; stderr was {TruncatedState}.",
                    process.ExitCode,
                    stderr.WasTruncated ? "truncated" : "bounded");
                throw MediaPreviewException.MediaTranscodeFailed();
            }

            if (!announcedReady)
            {
                ready();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw MediaPreviewException.MediaTranscodeFailed();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        string inputPhysicalPath,
        string outputDirectory)
    {
        var startInfo = MediaProcessExecution.CreateStartInfo(executable);
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "warning",
                     "-i", inputPhysicalPath,
                     "-map", "0:v:0", "-map", "0:a:0?",
                     "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                     "-c:a", "aac", "-b:a", "160k",
                     "-f", "hls", "-hls_time", "4", "-hls_list_size", "0",
                     "-hls_segment_filename", Path.Combine(outputDirectory, "segment-%06d.ts"),
                     Path.Combine(outputDirectory, "index.m3u8"),
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private void EnsureWithinSizeLimit(string outputDirectory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            total = checked(total + new FileInfo(file).Length);
            if (total > _options.MaximumTemporaryOutputBytes)
            {
                throw MediaPreviewException.MediaTranscodeFailed();
            }
        }
    }

    private static bool HasPlayableOutput(string outputDirectory) =>
        File.Exists(Path.Combine(outputDirectory, "index.m3u8")) &&
        Directory.EnumerateFiles(outputDirectory, "segment-*.ts", SearchOption.TopDirectoryOnly)
            .Any();
}
