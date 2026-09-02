using System.Text;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.UnitTests.MediaPreviews;

public sealed class SrtParserTests
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumCues = 20_000;
    private readonly SrtParser _parser = new(MaximumBytes, MaximumCues);

    [Fact]
    public void Parse_reads_multiline_utf8_cues_and_normalizes_newlines()
    {
        var document = _parser.Parse(Utf8(
            "1\n00:00:00,500 --> 00:00:02,000\nHello\nfamily\n\n" +
            "2\n00:00:03,000 --> 00:00:04,250\nWorld\n"));

        Assert.Collection(
            document.Cues,
            cue =>
            {
                Assert.Equal(0, cue.Index);
                Assert.Equal(500, cue.StartMilliseconds);
                Assert.Equal(2_000, cue.EndMilliseconds);
                Assert.Equal("Hello\nfamily", cue.Text);
            },
            cue =>
            {
                Assert.Equal(1, cue.Index);
                Assert.Equal(3_000, cue.StartMilliseconds);
                Assert.Equal(4_250, cue.EndMilliseconds);
                Assert.Equal("World", cue.Text);
            });
    }

    [Fact]
    public void RenderWithOffset_shifts_every_cue_and_clips_at_zero()
    {
        var document = _parser.Parse(Utf8(
            "1\r\n00:00:00,500 --> 00:00:02,000\r\nHello\r\n\r\n" +
            "2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld\r\n"));

        var corrected = document.RenderWithOffset(-750);

        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:01,250\r\nHello\r\n\r\n" +
            "2\r\n00:00:02,250 --> 00:00:03,250\r\nWorld\r\n",
            Encoding.UTF8.GetString(corrected));
    }

    [Fact]
    public void RenderWithOffset_emits_utf8_without_a_byte_order_mark()
    {
        var source = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nBună\r\n"))
            .ToArray();
        var document = _parser.Parse(source);

        var corrected = document.RenderWithOffset(1_400);

        Assert.False(corrected.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal(
            "1\r\n00:00:02,400 --> 00:00:03,400\r\nBună\r\n",
            Encoding.UTF8.GetString(corrected));
    }

    [Fact]
    public void Parse_reads_windows_1250_subtitles_without_a_byte_order_mark()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows1250 = Encoding.GetEncoding(
            1250,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        var source = windows1250.GetBytes(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\nBună, ştii, ţară, mâine.\r\n");

        var document = _parser.Parse(source);

        var cue = Assert.Single(document.Cues);
        Assert.Equal("Bună, ştii, ţară, mâine.", cue.Text);
    }

    [Theory]
    [InlineData("00:61:00,000 --> 00:62:00,000")]
    [InlineData("00:00:01.000 --> 00:00:02.000")]
    [InlineData("00:00:02,000 --> 00:00:01,000")]
    [InlineData("00:00:01,000 -> 00:00:02,000")]
    public void Parse_rejects_invalid_timestamps(string timestampLine)
    {
        var error = Assert.Throws<MediaPreviewException>(() =>
            _parser.Parse(Utf8($"1\r\n{timestampLine}\r\nText\r\n")));

        Assert.Equal("subtitle_invalid", error.Code);
    }

    [Fact]
    public void Parse_rejects_invalid_utf8_without_a_supported_bom()
    {
        var error = Assert.Throws<MediaPreviewException>(() =>
            _parser.Parse(new byte[] { 0x31, 0x0A, 0xFF, 0xFE, 0xFA }));

        Assert.Equal("subtitle_encoding_unsupported", error.Code);
    }

    [Fact]
    public void Parse_rejects_documents_over_the_byte_limit()
    {
        var parser = new SrtParser(maximumBytes: 8, maximumCues: MaximumCues);

        var error = Assert.Throws<MediaPreviewException>(() =>
            parser.Parse(Utf8("123456789")));

        Assert.Equal("subtitle_too_large", error.Code);
    }

    [Fact]
    public void Parse_rejects_documents_over_the_cue_limit()
    {
        var parser = new SrtParser(MaximumBytes, maximumCues: 1);

        var error = Assert.Throws<MediaPreviewException>(() => parser.Parse(Utf8(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\nOne\r\n\r\n" +
            "2\r\n00:00:03,000 --> 00:00:04,000\r\nTwo\r\n")));

        Assert.Equal("subtitle_too_large", error.Code);
    }

    [Fact]
    public void RenderWithOffset_rejects_a_cue_collapsed_before_zero()
    {
        var document = _parser.Parse(Utf8(
            "1\r\n00:00:00,100 --> 00:00:00,200\r\nShort\r\n"));

        var error = Assert.Throws<MediaPreviewException>(() =>
            document.RenderWithOffset(-500));

        Assert.Equal("subtitle_offset_invalid", error.Code);
    }

    [Fact]
    public void RenderWithOffset_rejects_checked_timestamp_overflow()
    {
        var document = _parser.Parse(Utf8(
            "1\r\n99:59:59,999 --> 100:00:00,000\r\nLong\r\n"));

        var error = Assert.Throws<MediaPreviewException>(() =>
            document.RenderWithOffset(long.MaxValue));

        Assert.Equal("subtitle_offset_invalid", error.Code);
    }

    private static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
}
