using System.Text;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed record TextEncodingAnalysis(
    TextEncodingKind SourceEncoding,
    TextEncodingConfidence Confidence,
    bool RequiresReview,
    string Text,
    byte[] OriginalBytes,
    string PreviewText);

internal static class TextEncodingCodec
{
    private const int PreviewByteLimit = 4_096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UTF8Encoding Utf8WithBom = new(true, true);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(false, true, true);
    private static readonly UnicodeEncoding Utf16BigEndian = new(true, true, true);
    private static readonly Encoding Windows1250;
    private static readonly Encoding Windows1252;

    static TextEncodingCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1250 = Encoding.GetEncoding(
            1250,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        Windows1252 = Encoding.GetEncoding(
            1252,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    public static TextEncodingAnalysis Analyze(
        byte[] bytes,
        TextEncodingKind requestedSourceEncoding,
        TextEncodingKind outputEncoding)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidateOutputEncoding(outputEncoding);

        var (sourceEncoding, confidence, requiresReview, text) = requestedSourceEncoding == TextEncodingKind.Auto
            ? DetectAndDecode(bytes)
            : DecodeRequested(bytes, requestedSourceEncoding);

        RejectBinaryContent(text);
        _ = Encode(text, outputEncoding);

        return new TextEncodingAnalysis(
            sourceEncoding,
            confidence,
            requiresReview,
            text,
            [.. bytes],
            BuildPreview(text));
    }

    public static byte[] Encode(string text, TextEncodingKind outputEncoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateOutputEncoding(outputEncoding);

        try
        {
            var encoding = GetEncoding(outputEncoding);
            var content = encoding.GetBytes(text);
            var preamble = outputEncoding is TextEncodingKind.Utf8Bom or TextEncodingKind.Utf16LittleEndian
                ? encoding.GetPreamble()
                : [];

            if (preamble.Length == 0)
            {
                return content;
            }

            var encoded = new byte[preamble.Length + content.Length];
            preamble.CopyTo(encoded, 0);
            content.CopyTo(encoded, preamble.Length);
            return encoded;
        }
        catch (EncoderFallbackException exception)
        {
            throw TextEncodingException.OutputUnrepresentable(exception);
        }
    }

    private static (TextEncodingKind Encoding, TextEncodingConfidence Confidence, bool RequiresReview, string Text)
        DetectAndDecode(byte[] bytes)
    {
        if (HasPreamble(bytes, Utf8WithBom))
        {
            return (TextEncodingKind.Utf8Bom, TextEncodingConfidence.High, false, DecodeAfterPreamble(bytes, Utf8WithBom));
        }

        if (HasPreamble(bytes, Utf16LittleEndian))
        {
            return (TextEncodingKind.Utf16LittleEndian, TextEncodingConfidence.High, false, DecodeAfterPreamble(bytes, Utf16LittleEndian));
        }

        if (HasPreamble(bytes, Utf16BigEndian))
        {
            return (TextEncodingKind.Utf16BigEndian, TextEncodingConfidence.High, false, DecodeAfterPreamble(bytes, Utf16BigEndian));
        }

        if (TryDecode(bytes, Utf8, out var utf8Text))
        {
            return (TextEncodingKind.Utf8, TextEncodingConfidence.High, false, utf8Text);
        }

        var decoded1250 = TryDecode(bytes, Windows1250, out var windows1250Text);
        var decoded1252 = TryDecode(bytes, Windows1252, out var windows1252Text);

        if (decoded1250 && decoded1252)
        {
            return (TextEncodingKind.Windows1250, TextEncodingConfidence.Low, true, windows1250Text);
        }

        if (decoded1250)
        {
            return (TextEncodingKind.Windows1250, TextEncodingConfidence.Medium, false, windows1250Text);
        }

        if (decoded1252)
        {
            return (TextEncodingKind.Windows1252, TextEncodingConfidence.Medium, false, windows1252Text);
        }

        throw TextEncodingException.DecodeFailed();
    }

    private static (TextEncodingKind Encoding, TextEncodingConfidence Confidence, bool RequiresReview, string Text)
        DecodeRequested(byte[] bytes, TextEncodingKind requestedSourceEncoding)
    {
        if (requestedSourceEncoding == TextEncodingKind.Auto)
        {
            throw TextEncodingException.InvalidSourceEncoding();
        }

        var encoding = GetEncoding(requestedSourceEncoding);
        var preambleLength = requestedSourceEncoding switch
        {
            TextEncodingKind.Utf8 when HasAnyUnicodePreamble(bytes) => -1,
            TextEncodingKind.Utf8Bom or TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian
                when !HasPreamble(bytes, encoding) => -1,
            TextEncodingKind.Utf8Bom or TextEncodingKind.Utf16LittleEndian or TextEncodingKind.Utf16BigEndian =>
                encoding.GetPreamble().Length,
            _ => 0,
        };

        if (preambleLength < 0)
        {
            throw TextEncodingException.DecodeFailed();
        }

        try
        {
            return (
                requestedSourceEncoding,
                TextEncodingConfidence.High,
                false,
                encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw TextEncodingException.DecodeFailed(exception);
        }
    }

    private static Encoding GetEncoding(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => Utf8,
        TextEncodingKind.Utf8Bom => Utf8WithBom,
        TextEncodingKind.Utf16LittleEndian => Utf16LittleEndian,
        TextEncodingKind.Utf16BigEndian => Utf16BigEndian,
        TextEncodingKind.Windows1250 => Windows1250,
        TextEncodingKind.Windows1252 => Windows1252,
        _ => throw TextEncodingException.InvalidSourceEncoding(),
    };

    private static void ValidateOutputEncoding(TextEncodingKind outputEncoding)
    {
        if (outputEncoding is TextEncodingKind.Auto or TextEncodingKind.Utf16BigEndian)
        {
            throw TextEncodingException.InvalidOutputEncoding();
        }
    }

    private static bool HasAnyUnicodePreamble(byte[] bytes) =>
        HasPreamble(bytes, Utf8WithBom) ||
        HasPreamble(bytes, Utf16LittleEndian) ||
        HasPreamble(bytes, Utf16BigEndian);

    private static bool HasPreamble(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble);
    }

    private static string DecodeAfterPreamble(byte[] bytes, Encoding encoding)
    {
        var preambleLength = encoding.GetPreamble().Length;
        try
        {
            return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        }
        catch (DecoderFallbackException exception)
        {
            throw TextEncodingException.DecodeFailed(exception);
        }
    }

    private static bool TryDecode(byte[] bytes, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static void RejectBinaryContent(string text)
    {
        if (text.IndexOf('\0', StringComparison.Ordinal) >= 0)
        {
            throw TextEncodingException.BinaryContent();
        }

        var disallowedControls = text.Count(character =>
            char.IsControl(character) && character is not ('\t' or '\n' or '\r'));
        if (disallowedControls > Math.Max(4, text.Length / 100))
        {
            throw TextEncodingException.BinaryContent();
        }
    }

    private static string BuildPreview(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= PreviewByteLimit)
        {
            return text;
        }

        var builder = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > PreviewByteLimit)
            {
                break;
            }

            builder.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}
