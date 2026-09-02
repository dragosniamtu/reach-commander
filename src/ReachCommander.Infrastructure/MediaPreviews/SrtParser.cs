using System.Text;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class SrtParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictWindows1250 = CreateStrictWindows1250();

    private readonly int _maximumBytes;
    private readonly int _maximumCues;

    public SrtParser(int maximumBytes, int maximumCues)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (maximumCues <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCues));
        }

        _maximumBytes = maximumBytes;
        _maximumCues = maximumCues;
    }

    public SrtDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw MediaPreviewException.SubtitleInvalid();
        }

        if (bytes.Length > _maximumBytes)
        {
            throw MediaPreviewException.SubtitleTooLarge();
        }

        try
        {
            var span = bytes.Span;
            string text;
            if (span.StartsWith(Encoding.UTF8.GetPreamble()))
            {
                text = StrictUtf8.GetString(span[Encoding.UTF8.Preamble.Length..]);
            }
            else if (span.StartsWith(Encoding.Unicode.GetPreamble()))
            {
                text = StrictUtf16LittleEndian.GetString(span[Encoding.Unicode.Preamble.Length..]);
            }
            else if (span.StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            {
                text = StrictUtf16BigEndian.GetString(
                    span[Encoding.BigEndianUnicode.Preamble.Length..]);
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(span);
                }
                catch (DecoderFallbackException)
                {
                    return ParseWindows1250(span);
                }
            }

            return SrtDocument.Parse(text, _maximumCues);
        }
        catch (DecoderFallbackException)
        {
            throw MediaPreviewException.SubtitleEncodingUnsupported();
        }
    }

    private SrtDocument ParseWindows1250(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = StrictWindows1250.GetString(bytes);
            return SrtDocument.Parse(text, _maximumCues);
        }
        catch (DecoderFallbackException)
        {
            throw MediaPreviewException.SubtitleEncodingUnsupported();
        }
        catch (MediaPreviewException exception)
            when (exception.Code == "subtitle_invalid")
        {
            throw MediaPreviewException.SubtitleEncodingUnsupported();
        }
    }

    private static Encoding CreateStrictWindows1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            1250,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
