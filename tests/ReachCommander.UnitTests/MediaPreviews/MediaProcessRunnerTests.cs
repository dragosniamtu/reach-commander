using System.Diagnostics;
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

        var startInfo = MediaTranscodeRunner.CreateStartInfo("ffmpeg", input, output);

        AssertHardened(startInfo);
        Assert.Equal(
            new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "warning",
                "-i", input,
                "-map", "0:v:0", "-map", "0:a:0?",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "160k",
                "-f", "hls", "-hls_time", "4", "-hls_list_size", "0",
                "-hls_segment_filename", Path.Combine(output, "segment-%06d.ts"),
                Path.Combine(output, "index.m3u8"),
            },
            startInfo.ArgumentList);
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
}
