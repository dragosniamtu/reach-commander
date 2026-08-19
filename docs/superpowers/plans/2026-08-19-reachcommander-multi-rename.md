# ReachCommander Multi-Rename Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a server-authoritative, Total Commander-inspired Multi-Rename Tool with complete new-filename previews, safe two-phase execution, and one-level revalidated Undo.

**Architecture:** The existing .NET 10 modular monolith gains isolated batch-rename application contracts and infrastructure services for rule evaluation, path/name validation, short-lived plans, serialized execution, compensation, and undo. Angular 22 gains a separate `MultiRenameStore` and accessible modal workspace; `Ctrl+M` sends the active pane's ordered logical selection to the server and never evaluates authoritative rename rules in the browser.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core controllers and Problem Details, `System.IO`, bounded .NET regular expressions, Angular 22 standalone components and Signals, Angular CDK A11y, Vitest, xUnit, and Playwright Chromium.

## Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Preserve .NET SDK `10.0.400`, Angular `22.1`, TypeScript strict mode, and Node `24.15+` or `22.22.3+` requirements.
- Use TDD for every production slice: observe the focused test fail before writing its implementation.
- Before every commit, inspect `git status --short` and stage only the files changed for that task; never absorb unrelated user edits even when a sample `git add` command names a directory.
- Browser requests and responses contain only source IDs, logical paths, logical names, and safe metadata; never serialize a configured root, canonical physical path, or temporary physical path.
- `Ctrl+M` opens Multi-Rename. F4 remains disabled/reserved for a later single-rename slice.
- Support at most 5,000 direct-child entries from one active logical directory per plan.
- Preview plans expire after 10 minutes and are bounded in memory.
- Preview, Execute, and Undo must re-enforce source availability, `readOnly: false`, canonical confinement, direct-child scope, and symbolic-link rejection.
- No overwrite, recursive traversal, cross-directory move, persistent history, background job, saved preset, date/time token, plugin token, or manual per-row override.
- Batch destination comparison is conservatively `OrdinalIgnoreCase` on every host, while a case-only rename of one entry remains legal.
- Use only built-in .NET/Angular capabilities and the already-installed Angular CDK; add no runtime package unless a failing requirement cannot be met otherwise.
- Keep `config/sources.json` and `compose.yaml` read-only by default. Writable access must be an explicit administrator configuration and mount decision.
- Before completion, run all backend, Angular, Playwright, publish, repository-hygiene, and available Docker checks. Do not claim Docker passed when its CLI or daemon is unavailable.

## File Structure

```text
src/ReachCommander.Application/BatchRenames/
├── BatchRenameExceptions.cs          Stable application failures
├── BatchRenameOperation.cs           Execute/Undo result records and enums
├── BatchRenamePreview.cs             Preview command/row/result records and enums
├── BatchRenameRules.cs               Rule/case-mode value objects
└── IBatchRenameService.cs            Preview/Execute/Undo application port

src/ReachCommander.Infrastructure/BatchRenames/
├── AsyncDirectoryLock.cs             Per-source/logical-directory serialization
├── BatchRenameExecutor.cs            Two-phase move and compensation
├── BatchRenameFileSystem.cs          Injectable System.IO boundary and snapshots
├── BatchRenamePlanner.cs             Authoritative preview construction/revalidation
├── BatchRenamePlanStore.cs           Bounded plan/result/undo cache
├── BatchRenameService.cs             Application-port orchestration and idempotency
├── RenameNameValidator.cs            Portable component-name policy
└── RenameRuleEvaluator.cs            Mask/replacement/case/counter evaluation

src/ReachCommander.Api/
├── Contracts/BatchRenames/
│   ├── BatchRenameRequestDto.cs       Browser request mapping
│   └── BatchRenameResponseDto.cs      Explicit safe response mapping
└── Controllers/BatchRenamesController.cs

client/reach-commander-ui/src/app/
├── core/api/api.models.ts             Batch-rename transport contracts
├── core/api/reach-commander-api.ts    HTTP implementation
├── core/state/multi-rename.models.ts  Dialog context and UI state
├── core/state/multi-rename-store.ts   Debounced preview/execute/undo state
├── features/multi-rename/
│   ├── multi-rename-dialog.component.{ts,html,scss,spec.ts}
│   ├── multi-rename-preview-table.component.{ts,html,scss,spec.ts}
│   └── rename-mask-field.component.{ts,html,scss,spec.ts}
└── shared/components/name-diff/
    └── name-diff.component.{ts,html,scss,spec.ts}

tests/ReachCommander.UnitTests/BatchRenames/
├── BatchRenameExecutorTests.cs
├── BatchRenamePlannerTests.cs
├── BatchRenameServiceTests.cs
├── RenameNameValidatorTests.cs
└── RenameRuleEvaluatorTests.cs

tests/ReachCommander.IntegrationTests/BatchRenamesApiTests.cs
tests/e2e/specs/multi-rename.spec.ts
```

---

### Task 1: Application contracts and deterministic rename-rule engine

**Files:**

- Create: `src/ReachCommander.Application/BatchRenames/BatchRenameRules.cs`
- Create: `src/ReachCommander.Application/BatchRenames/BatchRenamePreview.cs`
- Create: `src/ReachCommander.Application/BatchRenames/BatchRenameOperation.cs`
- Create: `src/ReachCommander.Application/BatchRenames/BatchRenameExceptions.cs`
- Create: `src/ReachCommander.Application/BatchRenames/IBatchRenameService.cs`
- Create: `src/ReachCommander.Infrastructure/BatchRenames/RenameRuleEvaluator.cs`
- Create: `src/ReachCommander.Infrastructure/Properties/AssemblyInfo.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/RenameRuleEvaluatorTests.cs`

**Interfaces:**

- Produces: `BatchRenameRules`, `BatchRenameCaseMode`, `BatchRenamePreviewCommand`, `BatchRenamePreview`, `BatchRenameOperationResult`, and `IBatchRenameService`.
- Produces: internal `RenameRuleEvaluator.Evaluate(string originalName, string? originalExtension, FileEntryType type, BatchRenameRules rules, int rowIndex)`.
- Consumes later: Tasks 3-10 use these names verbatim in backend and TypeScript mirrors.

- [ ] **Step 1: Write failing rule-engine tests**

Create table-driven tests for masks, ranges, counters, replacements, casing, dotfiles, folders, and malformed rules:

```csharp
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
    [InlineData("[E1-2]", "JPG", "JP")]
    public void Evaluate_supports_one_based_clamped_ranges(string mask, string source, string expected)
    {
        var rules = mask.StartsWith("[E", StringComparison.Ordinal)
            ? Rules(nameMask: "x", extensionMask: mask)
            : Rules(nameMask: mask, extensionMask: string.Empty);

        var result = _evaluator.Evaluate(
            mask.StartsWith("[E", StringComparison.Ordinal) ? "file.JPG" : source,
            mask.StartsWith("[E", StringComparison.Ordinal) ? "JPG" : null,
            FileEntryType.File,
            rules,
            rowIndex: 0);

        Assert.Equal(expected, mask.StartsWith("[E", StringComparison.Ordinal)
            ? result.ExtensionSegment
            : result.NameSegment);
    }

    [Fact]
    public void Evaluate_applies_regex_then_case_conversion()
    {
        var rules = Rules(
            nameMask: "[N]",
            extensionMask: "[E]",
            searchFor: "holiday-(\\d+)",
            replaceWith: "trip-$1",
            useRegex: true,
            matchCase: false,
            caseMode: BatchRenameCaseMode.Uppercase);

        var result = _evaluator.Evaluate("Holiday-42.jpg", "jpg", FileEntryType.File, rules, 0);

        Assert.Equal("TRIP-42.JPG", result.CompleteName);
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
    public void Evaluate_rejects_unknown_or_malformed_tokens(string mask)
    {
        Assert.Throws<InvalidRenameRuleException>(() =>
            _evaluator.Evaluate("file.txt", "txt", FileEntryType.File, Rules(mask, "[E]"), 0));
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
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~RenameRuleEvaluatorTests
```

Expected: compilation fails because the batch-rename contracts and evaluator do not exist.

- [ ] **Step 3: Add the application records and service port**

Use these exact public signatures:

```csharp
// BatchRenameRules.cs
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
```

```csharp
// BatchRenamePreview.cs
using ReachCommander.Domain.Files;

namespace ReachCommander.Application.BatchRenames;

public enum BatchRenamePreviewStatus { Ready, Unchanged, Invalid, Conflict, Stale }

public sealed record BatchRenamePreviewCommand(
    string SourceId,
    string DirectoryPath,
    IReadOnlyList<string> EntryPaths,
    BatchRenameRules Rules);

public sealed record BatchRenamePreviewRow(
    string SourcePath,
    string OldName,
    string? OldExtension,
    string NewName,
    FileEntryType Type,
    long? Size,
    DateTimeOffset ModifiedAt,
    BatchRenamePreviewStatus Status,
    string? Message);

public sealed record BatchRenamePreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<BatchRenamePreviewRow> Rows,
    bool CanExecute,
    int ChangedCount,
    int UnchangedCount,
    int InvalidCount);
```

```csharp
// BatchRenameOperation.cs
using ReachCommander.Domain.Files;

namespace ReachCommander.Application.BatchRenames;

public enum BatchRenameOperationStatus { Completed, Failed, RecoveryRequired, Undone }
public enum BatchRenameRowResult { Completed, Unchanged, Failed, RolledBack, RecoveryRequired }

public sealed record BatchRenameOperationRow(
    string OldPath,
    string NewPath,
    string CurrentPath,
    string OldName,
    string NewName,
    string CurrentName,
    FileEntryType Type,
    BatchRenameRowResult Result,
    string? Message);

public sealed record BatchRenameOperationResult(
    Guid OperationId,
    BatchRenameOperationStatus Status,
    IReadOnlyList<BatchRenameOperationRow> Rows,
    bool CompensationAttempted,
    bool RecoveryRequired,
    bool UndoAvailable,
    DateTimeOffset? UndoExpiresAt);
```

```csharp
// IBatchRenameService.cs
namespace ReachCommander.Application.BatchRenames;

public interface IBatchRenameService
{
    ValueTask<BatchRenamePreview> PreviewAsync(
        BatchRenamePreviewCommand command,
        CancellationToken cancellationToken);

    ValueTask<BatchRenameOperationResult> ExecuteAsync(
        Guid planId,
        CancellationToken cancellationToken);

    ValueTask<BatchRenameOperationResult> UndoAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
```

Define the focused exception types in `BatchRenameExceptions.cs` with one stable constructor shape:

```csharp
namespace ReachCommander.Application.BatchRenames;

public abstract class BatchRenameException(string message) : Exception(message) { }
public sealed class InvalidRenameRuleException(string message) : BatchRenameException(message) { }
public sealed class BatchTooLargeException(string message) : BatchRenameException(message) { }
public sealed class SourceReadOnlyException(string message) : BatchRenameException(message) { }
public sealed class RenamePlanNotFoundException(string message) : BatchRenameException(message) { }
public sealed class RenamePlanExpiredException(string message) : BatchRenameException(message) { }
public sealed class RenamePlanStaleException(string message) : BatchRenameException(message) { }
public sealed class RenameRecoveryRequiredException(string message) : BatchRenameException(message) { }
```

Every exception message may contain source IDs and logical paths but never physical paths.

- [ ] **Step 4: Implement the rule evaluator**

Add `InternalsVisibleTo("ReachCommander.UnitTests")` in `src/ReachCommander.Infrastructure/Properties/AssemblyInfo.cs`. Implement `RenameRuleEvaluator` with:

```csharp
internal sealed record EvaluatedRename(
    string NameSegment,
    string ExtensionSegment,
    string CompleteName);

internal sealed class RenameRuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public EvaluatedRename Evaluate(
        string originalName,
        string? originalExtension,
        FileEntryType type,
        BatchRenameRules rules,
        int rowIndex)
    {
        ValidateRules(rules);
        var extension = type == FileEntryType.File ? originalExtension ?? string.Empty : string.Empty;
        var name = extension.Length == 0
            ? originalName
            : originalName[..^(extension.Length + 1)];
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
        var complete = generatedExtension.Length == 0
            ? generatedName
            : $"{generatedName}.{generatedExtension}";
        return new EvaluatedRename(generatedName, generatedExtension, complete);
    }
}
```

Complete the private methods without client-side equivalents: scan bracket tokens left-to-right; expand only `N`, `E`, `C`, `N<start>-<end?>`, and `E<start>-<end?>`; clamp range ends; reject the invalid forms covered by the tests. Validate masks at 512 characters, search/replacement fields at 512 characters, counter digits at `1..12`, and non-zero counter step. Use `RegexOptions.CultureInvariant`, optional `IgnoreCase`, the 100 ms timeout, and a match evaluator for literal replacement so `$` remains literal outside regex mode. Translate regex construction/replacement/timeouts and checked counter overflow into `InvalidRenameRuleException`. Use invariant lower/upper casing, invariant `TextInfo.ToTitleCase`, and deterministic sentence casing.

- [ ] **Step 5: Run the focused and full backend unit suites**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~RenameRuleEvaluatorTests
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release
```

Expected: all rule tests and all existing unit tests pass.

- [ ] **Step 6: Commit the rule slice**

```powershell
git add src/ReachCommander.Application/BatchRenames src/ReachCommander.Infrastructure/BatchRenames/RenameRuleEvaluator.cs src/ReachCommander.Infrastructure/Properties/AssemblyInfo.cs tests/ReachCommander.UnitTests/BatchRenames/RenameRuleEvaluatorTests.cs
git commit -m "feat: add multi-rename rule engine"
```

---

### Task 2: Secure child resolution and portable filename policy

**Files:**

- Modify: `src/ReachCommander.Application/Files/IPathSecurityService.cs`
- Modify: `src/ReachCommander.Infrastructure/Security/PathSecurityService.cs`
- Create: `src/ReachCommander.Infrastructure/BatchRenames/RenameNameValidator.cs`
- Modify: `tests/ReachCommander.UnitTests/Security/PathSecurityServiceTests.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/RenameNameValidatorTests.cs`

**Interfaces:**

- Produces: `IPathSecurityService.ResolveChildAsync(string sourceId, string parentLogicalPath, string childName, CancellationToken)` for existing or not-yet-existing direct children.
- Produces: internal `RenameNameValidator.Validate(string completeName)` returning `RenameNameValidation(bool IsValid, string? Message)`.
- Consumes: Task 3 uses both to construct destinations without accepting destination paths from the browser.

- [ ] **Step 1: Write failing child-resolution and filename tests**

Add tests proving a missing direct child can be resolved safely while separators, dot segments, roots, and parent escapes fail:

```csharp
[Fact]
public async Task ResolveChildAsync_returns_a_confined_path_for_a_missing_child()
{
    var resolved = await _service.ResolveChildAsync(
        "media", "/Movies", "renamed.mkv", CancellationToken.None);

    Assert.Equal("/Movies/renamed.mkv", resolved.LogicalPath);
    Assert.Equal(
        Path.Combine(_sourceRoot, "Movies", "renamed.mkv"),
        resolved.PhysicalPath);
}

[Theory]
[InlineData("../escape")]
[InlineData("sub/name")]
[InlineData("sub\\name")]
[InlineData(".")]
[InlineData("..")]
[InlineData("C:drive")]
public async Task ResolveChildAsync_rejects_non_component_names(string childName)
{
    await Assert.ThrowsAsync<InvalidLogicalPathException>(() =>
        _service.ResolveChildAsync("media", "/Movies", childName, CancellationToken.None).AsTask());
}
```

Create filename-validator tests:

```csharp
public sealed class RenameNameValidatorTests
{
    private readonly RenameNameValidator _validator = new();

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("Résumé 2026.txt")]
    [InlineData(".env")]
    public void Validate_accepts_portable_names(string name) =>
        Assert.True(_validator.Validate(name).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("lpt1.txt")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("bad:name")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Validate_rejects_non_portable_or_reserved_names(string name) =>
        Assert.False(_validator.Validate(name).IsValid);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~PathSecurityServiceTests|FullyQualifiedName~RenameNameValidatorTests"
```

Expected: compilation fails because `ResolveChildAsync` and `RenameNameValidator` do not exist.

- [ ] **Step 3: Extend path security for direct children**

Add the method to the interface and implement it by resolving the existing parent through `ResolveAsync`, verifying it is a directory, rejecting any child string containing `/`, `\`, NUL, `.` or `..`, combining it with the canonical parent, and checking containment against the canonical source root. Do not require the child to exist and do not follow the final child when it does exist; Task 3 must be able to inspect and reject a symbolic-link entry lexically.

Use the same logical join convention as `LocalFileBrowser`:

```csharp
public async ValueTask<ResolvedSourcePath> ResolveChildAsync(
    string sourceId,
    string parentLogicalPath,
    string childName,
    CancellationToken cancellationToken)
{
    ValidateChildName(childName, parentLogicalPath);
    var parent = await ResolveAsync(sourceId, parentLogicalPath, cancellationToken);
    if (!Directory.Exists(parent.PhysicalPath))
    {
        throw new InvalidLogicalPathException(parent.LogicalPath, "the parent is not a directory");
    }

    var candidate = Path.GetFullPath(Path.Combine(parent.PhysicalPath, childName));
    var canonicalRoot = ResolveAbsolutePath(parent.Source.RootPath, cancellationToken);
    EnsureWithin(canonicalRoot, candidate, parent.LogicalPath);
    var logicalPath = parent.LogicalPath == "/"
        ? $"/{childName}"
        : $"{parent.LogicalPath}/{childName}";
    return new ResolvedSourcePath(parent.Source, logicalPath, candidate);
}
```

- [ ] **Step 4: Implement the conservative filename validator**

`RenameNameValidator` must reject control characters `U+0000..U+001F`, `< > : " / \ | ? *`, trailing dot/space, Windows device names `CON`, `PRN`, `AUX`, `NUL`, `COM1..COM9`, and `LPT1..LPT9` before the first dot, and names whose UTF-8 representation exceeds 255 bytes. Return safe user-facing messages such as `"A filename cannot end with a dot or space."`; never include a physical path.

```csharp
internal sealed record RenameNameValidation(bool IsValid, string? Message);

internal sealed partial class RenameNameValidator
{
    private static readonly char[] PortableInvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public RenameNameValidation Validate(string completeName)
    {
        if (string.IsNullOrEmpty(completeName) || completeName is "." or "..")
            return Invalid("A filename cannot be empty, '.' or '..'.");
        if (completeName.Any(character => character < ' ' || PortableInvalidCharacters.Contains(character)))
            return Invalid("The filename contains a forbidden character.");
        if (completeName.EndsWith('.') || completeName.EndsWith(' '))
            return Invalid("A filename cannot end with a dot or space.");
        if (ReservedDeviceName().IsMatch(completeName))
            return Invalid("The filename is reserved by Windows.");
        if (Encoding.UTF8.GetByteCount(completeName) > 255)
            return Invalid("The filename exceeds the 255-byte component limit.");
        return new RenameNameValidation(true, null);
    }

    private static RenameNameValidation Invalid(string message) => new(false, message);

    [GeneratedRegex("^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}
```

- [ ] **Step 5: Run security/unit tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~PathSecurityServiceTests|FullyQualifiedName~RenameNameValidatorTests"
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release
```

Expected: all focused and full unit tests pass.

- [ ] **Step 6: Commit path/name policy**

```powershell
git add src/ReachCommander.Application/Files/IPathSecurityService.cs src/ReachCommander.Infrastructure/Security/PathSecurityService.cs src/ReachCommander.Infrastructure/BatchRenames/RenameNameValidator.cs tests/ReachCommander.UnitTests/Security/PathSecurityServiceTests.cs tests/ReachCommander.UnitTests/BatchRenames/RenameNameValidatorTests.cs
git commit -m "feat: validate multi-rename destinations"
```

---

### Task 3: Authoritative preview planner and bounded plan cache

**Files:**

- Create: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenameFileSystem.cs`
- Create: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanner.cs`
- Create: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanStore.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/BatchRenamePlannerTests.cs`
- Create: `tests/ReachCommander.UnitTests/Support/BatchRenameTestFixture.cs`

**Interfaces:**

- Produces: internal `IBatchRenameFileSystem`, `LocalBatchRenameFileSystem`, `BatchRenameEntrySnapshot`, `EntryFingerprint`, `PlannedRename`, and `StoredBatchRenamePlan`.
- Produces: `BatchRenamePlanner.PreviewAsync(BatchRenamePreviewCommand, CancellationToken)` and `RevalidateAsync(StoredBatchRenamePlan, CancellationToken)`.
- Produces: `BatchRenamePlanStore.AddPlan` and `GetRequiredPlan`; Task 5 extends the same store with operation/result methods.
- Consumes: Tasks 4-6 orchestrate execution/undo from stored plans.

- [ ] **Step 1: Write failing preview-planner tests**

Use a fresh temporary source per test and assert authoritative order and full filenames:

```csharp
public sealed class BatchRenamePlannerTests : IDisposable
{
    private readonly BatchRenameTestFixture _fixture = new();

    [Fact]
    public async Task Preview_returns_complete_new_names_in_request_order()
    {
        _fixture.WriteFile("Movies/holiday-photo.jpg", "photo");
        _fixture.CreateDirectory("Movies/Drafts");
        var planner = _fixture.CreatePlanner();

        var preview = await planner.PreviewAsync(new BatchRenamePreviewCommand(
            "media",
            "/Movies",
            ["/Movies/holiday-photo.jpg", "/Movies/Drafts"],
            _fixture.Rules("Archive-[C]", "[E]", counterDigits: 3)),
            CancellationToken.None);

        Assert.Equal(["Archive-001.jpg", "Archive-002"], preview.Rows.Select(row => row.NewName));
        Assert.All(preview.Rows, row => Assert.Equal(BatchRenamePreviewStatus.Ready, row.Status));
        Assert.True(preview.CanExecute);
    }

    [Fact]
    public async Task Preview_marks_every_duplicate_and_existing_destination_as_conflict()
    {
        _fixture.WriteFile("Movies/a.txt", "a");
        _fixture.WriteFile("Movies/b.txt", "b");
        _fixture.WriteFile("Movies/taken.txt", "occupied");
        var planner = _fixture.CreatePlanner();

        var duplicate = await planner.PreviewAsync(_fixture.Command(
            "/Movies", ["a.txt", "b.txt"], _fixture.Rules("same", "txt")), CancellationToken.None);
        var occupied = await planner.PreviewAsync(_fixture.Command(
            "/Movies", ["a.txt"], _fixture.Rules("taken", "txt")), CancellationToken.None);

        Assert.All(duplicate.Rows, row => Assert.Equal(BatchRenamePreviewStatus.Conflict, row.Status));
        Assert.Equal(BatchRenamePreviewStatus.Conflict, Assert.Single(occupied.Rows).Status);
        Assert.False(duplicate.CanExecute);
        Assert.False(occupied.CanExecute);
    }

    [Fact]
    public async Task Preview_blocks_read_only_sources_and_non_child_or_symbolic_link_entries()
    {
        var readOnlyPlanner = _fixture.CreatePlanner(sourceReadOnly: true);
        await Assert.ThrowsAsync<SourceReadOnlyException>(() =>
            readOnlyPlanner.PreviewAsync(_fixture.Command("/Movies", ["a.txt"]), CancellationToken.None).AsTask());

        var planner = _fixture.CreatePlanner();
        await Assert.ThrowsAsync<InvalidLogicalPathException>(() =>
            planner.PreviewAsync(_fixture.Command("/Movies", ["../outside.txt"]), CancellationToken.None).AsTask());

        _fixture.WriteFile("Movies/link.txt", "target");
        _fixture.MarkEntryAsSymbolicLink("Movies/link.txt");
        var symbolicLink = await planner.PreviewAsync(
            _fixture.Command("/Movies", ["link.txt"]), CancellationToken.None);
        Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(symbolicLink.Rows).Status);
        Assert.False(symbolicLink.CanExecute);
    }

    [Fact]
    public async Task Preview_limits_batches_and_expires_plans_after_ten_minutes()
    {
        var clock = _fixture.Clock;
        var planner = _fixture.CreatePlanner(maxEntries: 2);
        await Assert.ThrowsAsync<BatchTooLargeException>(() =>
            planner.PreviewAsync(_fixture.Command("/Movies", ["a", "b", "c"]), CancellationToken.None).AsTask());

        _fixture.WriteFile("Movies/a.txt", "A");
        var preview = await planner.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"]), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Throws<RenamePlanExpiredException>(() => _fixture.PlanStore.GetRequiredPlan(preview.PlanId));
    }

    public void Dispose() => _fixture.Dispose();
}
```

The fixture must create only paths under `TemporaryDirectory`, expose a controllable `TimeProvider`, configure `PathSecurityService` with a fake catalog, provide helpers that convert child names to logical paths, and let `MarkEntryAsSymbolicLink` override only the fake filesystem snapshot metadata so the test is portable on Windows hosts without symlink privileges.

- [ ] **Step 2: Run the planner tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~BatchRenamePlannerTests
```

Expected: compilation fails because planner, store, filesystem boundary, and fixture do not exist.

- [ ] **Step 3: Implement the injectable filesystem boundary and fingerprints**

Define exact internal records and methods:

```csharp
internal sealed record EntryFingerprint(
    FileEntryType Type,
    long? Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes);

internal sealed record BatchRenameEntrySnapshot(
    string LogicalPath,
    string PhysicalPath,
    string Name,
    string? Extension,
    FileEntryType Type,
    long? Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes,
    bool IsSymbolicLink)
{
    public EntryFingerprint Fingerprint => new(Type, Length, ModifiedAt, Attributes);
}

internal interface IBatchRenameFileSystem
{
    BatchRenameEntrySnapshot GetEntry(string logicalPath, string physicalPath);
    IReadOnlyList<BatchRenameEntrySnapshot> ListChildren(string parentLogicalPath, string parentPhysicalPath);
    bool EntryExists(string physicalPath);
    void Move(string sourcePhysicalPath, string destinationPhysicalPath, FileEntryType type);
}
```

`LocalBatchRenameFileSystem` maps metadata like `LocalFileBrowser`, detects `LinkTarget`/`ReparsePoint`, uses `File.Move(source, destination, overwrite: false)` for files and `Directory.Move` for directories, and converts missing/unauthorized conditions into existing safe application exceptions.

- [ ] **Step 4: Implement stored plans and bounded expiration**

Use internal records:

```csharp
internal sealed record PlannedRename(
    string OldLogicalPath,
    string NewLogicalPath,
    string OldPhysicalPath,
    string NewPhysicalPath,
    string OldName,
    string NewName,
    FileEntryType Type,
    EntryFingerprint PreviewFingerprint,
    BatchRenamePreviewStatus Status,
    string? Message);

internal sealed record StoredBatchRenamePlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    string DirectoryLogicalPath,
    string DirectoryPhysicalPath,
    IReadOnlyList<PlannedRename> Entries,
    BatchRenamePreview Preview);
```

`BatchRenamePlanStore` uses `ConcurrentDictionary` plus a private lock for compound transitions, creates opaque correlation IDs with `Guid.NewGuid()`, uses `TimeProvider` for deterministic tests, caps storage at 256 preview plans and 128 completed operations, and evicts oldest-created records after expired records are removed. A missing ID throws `RenamePlanNotFoundException`; an expired ID is removed and throws `RenamePlanExpiredException`.

- [ ] **Step 5: Implement authoritative preview planning**

`BatchRenamePlanner` must:

1. Reject zero entries and more than the configured maximum.
2. Resolve the directory and reject a non-directory, unavailable, or read-only source.
3. Require distinct entry paths whose normalized logical parent equals the resolved directory.
4. Use `ResolveChildAsync` plus `GetEntry` for lexical entry inspection; mark symbolic links and entry types other than File/Directory Invalid.
5. Evaluate rules in caller order and validate complete names.
6. Compare final names with `StringComparer.OrdinalIgnoreCase`.
7. Mark every member of an in-plan duplicate Conflict.
8. Allow a destination occupied by the same selected plan (swap/cycle), but mark a destination occupied by an unselected entry Conflict.
9. Mark exact old/new name equality Unchanged.
10. Compute `ChangedCount` as Ready rows, `UnchangedCount` as Unchanged rows, and `InvalidCount` as Invalid, Conflict, or Stale rows. `CanExecute` is true only when `ChangedCount > 0` and `InvalidCount == 0`.
11. Add the complete plan to the store even when `CanExecute` is false, then return its safe public preview.

`RevalidateAsync` repeats source policy, containment, lexical symlink, fingerprint, and destination checks under the future Task 4 directory lock. Any mismatch throws `RenamePlanStaleException`; it never silently rebuilds a changed plan.

- [ ] **Step 6: Run focused and full backend tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~BatchRenamePlannerTests
dotnet test ReachCommander.slnx -c Release
```

Expected: planner tests and all existing solution tests pass.

- [ ] **Step 7: Commit the preview slice**

```powershell
git add src/ReachCommander.Infrastructure/BatchRenames/BatchRenameFileSystem.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanner.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanStore.cs tests/ReachCommander.UnitTests/BatchRenames/BatchRenamePlannerTests.cs tests/ReachCommander.UnitTests/Support/BatchRenameTestFixture.cs
git commit -m "feat: add authoritative multi-rename previews"
```

---

### Task 4: Two-phase execution, case-only swaps, and compensation

**Files:**

- Create: `src/ReachCommander.Infrastructure/BatchRenames/AsyncDirectoryLock.cs`
- Create: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenameExecutor.cs`
- Modify: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenameFileSystem.cs`
- Modify: `tests/ReachCommander.UnitTests/Support/BatchRenameTestFixture.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/BatchRenameExecutorTests.cs`

**Interfaces:**

- Produces: `AsyncDirectoryLock.AcquireAsync(string key, CancellationToken)` returning an `IAsyncDisposable` lease.
- Produces: `BatchRenameExecutor.ExecuteAsync(Guid operationId, StoredBatchRenamePlan plan, CancellationToken)` returning `BatchRenameExecutionOutcome`.
- Produces: `ExecutedRename` records containing original/final paths and post-execution fingerprints for Task 5 Undo.
- Consumes: Task 3 `BatchRenamePlanner.RevalidateAsync` and `IBatchRenameFileSystem`.

- [ ] **Step 1: Write failing executor tests for swap, cycle, case-only rename, and compensation**

Construct exact stored plans through fixture helpers so the executor is tested independently of mask syntax:

```csharp
public sealed class BatchRenameExecutorTests : IDisposable
{
    private readonly BatchRenameTestFixture _fixture = new();

    [Fact]
    public async Task Execute_supports_a_two_way_swap()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var executor = _fixture.CreateExecutor();
        var plan = _fixture.StoredPlan("/Movies", ("a.txt", "b.txt"), ("b.txt", "a.txt"));

        var outcome = await executor.ExecuteAsync(Guid.NewGuid(), plan, CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.Completed, outcome.Result.Status);
        Assert.Equal("B", _fixture.ReadFile("Movies/a.txt"));
        Assert.Equal("A", _fixture.ReadFile("Movies/b.txt"));
        Assert.Empty(_fixture.ReservedTemporaryEntries("Movies"));
    }

    [Fact]
    public async Task Execute_supports_a_three_entry_cycle_and_case_only_change()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        _fixture.WriteFile("Movies/c.txt", "C");
        var executor = _fixture.CreateExecutor();

        var cycle = await executor.ExecuteAsync(Guid.NewGuid(), _fixture.StoredPlan(
            "/Movies", ("a.txt", "b.txt"), ("b.txt", "c.txt"), ("c.txt", "a.txt")), CancellationToken.None);
        var casing = await executor.ExecuteAsync(Guid.NewGuid(), _fixture.StoredPlan(
            "/Movies", ("a.txt", "A.TXT")), CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.Completed, cycle.Result.Status);
        Assert.Equal(BatchRenameOperationStatus.Completed, casing.Result.Status);
        Assert.True(File.Exists(Path.Combine(_fixture.SourceRoot, "Movies", "A.TXT")));
    }

    [Fact]
    public async Task Execute_compensates_every_completed_move_after_a_failure()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var failingFileSystem = _fixture.CreateFailingFileSystem(failOnMoveNumber: 4);
        var executor = _fixture.CreateExecutor(failingFileSystem);

        var outcome = await executor.ExecuteAsync(Guid.NewGuid(), _fixture.StoredPlan(
            "/Movies", ("a.txt", "one.txt"), ("b.txt", "two.txt")), CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.Result.CompensationAttempted);
        Assert.False(outcome.Result.RecoveryRequired);
        Assert.Equal("A", _fixture.ReadFile("Movies/a.txt"));
        Assert.Equal("B", _fixture.ReadFile("Movies/b.txt"));
        Assert.Empty(_fixture.ReservedTemporaryEntries("Movies"));
    }

    [Fact]
    public async Task Execute_reports_recovery_required_when_compensation_also_fails()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var failingFileSystem = _fixture.CreateFailingFileSystem(4, 5);

        var outcome = await _fixture.CreateExecutor(failingFileSystem).ExecuteAsync(
            Guid.NewGuid(),
            _fixture.StoredPlan("/Movies", ("a.txt", "one.txt"), ("b.txt", "two.txt")),
            CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.RecoveryRequired, outcome.Result.Status);
        Assert.True(outcome.Result.RecoveryRequired);
        Assert.Contains(outcome.Result.Rows, row => row.Result == BatchRenameRowResult.RecoveryRequired);
        Assert.All(outcome.Result.Rows, row => Assert.DoesNotContain(_fixture.SourceRoot, row.Message ?? string.Empty));
    }

    public void Dispose() => _fixture.Dispose();
}
```

- [ ] **Step 2: Run executor tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~BatchRenameExecutorTests
```

Expected: compilation fails because executor, lock, execution outcome, and failure-injection helpers do not exist.

- [ ] **Step 3: Implement the keyed asynchronous directory lock**

Use a `ConcurrentDictionary<string, LockEntry>` where `LockEntry` contains a `SemaphoreSlim` and reference count. `AcquireAsync` increments the reference before waiting, returns a lease that releases once, and removes/disposes the entry only when no holder or waiter remains. Executor keys are `$"directory\0{sourceId}\0{directoryLogicalPath}"` with source ID compared ordinally and normalized logical paths supplied by the planner; Task 5 uses disjoint `plan\0` and `undo\0` prefixes for request idempotency.

Cancellation is honored while waiting. The lease must be safe under exceptions and never expose a physical path in its key.

- [ ] **Step 4: Implement the two-phase executor**

Add internal results:

```csharp
internal sealed record ExecutedRename(
    string OriginalLogicalPath,
    string FinalLogicalPath,
    string OriginalPhysicalPath,
    string FinalPhysicalPath,
    FileEntryType Type,
    EntryFingerprint PostExecutionFingerprint);

internal sealed record BatchRenameExecutionOutcome(
    BatchRenameOperationResult Result,
    IReadOnlyList<ExecutedRename> ExecutedEntries);
```

Execution behavior:

1. Acquire the logical directory lock.
2. Revalidate the complete plan through `BatchRenamePlanner.RevalidateAsync`.
3. Generate collision-free names `.reachcommander-rename-{operationId:N}-{index:D5}.tmp` through `ResolveChildAsync`.
4. Before the first mutation, emit one structured log event containing the operation ID and the complete logical original/temporary/final mapping for every Ready row; never log physical paths.
5. Check cancellation once more before the first move. After mutation begins, finish or compensate even if the HTTP cancellation token fires.
6. Record every successful move as `(from, to, type)`.
7. Phase one moves every Ready source to its temporary path.
8. Phase two moves every temporary path to its final path.
9. Capture post-execution fingerprints and return Completed rows plus Unchanged rows.
10. On an expected `IOException`, `UnauthorizedAccessException`, or safe application exception, reverse all successful move records in reverse order.
11. If all reverse moves succeed, return Failed with `CompensationAttempted: true`; if any reverse move fails, inspect each known entry location and return RecoveryRequired with logical original/intended/current paths and names only.

Never catch `StackOverflowException`, `OutOfMemoryException`, or process-fatal exceptions. Map caught filesystem failures to stable safe row messages rather than returning `Exception.Message`. Structured logs may include source ID, logical directory, operation ID, row index, and phase, but not physical paths.

- [ ] **Step 5: Run executor, planner, and full tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~BatchRenameExecutorTests|FullyQualifiedName~BatchRenamePlannerTests"
dotnet test ReachCommander.slnx -c Release
```

Expected: swap, cycle, case-only, compensation, and existing tests all pass.

- [ ] **Step 6: Commit the execution slice**

```powershell
git add src/ReachCommander.Infrastructure/BatchRenames/AsyncDirectoryLock.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenameExecutor.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenameFileSystem.cs tests/ReachCommander.UnitTests/BatchRenames/BatchRenameExecutorTests.cs tests/ReachCommander.UnitTests/Support/BatchRenameTestFixture.cs
git commit -m "feat: execute multi-rename plans safely"
```

---

### Task 5: Service orchestration, idempotency, and one-level Undo

**Files:**

- Create: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenameService.cs`
- Modify: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanStore.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Modify: `tests/ReachCommander.UnitTests/Support/BatchRenameTestFixture.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/BatchRenameServiceTests.cs`

**Interfaces:**

- Implements: Task 1 `IBatchRenameService` exactly.
- Produces: stored completed operation and undo mapping keyed by operation ID and original plan ID.
- Consumes: planner, plan store, executor, filesystem boundary, keyed lock, and `TimeProvider`.
- Used by: Task 6 API and Task 7 Angular transport.

- [ ] **Step 1: Write failing orchestration/Undo tests**

```csharp
public sealed class BatchRenameServiceTests : IDisposable
{
    private readonly BatchRenameTestFixture _fixture = new();

    [Fact]
    public async Task Execute_is_idempotent_for_concurrent_and_later_retries()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            service.ExecuteAsync(preview.PlanId, CancellationToken.None).AsTask()));
        var retry = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);

        Assert.All(concurrent, result => Assert.Equal(concurrent[0], result));
        Assert.Equal(concurrent[0], retry);
        Assert.False(_fixture.EntryExists("Movies/a.txt"));
        Assert.Equal("A", _fixture.ReadFile("Movies/renamed.txt"));
    }

    [Fact]
    public async Task Undo_restores_the_whole_batch_once_for_concurrent_and_later_retries()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.CreateDirectory("Movies/Drafts");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt", "Drafts"], _fixture.Rules("Archive-[C]", "[E]")),
            CancellationToken.None);
        var operation = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            service.UndoAsync(operation.OperationId, CancellationToken.None).AsTask()));
        var retry = await service.UndoAsync(operation.OperationId, CancellationToken.None);

        Assert.All(concurrent, result => Assert.Equal(concurrent[0], result));
        Assert.Equal(BatchRenameOperationStatus.Undone, concurrent[0].Status);
        Assert.Equal(concurrent[0], retry);
        Assert.True(_fixture.EntryExists("Movies/a.txt"));
        Assert.True(_fixture.EntryExists("Movies/Drafts"));
    }

    [Fact]
    public async Task Undo_blocks_the_entire_batch_when_one_destination_changed()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt", "b.txt"], _fixture.Rules("item-[C]", "txt")),
            CancellationToken.None);
        var operation = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);
        _fixture.WriteFile("Movies/item-1.txt", "changed content");

        await Assert.ThrowsAsync<RenamePlanStaleException>(() =>
            service.UndoAsync(operation.OperationId, CancellationToken.None).AsTask());

        Assert.True(_fixture.EntryExists("Movies/item-1.txt"));
        Assert.True(_fixture.EntryExists("Movies/item-2.txt"));
    }

    [Fact]
    public async Task Execute_rejects_expired_or_non_executable_plans_without_mutation()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var service = _fixture.CreateService();
        var invalid = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("a", "txt")),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidRenameRuleException>(() =>
            service.ExecuteAsync(invalid.PlanId, CancellationToken.None).AsTask());

        var valid = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);
        _fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<RenamePlanExpiredException>(() =>
            service.ExecuteAsync(valid.PlanId, CancellationToken.None).AsTask());
        Assert.True(_fixture.EntryExists("Movies/a.txt"));
    }

    public void Dispose() => _fixture.Dispose();
}
```

- [ ] **Step 2: Run service tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~BatchRenameServiceTests
```

Expected: compilation fails because `BatchRenameService`, operation storage, and undo orchestration do not exist.

- [ ] **Step 3: Extend the store for completed operations and retries**

Add:

```csharp
internal sealed record StoredBatchRenameOperation(
    Guid OperationId,
    Guid PlanId,
    DateTimeOffset CompletedAt,
    DateTimeOffset UndoExpiresAt,
    string SourceId,
    string DirectoryLogicalPath,
    string DirectoryPhysicalPath,
    IReadOnlyList<ExecutedRename> Entries,
    BatchRenameOperationResult ExecuteResult,
    BatchRenameOperationResult? UndoResult);
```

The store must atomically associate one operation with one plan. `TryGetOperationForPlan(planId)` supplies idempotent Execute. `GetRequiredOperation(operationId)` supplies Undo. `SaveUndoResult` is compare-and-set as a final consistency guard. Retain operation results for 30 minutes and include `UndoExpiresAt` in Execute responses; an expired operation throws `RenamePlanExpiredException`.

- [ ] **Step 4: Implement service orchestration**

`BatchRenameService` behavior:

```csharp
public ValueTask<BatchRenamePreview> PreviewAsync(
    BatchRenamePreviewCommand command,
    CancellationToken cancellationToken) =>
    planner.PreviewAsync(command, cancellationToken);
```

`ExecuteAsync` first returns any stored operation for the plan. Otherwise it acquires `AsyncDirectoryLock` with the non-filesystem key `plan\0{planId:N}`, checks the operation store again, loads the plan, rejects `CanExecute == false`, generates an operation ID, runs the executor, captures successful mappings, and atomically stores the result before releasing the plan lock. This double-check guarantees simultaneous requests for one plan execute only once; later retries receive the exact stored success, compensated failure, or recovery-required result.

`UndoAsync` first returns an existing stored Undo result. Otherwise it acquires `AsyncDirectoryLock` with `undo\0{operationId:N}`, checks for a stored Undo result again, and then:

1. Loads the successful unexpired operation.
2. Revalidates every final entry against its post-execution fingerprint and verifies every original name is free.
3. Builds a reverse `StoredBatchRenamePlan` from final paths to original paths without evaluating masks.
4. Runs the same executor under the same directory lock.
5. Maps a successful result to `BatchRenameOperationStatus.Undone` with `UndoAvailable: false`.
6. Atomically stores and returns the Undo result before releasing the operation lock.

The `plan\0`, `undo\0`, and `directory\0` key prefixes keep orchestration locks disjoint from executor directory locks. Undo never skips a stale row. Any preflight mismatch throws `RenamePlanStaleException` before the first reverse move.

- [ ] **Step 5: Register the complete backend feature**

Add singletons in `DependencyInjection.AddReachCommanderInfrastructure`:

```csharp
services.AddSingleton<TimeProvider>(TimeProvider.System);
services.AddSingleton<IBatchRenameFileSystem, LocalBatchRenameFileSystem>();
services.AddSingleton<RenameRuleEvaluator>();
services.AddSingleton<RenameNameValidator>();
services.AddSingleton<BatchRenamePlanStore>();
services.AddSingleton<BatchRenamePlanner>();
services.AddSingleton<AsyncDirectoryLock>();
services.AddSingleton<BatchRenameExecutor>();
services.AddSingleton<IBatchRenameService, BatchRenameService>();
```

Keep every service stateless or concurrency-safe because all registrations are singleton.

- [ ] **Step 6: Run service and full solution tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~BatchRenameServiceTests
dotnet test ReachCommander.slnx -c Release
```

Expected: service, unit, and integration suites pass.

- [ ] **Step 7: Commit service and Undo**

```powershell
git add src/ReachCommander.Infrastructure/BatchRenames/BatchRenameService.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanStore.cs src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/BatchRenames/BatchRenameServiceTests.cs tests/ReachCommander.UnitTests/Support/BatchRenameTestFixture.cs
git commit -m "feat: add idempotent multi-rename undo"
```

---

### Task 6: Batch-Rename HTTP API and safe Problem Details

**Files:**

- Create: `src/ReachCommander.Api/Contracts/BatchRenames/BatchRenameRequestDto.cs`
- Create: `src/ReachCommander.Api/Contracts/BatchRenames/BatchRenameResponseDto.cs`
- Create: `src/ReachCommander.Api/Controllers/BatchRenamesController.cs`
- Modify: `src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs`
- Create: `tests/ReachCommander.IntegrationTests/BatchRenamesApiTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`

**Interfaces:**

- Produces: `POST /api/batch-renames/preview`.
- Produces: `POST /api/batch-renames/{planId:guid}/execute`.
- Produces: `POST /api/batch-renames/{operationId:guid}/undo`.
- Produces: camel-case DTOs matching the TypeScript contracts in Task 7.

- [ ] **Step 1: Write failing end-to-end API integration tests**

Each test creates its own unique subdirectory beneath `factory.MediaRoot` to avoid shared-fixture mutation:

```csharp
public sealed class BatchRenamesApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Preview_execute_and_undo_return_only_logical_names()
    {
        var caseName = $"rename-{Guid.NewGuid():N}";
        var physicalDirectory = Directory.CreateDirectory(Path.Combine(factory.MediaRoot, caseName));
        File.WriteAllText(Path.Combine(physicalDirectory.FullName, "alpha.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(physicalDirectory.FullName, "Drafts"));
        using var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId = "media",
            directoryPath = $"/{caseName}",
            entryPaths = new[] { $"/{caseName}/alpha.txt", $"/{caseName}/Drafts" },
            rules = Rules("Archive-[C]", "[E]", counterDigits: 3),
        });
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        var preview = await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Equal(["Archive-001.txt", "Archive-002"], preview!.Rows.Select(row => row.NewName));
        Assert.DoesNotContain(factory.MediaRoot, previewBody, StringComparison.OrdinalIgnoreCase);

        var execute = await client.PostAsync($"/api/batch-renames/{preview.PlanId}/execute", null);
        var operation = await execute.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
        Assert.True(File.Exists(Path.Combine(physicalDirectory.FullName, "Archive-001.txt")));

        var undo = await client.PostAsync($"/api/batch-renames/{operation!.OperationId}/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.True(File.Exists(Path.Combine(physicalDirectory.FullName, "alpha.txt")));
        Assert.True(Directory.Exists(Path.Combine(physicalDirectory.FullName, "Drafts")));
    }

    [Fact]
    public async Task Preview_blocks_read_only_source_without_mutation()
    {
        using var client = factory.CreateClient();
        File.WriteAllText(Path.Combine(factory.ArchiveRoot, "file.txt"), "archive");
        var readOnly = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId = "archive",
            directoryPath = "/",
            entryPaths = new[] { "/file.txt" },
            rules = Rules("renamed", "[E]"),
        });

        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        var problem = await readOnly.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("source_read_only", problem!.Code);
        Assert.True(File.Exists(Path.Combine(factory.ArchiveRoot, "file.txt")));
    }

    private static BatchRenameRulesDto Rules(
        string nameMask,
        string extensionMask,
        int counterDigits = 1) => new(
            nameMask, extensionMask, "", "", false, false, false,
            BatchRenameCaseMode.Unchanged, 1, 1, counterDigits);

    private sealed record PreviewRowResponse(string NewName);
    private sealed record PreviewResponse(Guid PlanId, IReadOnlyList<PreviewRowResponse> Rows);
    private sealed record OperationResponse(Guid OperationId);
    private sealed record ProblemResponse(string Code);
}
```

`ReachCommanderApiFactory` must expose existing directories `MediaRoot` and `ArchiveRoot`, configure `media` as writable, configure `archive` with `readOnly: true`, and retain `usb` as unavailable. Add separate tests for `usb` returning 503 `source_unavailable`, a destination conflict returning a non-executable 200 preview without mutation, invalid rule returning 400 `invalid_rename_rule`, expired plan returning 410 `rename_plan_expired`, stale plan returning 409 `rename_plan_stale`, and an unknown API route remaining JSON 404.

- [ ] **Step 2: Run the API tests and verify RED**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~BatchRenamesApiTests
```

Expected: API requests return 404 because the controller/contracts do not exist.

- [ ] **Step 3: Add explicit request and response DTOs**

Use records whose field names match Task 1, and map manually:

```csharp
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
        NameMask, ExtensionMask, SearchFor, ReplaceWith,
        UseRegex, MatchCase, ReplaceInExtension, CaseMode,
        CounterStart, CounterStep, CounterDigits);
}
```

Response DTOs must include all safe public fields from `BatchRenamePreview` and `BatchRenameOperationResult` but omit every internal physical/temp/fingerprint field. Keep enums typed; the existing global `JsonStringEnumConverter` serializes them camel-case.

- [ ] **Step 4: Add the thin controller**

```csharp
[ApiController]
[Route("api/batch-renames")]
public sealed class BatchRenamesController(IBatchRenameService service) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<ActionResult<BatchRenamePreviewDto>> Preview(
        BatchRenamePreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(BatchRenamePreviewDto.FromModel(
            await service.PreviewAsync(request.ToCommand(), cancellationToken)));

    [HttpPost("{planId:guid}/execute")]
    public async Task<ActionResult<BatchRenameOperationDto>> Execute(
        Guid planId,
        CancellationToken cancellationToken) =>
        Ok(BatchRenameOperationDto.FromModel(
            await service.ExecuteAsync(planId, cancellationToken)));

    [HttpPost("{operationId:guid}/undo")]
    public async Task<ActionResult<BatchRenameOperationDto>> Undo(
        Guid operationId,
        CancellationToken cancellationToken) =>
        Ok(BatchRenameOperationDto.FromModel(
            await service.UndoAsync(operationId, cancellationToken)));
}
```

- [ ] **Step 5: Extend Problem Details mappings**

Map:

| Exception | HTTP | `code` |
|---|---:|---|
| `InvalidRenameRuleException` | 400 | `invalid_rename_rule` |
| `BatchTooLargeException` | 400 | `batch_too_large` |
| `SourceReadOnlyException` | 403 | `source_read_only` |
| `RenamePlanNotFoundException` | 404 | `rename_plan_not_found` |
| `RenamePlanExpiredException` | 410 | `rename_plan_expired` |
| `RenamePlanStaleException` | 409 | `rename_plan_stale` |
| `RenameRecoveryRequiredException` | 500 | `rename_recovery_required` |

Keep existing source/path mappings. Log stable codes and logical request routes only.

- [ ] **Step 6: Run integration and full backend tests**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~BatchRenamesApiTests
dotnet test ReachCommander.slnx -c Release
```

Expected: all integration, unit, and static-hosting tests pass.

- [ ] **Step 7: Commit the HTTP slice**

```powershell
git add src/ReachCommander.Api/Contracts/BatchRenames src/ReachCommander.Api/Controllers/BatchRenamesController.cs src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs tests/ReachCommander.IntegrationTests/BatchRenamesApiTests.cs tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs
git commit -m "feat: expose secure multi-rename API"
```

---

### Task 7: Angular transport contracts and debounced Multi-Rename state

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/multi-rename.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/multi-rename-store.ts`
- Test: `client/reach-commander-ui/src/app/core/state/multi-rename-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/app.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`

**Interfaces:**

- Produces: TypeScript mirrors `BatchRenameRulesDto`, `BatchRenamePreviewRequestDto`, `BatchRenamePreviewDto`, and `BatchRenameOperationDto`.
- Adds: `CommanderApiPort.previewBatchRename`, `executeBatchRename`, and `undoBatchRename`.
- Produces: `MultiRenameContext`, `MultiRenameState`, and root-provided `MultiRenameStore` with open/update/execute/undo/close methods.
- Consumes: Tasks 8-9 render and trigger this state.

- [ ] **Step 1: Write failing HTTP transport tests**

```typescript
it('posts only logical values when previewing a batch rename', async () => {
  const body: BatchRenamePreviewRequestDto = previewRequest();
  const result = api.previewBatchRename(body);
  const request = http.expectOne('/api/batch-renames/preview');

  expect(request.request.method).toBe('POST');
  expect(request.request.body).toEqual(body);
  expect(JSON.stringify(request.request.body)).not.toContain('physical');
  request.flush(previewResponse());

  await expect(result).resolves.toEqual(previewResponse());
});

it('uses identifier-only routes for execute and undo', async () => {
  const planId = '11111111-1111-4111-8111-111111111111';
  const operationId = '22222222-2222-4222-8222-222222222222';
  const execute = api.executeBatchRename(planId);
  const executeRequest = http.expectOne(`/api/batch-renames/${planId}/execute`);
  expect(executeRequest.request.method).toBe('POST');
  expect(executeRequest.request.body).toEqual({});
  executeRequest.flush(operationResponse());
  await execute;

  const undo = api.undoBatchRename(operationId);
  const undoRequest = http.expectOne(`/api/batch-renames/${operationId}/undo`);
  expect(undoRequest.request.method).toBe('POST');
  undoRequest.flush(operationResponse({ status: 'undone', undoAvailable: false }));
  await undo;
});
```

- [ ] **Step 2: Write failing MultiRenameStore tests**

Use `vi.useFakeTimers()` and a fake API:

```typescript
it('debounces rule edits and discards a stale preview response', async () => {
  const first = deferred<BatchRenamePreviewDto>();
  const second = deferred<BatchRenamePreviewDto>();
  api.previewHandler = (request) => request.rules.nameMask === 'first' ? first.promise : second.promise;
  store.open(context());
  store.updateRules({ nameMask: 'first' });
  await vi.advanceTimersByTimeAsync(250);
  store.updateRules({ nameMask: 'second' });
  await vi.advanceTimersByTimeAsync(250);

  second.resolve(previewResponse({ rows: [previewRow('second-001.txt')] }));
  await Promise.resolve();
  first.resolve(previewResponse({ rows: [previewRow('first-001.txt')] }));
  await Promise.resolve();

  expect(store.state().preview?.rows[0]?.newName).toBe('second-001.txt');
});

it('enables Start only for a current executable preview with changes', async () => {
  store.open(context());
  api.resolvePreview(previewResponse({ canExecute: true, changedCount: 2 }));
  await vi.advanceTimersByTimeAsync(250);

  expect(store.canExecute()).toBe(true);
  store.updateRules({ nameMask: '[N]' });
  expect(store.canExecute()).toBe(false);
});

it('shows a disabled read-only state without requesting preview', () => {
  store.open(context({ isReadOnly: true }));

  expect(store.state().disabledReason).toContain('read-only');
  expect(api.previewRequests).toHaveLength(0);
});
```

- [ ] **Step 3: Run Angular tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
Pop-Location
```

Expected: TypeScript compilation fails because batch-rename DTOs and store do not exist.

- [ ] **Step 4: Add exact TypeScript transport contracts**

Mirror server enum strings and fields:

```typescript
export type BatchRenameCaseMode =
  | 'unchanged' | 'lowercase' | 'uppercase' | 'capitalizeWords' | 'sentenceCase';
export type BatchRenamePreviewStatus = 'ready' | 'unchanged' | 'invalid' | 'conflict' | 'stale';
export type BatchRenameOperationStatus = 'completed' | 'failed' | 'recoveryRequired' | 'undone';
export type BatchRenameRowResult = 'completed' | 'unchanged' | 'failed' | 'rolledBack' | 'recoveryRequired';

export interface BatchRenameRulesDto {
  readonly nameMask: string;
  readonly extensionMask: string;
  readonly searchFor: string;
  readonly replaceWith: string;
  readonly useRegex: boolean;
  readonly matchCase: boolean;
  readonly replaceInExtension: boolean;
  readonly caseMode: BatchRenameCaseMode;
  readonly counterStart: number;
  readonly counterStep: number;
  readonly counterDigits: number;
}

export interface BatchRenamePreviewRequestDto {
  readonly sourceId: string;
  readonly directoryPath: string;
  readonly entryPaths: readonly string[];
  readonly rules: BatchRenameRulesDto;
}

export interface BatchRenamePreviewRowDto {
  readonly sourcePath: string;
  readonly oldName: string;
  readonly oldExtension: string | null;
  readonly newName: string;
  readonly type: FileEntryType;
  readonly size: number | null;
  readonly modifiedAt: string;
  readonly status: BatchRenamePreviewStatus;
  readonly message: string | null;
}

export interface BatchRenamePreviewDto {
  readonly planId: string;
  readonly expiresAt: string;
  readonly rows: readonly BatchRenamePreviewRowDto[];
  readonly canExecute: boolean;
  readonly changedCount: number;
  readonly unchangedCount: number;
  readonly invalidCount: number;
}

export interface BatchRenameOperationRowDto {
  readonly oldPath: string;
  readonly newPath: string;
  readonly currentPath: string;
  readonly oldName: string;
  readonly newName: string;
  readonly currentName: string;
  readonly type: FileEntryType;
  readonly result: BatchRenameRowResult;
  readonly message: string | null;
}

export interface BatchRenameOperationDto {
  readonly operationId: string;
  readonly status: BatchRenameOperationStatus;
  readonly rows: readonly BatchRenameOperationRowDto[];
  readonly compensationAttempted: boolean;
  readonly recoveryRequired: boolean;
  readonly undoAvailable: boolean;
  readonly undoExpiresAt: string | null;
}
```

Use the existing `FileEntryType` in `api.models.ts`; GUIDs and ISO timestamps remain `string` on the client.

Extend the abstract port:

```typescript
abstract previewBatchRename(request: BatchRenamePreviewRequestDto): Promise<BatchRenamePreviewDto>;
abstract executeBatchRename(planId: string): Promise<BatchRenameOperationDto>;
abstract undoBatchRename(operationId: string): Promise<BatchRenameOperationDto>;
```

Update `AppTestApi` and `FakeCommanderApi` with explicit methods that throw `new Error('Not used by this test')` so all existing tests remain type-safe.

- [ ] **Step 5: Implement the HTTP methods**

```typescript
previewBatchRename(request: BatchRenamePreviewRequestDto): Promise<BatchRenamePreviewDto> {
  return firstValueFrom(this.http.post<BatchRenamePreviewDto>('/api/batch-renames/preview', request));
}

executeBatchRename(planId: string): Promise<BatchRenameOperationDto> {
  return firstValueFrom(this.http.post<BatchRenameOperationDto>(
    `/api/batch-renames/${encodeURIComponent(planId)}/execute`, {},
  ));
}

undoBatchRename(operationId: string): Promise<BatchRenameOperationDto> {
  return firstValueFrom(this.http.post<BatchRenameOperationDto>(
    `/api/batch-renames/${encodeURIComponent(operationId)}/undo`, {},
  ));
}
```

- [ ] **Step 6: Implement isolated Multi-Rename state**

Define:

```typescript
export interface MultiRenameContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
  readonly entries: readonly FileEntryDto[];
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
}

export interface MultiRenameState {
  readonly open: boolean;
  readonly context: MultiRenameContext | null;
  readonly rules: BatchRenameRulesDto;
  readonly preview: BatchRenamePreviewDto | null;
  readonly operation: BatchRenameOperationDto | null;
  readonly previewPending: boolean;
  readonly actionPending: boolean;
  readonly disabledReason: string | null;
  readonly errorCode: string | null;
  readonly requestToken: number;
}
```

`MultiRenameStore` exposes readonly `state`, `canExecute`, and `canUndo` signals. `open(context)` resets to `[N]`/`[E]`, counter `1/1/1`, and schedules a preview unless disabled. `updateRules` merges immutable rules, clears operation state, immediately disables Start, and schedules a 250 ms preview. The response applies only when its token equals the latest token and the context is unchanged. Schedule a timer for the authoritative `expiresAt`; when it fires, immutably mark preview rows `stale`, set `canExecute` false, and show the stable expired-plan message. A newer preview replaces that timer. `close` clears debounce and expiry timers plus state. Parse safe API problem `code` values into form-level messages; preserve no physical text from arbitrary server failures.

Implement `execute()` and `undo()` API calls now, but leave pane refresh and dialog action wiring for Task 9. A successful Execute stores the operation and disables further rule edits; a successful Undo stores the Undone result and disables Undo.

- [ ] **Step 7: Run Angular tests and production build**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
```

Expected: all Angular tests pass and production bundle generation exits 0.

- [ ] **Step 8: Commit Angular transport/state**

```powershell
git add client/reach-commander-ui/src/app/core/api client/reach-commander-ui/src/app/core/state/multi-rename.models.ts client/reach-commander-ui/src/app/core/state/multi-rename-store.ts client/reach-commander-ui/src/app/core/state/multi-rename-store.spec.ts client/reach-commander-ui/src/app/app.spec.ts client/reach-commander-ui/src/app/core/state/commander-store.spec.ts
git commit -m "feat: add multi-rename client state"
```

---

### Task 8: `Ctrl+M`, ordered selection context, and live new-name preview UI

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-command.ts`
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.ts`
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/rename-mask-field.component.ts`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/rename-mask-field.component.html`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/rename-mask-field.component.scss`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/rename-mask-field.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-preview-table.component.ts`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-preview-table.component.html`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-preview-table.component.scss`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-preview-table.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/shared/components/name-diff/name-diff.component.ts`
- Create: `client/reach-commander-ui/src/app/shared/components/name-diff/name-diff.component.html`
- Create: `client/reach-commander-ui/src/app/shared/components/name-diff/name-diff.component.scss`
- Create: `client/reach-commander-ui/src/app/shared/components/name-diff/name-diff.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`

**Interfaces:**

- Adds: `CommanderCommand` variant `{ readonly type: 'multi-rename' }` mapped from `Ctrl+M`.
- Produces: `CommanderStore.createMultiRenameContext(side: PanelSide): MultiRenameContext | null`.
- Produces: accessible modal and preview components with stable `data-testid` hooks.
- Consumes: Task 7 `MultiRenameStore`; Task 9 completes Execute/Undo integration and focus restoration.

- [ ] **Step 1: Write failing keyboard and ordered-selection tests**

Add `Ctrl+M` to the keyboard table:

```typescript
['m', { ctrlKey: true }, { type: 'multi-rename' }],
```

Add CommanderStore tests:

```typescript
it('creates rename context in visible table order rather than Set insertion order', async () => {
  const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
  api.entries.set('downloads:/', [
    entry('zeta.txt'),
    { ...entry('Drafts'), type: 'directory', extension: null, size: null },
    entry('alpha.txt'),
  ]);
  const store = new CommanderStore(api);
  await store.initialize();
  store.selectWithPointer('left', 1, 'replace');
  store.selectWithPointer('left', 0, 'toggle');

  const context = store.createMultiRenameContext('left');

  expect(context?.entries.map(item => item.name)).toEqual(['Drafts', 'alpha.txt']);
  expect(context?.directoryPath).toBe('/');
});

it('uses the cursor item when there is no selection and excludes the parent row', async () => {
  const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
  api.entries.set('downloads:/Folder', [entry('one.txt')]);
  const store = new CommanderStore(api);
  await store.initialize();
  await store.navigateTo('left', '/Folder');
  store.moveCursor('left', 1);

  expect(store.createMultiRenameContext('left')?.entries.map(item => item.name)).toEqual(['one.txt']);
});
```

- [ ] **Step 2: Write failing component tests for complete new-name preview**

`NameDiffComponent`:

```typescript
it('renders the complete proposed filename with an accessible label', () => {
  fixture.componentRef.setInput('oldName', 'holiday-photo.jpg');
  fixture.componentRef.setInput('newName', 'Trip-001.jpg');
  fixture.detectChanges();

  const output: HTMLElement = fixture.nativeElement.querySelector('[data-testid="new-name"]');
  expect(output.textContent).toContain('Trip-001.jpg');
  expect(output.getAttribute('aria-label')).toBe('New filename: Trip-001.jpg');
  expect(output.querySelector('mark')?.textContent).toContain('Trip-001');
});
```

`MultiRenamePreviewTableComponent`:

```typescript
it('shows old and complete new filenames plus row status', () => {
  fixture.componentRef.setInput('rows', [previewRow('Trip-001.jpg', 'ready')]);
  fixture.detectChanges();

  expect(fixture.nativeElement.textContent).toContain('holiday-photo.jpg');
  expect(fixture.nativeElement.textContent).toContain('Trip-001.jpg');
  expect(fixture.nativeElement.textContent).toContain('Ready');
});
```

`MultiRenameDialogComponent`:

```typescript
it('renders the dense rule controls, preview table, and disabled Start state', () => {
  fakeStore.state.set(openState({ preview: previewResponse({ canExecute: false }) }));
  fakeStore.canExecute.set(false);
  fixture.detectChanges();

  const root: HTMLElement = fixture.nativeElement;
  expect(root.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true');
  expect(root.querySelector('[data-testid="name-mask"]')).toBeTruthy();
  expect(root.querySelector('[data-testid="extension-mask"]')).toBeTruthy();
  expect(root.querySelector('[data-testid="multi-rename-preview"]')).toBeTruthy();
  expect((root.querySelector('[data-testid="rename-start"]') as HTMLButtonElement).disabled).toBe(true);
});
```

The test provides `fakeStore` through dependency injection with writable test signals and spies matching the public `MultiRenameStore` interface; production state exposes no test-only mutators.

- [ ] **Step 3: Run Angular tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
Pop-Location
```

Expected: compilation fails because the command, context method, and components do not exist.

- [ ] **Step 4: Implement command mapping and context extraction**

Add `{ readonly type: 'multi-rename' }` and map lowercase `m` inside the existing Ctrl-only switch. Preserve input behavior: text controls continue to emit only Escape, so typing in rule fields cannot reopen the tool.

`createMultiRenameContext` must:

1. Resolve active tab and selected source metadata.
2. Call `buildVisibleRows(panel)`.
3. Exclude the parent row.
4. If selection is non-empty, filter visible rows by `selectedItems` while preserving row order.
5. Otherwise use the visible row at `cursorIndex` when it is real.
6. Return null when no eligible row exists.
7. Include source availability/read-only metadata without filtering symbolic links; the server preview must visibly reject them.

- [ ] **Step 5: Implement focused mask and name-diff components**

`RenameMaskFieldComponent` inputs: `label`, `testId`, `value`, and `tokens`; output: `valueChanged`. Keep an input `ViewChild`. A token button inserts at `selectionStart/selectionEnd`, emits the new value, restores focus, and positions the caret after the token. Provide valid starter tokens `[N]`, `[N1-3]`, `[E]`, `[E1-3]`, and `[C]` appropriate to each field; label the range examples “Range” and let the user edit their bounds.

`NameDiffComponent` computes the longest common prefix and suffix without overlapping, then renders prefix, `<mark>` changed middle, and suffix while exposing the complete new name through text, `aria-label`, and `title`. If the names are identical, render the complete new name without `<mark>`.

- [ ] **Step 6: Implement the preview table**

Use a dense native table with columns Old name, Ext, New name, Size, Modified, and Status. Reuse `FileSizePipe` and `DatePipe`. Rows receive `data-status` and `data-testid="rename-preview-row"`; the New-name cell uses `NameDiffComponent`. Status text and color cannot be the sole indicator. Keep a sticky header and independently scrollable body region.

- [ ] **Step 7: Implement the preview-focused modal workspace**

Use `CdkTrapFocus`/`cdkTrapFocusAutoCapture` from `@angular/cdk/a11y`. The template must contain:

```html
<div class="rename-backdrop" data-testid="multi-rename-dialog">
  <section
    class="rename-workspace"
    role="dialog"
    aria-modal="true"
    aria-labelledby="multi-rename-title"
    cdkTrapFocus
    [cdkTrapFocusAutoCapture]="true"
  >
    <header>
      <div>
        <strong id="multi-rename-title">Multi-Rename Tool</strong>
        <span>{{ context()?.sourceName }} · {{ context()?.directoryPath }}</span>
      </div>
      <button type="button" aria-label="Close Multi-Rename" (click)="closeRequested.emit()">×</button>
    </header>
    <div class="rules-grid">
      <fieldset>
        <legend>Rename mask: file name</legend>
        <app-rename-mask-field
          label="Name mask"
          testId="name-mask"
          [value]="store.state().rules.nameMask"
          [tokens]="nameTokens"
          (valueChanged)="setNameMask($event)"
        />
      </fieldset>
      <fieldset>
        <legend>Extension</legend>
        <app-rename-mask-field
          label="Extension mask"
          testId="extension-mask"
          [value]="store.state().rules.extensionMask"
          [tokens]="extensionTokens"
          (valueChanged)="setExtensionMask($event)"
        />
      </fieldset>
      <fieldset>
        <legend>Search &amp; Replace</legend>
        <label>Search for <input [value]="store.state().rules.searchFor" (input)="setSearchFor($any($event.target).value)" /></label>
        <label>Replace with <input [value]="store.state().rules.replaceWith" (input)="setReplaceWith($any($event.target).value)" /></label>
        <label><input type="checkbox" [checked]="store.state().rules.useRegex" (change)="setUseRegex($any($event.target).checked)" /> Regex</label>
        <label><input type="checkbox" [checked]="store.state().rules.matchCase" (change)="setMatchCase($any($event.target).checked)" /> Match case</label>
        <label><input type="checkbox" [checked]="store.state().rules.replaceInExtension" (change)="setReplaceInExtension($any($event.target).checked)" /> Include extension</label>
        <label>Case mode
          <select [value]="store.state().rules.caseMode" (change)="setCaseMode($any($event.target).value)">
            <option value="unchanged">Unchanged</option>
            <option value="lowercase">Lowercase</option>
            <option value="uppercase">Uppercase</option>
            <option value="capitalizeWords">Capitalize words</option>
            <option value="sentenceCase">Sentence case</option>
          </select>
        </label>
      </fieldset>
      <fieldset>
        <legend>Define counter [C]</legend>
        <label>Start at <input type="number" [value]="store.state().rules.counterStart" (input)="setCounterStart($any($event.target).valueAsNumber)" /></label>
        <label>Step by <input type="number" [value]="store.state().rules.counterStep" (input)="setCounterStep($any($event.target).valueAsNumber)" /></label>
        <label>Counter digits <input type="number" min="1" max="12" [value]="store.state().rules.counterDigits" (input)="setCounterDigits($any($event.target).valueAsNumber)" /></label>
      </fieldset>
    </div>
    @if (store.state().disabledReason; as reason) {
      <p role="status" data-testid="rename-disabled-reason">{{ reason }}</p>
    }
    @if (store.state().errorCode; as errorCode) {
      <p role="alert">{{ errorMessage(errorCode) }}</p>
    }
    <app-multi-rename-preview-table [rows]="store.state().preview?.rows ?? []" />
    <footer>
      <span role="status" aria-live="polite">{{ summary() }}</span>
      <button type="button" data-testid="rename-undo" [disabled]="!store.canUndo()">Undo</button>
      <button type="button" data-testid="rename-start" [disabled]="!store.canExecute()">Start</button>
      <button type="button" (click)="closeRequested.emit()">Close</button>
    </footer>
  </section>
</div>
```

Implement the named handlers as small calls to `MultiRenameStore.updateRules`. Inputs never two-way mutate readonly state. Add a pending indicator while `previewPending` or `actionPending` is true. CSS follows the screenshot's compact grouped layout but uses existing ReachCommander tokens, responsive collapse below 1000 px, and no native-window imitation.

- [ ] **Step 8: Open the modal from CommanderShell**

Inject `MultiRenameStore`, import the dialog component, handle `multi-rename` before ordinary Commander commands, and conditionally render the dialog. When context is null, set `commandStatus` to `"Select or focus an item before opening Multi-Rename."`. During this task, Close calls `multiRename.close()`; Task 9 adds focus restoration and pane refresh.

- [ ] **Step 9: Run Angular tests/build and commit**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
git add client/reach-commander-ui/src/app/core/keyboard client/reach-commander-ui/src/app/core/state/commander-store.ts client/reach-commander-ui/src/app/core/state/commander-store.spec.ts client/reach-commander-ui/src/app/features/multi-rename client/reach-commander-ui/src/app/shared/components/name-diff client/reach-commander-ui/src/app/features/commander/commander-shell
git commit -m "feat: build multi-rename preview workspace"
```

Expected: all Angular tests pass and the production build exits 0.

---

### Task 9: Execute/Undo interaction, pane refresh, focus, and recovery states

**Files:**

- Modify: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.html`
- Modify: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/multi-rename/multi-rename-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.spec.ts`

**Interfaces:**

- Produces: dialog output `filesystemChanged` carrying the origin `PanelSide` after successful Execute or Undo.
- Produces: `CommanderPanelComponent.focusPanel()` for modal focus restoration.
- Consumes: Task 7 `MultiRenameStore.execute/undo` and Task 8 modal/context.

- [ ] **Step 1: Write failing dialog action and shell-refresh tests**

Dialog tests:

```typescript
it('executes only an authoritative plan and exposes Undo after success', async () => {
  fakeStore.state.set(openState({ preview: previewResponse({ canExecute: true }) }));
  fakeStore.canExecute.set(true);
  fixture.detectChanges();

  fixture.nativeElement.querySelector('[data-testid="rename-start"]').click();
  await fixture.whenStable();
  fixture.detectChanges();

  expect(fakeStore.execute).toHaveBeenCalledOnce();
  expect(fixture.nativeElement.textContent).toContain('2 entries renamed');
  expect(fixture.nativeElement.querySelector('[data-testid="rename-undo"]').disabled).toBe(false);
});

it('blocks close while pending and requires acknowledgement for recovery-required results', () => {
  fakeStore.state.set(openState({
    actionPending: true,
    operation: operationResponse({ status: 'recoveryRequired', recoveryRequired: true }),
  }));
  fixture.detectChanges();

  expect(fixture.nativeElement.querySelector('[aria-label="Close Multi-Rename"]').disabled).toBe(true);
  expect(fixture.nativeElement.textContent).toContain('Recovery required');
});
```

Shell/store integration test:

```typescript
it('refreshes only the originating pane and restores its focus after rename', async () => {
  const rightPanelBefore = commanderStore.rightPanel();
  await component.handleRenameFilesystemChanged('left');

  expect(commanderStore.leftPanel().selectedItems.size).toBe(0);
  expect(api.listRequests.at(-1)).toEqual({ sourceId: 'downloads', path: '/' });
  expect(rightPanelBefore).toBe(commanderStore.rightPanel());
  component.closeMultiRename();
  await Promise.resolve();
  expect(leftPanel.focusPanel).toHaveBeenCalledOnce();
});
```

- [ ] **Step 2: Run Angular tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
Pop-Location
```

Expected: tests fail because dialog actions, recovery acknowledgement, and focus/refresh hooks are not wired.

- [ ] **Step 3: Wire Start, Undo, result rows, and Ctrl+Enter**

`start()` awaits `store.execute()`. On Completed it emits `filesystemChanged` once and leaves the dialog open with rule inputs locked and result rows visible. `undo()` awaits `store.undo()`, emits `filesystemChanged` on Undone, and disables Undo. Ctrl+Enter executes only when `store.canExecute()`; Enter in an input never executes. Escape requests close only when no preview/action request is pending.

Render the operation's per-row results using the same preview table structure and statuses. A RecoveryRequired result shows a persistent banner and a logical recovery table with Original, Intended, Current, and Status columns sourced from the safe operation-row fields. The close button remains disabled until the user checks `I have reviewed the recovery list`; never render an arbitrary server exception string.

- [ ] **Step 4: Refresh the origin pane and restore focus**

Add a root `ViewChild` to `CommanderPanelComponent` and:

```typescript
focusPanel(): void {
  this.panelRoot?.nativeElement.focus();
}
```

In the shell:

```typescript
async handleRenameFilesystemChanged(side: PanelSide): Promise<void> {
  this.store.clearSelection(side);
  await this.store.refresh(side);
}

closeMultiRename(): void {
  const side = this.multiRename.state().context?.panelSide ?? this.store.activePanel();
  this.multiRename.close();
  queueMicrotask(() =>
    (side === 'left' ? this.leftPanel : this.rightPanel)?.focusPanel());
}
```

Do not refresh the opposite pane or inactive tabs automatically. A user can use Ctrl+R there if the same directory is open elsewhere.

- [ ] **Step 5: Prevent background Commander commands while the modal is open**

At the beginning of `CommanderShellComponent.execute`, when Multi-Rename is open, handle only Escape and ignore other global Commander commands. The dialog's local key handler owns Ctrl+Enter and stops propagation. This prevents arrow keys, Tab, Insert, and printable characters outside rule inputs from mutating the hidden pane state.

- [ ] **Step 6: Update product status and command help accurately**

Replace `READ-ONLY FOUNDATION` with `CONTROLLED FILE OPERATIONS`. Add `Ctrl+M Multi-Rename` to the F9 command menu and global shortcut hint. Keep F4-F8 disabled and their future-command descriptions intact. Update command-bar tests to prove F4 remains disabled and F9 remains enabled; no F4 click may open Multi-Rename.

- [ ] **Step 7: Run accessibility-oriented Angular tests and build**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
```

Expected: all tests pass; dialog has one `role="dialog"`, a labelled title, trapped initial focus, keyboard-safe actions, status announcements, disabled reasons, and focus restoration.

- [ ] **Step 8: Commit the completed client interaction**

```powershell
git add client/reach-commander-ui/src/app/features/multi-rename client/reach-commander-ui/src/app/features/commander
git commit -m "feat: complete multi-rename execution workflow"
```

---

### Task 10: Writable acceptance fixtures, operational docs, and full release verification

**Files:**

- Create: `tests/e2e/specs/multi-rename.spec.ts`
- Modify: `tests/e2e/fixtures/sources.json`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Modify: `README.md`
- Verify: `config/sources.json`
- Verify: `compose.yaml`

**Interfaces:**

- Consumes the real single-origin application at `http://127.0.0.1:8092`.
- Produces repeatable browser acceptance for writable Execute/Undo, all-or-nothing conflict handling, and read-only denial.
- Leaves production sample sources/mounts read-only unless an administrator explicitly changes both layers.

- [ ] **Step 1: Write the failing Playwright acceptance scenarios**

```typescript
import { expect, test } from '@playwright/test';

test('previews complete names, renames mixed entries, and safely undoes', async ({ page }) => {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByText('Rename Lab', { exact: true }).dblclick();
  await expect(left.locator('.path-status')).toHaveText('/Rename Lab');
  await left.click();
  await page.keyboard.press('Control+A');
  await page.keyboard.press('Control+M');

  const dialog = page.getByTestId('multi-rename-dialog');
  await dialog.getByTestId('name-mask').fill('Archive-[C]');
  await dialog.getByLabel('Counter digits').fill('3');

  await expect(dialog.getByTestId('new-name')).toHaveText([
    'Archive-001',
    'Archive-002.jpg',
    'Archive-003.mp4',
  ]);
  await expect(dialog.getByTestId('rename-start')).toBeEnabled();
  await dialog.getByTestId('rename-start').click();
  await expect(dialog).toContainText('3 entries renamed');
  await expect(dialog.getByTestId('rename-undo')).toBeEnabled();

  await dialog.getByTestId('rename-undo').click();
  await expect(dialog).toContainText('Undo completed');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(left.getByText('Drafts', { exact: true })).toBeVisible();
  await expect(left.getByText('holiday-photo.jpg', { exact: true })).toBeVisible();
  await expect(left.getByText('holiday-video.mp4', { exact: true })).toBeVisible();
});

test('one conflict blocks the entire batch and leaves every entry unchanged', async ({ page }) => {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByText('Conflict Lab', { exact: true }).dblclick();
  await left.click();
  await page.keyboard.press('Control+A');
  await page.keyboard.press('Control+M');
  const dialog = page.getByTestId('multi-rename-dialog');
  await dialog.getByTestId('name-mask').fill('same');
  await dialog.getByTestId('extension-mask').fill('txt');

  await expect(dialog.getByText('Conflict')).toHaveCount(2);
  await expect(dialog.getByTestId('rename-start')).toBeDisabled();
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(left.getByText('one.txt', { exact: true })).toBeVisible();
  await expect(left.getByText('two.txt', { exact: true })).toBeVisible();
});

test('read-only sources explain why Multi-Rename cannot start', async ({ page }) => {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByTestId('source-archive').click();
  await left.getByText('locked.txt', { exact: true }).click();
  await page.keyboard.press('Control+M');

  const dialog = page.getByTestId('multi-rename-dialog');
  await expect(dialog).toContainText('read-only');
  await expect(dialog.getByTestId('rename-start')).toBeDisabled();
});
```

- [ ] **Step 2: Run Playwright and verify RED**

```powershell
Push-Location tests/e2e
npm test -- multi-rename.spec.ts
Pop-Location
```

Expected: tests fail because writable/read-only rename fixtures and/or production hooks are not complete.

- [ ] **Step 3: Seed deterministic writable and read-only sources**

Set E2E Downloads and Media to `readOnly: false`. Add an available Archive source with `readOnly: true` and `{{ARCHIVE_ROOT}}`. Extend `seed-fixtures.ts` to create:

```text
Downloads/
├── Rename Lab/
│   ├── Drafts/
│   ├── holiday-photo.jpg
│   └── holiday-video.mp4
└── Conflict Lab/
    ├── one.txt
    └── two.txt
Archive/
└── locked.txt
```

Map `archive` to its temporary root. Continue deleting only the generated temporary fixture root at teardown. Do not add a test-only API or weaken backend validation.

- [ ] **Step 4: Run the Playwright scenarios to GREEN**

```powershell
Push-Location tests/e2e
npm test -- multi-rename.spec.ts
npm test
Pop-Location
```

Expected: all Multi-Rename scenarios and the existing commander acceptance flow pass.

- [ ] **Step 5: Update README for controlled writes**

Document:

- Total Commander-inspired Multi-Rename purpose and screenshot-derived workflow.
- `Ctrl+M`, cursor fallback, selected-row ordering, and direct-child/mixed-entry scope.
- `[N]`, `[E]`, `[C]`, ranges, search/replace, regex, match case, extension replacement, casing, and counter semantics.
- Complete new-filename preview, conflict statuses, Start gating, two-phase execution, compensation, idempotency, and one-level Undo.
- `readOnly: false` plus operating-system permissions plus a writable bind mount are all required.
- A production override example using `/srv/rename-lab:/sources/rename-lab:rw`; never change existing mounts silently and never mount `/` or Docker socket.
- Crash/interruption limitation, logical recovery reporting, backups, trusted-network warning, and lack of authentication.
- New API routes and stable error codes.
- Backend, Angular, E2E, and publish commands.
- F4 remains a future single-item rename; date/time tokens, presets, plugins, persistent history, recursive rename, and symbolic links remain excluded.
- Roadmap marks this feature as Milestone 2A and leaves copy/move/mkdir/delete for subsequent reviewed slices.

- [ ] **Step 6: Run the fresh verification matrix**

Run in order and retain exit codes/output:

```powershell
dotnet test ReachCommander.slnx -c Release
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
dotnet publish src/ReachCommander.Api -c Release -o artifacts/publish
Push-Location tests/e2e
npm test
Pop-Location
docker build -t reachcommander:multi-rename .
docker compose config
docker compose up -d
docker compose ps
curl.exe --fail http://localhost:8092/health
curl.exe --fail http://localhost:8092/api/sources
docker compose down
git diff --check
git status --short
```

Expected: all tests/build/publish checks exit 0; the published `wwwroot/index.html` exists; the acceptance flow passes against temporary sources; available Docker checks pass. If Docker is absent, record the exact failed command and do not claim image/runtime verification.

- [ ] **Step 7: Perform final security and visual checks**

Search for physical-path leakage and mutation scope:

```powershell
rg -n "PhysicalPath|RootPath" client/reach-commander-ui/src src/ReachCommander.Api tests/e2e/specs
rg -n "Http(Post|Patch|Delete|Put)|Map(Post|Patch|Delete|Put)" src/ReachCommander.Api
```

Expected: client/API DTO code contains no physical path fields; only the three intended batch-rename POST routes are added. Capture a 1440×900 screenshot of the seeded dialog, inspect grouped controls, complete New-name column, conflicts, focus treatment, footer actions, and absence of overflow. Keep the image under ignored `artifacts/`.

- [ ] **Step 8: Commit acceptance and documentation**

```powershell
git add tests/e2e/specs/multi-rename.spec.ts tests/e2e/fixtures/sources.json tests/e2e/support/seed-fixtures.ts README.md
git commit -m "test: verify multi-rename workflow"
```

## Plan Self-Review Checklist

- Every approved spec requirement maps to Tasks 1-10.
- The browser never supplies a destination path to Execute or Undo.
- Rule names, enum strings, DTO fields, GUID identifiers, and method signatures match across .NET, API, Angular, and Playwright tasks.
- Every production slice begins with a focused failing test and records the expected RED reason.
- Complete new filenames are verified in backend, Angular, integration, and Playwright tests before mutation.
- Read-only, unavailable, traversal, non-child, symlink, stale, conflict, case-only, cycle, compensation, recovery-required, retry, and Undo behavior are explicit.
- Existing Docker/config defaults remain read-only; writable deployment remains opt-in.
- F4 and all unrelated Milestone 2 operations remain out of scope.
- No task relies on the developer's actual filesystem contents.
