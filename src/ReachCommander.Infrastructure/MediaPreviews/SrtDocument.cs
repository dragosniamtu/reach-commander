using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed partial class SrtDocument
{
    private readonly IReadOnlyList<SrtCueBlock> _blocks;

    private SrtDocument(IReadOnlyList<SrtCueBlock> blocks)
    {
        _blocks = blocks;
        Cues = blocks
            .Select((block, index) => new SubtitleCue(
                index,
                block.StartMilliseconds,
                block.EndMilliseconds,
                string.Join('\n', block.TextLines)))
            .ToArray();
    }

    public IReadOnlyList<SubtitleCue> Cues { get; }

    public static SrtDocument Parse(string text, int maximumCues)
    {
        if (string.IsNullOrWhiteSpace(text) || maximumCues <= 0)
        {
            throw MediaPreviewException.SubtitleInvalid();
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        var rawBlocks = BlankLinePattern().Split(normalized);
        if (rawBlocks.Length == 0)
        {
            throw MediaPreviewException.SubtitleInvalid();
        }

        var blocks = new List<SrtCueBlock>(Math.Min(rawBlocks.Length, maximumCues));
        foreach (var rawBlock in rawBlocks)
        {
            if (blocks.Count >= maximumCues)
            {
                throw MediaPreviewException.SubtitleTooLarge();
            }

            var lines = rawBlock.Split('\n');
            if (lines.Length < 3 ||
                !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw MediaPreviewException.SubtitleInvalid();
            }

            var timing = TimingPattern().Match(lines[1]);
            if (!timing.Success)
            {
                throw MediaPreviewException.SubtitleInvalid();
            }

            var start = ParseTimestamp(timing, "s");
            var end = ParseTimestamp(timing, "e");
            if (end <= start || lines.Skip(2).All(string.IsNullOrEmpty))
            {
                throw MediaPreviewException.SubtitleInvalid();
            }

            blocks.Add(new SrtCueBlock(start, end, lines[2..]));
        }

        return new SrtDocument(blocks);
    }

    public byte[] RenderWithOffset(long offsetMilliseconds)
    {
        try
        {
            var builder = new StringBuilder();
            for (var index = 0; index < _blocks.Count; index++)
            {
                var block = _blocks[index];
                var shiftedStart = checked(block.StartMilliseconds + offsetMilliseconds);
                var shiftedEnd = checked(block.EndMilliseconds + offsetMilliseconds);
                var start = Math.Max(0, shiftedStart);
                var end = Math.Max(0, shiftedEnd);
                if (end <= start)
                {
                    throw MediaPreviewException.SubtitleOffsetInvalid();
                }

                builder.Append(index + 1)
                    .Append("\r\n")
                    .Append(FormatTimestamp(start))
                    .Append(" --> ")
                    .Append(FormatTimestamp(end))
                    .Append("\r\n");
                foreach (var line in block.TextLines)
                {
                    builder.Append(line).Append("\r\n");
                }

                if (index < _blocks.Count - 1)
                {
                    builder.Append("\r\n");
                }
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetBytes(builder.ToString());
        }
        catch (OverflowException)
        {
            throw MediaPreviewException.SubtitleOffsetInvalid();
        }
    }

    private static long ParseTimestamp(Match match, string prefix)
    {
        if (!long.TryParse(match.Groups[$"{prefix}h"].Value, out var hours) ||
            !int.TryParse(match.Groups[$"{prefix}m"].Value, out var minutes) ||
            !int.TryParse(match.Groups[$"{prefix}s"].Value, out var seconds) ||
            !int.TryParse(match.Groups[$"{prefix}f"].Value, out var milliseconds) ||
            minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            throw MediaPreviewException.SubtitleInvalid();
        }

        try
        {
            return checked((((hours * 60) + minutes) * 60 + seconds) * 1_000 + milliseconds);
        }
        catch (OverflowException)
        {
            throw MediaPreviewException.SubtitleInvalid();
        }
    }

    private static string FormatTimestamp(long totalMilliseconds)
    {
        var hours = totalMilliseconds / 3_600_000;
        var remainder = totalMilliseconds % 3_600_000;
        var minutes = remainder / 60_000;
        remainder %= 60_000;
        var seconds = remainder / 1_000;
        var milliseconds = remainder % 1_000;
        return FormattableString.Invariant(
            $"{hours:00}:{minutes:00}:{seconds:00},{milliseconds:000}");
    }

    [GeneratedRegex(@"\n[\t ]*\n+")]
    private static partial Regex BlankLinePattern();

    [GeneratedRegex(
        @"^(?<sh>\d{2,}):(?<sm>\d{2}):(?<ss>\d{2}),(?<sf>\d{3})[ \t]+-->[ \t]+(?<eh>\d{2,}):(?<em>\d{2}):(?<es>\d{2}),(?<ef>\d{3})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimingPattern();

    private sealed record SrtCueBlock(
        long StartMilliseconds,
        long EndMilliseconds,
        IReadOnlyList<string> TextLines);
}
