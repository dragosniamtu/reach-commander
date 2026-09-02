# Batch Text Encoding Conversion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a safe batch Encoding tool that converts selected text files while preserving byte-exact `_original` backups.

**Architecture:** A new `TextEncodings` application boundary owns strict encoding analysis, short-lived preview plans, tracked background operations, and per-file transactional replacement under the existing directory mutation lock. Angular captures the active-panel selection, previews detection and output validity, polls conversion progress, and exposes the workflow through an accessible toolbar action and blocking dialog.

**Tech Stack:** ASP.NET Core 10 controllers and Problem Details, .NET 10 `System.Text.Encoding` code-page providers, SHA-256 fingerprints, Angular 22 standalone components/signals, Vitest, xUnit, and Playwright.

## Global Constraints

- Work directly on `master`; do not create a worktree or another branch.
- Preserve unrelated working-tree changes, including the media-preview pause fix and `NC-theme.png`; every commit below stages only its listed files.
- Recognized extensions are exactly `.srt`, `.sub`, `.txt`, `.csv`, `.nfo`, `.md`, and `.json`, matched case-insensitively.
- Accept at most 100 selected files and at most 32 MiB per file.
- Reject directories, symbolic links, read-only or unavailable sources, parent traversal, unsupported extensions, NUL-containing content, and binary-looking control-character distributions.
- Source choices are Auto, UTF-8, UTF-8 with BOM, UTF-16 LE, UTF-16 BE, Windows-1250, and Windows-1252.
- Output choices are UTF-8, UTF-8 with BOM, UTF-16 LE, Windows-1250, and Windows-1252; UTF-8 is the default.
- Strict decoders and encoders must never insert `?` or U+FFFD replacement characters.
- Auto detection prioritizes BOMs, then strict BOM-less UTF-8; when both Windows code pages decode successfully, choose Windows-1250 with low confidence and show a warning.
- Preserve text and line endings exactly; only the byte encoding and required BOM may change.
- Preserve original bytes as `<stem>_original<extension>`, then `<stem>_original (2)<extension>` through `(999)` without overwriting.
- Plans expire after 10 minutes; terminal operations remain available for one hour.
- Process files sequentially, allow cancellation only between per-file transactions, and keep the current transaction atomic or rolled back.
- Preview samples are safe logical data only and are bounded to 4 KiB of UTF-8 text per row; never expose physical paths or full file content.
- Authentication, antiforgery, source confinement, rate limiting, update-drain protection, and no-store API headers remain enabled.
- Use the existing `.reachcommander-operation-` reserved prefix for staging files and remove registered staging files older than 24 hours at startup.

---

## File Structure

### Backend application boundary

- `src/ReachCommander.Application/TextEncodings/TextEncodingModels.cs`: public enums, preview/operation records, and commands.
- `src/ReachCommander.Application/TextEncodings/ITextEncodingService.cs`: preview, execute, status, and cancellation interface.
- `src/ReachCommander.Application/TextEncodings/TextEncodingExceptions.cs`: stable safe exception codes/details.

### Backend infrastructure

- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingCodec.cs`: strict detection, decoding, binary guard, output validation, encoding, and bounded preview samples.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingFileSystem.cs`: snapshots, SHA-256 fingerprints, create-new staging writes, non-overwriting moves, rollback primitives, and directory flush.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingPlanStore.cs`: immutable 10-minute plans and bound operation IDs.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingPlanner.cs`: authoritative selection/path/source/size/extension validation and preview rows.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingOperationStore.cs`: thread-safe state machine, monotonic progress, cancellation token, per-file results, and one-hour pruning.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingExecutor.cs`: locked sequential conversion and one-file transaction/rollback.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingStagingRegistry.cs`: application-data manifests for crash cleanup.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingCleanupService.cs`: safe startup cleanup of registered staging files older than 24 hours.
- `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingService.cs`: capacity gate, supervised background execution, status, and cancellation.

### HTTP API

- `src/ReachCommander.Api/Contracts/TextEncodings/TextEncodingDtos.cs`: logical-path-only request/response mappings.
- `src/ReachCommander.Api/Controllers/TextEncodingsController.cs`: preview, execute, status, and cancel routes.
- `src/ReachCommander.Api/Errors/TextEncodingExceptionHandler.cs`: safe status/title/code mapping.

### Angular

- `client/reach-commander-ui/src/app/core/state/text-encoding.models.ts`: context, phase, and state types.
- `client/reach-commander-ui/src/app/core/state/text-encoding-store.ts`: preview debounce, execution polling, cancellation, completion callback, and reset.
- `client/reach-commander-ui/src/app/features/text-encoding/text-encoding-dialog.component.{ts,html,scss}`: accessible conversion workspace.
- Existing API, test fake, toolbar, shell, README, and E2E files receive focused integration edits.

---

### Task 1: Define encoding contracts and build the strict codec

**Files:**
- Create: `src/ReachCommander.Application/TextEncodings/TextEncodingModels.cs`
- Create: `src/ReachCommander.Application/TextEncodings/ITextEncodingService.cs`
- Create: `src/ReachCommander.Application/TextEncodings/TextEncodingExceptions.cs`
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingCodec.cs`
- Create: `tests/ReachCommander.UnitTests/TextEncodings/TextEncodingCodecTests.cs`

**Interfaces:**
- Consumes: .NET `Encoding`, `CodePagesEncodingProvider`, and the global encoding/size/sample constraints.
- Produces: `TextEncodingKind`, `TextEncodingConfidence`, `TextEncodingPreviewStatus`, `TextEncodingOperationState`, `TextEncodingRowResult`, public request/result records, `ITextEncodingService`, `TextEncodingException`, and internal `TextEncodingCodec.Analyze`/`Encode` methods.

- [ ] **Step 1: Write failing codec contract tests**

Create `TextEncodingCodecTests.cs` with individual facts for BOM UTF-8, BOM-less UTF-8, UTF-16 LE/BE BOMs, ambiguous Romanian Windows-1250 fallback, manual Windows-1252 smart quotes, NUL rejection, excessive controls, and unrepresentable Windows-1250 output. Use assertions like:

```csharp
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
public void Analyze_rejects_output_that_would_replace_an_emoji()
{
    var error = Assert.Throws<TextEncodingException>(() => TextEncodingCodec.Analyze(
        Encoding.UTF8.GetBytes("Ready 😀"),
        TextEncodingKind.Auto,
        TextEncodingKind.Windows1250));

    Assert.Equal("text_output_unrepresentable", error.Code);
}
```

Use `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` in the test helper and strict exception fallbacks.

- [ ] **Step 2: Run the codec tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncodingCodecTests"
```

Expected: compilation fails because the `TextEncodings` contracts and `TextEncodingCodec` do not exist.

- [ ] **Step 3: Add the application records and service interface**

Define these exact public shapes in `TextEncodingModels.cs`:

```csharp
public enum TextEncodingKind
{
    Auto,
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
    Windows1250,
    Windows1252,
}

public enum TextEncodingConfidence { High, Medium, Low }
public enum TextEncodingPreviewStatus { Ready, Warning, Invalid }
public enum TextEncodingOperationState
{
    Queued,
    Running,
    CancelRequested,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}
public enum TextEncodingRowResult
{
    Pending,
    Converted,
    Skipped,
    Failed,
    RecoveryRequired,
}

public sealed record TextEncodingPreviewRequest(
    string SourceId,
    IReadOnlyList<string> FilePaths,
    TextEncodingKind SourceEncoding,
    TextEncodingKind OutputEncoding);

public sealed record TextEncodingPreviewRow(
    string FilePath,
    string FileName,
    TextEncodingKind? DetectedSourceEncoding,
    TextEncodingConfidence? Confidence,
    TextEncodingPreviewStatus Status,
    string? Code,
    string? Detail,
    string PreviewText);

public sealed record TextEncodingPreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TextEncodingPreviewRow> Rows,
    int ReadyCount,
    int WarningCount,
    int InvalidCount,
    bool CanExecute);

public sealed record TextEncodingOperationRow(
    string FilePath,
    string? BackupPath,
    TextEncodingRowResult Result,
    string? Code,
    string? Detail);

public sealed record TextEncodingOperation(
    Guid OperationId,
    TextEncodingOperationState State,
    int CompletedFiles,
    int TotalFiles,
    double Percent,
    string? CurrentFileName,
    bool CanCancel,
    IReadOnlyList<TextEncodingOperationRow> Rows,
    string? ErrorCode,
    string? ErrorDetail);
```

Define `ITextEncodingService` with:

```csharp
ValueTask<TextEncodingPreview> PreviewAsync(TextEncodingPreviewRequest request, CancellationToken ct);
ValueTask<TextEncodingOperation> ExecuteAsync(Guid planId, CancellationToken ct);
ValueTask<TextEncodingOperation> GetAsync(Guid operationId, CancellationToken ct);
ValueTask<TextEncodingOperation> CancelAsync(Guid operationId, CancellationToken ct);
```

Define `TextEncodingException(string code, string publicDetail)` with `Code`, `PublicDetail`, and factories for invalid request, plan not found/expired, capacity reached, invalid source/output selection, and staging cleanup failure. Per-file validation failures remain row codes rather than thrown exceptions.

- [ ] **Step 4: Implement strict codec behavior**

In `TextEncodingCodec.cs`, register the code-page provider once, create strict UTF/code-page encodings, and return an internal record:

```csharp
internal sealed record TextEncodingAnalysis(
    TextEncodingKind SourceEncoding,
    TextEncodingConfidence Confidence,
    bool RequiresReview,
    string Text,
    byte[] OriginalBytes,
    string PreviewText);

internal static TextEncodingAnalysis Analyze(
    byte[] bytes,
    TextEncodingKind requestedSource,
    TextEncodingKind outputEncoding);

internal static byte[] Encode(string text, TextEncodingKind outputEncoding);
```

Detection must follow the four-step precedence in the global constraints. `Utf8Bom` and UTF-16 manual selections require their matching BOM; UTF-8 manual selection accepts BOM-less bytes only; Windows selections decode the complete byte array strictly. Validate text-likeness after decoding: reject any NUL and reject when disallowed C0/C1 controls other than tab/CR/LF exceed `Math.Max(4, text.Length / 100)`. Validate output by calling the strict encoder during `Analyze`. Build the preview by appending Unicode scalar values until adding the next scalar would make `Encoding.UTF8.GetByteCount(sample) > 4096`.

`Encode` emits no BOM for UTF-8/Windows outputs, prepends the UTF-8 BOM for `Utf8Bom`, and prepends the UTF-16 LE BOM for `Utf16LittleEndian`. Reject `Auto` and `Utf16BigEndian` as output kinds.

- [ ] **Step 5: Run codec tests and verify GREEN**

Run the Task 1 test command. Expected: all `TextEncodingCodecTests` pass with no warnings.

- [ ] **Step 6: Commit Task 1 selectively**

```powershell
git add src/ReachCommander.Application/TextEncodings src/ReachCommander.Infrastructure/TextEncodings/TextEncodingCodec.cs tests/ReachCommander.UnitTests/TextEncodings/TextEncodingCodecTests.cs
git commit -m "feat: add strict text encoding codec"
```

---

### Task 2: Create authoritative preview planning and expiring plans

**Files:**
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingFileSystem.cs`
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingPlanStore.cs`
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingPlanner.cs`
- Create: `tests/ReachCommander.UnitTests/TextEncodings/TextEncodingPlannerTests.cs`
- Create: `tests/ReachCommander.UnitTests/Support/TextEncodingTestFixture.cs`

**Interfaces:**
- Consumes: Task 1 `TextEncodingCodec`, `IPathSecurityService`, and configured `SourceDefinition` policy.
- Produces: immutable `StoredTextEncodingPlan`, `StoredTextEncodingEntry`, `TextFileFingerprint`, `ITextEncodingFileSystem`, and `TextEncodingPlanner.PreviewAsync` for Task 3 and Task 4.

- [ ] **Step 1: Write failing planner tests**

Use a real temporary source root behind a fixture path-security fake. Cover a mixed valid/invalid batch, read-only source, unavailable path, 101 files, a 32 MiB + 1 byte file, directory, reparse/symlink snapshot, unsupported extension, binary `.sub`, low-confidence warning, manual override, and 10-minute expiry. The core success assertion is:

```csharp
var preview = await fixture.Planner.PreviewAsync(new(
    "media",
    ["/TV/episode.srt", "/TV/notes.txt"],
    TextEncodingKind.Auto,
    TextEncodingKind.Utf8),
    CancellationToken.None);

Assert.True(preview.CanExecute);
Assert.Equal(2, preview.Rows.Count);
Assert.Equal(1, preview.WarningCount);
Assert.DoesNotContain(fixture.SourceRoot, JsonSerializer.Serialize(preview));
Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(10), preview.ExpiresAt);
```

- [ ] **Step 2: Run planner tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncodingPlannerTests"
```

Expected: compilation fails because planner/storage/filesystem types do not exist.

- [ ] **Step 3: Implement snapshots and fingerprints**

In `TextEncodingFileSystem.cs`, define:

```csharp
internal sealed record TextFileFingerprint(
    long Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes,
    string Sha256);

internal sealed record TextFileSnapshot(
    string LogicalPath,
    string PhysicalPath,
    string Name,
    string Extension,
    string LogicalDirectory,
    string PhysicalDirectory,
    bool IsSymbolicLink,
    TextFileFingerprint Fingerprint,
    byte[] Bytes);
```

`LocalTextEncodingFileSystem.ReadSnapshot` must use `FileStream` with `FileShare.Read`, fail before allocation when length exceeds 32 MiB, read exactly the declared length, re-read length/last-write/attributes after reading, and SHA-256 the bytes. Expose create-new staging write with `FlushAsync` plus `Flush(flushToDisk: true)`, `MoveFile` without overwrite, `DeleteFile`, `FileExists`, directory-name enumeration, and best-effort directory flush matching `LocalMediaPreviewFileSystem` behavior.

- [ ] **Step 4: Implement the plan store**

Store only logical/physical identity, fingerprint, detected source encoding, output encoding, and preview metadata—never the full bytes. Use these exact plan records:

```csharp
internal sealed record StoredTextEncodingEntry(
    string LogicalPath,
    string PhysicalPath,
    string LogicalDirectory,
    string PhysicalDirectory,
    string FileName,
    TextFileFingerprint Fingerprint,
    TextEncodingKind SourceEncoding,
    TextEncodingKind OutputEncoding,
    TextEncodingPreviewStatus Status);

internal sealed record StoredTextEncodingPlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    IReadOnlyList<StoredTextEncodingEntry> Entries,
    TextEncodingPreview Preview,
    Guid? BoundOperationId);
```

`TextEncodingPlanStore` caps at 128 plans, prunes expired entries on every access, throws `text_encoding_plan_not_found` or `text_encoding_plan_expired`, and atomically binds one operation ID so repeated Execute calls return the same operation rather than starting duplicate conversions.

- [ ] **Step 5: Implement preview planning**

`TextEncodingPlanner.PreviewAsync` validates non-null/non-empty requests, at most 100 paths, valid source/output enum combinations, and one common logical directory. Resolve every path independently through `IPathSecurityService.ResolveAsync`. Throw existing source/path exceptions for unavailable/read-only/forbidden requests. For individual file problems, return an `Invalid` row with one of:

```text
unsupported_text_extension
text_file_too_large
text_file_not_regular
text_symbolic_link_rejected
text_binary_content
text_decode_failed
text_output_unrepresentable
```

Map valid analyses to `Ready`; map `RequiresReview` to `Warning` with `legacy_encoding_review_required`. Create and store a plan containing only Ready/Warning entries. `CanExecute` is true when at least one row is Ready or Warning. Never include a physical path in a public record or validation detail.

- [ ] **Step 6: Run planner and codec tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncoding"
```

Expected: all Task 1 and Task 2 tests pass.

- [ ] **Step 7: Commit Task 2 selectively**

```powershell
git add src/ReachCommander.Infrastructure/TextEncodings/TextEncodingFileSystem.cs src/ReachCommander.Infrastructure/TextEncodings/TextEncodingPlanStore.cs src/ReachCommander.Infrastructure/TextEncodings/TextEncodingPlanner.cs tests/ReachCommander.UnitTests/TextEncodings/TextEncodingPlannerTests.cs tests/ReachCommander.UnitTests/Support/TextEncodingTestFixture.cs
git commit -m "feat: preview batch text encoding changes"
```

---

### Task 3: Implement tracked transactional conversion

**Files:**
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingOperationStore.cs`
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingExecutor.cs`
- Create: `tests/ReachCommander.UnitTests/TextEncodings/TextEncodingOperationStoreTests.cs`
- Create: `tests/ReachCommander.UnitTests/TextEncodings/TextEncodingExecutorTests.cs`
- Modify: `tests/ReachCommander.UnitTests/Support/TextEncodingTestFixture.cs`

**Interfaces:**
- Consumes: Task 2 plans, file snapshots/filesystem, `DirectoryMutationLock`, and Task 1 codec.
- Produces: `TextEncodingOperationStore.Create/GetRequired/RequestCancellation/MarkRunning/BeginFile/CompleteFile/MarkTerminal` and `TextEncodingExecutor.RunAsync(plan, operationId, cancellationToken)` for Task 4.

- [ ] **Step 1: Write failing operation-store tests**

Verify queued → running → completed, completed-with-errors, cancel-requested → cancelled, monotonic progress, safe filename bounding, terminal idempotence, cancellation token signaling, maximum 100 terminal records, and one-hour expiry. Assert 50% after one of two rows completes and `CanCancel` only in queued/running states.

- [ ] **Step 2: Run operation-store tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncodingOperationStoreTests"
```

Expected: compilation fails because `TextEncodingOperationStore` does not exist.

- [ ] **Step 3: Implement the thread-safe operation state machine**

Store mutable entries only behind one lock. Each entry owns a `CancellationTokenSource`, immutable total count, mutable completed count/current filename/rows, and terminal timestamp/sequence. `RequestCancellation` changes running/queued to `CancelRequested` and cancels the token. `CompleteFile` replaces exactly one Pending row, clears current filename, and increments completed count. Terminal selection is:

```csharp
Completed when every ready row is Converted;
CompletedWithErrors when at least one row is Failed or Skipped and none is RecoveryRequired;
Failed when the supervised batch fails before row-level reporting;
Cancelled when cancellation is observed between files;
Failed with text_encoding_recovery_required when any row requires recovery.
```

Snapshot percent is `total == 0 ? 100 : completed * 100d / total`, clamped to 100.

- [ ] **Step 4: Write failing executor tests**

Add tests proving:

1. Windows-1250 source becomes BOM-less UTF-8 and the backup equals the original bytes.
2. Existing `_original` and `_original (2)` choose `_original (3)`.
3. A fingerprint change after preview yields a `Skipped`/`text_file_stale` row and no mutation.
4. A staging write failure leaves the original untouched.
5. A publish failure restores the backup to the original name.
6. A rollback failure yields `RecoveryRequired` with logical names only.
7. Cancellation after the first file does not begin the second file.
8. The executor rejects a symlink introduced after preview.

Use an injectable fake filesystem that fails on named move/write calls; do not simulate rollback by mocking the executor itself.

- [ ] **Step 5: Run executor tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncodingExecutorTests"
```

Expected: compilation fails because `TextEncodingExecutor` does not exist.

- [ ] **Step 6: Implement per-file transactional execution**

`RunAsync` acquires `DirectoryMutationLock.AcquireManyAsync` for the distinct logical directories in the plan, marks the operation running, and loops entries sequentially. Before each row, observe cancellation and call `BeginFile`. Re-resolve with `IPathSecurityService`, reject physical-path changes/symlinks, and compare the complete fresh fingerprint to the plan.

For each valid row:

```text
strictly decode using StoredTextEncodingEntry.SourceEncoding
strictly encode using StoredTextEncodingEntry.OutputEncoding
write+flush .reachcommander-operation-encoding-<operationId>-<row>.partial with create-new
choose first free _original name through (999)
move original -> backup without overwrite
move staging -> original without overwrite
flush directory
record Converted with logical backup path
```

On failure before backup movement, delete staging and record `text_conversion_failed`. On failure after backup movement, delete staging and move backup back. If rollback succeeds, record `text_conversion_failed`; if rollback fails, record `RecoveryRequired`/`text_encoding_recovery_required` and stop the batch. Never catch `OperationCanceledException` inside an active file transaction after the original move; finish or roll back with `CancellationToken.None` first.

- [ ] **Step 7: Run all text-encoding unit tests**

Run the Task 2 combined command. Expected: codec, planner, operation-store, and executor suites all pass.

- [ ] **Step 8: Commit Task 3 selectively**

```powershell
git add src/ReachCommander.Infrastructure/TextEncodings/TextEncodingOperationStore.cs src/ReachCommander.Infrastructure/TextEncodings/TextEncodingExecutor.cs tests/ReachCommander.UnitTests/TextEncodings/TextEncodingOperationStoreTests.cs tests/ReachCommander.UnitTests/TextEncodings/TextEncodingExecutorTests.cs tests/ReachCommander.UnitTests/Support/TextEncodingTestFixture.cs
git commit -m "feat: convert text files transactionally"
```

---

### Task 4: Supervise background work, crash cleanup, and update blocking

**Files:**
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingStagingRegistry.cs`
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingCleanupService.cs`
- Create: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingService.cs`
- Create: `tests/ReachCommander.UnitTests/TextEncodings/TextEncodingServiceTests.cs`
- Create: `tests/ReachCommander.UnitTests/TextEncodings/TextEncodingCleanupServiceTests.cs`
- Modify: `src/ReachCommander.Infrastructure/TextEncodings/TextEncodingExecutor.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationProbe.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationProbeTests.cs`

**Interfaces:**
- Consumes: Task 2 planner/store, Task 3 executor/operation store, `AuthenticationDataPaths`, and update operation probing.
- Produces: concrete `ITextEncodingService`, registered staging manifests, safe cleanup, one active conversion capacity, and updater/source-management blocking while conversion is queued/running/cancel-requested.

- [ ] **Step 1: Write failing service supervision tests**

Test that Execute binds idempotently, returns Queued immediately, starts a supervised operation, enforces one active batch with `text_encoding_capacity_reached`, Get returns snapshots, Cancel signals the operation, terminal completion releases capacity, and an unexpected executor exception marks Failed without becoming unobserved.

- [ ] **Step 2: Run service tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncodingServiceTests"
```

Expected: compilation fails because `TextEncodingService` does not exist.

- [ ] **Step 3: Implement supervised service orchestration**

`TextEncodingService` delegates Preview to the planner. Execute locks a capacity gate, asks the plan store to bind a generated `Guid`, returns an existing operation if already bound, rejects when one operation is active, creates the Queued operation, and starts exactly one `Task.Run(() => RunSupervisedAsync(...), CancellationToken.None)`. The supervisor catches unexpected exceptions, logs only operation ID/type/HResult, marks a safe Failed result, and decrements active capacity in `finally`. Get and Cancel are synchronous operation-store snapshots wrapped in `ValueTask`.

- [ ] **Step 4: Write failing staging cleanup tests**

Test registry manifests with logical source/directory/staging name only, atomic manifest writes, deletion after normal completion, preservation before 24 hours, removal after 24 hours, rejection of a manifest whose filename lacks `.reachcommander-operation-encoding-`, safe handling of a stale/missing source, and no recursive source scan.

- [ ] **Step 5: Run cleanup tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncodingCleanupServiceTests"
```

Expected: compilation fails because registry and cleanup service do not exist.

- [ ] **Step 6: Implement the staging registry and startup cleanup**

Create one atomic JSON manifest per staging file beneath `<authentication-root>/text-encodings/staging`. A manifest contains:

```csharp
internal sealed record TextEncodingStagingRecord(
    Guid RecordId,
    string SourceId,
    string LogicalDirectory,
    string StagingName,
    DateTimeOffset CreatedAt);
```

The executor registers before creating staging and removes the manifest in `finally` after successful cleanup. `TextEncodingCleanupService.StartAsync` reads only those manifests, rejects malformed or non-private names, resolves the staging file through `ResolveChildAsync`, deletes it only when `CreatedAt <= now - 24 hours`, then removes the manifest. It logs safe record IDs/result codes without physical paths. Missing source/file is treated as already cleaned; malformed manifests are quarantined by renaming only inside the registry directory.

- [ ] **Step 7: Add active conversion to the system-update operation probe**

Extend `SystemUpdateOperationProbe` with optional `TextEncodingOperationStore? textEncodings = null` and return true when `textEncodings.HasActiveOperations()` after existing file/archive checks. Add tests for Queued, Running, CancelRequested, and terminal states. This prevents update or source-management mutation drain from interrupting a conversion transaction.

- [ ] **Step 8: Run Task 4 and all text-encoding tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TextEncoding|FullyQualifiedName~SystemUpdateOperationProbeTests"
```

Expected: all selected tests pass.

- [ ] **Step 9: Commit Task 4 selectively**

```powershell
git add src/ReachCommander.Infrastructure/TextEncodings src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationProbe.cs tests/ReachCommander.UnitTests/TextEncodings tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationProbeTests.cs
git commit -m "feat: supervise text encoding operations"
```

---

### Task 5: Expose authenticated, rate-limited HTTP endpoints

**Files:**
- Create: `src/ReachCommander.Api/Contracts/TextEncodings/TextEncodingDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/TextEncodingsController.cs`
- Create: `src/ReachCommander.Api/Errors/TextEncodingExceptionHandler.cs`
- Create: `tests/ReachCommander.IntegrationTests/TextEncodingsApiTests.cs`
- Modify: `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`
- Modify: `src/ReachCommander.Api/Program.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Modify: `tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ErrorContractTests.cs`

**Interfaces:**
- Consumes: Task 4 `ITextEncodingService` and all application records.
- Produces: `/api/text-encodings` REST contract, JSON string enums, Problem Details, DI registrations, and rate-limit policy for Angular Task 6.

- [ ] **Step 1: Write failing API integration tests**

Cover preview, execute/status polling, cancellation, byte-exact backup, logical-path-only bodies, mixed invalid rows, read-only/invalid path, stale plan row, plan expiry, authentication/antiforgery, unknown operation, and rate limit. A success scenario should:

```csharp
var original = StrictWindows(1250).GetBytes("Bună, ştii, ţară.\r\n");
File.WriteAllBytes(Path.Combine(directory, "episode.srt"), original);

var previewResponse = await client.PostAsJsonAsync("/api/text-encodings/preview", new
{
    sourceId = "media",
    filePaths = new[] { $"{logicalDirectory}/episode.srt" },
    sourceEncoding = "auto",
    outputEncoding = "utf8",
});
var preview = await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>();
var start = await client.PostAsync($"/api/text-encodings/{preview!.PlanId}/execute", null);
var operation = await PollTerminal(client, await start.Content.ReadFromJsonAsync<OperationResponse>());

Assert.Equal("completed", operation.State);
Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "episode_original.srt")));
Assert.Equal("Bună, ştii, ţară.\r\n", File.ReadAllText(Path.Combine(directory, "episode.srt"), new UTF8Encoding(false, true)));
```

- [ ] **Step 2: Run API tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj --filter "FullyQualifiedName~TextEncodingsApiTests"
```

Expected: 404 responses because the controller is absent.

- [ ] **Step 3: Add DTO mappings and controller routes**

Define DTOs mirroring public application records and map every row explicitly. Controller routes are exactly:

```text
POST /api/text-encodings/preview
POST /api/text-encodings/{planId:guid}/execute
GET  /api/text-encodings/operations/{operationId:guid}
POST /api/text-encodings/operations/{operationId:guid}/cancel
```

Apply `[EnableRateLimiting(AuthenticationConfiguration.TextEncodingPolicy)]` to all four actions. Mutating routes rely on the existing global antiforgery filter and mutation gate. Responses contain logical paths, safe details, and opaque GUIDs only.

- [ ] **Step 4: Add safe exception and rate-limit contracts**

Register `TextEncodingExceptionHandler` before `FileAccessExceptionHandler`. Map plan not found to 404, plan expired to 410, capacity to 429, invalid source/output/request to 422, and cleanup/internal conversion failures to 500. Add `TextEncodingPolicy = "text-encoding"` at 20 requests per IP per minute. Extend `OnRejected` path classification for `/api/text-encodings` with title `Text-encoding rate limit exceeded`, detail `Too many text-encoding requests were submitted. Try again later.`, and code `text_encoding_rate_limited`.

- [ ] **Step 5: Register infrastructure and cleanup service**

In `DependencyInjection.cs`, register `ITextEncodingFileSystem` as `LocalTextEncodingFileSystem`, then singleton `TextEncodingPlanStore`, `TextEncodingPlanner`, `TextEncodingOperationStore`, `TextEncodingStagingRegistry`, `TextEncodingExecutor`, and `ITextEncodingService` as `TextEncodingService`; register `TextEncodingCleanupService` as a hosted service. `TextEncodingCodec` remains static and is not registered. Register code pages without adding a NuGet package because .NET 10 already supplies `System.Text.Encoding.CodePages` in the shared framework/reference pack.

- [ ] **Step 6: Finish authorization/error/factory coverage**

Add the controller to `AuthorizationBoundaryTests` discovery expectations, assert stable Problem Details codes in `ErrorContractTests`, and configure the API factory with no lower production limits unless a test explicitly overrides them. Poll terminal integration operations with a five-second deadline and 25 ms delay; fail with the last safe response rather than sleeping unboundedly.

- [ ] **Step 7: Run integration and full backend tests**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj --filter "FullyQualifiedName~TextEncodingsApiTests|FullyQualifiedName~AuthorizationBoundaryTests|FullyQualifiedName~ErrorContractTests"
dotnet test ReachCommander.slnx
```

Expected: selected integration tests and all backend tests pass with zero failures.

- [ ] **Step 8: Commit Task 5 selectively**

```powershell
git add src/ReachCommander.Api/Contracts/TextEncodings src/ReachCommander.Api/Controllers/TextEncodingsController.cs src/ReachCommander.Api/Errors/TextEncodingExceptionHandler.cs src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs src/ReachCommander.Api/Program.cs src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.IntegrationTests
git commit -m "feat: expose text encoding conversion API"
```

---

### Task 6: Add Angular API contracts and conversion state

**Files:**
- Create: `client/reach-commander-ui/src/app/core/state/text-encoding.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/text-encoding-store.ts`
- Create: `client/reach-commander-ui/src/app/core/state/text-encoding-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Modify: `client/reach-commander-ui/src/app/testing/commander-api-test-base.ts`

**Interfaces:**
- Consumes: Task 5 JSON DTOs and existing `PanelSide`, `FileEntryDto`, and HTTP interceptor behavior.
- Produces: `TextEncodingContext`, `captureTextEncodingContext`, `TextEncodingStore`, and four `CommanderApiPort` methods for Task 7.

- [ ] **Step 1: Write failing API client tests**

Add tests that assert exact method, URL, and body for preview/execute/get/cancel. The port methods are:

```typescript
abstract previewTextEncoding(request: TextEncodingPreviewRequestDto): Promise<TextEncodingPreviewDto>;
abstract executeTextEncoding(planId: string): Promise<TextEncodingOperationDto>;
abstract getTextEncodingOperation(operationId: string): Promise<TextEncodingOperationDto>;
abstract cancelTextEncodingOperation(operationId: string): Promise<TextEncodingOperationDto>;
```

Extend `CommanderApiTestBase` with rejecting defaults so unrelated tests continue compiling.

- [ ] **Step 2: Run API client tests and verify RED**

From `client/reach-commander-ui`, run:

```powershell
npm test -- --watch=false --include=src/app/core/api/reach-commander-api.spec.ts
```

Expected: TypeScript compilation fails because text-encoding DTOs and port methods are absent.

- [ ] **Step 3: Add Angular transport contracts and client methods**

Use string unions matching server camelCase enum serialization:

```typescript
export type TextEncodingKind =
  'auto' | 'utf8' | 'utf8Bom' | 'utf16LittleEndian' | 'utf16BigEndian' |
  'windows1250' | 'windows1252';
export type TextEncodingOperationState =
  'queued' | 'running' | 'cancelRequested' | 'completed' |
  'completedWithErrors' | 'cancelled' | 'failed';
```

Define readonly DTO interfaces for every Task 1 public record. Implement exact Task 5 URLs with `encodeURIComponent` for IDs.

- [ ] **Step 4: Write failing context/store tests**

Test context capture from the active filesystem panel, disabled reasons for archive/unavailable/read-only/no recognized file, retention of mixed selected rows, default Auto → UTF-8 preview, 250 ms debounced setting changes, stale-response suppression, plan expiry, execute/poll every 500 ms, cancellation, terminal callback once, safe HTTP error mapping, and timer cleanup on close/destroy.

The context capture assertion should prove a selected `.srt` plus directory/unsupported file keeps all three rows for backend diagnostics while enabling because at least one recognized file exists.

- [ ] **Step 5: Run store tests and verify RED**

Run:

```powershell
npm test -- --watch=false --include=src/app/core/state/text-encoding-store.spec.ts
```

Expected: compilation fails because models/store do not exist.

- [ ] **Step 6: Implement context and store state machine**

Define:

```typescript
export type TextEncodingPhase =
  'closed' | 'previewing' | 'review' | 'starting' | 'running' |
  'cancelling' | 'completed' | 'completedWithErrors' | 'cancelled' | 'failed';

export interface TextEncodingContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
  readonly entries: readonly FileEntryDto[];
}
```

`captureTextEncodingContext(panelSide, panel, sources)` uses selected rows when non-empty or the focused row otherwise, requires a filesystem tab and writable/available source, and succeeds only when at least one non-directory filename has a recognized extension. It returns `{ context, error }` like archive extraction capture.

`TextEncodingStore` follows `ArchiveExtractionStore`: scheduler injection for deterministic tests, request tokens for stale preview suppression, 250 ms preview debounce, 500 ms operation polling, explicit `reviewAgain`, `execute`, `cancel`, `close`, and one completion handler. Convert operation state to UI phase without inventing client-side success. Closing/destroying invalidates every timer and response generation.

- [ ] **Step 7: Run Angular API/store tests**

Run both Task 6 test commands. Expected: both suites pass.

- [ ] **Step 8: Commit Task 6 selectively**

```powershell
git add client/reach-commander-ui/src/app/core/api client/reach-commander-ui/src/app/core/state/text-encoding.models.ts client/reach-commander-ui/src/app/core/state/text-encoding-store.ts client/reach-commander-ui/src/app/core/state/text-encoding-store.spec.ts client/reach-commander-ui/src/app/testing/commander-api-test-base.ts
git commit -m "feat: add text encoding client state"
```

---

### Task 7: Build the dialog and integrate the toolbar/shell

**Files:**
- Create: `client/reach-commander-ui/src/app/features/text-encoding/text-encoding-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/text-encoding/text-encoding-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/text-encoding/text-encoding-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/text-encoding/text-encoding-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Consumes: Task 6 context/store/DTOs and existing toolbar/modal/focus/theme patterns.
- Produces: visible Encoding toolbar action, blocking accessible dialog, progress/results UI, panel refresh, protected-state reset, and focus restoration.

- [ ] **Step 1: Write failing dialog tests**

Test initial focus/trap/ARIA, source and output options, preview rows with confidence/status/sample, backup notice, pending/error regions, Convert enablement, progress/current filename, per-file terminal results, recovery-required acknowledgement, cancellation confirmation, Escape behavior, and narrow rendering contract. Assert preview text is rendered via Angular interpolation/text content and never `[innerHTML]`.

- [ ] **Step 2: Run dialog tests and verify RED**

Run:

```powershell
npm test -- --watch=false --include=src/app/features/text-encoding/text-encoding-dialog.component.spec.ts
```

Expected: test discovery/compilation fails because the component does not exist.

- [ ] **Step 3: Implement the accessible conversion workspace**

Use a full-viewport backdrop, `role="dialog"`, `aria-modal="true"`, `cdkTrapFocus`, a heading `Change text encoding`, and initial focus on the source selector. Render selectors only while no operation is active. The preview table columns are File, Source encoding, Confidence, Status, and Text preview. Samples use `<pre>{{ row.previewText }}</pre>`.

During running/cancelling, render:

```html
<progress [value]="store.state().operation?.percent ?? 0" max="100"></progress>
<span aria-live="polite">
  {{ store.state().operation?.completedFiles }} / {{ store.state().operation?.totalFiles }}
  · {{ store.state().operation?.currentFileName ?? 'Preparing next file…' }}
</span>
```

The footer provides Convert files, Cancel operation, Review again, and Close according to phase. Escape closes only review/terminal phases; during active work it asks `window.confirm('Cancel the text encoding operation?')` then calls cancel. A recovery-required row must be acknowledged before Close.

- [ ] **Step 4: Write failing toolbar and shell integration tests**

Add `encodingDisabledReason` to `ActivePanelToolbarContext`, an `encodingRequested` output carrying the trigger element, and tests for available/read-only/archive/no-recognized-file states plus tooltip text. Shell tests must prove it captures the current selection, opens one dialog, blocks commander commands while open, resets on authentication loss, refreshes the captured panel once terminal, and restores focus to the toolbar Encoding button on close.

- [ ] **Step 5: Run toolbar/shell tests and verify RED**

Run:

```powershell
npm test -- --watch=false --include=src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: TypeScript/template failures because Encoding bindings do not exist.

- [ ] **Step 6: Integrate toolbar and shell**

Place the new toolbar button immediately after Multi-Rename with `data-testid="toolbar-text-encoding"`, visible text `Encoding`, a document/code-page SVG marked `aria-hidden="true"`, disabled wrapper tooltip, and emitted opener element.

In the shell:

- inject `TextEncodingStore` and import `TextEncodingDialogComponent`;
- compute encoding context/error from the active panel and pass the exact error to the toolbar;
- capture the opener, call `textEncoding.open(context)`, and clear other menu/status state;
- render `<app-text-encoding-dialog />` while phase is not closed;
- include the dialog in keyboard/modal blocking before panel commands;
- register completion to refresh the captured panel;
- close/reset on protected-state reset and restore focus to the connected toolbar trigger, falling back to the captured panel;
- never add an undocumented keyboard shortcut.

- [ ] **Step 7: Implement responsive/theme-safe styles and run component tests**

Use existing CSS custom properties for surfaces, borders, focus, success/warning/error colors, and fonts so Modern, Norton, and Windows 95 themes inherit correctly. Constrain samples with `white-space: pre-wrap`, `overflow-wrap: anywhere`, and a fixed maximum height. At widths below 720 px, stack controls and make the table horizontally scroll inside the dialog without widening the viewport. Respect `prefers-reduced-motion` and do not add continuous animation.

Run all three Task 7 test commands. Expected: all pass.

- [ ] **Step 8: Run full Angular tests and production build**

From `client/reach-commander-ui`, run:

```powershell
npm test -- --watch=false
npm run build
```

Expected: all Angular tests pass and the production build completes within configured budgets.

- [ ] **Step 9: Commit Task 7 selectively**

```powershell
git add client/reach-commander-ui/src/app/features/text-encoding client/reach-commander-ui/src/app/features/commander/active-panel-toolbar client/reach-commander-ui/src/app/features/commander/commander-shell
git commit -m "feat: add batch encoding conversion workspace"
```

---

### Task 8: Add browser acceptance, public documentation, and final verification

**Files:**
- Create: `tests/e2e/specs/text-encoding.spec.ts`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `docs/deployment/ubuntu.md`

**Interfaces:**
- Consumes: Tasks 1–7 complete feature.
- Produces: real-browser proof, byte-level filesystem proof, and public usage/deployment documentation.

- [ ] **Step 1: Seed deterministic text-encoding fixtures**

Create `Downloads/Encoding Lab` with:

- `romanian.srt` written from an explicit Windows-1250 byte array containing `Bună, ştii, ţară`;
- `notes.txt` in UTF-8;
- `binary.sub` containing NUL/control bytes;
- `photo.jpg` as an unsupported selection canary.

Keep using `REACHCOMMANDER_E2E_DOWNLOADS_ROOT` for post-operation byte assertions; do not expose a new server route or commit generated fixture bytes.

- [ ] **Step 2: Write the failing Playwright workflow**

The scenario navigates to Encoding Lab, selects the Romanian SRT and notes file, clicks Encoding, verifies low-confidence Windows-1250 plus correct Romanian preview, selects UTF-8, converts, waits for Completed, closes, and verifies both backup rows appear. Then read disk bytes through the exported downloads root and assert:

```typescript
expect(readFileSync(join(lab, "romanian_original.srt"))).toEqual(originalWindows1250);
expect(readFileSync(join(lab, "romanian.srt"), "utf8")).toContain("Bună, ştii, ţară");
expect(readFileSync(join(lab, "romanian.srt")).subarray(0, 3)).not.toEqual(Buffer.from([0xef, 0xbb, 0xbf]));
```

Add separate assertions that binary/unsupported-only selection disables the toolbar with a useful tooltip and that the dialog fits a 390×844 viewport without horizontal document overflow.

- [ ] **Step 3: Run the new browser test and fix only feature defects**

From `tests/e2e`, run:

```powershell
npm test -- specs/text-encoding.spec.ts
```

Expected: all text-encoding browser scenarios pass. Inspect failure screenshots/traces under `artifacts/playwright-results` if not.

- [ ] **Step 4: Document the feature**

Update README’s overview/feature/toolbar sections and deployment docs with:

- select supported text files in one writable active panel;
- click **Encoding**;
- review detected encoding and sample, override legacy input when needed;
- choose one of the five output encodings;
- originals are byte-exact `_original`, `_original (2)`, and later backups;
- strict conversion refuses replacement-character loss, binary files, symlinks, read-only sources, files over 32 MiB, and batches over 100;
- no additional Docker volume, host agent, or installer migration is required.

Do not claim automatic legacy detection is infallible; explicitly tell users to review low-confidence rows.

- [ ] **Step 5: Run complete verification**

From repository root:

```powershell
dotnet test ReachCommander.slnx -c Release
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run test:pwa
npm run build
npm run verify:pwa
Pop-Location
Push-Location tests/e2e
npm test
Pop-Location
git diff --check
git status --short
```

Expected: backend, Angular, PWA, production build, and all Playwright tests pass; `git diff --check` is silent. `git status --short` may still show the preserved pre-existing media-preview changes and `NC-theme.png`, but no generated build artifact or unplanned file may be staged.

- [ ] **Step 6: Commit Task 8 selectively**

```powershell
git add tests/e2e/specs/text-encoding.spec.ts tests/e2e/support/seed-fixtures.ts README.md docs/INSTALL.md docs/deployment/ubuntu.md
git commit -m "test: cover text encoding conversion workflow"
```

- [ ] **Step 7: Review the final feature diff without remote mutation**

```powershell
git log --oneline -8
git diff HEAD~8..HEAD --stat
git status --short
```

Confirm the diff contains only the encoding feature plus its approved design/plan commits and preserves unrelated local work. Do not push, tag, or publish a release unless the user explicitly requests those remote actions.
