using System.Globalization;
using System.Text.RegularExpressions;
using ReachCommander.Domain.Archives;

namespace ReachCommander.Infrastructure.Archives.Classification;

internal sealed record ArchiveFilenameMetadata(ArchiveFormat Format, ArchiveRole Role);

internal static partial class ArchiveFilenameClassifier
{
    public static ArchiveFilenameMetadata? Classify(string name, bool isLink) =>
        Classify(name, isLink, siblingNames: null);

    public static ArchiveFilenameMetadata? Classify(
        string name,
        bool isLink,
        IReadOnlySet<string>? siblingNames)
    {
        if (isLink || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var numbered = ClassifyNumbered(name, out var matchedNumberedPattern);
        if (matchedNumberedPattern)
        {
            return numbered;
        }

        if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveFilenameMetadata(ArchiveFormat.SevenZip, ArchiveRole.Single);
        }

        if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            var role = HasSibling(name, ".rar", ".r00", siblingNames)
                ? ArchiveRole.Primary
                : ArchiveRole.Single;
            return new ArchiveFilenameMetadata(ArchiveFormat.Rar, role);
        }

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var role = HasSibling(name, ".zip", ".z01", siblingNames)
                ? ArchiveRole.Primary
                : ArchiveRole.Single;
            return new ArchiveFilenameMetadata(ArchiveFormat.Zip, role);
        }

        return null;
    }

    private static ArchiveFilenameMetadata? ClassifyNumbered(
        string name,
        out bool matchedNumberedPattern)
    {
        var match = PartRarRegex().Match(name);
        if (match.Success)
        {
            matchedNumberedPattern = true;
            return TryPositiveIndex(match, out var partIndex)
                ? new ArchiveFilenameMetadata(
                    ArchiveFormat.Rar,
                    partIndex == 1 ? ArchiveRole.Primary : ArchiveRole.Secondary)
                : null;
        }

        match = LegacyRarRegex().Match(name);
        if (match.Success)
        {
            matchedNumberedPattern = true;
            return TryNonNegativeIndex(match)
                ? new ArchiveFilenameMetadata(ArchiveFormat.Rar, ArchiveRole.Secondary)
                : null;
        }

        match = SplitSevenZipRegex().Match(name);
        if (match.Success)
        {
            matchedNumberedPattern = true;
            return TryPositiveIndex(match, out var sevenZipIndex)
                ? new ArchiveFilenameMetadata(
                    ArchiveFormat.SevenZip,
                    sevenZipIndex == 1 ? ArchiveRole.Primary : ArchiveRole.Secondary)
                : null;
        }

        match = SplitZipRegex().Match(name);
        if (match.Success)
        {
            matchedNumberedPattern = true;
            return TryPositiveIndex(match, out var zipIndex)
                ? new ArchiveFilenameMetadata(
                    ArchiveFormat.Zip,
                    zipIndex == 1 ? ArchiveRole.Primary : ArchiveRole.Secondary)
                : null;
        }

        match = ClassicZipRegex().Match(name);
        if (match.Success)
        {
            matchedNumberedPattern = true;
            return TryPositiveIndex(match, out _)
                ? new ArchiveFilenameMetadata(ArchiveFormat.Zip, ArchiveRole.Secondary)
                : null;
        }

        matchedNumberedPattern = false;
        return null;
    }

    private static bool HasSibling(
        string name,
        string terminalExtension,
        string siblingExtension,
        IReadOnlySet<string>? siblingNames)
    {
        if (siblingNames is null)
        {
            return false;
        }

        var baseName = name[..^terminalExtension.Length];
        var expected = baseName + siblingExtension;
        return siblingNames.Any(candidate =>
            string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryPositiveIndex(Match match, out int index) =>
        int.TryParse(
            match.Groups["index"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out index) && index > 0;

    private static bool TryNonNegativeIndex(Match match) =>
        int.TryParse(
            match.Groups["index"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var index) && index >= 0;

    [GeneratedRegex(@"^.+\.part(?<index>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartRarRegex();

    [GeneratedRegex(@"^.+\.r(?<index>\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRarRegex();

    [GeneratedRegex(@"^.+\.7z\.(?<index>\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitSevenZipRegex();

    [GeneratedRegex(@"^.+\.zip\.(?<index>\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitZipRegex();

    [GeneratedRegex(@"^.+\.z(?<index>\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassicZipRegex();
}
