using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Api.Contracts.BatchRenames;

public sealed record BatchRenamePreviewRequestDto(
    string SourceId,
    string DirectoryPath,
    IReadOnlyList<string> EntryPaths,
    BatchRenameRulesDto Rules)
{
    public BatchRenamePreviewCommand ToCommand() => new(
        SourceId,
        DirectoryPath,
        EntryPaths,
        Rules.ToModel());
}

public sealed record BatchRenameRulesDto(
    string NameMask,
    string ExtensionMask,
    string SearchFor,
    string ReplaceWith,
    bool UseRegex,
    bool MatchCase,
    bool ReplaceInExtension,
    BatchRenameCaseMode CaseMode,
    int CounterStart,
    int CounterStep,
    int CounterDigits)
{
    public BatchRenameRules ToModel() => new(
        NameMask,
        ExtensionMask,
        SearchFor,
        ReplaceWith,
        UseRegex,
        MatchCase,
        ReplaceInExtension,
        CaseMode,
        CounterStart,
        CounterStep,
        CounterDigits);
}
