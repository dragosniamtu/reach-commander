using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.UnitTests.MediaPreviews;

public sealed class MediaProcessRunnerTests
{
    [Fact]
    public void Probe_start_info_uses_a_shell_free_bounded_process_boundary()
    {
        var input = Path.Combine(Path.GetTempPath(), "Family Movie.mp4");

        var startInfo = MediaProbeRunner.CreateStartInfo("ffprobe", input);

        AssertHardened(startInfo);
        Assert.Equal(
            new[]
            {
                "-v", "error",
                "-show_entries", "format=format_name,duration:stream=codec_type,codec_name",
                "-of", "json", input,
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void Transcode_start_info_adds_every_argument_without_shell_interpolation()
    {
        var input = Path.Combine(Path.GetTempPath(), "Family Movie.mkv");
        var output = Path.Combine(Path.GetTempPath(), "preview with spaces");

        var startInfo = MediaTranscodeRunner.CreateStartInfo(
            "ffmpeg",
            input,
            output,
            maximumThreads: 2,
            preset: "ultrafast");

        AssertHardened(startInfo);
        Assert.Equal(
            new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "warning",
                "-threads", "2", "-i", input,
                "-map", "0:v:0", "-map", "0:a:0?",
                "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "160k",
                "-f", "hls", "-hls_time", "4", "-hls_list_size", "0",
                "-hls_segment_filename", Path.Combine(output, "segment-%06d.ts"),
                Path.Combine(output, "index.m3u8"),
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task Running_child_can_be_lowered_to_below_normal_priority()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
        }

        using var process = Process.Start(startInfo)!;
        try
        {
            Assert.True(MediaProcessExecution.TrySetBelowNormalPriority(process));
            Assert.False(process.HasExited);
        }
        finally
        {
            MediaProcessExecution.TryKill(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Transcode_start_log_records_the_bounded_resource_profile_without_physical_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reachcommander-runner-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "private", "Family Movie.mkv");
        var output = Path.Combine(root, "session-safe-id");
        var logger = new RecordingLogger<MediaTranscodeRunner>();
        var runner = new MediaTranscodeRunner(
            Options.Create(new MediaPreviewOptions
            {
                FfmpegPath = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
                MaximumTranscodeThreads = 2,
                TranscodePreset = "ultrafast",
                MaximumTranscodeDuration = TimeSpan.FromSeconds(5),
            }),
            logger);

        try
        {
            await Assert.ThrowsAsync<ReachCommander.Application.MediaPreviews.MediaPreviewException>(
                () => runner.RunAsync(input, output, () => { }, CancellationToken.None));

            var started = Assert.Single(
                logger.Messages,
                message => message.Contains("started for media preview", StringComparison.Ordinal));
            Assert.Contains("with 2 threads, preset ultrafast", started, StringComparison.Ordinal);
            Assert.Contains("lower priority applied", started, StringComparison.Ordinal);
            Assert.Contains("Family Movie.mkv", started, StringComparison.Ordinal);
            Assert.DoesNotContain(root, started, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Bounded_output_keeps_at_most_the_configured_number_of_characters()
    {
        var output = new BoundedProcessOutput(8);

        output.Append("12345");
        output.Append("67890");

        Assert.Equal("12345678", output.ToString());
        Assert.True(output.WasTruncated);
    }

    [Fact]
    public void Hls_output_permissions_are_private_on_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var output = Path.Combine(
            Path.GetTempPath(),
            $"reachcommander-permissions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var segment = Path.Combine(output, "segment-000000.ts");
        File.WriteAllText(segment, "fixture");
        try
        {
            MediaTranscodeRunner.HardenOutputPermissions(output);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(output));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(segment));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Process_cleanup_terminates_a_running_child()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
        }

        using var process = Process.Start(startInfo)!;
        Assert.False(process.HasExited);

        MediaProcessExecution.TryKill(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
    }

    [Fact]
    public void Transcode_diagnostics_redact_physical_paths()
    {
        var input = Path.Combine(Path.GetTempPath(), "private", "Family Movie.mkv");
        var output = Path.Combine(Path.GetTempPath(), "data", "media-previews", "session");

        var diagnostic = MediaTranscodeRunner.SanitizeDiagnostic(
            $"Could not read {input}; output {output} failed.",
            input,
            output);

        Assert.DoesNotContain(Path.GetDirectoryName(input)!, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(output, diagnostic, StringComparison.Ordinal);
        Assert.Contains("Family Movie.mkv", diagnostic, StringComparison.Ordinal);
        Assert.Contains("<preview-output>", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "h264", null, true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac", true)]
    [InlineData("matroska,webm", "h264", "aac", false)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "hevc", "aac", false)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "h264", "ac3", false)]
    public void Direct_play_classifier_requires_MP4_H264_and_optional_AAC(
        string format,
        string videoCodec,
        string? audioCodec,
        bool expected)
    {
        var probe = new MediaProbeResult(format, videoCodec, audioCodec, 12_500);

        Assert.Equal(expected, probe.CanPlayDirectly);
    }

    private static void AssertHardened(ProcessStartInfo startInfo)
    {
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
