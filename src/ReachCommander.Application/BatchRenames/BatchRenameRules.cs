namespace ReachCommander.Application.BatchRenames;

public enum BatchRenameCaseMode
{
    Unchanged,
    Lowercase,
    Uppercase,
    CapitalizeWords,
    SentenceCase,
}

public sealed record BatchRenameRules(
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
    int CounterDigits);
