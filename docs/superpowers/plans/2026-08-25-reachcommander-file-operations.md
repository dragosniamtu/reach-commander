# ReachCommander File Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add production-safe Copy, Move, Create Directory, managed Trash, Restore, permanent Delete, Empty Trash, queued progress, cancellation, and reconnectable UI workflows to ReachCommander.

**Architecture:** The ASP.NET Core backend exposes logical-path-only preview and mutation APIs backed by atomic JSON plans/jobs, one persistent FIFO `BackgroundService`, staged filesystem commits, and source-local managed Trash. Angular adds focused operation/trash stores, accessible dialogs, function-key commands, and a compact top-toolbar task surface restored after login or refresh through REST polling.

**Tech Stack:** .NET 10, ASP.NET Core controllers and `BackgroundService`, `System.Text.Json`, Angular 22.1 standalone components, TypeScript 6, RxJS/signals, Vitest, Playwright, xUnit, Docker/Linux and Windows CI.

## Global Constraints

- Execute directly on `master`; do not create a branch, worktree, or subagent.
- Preserve the unrelated untracked `NC-theme.png`; never stage, modify, or remove it.
- Do not push until the user explicitly requests a push.
- Use TDD for every behavior: add a focused failing test, observe its expected failure, implement the minimum behavior, then rerun focused and neighboring suites.
- Operate on selected non-parent entries in visible order, falling back to the focused non-parent entry when selection is empty.
- Default Copy/Move to the opposite panel's current filesystem folder and permit editing only its normalized logical destination path.
- Conflict choices are `overwrite`, `skip`, and `createUniqueName`; closing/cancelling submits nothing. Directory overwrite merges and preserves destination-only entries.
- Run exactly one Copy, Move, or long permanent-delete job at a time in persistent FIFO order.
- Persist logical paths only under the existing application-data root in `file-operations`; never persist, return, or normally log host paths.
- Queued jobs survive restart. `validating`, `running`, and `cancelling` become `interrupted`; partial file copying is never resumed.
- Keep the newest 100 terminal operation records; never expire valid Trash records automatically.
- Reuse authentication, antiforgery, `IPathSecurityService`, source policies, and `DirectoryMutationLock`.
- Copy may read a read-only source. Move, Delete, Restore, Empty Trash, and Create Directory require writable affected sources.
- Reject archives, source roots, parent rows, traversal, duplicate/nested selection, self/descendant destinations, symbolic links, junctions, and reparse points.
- Hide/reserve `/.reachcommander-trash/` and `/.reachcommander-operation-<operation-id>-*` in browse, upload, rename, extraction, and every mutation.
- Use destination-local staging and per-item overwrite quarantine. Cancellation preserves completed items and repairs only the current item.
- Delete a Move source only after destination commit; source deletion failure returns `move_source_not_removed` while preserving both copies.
- Permanent deletion displays exactly: `This deletion is permanent, cannot be undone, and is unrecoverable.`
- Managed Trash unavailability never silently falls back to permanent deletion.
- Eligible archive extraction retains F5 priority; otherwise F5 Copy, F6 Move, F7 MkDir, and F8 Delete.
- Progress starts as a blocking modal, backgrounds into the top toolbar, and restores when the compact task is clicked.
- Use REST polling; add no SignalR, database, external queue, or host shell command.
- Preserve both themes, PWA layouts, keyboard/touch interaction, focus trap/return, reduced motion, and accessible progress announcements.

---

## File and contract map

- `ReachCommander.Application/FileOperations`: logical public models, service interface, phases, outcomes, and stable exceptions.
- `ReachCommander.Infrastructure/FileOperations/Planning`: validation, fingerprints, recursion, conflicts, deterministic naming, and expiring plans.
- `ReachCommander.Infrastructure/FileOperations/Persistence`: atomic JSON documents, FIFO order, recovery, cancellation, and history.
- `ReachCommander.Infrastructure/FileOperations/Execution`: filesystem adapter, staging/quarantine journal, executor, progress, and cleanup.
- `ReachCommander.Infrastructure/Trash`: strict manifests, capability, Trash, Restore, permanent deletion, and Empty Trash.
- `ReachCommander.Api/Contracts`: DTO mapping and authenticated/antiforgery HTTP controllers.
- `client/reach-commander-ui/src/app/features/commander/file-operations` and `client/reach-commander-ui/src/app/features/commander/trash`: state, dialogs, task indicator, and toolbar/command integration.

### Task 1: Reserve and hide operation-owned namespaces

**Files:**
- Create: `src/ReachCommander.Infrastructure/FileOperations/ReservedFileOperationPathPolicy.cs`
- Modify: `src/ReachCommander.Infrastructure/FileSystem/PathSecurityService.cs`
- Modify: `src/ReachCommander.Infrastructure/FileSystem/LocalFileBrowser.cs`
- Modify: `src/ReachCommander.Infrastructure/Uploads/UploadFilenameValidator.cs`
- Modify: `src/ReachCommander.Infrastructure/BatchRenames/RenameNameValidator.cs`
- Modify: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveStagingWriter.cs`
- Test: `tests/ReachCommander.UnitTests/Files/ReservedFileOperationPathPolicyTests.cs`
- Test: `tests/ReachCommander.UnitTests/Files/PathSecurityServiceTests.cs`
- Test: `tests/ReachCommander.UnitTests/Files/LocalFileBrowserTests.cs`
- Test: `tests/ReachCommander.UnitTests/Uploads/UploadFilenameValidatorTests.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/RenameNameValidatorTests.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveStagingWriterTests.cs`

**Interfaces:**
- Consumes: existing logical-path normalization and filename validation.
- Produces: `ReservedFileOperationPathPolicy.IsReservedName(string)`, `ContainsReservedSegment(string)`, and `ThrowIfReservedName(string)`.

- [ ] **Step 1: Write failing policy and integration tests**

```csharp
[Theory]
[InlineData(".reachcommander-trash")]
[InlineData(".REACHCOMMANDER-TRASH")]
[InlineData(".reachcommander-operation-7b97-stage")]
public void IsReservedName_RejectsInternalNames(string name) =>
    Assert.True(ReservedFileOperationPathPolicy.IsReservedName(name));

[Theory]
[InlineData("/.reachcommander-trash/items")]
[InlineData("/movies/.reachcommander-operation-7b97-quarantine")]
public async Task ResolveAsync_RejectsReservedSegments(string path) =>
    await Assert.ThrowsAsync<InvalidLogicalPathException>(
        () => _security.ResolveAsync("media", path, CancellationToken.None));

[Fact]
public async Task ListAsync_HidesOperationOwnedEntries()
{
    Directory.CreateDirectory(Path.Combine(_root, ".reachcommander-trash"));
    Directory.CreateDirectory(Path.Combine(_root, ".reachcommander-operation-123-stage"));
    var result = await _browser.ListAsync("media", "/", null, null, default);
    Assert.DoesNotContain(result.Entries, x => x.Name.StartsWith(".reachcommander-", StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 2: Run tests and verify the expected failure**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~ReservedFileOperationPathPolicyTests|FullyQualifiedName~PathSecurityServiceTests|FullyQualifiedName~LocalFileBrowserTests|FullyQualifiedName~UploadFilenameValidatorTests|FullyQualifiedName~RenameNameValidatorTests|FullyQualifiedName~ArchiveStagingWriterTests"`

Expected: FAIL because the shared policy is missing and current entry points accept/expose internal names.

- [ ] **Step 3: Implement one case-insensitive policy and call it at every entry point**

```csharp
internal static class ReservedFileOperationPathPolicy
{
    internal const string TrashRootName = ".reachcommander-trash";
    internal const string OperationPrefix = ".reachcommander-operation-";

    internal static bool IsReservedName(string name) =>
        name.Equals(TrashRootName, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(OperationPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool ContainsReservedSegment(string logicalPath) =>
        logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(IsReservedName);

    internal static void ThrowIfReservedName(string name)
    {
        if (IsReservedName(name))
            throw new InvalidLogicalPathException("invalid_path", "The path uses a reserved ReachCommander name.");
    }
}
```

Call `ContainsReservedSegment` from all three path-security resolution methods, filter `LocalFileBrowser` entries with `IsReservedName`, and invoke `ThrowIfReservedName` before upload, rename, or extraction creates a destination. Later infrastructure accesses internal storage only after resolving source root `/`; no user path bypasses this policy.

- [ ] **Step 4: Run focused and neighboring tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~Files|FullyQualifiedName~Uploads|FullyQualifiedName~BatchRenames|FullyQualifiedName~Archives"`

Expected: PASS; ordinary dotted names remain valid.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/FileOperations/ReservedFileOperationPathPolicy.cs src/ReachCommander.Infrastructure/FileSystem/PathSecurityService.cs src/ReachCommander.Infrastructure/FileSystem/LocalFileBrowser.cs src/ReachCommander.Infrastructure/Uploads/UploadFilenameValidator.cs src/ReachCommander.Infrastructure/BatchRenames/RenameNameValidator.cs src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveStagingWriter.cs tests/ReachCommander.UnitTests
git commit -m "feat: reserve file operation namespaces"
```

### Task 2: Define logical operation, Trash, directory, and error contracts

**Files:**
- Create: `src/ReachCommander.Application/FileOperations/FileOperationModels.cs`
- Create: `src/ReachCommander.Application/FileOperations/IFileOperationService.cs`
- Create: `src/ReachCommander.Application/FileOperations/FileOperationExceptions.cs`
- Create: `src/ReachCommander.Application/Trash/TrashModels.cs`
- Create: `src/ReachCommander.Application/Trash/ITrashService.cs`
- Create: `src/ReachCommander.Application/Directories/DirectoryMutationModels.cs`
- Create: `src/ReachCommander.Application/Directories/IDirectoryMutationService.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/FileOperationContractTests.cs`

**Interfaces:**
- Consumes: existing `FileEntry` and `FileEntryType`.
- Produces: `IFileOperationService`, `ITrashService`, `IDirectoryMutationService`, records, enums, and sanitized exceptions.

- [ ] **Step 1: Add failing serialization and invariant tests**

```csharp
[Fact]
public void Status_UsesCamelCasePhaseAndContainsNoPhysicalPath()
{
    var json = JsonSerializer.Serialize(Samples.Status(FileOperationPhase.CompletedWithErrors), JsonOptions);
    Assert.Contains("\"phase\":\"completedWithErrors\"", json);
    Assert.DoesNotContain(Samples.PhysicalRoot, json, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void PermanentWarning_IsExact() => Assert.Equal(
    "This deletion is permanent, cannot be undone, and is unrecoverable.",
    PermanentDeleteConfirmation.Warning);
```

- [ ] **Step 2: Run the contract test and observe missing types**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter FullyQualifiedName~FileOperationContractTests`

Expected: FAIL at compile time.

- [ ] **Step 3: Add exact public models and service signatures**

```csharp
public enum FileOperationKind { Copy, Move, PermanentDelete, Trash, Restore, EmptyTrash }
public enum FileOperationConflictDecision { Overwrite, Skip, CreateUniqueName }
public enum FileOperationPhase { Queued, Validating, Running, Cancelling, Completed, CompletedWithErrors, Cancelled, Failed, Interrupted }
public enum FileOperationItemResult { Completed, Skipped, Failed, CopiedButNotRemoved, NotStarted }

public sealed record FileOperationPreviewRequest(FileOperationKind Kind, string SourceId, IReadOnlyList<string> LogicalPaths, string DestinationSourceId, string DestinationLogicalDirectory);
public sealed record FileOperationConflict(Guid ConflictId, string SourceLogicalPath, string DestinationLogicalPath, FileEntryType SourceType, FileEntryType DestinationType, IReadOnlyList<FileOperationConflictDecision> AllowedDecisions);
public sealed record FileOperationPreview(Guid PlanId, DateTimeOffset ExpiresAt, FileOperationKind Kind, string SourceId, IReadOnlyList<string> LogicalPaths, string DestinationSourceId, string DestinationLogicalDirectory, int TotalItems, long? TotalBytes, IReadOnlyList<FileOperationConflict> Conflicts, IReadOnlyList<string> Warnings);
public sealed record FileOperationConflictResolution(Guid ConflictId, FileOperationConflictDecision Decision);
public sealed record FileOperationSubmission(Guid PlanId, IReadOnlyList<FileOperationConflictResolution> Resolutions);
public sealed record FileOperationItemOutcome(string SourceId, string SourceLogicalPath, string? DestinationSourceId, string? DestinationLogicalPath, FileOperationItemResult Result, string? ErrorCode, string? Detail);
public sealed record FileOperationProgress(string? CurrentLogicalName, int CompletedItems, int TotalItems, long CompletedBytes, long? TotalBytes, double? Percentage, long? BytesPerSecond, TimeSpan Elapsed, TimeSpan? EstimatedRemaining);
public sealed record FileOperationStatus(Guid OperationId, FileOperationKind Kind, FileOperationPhase Phase, int QueuePosition, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, FileOperationProgress Progress, IReadOnlyList<FileOperationItemOutcome> Outcomes, IReadOnlyList<string> Warnings, bool Acknowledged);

public interface IFileOperationService
{
    Task<FileOperationPreview> PreviewAsync(FileOperationPreviewRequest request, CancellationToken cancellationToken);
    Task<FileOperationStatus> SubmitAsync(FileOperationSubmission request, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileOperationStatus>> ListAsync(CancellationToken cancellationToken);
    Task<FileOperationStatus> GetAsync(Guid operationId, CancellationToken cancellationToken);
    Task<FileOperationStatus> CancelAsync(Guid operationId, CancellationToken cancellationToken);
    Task AcknowledgeAsync(Guid operationId, CancellationToken cancellationToken);
}
```

Add the complete Trash and directory contracts:

```csharp
public enum DeleteMode { Trash, Permanent }
public sealed record DeletePreviewRequest(string SourceId, IReadOnlyList<string> LogicalPaths, DeleteMode Mode);
public sealed record DeletePreview(Guid PlanId, DateTimeOffset ExpiresAt, DeleteMode Mode, bool TrashAvailable, string? TrashUnavailableReason, int TotalItems, long? TotalBytes);
public sealed record DeleteSubmission(Guid PlanId, bool PermanentDeleteConfirmed);
public sealed record TrashEntry(Guid TrashId, string SourceId, string OriginalLogicalPath, string Name, FileEntryType Type, long? Size, DateTimeOffset DeletedAt);
public sealed record RestorePreviewRequest(IReadOnlyList<Guid> TrashIds);
public sealed record RestorePreview(Guid PlanId, DateTimeOffset ExpiresAt, IReadOnlyList<TrashEntry> Entries, IReadOnlyList<FileOperationConflict> Conflicts, IReadOnlyList<string> ParentsToCreate);
public sealed record RestoreSubmission(Guid PlanId, IReadOnlyList<FileOperationConflictResolution> Resolutions);
public sealed record TrashPermanentDeleteRequest(IReadOnlyList<Guid> TrashIds, bool PermanentDeleteConfirmed);
public sealed record EmptyTrashRequest(string? SourceId, bool PermanentDeleteConfirmed);
public sealed record CreateDirectoryRequest(string SourceId, string ParentLogicalPath, string Name);

public interface ITrashService
{
    Task<DeletePreview> PreviewDeleteAsync(DeletePreviewRequest request, CancellationToken cancellationToken);
    Task<FileOperationStatus> SubmitDeleteAsync(DeleteSubmission request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrashEntry>> ListAsync(string? sourceId, CancellationToken cancellationToken);
    Task<RestorePreview> PreviewRestoreAsync(RestorePreviewRequest request, CancellationToken cancellationToken);
    Task<FileOperationStatus> SubmitRestoreAsync(RestoreSubmission request, CancellationToken cancellationToken);
    Task<FileOperationStatus> PermanentlyDeleteAsync(TrashPermanentDeleteRequest request, CancellationToken cancellationToken);
    Task<FileOperationStatus> EmptyAsync(EmptyTrashRequest request, CancellationToken cancellationToken);
}

public interface IDirectoryMutationService
{
    Task<FileEntry> CreateAsync(CreateDirectoryRequest request, CancellationToken cancellationToken);
}
```

Create explicit sanitized exceptions/factories for these codes: `source_read_only`, `source_unavailable`, `destination_unavailable`, `invalid_operation_selection`, `invalid_directory_name`, `unsafe_symbolic_link`, `operation_plan_not_found`, `operation_plan_expired`, `operation_plan_stale`, `destination_conflict`, `insufficient_storage`, `operation_cancelled`, `operation_interrupted`, `move_source_not_removed`, `trash_unavailable`, `trash_manifest_invalid`, `trash_restore_conflict`, and `permanent_delete_confirmation_required`. No constructor accepts a physical path.

```csharp
public class FileOperationException(string code, string publicDetail) : Exception(publicDetail)
{
    public string Code { get; } = code;
    public string PublicDetail { get; } = publicDetail;
}

public sealed class TrashUnavailableException(string detail)
    : FileOperationException("trash_unavailable", detail);
public sealed class OperationPlanNotFoundException()
    : FileOperationException("operation_plan_not_found", "The operation plan was not found.");
public sealed class OperationPlanExpiredException()
    : FileOperationException("operation_plan_expired", "The operation plan has expired. Preview the operation again.");
public sealed class OperationPlanStaleException()
    : FileOperationException("operation_plan_stale", "Files changed after preview. Preview the operation again.");
public sealed class PermanentDeleteConfirmationRequiredException()
    : FileOperationException("permanent_delete_confirmation_required", PermanentDeleteConfirmation.Warning);
```

Use these factories for conditions that do not need a dedicated catch type:

```csharp
public static class FileOperationErrors
{
    public static FileOperationException SourceReadOnly() => new("source_read_only", "The source is read-only.");
    public static FileOperationException SourceUnavailable() => new("source_unavailable", "The source is unavailable.");
    public static FileOperationException DestinationUnavailable() => new("destination_unavailable", "The destination is unavailable.");
    public static FileOperationException InvalidSelection() => new("invalid_operation_selection", "The selected entries cannot be used for this operation.");
    public static FileOperationException InvalidDirectoryName() => new("invalid_directory_name", "The directory name is invalid.");
    public static FileOperationException UnsafeLink() => new("unsafe_symbolic_link", "Symbolic links and reparse points are not supported.");
    public static FileOperationException DestinationConflict() => new("destination_conflict", "The destination changed after preview.");
    public static FileOperationException InsufficientStorage() => new("insufficient_storage", "The destination does not have enough available storage.");
    public static FileOperationException Cancelled() => new("operation_cancelled", "The operation was cancelled.");
    public static FileOperationException Interrupted() => new("operation_interrupted", "The operation was interrupted by a server restart.");
    public static FileOperationException MoveSourceNotRemoved() => new("move_source_not_removed", "The item was copied but the source could not be removed.");
    public static FileOperationException TrashManifestInvalid() => new("trash_manifest_invalid", "The Trash record is invalid.");
    public static FileOperationException TrashRestoreConflict() => new("trash_restore_conflict", "The restore destination changed after preview.");
}
```

- [ ] **Step 4: Rerun the contract tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter FullyQualifiedName~FileOperationContractTests`

Expected: PASS, including reflection checks that public records contain no physical-path property.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Application/FileOperations src/ReachCommander.Application/Trash src/ReachCommander.Application/Directories tests/ReachCommander.UnitTests/FileOperations/FileOperationContractTests.cs
git commit -m "feat: define file operation contracts"
```

### Task 3: Implement deterministic naming and immutable planning

**Files:**
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/FileOperationEntryFingerprint.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/FileOperationPlan.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/UniqueNamePolicy.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/IFileOperationInspector.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/LocalFileOperationInspector.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/FileOperationPlanner.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/IFileOperationPlanStore.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/InMemoryFileOperationPlanStore.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/UniqueNamePolicyTests.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/FileOperationPlannerTests.cs`

**Interfaces:**
- Consumes: Task 1 policy, Task 2 preview contracts, `ISourceCatalog`, `IPathSecurityService`, and `TimeProvider`.
- Produces: `FileOperationPlanner.PreviewAsync`, `GetValidatedPlanAsync`, and immutable logical-only `FileOperationPlan`.

- [ ] **Step 1: Add failing planner and naming tests**

```csharp
[Fact]
public async Task DirectoryMerge_ReportsOnlyCollidingChild()
{
    _fixture.AddFile("media", "/Shows/Episode.mkv", 12);
    _fixture.AddFile("downloads", "/Shows/Episode.mkv", 7);
    _fixture.AddFile("downloads", "/Shows/Poster.jpg", 3);
    var result = await _planner.PreviewAsync(new(FileOperationKind.Copy, "media", ["/Shows"], "downloads", "/"), default);
    Assert.Single(result.Conflicts, x => x.DestinationLogicalPath == "/Shows/Episode.mkv");
    Assert.DoesNotContain(result.Conflicts, x => x.DestinationLogicalPath == "/Shows/Poster.jpg");
}

[Fact]
public void Find_InsertsSuffixBeforeFinalExtension()
{
    Assert.Equal("/target/file (3).txt", UniqueNamePolicy.Find("/target/file.txt", x => x is "/target/file.txt" or "/target/file (2).txt"));
    Assert.Equal("/target/Folder (2)", UniqueNamePolicy.Find("/target/Folder", x => x == "/target/Folder"));
}
```

Also test ordered input, duplicate/nested/root rejection, same/descendant destination, Copy from read-only, Move from read-only rejection, archives, links/reparse points, fingerprints, free-space estimate, and ten-minute expiry.

- [ ] **Step 2: Run tests and observe missing planner**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~UniqueNamePolicyTests|FullyQualifiedName~FileOperationPlannerTests"`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement the immutable logical plan**

```csharp
internal sealed record FileOperationEntryFingerprint(FileEntryType Type, long? Length, DateTimeOffset ModifiedAt, FileAttributes Attributes);
internal sealed record PlannedFileOperationEntry(string SourceLogicalPath, string DestinationLogicalPath, FileOperationEntryFingerprint Fingerprint, Guid? ConflictId, bool IsTopLevel);
internal sealed record FileOperationPlan(Guid PlanId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, FileOperationKind Kind, string? SourceId, string? DestinationSourceId, string? DestinationLogicalDirectory, IReadOnlyList<PlannedFileOperationEntry> Entries, IReadOnlyList<Guid> TrashIds, string? TrashSourceScope, IReadOnlyList<FileOperationConflict> Conflicts, IReadOnlyList<DirectoryMutationTarget> LockTargets, long? TotalBytes);

internal interface IFileOperationInspector
{
    Task<FileOperationEntryFingerprint> GetFingerprintAsync(string sourceId, string logicalPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListChildLogicalPathsAsync(string sourceId, string logicalDirectory, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string sourceId, string logicalPath, CancellationToken cancellationToken);
    Task<long?> GetAvailableBytesAsync(string sourceId, string logicalDirectory, CancellationToken cancellationToken);
}
```

Normalize/de-duplicate before inspection, recurse without following links, map destination children, treat directory/directory as merge, report file/type collisions, and store a plan expiring at `GetUtcNow().AddMinutes(10)`. `GetValidatedPlanAsync` throws `operation_plan_not_found`/`operation_plan_expired`. Unique candidates start at `(2)` in ascending order.

- [ ] **Step 4: Run planner, path, and lock tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~FileOperationPlannerTests|FullyQualifiedName~UniqueNamePolicyTests|FullyQualifiedName~PathSecurityServiceTests|FullyQualifiedName~DirectoryMutationLockTests"`

Expected: PASS; preview makes no filesystem mutations.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/FileOperations/Planning tests/ReachCommander.UnitTests/FileOperations/UniqueNamePolicyTests.cs tests/ReachCommander.UnitTests/FileOperations/FileOperationPlannerTests.cs
git commit -m "feat: plan safe file operations"
```

### Task 4: Persist plans, FIFO jobs, cancellation, recovery, and history

**Files:**
- Create: `src/ReachCommander.Infrastructure/FileOperations/Persistence/FileOperationDataPaths.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Persistence/PersistedFileOperationDocuments.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Persistence/FileOperationExecutionJournal.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Persistence/AtomicJsonFile.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Persistence/FileOperationRepository.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Persistence/FileOperationQueue.cs`
- Delete: `src/ReachCommander.Infrastructure/FileOperations/Planning/InMemoryFileOperationPlanStore.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Planning/JsonFileOperationPlanStore.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/AtomicJsonFileTests.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/FileOperationRepositoryTests.cs`

**Interfaces:**
- Consumes: Task 3 plans/store, Task 2 statuses, `AuthenticationDataPaths.RootPath`, and `TimeProvider`.
- Produces: repository `EnqueueAsync`, `TryTakeNextAsync`, `UpdateAsync`, `RequestCancellationAsync`, `RecoverAsync`, `ListAsync`, `GetAsync`, `AcknowledgeAsync`; queue `Signal`/`WaitAsync`.

- [ ] **Step 1: Add failing persistence/recovery tests**

```csharp
[Fact]
public async Task Recovery_PreservesQueuedOrderAndInterruptsUncertainJobs()
{
    await _repository.SaveForTestAsync(Job("first", FileOperationPhase.Running, 1));
    await _repository.SaveForTestAsync(Job("second", FileOperationPhase.Queued, 2));
    await _repository.SaveForTestAsync(Job("third", FileOperationPhase.Queued, 3));
    await _repository.RecoverAsync(default);
    Assert.Equal(FileOperationPhase.Interrupted, (await _repository.GetAsync(Id("first"), default)).Status.Phase);
    Assert.Equal(Id("second"), (await _repository.TryTakeNextAsync(default))!.OperationId);
    Assert.Equal(Id("third"), (await _repository.TryTakeNextAsync(default))!.OperationId);
}

[Fact]
public async Task History_RetainsNewestOneHundred()
{
    for (var index = 0; index < 101; index++) await _repository.SaveForTestAsync(TerminalJob(index));
    var records = await _repository.ListAsync(default);
    Assert.Equal(100, records.Count);
    Assert.DoesNotContain(records, x => x.OperationId == Id("0"));
}
```

Also test strict schema rejection, same-directory atomic replacement, queued cancellation without mutation, monotonic phases, acknowledgement, reload from a fresh instance, and absence of physical roots in JSON.

- [ ] **Step 2: Run persistence tests and observe missing repository**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~AtomicJsonFileTests|FullyQualifiedName~FileOperationRepositoryTests"`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement app-owned atomic JSON state**

```csharp
internal sealed record FileOperationDataPaths(string Root, string Plans, string Operations)
{
    internal static FileOperationDataPaths FromAuthenticationRoot(string authenticationRoot)
    {
        var root = Path.Combine(authenticationRoot, "file-operations");
        return new(root, Path.Combine(root, "plans"), Path.Combine(root, "operations"));
    }
}

internal sealed record FileOperationJournalEntry(string SourceId, string ParentLogicalPath, string OwnedName, string? PublicDestinationLogicalPath, bool IsQuarantine);
internal sealed record FileOperationExecutionJournal(Guid OperationId, IReadOnlyList<FileOperationJournalEntry> Entries);
internal sealed record FileOperationSubmissionApproval(IReadOnlyList<FileOperationConflictResolution> Resolutions, bool PermanentDeleteConfirmed);
internal sealed record PersistedFileOperationDocument(int SchemaVersion, long Sequence, FileOperationPlan Plan, FileOperationSubmissionApproval Approval, FileOperationStatus Status, bool CancellationRequested, FileOperationExecutionJournal? Journal);
```

`AtomicJsonFile.WriteAsync` writes `<target>.tmp-<guid>` in the same directory, serializes camel-case/string enums, flushes to disk, closes, then moves over the target; failure removes only that exact temp. Serialize repository access with `SemaphoreSlim`, order queued jobs by sequence, permit monotonic phases only, turn queued cancellation into `cancelled`, interrupt uncertain startup phases, and trim oldest terminal metadata beyond 100 without touching Trash.

- [ ] **Step 4: Run the tests twice to prove reload behavior**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~AtomicJsonFileTests|FullyQualifiedName~FileOperationRepositoryTests"`

Expected: PASS both times.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/FileOperations/Planning src/ReachCommander.Infrastructure/FileOperations/Persistence tests/ReachCommander.UnitTests/FileOperations/AtomicJsonFileTests.cs tests/ReachCommander.UnitTests/FileOperations/FileOperationRepositoryTests.cs
git commit -m "feat: persist file operation queue"
```

### Task 5: Execute staged Copy with conflicts, merge, progress, and rollback

**Files:**
- Create: `src/ReachCommander.Infrastructure/FileOperations/Execution/IFileOperationFileSystem.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Execution/LocalFileOperationFileSystem.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Execution/FileOperationProgressTracker.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Execution/FileOperationExecutor.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/FileOperationProgressTrackerTests.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/FileOperationExecutorCopyTests.cs`

**Interfaces:**
- Consumes: validated Task 3 plan/resolutions, Task 4 repository/journal, Task 1 prefix, path security, and `DirectoryMutationLock`.
- Produces: `Task<FileOperationStatus> FileOperationExecutor.ExecuteAsync(PersistedFileOperationDocument, CancellationToken)` with committed outcomes.

- [ ] **Step 1: Add failing Copy tests**

```csharp
[Fact]
public async Task OverwriteFailure_RestoresQuarantinedDestination()
{
    _fs.AddFile("source", "/movie.mkv", "new");
    _fs.AddFile("destination", "/movie.mkv", "old");
    _fs.FailWhenCommittingStaging("destination", "/movie.mkv");
    var result = await _executor.ExecuteAsync(CopyJob("/movie.mkv", "/movie.mkv", FileOperationConflictDecision.Overwrite), default);
    Assert.Equal("old", _fs.ReadText("destination", "/movie.mkv"));
    Assert.Empty(_fs.OperationOwnedEntries);
    Assert.Equal(FileOperationItemResult.Failed, Assert.Single(result.Outcomes).Result);
}

[Fact]
public async Task DirectoryMerge_PreservesDestinationOnlyEntry()
{
    _fs.AddFile("source", "/Shows/Episode.mkv", "episode");
    _fs.AddFile("destination", "/Shows/Poster.jpg", "poster");
    await _executor.ExecuteAsync(CopyDirectoryJob("/Shows", "/Shows"), default);
    Assert.Equal("poster", _fs.ReadText("destination", "/Shows/Poster.jpg"));
    Assert.Equal("episode", _fs.ReadText("destination", "/Shows/Episode.mkv"));
}
```

Add Skip, Create Unique Name, length verification, basic metadata, known insufficient capacity, new conflict during revalidation, current-stage cancellation, committed earlier item, monotonic progress, and lock-release tests.

- [ ] **Step 2: Run Copy tests and observe missing executor**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~FileOperationProgressTrackerTests|FullyQualifiedName~FileOperationExecutorCopyTests"`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement the filesystem boundary and staged algorithm**

```csharp
internal enum MoveAttempt { Moved, CrossDevice }

internal interface IFileOperationFileSystem
{
    FileOperationEntryFingerprint GetFingerprint(string physicalPath);
    IEnumerable<string> EnumerateChildren(string physicalDirectory);
    bool Exists(string physicalPath);
    void CreateDirectory(string physicalPath);
    Task<long> CopyFileAsync(string source, string destination, Func<long, CancellationToken, ValueTask> onBytes, CancellationToken cancellationToken);
    MoveAttempt TryMove(string source, string destination);
    void DeleteFile(string physicalPath);
    void DeleteDirectory(string physicalPath, bool recursive);
    void ApplyBasicMetadata(string source, string destination);
    long? GetAvailableBytes(string physicalDirectory);
}
```

For each file, re-resolve source/destination parent, create `.reachcommander-operation-<id>-stage-<guid>` in the destination directory, journal it, copy with 1 MiB buffers and cancellation/progress checks, flush, verify length, and apply basic metadata. Overwrite first renames destination to a journaled quarantine entry, restores it if current commit fails, and removes it after success. Merge directories incrementally; Skip on a top-level directory skips its entire tree; top-level Create Unique uses Task 3 naming.

`FileOperationProgressTracker` uses `TimeProvider`, never decreases values, returns null percentage for unknown totals, clamps known percentage to 0–100, and calculates rate/ETA only with positive elapsed/progress.

- [ ] **Step 4: Run Copy, planner, and lock tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~FileOperationExecutorCopyTests|FullyQualifiedName~FileOperationProgressTrackerTests|FullyQualifiedName~FileOperationPlannerTests|FullyQualifiedName~DirectoryMutationLockTests"`

Expected: PASS with no operation-owned debris after success, failure, or cancellation.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/FileOperations/Execution tests/ReachCommander.UnitTests/FileOperations/FileOperationProgressTrackerTests.cs tests/ReachCommander.UnitTests/FileOperations/FileOperationExecutorCopyTests.cs
git commit -m "feat: execute staged file copies"
```

### Task 6: Add Move, cancellation, and interrupted cleanup

**Files:**
- Modify: `src/ReachCommander.Infrastructure/FileOperations/Execution/LocalFileOperationFileSystem.cs`
- Modify: `src/ReachCommander.Infrastructure/FileOperations/Execution/FileOperationExecutor.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Execution/NativeMoveErrorClassifier.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/Execution/InterruptedOperationCleaner.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/FileOperationExecutorMoveTests.cs`
- Test: `tests/ReachCommander.UnitTests/FileOperations/InterruptedOperationCleanerTests.cs`

**Interfaces:**
- Consumes: Task 5 staged-copy primitives and Task 4 journal.
- Produces: same-filesystem atomic Move, cross-filesystem copy/commit/delete, `move_source_not_removed`, and allowlisted recovery.

- [ ] **Step 1: Add failing Move/recovery tests**

```csharp
[Fact]
public async Task CrossDeviceMove_DeletesSourceAfterDestinationCommit()
{
    _fs.AddFile("source", "/game.iso", "bytes");
    _fs.ReturnCrossDeviceForNextMove();
    await _executor.ExecuteAsync(MoveJob("/game.iso", "/game.iso"), default);
    Assert.False(_fs.Exists("source", "/game.iso"));
    Assert.Equal("bytes", _fs.ReadText("destination", "/game.iso"));
    Assert.Equal(["commit:/game.iso", "delete-source:/game.iso"], _fs.MutationOrder);
}

[Fact]
public async Task SourceDeleteFailure_ReportsCopiedButNotRemoved()
{
    _fs.FailSourceDelete("source", "/game.iso");
    var result = await _executor.ExecuteAsync(MoveJob("/game.iso", "/game.iso", true), default);
    var outcome = Assert.Single(result.Outcomes);
    Assert.Equal(FileOperationItemResult.CopiedButNotRemoved, outcome.Result);
    Assert.Equal("move_source_not_removed", outcome.ErrorCode);
    Assert.True(_fs.Exists("source", "/game.iso"));
    Assert.True(_fs.Exists("destination", "/game.iso"));
}
```

Also test directory merge, atomic overwrite quarantine, running/queued cancellation, valid cleanup, quarantine restoration, and refusal to touch malformed/out-of-scope/link journal paths.

- [ ] **Step 2: Run Move/recovery tests and observe missing behavior**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~FileOperationExecutorMoveTests|FullyQualifiedName~InterruptedOperationCleanerTests"`

Expected: FAIL.

- [ ] **Step 3: Implement precise Move and recovery behavior**

```csharp
internal static class NativeMoveErrorClassifier
{
    internal static bool IsCrossDevice(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() ? code == 17 : code == 18;
    }
}
```

`TryMove` returns `CrossDevice` only for this code and rethrows every other I/O error. After conflict preparation/revalidation, try atomic rename. On cross-device, run staged Copy and delete the source only after destination commit and source fingerprint revalidation. Deletion failure preserves both and reports `CopiedButNotRemoved`.

Recovery accepts only journal basenames beginning `.reachcommander-operation-<same-id>-`, under the journaled logical parent, with no link/reparse attributes. Restore quarantine only when public destination is absent, delete exact allowlisted staging entries, report cleanup failure, and never broadly recurse.

- [ ] **Step 4: Run all executor/repository tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~FileOperationExecutor|FullyQualifiedName~InterruptedOperationCleaner|FullyQualifiedName~FileOperationRepository"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/FileOperations/Execution tests/ReachCommander.UnitTests/FileOperations/FileOperationExecutorMoveTests.cs tests/ReachCommander.UnitTests/FileOperations/InterruptedOperationCleanerTests.cs
git commit -m "feat: execute and recover file moves"
```

### Task 7: Implement managed Trash, Restore, permanent deletion, and Create Directory

**Files:**
- Create: `src/ReachCommander.Infrastructure/Trash/TrashManifest.cs`
- Create: `src/ReachCommander.Infrastructure/Trash/TrashLayout.cs`
- Create: `src/ReachCommander.Infrastructure/Trash/TrashManifestStore.cs`
- Create: `src/ReachCommander.Infrastructure/Trash/TrashService.cs`
- Create: `src/ReachCommander.Infrastructure/Trash/ITrashOperationExecutor.cs`
- Create: `src/ReachCommander.Infrastructure/Trash/TrashOperationExecutor.cs`
- Create: `src/ReachCommander.Infrastructure/Directories/DirectoryMutationService.cs`
- Test: `tests/ReachCommander.UnitTests/Trash/TrashManifestStoreTests.cs`
- Test: `tests/ReachCommander.UnitTests/Trash/TrashServiceTests.cs`
- Test: `tests/ReachCommander.UnitTests/Directories/DirectoryMutationServiceTests.cs`

**Interfaces:**
- Consumes: Task 2 interfaces, Task 3 naming/preview, Task 5 staged copy, path security, source catalog, and mutation lock.
- Produces: managed Trash lifecycle, `Task<FileOperationStatus> ITrashOperationExecutor.ExecuteAsync(PersistedFileOperationDocument, CancellationToken)`, and synchronous single-directory creation.

- [ ] **Step 1: Add failing Trash/directory tests**

```csharp
[Fact]
public async Task TrashUnavailable_DoesNotFallBackToPermanentDelete()
{
    _fixture.CreateUnknownReservedCollision("media");
    var preview = await _service.PreviewDeleteAsync(new("media", ["/photo.jpg"], DeleteMode.Trash), default);
    Assert.False(preview.TrashAvailable);
    await Assert.ThrowsAsync<TrashUnavailableException>(() => _service.SubmitDeleteAsync(new(preview.PlanId, false), default));
    Assert.True(_fixture.Exists("media", "/photo.jpg"));
}

[Fact]
public async Task RestoreUnique_CommitsThenRemovesManifest()
{
    var trashId = await _fixture.TrashAsync("media", "/photo.jpg");
    _fixture.AddFile("media", "/photo.jpg", "existing");
    var preview = await _service.PreviewRestoreAsync(new([trashId]), default);
    await _service.SubmitRestoreAsync(new(preview.PlanId, [new(preview.Conflicts.Single().ConflictId, FileOperationConflictDecision.CreateUniqueName)]), default);
    Assert.True(_fixture.Exists("media", "/photo (2).jpg"));
    Assert.False(_fixture.ManifestExists(trashId));
}
```

Also test strict logical-only manifests, atomic/staged Trash moves, invalid-manifest isolation, newest-first filtering, missing-parent preview, all restore conflicts, confirmation binding, item deletion, scoped/all Empty Trash, no retention, read-only/link rejection, exact warning, one-level MkDir, and canaries.

- [ ] **Step 2: Run tests and observe missing services**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~TrashManifestStoreTests|FullyQualifiedName~TrashServiceTests|FullyQualifiedName~DirectoryMutationServiceTests"`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement strict source-local Trash and one-level MkDir**

```csharp
internal sealed record TrashManifest(int SchemaVersion, Guid TrashId, string SourceId, string OriginalLogicalPath, string OriginalName, FileEntryType Type, long? Size, DateTimeOffset DeletedAt, string StoredRelativeItemPath, FileOperationEntryFingerprint Fingerprint);

internal static class TrashLayout
{
    internal const string Root = ".reachcommander-trash";
    internal const string Manifests = "manifests";
    internal const string Items = "items";
    internal const string Staging = "staging";
}
```

Resolve source root `/`, build only exact layout children, and reject a pre-existing root without a valid ownership marker. Commit manifest after its item reaches `items/<trash-id>`. Listing requires item/manifest agreement and isolates invalid records. Restore previews parents/conflicts and removes manifest/item container only after destination commit. Permanent Trash deletion and Empty Trash require `PermanentDeleteConfirmed`; delete only IDs loaded from valid manifests, never glob or delete the root.

`DirectoryMutationService` validates writable filesystem parent, a single safe/reserved-free child name, no conflict/link, and holds `DirectoryMutationLock`; it creates only the exact child and returns logical `FileEntry`.

`TrashService` owns preview, capability, plan persistence, submission, listing, and queues `Trash`, `Restore`, `PermanentDelete`, or `EmptyTrash` jobs. `TrashOperationExecutor` owns their filesystem mutation. It consumes the persisted plan's `Entries`, `TrashIds`, `TrashSourceScope`, conflict `Resolutions`, and bound `PermanentDeleteConfirmed`; it updates the shared repository progress/outcomes exactly as the Copy/Move executor does.

- [ ] **Step 4: Run Trash, directory, executor, and reserved tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~Trash|FullyQualifiedName~DirectoryMutationService|FullyQualifiedName~FileOperationExecutor|FullyQualifiedName~ReservedFileOperationPathPolicy"`

Expected: PASS; no implicit deletion or automatic expiry.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/Trash src/ReachCommander.Infrastructure/Directories tests/ReachCommander.UnitTests/Trash tests/ReachCommander.UnitTests/Directories
git commit -m "feat: add managed trash and directory creation"
```

### Task 8: Wire the worker, services, HTTP contracts, and sanitized errors

**Files:**
- Create: `src/ReachCommander.Infrastructure/FileOperations/FileOperationService.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/FileOperationWorker.cs`
- Create: `src/ReachCommander.Infrastructure/FileOperations/FileOperationJobDispatcher.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Create: `src/ReachCommander.Api/Contracts/FileOperations/FileOperationDtos.cs`
- Create: `src/ReachCommander.Api/Contracts/Trash/TrashDtos.cs`
- Create: `src/ReachCommander.Api/Contracts/Directories/CreateDirectoryDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/FileOperationsController.cs`
- Create: `src/ReachCommander.Api/Controllers/TrashController.cs`
- Create: `src/ReachCommander.Api/Controllers/DirectoriesController.cs`
- Create: `src/ReachCommander.Api/Errors/FileOperationExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/Program.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Create: `tests/ReachCommander.IntegrationTests/FileOperationsApiTests.cs`
- Create: `tests/ReachCommander.IntegrationTests/TrashApiTests.cs`
- Create: `tests/ReachCommander.IntegrationTests/DirectoriesApiTests.cs`

**Interfaces:**
- Consumes: Tasks 2–7 services plus `FileOperationExecutor` and `ITrashOperationExecutor`.
- Produces: approved `/api/file-operations`, `/api/trash`, and `/api/directories` routes plus startup recovery.

- [ ] **Step 1: Add failing authenticated integration tests**

```csharp
[Fact]
public async Task CopyPreviewAndSubmit_ReturnAcceptedThenLogicalTerminalStatus()
{
    using var client = await _factory.CreateAuthenticatedClientAsync();
    var previewResponse = await client.PostAsJsonAsync("/api/file-operations/preview", new
    {
        kind = "copy", sourceId = "media", logicalPaths = new[] { "/photo.jpg" },
        destinationSourceId = "downloads", destinationLogicalDirectory = "/incoming"
    });
    previewResponse.EnsureSuccessStatusCode();
    var preview = await previewResponse.Content.ReadFromJsonAsync<FileOperationPreviewDto>();
    var submit = await client.PostAsJsonAsync("/api/file-operations", new { planId = preview!.PlanId, resolutions = Array.Empty<object>() });
    Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
    var status = await _factory.WaitForTerminalOperationAsync(client, await submit.Content.ReadFromJsonAsync<FileOperationStatusDto>());
    Assert.DoesNotContain(_factory.MediaPhysicalRoot, JsonSerializer.Serialize(status), StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task PermanentDelete_RequiresAntiforgery()
{
    using var client = await _factory.CreateAuthenticatedClientAsync(includeAntiforgery: false);
    var response = await client.PostAsJsonAsync("/api/trash", new { planId = Guid.NewGuid(), permanentDeleteConfirmed = true });
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}
```

Cover unauthenticated/missing antiforgery, traversal/reserved paths, read-only/unavailable/archive sources, stale plans, missing/duplicate resolutions, FIFO, cancel/acknowledge, MkDir, Trash/Restore/permanent/Empty lifecycle, supported-platform symlinks, stable Problem Details, and path redaction.

- [ ] **Step 2: Run API tests and observe 404 failures**

Run: `dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj --filter "FullyQualifiedName~FileOperationsApiTests|FullyQualifiedName~TrashApiTests|FullyQualifiedName~DirectoriesApiTests"`

Expected: FAIL with 404.

- [ ] **Step 3: Implement service submission, worker, DTOs, and controllers**

```csharp
internal sealed class FileOperationJobDispatcher(FileOperationExecutor files, ITrashOperationExecutor trash)
{
    internal Task DispatchAsync(PersistedFileOperationDocument job, CancellationToken cancellationToken) =>
        job.Plan.Kind is FileOperationKind.Copy or FileOperationKind.Move
            ? files.ExecuteAsync(job, cancellationToken)
            : trash.ExecuteAsync(job, cancellationToken);
}

internal sealed class FileOperationWorker(FileOperationRepository repository, FileOperationQueue queue, FileOperationJobDispatcher dispatcher, InterruptedOperationCleaner cleaner) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await repository.RecoverAsync(stoppingToken);
        await cleaner.CleanRecoveredOperationsAsync(stoppingToken);
        queue.Signal();
        while (!stoppingToken.IsCancellationRequested)
        {
            await queue.WaitAsync(stoppingToken);
            PersistedFileOperationDocument? job;
            while ((job = await repository.TryTakeNextAsync(stoppingToken)) is not null)
                await dispatcher.DispatchAsync(job, stoppingToken);
        }
    }
}
```

Register repository/planner/filesystem/executor/cleaner/Trash/directory/queue as singletons where state requires it and the same worker instance as hosted service. Build paths from `AuthenticationDataPaths.RootPath` so containers use `/data/file-operations`.

Controllers return 202 for queued submit, 200 for preview/status/list/cancel, 204 for acknowledge, and 201 for MkDir. Put `FileOperationExceptionHandler` before general file-access handling; map stable codes to 400/404/409/410/422/507 and emit sanitized Problem Details. Log operation ID, source ID, logical path, phase, and exception type only.

- [ ] **Step 4: Run the entire backend**

Run: `dotnet test ReachCommander.slnx`

Expected: PASS, including existing authentication/files/uploads/rename/archive tests.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure src/ReachCommander.Api tests/ReachCommander.IntegrationTests
git commit -m "feat: expose queued file operation APIs"
```

### Task 9: Extend the Angular API port

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`

**Interfaces:**
- Consumes: Task 8 camel-case DTOs and existing auth/antiforgery interceptor.
- Produces: typed client methods for operations, MkDir, and Trash lifecycle.

- [ ] **Step 1: Add failing HTTP client tests**

```typescript
it('submits a file operation', async () => {
  const promise = api.submitFileOperation({ planId, resolutions: [] });
  const request = http.expectOne('/api/file-operations');
  expect(request.request.method).toBe('POST');
  request.flush(operationStatus);
  await expectAsync(promise).toBeResolvedTo(operationStatus);
});

it('empties the selected trash scope', async () => {
  const promise = api.emptyTrash({ sourceId: 'media', permanentDeleteConfirmed: true });
  const request = http.expectOne('/api/trash');
  expect(request.request.method).toBe('DELETE');
  expect(request.request.body.sourceId).toBe('media');
  request.flush(operationStatus);
  await promise;
});
```

- [ ] **Step 2: Run the client spec and observe missing methods**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/core/api/reach-commander-api.spec.ts`

Expected: FAIL at TypeScript compile time.

- [ ] **Step 3: Add exact discriminants and port methods**

```typescript
export type FileOperationKind = 'copy' | 'move' | 'permanentDelete' | 'trash' | 'restore' | 'emptyTrash';
export type FileOperationConflictDecision = 'overwrite' | 'skip' | 'createUniqueName';
export type FileOperationPhase = 'queued' | 'validating' | 'running' | 'cancelling' | 'completed' | 'completedWithErrors' | 'cancelled' | 'failed' | 'interrupted';
export type FileOperationItemResult = 'completed' | 'skipped' | 'failed' | 'copiedButNotRemoved' | 'notStarted';

export interface FileOperationPreviewRequestDto { readonly kind: 'copy' | 'move'; readonly sourceId: string; readonly logicalPaths: readonly string[]; readonly destinationSourceId: string; readonly destinationLogicalDirectory: string; }
export interface FileOperationConflictDto { readonly conflictId: string; readonly sourceLogicalPath: string; readonly destinationLogicalPath: string; readonly sourceType: FileEntryType; readonly destinationType: FileEntryType; readonly allowedDecisions: readonly FileOperationConflictDecision[]; }
export interface FileOperationPreviewDto { readonly planId: string; readonly expiresAt: string; readonly kind: 'copy' | 'move'; readonly sourceId: string; readonly logicalPaths: readonly string[]; readonly destinationSourceId: string; readonly destinationLogicalDirectory: string; readonly totalItems: number; readonly totalBytes: number | null; readonly conflicts: readonly FileOperationConflictDto[]; readonly warnings: readonly string[]; }
export interface FileOperationSubmissionDto { readonly planId: string; readonly resolutions: ReadonlyArray<{ readonly conflictId: string; readonly decision: FileOperationConflictDecision }>; }
export interface FileOperationProgressDto { readonly currentLogicalName: string | null; readonly completedItems: number; readonly totalItems: number; readonly completedBytes: number; readonly totalBytes: number | null; readonly percentage: number | null; readonly bytesPerSecond: number | null; readonly elapsed: string; readonly estimatedRemaining: string | null; }
export interface FileOperationItemOutcomeDto { readonly sourceId: string; readonly sourceLogicalPath: string; readonly destinationSourceId: string | null; readonly destinationLogicalPath: string | null; readonly result: FileOperationItemResult; readonly errorCode: string | null; readonly detail: string | null; }
export interface FileOperationStatusDto { readonly operationId: string; readonly kind: FileOperationKind; readonly phase: FileOperationPhase; readonly queuePosition: number; readonly createdAt: string; readonly updatedAt: string; readonly progress: FileOperationProgressDto; readonly outcomes: readonly FileOperationItemOutcomeDto[]; readonly warnings: readonly string[]; readonly acknowledged: boolean; }
export interface CreateDirectoryRequestDto { readonly sourceId: string; readonly parentLogicalPath: string; readonly name: string; }
export interface DeletePreviewRequestDto { readonly sourceId: string; readonly logicalPaths: readonly string[]; readonly mode: 'trash' | 'permanent'; }
export interface DeletePreviewDto { readonly planId: string; readonly expiresAt: string; readonly mode: 'trash' | 'permanent'; readonly trashAvailable: boolean; readonly trashUnavailableReason: string | null; readonly totalItems: number; readonly totalBytes: number | null; }
export interface DeleteSubmissionDto { readonly planId: string; readonly permanentDeleteConfirmed: boolean; }
export interface TrashEntryDto { readonly trashId: string; readonly sourceId: string; readonly originalLogicalPath: string; readonly name: string; readonly type: FileEntryType; readonly size: number | null; readonly deletedAt: string; }
export interface RestorePreviewRequestDto { readonly trashIds: readonly string[]; }
export interface RestorePreviewDto { readonly planId: string; readonly expiresAt: string; readonly entries: readonly TrashEntryDto[]; readonly conflicts: readonly FileOperationConflictDto[]; readonly parentsToCreate: readonly string[]; }
export interface RestoreSubmissionDto { readonly planId: string; readonly resolutions: ReadonlyArray<{ readonly conflictId: string; readonly decision: FileOperationConflictDecision }>; }
export interface TrashPermanentDeleteRequestDto { readonly trashIds: readonly string[]; readonly permanentDeleteConfirmed: boolean; }
export interface EmptyTrashRequestDto { readonly sourceId: string | null; readonly permanentDeleteConfirmed: boolean; }

// Append these declarations to the existing class; preserve every existing API method.
export abstract class CommanderApiPort {
  abstract previewFileOperation(request: FileOperationPreviewRequestDto): Promise<FileOperationPreviewDto>;
  abstract submitFileOperation(request: FileOperationSubmissionDto): Promise<FileOperationStatusDto>;
  abstract listFileOperations(): Promise<ReadonlyArray<FileOperationStatusDto>>;
  abstract getFileOperation(operationId: string): Promise<FileOperationStatusDto>;
  abstract cancelFileOperation(operationId: string): Promise<FileOperationStatusDto>;
  abstract acknowledgeFileOperation(operationId: string): Promise<void>;
  abstract createDirectory(request: CreateDirectoryRequestDto): Promise<FileEntryDto>;
  abstract previewDelete(request: DeletePreviewRequestDto): Promise<DeletePreviewDto>;
  abstract submitDelete(request: DeleteSubmissionDto): Promise<FileOperationStatusDto>;
  abstract listTrash(sourceId?: string): Promise<ReadonlyArray<TrashEntryDto>>;
  abstract previewRestore(request: RestorePreviewRequestDto): Promise<RestorePreviewDto>;
  abstract submitRestore(request: RestoreSubmissionDto): Promise<FileOperationStatusDto>;
  abstract permanentlyDeleteTrash(request: TrashPermanentDeleteRequestDto): Promise<FileOperationStatusDto>;
  abstract emptyTrash(request: EmptyTrashRequestDto): Promise<FileOperationStatusDto>;
}
```

Add these methods to `ReachCommanderApi` and import every DTO used here; keep antiforgery in the existing interceptor:

```typescript
previewFileOperation(request: FileOperationPreviewRequestDto): Promise<FileOperationPreviewDto> {
  return firstValueFrom(this.http.post<FileOperationPreviewDto>('/api/file-operations/preview', request));
}
submitFileOperation(request: FileOperationSubmissionDto): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.post<FileOperationStatusDto>('/api/file-operations', request));
}
listFileOperations(): Promise<ReadonlyArray<FileOperationStatusDto>> {
  return firstValueFrom(this.http.get<ReadonlyArray<FileOperationStatusDto>>('/api/file-operations'));
}
getFileOperation(operationId: string): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.get<FileOperationStatusDto>(`/api/file-operations/${encodeURIComponent(operationId)}`));
}
cancelFileOperation(operationId: string): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.post<FileOperationStatusDto>(`/api/file-operations/${encodeURIComponent(operationId)}/cancel`, null));
}
acknowledgeFileOperation(operationId: string): Promise<void> {
  return firstValueFrom(this.http.post<void>(`/api/file-operations/${encodeURIComponent(operationId)}/acknowledge`, null));
}
createDirectory(request: CreateDirectoryRequestDto): Promise<FileEntryDto> {
  return firstValueFrom(this.http.post<FileEntryDto>('/api/directories', request));
}
previewDelete(request: DeletePreviewRequestDto): Promise<DeletePreviewDto> {
  return firstValueFrom(this.http.post<DeletePreviewDto>('/api/trash/preview', request));
}
submitDelete(request: DeleteSubmissionDto): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.post<FileOperationStatusDto>('/api/trash', request));
}
listTrash(sourceId?: string): Promise<ReadonlyArray<TrashEntryDto>> {
  const params = sourceId ? new HttpParams().set('sourceId', sourceId) : new HttpParams();
  return firstValueFrom(this.http.get<ReadonlyArray<TrashEntryDto>>('/api/trash', { params }));
}
previewRestore(request: RestorePreviewRequestDto): Promise<RestorePreviewDto> {
  return firstValueFrom(this.http.post<RestorePreviewDto>('/api/trash/restore/preview', request));
}
submitRestore(request: RestoreSubmissionDto): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.post<FileOperationStatusDto>('/api/trash/restore', request));
}
permanentlyDeleteTrash(request: TrashPermanentDeleteRequestDto): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.delete<FileOperationStatusDto>('/api/trash/items', { body: request }));
}
emptyTrash(request: EmptyTrashRequestDto): Promise<FileOperationStatusDto> {
  return firstValueFrom(this.http.delete<FileOperationStatusDto>('/api/trash', { body: request }));
}
```

- [ ] **Step 4: Run API/auth client tests**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/core/api/reach-commander-api.spec.ts --include=src/app/core/auth/authentication.interceptor.spec.ts`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/core/api/api.models.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts
git commit -m "feat: add file operation client API"
```

### Task 10: Build focused client stores for capture, preview, polling, and Trash

**Files:**
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/file-operation.models.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/file-operation.store.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/file-operation.store.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/trash.store.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/trash.store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander.store.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander.store.spec.ts`

**Interfaces:**
- Consumes: Task 9 API and existing panel state/refresh.
- Produces: immutable `CapturedFileOperationContext`, `FileOperationStore`, and `TrashStore`.

- [ ] **Step 1: Add failing capture/polling/reset tests**

```typescript
it('captures selected rows in visible order and ignores later selection', () => {
  commander.select('left', ['/b.txt', '/a.txt']);
  const captured = commander.captureFileOperationContext('copy');
  commander.select('left', ['/c.txt']);
  expect(captured?.logicalPaths).toEqual(['/a.txt', '/b.txt']);
  expect(captured?.destinationSourceId).toBe('downloads');
});

it('ignores a slow preview after destination changes', async () => {
  const first = deferred<FileOperationPreviewDto>();
  api.previewFileOperation.and.returnValues(first.promise, Promise.resolve(secondPreview));
  store.open('copy', context);
  store.setDestination('/old');
  store.setDestination('/new');
  first.resolve(firstPreview);
  await flushPromises();
  expect(store.preview()?.destinationLogicalDirectory).toBe('/new');
});

it('logout clears polling without cancelling server jobs', async () => {
  api.listFileOperations.and.resolveTo([runningStatus]);
  await store.restoreTasks();
  store.resetProtectedState();
  expect(store.tasks()).toEqual([]);
  expect(api.cancelFileOperation).not.toHaveBeenCalled();
});
```

Also test focus fallback, parent/root/archive rejection, Apply to remaining, unresolved conflicts, one 750 ms timer, modal/background/restore, terminal acknowledgement/refresh, queued count, Trash filtering/selection/restore, and timer teardown.

- [ ] **Step 2: Run store specs and observe missing classes**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/file-operations/file-operation.store.spec.ts --include=src/app/features/commander/trash/trash.store.spec.ts --include=src/app/features/commander/commander.store.spec.ts`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement immutable capture and signal stores**

```typescript
export interface CapturedFileOperationContext {
  readonly kind: 'copy' | 'move';
  readonly sourceId: string;
  readonly logicalPaths: ReadonlyArray<string>;
  readonly destinationSourceId: string;
  readonly destinationLogicalDirectory: string;
  readonly selectedNames: ReadonlyArray<string>;
  readonly knownTotalBytes: number | null;
}

export class FileOperationStore {
  readonly dialog = signal<'closed' | 'confirm' | 'progress'>('closed');
  readonly presentation = signal<'modal' | 'background'>('modal');
  readonly preview = signal<FileOperationPreviewDto | null>(null);
  readonly tasks = signal<ReadonlyArray<FileOperationStatusDto>>([]);
  readonly conflictDecisions = signal<ReadonlyMap<string, FileOperationConflictDecision>>(new Map());
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
}
```

Capture snapshots selected visible entries or focus and requires a writable opposite filesystem destination. Increment a preview sequence for each destination change and discard obsolete responses. Submit only after all conflicts have decisions. Poll every 750 ms while any unacknowledged job is nonterminal; restore after auth and refresh affected panels once on terminal transition. Reset clears state/timers without cancel.

`TrashStore` owns optional source filter, listing, selection, restore preview/resolutions, delete/empty confirmation, and passes returned jobs to `FileOperationStore.track`.

- [ ] **Step 4: Run store/reset tests**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/file-operations/file-operation.store.spec.ts --include=src/app/features/commander/trash/trash.store.spec.ts --include=src/app/features/commander/commander.store.spec.ts --include=src/app/core/auth/protected-state-reset.service.spec.ts`

Expected: PASS with fake timers drained.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/features/commander/file-operations client/reach-commander-ui/src/app/features/commander/trash client/reach-commander-ui/src/app/features/commander/commander.store.ts client/reach-commander-ui/src/app/features/commander/commander.store.spec.ts
git commit -m "feat: manage file operation client state"
```

### Task 11: Add Copy/Move confirmation, progress modal, and toolbar task

**Files:**
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/copy-move-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/copy-move-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/copy-move-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/copy-move-dialog.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-progress-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-progress-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-progress-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-progress-dialog.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-task-indicator.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-task-indicator.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-task-indicator.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/file-operations/transfer-task-indicator.component.spec.ts`

**Interfaces:**
- Consumes: Task 10 `FileOperationStore` signals/actions.
- Produces: accessible confirmation, blocking progress, and compact background task components.

- [ ] **Step 1: Add failing conflict/focus/background tests**

```typescript
it('applies one decision to all remaining conflicts', async () => {
  fixture.componentRef.setInput('preview', previewWithThreeConflicts);
  fixture.detectChanges();
  await user.selectOptions(screen.getByLabelText('Conflict action for movie.mkv'), 'createUniqueName');
  await user.click(screen.getByRole('checkbox', { name: 'Apply to remaining conflicts' }));
  expect(component.submitEnabled()).toBe(true);
  expect(component.resolutions().every(x => x.decision === 'createUniqueName')).toBe(true);
});

it('backgrounds progress and restores it from toolbar', async () => {
  await user.click(screen.getByRole('button', { name: 'Background' }));
  expect(store.presentation()).toBe('background');
  await user.click(screen.getByRole('button', { name: /Copy 42%/ }));
  expect(store.presentation()).toBe('modal');
});
```

Also test normalized destination, immutable summary, cancel-without-submit, percentage/indeterminate display, speed/elapsed/ETA, queue count, cancel/terminal/acknowledge, focus trap/return, live-region throttling, narrow layout, and themes.

- [ ] **Step 2: Run component specs and observe missing components**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/file-operations`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement accessible standalone components**

```html
<section role="dialog" aria-modal="true" aria-labelledby="copy-move-title" cdkTrapFocus [cdkTrapFocusAutoCapture]="true">
  <h2 id="copy-move-title">{{ context().kind === 'copy' ? 'Copy' : 'Move' }} selected items</h2>
  <p>{{ context().selectedNames.length }} item(s) from {{ context().sourceId }}</p>
  <label for="operation-destination">Destination path</label>
  <input id="operation-destination" [value]="destination()" (input)="destinationChanged($event)" />
  @for (conflict of preview()?.conflicts ?? []; track conflict.conflictId) {
    <label>Conflict action for {{ basename(conflict.destinationLogicalPath) }}
      <select (change)="decisionChanged(conflict.conflictId, $event)">
        <option value="">Choose an action</option>
        <option value="overwrite">Overwrite</option>
        <option value="skip">Skip</option>
        <option value="createUniqueName">Create Unique Name</option>
      </select>
    </label>
  }
  <button type="button" (click)="cancel.emit()">Cancel</button>
  <button type="button" [disabled]="!submitEnabled()" (click)="submit.emit(resolutions())">Start</button>
</section>
```

Use Angular CDK focus trap. Progress binds the selected task and exposes Cancel/Background. Indicator shows icon, Copy/Move, percentage/indeterminate, and queued count with one accessible summary. Announce phase and 10-percent boundaries only. Style with project variables, Norton overrides, narrow wrapping, and reduced motion.

- [ ] **Step 4: Run component and shell specs**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/file-operations --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

Expected: PASS; modal keystrokes do not leak.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/features/commander/file-operations
git commit -m "feat: add transfer dialogs and progress"
```

### Task 12: Add Delete, Trash, Restore, Empty Trash, and MkDir dialogs

**Files:**
- Create: `client/reach-commander-ui/src/app/features/commander/trash/delete-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/delete-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/delete-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/delete-dialog.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/trash-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/trash-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/trash-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/trash/trash-dialog.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/create-directory/create-directory-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/create-directory/create-directory-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/create-directory/create-directory-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/create-directory/create-directory-dialog.component.spec.ts`

**Interfaces:**
- Consumes: Task 9 API, Task 10 stores, exact warning.
- Produces: remaining mutation dialogs.

- [ ] **Step 1: Add failing warning/scope/validation/focus tests**

```typescript
it('requires permanent checkbox before irreversible confirmation', async () => {
  expect(screen.queryByText(PERMANENT_DELETE_WARNING)).toBeNull();
  await user.click(screen.getByRole('checkbox', { name: 'Permanent delete' }));
  expect(screen.getByText('This deletion is permanent, cannot be undone, and is unrecoverable.')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Delete forever' })).toBeEnabled();
});

it('labels filtered Empty Trash scope', () => {
  store.setSourceFilter('media');
  fixture.detectChanges();
  expect(screen.getByRole('button', { name: 'Empty Trash for media' })).toBeVisible();
});

it('creates one directory on Enter', async () => {
  await user.type(screen.getByLabelText('Directory name'), 'Family{Enter}');
  expect(api.createDirectory).toHaveBeenCalledWith({ sourceId: 'media', parentLogicalPath: '/Movies', name: 'Family' });
});
```

Also test default Trash, permanent-only reason, bounded name list/count, no fallback, filtering/multiselect, restore conflicts/missing parents, item delete, all-source Empty label, invalid/reserved names, busy/Escape, focus, themes, and touch layout.

- [ ] **Step 2: Run dialog specs and observe missing components**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/trash --include=src/app/features/commander/create-directory`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement confirmations and one-name form**

```typescript
export const PERMANENT_DELETE_WARNING =
  'This deletion is permanent, cannot be undone, and is unrecoverable.';

readonly permanentDelete = signal(false);
readonly confirmationReady = computed(() => this.permanentDelete() && this.preview()?.mode === 'permanent');

confirmDelete(): void {
  const preview = this.preview();
  if (!preview || (this.permanentDelete() && !this.confirmationReady())) return;
  this.confirm.emit({ planId: preview.planId, permanentDeleteConfirmed: this.permanentDelete() });
}
```

Default to Trash only when available; otherwise disable it, explain `trashUnavailableReason`, select permanent, and require separate warning/confirm. Trash dialog exposes filter, selection, Restore, permanent Delete, and exact one-source/all-sources Empty labels. Reuse Copy/Move conflict semantics for Restore.

MkDir shows captured parent, rejects separators, control characters, `.`, `..`, device names, and internal names before API; server remains authoritative. Enter submits only valid/not-busy; Escape closes only not-busy.

- [ ] **Step 4: Run dialog/store specs**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/trash --include=src/app/features/commander/create-directory --include=src/app/features/commander/file-operations/file-operation.store.spec.ts`

Expected: PASS with exact warning/scope.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/features/commander/trash client/reach-commander-ui/src/app/features/commander/create-directory
git commit -m "feat: add trash and directory dialogs"
```

### Task 13: Integrate function keys, toolbar, modal gating, and themes

**Files:**
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Consumes: Tasks 10–12 and existing archive extraction context.
- Produces: end-user F5/F6/F7/F8, Trash toolbar, and transfer task behavior.

- [ ] **Step 1: Add failing shortcut/availability tests**

```typescript
it('keeps eligible extraction ahead of Copy on F5', () => {
  shell.setArchiveExtractionContext(archiveContext);
  shell.handleWindowKeydown(keydown('F5'));
  expect(extractionStore.open).toHaveBeenCalled();
  expect(fileOperationStore.open).not.toHaveBeenCalled();
});

it('maps filesystem commands to F5 through F8', () => {
  shell.setActiveFilesystemSelection(['/movie.mkv']);
  shell.handleWindowKeydown(keydown('F5'));
  shell.handleWindowKeydown(keydown('F6'));
  shell.handleWindowKeydown(keydown('F7'));
  shell.handleWindowKeydown(keydown('F8'));
  expect(fileOperationStore.open).toHaveBeenCalledWith('copy', jasmine.any(Object));
  expect(fileOperationStore.open).toHaveBeenCalledWith('move', jasmine.any(Object));
  expect(shell.createDirectoryOpen()).toBe(true);
  expect(shell.deleteOpen()).toBe(true);
});
```

Also test RO Copy versus disabled Move/Delete/MkDir, archive destination/unavailable/root reasons, Trash button, indicator restore, terminal refresh preserving navigation state, logout reset, modal suppression, and both themes.

- [ ] **Step 2: Run shell/command specs and observe reserved behavior**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/commander/command-bar/command-bar.component.spec.ts --include=src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

Expected: FAIL because commands remain reserved/disabled.

- [ ] **Step 3: Wire computed availability and surfaces**

```typescript
export interface FileCommandAvailability {
  readonly copy: { enabled: boolean; reason: string | null; label: 'Copy' | 'Extract' };
  readonly move: { enabled: boolean; reason: string | null };
  readonly createDirectory: { enabled: boolean; reason: string | null };
  readonly delete: { enabled: boolean; reason: string | null };
}

handleFileCommand(command: 'copy' | 'move' | 'createDirectory' | 'delete'): void {
  if (this.hasBlockingModal()) return;
  if (command === 'copy' && this.archiveExtractionContext()) { this.openArchiveExtraction(); return; }
  if (command === 'copy' || command === 'move') this.openCopyMove(command);
  if (command === 'createDirectory') this.openCreateDirectory();
  if (command === 'delete') this.openDelete();
}
```

Replace disabled `Transfers` with the task indicator and add accessible Trash action. Render dialogs at shell root, add them to existing modal gate, and reset both stores inside the existing protected-state reset callback. Refresh affected sides through existing `CommanderStore.refresh` without changing path/filter/sort/tab/active side. Command bar takes computed availability, emits actions, uses F5 Copy/Extract label, and exposes specific disabled reasons. Style from existing variables with Norton/narrow/reduced-motion rules.

- [ ] **Step 4: Run Angular unit/build gates**

Run from `client/reach-commander-ui`: `npm test -- --watch=false`

Expected: PASS.

Run from `client/reach-commander-ui`: `npm run build`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/features/commander
git commit -m "feat: enable commander file operations"
```

### Task 14: Add browser acceptance, operations documentation, and final gates

**Files:**
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/specs/file-operations.spec.ts`
- Modify: `README.md`
- Create: `docs/INSTALL.md`
- Create: `docs/operations.md`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: Tasks 1–13 and existing authenticated E2E fixture.
- Produces: deterministic acceptance evidence and Windows/Ubuntu/container regression gates.

- [ ] **Step 1: Seed isolated trees and write failing E2E cases**

```typescript
test('copies multiple files, backgrounds progress, and restores task', async ({ page }) => {
  await commander.openSource(page, 'media', '/copy-source');
  await commander.openSource(page, 'downloads', '/copy-target', 'right');
  await commander.selectRows(page, ['alpha.bin', 'beta.bin']);
  await page.keyboard.press('F5');
  await page.getByRole('button', { name: 'Start' }).click();
  await page.getByRole('button', { name: 'Background' }).click();
  const task = page.getByRole('button', { name: /Copy .*%|Copy in progress/ });
  await expect(task).toBeVisible();
  await task.click();
  await commander.waitForTerminalOperation(page);
  await commander.expectEntries(page, 'right', ['alpha.bin', 'beta.bin']);
});

test('deletes to Trash and restores with unique name', async ({ page }) => {
  await commander.deleteWithF8(page, 'photo.jpg', { permanent: false });
  await page.getByRole('button', { name: 'Open Trash' }).click();
  await commander.selectTrashEntry(page, 'photo.jpg');
  await commander.restoreTrash(page, 'createUniqueName');
  await commander.expectEntry(page, 'left', 'photo (2).jpg');
});
```

Add Move, merge canary, Skip/Unique, cancellation/partial, FIFO, refresh restore, F7, warning, scoped Empty, RO/archive reasons, Norton theme, and byte-identical unrelated canaries.

- [ ] **Step 2: Run new E2E and capture first concrete failure**

Run from `tests/e2e`: `npm test -- --grep "file operations"`

Expected: FAIL only at missing fixture/helper/cross-layer wiring; record exact assertion before changing code.

- [ ] **Step 3: Complete deterministic fixtures, docs, and CI**

Seed separate Copy/Move/Trash/conflict/canary directories under temporary E2E sources. Use an integration-test-only throttled filesystem adapter for Background/Cancel/FIFO instead of host timing. Reset only temporary source/app-data roots.

Add this operator documentation:

```markdown
## File operations and managed Trash

ReachCommander supports Copy (F5), Move (F6), Create Directory (F7), and Delete (F8). Delete defaults to source-local `.reachcommander-trash` when that writable source can be safely owned. Trash never expires automatically; use the toolbar Trash action to Restore, permanently delete selected items, or Empty Trash.

Back up `/data` for operation metadata/account state, and back up source-local `.reachcommander-trash` when deleted files must remain recoverable. Uninstallers do not remove source-local Trash. Read-only sources may be copied from but cannot otherwise be mutated.
```

Run focused backend tests in existing Windows/Ubuntu jobs, Angular/E2E on Ubuntu, and extend amd64 container smoke with two temporary writable sources plus Copy/Trash/Restore API lifecycle and host-path redaction assertions.

- [ ] **Step 4: Run every local verification gate**

Run: `dotnet test ReachCommander.slnx`

Expected: PASS.

Run from `client/reach-commander-ui`: `npm test -- --watch=false`

Expected: PASS.

Run from `client/reach-commander-ui`: `npm run build`

Expected: PASS.

Run from `tests/e2e`: `npm test`

Expected: PASS.

Run: `docker build --platform linux/amd64 -t reach-commander:file-operations .`

Expected: PASS.

Run: `git -c safe.directory='D:/Work/Personal/Reach Commander' status --short`

Expected: only `?? NC-theme.png`.

- [ ] **Step 5: Commit without pushing**

```powershell
git add tests/e2e/support/seed-fixtures.ts tests/e2e/specs/file-operations.spec.ts README.md docs/INSTALL.md docs/operations.md .github/workflows/ci.yml
git commit -m "test: verify file operation workflows"
```

Report commit range, verification results, platform gates unavailable locally, and confirm `NC-theme.png` is untracked/untouched. Do not push.

## Final acceptance matrix

| Requirement | Evidence |
|---|---|
| F5/F6/F7/F8 and Extract priority | Angular command/shell unit tests and E2E |
| Editable opposite destination and immutable capture | Store tests and Copy/Move E2E |
| All conflict decisions and merge preservation | Planner/executor tests and conflict E2E |
| One durable FIFO, polling, refresh recovery, cancel | Repository/worker tests and queue/background/refresh E2E |
| Staging, quarantine, Move commit-before-delete | Executor tests and canaries |
| Managed Trash, Restore, permanent Delete, Empty | Trash unit/API tests and lifecycle E2E |
| Exact warning and no fallback | Contract/service/dialog tests |
| One validated MkDir | Directory unit/API tests and F7 E2E |
| RO/archive/link/traversal/reserved protections | Planner/path/API tests on Windows and Ubuntu |
| No physical-path disclosure | persistence/API/log/container assertions |
| Existing features/themes/PWA intact | full backend/Angular/E2E/build/container gates |
| Unselected data unchanged | byte-identical canaries across success/failure/cancel/recovery/Trash |
