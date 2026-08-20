using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReachCommander.Application.BatchRenames;
using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed record EvaluatedRename(
    string NameSegment,
    string ExtensionSegment,
    string CompleteName);

internal sealed partial class RenameRuleEvaluator
{
    private const int MaximumRuleLength = 512;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public EvaluatedRename Evaluate(
        string originalName,
        string? originalExtension,
        FileEntryType type,
        BatchRenameRules rules,
        int rowIndex)
    {
        ValidateRules(rules, rowIndex);
        var extension = type == FileEntryType.File ? originalExtension ?? string.Empty : string.Empty;
        var name = extension.Length == 0
            ? originalName
            : originalName[..^(extension.Length + 1)];

        try
        {
            var counter = checked(rules.CounterStart + rowIndex * rules.CounterStep)
                .ToString($"D{rules.CounterDigits}", CultureInfo.InvariantCulture);
            var generatedName = Expand(rules.NameMask, name, extension, counter);
            var generatedExtension = Expand(rules.ExtensionMask, name, extension, counter);
            generatedName = Replace(generatedName, rules);
            if (rules.ReplaceInExtension)
            {
                generatedExtension = Replace(generatedExtension, rules);
            }

            generatedName = ConvertCase(generatedName, rules.CaseMode);
            generatedExtension = ConvertCase(generatedExtension, rules.CaseMode);
            var completeName = generatedExtension.Length == 0
                ? generatedName
                : $"{generatedName}.{generatedExtension}";
            return new EvaluatedRename(generatedName, generatedExtension, completeName);
        }
        catch (InvalidRenameRuleException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentException or RegexMatchTimeoutException)
        {
            throw new InvalidRenameRuleException("The rename rule could not be evaluated safely.");
        }
    }

    private static void ValidateRules(BatchRenameRules rules, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.NameMask is null ||
            rules.ExtensionMask is null ||
            rules.SearchFor is null ||
            rules.ReplaceWith is null)
        {
            throw new InvalidRenameRuleException("Rename rule fields cannot be null.");
        }

        if (rules.NameMask.Length > MaximumRuleLength ||
            rules.ExtensionMask.Length > MaximumRuleLength ||
            rules.SearchFor.Length > MaximumRuleLength ||
            rules.ReplaceWith.Length > MaximumRuleLength)
        {
            throw new InvalidRenameRuleException("Rename rule fields cannot exceed 512 characters.");
        }

        if (rules.CounterDigits is < 1 or > 12)
        {
            throw new InvalidRenameRuleException("Counter digits must be between 1 and 12.");
        }

        if (rules.CounterStep == 0)
        {
            throw new InvalidRenameRuleException("Counter step cannot be zero.");
        }

        if (rowIndex < 0)
        {
            throw new InvalidRenameRuleException("The rename row index cannot be negative.");
        }
    }

    private static string Expand(
        string mask,
        string originalName,
        string originalExtension,
        string counter)
    {
        var result = new StringBuilder(mask.Length + originalName.Length);
        for (var index = 0; index < mask.Length; index++)
        {
            if (mask[index] != '[')
            {
                result.Append(mask[index]);
                continue;
            }

            var closeIndex = mask.IndexOf(']', index + 1);
            if (closeIndex < 0)
            {
                throw new InvalidRenameRuleException("A rename mask token is not closed.");
            }

            var token = mask[(index + 1)..closeIndex];
            result.Append(ExpandToken(token, originalName, originalExtension, counter));
            index = closeIndex;
        }

        return result.ToString();
    }

    private static string ExpandToken(
        string token,
        string originalName,
        string originalExtension,
        string counter)
    {
        return token switch
        {
            "N" => originalName,
            "E" => originalExtension,
            "C" => counter,
            _ => ExpandRange(token, originalName, originalExtension),
        };
    }

    private static string ExpandRange(
        string token,
        string originalName,
        string originalExtension)
    {
        var match = RangeToken().Match(token);
        if (!match.Success ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            start < 1)
        {
            throw new InvalidRenameRuleException($"Rename token '[{token}]' is invalid.");
        }

        int? end = null;
        if (match.Groups[3].Length > 0)
        {
            if (!int.TryParse(
                    match.Groups[3].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedEnd) ||
                parsedEnd < start)
            {
                throw new InvalidRenameRuleException($"Rename token '[{token}]' has an invalid range.");
            }

            end = parsedEnd;
        }

        var source = match.Groups[1].Value == "N" ? originalName : originalExtension;
        if (start > source.Length)
        {
            return string.Empty;
        }

        var finalPosition = Math.Min(end ?? source.Length, source.Length);
        return source.Substring(start - 1, finalPosition - start + 1);
    }

    private static string Replace(string value, BatchRenameRules rules)
    {
        if (rules.SearchFor.Length == 0)
        {
            return value;
        }

        var options = RegexOptions.CultureInvariant;
        if (!rules.MatchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var pattern = rules.UseRegex ? rules.SearchFor : Regex.Escape(rules.SearchFor);
        var regex = new Regex(pattern, options, RegexTimeout);
        return rules.UseRegex
            ? regex.Replace(value, rules.ReplaceWith)
            : regex.Replace(value, _ => rules.ReplaceWith);
    }

    private static string ConvertCase(string value, BatchRenameCaseMode mode) => mode switch
    {
        BatchRenameCaseMode.Unchanged => value,
        BatchRenameCaseMode.Lowercase => value.ToLowerInvariant(),
        BatchRenameCaseMode.Uppercase => value.ToUpperInvariant(),
        BatchRenameCaseMode.CapitalizeWords => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            value.ToLowerInvariant()),
        BatchRenameCaseMode.SentenceCase => SentenceCase(value),
        _ => throw new InvalidRenameRuleException("The requested case mode is invalid."),
    };

    private static string SentenceCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var lowered = value.ToLowerInvariant();
        return char.ToUpperInvariant(lowered[0]) + lowered[1..];
    }

    [GeneratedRegex("^([NE])([0-9]+)-([0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RangeToken();
}
