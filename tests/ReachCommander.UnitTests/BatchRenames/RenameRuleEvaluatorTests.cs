using ReachCommander.Application.BatchRenames;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.BatchRenames;

namespace ReachCommander.UnitTests.BatchRenames;

public sealed class RenameRuleEvaluatorTests
{
    private readonly RenameRuleEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_expands_name_extension_and_padded_counter()
    {
        var rules = Rules(nameMask: "[N]-[C]", extensionMask: "[E]", counterStart: 7, counterDigits: 3);

        var result = _evaluator.Evaluate("Holiday.JPG", "JPG", FileEntryType.File, rules, rowIndex: 0);

        Assert.Equal("Holiday-007.JPG", result.CompleteName);
    }

    [Theory]
    [InlineData("[N1-4]", "Holiday", "Holi")]
    [InlineData("[N3-]", "Holiday", "liday")]
    [InlineData("[N3-99]", "Holiday", "liday")]
    [InlineData("[E1-2]", "JPG", "JP")]
    public void Evaluate_supports_one_based_clamped_ranges(string mask, string source, string expected)
    {
        var isExtension = mask.StartsWith("[E", StringComparison.Ordinal);
        var rules = isExtension
            ? Rules(nameMask: "x", extensionMask: mask)
            : Rules(nameMask: mask, extensionMask: string.Empty);

        var result = _evaluator.Evaluate(
            isExtension ? "file.JPG" : source,
            isExtension ? "JPG" : null,
            FileEntryType.File,
            rules,
            rowIndex: 0);

        Assert.Equal(expected, isExtension ? result.ExtensionSegment : result.NameSegment);
    }

    [Fact]
    public void Evaluate_applies_regex_then_case_conversion()
    {
        var rules = Rules(
            searchFor: "holiday-(\\d+)",
            replaceWith: "trip-$1",
            useRegex: true,
            matchCase: false,
            caseMode: BatchRenameCaseMode.Uppercase);

        var result = _evaluator.Evaluate("Holiday-42.jpg", "jpg", FileEntryType.File, rules, 0);

        Assert.Equal("TRIP-42.JPG", result.CompleteName);
    }

    [Fact]
    public void Evaluate_keeps_dollar_literal_in_non_regex_replacement()
    {
        var rules = Rules(searchFor: "Holiday", replaceWith: "$archive", matchCase: false);

        var result = _evaluator.Evaluate("holiday.jpg", "jpg", FileEntryType.File, rules, 0);

        Assert.Equal("$archive.jpg", result.CompleteName);
    }

    [Fact]
    public void Evaluate_replaces_extension_only_when_enabled()
    {
        var unchanged = _evaluator.Evaluate(
            "photo.JPG",
            "JPG",
            FileEntryType.File,
            Rules(searchFor: "JPG", replaceWith: "png"),
            0);
        var changed = _evaluator.Evaluate(
            "photo.JPG",
            "JPG",
            FileEntryType.File,
            Rules(searchFor: "JPG", replaceWith: "png", replaceInExtension: true),
            0);

        Assert.Equal("photo.JPG", unchanged.CompleteName);
        Assert.Equal("photo.png", changed.CompleteName);
    }

    [Theory]
    [InlineData(BatchRenameCaseMode.Lowercase, "holiday photo.jpg")]
    [InlineData(BatchRenameCaseMode.Uppercase, "HOLIDAY PHOTO.JPG")]
    [InlineData(BatchRenameCaseMode.CapitalizeWords, "Holiday Photo.Jpg")]
    [InlineData(BatchRenameCaseMode.SentenceCase, "Holiday photo.Jpg")]
    public void Evaluate_applies_deterministic_case_modes(
        BatchRenameCaseMode caseMode,
        string expected)
    {
        var result = _evaluator.Evaluate(
            "hOLIDAY pHOTO.jPg",
            "jPg",
            FileEntryType.File,
            Rules(caseMode: caseMode),
            0);

        Assert.Equal(expected, result.CompleteName);
    }

    [Fact]
    public void Evaluate_treats_dotfile_and_directory_as_extensionless()
    {
        var rules = Rules("[N]-[C]", "[E]", counterDigits: 2);

        Assert.Equal(".env-01", _evaluator.Evaluate(".env", null, FileEntryType.File, rules, 0).CompleteName);
        Assert.Equal("Drafts-01", _evaluator.Evaluate("Drafts", null, FileEntryType.Directory, rules, 0).CompleteName);
    }

    [Theory]
    [InlineData("[Q]")]
    [InlineData("[N0-2]")]
    [InlineData("[N4-2]")]
    [InlineData("[Nabc]")]
    [InlineData("[N")]
    public void Evaluate_rejects_unknown_or_malformed_tokens(string mask)
    {
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate("file.txt", "txt", FileEntryType.File, Rules(mask, "[E]"), 0));
    }

    [Fact]
    public void Evaluate_rejects_invalid_limits_regex_and_counter_overflow()
    {
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate("file.txt", "txt", FileEntryType.File, Rules(nameMask: new string('x', 513)), 0));
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate("file.txt", "txt", FileEntryType.File, Rules(counterDigits: 13), 0));
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate("file.txt", "txt", FileEntryType.File, Rules(counterStep: 0), 0));
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate("file.txt", "txt", FileEntryType.File, Rules(searchFor: "(" , useRegex: true), 0));
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate(
                "file.txt",
                "txt",
                FileEntryType.File,
                Rules(counterStart: int.MaxValue, counterStep: int.MaxValue),
                2));
    }

    private static BatchRenameRules Rules(
        string nameMask = "[N]",
        string extensionMask = "[E]",
        string searchFor = "",
        string replaceWith = "",
        bool useRegex = false,
        bool matchCase = true,
        bool replaceInExtension = false,
        BatchRenameCaseMode caseMode = BatchRenameCaseMode.Unchanged,
        int counterStart = 1,
        int counterStep = 1,
        int counterDigits = 1) => new(
            nameMask,
            extensionMask,
            searchFor,
            replaceWith,
            useRegex,
            matchCase,
            replaceInExtension,
            caseMode,
            counterStart,
            counterStep,
            counterDigits);
}
