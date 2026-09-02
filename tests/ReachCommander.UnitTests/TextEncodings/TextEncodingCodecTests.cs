using System.Text;
using ReachCommander.Application.TextEncodings;
using ReachCommander.Infrastructure.TextEncodings;

namespace ReachCommander.UnitTests.TextEncodings;

public sealed class TextEncodingCodecTests
{
    [Theory]
    [InlineData(TextEncodingKind.Utf8Bom)]
    [InlineData(TextEncodingKind.Utf16LittleEndian)]
    [InlineData(TextEncodingKind.Utf16BigEndian)]
    public void Analyze_detects_unicode_byte_order_marks(TextEncodingKind kind)
    {
        var encoding = EncodingFor(kind);
        var source = encoding.GetPreamble().Concat(encoding.GetBytes("Bună\r\n")).ToArray();

        var analysis = TextEncodingCodec.Analyze(
            source,
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8);

        Assert.Equal(kind, analysis.SourceEncoding);
        Assert.Equal(TextEncodingConfidence.High, analysis.Confidence);
        Assert.False(analysis.RequiresReview);
        Assert.Equal("Bună\r\n", analysis.Text);
    }

    [Fact]
    public void Analyze_detects_bomless_strict_utf8()
    {
        var source = new UTF8Encoding(false, true).GetBytes("Bună 😀\n");

        var analysis = TextEncodingCodec.Analyze(
            source,
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8);

        Assert.Equal(TextEncodingKind.Utf8, analysis.SourceEncoding);
        Assert.Equal(TextEncodingConfidence.High, analysis.Confidence);
        Assert.Equal("Bună 😀\n", analysis.Text);
    }

    [Fact]
    public void Analyze_marks_ambiguous_legacy_romanian_as_low_confidence_windows_1250()
    {
        var source = Windows(1250).GetBytes("Bună, ştii, ţară, mâine.\r\n");

        var analysis = TextEncodingCodec.Analyze(
            source,
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8);

        Assert.Equal(TextEncodingKind.Windows1250, analysis.SourceEncoding);
        Assert.Equal(TextEncodingConfidence.Low, analysis.Confidence);
        Assert.True(analysis.RequiresReview);
        Assert.Equal("Bună, ştii, ţară, mâine.\r\n", analysis.Text);
        Assert.Equal(source, analysis.OriginalBytes);
    }

    [Fact]
    public void Analyze_honors_manual_windows_1252_for_smart_punctuation()
    {
        var source = Windows(1252).GetBytes("“quoted”—price €10\r\n");

        var analysis = TextEncodingCodec.Analyze(
            source,
            TextEncodingKind.Windows1252,
            TextEncodingKind.Utf8);

        Assert.Equal(TextEncodingKind.Windows1252, analysis.SourceEncoding);
        Assert.Equal(TextEncodingConfidence.High, analysis.Confidence);
        Assert.False(analysis.RequiresReview);
        Assert.Equal("“quoted”—price €10\r\n", analysis.Text);
    }

    [Fact]
    public void Analyze_rejects_nul_content_as_binary()
    {
        var error = Assert.Throws<TextEncodingException>(() => TextEncodingCodec.Analyze(
            [0x61, 0x00, 0x62],
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8));

        Assert.Equal("text_binary_content", error.Code);
    }

    [Fact]
    public void Analyze_rejects_excessive_control_characters_as_binary()
    {
        var source = Encoding.UTF8.GetBytes("text\u0001\u0002\u0003\u0004\u0005");

        var error = Assert.Throws<TextEncodingException>(() => TextEncodingCodec.Analyze(
            source,
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8));

        Assert.Equal("text_binary_content", error.Code);
    }

    [Fact]
    public void Analyze_rejects_output_that_would_replace_an_emoji()
    {
        var error = Assert.Throws<TextEncodingException>(() => TextEncodingCodec.Analyze(
            Encoding.UTF8.GetBytes("Ready 😀"),
            TextEncodingKind.Auto,
            TextEncodingKind.Windows1250));

        Assert.Equal("text_output_unrepresentable", error.Code);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8, false)]
    [InlineData(TextEncodingKind.Utf8Bom, true)]
    [InlineData(TextEncodingKind.Utf16LittleEndian, true)]
    [InlineData(TextEncodingKind.Windows1250, false)]
    [InlineData(TextEncodingKind.Windows1252, false)]
    public void Encode_emits_only_the_requested_byte_order_mark(
        TextEncodingKind output,
        bool expectsPreamble)
    {
        const string text = "Café";
        var bytes = TextEncodingCodec.Encode(text, output);
        var preamble = EncodingFor(output).GetPreamble();

        Assert.Equal(expectsPreamble, preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble));
        Assert.Equal(text, DecodeOutput(bytes, output));
    }

    [Fact]
    public void Analyze_bounds_preview_to_4096_utf8_bytes_without_splitting_scalars()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 2_000));

        var analysis = TextEncodingCodec.Analyze(
            Encoding.UTF8.GetBytes(text),
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8);

        Assert.True(Encoding.UTF8.GetByteCount(analysis.PreviewText) <= 4_096);
        Assert.DoesNotContain('\uFFFD', analysis.PreviewText);
    }

    private static Encoding Windows(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static Encoding EncodingFor(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => new UTF8Encoding(false, true),
        TextEncodingKind.Utf8Bom => new UTF8Encoding(true, true),
        TextEncodingKind.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
        TextEncodingKind.Utf16BigEndian => new UnicodeEncoding(true, true, true),
        TextEncodingKind.Windows1250 => Windows(1250),
        TextEncodingKind.Windows1252 => Windows(1252),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string DecodeOutput(byte[] bytes, TextEncodingKind kind)
    {
        var encoding = EncodingFor(kind);
        var preamble = encoding.GetPreamble();
        return encoding.GetString(
            bytes.AsSpan().StartsWith(preamble) ? bytes.AsSpan(preamble.Length) : bytes);
    }
}
