using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed record MediaProbeResult(
    string FormatName,
    string VideoCodec,
    string? AudioCodec,
    long? DurationMilliseconds)
{
    public bool CanPlayDirectly =>
        FormatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("mp4", StringComparer.OrdinalIgnoreCase) &&
        VideoCodec.Equals("h264", StringComparison.OrdinalIgnoreCase) &&
        (AudioCodec is null || AudioCodec.Equals("aac", StringComparison.OrdinalIgnoreCase));
}

internal interface IMediaProbeRunner
{
    ValueTask<MediaProbeResult> ProbeAsync(
        string inputPhysicalPath,
        CancellationToken cancellationToken);
}

internal sealed class MediaProbeRunner(
    IOptions<MediaPreviewOptions> options) : IMediaProbeRunner
{
    private readonly MediaPreviewOptions _options = options.Value;

    public async ValueTask<MediaProbeResult> ProbeAsync(
        string inputPhysicalPath,
        CancellationToken cancellationToken)
    {
        var result = await MediaProcessExecution.RunAsync(
            CreateStartInfo(_options.FfprobePath, inputPhysicalPath),
            _options.MaximumProcessOutputCharacters,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (result.ExitCode != 0 || result.StandardOutput.WasTruncated)
        {
            throw MediaPreviewException.MediaProbeFailed();
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput.ToString());
            var root = document.RootElement;
            var formatName = root.GetProperty("format").GetProperty("format_name").GetString();
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw MediaPreviewException.MediaProbeFailed();
            }

            string? videoCodec = null;
            string? audioCodec = null;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                var type = stream.GetProperty("codec_type").GetString();
                var codec = stream.GetProperty("codec_name").GetString();
                if (type == "video" && videoCodec is null)
                {
                    videoCodec = codec;
                }
                else if (type == "audio" && audioCodec is null)
                {
                    audioCodec = codec;
                }
            }

            if (string.IsNullOrWhiteSpace(videoCodec))
            {
                throw MediaPreviewException.VideoInvalid();
            }

            long? durationMilliseconds = null;
            var durationText = root.GetProperty("format").TryGetProperty("duration", out var duration)
                ? duration.GetString()
                : null;
            if (decimal.TryParse(
                    durationText,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var durationSeconds) &&
                durationSeconds >= 0)
            {
                durationMilliseconds = checked((long)decimal.Round(
                    durationSeconds * 1000,
                    0,
                    MidpointRounding.AwayFromZero));
            }

            return new MediaProbeResult(
                formatName,
                videoCodec,
                audioCodec,
                durationMilliseconds);
        }
        catch (MediaPreviewException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or OverflowException)
        {
            throw MediaPreviewException.MediaProbeFailed();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        string inputPhysicalPath)
    {
        var startInfo = MediaProcessExecution.CreateStartInfo(executable);
        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=format_name,duration:stream=codec_type,codec_name",
                     "-of", "json", inputPhysicalPath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

internal sealed class BoundedProcessOutput(int maximumCharacters)
{
    private readonly object _gate = new();
    private readonly char[] _buffer = new char[maximumCharacters > 0
        ? maximumCharacters
        : throw new ArgumentOutOfRangeException(nameof(maximumCharacters))];
    private int _length;

    public bool WasTruncated { get; private set; }

    public void Append(ReadOnlySpan<char> value)
    {
        lock (_gate)
        {
            var available = _buffer.Length - _length;
            var copyLength = Math.Min(available, value.Length);
            value[..copyLength].CopyTo(_buffer.AsSpan(_length));
            _length += copyLength;
            WasTruncated |= copyLength < value.Length;
        }
    }

    public void Append(string value) => Append(value.AsSpan());

    public override string ToString()
    {
        lock (_gate)
        {
            return new string(_buffer, 0, _length);
        }
    }
}

internal sealed record MediaProcessResult(
    int ExitCode,
    BoundedProcessOutput StandardOutput,
    BoundedProcessOutput StandardError);

internal static class MediaProcessExecution
{
    public static ProcessStartInfo CreateStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
    };

    public static async Task<MediaProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        int maximumOutputCharacters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var process = new Process { StartInfo = startInfo };
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

        var standardOutput = new BoundedProcessOutput(maximumOutputCharacters);
        var standardError = new BoundedProcessOutput(maximumOutputCharacters);
        var stdoutTask = ReadAsync(process.StandardOutput, standardOutput, timeoutSource.Token);
        var stderrTask = ReadAsync(process.StandardError, standardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new MediaProcessResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw MediaPreviewException.MediaProbeFailed();
        }
    }

    internal static async Task ReadAsync(
        StreamReader reader,
        BoundedProcessOutput destination,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            destination.Append(buffer.AsSpan(0, read));
        }
    }
}
