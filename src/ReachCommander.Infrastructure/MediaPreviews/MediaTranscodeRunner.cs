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
        HardenOutputPermissions(outputDirectory);
        var sessionId = Path.GetFileName(outputDirectory);
        var videoName = Path.GetFileName(inputPhysicalPath);
        var startedTimestamp = Stopwatch.GetTimestamp();
        var nextProgressLog = TimeSpan.FromSeconds(30);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.MaximumTranscodeDuration);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                _options.FfmpegPath,
                inputPhysicalPath,
                outputDirectory,
                _options.MaximumTranscodeThreads,
                _options.TranscodePreset),
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

        var priorityLowered = MediaProcessExecution.TrySetBelowNormalPriority(process);
        logger.LogInformation(
            "FFmpeg process {ProcessId} started for media preview {SessionId}, file {VideoName}, with {MaximumThreads} threads, preset {Preset}, lower priority applied {PriorityLowered}.",
            process.Id,
            sessionId,
            videoName,
            _options.MaximumTranscodeThreads,
            _options.TranscodePreset,
            priorityLowered);

        using var cancellationRegistration = timeoutSource.Token.Register(
            () => MediaProcessExecution.TryKill(process));

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
        var failureLogged = false;

        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(
                    exitTask,
                    Task.Delay(TimeSpan.FromMilliseconds(250), timeoutSource.Token));
                var outputBytes = EnsureWithinSizeLimit(outputDirectory);
                var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
                if (elapsed >= nextProgressLog)
                {
                    logger.LogInformation(
                        "FFmpeg process {ProcessId} is active for media preview {SessionId} after {Elapsed}; temporary output is {OutputBytes} bytes.",
                        process.Id,
                        sessionId,
                        elapsed,
                        outputBytes);
                    nextProgressLog += TimeSpan.FromSeconds(30);
                }
                if (!announcedReady && HasPlayableOutput(outputDirectory))
                {
                    ready();
                    announcedReady = true;
                    logger.LogInformation(
                        "FFmpeg produced playable HLS output for media preview {SessionId} after {Elapsed}.",
                        sessionId,
                        elapsed);
                }
            }

            await exitTask;
            await Task.WhenAll(stdoutTask, stderrTask);
            var finalOutputBytes = EnsureWithinSizeLimit(outputDirectory);
            if (process.ExitCode != 0 || !HasPlayableOutput(outputDirectory))
            {
                failureLogged = true;
                logger.LogWarning(
                    "FFmpeg failed for media preview {SessionId} with exit code {ExitCode}; diagnostic output was {TruncatedState}: {DiagnosticOutput}",
                    sessionId,
                    process.ExitCode,
                    stderr.WasTruncated ? "truncated" : "bounded",
                    SanitizeDiagnostic(stderr.ToString(), inputPhysicalPath, outputDirectory));
                throw MediaPreviewException.MediaTranscodeFailed();
            }

            if (!announcedReady)
            {
                ready();
            }

            logger.LogInformation(
                "FFmpeg completed media preview {SessionId} successfully after {Elapsed}; temporary output is {OutputBytes} bytes.",
                sessionId,
                Stopwatch.GetElapsedTime(startedTimestamp),
                finalOutputBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "FFmpeg was canceled for media preview {SessionId} after {Elapsed}.",
                sessionId,
                Stopwatch.GetElapsedTime(startedTimestamp));
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "FFmpeg timed out for media preview {SessionId} after the configured limit {Timeout}.",
                sessionId,
                _options.MaximumTranscodeDuration);
            throw MediaPreviewException.MediaTranscodeFailed();
        }
        catch (MediaPreviewException exception) when (!failureLogged)
        {
            logger.LogWarning(
                "FFmpeg stopped for media preview {SessionId} after {Elapsed}; failure code {FailureCode}; diagnostic output: {DiagnosticOutput}",
                sessionId,
                Stopwatch.GetElapsedTime(startedTimestamp),
                exception.Code,
                SanitizeDiagnostic(stderr.ToString(), inputPhysicalPath, outputDirectory));
            throw;
        }
        finally
        {
            timeoutSource.Cancel();
            MediaProcessExecution.TryKill(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        string inputPhysicalPath,
        string outputDirectory,
        int maximumThreads,
        string preset)
    {
        var startInfo = MediaProcessExecution.CreateStartInfo(executable);
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "warning",
                     "-threads", maximumThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-i", inputPhysicalPath,
                     "-map", "0:v:0", "-map", "0:a:0?",
                     "-c:v", "libx264", "-preset", preset,
                     "-threads", maximumThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-pix_fmt", "yuv420p",
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

    private long EnsureWithinSizeLimit(string outputDirectory)
    {
        HardenOutputPermissions(outputDirectory);
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            total = checked(total + new FileInfo(file).Length);
            if (total > _options.MaximumTemporaryOutputBytes)
            {
                throw MediaPreviewException.MediaTranscodeFailed();
            }
        }

        return total;
    }

    private static bool HasPlayableOutput(string outputDirectory) =>
        File.Exists(Path.Combine(outputDirectory, "index.m3u8")) &&
        Directory.EnumerateFiles(outputDirectory, "segment-*.ts", SearchOption.TopDirectoryOnly)
            .Any();

    internal static void HardenOutputPermissions(string outputDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            outputDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(
                     outputDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static string SanitizeDiagnostic(
        string diagnostic,
        string inputPhysicalPath,
        string outputDirectory) => diagnostic
        .Replace(inputPhysicalPath, Path.GetFileName(inputPhysicalPath), StringComparison.Ordinal)
        .Replace(outputDirectory, "<preview-output>", StringComparison.Ordinal);
}
