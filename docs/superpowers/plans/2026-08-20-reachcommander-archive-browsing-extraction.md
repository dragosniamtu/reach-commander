# ReachCommander Archive Browsing and Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ZIP, RAR, and 7z archives—including supported multi-volume sets—browseable as read-only virtual folders and safely extract selected entries or a whole archive into the opposite writable filesystem panel.

**Architecture:** Domain and application contracts keep `FilesystemLocation` and `ArchiveLocation` separate. Infrastructure classifies archive filenames, resolves bounded same-directory volume sets, and delegates untrusted parsing to a one-shot bundled .NET worker using a framed standard-input/standard-output protocol. The API validates catalogs, creates expiring immutable extraction plans, streams worker output into destination-local staging, and commits with multi-directory locks and compensation. Angular adds archive-aware panel state and an accessible extraction review/progress dialog while retaining existing selection, tabs, filtering, and keyboard behavior.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core controller APIs and Problem Details, SharpCompress 0.50.4 in the isolated worker only, `System.Diagnostics.Process`, `System.IO.Pipelines`, built-in options validation and memory cache primitives, Angular 22 standalone components and Signals, Angular CDK A11y, xUnit, Vitest, Playwright, Docker, and GitHub Actions on Windows and Ubuntu.

## Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Use TDD for every production slice and observe the focused test fail for the expected missing behavior before implementation.
- Treat an archive extension as an interaction hint only. Opening or extracting must verify the signature, archive structure, and complete volume set server-side.
- Keep archive-internal paths out of `IPathSecurityService`, `Path.Combine`, `FileInfo`, `DirectoryInfo`, and every other host-filesystem API.
- Never follow symlinks, reparse points, hard-link metadata, archive link entries, device paths, rooted paths, drive-qualified paths, UNC paths, parent traversal, alternate data streams, or platform-reserved names.
- Reject any output component matching ReachCommander's `.reachcommander-extract-*.partial` staging-control namespace.
- Never invoke a shell, system `7z`, WinRAR, unrar, or a host agent. The bundled worker starts with `UseShellExecute = false`, receives physical volume paths over standard input rather than command-line arguments, opens no listener, and has no network function.
- Start a fresh worker for each inspection or extraction request and terminate it after exactly one completion/failure frame; never pool or reuse worker processes.
- The API process owns all staging files and final destination paths. The worker receives no destination path and cannot choose an output path.
- Do not use SharpCompress `WriteToDirectory` or any equivalent bulk extraction helper. Stream one validated entry at a time through the framed protocol.
- Reject encrypted headers or entries; do not accept, store, log, or prompt for passwords.
- Reject nested archive navigation. An archive file displayed inside an archive is an ordinary read-only file.
- Reject every detected destination conflict before execution and recheck immediately before finalization. Do not overwrite, skip, merge, or auto-rename.
- Preserve selected-directory roots, place selected files directly in the destination, and extract a whole unopened archive without an added wrapper folder.
- Hold the source containing-directory lock and destination-directory lock in deterministic order from final revalidation through staging, finalization, and cleanup. Keep final top-level moves non-cancellable and compensate a partial finalization.
- Enforce actual streamed-byte limits even when declared entry sizes are missing or false. Process solid RAR and 7z archives sequentially.
- Keep error responses stable and safe: logical source/path data is allowed; physical paths, process arguments, stack traces, library type names, and raw worker stderr are not.
- Preserve existing file browsing, secure upload, multi-rename, search, tabs, hardware metrics, read-only indicators, and keyboard behavior.
- Pin `SharpCompress` to exactly `0.50.4`; do not add it to the API or Infrastructure project.
- Before every commit, inspect `git status --short` and stage only the task's planned files.

## File Structure

```text
src/ReachCommander.Domain/
├── Archives/
│   ├── ArchiveEntry.cs
│   ├── ArchiveEntryType.cs
│   ├── ArchiveFormat.cs
│   └── ArchiveRole.cs
└── Files/FileEntry.cs

src/ReachCommander.Application/Archives/
├── ArchiveBrowseModels.cs
├── ArchiveExceptions.cs
├── ArchiveExtractionModels.cs
├── IArchiveBrowser.cs
└── IArchiveExtractionService.cs

src/ReachCommander.ArchiveProtocol/
├── ArchiveFrame.cs
├── ArchiveProtocolJsonContext.cs
├── ArchiveWorkerMessages.cs
└── ReachCommander.ArchiveProtocol.csproj

src/ReachCommander.ArchiveWorker/
├── Program.cs
├── ArchiveFrameReader.cs
├── ArchiveFrameWriter.cs
├── Properties/AssemblyInfo.cs
├── SharpCompressArchiveAdapter.cs
├── WorkerRequestDispatcher.cs
└── ReachCommander.ArchiveWorker.csproj

src/ReachCommander.Infrastructure/Archives/
├── ArchiveBrowser.cs
├── ArchiveOptions.cs
├── ArchiveOptionsValidator.cs
├── Classification/ArchiveFilenameClassifier.cs
├── Catalog/ArchiveCatalog.cs
├── Catalog/ArchiveCatalogBuilder.cs
├── Catalog/ArchiveCatalogCache.cs
├── Catalog/ArchiveCatalogProvider.cs
├── Catalog/IArchiveCatalogProvider.cs
├── Catalog/ArchivePathPolicy.cs
├── Extraction/ArchiveExtractionCoordinator.cs
├── Extraction/ArchiveExtractionOperationStore.cs
├── Extraction/ArchiveExtractionPlanStore.cs
├── Extraction/ArchiveExtractionPlanner.cs
├── Extraction/ArchiveExtractionService.cs
├── Extraction/ArchiveStagingWriter.cs
├── Volumes/ArchivePartResolver.cs
├── Volumes/ArchiveVolumeFingerprint.cs
├── Worker/ArchiveWorkerClient.cs
├── Worker/ArchiveWorkerProcess.cs
└── Worker/IArchiveWorkerClient.cs

src/ReachCommander.Api/
├── Contracts/Archives/ArchiveDtos.cs
├── Controllers/ArchivesController.cs
├── Controllers/ArchiveExtractionsController.cs
├── Errors/FileAccessExceptionHandler.cs
├── Program.cs
├── ReachCommander.Api.csproj
└── appsettings.json

client/reach-commander-ui/src/app/
├── core/api/api.models.ts
├── core/api/reach-commander-api.ts
├── core/state/archive-extraction.models.ts
├── core/state/archive-extraction-store.ts
├── core/state/commander.models.ts
├── core/state/commander-store.ts
├── core/state/panel-persistence.ts
├── features/archive-extraction/archive-extraction-dialog.component.{ts,html,scss,spec.ts}
└── features/commander/
    ├── active-panel-toolbar/active-panel-toolbar.component.{ts,html,spec.ts}
    ├── command-bar/command-bar.component.{ts,html,spec.ts}
    ├── commander-panel/commander-panel.component.{ts,html,spec.ts}
    ├── commander-shell/commander-shell.component.{ts,html,spec.ts}
    ├── directory-tabs/directory-tabs.component.{ts,html,spec.ts}
    ├── file-table/file-table.component.{ts,html,scss,spec.ts}
    └── path-bar/path-bar.component.{ts,html,spec.ts}

tests/
├── fixtures/archives/
│   ├── README.md
│   ├── generate-safe-fixtures.ps1
│   ├── nested.zip
│   ├── sample.7z
│   ├── sample.rar
│   ├── solid.rar
│   ├── encrypted.rar
│   ├── split.7z.001
│   ├── split.7z.002
│   ├── split.7z.003
│   ├── split.7z.004
│   ├── split.7z.005
│   ├── split.7z.006
│   ├── split.7z.007
│   ├── split.zip.001
│   ├── split.zip.002
│   ├── split.zip.003
│   ├── classic.z01
│   ├── classic.zip
│   ├── split.part01.rar
│   ├── split.part02.rar
│   ├── split.part03.rar
│   ├── split.part04.rar
│   ├── split.part05.rar
│   ├── split.part06.rar
│   ├── legacy.rar
│   ├── legacy.r00
│   ├── legacy.r01
│   ├── legacy.r02
│   ├── legacy.r03
│   ├── legacy.r04
│   └── legacy.r05
├── ReachCommander.UnitTests/Archives/
│   ├── ArchiveCatalogBuilderTests.cs
│   ├── ArchiveExtractionCoordinatorTests.cs
│   ├── ArchiveExtractionPlannerTests.cs
│   ├── ArchiveFilenameClassifierTests.cs
│   ├── ArchiveOptionsValidatorTests.cs
│   ├── ArchivePartResolverTests.cs
│   ├── ArchivePathPolicyTests.cs
│   ├── ArchiveCatalogCacheTests.cs
│   ├── ArchiveWorkerClientTests.cs
│   ├── ArchiveWorkerExtractionTests.cs
│   ├── ArchiveWorkerInspectionTests.cs
│   └── ArchiveWorkerProtocolTests.cs
├── ReachCommander.IntegrationTests/
│   ├── ArchiveBrowsingApiTests.cs
│   └── ArchiveExtractionsApiTests.cs
└── e2e/specs/archive-workflow.spec.ts
```

`ReachCommander.slnx`, `Dockerfile`, `.github/workflows/ci.yml`, `tests/e2e/support/seed-fixtures.ts`, and `README.md` also change for build, deployment, fixtures, CI, and operator documentation.

---

### Task 1: Classify archive candidates without parsing files

**Files:**

- Create: `src/ReachCommander.Domain/Archives/ArchiveFormat.cs`
- Create: `src/ReachCommander.Domain/Archives/ArchiveRole.cs`
- Modify: `src/ReachCommander.Domain/Files/FileEntry.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Classification/ArchiveFilenameClassifier.cs`
- Modify: `src/ReachCommander.Infrastructure/FileSystem/LocalFileBrowser.cs`
- Modify: `src/ReachCommander.Api/Contracts/FileEntryDto.cs`
- Modify: `src/ReachCommander.Api/Controllers/FilesController.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveFilenameClassifierTests.cs`
- Modify test: `tests/ReachCommander.UnitTests/Files/LocalFileBrowserTests.cs`
- Modify test: `tests/ReachCommander.IntegrationTests/FilesApiTests.cs`

**Interfaces:**

- Produces: optional `archiveFormatHint` and `archiveRole` fields on normal filesystem file entries.
- Produces: internal `ArchiveFilenameClassifier.Classify(string name, bool isLink)` for `LocalFileBrowser`.
- Consumed by: the Angular open/F5 routing in Tasks 7 and 11; neither field proves that a file is a valid archive.

- [ ] **Step 1: Write failing classifier tests**

```csharp
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Classification;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveFilenameClassifierTests
{
    [Theory]
    [InlineData("photos.zip", ArchiveFormat.Zip, ArchiveRole.Single)]
    [InlineData("photos.7z", ArchiveFormat.SevenZip, ArchiveRole.Single)]
    [InlineData("photos.rar", ArchiveFormat.Rar, ArchiveRole.Single)]
    [InlineData("photos.part01.rar", ArchiveFormat.Rar, ArchiveRole.Primary)]
    [InlineData("photos.part02.rar", ArchiveFormat.Rar, ArchiveRole.Secondary)]
    [InlineData("photos.r00", ArchiveFormat.Rar, ArchiveRole.Secondary)]
    [InlineData("photos.7z.001", ArchiveFormat.SevenZip, ArchiveRole.Primary)]
    [InlineData("photos.7z.002", ArchiveFormat.SevenZip, ArchiveRole.Secondary)]
    [InlineData("photos.zip.001", ArchiveFormat.Zip, ArchiveRole.Primary)]
    [InlineData("photos.z01", ArchiveFormat.Zip, ArchiveRole.Secondary)]
    public void Classifies_supported_single_and_volume_names(
        string name,
        ArchiveFormat format,
        ArchiveRole role)
    {
        var result = ArchiveFilenameClassifier.Classify(name, isLink: false);

        Assert.NotNull(result);
        Assert.Equal(format, result.Format);
        Assert.Equal(role, result.Role);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("photos.part00.rar")]
    [InlineData("photos.7z.000")]
    public void Rejects_unsupported_names(string name)
        => Assert.Null(ArchiveFilenameClassifier.Classify(name, isLink: false));

    [Fact]
    public void Never_marks_a_link_as_openable()
        => Assert.Null(ArchiveFilenameClassifier.Classify("photos.zip", isLink: true));
}
```

In `LocalFileBrowserTests`, create `legacy.rar` with `legacy.r00` and `classic.z01` with `classic.zip`. Assert the directory-aware listing reports `legacy.rar` and `classic.zip` as `Primary`, their numbered parts as `Secondary`, and standalone `single.rar`/`single.zip` as `Single`.

- [ ] **Step 2: Run the focused test and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~ArchiveFilenameClassifierTests
```

Expected: compilation fails because the archive enums and classifier do not exist.

- [ ] **Step 3: Add the public metadata types and classifier**

```csharp
namespace ReachCommander.Domain.Archives;

public enum ArchiveFormat
{
    Zip,
    Rar,
    SevenZip,
}

public enum ArchiveRole
{
    Single,
    Primary,
    Secondary,
}
```

Add nullable `ArchiveFormatHint` and `ArchiveRole` positional members to `FileEntry` and `FileEntryDto`. Implement the classifier with compiled, culture-invariant, case-insensitive regular expressions for `partNN.rar`, `.rNN`, `.7z.NNN`, `.zip.NNN`, and `.zNN`, plus exact terminal extensions `.zip`, `.rar`, and `.7z`. Indexes start at one except legacy `.r00` and `.z01`; the two-argument classifier returns `Single` for plain `.rar` and `.zip`. Add a directory-aware overload accepting the complete sibling-name set; it upgrades plain `.rar` to `Primary` when the matching `.r00` exists and plain `.zip` to `Primary` when matching `.z01` exists. It never upgrades when the first numbered sibling is absent.

- [ ] **Step 4: Map metadata through filesystem listing and API JSON**

In `LocalFileBrowser`, enumerate and validate directory entries once, build an ordinal-ignore-case sibling-name set, then call the directory-aware classifier only for regular files and pass `entry.IsSymbolicLink`. Keep directories and links at null metadata. In `FilesController`, map enum values using the existing JSON string-enum configuration and add an integration assertion for:

```json
{
  "name": "photos.7z",
  "archiveFormatHint": "sevenZip",
  "archiveRole": "single"
}
```

- [ ] **Step 5: Run focused and regression tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchiveFilenameClassifierTests|FullyQualifiedName~LocalFileBrowserTests"
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~FilesApiTests
```

Expected: archive metadata is present only for supported, non-link file candidates; existing listing tests pass.

- [ ] **Step 6: Commit the classification slice**

```powershell
git status --short
git add src/ReachCommander.Domain/Archives src/ReachCommander.Domain/Files/FileEntry.cs src/ReachCommander.Infrastructure/Archives/Classification src/ReachCommander.Infrastructure/FileSystem/LocalFileBrowser.cs src/ReachCommander.Api/Contracts/FileEntryDto.cs src/ReachCommander.Api/Controllers/FilesController.cs tests/ReachCommander.UnitTests/Archives/ArchiveFilenameClassifierTests.cs tests/ReachCommander.UnitTests/Files/LocalFileBrowserTests.cs tests/ReachCommander.IntegrationTests/FilesApiTests.cs
git commit -m "feat: classify archive volume candidates"
```

---

### Task 2: Define archive contracts, limits, and safe failures

**Files:**

- Create: `src/ReachCommander.Domain/Archives/ArchiveEntryType.cs`
- Create: `src/ReachCommander.Domain/Archives/ArchiveEntry.cs`
- Create: `src/ReachCommander.Application/Archives/ArchiveBrowseModels.cs`
- Create: `src/ReachCommander.Application/Archives/ArchiveExtractionModels.cs`
- Create: `src/ReachCommander.Application/Archives/ArchiveExceptions.cs`
- Create: `src/ReachCommander.Application/Archives/IArchiveBrowser.cs`
- Create: `src/ReachCommander.Application/Archives/IArchiveExtractionService.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/ArchiveOptions.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/ArchiveOptionsValidator.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveOptionsValidatorTests.cs`

**Interfaces:**

- Produces: framework-independent browse, preview, operation, cancellation, and stable error contracts.
- Produces: `ArchiveOptions` used by the resolver, catalog, worker process, cache, planner, and coordinator.
- Consumed by: all remaining backend tasks and the API DTO mapping in Tasks 6 and 10.

- [ ] **Step 1: Write failing default and invalid-limit tests**

```csharp
using ReachCommander.Infrastructure.Archives;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveOptionsValidatorTests
{
    private readonly ArchiveOptionsValidator _validator = new();

    [Fact]
    public void Defaults_match_the_approved_safety_envelope()
    {
        var value = new ArchiveOptions();

        Assert.True(value.Enabled);
        Assert.Equal(100_000, value.MaxEntries);
        Assert.Equal(100, value.MaxVolumes);
        Assert.Equal(500L * 1024 * 1024 * 1024, value.MaxTotalCompressedBytes);
        Assert.Equal(500L * 1024 * 1024 * 1024, value.MaxTotalExtractedBytes);
        Assert.Equal(200L * 1024 * 1024 * 1024, value.MaxSingleExtractedFileBytes);
        Assert.Equal(1_000, value.MaxExpansionRatio);
        Assert.Equal(64, value.MaxPathDepth);
        Assert.Equal(4_096, value.MaxPathCharacters);
        Assert.Equal(255, value.MaxComponentCharacters);
        Assert.Equal(1, value.MaxConcurrentExtractions);
        Assert.Equal(TimeSpan.FromSeconds(30), value.InspectionTimeout);
        Assert.Equal(TimeSpan.FromHours(6), value.ExtractionTimeout);
        Assert.Equal(1L * 1024 * 1024 * 1024, value.WorkerManagedMemoryBytes);
        Assert.Equal(1_536L * 1024 * 1024, value.WorkerWorkingSetBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), value.PlanLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), value.CatalogLifetime);
        Assert.Equal(16, value.MaxCachedCatalogs);
        Assert.Equal(250_000, value.MaxCachedEntries);
        Assert.True(_validator.Validate(null, value).Succeeded);
    }

    [Fact]
    public void Rejects_a_single_file_limit_above_the_total_limit()
    {
        var value = new ArchiveOptions
        {
            MaxSingleExtractedFileBytes = 11,
            MaxTotalExtractedBytes = 10,
        };

        Assert.True(_validator.Validate(null, value).Failed);
    }
}
```

- [ ] **Step 2: Run the focused test and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~ArchiveOptionsValidatorTests
```

Expected: compilation fails because `ArchiveOptions` and its validator do not exist.

- [ ] **Step 3: Add exact public models**

```csharp
namespace ReachCommander.Domain.Archives;

public enum ArchiveEntryType
{
    File,
    Directory,
}

public sealed record ArchiveEntry(
    string Path,
    string Name,
    ArchiveEntryType Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Extension,
    string Attributes);
```

```csharp
namespace ReachCommander.Application.Archives;

public sealed record ArchiveLocation(
    string SourceId,
    string ArchivePath,
    string InternalPath);

public sealed record ArchiveDirectoryListing(
    ArchiveLocation Location,
    ArchiveFormat Format,
    int VolumeCount,
    IReadOnlyList<ArchiveEntry> Entries);

public sealed record ArchiveExtractionPreviewRequest(
    string SourceId,
    string ArchivePath,
    string InternalDirectory,
    IReadOnlyList<string> EntryPaths,
    bool ExtractAll,
    string DestinationSourceId,
    string DestinationPath);

public sealed record ArchiveExtractionIssue(
    string Code,
    string Message,
    IReadOnlyList<string> LogicalPaths);

public sealed record ArchiveExtractionPreview(
    string PlanId,
    DateTimeOffset ExpiresAt,
    ArchiveFormat Format,
    int VolumeCount,
    IReadOnlyList<string> SelectedRoots,
    int FileCount,
    int DirectoryCount,
    long? TotalExtractedBytes,
    string DestinationSourceId,
    string DestinationPath,
    IReadOnlyList<ArchiveExtractionIssue> Conflicts,
    IReadOnlyList<ArchiveExtractionIssue> Violations,
    bool CanExecute);

public enum ArchiveExtractionState
{
    Queued,
    Extracting,
    Finalizing,
    Completed,
    Cancelled,
    Failed,
    RecoveryRequired,
}

public enum ArchiveCompensationState
{
    NotRequired,
    NotStarted,
    Succeeded,
    Failed,
}

public sealed record ArchiveExtractionOperation(
    string OperationId,
    ArchiveExtractionState State,
    int CompletedFiles,
    int TotalFiles,
    long ExtractedBytes,
    long? TotalBytes,
    double? Percent,
    string? CurrentEntryName,
    bool CanCancel,
    ArchiveCompensationState CompensationState,
    IReadOnlyList<string> RecoveryNames,
    string? ErrorCode,
    string? ErrorDetail);
```

`IArchiveBrowser.ListAsync` accepts an `ArchiveLocation`. `IArchiveExtractionService` exposes `PreviewAsync`, `ExecuteAsync(planId)`, `GetAsync(operationId)`, and `CancelAsync(operationId)`, all with cancellation tokens and `ValueTask` results. `ExecuteAsync` returns the operation in `Queued`; `CancelAsync` returns its updated public state.

- [ ] **Step 4: Add stable exception types and exact codes**

Create abstract `ArchiveException(string code, string detail)` and sealed application failures mapping exactly to: `archive_unsupported`, `archive_invalid`, `archive_encrypted`, `archive_volume_secondary`, `archive_volume_set_invalid`, `archive_entry_unsafe`, `archive_limit_exceeded`, `archive_destination_invalid`, `archive_destination_read_only`, `archive_destination_conflict`, `archive_plan_not_found`, `archive_plan_expired`, `archive_plan_stale`, `archive_destination_changed`, `archive_capacity_reached`, `archive_worker_failed`, `archive_extraction_cancelled`, and `archive_recovery_required`. Exception fields may contain logical paths, missing volume indexes, and conflicting final names only.

- [ ] **Step 5: Implement exact defaults and validation**

Set `ArchiveOptions.SectionName` to `"Archives"` and implement every default asserted above. Validation fails for non-positive numeric/time values; `MaxSingleExtractedFileBytes > MaxTotalExtractedBytes`; `MaxCachedCatalogs > 1_024`; `MaxCachedEntries < MaxEntries`; `WorkerWorkingSetBytes < WorkerManagedMemoryBytes`; path depth/length combinations that cannot represent one legal component; and overflow in configured byte arithmetic.

- [ ] **Step 6: Run focused and full contract tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~ArchiveOptionsValidatorTests
dotnet test ReachCommander.slnx -c Release
```

Expected: the options tests and existing suite pass.

- [ ] **Step 7: Commit the contract slice**

```powershell
git status --short
git add src/ReachCommander.Domain/Archives src/ReachCommander.Application/Archives src/ReachCommander.Infrastructure/Archives/ArchiveOptions.cs src/ReachCommander.Infrastructure/Archives/ArchiveOptionsValidator.cs tests/ReachCommander.UnitTests/Archives/ArchiveOptionsValidatorTests.cs
git commit -m "feat: define bounded archive contracts"
```

---

### Task 3: Resolve and fingerprint complete same-directory volume sets

**Files:**

- Create: `src/ReachCommander.Infrastructure/Archives/Volumes/ArchiveVolumeFingerprint.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Volumes/ArchivePartResolver.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchivePartResolverTests.cs`

**Interfaces:**

- Produces: internal immutable `ResolvedArchivePartSet` containing format, primary logical path, ordered resolved regular-file parts, and a stable fingerprint.
- Consumes: `ISourceCatalog`, `IPathSecurityService`, the filename classifier, `ArchiveOptions`, and filesystem metadata.
- Consumed by: worker inspection, browser caching, preview staleness checks, and execution revalidation.

- [ ] **Step 1: Write failing resolution tests**

Create table-driven tests using `TemporaryDirectory` for these exact cases:

```csharp
[Theory]
[InlineData("movie.part01.rar", 3)]
[InlineData("movie.rar", 3)]
[InlineData("movie.7z.001", 3)]
[InlineData("movie.zip.001", 3)]
[InlineData("movie.zip", 3)]
public async Task Resolves_primary_and_orders_all_contiguous_parts(
    string primaryName,
    int expectedCount)
```

For the five rows create, respectively: `part01.rar` through `part03.rar`; `.rar`, `.r00`, `.r01`; `.7z.001` through `.003`; `.zip.001` through `.003`; and `.z01`, `.z02`, `.zip`. Assert the logical parts are in archive order. Add separate tests proving secondary input throws `archive_volume_secondary`; a missing middle part, duplicate numeric index, mixed scheme, over-limit part count, symlink/reparse part, and cumulative compressed bytes above the limit each throw the approved safe error. Add a fingerprint test proving a length or last-write-time change changes the fingerprint.

- [ ] **Step 2: Run the focused test and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~ArchivePartResolverTests
```

Expected: compilation fails because the resolver and part-set types do not exist.

- [ ] **Step 3: Implement resolution with authoritative path confinement**

```csharp
internal sealed record ResolvedArchivePart(
    string LogicalPath,
    string PhysicalPath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

internal sealed record ResolvedArchivePartSet(
    ArchiveFormat Format,
    string PrimaryLogicalPath,
    IReadOnlyList<ResolvedArchivePart> Parts,
    ArchiveVolumeFingerprint Fingerprint);

internal interface IArchivePartResolver
{
    ValueTask<ResolvedArchivePartSet> ResolveAsync(
        string sourceId,
        string archivePath,
        CancellationToken cancellationToken);
}
```

Resolve the requested archive through `IPathSecurityService`, reject a directory/link/reparse target, enumerate only its resolved parent directory, and match sibling names with `OrdinalIgnoreCase`. Reject names that normalize to the same comparison key. Require contiguous numeric ranges and the scheme-specific terminal primary part. Secondary-part errors contain only the expected primary logical filename; incomplete-set errors contain only capped missing indexes or expected logical filenames. Resolve every chosen sibling independently through `IPathSecurityService`; verify it remains in the same source and parent, is a regular non-link file, and has a unique physical file identity where the platform exposes one. Sum lengths with `checked`, enforce `MaxVolumes` and `MaxTotalCompressedBytes`, then fingerprint ordered `(sourceId, primaryLogicalPath, logicalPartPath, length, lastWriteTimeUtcTicks)` values with SHA-256. Return no physical path in an application/API model or exception.

- [ ] **Step 4: Run focused tests and mutation regressions**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchivePartResolverTests|FullyQualifiedName~PathSecurityServiceTests"
```

Expected: all valid schemes resolve in correct order and every unsafe or incomplete set is rejected.

- [ ] **Step 5: Commit the volume-resolution slice**

```powershell
git status --short
git add src/ReachCommander.Infrastructure/Archives/Volumes tests/ReachCommander.UnitTests/Archives/ArchivePartResolverTests.cs
git commit -m "feat: resolve safe archive volume sets"
```

---

### Task 4: Normalize untrusted entries into a bounded virtual catalog

**Files:**

- Create: `src/ReachCommander.Infrastructure/Archives/Catalog/ArchiveCatalog.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Catalog/ArchivePathPolicy.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Catalog/ArchiveCatalogBuilder.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchivePathPolicyTests.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveCatalogBuilderTests.cs`

**Interfaces:**

- Produces: internal `UntrustedArchiveEntry`, immutable `ArchiveCatalog`, and `ArchiveCatalogBuilder.Build`.
- Produces: normalized absolute virtual paths using `/` only and an immediate-children query that never touches the host filesystem.
- Consumed by: archive browsing, selection expansion, conflict planning, and extraction index selection.

- [ ] **Step 1: Write failing path-policy and catalog tests**

Use `[Theory]` cases that reject `../escape`, `/rooted`, `C:/drive`, `C:\\drive`, `\\\\server\\share`, `a/./b`, `a/../b`, `a//b`, `a:b`, `name.`, `name `, `.reachcommander-extract-forged.partial/file.txt`, NUL/control characters, Windows device names (`CON`, `aux.txt`), depth 65, a 256-character component, and total path length 4,097. Assert Unicode NFC normalization. Build catalog tests that prove:

- `a/b/file.txt` synthesizes `/a` and `/a/b` directories.
- listing `/a` returns only `b`, not its descendants.
- explicit and synthesized copies of the same directory merge once.
- file/file, file/directory, case-only, and Unicode-normalization collisions fail on Windows and Linux policy modes.
- link, hard-link, device, FIFO, socket, and other special entry flags fail.
- entry count, single-file size, total extracted size, checked aggregation overflow, and known expansion ratio above 1,000 fail.
- unknown declared sizes remain `null` and do not bypass runtime enforcement.

Define test input with explicit data rather than opening a real archive:

```csharp
new UntrustedArchiveEntry(
    Index: 7,
    Key: "Family/2025/photo.jpg",
    IsDirectory: false,
    IsEncrypted: false,
    IsLink: false,
    IsSpecial: false,
    Size: 1_024,
    CompressedSize: 512,
    ModifiedAt: DateTimeOffset.Parse("2026-08-20T10:00:00Z"));
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchivePathPolicyTests|FullyQualifiedName~ArchiveCatalogBuilderTests"
```

Expected: compilation fails because the catalog types do not exist.

- [ ] **Step 3: Implement a platform-portable path policy**

`ArchivePathPolicy.NormalizeEntryPath` must replace `\` with `/`, reject rooted/UNC/drive forms before trimming, split without dropping empty segments, normalize each component to Unicode NFC, and reject null/dot/parent components. Reject control characters, `/`, `\`, `:`, platform-invalid filename characters, trailing dot/space, case-insensitive Windows device names even when an extension exists, and components matching `.reachcommander-extract-*.partial` with ordinal-ignore-case comparison. Enforce the configured component count, component length, and full normalized length. Return one leading slash and no trailing slash except for `/`.

Use `StringComparer.OrdinalIgnoreCase` for collision keys on every supported deployment platform. This intentionally makes a catalog portable from Windows development to Ubuntu deployment and prevents extraction results that differ by host.

- [ ] **Step 4: Build immutable nodes and checked aggregates**

```csharp
internal sealed record ArchiveCatalogNode(
    int? WorkerEntryIndex,
    string Path,
    string Name,
    ArchiveEntryType Type,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? ModifiedAt,
    string? Extension,
    string Attributes,
    int DescendantFileCount,
    int DescendantDirectoryCount,
    long? DescendantSize,
    IReadOnlyList<string> Children);

internal sealed class ArchiveCatalog
{
    public required ArchiveFormat Format { get; init; }
    public required IReadOnlyDictionary<string, ArchiveCatalogNode> Nodes { get; init; }
    public required int FileCount { get; init; }
    public required int DirectoryCount { get; init; }
    public required long? TotalDeclaredSize { get; init; }
    public IReadOnlyList<ArchiveCatalogNode> ListChildren(string internalDirectory);
    public IReadOnlyList<ArchiveCatalogNode> ExpandSelection(
        string internalDirectory,
        IReadOnlyList<string> selectedPaths,
        bool extractAll);
}
```

The builder rejects encryption and special/link metadata before adding nodes. It creates missing ancestors, prevents an existing file from becoming an ancestor, stores children in deterministic ordinal-ignore-case order, derives safe extensions/attributes, calculates directory descendant file/directory counts and sizes, and uses `checked` arithmetic. Apply known-size and known-ratio limits during construction, treating positive size over zero compressed bytes as over-limit; retain `null` when any descendant file size is unknown. `ExpandSelection` requires every requested path to exist at or below `internalDirectory`, removes duplicate/descendant roots when an ancestor is selected, and returns each file worker index once.

- [ ] **Step 5: Run focused tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchivePathPolicyTests|FullyQualifiedName~ArchiveCatalogBuilderTests"
```

Expected: all unsafe names, collisions, metadata types, and limit breaches fail with `archive_entry_unsafe` or `archive_limit_exceeded`; valid entries produce deterministic immediate-directory listings.

- [ ] **Step 6: Commit the virtual-catalog slice**

```powershell
git status --short
git add src/ReachCommander.Infrastructure/Archives/Catalog tests/ReachCommander.UnitTests/Archives/ArchivePathPolicyTests.cs tests/ReachCommander.UnitTests/Archives/ArchiveCatalogBuilderTests.cs
git commit -m "feat: validate virtual archive catalogs"
```

---

### Task 5: Add the framed protocol and isolated SharpCompress worker

**Files:**

- Create: `src/ReachCommander.ArchiveProtocol/ReachCommander.ArchiveProtocol.csproj`
- Create: `src/ReachCommander.ArchiveProtocol/ArchiveFrame.cs`
- Create: `src/ReachCommander.ArchiveProtocol/ArchiveWorkerMessages.cs`
- Create: `src/ReachCommander.ArchiveProtocol/ArchiveProtocolJsonContext.cs`
- Create: `src/ReachCommander.ArchiveWorker/ReachCommander.ArchiveWorker.csproj`
- Create: `src/ReachCommander.ArchiveWorker/Program.cs`
- Create: `src/ReachCommander.ArchiveWorker/ArchiveFrameReader.cs`
- Create: `src/ReachCommander.ArchiveWorker/ArchiveFrameWriter.cs`
- Create: `src/ReachCommander.ArchiveWorker/SharpCompressArchiveAdapter.cs`
- Create: `src/ReachCommander.ArchiveWorker/WorkerRequestDispatcher.cs`
- Create: `src/ReachCommander.ArchiveWorker/Properties/AssemblyInfo.cs`
- Modify: `src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj`
- Modify: `tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj`
- Modify: `ReachCommander.slnx`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveWorkerProtocolTests.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveWorkerInspectionTests.cs`

**Interfaces:**

- Produces: a versioned binary framing protocol and a one-request worker executable.
- Produces: inspection frames containing detected format and raw entry metadata; extraction frame types are defined now and executed in Task 8.
- Consumes: ordered physical volume paths only inside the worker request body.
- Consumed by: Infrastructure's bounded process client in Task 6.

- [ ] **Step 1: Write failing framing tests**

```csharp
using ReachCommander.ArchiveProtocol;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveWorkerProtocolTests
{
    [Fact]
    public async Task Round_trips_a_versioned_inspection_request()
    {
        var request = new ArchiveInspectionRequest(
            ProtocolVersion: 1,
            RequestId: "request-1",
            VolumePaths: ["/srv/archive.7z.001", "/srv/archive.7z.002"],
            Limits: new ArchiveWorkerLimits(100_000, 500L * 1024 * 1024 * 1024));
        await using var stream = new MemoryStream();

        await ArchiveFrameCodec.WriteJsonAsync(stream, ArchiveFrameKind.InspectionRequest, request, default);
        stream.Position = 0;
        var frame = await ArchiveFrameCodec.ReadAsync(stream, 1_048_576, default);
        var actual = frame.Deserialize<ArchiveInspectionRequest>();

        Assert.Equal(ArchiveFrameKind.InspectionRequest, frame.Kind);
        Assert.Equal(request, actual);
    }

    [Fact]
    public async Task Rejects_a_frame_above_the_reader_limit()
    {
        await using var stream = new MemoryStream(
            new byte[] { 82, 67, 65, 82, 1, 1, 0, 0, 0, 16 });

        await Assert.ThrowsAsync<ArchiveProtocolException>(
            () => ArchiveFrameCodec.ReadAsync(stream, 8, default).AsTask());
    }
}
```

Use a ten-byte header: four ASCII bytes `RCAR`, one protocol-version byte, one `ArchiveFrameKind` byte, and a four-byte unsigned big-endian payload length. Metadata/request payloads are UTF-8 JSON; `EntryData` payload is opaque bytes. Cap request/metadata frames at 1 MiB and data frames at 64 KiB. Reject bad magic, unsupported version, unknown kind, truncated header/payload, and oversize frames.

- [ ] **Step 2: Run protocol tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~ArchiveWorkerProtocolTests
```

Expected: compilation fails because the protocol project and codec do not exist.

- [ ] **Step 3: Define exact request and frame payloads**

```csharp
public sealed record ArchiveWorkerLimits(int MaxEntries, long MaxTotalExtractedBytes);

public sealed record ArchiveInspectionRequest(
    byte ProtocolVersion,
    string RequestId,
    IReadOnlyList<string> VolumePaths,
    ArchiveWorkerLimits Limits);

public sealed record ArchiveExtractionRequest(
    byte ProtocolVersion,
    string RequestId,
    IReadOnlyList<string> VolumePaths,
    IReadOnlyList<int> EntryIndexes,
    ArchiveWorkerLimits Limits);

public sealed record ArchiveDetectedFrame(string Format, bool IsSolid);

public sealed record ArchiveEntryFrame(
    int Index,
    string Key,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsLink,
    bool IsSpecial,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? ModifiedAt);

public sealed record ArchiveEntryStartFrame(int Index);
public sealed record ArchiveEntryEndFrame(int Index, long ActualBytes);
public sealed record ArchiveProgressFrame(int CompletedFiles, long ActualBytes);
public sealed record ArchiveCompletedFrame(int CompletedFiles, long ActualBytes);
public sealed record ArchiveFailureFrame(string Code, string Detail);
```

Frame kinds are `InspectionRequest`, `ExtractionRequest`, `ArchiveDetected`, `ArchiveEntry`, `InspectionCompleted`, `EntryStart`, `EntryData`, `EntryEnd`, `Progress`, `Completed`, and `Failure`. Use source-generated `System.Text.Json` metadata for every JSON record and camel-case enum/string values. The protocol project has no package dependencies.

- [ ] **Step 4: Implement one-shot worker inspection with SharpCompress 0.50.4**

The Worker project references ArchiveProtocol and:

```xml
<PackageReference Include="SharpCompress" Version="0.50.4" />
```

Read exactly one request, reject trailing request frames, open ordered parts with `ArchiveFactory.OpenArchive(IReadOnlyList<FileInfo>)`, and enumerate in library order. Detect ZIP, RAR, or SevenZip from the opened archive type/signature and reject all other formats with `archive_unsupported`. For a multi-part request, verify the opened archive reports a multi-volume structure that consumes the complete ordered part list; an ignored, unrelated, reordered, or mismatched part set returns `archive_volume_set_invalid`. Emit `ArchiveDetected`, one `ArchiveEntry` per entry, then `InspectionCompleted`. Stop and emit one safe `Failure` for invalid structure, encryption, unsupported format, entry-count breach, or unexpected parser failure; never copy the SharpCompress exception message into `Detail`.

The worker must not parse command-line archive paths, create files, read environment secrets, start child processes, or write non-protocol bytes to stdout. It may write one bounded diagnostic category to stderr for API-side diagnostics, never a path or archive entry name.

- [ ] **Step 5: Add real ZIP, 7z, RAR, solid, encrypted, and split-set inspection tests**

Reference Worker from the unit-test project and expose its internals only to `ReachCommander.UnitTests`. Test `WorkerRequestDispatcher` with in-memory input/output and fixture paths. Assert successful detected format and entry frames for supported fixtures; deterministic rejection for encrypted and malformed fixtures; ordered multi-volume opening for modern RAR, legacy RAR, split 7z, and split ZIP fixtures; and `archive_volume_set_invalid` for a same-named part replaced with bytes from another set. Fixture acquisition/provenance is finalized in Task 12; during this task commit only the small fixtures needed by the focused tests with their source recorded in `tests/fixtures/archives/README.md`.

- [ ] **Step 6: Run worker tests and build every project**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchiveWorkerProtocolTests|FullyQualifiedName~ArchiveWorkerInspectionTests"
dotnet build ReachCommander.slnx -c Release
```

Expected: framing rejects malformed input, inspection recognizes all approved formats and volumes, and the solution builds with the pinned package.

- [ ] **Step 7: Commit the isolated worker slice**

```powershell
git status --short
git add ReachCommander.slnx src/ReachCommander.ArchiveProtocol src/ReachCommander.ArchiveWorker src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj tests/ReachCommander.UnitTests/Archives/ArchiveWorkerProtocolTests.cs tests/ReachCommander.UnitTests/Archives/ArchiveWorkerInspectionTests.cs tests/fixtures/archives
git commit -m "feat: add isolated archive inspection worker"
```

---

### Task 6: Expose cached virtual archive browsing through the API

**Files:**

- Create: `src/ReachCommander.Infrastructure/Archives/Worker/IArchiveWorkerClient.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Worker/ArchiveWorkerProcess.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Worker/ArchiveWorkerClient.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Catalog/ArchiveCatalogCache.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Catalog/IArchiveCatalogProvider.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Catalog/ArchiveCatalogProvider.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/ArchiveBrowser.cs`
- Create: `src/ReachCommander.Api/Contracts/Archives/ArchiveDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/ArchivesController.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Modify: `src/ReachCommander.Api/Program.cs`
- Modify: `src/ReachCommander.Api/appsettings.json`
- Modify: `src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/ReachCommander.Api.csproj`
- Modify: `Dockerfile`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveWorkerClientTests.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveCatalogCacheTests.cs`
- Test: `tests/ReachCommander.IntegrationTests/ArchiveBrowsingApiTests.cs`
- Modify test support: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`

**Interfaces:**

- Produces: `GET /api/archives/entries?sourceId={id}&archivePath={path}&path={virtualPath}`.
- Produces: bounded worker lifecycle and a fingerprint-keyed metadata cache.
- Consumes: Tasks 2–5 contracts, resolver, catalog builder, and worker protocol.
- Consumed by: Angular archive navigation in Task 7 and extraction preview in Task 8.

- [ ] **Step 1: Write failing worker-boundary tests**

Use a fake executable/process seam to prove the client:

- starts `dotnet` with only the configured worker DLL argument and `UseShellExecute = false`;
- sends physical volume paths only in the framed stdin request;
- sets `DOTNET_GCHeapHardLimit` to the configured byte value in invariant hexadecimal;
- kills the entire process tree on 30-second inspection timeout, cancellation, invalid frame, unexpected exit, output limit, or working set above 1,536 MiB;
- samples working set at most every 250 milliseconds;
- captures at most 16 KiB of stderr and maps it to a safe `archive_worker_failed` without returning the captured text;
- validates request IDs, frame order, entry indexes, completion, and detected format.

- [ ] **Step 2: Write failing browser API tests**

```csharp
[Fact]
public async Task Get_entries_returns_only_immediate_virtual_children()
{
    using var client = _factory.CreateClient();

    var response = await client.GetAsync(
        "/api/archives/entries?sourceId=downloads&archivePath=/sample.zip&path=/Family");

    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadFromJsonAsync<ArchiveDirectoryDto>();
    Assert.Equal("/sample.zip", body!.ArchivePath);
    Assert.Equal("/Family", body.Path);
    Assert.Equal("zip", body.Format);
    Assert.True(body.IsReadOnly);
    Assert.All(body.Entries, entry => Assert.DoesNotContain("/", entry.Name));
}
```

Add error-contract cases for secondary volume, invalid signature, unsupported signature, encrypted content, unsafe entry, missing volume, and every inspection limit. Assert response bodies contain no temp-root physical path.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchiveWorkerClientTests|FullyQualifiedName~ArchiveCatalogCacheTests"
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ArchiveBrowsingApiTests
```

Expected: compilation fails because the worker client, cache, browser, DTO, and controller do not exist.

- [ ] **Step 4: Implement the bounded process client and catalog cache**

`IArchiveWorkerClient.InspectAsync` returns detected format, solid flag, and `UntrustedArchiveEntry` values. `ArchiveWorkerProcess` owns `Process`, standard streams, timeout, a 250-millisecond watchdog, bounded stderr capture, kill-tree cleanup, and asynchronous disposal. Use `ArgumentList`; never construct a shell command string.

`ArchiveCatalogCache` keys entries by format plus the complete `ArchiveVolumeFingerprint`, uses a five-minute absolute lifetime, caps itself at 16 catalogs and 250,000 aggregate nodes, evicts least-recently-used catalogs under one lock, and never serves a fingerprint mismatch. Cache entries are immutable; do not cache exceptions. `IArchiveCatalogProvider.GetAsync(sourceId, archivePath)` performs part resolution, worker inspection, catalog construction/cache lookup, and returns one internal `ResolvedArchiveCatalog` containing the current part set plus catalog; both browser and extraction planner call this provider so verification logic is not duplicated.

- [ ] **Step 5: Implement browsing and safe DTO mapping**

`ArchiveBrowser.ListAsync` normalizes the controller's virtual `path` into `ArchiveLocation.InternalPath`, asks `IArchiveCatalogProvider` for the current verified catalog, verifies the requested directory exists, and maps immediate nodes to `ArchiveEntry`. Set `Attributes` to `"Archive · RO"`, extensions only for files, and `IsReadOnly = true` on the directory DTO.

The controller rejects missing query values through model validation and returns:

```json
{
  "sourceId": "downloads",
  "archivePath": "/backups/photos.7z",
  "path": "/Family/2025",
  "format": "sevenZip",
  "volumeCount": 2,
  "isReadOnly": true,
  "entries": []
}
```

Extend `FileAccessExceptionHandler` with the exact status table in the design: 415 unsupported; 400 invalid; 422 encrypted/volume-set/unsafe; 409 secondary; 413 limits; and 500 sanitized worker failure.

- [ ] **Step 6: Wire validated options and publish the worker beside the API**

Add this complete section to `appsettings.json`:

```json
"Archives": {
  "Enabled": true,
  "MaxEntries": 100000,
  "MaxVolumes": 100,
  "MaxTotalCompressedBytes": 536870912000,
  "MaxTotalExtractedBytes": 536870912000,
  "MaxSingleExtractedFileBytes": 214748364800,
  "MaxExpansionRatio": 1000,
  "MaxPathDepth": 64,
  "MaxPathCharacters": 4096,
  "MaxComponentCharacters": 255,
  "MaxConcurrentExtractions": 1,
  "InspectionTimeout": "00:00:30",
  "ExtractionTimeout": "06:00:00",
  "WorkerManagedMemoryBytes": 1073741824,
  "WorkerWorkingSetBytes": 1610612736,
  "PlanLifetime": "00:10:00",
  "CatalogLifetime": "00:05:00",
  "MaxCachedCatalogs": 16,
  "MaxCachedEntries": 250000
}
```

Register the validator with `ValidateOnStart`, the resolver/browser/cache/process client as appropriate singleton/scoped services, and no-op archive services only when `Enabled` is false that return `archive_unsupported` without launching a worker.

Add an API `ProjectReference` to the Worker with `ReferenceOutputAssembly="false"` so restore/build ordering is explicit without linking worker types into the API. Add one MSBuild target that copies the Worker's framework-dependent build output into `$(TargetDir)archive-worker/` after ordinary API builds and another that publishes the Worker into `$(PublishDir)archive-worker/` during API publish. Both layouts must include its `.deps.json`, `.runtimeconfig.json`, `SharpCompress.dll`, ArchiveProtocol assembly, and license files. Resolve the fixed worker DLL location as `archive-worker/ReachCommander.ArchiveWorker.dll` under `AppContext.BaseDirectory`; do not accept the location from an HTTP request. Update Docker restore layers to copy both new project files before restore; do not install OS archive packages.

- [ ] **Step 7: Verify API, publish layout, and Docker definition**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchiveWorkerClientTests|FullyQualifiedName~ArchiveCatalogCacheTests"
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ArchiveBrowsingApiTests
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release -o artifacts/archive-publish
Get-ChildItem artifacts/archive-publish/archive-worker
```

Expected: tests pass and publish output contains the worker DLL, runtime config, dependency manifest, SharpCompress, ArchiveProtocol, and license notice.

- [ ] **Step 8: Commit the browse API slice**

```powershell
git status --short
git add src/ReachCommander.Infrastructure/Archives src/ReachCommander.Infrastructure/DependencyInjection.cs src/ReachCommander.Api/Contracts/Archives src/ReachCommander.Api/Controllers/ArchivesController.cs src/ReachCommander.Api/Program.cs src/ReachCommander.Api/appsettings.json src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs src/ReachCommander.Api/ReachCommander.Api.csproj Dockerfile tests/ReachCommander.UnitTests/Archives/ArchiveWorkerClientTests.cs tests/ReachCommander.UnitTests/Archives/ArchiveCatalogCacheTests.cs tests/ReachCommander.IntegrationTests/ArchiveBrowsingApiTests.cs tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs
git commit -m "feat: expose virtual archive browsing"
```

---

### Task 7: Browse archives as read-only Angular panel locations

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/panel-persistence.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/panel-persistence.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/path-bar/path-bar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/path-bar/path-bar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/path-bar/path-bar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/directory-tabs/directory-tabs.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts`

**Interfaces:**

- Produces: a discriminated `PanelLocation` that cannot represent a filesystem/archive hybrid.
- Produces: open, parent, refresh, tab persistence, search, sort, and selection behavior for virtual locations.
- Consumes: filesystem archive hints from Task 1 and browse API from Task 6.
- Consumed by: extraction context and F5 behavior in Task 11.

- [ ] **Step 1: Write failing state and API tests**

Add these exact location models to test imports and use them in test fixtures:

```typescript
export interface FilesystemLocation {
  readonly kind: 'filesystem';
  readonly sourceId: string;
  readonly path: string;
}

export interface ArchiveLocation {
  readonly kind: 'archive';
  readonly sourceId: string;
  readonly archivePath: string;
  readonly internalPath: string;
}

export type PanelLocation = FilesystemLocation | ArchiveLocation;

export interface ArchivePanelMetadata {
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
}

export interface DirectoryTab {
  readonly id: string;
  readonly label: string;
  readonly location: PanelLocation;
}
```

Tests must prove:

- `ReachCommanderApi.listArchive` URL-encodes `sourceId`, `archivePath`, and the virtual `path` as query parameters.
- Enter/double-click on a primary candidate calls `listArchive` and replaces the active tab location with archive root.
- Enter on a secondary part surfaces the server's `archive_volume_secondary` detail and does not change location.
- Directories inside an archive change only `internalPath`.
- parent at archive root returns to the containing filesystem directory; parent below root changes only `internalPath`.
- a nested `.zip` returned by archive browsing is not classified/opened as an archive.
- search, sort, Ctrl+A, cursor, refresh, tab creation/activation/close all continue to work.
- persisted archive tabs restore after reload; a missing/stale archive leaves the tab and exposes a return-to-parent action.
- switching source always creates a filesystem-root location.
- upload and Multi-Rename are disabled for an archive location even when its underlying source is writable.

- [ ] **Step 2: Run focused Angular tests and verify RED**

```powershell
Set-Location client/reach-commander-ui
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false --include='src/app/core/api/reach-commander-api.spec.ts' --include='src/app/core/state/commander-store.spec.ts' --include='src/app/core/state/panel-persistence.spec.ts'
```

Expected: compilation/tests fail because archive DTOs, `PanelLocation`, and archive store operations do not exist.

- [ ] **Step 3: Extend API DTOs and the port**

```typescript
export type ArchiveFormat = 'zip' | 'rar' | 'sevenZip';
export type ArchiveRole = 'single' | 'primary' | 'secondary';

export interface ArchiveDirectoryDto {
  readonly sourceId: string;
  readonly archivePath: string;
  readonly path: string;
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
  readonly isReadOnly: true;
  readonly entries: readonly FileEntryDto[];
}
```

Add nullable `archiveFormatHint` and `archiveRole` to `FileEntryDto`. Add abstract `listArchive(sourceId, archivePath, internalPath)` to `CommanderApiPort` and implement it against `/api/archives/entries`, sending `internalPath` under the query key `path`.

- [ ] **Step 4: Migrate tabs and persistence to the discriminated location**

Replace tab-level `sourceId/path` fields with `location`. Keep verified format and volume count as nullable `archiveMetadata` on transient `PanelState`, not in `ArchiveLocation`. Add pure helpers `locationSourceId`, `locationDisplayPath`, and `locationParent`. The archive display path format is exactly:

```text
Downloads:/backups/photos.7z!/Family/2025
```

Bump persisted state to version 2 and store the full discriminated location. Accept valid version-1 filesystem tabs and migrate each to `{ kind: 'filesystem', sourceId, path }`; reject malformed archive locations. Preserve failed archive tabs rather than silently rewriting them.

- [ ] **Step 5: Implement location-aware loading and opening**

`loadPanel` dispatches to `listFiles` or `listArchive` based on `location.kind`, keeps request-token stale-response protection, and stores archive API entries exactly as read-only table entries. A successful archive response sets transient `archiveMetadata`; filesystem navigation and failed archive loads clear it. `openEntry` handles filesystem directories and primary/single archive candidates. In an archive, it handles directories only. When archive loading fails, retain its location and set the stable API problem code.

At archive root, `navigateParent` changes to the containing filesystem directory. Add `returnArchiveToParent(side)` for the stale/missing recovery action and focus the former archive row when it is still present.

- [ ] **Step 6: Update shell, panel, path, and toolbar UI**

Route both keyboard open and row double-click through `CommanderStore.openEntry`. Show an `Archive · RO` badge beside the formatted path. Give supported archive rows an archive icon/name tooltip and secondary parts a distinct volume-part icon with accessible guidance to open the primary; do not communicate either state by color alone. Disable path editing for archive locations; keep copy/select behavior. Show loading, empty, unsupported, and failure changes through a polite live region, preserve focus on the active panel after navigation, and show a safe inline error plus `Return to parent folder` button for unavailable archive tabs. Disable Upload/Add and Multi-Rename based on `location.kind === 'archive'`; keep search enabled. Nested archive-looking rows use an ordinary file icon and an accessible explanation that nested browsing is unavailable.

- [ ] **Step 7: Run focused and complete Angular tests**

```powershell
Set-Location client/reach-commander-ui
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false --include='src/app/core/api/reach-commander-api.spec.ts' --include='src/app/core/state/commander-store.spec.ts' --include='src/app/core/state/panel-persistence.spec.ts' --include='src/app/features/commander/commander-panel/commander-panel.component.spec.ts' --include='src/app/features/commander/commander-shell/commander-shell.component.spec.ts' --include='src/app/features/commander/file-table/file-table.component.spec.ts' --include='src/app/features/commander/path-bar/path-bar.component.spec.ts' --include='src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts'
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false
```

Expected: archive navigation and recovery tests pass and the full frontend suite has no regression.

- [ ] **Step 8: Commit the archive navigation slice**

```powershell
git status --short
git add client/reach-commander-ui/src/app/core/api client/reach-commander-ui/src/app/core/state/commander.models.ts client/reach-commander-ui/src/app/core/state/commander-store.ts client/reach-commander-ui/src/app/core/state/commander-store.spec.ts client/reach-commander-ui/src/app/core/state/panel-persistence.ts client/reach-commander-ui/src/app/core/state/panel-persistence.spec.ts client/reach-commander-ui/src/app/features/commander
git commit -m "feat: browse archives in commander panels"
```

---

### Task 8: Create immutable extraction previews and acquire multiple mutation locks

**Files:**

- Create: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionPlanStore.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionPlanner.cs`
- Modify: `src/ReachCommander.Infrastructure/Mutations/DirectoryMutationLock.cs`
- Modify test: `tests/ReachCommander.UnitTests/Uploads/DirectoryMutationLockTests.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveExtractionPlannerTests.cs`

**Interfaces:**

- Produces: internal immutable `ArchiveExtractionPlan` and expiring single-operation plan binding.
- Produces: `DirectoryMutationLock.AcquireManyAsync` with deduplication and deterministic ordering.
- Consumes: source catalog, safe path resolution, part resolver/fingerprint, cached catalog, and extraction preview request.
- Consumed by: coordinator in Task 9 and preview API in Task 10.

- [ ] **Step 1: Write failing multi-lock tests**

Add tests proving `AcquireManyAsync`:

- normalizes, deduplicates, and ordinally sorts `(sourceId, logicalDirectory)` keys before acquisition;
- does not deadlock when two callers request the same keys in opposite input order;
- preserves existing same-directory and ancestor/descendant exclusion;
- allows siblings to proceed;
- releases every acquired lease when acquisition is cancelled or one acquisition fails.

Use two coordinated tasks and `TaskCompletionSource` barriers; do not use arbitrary sleeps.

- [ ] **Step 2: Write failing planner tests**

Create fake part resolver, catalog provider, source catalog, path security service, clock, and random-ID source. Cover:

- archive-panel selected file maps directly to destination `/file.txt`;
- selected directory `/Family` maps to `/Family` and retains its descendants;
- redundant child selection under a selected parent is removed;
- `extractAll` maps archive root contents directly, without an archive-name wrapper;
- direct F5 accepts exactly one filesystem primary/single archive and sets `extractAll`;
- multiple archives, a mixed selection, or a secondary part is rejected;
- destination must be an available writable filesystem location, never an archive;
- conflict comparison is `OrdinalIgnoreCase`, includes existing files/directories and collisions between selected roots, and returns a complete preview with `canExecute: false` and capped safe conflict names;
- declared counts, sizes, ratio, path depth, and output path length are enforced;
- known total size above destination free space returns a non-executable `archive_limit_exceeded` violation; an unknown total remains allowed and runtime-bounded;
- unknown total size remains `null`;
- plan ID is 256 random bits encoded base64url and expiration is exactly 10 minutes;
- only logical destination/source data appears in the public preview.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~DirectoryMutationLockTests|FullyQualifiedName~ArchiveExtractionPlannerTests"
```

Expected: new multi-lock and planner tests fail because the methods/types do not exist.

- [ ] **Step 4: Implement deterministic multi-lock acquisition**

```csharp
public sealed record DirectoryMutationTarget(string SourceId, string LogicalDirectory);

public ValueTask<IAsyncDisposable> AcquireManyAsync(
    IEnumerable<DirectoryMutationTarget> targets,
    CancellationToken cancellationToken);
```

Normalize with the lock's existing source/path key rules, deduplicate exact keys, sort first by source ID and then logical path with `StringComparer.Ordinal`, and acquire in that order. If a later acquisition fails, dispose earlier leases in reverse order. Return a composite lease that disposes once in reverse order.

- [ ] **Step 5: Implement immutable preview planning and storage**

```csharp
internal sealed record PlannedArchiveFile(
    int WorkerEntryIndex,
    string ArchivePath,
    string RelativeOutputPath,
    long? DeclaredSize,
    long? DeclaredCompressedSize,
    DateTimeOffset? ModifiedAt);

internal sealed record ArchiveExtractionPlan(
    string PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    string ArchivePath,
    ResolvedArchivePartSet PartSet,
    string InternalDirectory,
    IReadOnlyList<string> SelectedRoots,
    IReadOnlyList<PlannedArchiveFile> Files,
    IReadOnlyList<string> Directories,
    string DestinationSourceId,
    string DestinationPath,
    string DestinationSnapshot,
    IReadOnlyList<ArchiveExtractionIssue> Conflicts,
    IReadOnlyList<ArchiveExtractionIssue> Violations,
    bool CanExecute);
```

The destination snapshot hashes the normalized immediate destination names, their file/directory type, length where applicable, and last-write timestamps. Query destination free space from the resolved volume and compare it only when the selected total is known. Conflict, policy, free-space, and selection violations return a safe complete preview with `CanExecute = false`; invalid structure, encryption, incomplete volumes, and unsafe catalog failures still throw Problem Details because no trustworthy review exists. Cap issue collections and each logical name/detail to fixed safe lengths. The plan store uses an injected clock, removes expired never-executed plans opportunistically, caps stored plans at 128, and atomically binds an executable plan to at most one operation ID. Execute on a non-executable plan returns its highest-priority stable issue without starting an operation. A failed pre-execution validation releases the reservation only when retrying remains safe; an accepted operation retains its binding so repeated execute calls are idempotent.

Planner resolution must never combine the archive `InternalDirectory` with a host path. Generate output components only from catalog nodes already accepted by `ArchivePathPolicy`, make selected roots relative to the current virtual directory so `/Family/2025/photo.jpg` selected while browsing `/Family/2025` produces only `photo.jpg`, resolve final logical output paths through `IPathSecurityService`, and inspect conflicts without creating anything.

- [ ] **Step 6: Run focused planner and lock tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~DirectoryMutationLockTests|FullyQualifiedName~ArchiveExtractionPlannerTests"
```

Expected: lock order is deterministic and previews implement exact selected-root, all-content, free-space, limit, conflict, issue-reporting, and expiry semantics.

- [ ] **Step 7: Commit the extraction-planning slice**

```powershell
git status --short
git add src/ReachCommander.Infrastructure/Mutations/DirectoryMutationLock.cs src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionPlanStore.cs src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionPlanner.cs tests/ReachCommander.UnitTests/Uploads/DirectoryMutationLockTests.cs tests/ReachCommander.UnitTests/Archives/ArchiveExtractionPlannerTests.cs
git commit -m "feat: plan conflict-free archive extractions"
```

---

### Task 9: Stream extraction into staging, finalize atomically, and compensate failures

**Files:**

- Modify: `src/ReachCommander.ArchiveWorker/SharpCompressArchiveAdapter.cs`
- Modify: `src/ReachCommander.ArchiveWorker/WorkerRequestDispatcher.cs`
- Modify: `src/ReachCommander.Infrastructure/Archives/Worker/IArchiveWorkerClient.cs`
- Modify: `src/ReachCommander.Infrastructure/Archives/Worker/ArchiveWorkerClient.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveStagingWriter.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionOperationStore.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionCoordinator.cs`
- Create: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionService.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveWorkerExtractionTests.cs`
- Test: `tests/ReachCommander.UnitTests/Archives/ArchiveExtractionCoordinatorTests.cs`

**Interfaces:**

- Produces: worker `ExtractionRequest` handling and streamed entry frames.
- Produces: bounded operation acceptance, state tracking, cancellation, staging, finalization, and compensation.
- Consumes: immutable preview plans, multi-directory locks, volume/destination revalidation, and worker frames.
- Consumed by: extraction API in Task 10.

- [ ] **Step 1: Write failing worker extraction tests**

Using real small fixtures and an in-memory framed output stream, assert:

- only requested worker entry indexes emit output;
- each file emits `EntryStart`, one or more bounded `EntryData` frames, matching `EntryEnd`, then progress;
- directories and unselected entries emit no bytes;
- each selected index appears exactly once and solid archives are read sequentially;
- encrypted entries fail before any entry bytes;
- premature parser errors emit a safe failure and no completion frame;
- actual byte totals in end/progress/completed frames equal payload bytes.

- [ ] **Step 2: Write failing coordinator tests**

Use injectable filesystem and worker seams. Cover the operation state sequence `queued → extracting → finalizing → completed`; monotonic completed-file/byte/percent progress and safe current logical entry name; immediate `archive_capacity_reached` when one operation is active; source fingerprint staleness; destination snapshot/name/free-space changes; create-new non-link staging; runtime single-file, total-byte, and ratio limits; timestamp clamping; cancellation during staging; cancellation ignored after finalization begins; worker crash; handled cleanup; partial final-move compensation; and compensation failure ending in `recoveryRequired` with only a safe logical recovery name.

Assert final names never appear before finalization. On successful finalization assert the staging directory is gone. On handled failure/cancel assert both final names and staging are gone. On simulated process crash/recovery-required assert `.reachcommander-extract-{operationId}.partial` remains and is never auto-deleted by a later service start.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchiveWorkerExtractionTests|FullyQualifiedName~ArchiveExtractionCoordinatorTests"
```

Expected: worker extraction and coordinator tests fail because extraction dispatch, staging, and operation services do not exist.

- [ ] **Step 4: Implement sequential worker streaming**

Open the archive once with the ordered `FileInfo` list. Validate requested indexes are unique and refer to non-directory, non-encrypted, non-link entries. Iterate archive entries in their natural order; for selected files, open the entry stream and copy through a pooled 64 KiB buffer into `EntryData` frames. Track all totals with `checked`, enforce worker-side `MaxTotalExtractedBytes`, and emit progress after each `EntryEnd`. Do not seek on entry streams or parallelize solid archives.

Extend the client with:

```csharp
ValueTask ExtractAsync(
    ResolvedArchivePartSet partSet,
    IReadOnlyList<int> entryIndexes,
    IArchiveEntrySink sink,
    CancellationToken cancellationToken);

internal interface IArchiveEntrySink
{
    ValueTask StartAsync(int entryIndex, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    ValueTask EndAsync(int entryIndex, long actualBytes, CancellationToken cancellationToken);
    ValueTask ProgressAsync(int completedFiles, long actualBytes, CancellationToken cancellationToken);
}
```

The client validates frame ordering and counts before forwarding data. Apply the six-hour extraction timeout and the same memory/watchdog/kill policy as inspection.

- [ ] **Step 5: Implement API-owned staging and runtime enforcement**

Create `.reachcommander-extract-{operationId}.partial` inside the resolved destination with create-new semantics. Immediately verify the created staging root and every created ancestor is a real non-link directory beneath the canonical destination, and repeat that verification before each output open and final move. Pre-create validated planned directories beneath staging. `ArchiveStagingWriter.StartAsync` maps an expected worker index to its preplanned relative output path and opens exactly one new file. It rejects duplicate/unexpected indexes. `WriteAsync` increments actual per-file and total bytes before writing and aborts on limits or known compressed-size ratio above 1,000. `EndAsync` verifies worker totals, flushes/disposes the file, and applies a modified timestamp only when within the host filesystem's supported UTC range.

The worker never receives the staging root, relative output path, final destination, or source ID.

- [ ] **Step 6: Implement bounded operations, revalidation, finalization, and compensation**

`ArchiveExtractionService.ExecuteAsync` atomically binds a valid plan, reserves one concurrency slot, creates a random operation ID, stores `Queued`, and starts coordinator work through a supervised background task whose exceptions are observed. A previously bound plan returns its existing operation without reserving another slot. Do not add an unbounded queue. Operation storage retains the latest 100 completed terminal operations for one hour and exposes immutable snapshots.

The coordinator acquires source-containing and destination locks with `AcquireManyAsync`, reopens every source volume with read sharing only, re-resolves its fingerprint, re-resolves destination policy/permissions/free space, and rechecks its snapshot/conflicts before creating staging. Transition to `Extracting`, stream the worker, then recheck source fingerprint and destination conflicts once more. Transition to `Finalizing`, stop observing caller/user cancellation, and move each staged top-level root into its final create-new name. On failure, move already-finalized roots back into staging in reverse order. Successful compensation yields `Failed` and deletes staging; incomplete compensation yields `RecoveryRequired` and preserves staging. Release the concurrency slot in `finally`.

Operation updates reject decreasing file/byte counts and decreasing derivable percent, expose the planned logical basename as `CurrentEntryName`, set `CanCancel` only in queued/extracting states, and record compensation state plus capped recovery names. Generate operation IDs from 256 cryptographically random bits encoded base64url. Structured logs contain only operation ID, source ID, archive logical path, destination source/logical path, counts, safe status/code, and elapsed time; never log physical paths, complete private entry lists, raw worker output, or file content.

Cancellation is idempotent: set an operation-owned cancellation token during `Queued`/`Extracting`; return unchanged state during `Finalizing` or after terminal state. A staging cancellation kills the worker, closes open handles, recursively deletes the exact validated staging directory, and records `Cancelled`.

- [ ] **Step 7: Run focused and full backend tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~ArchiveWorkerExtractionTests|FullyQualifiedName~ArchiveExtractionCoordinatorTests"
dotnet test ReachCommander.slnx -c Release
```

Expected: streamed bytes, runtime bounds, operation transitions, cancellation, staging cleanup, finalization, and compensation all pass; existing upload and rename lock tests remain green.

- [ ] **Step 8: Commit the extraction engine slice**

```powershell
git status --short
git add src/ReachCommander.ArchiveWorker src/ReachCommander.Infrastructure/Archives src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/Archives/ArchiveWorkerExtractionTests.cs tests/ReachCommander.UnitTests/Archives/ArchiveExtractionCoordinatorTests.cs
git commit -m "feat: execute staged archive extractions"
```

---

### Task 10: Add preview, execute, status, and cancellation endpoints

**Files:**

- Modify: `src/ReachCommander.Api/Contracts/Archives/ArchiveDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/ArchiveExtractionsController.cs`
- Modify: `src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs`
- Test: `tests/ReachCommander.IntegrationTests/ArchiveExtractionsApiTests.cs`
- Modify test support: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`

**Interfaces:**

- Produces: `POST /api/archive-extractions/preview`.
- Produces: `POST /api/archive-extractions/{planId}/execute` with `202 Accepted`.
- Produces: `GET /api/archive-extractions/{operationId}`.
- Produces: `POST /api/archive-extractions/{operationId}/cancel`.
- Consumes: `IArchiveExtractionService` and application records from Tasks 2, 8, and 9.

- [ ] **Step 1: Write failing request/response and lifecycle integration tests**

Use the exact preview body:

```json
{
  "sourceId": "downloads",
  "archivePath": "/backups/photos.7z",
  "internalDirectory": "/Family",
  "entryPaths": ["/Family/2025"],
  "extractAll": false,
  "destinationSourceId": "media",
  "destinationPath": "/Photos"
}
```

Assert preview returns `200 OK` with plan ID, expiration, verified format, volume count, selected roots, file/directory counts, nullable total size, logical destination, conflicts, violations, and `canExecute`. Add a safe destination-conflict case that returns `200 OK` with `canExecute: false`; executing that plan returns `archive_destination_conflict` without launching a worker. Assert execute returns `202 Accepted`, a `Location` header pointing to the status endpoint, and a queued/running operation DTO. Repeating execute with the same accepted plan ID must return the same operation ID and must not launch a second worker. Assert status progresses monotonically to terminal state and cancellation is idempotent.

Add exact status tests:

| Code | HTTP status |
|---|---:|
| `archive_unsupported` | 415 |
| `archive_invalid` | 400 |
| `archive_encrypted` | 422 |
| `archive_volume_secondary` | 409 |
| `archive_volume_set_invalid` | 422 |
| `archive_entry_unsafe` | 422 |
| `archive_limit_exceeded` | 413 |
| `archive_destination_invalid` | 400 |
| `archive_destination_read_only` | 403 |
| `archive_destination_conflict` | 409 |
| `archive_plan_not_found` | 404 |
| `archive_plan_expired` | 410 |
| `archive_plan_stale` | 409 |
| `archive_destination_changed` | 409 |
| `archive_capacity_reached` | 429 |
| `archive_worker_failed` | 500 |
| `archive_extraction_cancelled` | 499 |
| `archive_recovery_required` | 500 |

For every response assert RFC 9457 Problem Details shape with stable `code`, a safe `detail`, and no physical root, worker stderr, SharpCompress type, or stack trace.

- [ ] **Step 2: Run endpoint tests and verify RED**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ArchiveExtractionsApiTests
```

Expected: route tests fail because the controller and DTO mappings do not exist.

- [ ] **Step 3: Add strict DTOs and controller actions**

Use an 8 MiB request-body limit plus data-annotation and explicit validation to require nonblank source IDs and logical paths, at most `MaxEntries` selected paths, no duplicate selected paths, and exactly one of these modes: `extractAll == true` with `internalDirectory == "/"` and empty `entryPaths`, or `extractAll == false` with one or more `entryPaths`. Mark the request DTO with `JsonUnmappedMemberHandling.Disallow` so extra JSON fields fail without changing unrelated APIs. Do not accept physical paths, volume lists, library format/type names, output names, conflict strategy, overwrite flags, or passwords.

Map operations as:

```json
{
  "operationId": "base64url-id",
  "state": "extracting",
  "completedFiles": 3,
  "totalFiles": 9,
  "extractedBytes": 1048576,
  "totalBytes": null,
  "percent": null,
  "currentEntryName": "photo.jpg",
  "canCancel": true,
  "compensationState": "notRequired",
  "recoveryNames": [],
  "errorCode": null,
  "errorDetail": null
}
```

`Execute` returns `AcceptedAtAction(nameof(GetOperation), new { operationId }, dto)`. `GetOperation` is read-only. `Cancel` calls the idempotent service method and returns `200 OK` with the latest DTO.

- [ ] **Step 4: Preserve execute idempotency in plan storage**

When the first execute call atomically binds a plan to an operation, retain the `planId → operationId` binding until the later of plan expiry and operation retention expiry. A repeated call returns that operation snapshot, including after terminal completion. Concurrent calls race through one compare-and-swap/lock and can create only one operation. An expired never-executed plan returns `archive_plan_expired`; a random ID returns `archive_plan_not_found`.

- [ ] **Step 5: Run integration and full backend tests**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ArchiveExtractionsApiTests
dotnet test ReachCommander.slnx -c Release
```

Expected: lifecycle, idempotency, cancellation, validation, safe error mapping, and existing endpoints all pass.

- [ ] **Step 6: Commit the extraction API slice**

```powershell
git status --short
git add src/ReachCommander.Api/Contracts/Archives/ArchiveDtos.cs src/ReachCommander.Api/Controllers/ArchiveExtractionsController.cs src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs tests/ReachCommander.IntegrationTests/ArchiveExtractionsApiTests.cs tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionPlanStore.cs src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionService.cs
git commit -m "feat: expose archive extraction operations"
```

---

### Task 11: Add F5 extraction review, progress, cancellation, and completion UI

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/archive-extraction.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/archive-extraction-store.ts`
- Create: `client/reach-commander-ui/src/app/core/state/archive-extraction-store.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/archive-extraction/archive-extraction-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/archive-extraction/archive-extraction-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/archive-extraction/archive-extraction-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/archive-extraction/archive-extraction-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**

- Produces: immutable extraction context captured from active source and opposite destination panels.
- Produces: accessible review/progress/completion dialog and F5 routing.
- Consumes: all extraction API endpoints and archive-aware commander locations.

- [ ] **Step 1: Write failing API and store tests**

Add DTOs matching Task 10 and port methods:

```typescript
abstract previewArchiveExtraction(
  request: ArchiveExtractionPreviewRequestDto,
): Promise<ArchiveExtractionPreviewDto>;
abstract executeArchiveExtraction(planId: string): Promise<ArchiveExtractionOperationDto>;
abstract getArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto>;
abstract cancelArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto>;
```

API tests assert correct methods, URL encoding, and empty cancellation body. Store tests use a fake scheduler/clock and prove:

- preview context captures source selection and opposite destination once and is not changed by later panel navigation;
- inside an archive, selected rows are used; without selection, the focused non-parent row is used;
- direct F5 on exactly one filesystem primary/single archive requests `extractAll` at `/`;
- multiple archive selection, mixed selection, secondary volume, ordinary file, missing destination, archive destination, unavailable destination, and read-only destination stop before an API call with a specific message;
- preview enters review phase; Execute enters running phase and polls by operation ID;
- polling stops on completed/cancelled/failed/recovery-required, component destruction, or dialog closure;
- capacity and stale/conflict failures retain review context for a safe retry/re-preview;
- cancellation is disabled during finalizing and idempotent otherwise;
- completion requests refresh of both source and destination panels while preserving archive location/selection rules.

- [ ] **Step 2: Run focused state tests and verify RED**

```powershell
Set-Location client/reach-commander-ui
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false --include='src/app/core/api/reach-commander-api.spec.ts' --include='src/app/core/state/archive-extraction-store.spec.ts'
```

Expected: compilation/tests fail because extraction DTOs, API methods, and store do not exist.

- [ ] **Step 3: Implement explicit extraction state and polling**

Use these UI phases:

```typescript
export type ArchiveExtractionPhase =
  | 'closed'
  | 'previewing'
  | 'review'
  | 'starting'
  | 'running'
  | 'cancelling'
  | 'completed'
  | 'cancelled'
  | 'failed'
  | 'recoveryRequired';
```

Keep context, preview, operation, and safe error separately in the state. Poll every 500 milliseconds with one request in flight; schedule the next poll only after the previous request settles. Treat `queued`, `extracting`, and `finalizing` as running backend states. Stop polling and clear timers through `DestroyRef`. Do not optimistic-complete or infer progress from elapsed time.

- [ ] **Step 4: Build the accessible review/progress dialog**

In review, display archive logical path, verified format, volume count, selected roots, file/directory counts, unpacked size or `Unknown`, conflicts, violations, `canExecute`, and captured destination source/path. Buttons are `Extract` and `Cancel`, with Extract disabled and the first issue focused when `canExecute` is false. In progress, display backend state, current logical entry name, completed/total files, extracted/total bytes, a determinate progress bar only when total bytes and percent are known, and `Cancel extraction` only when `canCancel` is true. Completion displays extracted totals and `Close`; failure displays safe detail and compensation state plus `Review again`; recovery-required explicitly lists capped safe staging/recovery names and tells the operator not to delete them until inspected.

Use native `<dialog>`, the existing dialog visual language, focus trap, labelled title/description, `aria-live="polite"` for progress, `aria-live="assertive"` for failures, initial focus on the safest action, and opener focus restoration. Escape closes only review or a terminal state; while extracting it requests confirmation/cancellation rather than silently dismissing.

- [ ] **Step 5: Route F5 and command-bar copy semantics**

Change F5 label from reserved to `Extract` when the active context is an archive location or exactly one selected/focused filesystem archive candidate. Add the same Extract action/state to the active-panel toolbar so mouse and keyboard use one shell command. Leave F5 reserved for ordinary filesystem entries until the normal copy feature exists. `CommanderShell.handleFunctionKey('F5')` builds context from the active panel and the opposite panel, opens preview, and prevents other commander commands while the modal is active. Double-click/Enter remains browse-only and never extracts.

- [ ] **Step 6: Write and run component behavior tests**

Test review metadata, unknown-size rendering, progress modes, cancellation visibility, finalizing lockout, terminal errors, focus trap/restoration, Escape behavior, and responsive narrow viewport layout.

```powershell
Set-Location client/reach-commander-ui
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false --include='src/app/core/state/archive-extraction-store.spec.ts' --include='src/app/features/archive-extraction/archive-extraction-dialog.component.spec.ts' --include='src/app/features/commander/command-bar/command-bar.component.spec.ts' --include='src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts' --include='src/app/features/commander/commander-shell/commander-shell.component.spec.ts'
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false
```

Expected: review, execute, polling, cancellation, terminal states, accessibility, and all frontend regressions pass.

- [ ] **Step 7: Commit the extraction interface slice**

```powershell
git status --short
git add client/reach-commander-ui/src/app/core/api client/reach-commander-ui/src/app/core/state/archive-extraction.models.ts client/reach-commander-ui/src/app/core/state/archive-extraction-store.ts client/reach-commander-ui/src/app/core/state/archive-extraction-store.spec.ts client/reach-commander-ui/src/app/features/archive-extraction client/reach-commander-ui/src/app/features/commander/active-panel-toolbar client/reach-commander-ui/src/app/features/commander/command-bar client/reach-commander-ui/src/app/features/commander/commander-shell
git commit -m "feat: add archive extraction interface"
```

---

### Task 12: Verify fixtures, browser flows, cross-platform CI, deployment, and documentation

**Files:**

- Create/modify: `tests/fixtures/archives/README.md`
- Create: `tests/fixtures/archives/generate-safe-fixtures.ps1`
- Add: approved small fixture files under `tests/fixtures/archives/`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/specs/archive-workflow.spec.ts`
- Modify: `.github/workflows/ci.yml`
- Modify: `README.md`
- Modify: `Dockerfile`
- Modify: `src/ReachCommander.Api/appsettings.json`

**Interfaces:**

- Produces: reproducible fixture provenance and end-to-end acceptance on the published app/worker layout.
- Produces: backend test coverage on Windows and Ubuntu plus frontend/browser coverage on Ubuntu.
- Produces: public operator and portfolio documentation for supported archive behavior and limits.

- [ ] **Step 1: Make the fixture set reproducible and legally attributable**

Create `generate-safe-fixtures.ps1` so the ReachCommander-owned fixtures are reproducible. It uses `System.IO.Compression.ZipArchive` for `nested.zip` and SharpCompress 0.50.4 `SevenZipWriter` for `sample.7z`, with fixed timestamp `2000-01-01T00:00:00Z` and these exact UTF-8 files: `root.txt` = `root fixture\n`, `Family/2025/photo.txt` = `photo fixture\n`, and `Family/2025/nested.zip` = `nested archive marker\n`. It also byte-splits a generated ZIP into contiguous `split.zip.001` through `.003`; its worker test remains red until the adapter opens the complete ordered set and reproduces the original catalog/content checksums.

Copy the remaining samples from SharpCompress tag `0.50.4`, peeled commit `c083c6efd843a844b0c8f7878787360e815be781`, under `tests/TestArchives/Archives/`: `Rar.rar`, `Rar.solid.rar`, `Rar.multi.part01.rar` through `Rar.multi.part06.rar`, `Rar2.multi.rar` plus `Rar2.multi.r00` through `Rar2.multi.r05`, `Rar.encrypted_filesOnly.rar`, `Original.7z.001` through `Original.7z.007`, and `Infozip.nocomp.multi.z01` plus `Infozip.nocomp.multi.zip`. Rename copies to the friendly fixture names shown in the File Structure and record both names. Keep only fixtures directly exercised by tests.

`tests/fixtures/archives/README.md` must list, for every binary: filename, format/purpose, how it was generated or the immutable upstream raw URL, SHA-256 emitted by `Get-FileHash`, expected entry names/sizes, upstream MIT license attribution, and the exact test classes that use it. Do not include personal media, credentials, or copyrighted user content.

- [ ] **Step 2: Write the failing Playwright workflow first**

Seed a writable `Downloads` source with one nested ZIP and a complete multi-volume RAR set, plus a writable empty `Media/Extracted` destination and a deliberate `Media/Conflicts` name. Add acceptance tests that:

1. Double-click a ZIP, assert `Archive · RO` and `Downloads:/nested.zip!/`, browse a directory, search with `*.txt`, navigate parent, and retain the archive tab.
2. Select one archive file, press F5, verify review metadata/destination, extract, wait for completion, close, and assert the exact file appears in the opposite panel.
3. Focus an unopened archive in the filesystem, press F5, verify whole-archive review, and extract root contents without a wrapper directory.
4. Attempt extraction into a conflict and assert no partial final file appears.
5. Open a secondary RAR part and assert the primary-part guidance.
6. Reload a persisted archive tab after removing its fixture through test setup and assert the recoverable return-to-parent UI.

- [ ] **Step 3: Run Playwright and verify RED before completing wiring**

```powershell
Set-Location tests/e2e
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@playwright\test\cli.js' test specs/archive-workflow.spec.ts --workers=1
```

Expected: new acceptance assertions fail until seed data, published worker discovery, and final selectors/status behavior are complete.

- [ ] **Step 4: Complete seed/publish behavior and run acceptance**

Copy committed fixtures into the temporary source without modifying them. Ensure `dotnet publish` creates `publish/archive-worker/`, and run the API with that directory as its working tree on Windows and Ubuntu. Add `data-testid` only where role/name selectors cannot reliably express an archive-specific state.

```powershell
Set-Location client/reach-commander-ui
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' build
Set-Location ../../../tests/e2e
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@playwright\test\cli.js' test --workers=1
```

Expected: all existing and archive Playwright tests pass against the published API plus bundled worker.

- [ ] **Step 5: Split CI into cross-platform backend and Ubuntu UI jobs**

Create a `backend` job with matrix `os: [ubuntu-latest, windows-latest]` that restores and runs `dotnet test ReachCommander.slnx -c Release --no-restore`. Keep Angular test/build, API publish, Chromium installation, and Playwright in an `acceptance` job on Ubuntu. Upload diagnostics only on failure and give each OS/job a distinct artifact name. Ensure both jobs have a 30-minute limit and read-only repository permissions.

- [ ] **Step 6: Document configuration, behavior, and recovery**

Add README sections covering:

- browse/open gestures and `Archive · RO` behavior;
- supported single and primary multi-volume naming schemes;
- F5 selected-entry and direct whole-archive extraction semantics;
- no password or nested-archive support;
- every `Archives` option/default from Task 2;
- worker isolation, no system archive dependency, and Windows/Ubuntu compatibility;
- destination conflict/all-or-nothing behavior;
- `.reachcommander-extract-{operationId}.partial` recovery guidance with the rule that ReachCommander never auto-deletes crash leftovers;
- Docker bind-mount permissions and the requirement that writable destinations are configured writable;
- attribution to SharpCompress 0.50.4 and its MIT license.

Update the feature list and architecture tree. Reuse the existing public screenshot; add an archive-workflow screenshot only if the final interface materially differs and contains no personal paths/data.

- [ ] **Step 7: Run the full release verification**

```powershell
Set-Location 'D:\Work\Personal\Reach Commander'
dotnet restore ReachCommander.slnx
dotnet test ReachCommander.slnx -c Release --no-restore
Set-Location client/reach-commander-ui
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' test --watch=false
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@angular\cli\bin\ng.js' build
Set-Location ../../../
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/archive-release -p:BuildAngularOnPublish=false
Get-ChildItem artifacts/archive-release/archive-worker
Set-Location tests/e2e
& 'C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\@playwright\test\cli.js' test --workers=1
Set-Location ../../
git diff --check
git status --short
```

Expected: every .NET, Angular, build, publish-layout, and Playwright check passes; `git diff --check` is clean; only planned files are modified.

- [ ] **Step 8: Commit the verification and documentation slice**

```powershell
git status --short
git add tests/fixtures/archives tests/e2e .github/workflows/ci.yml README.md Dockerfile src/ReachCommander.Api/appsettings.json
git commit -m "test: verify archive workflows across platforms"
```

---

## Final Acceptance Checklist

- [ ] A valid primary ZIP, RAR, or 7z archive opens by Enter or double-click as a read-only virtual folder.
- [ ] Supported complete multi-volume RAR, legacy RAR, 7z, and ZIP sets open through their primary name; secondary parts explain which primary is required.
- [ ] Archive internal paths never enter filesystem path APIs, and unsafe/link/special/colliding entries fail before display or extraction.
- [ ] Tabs, parent navigation, refresh, search, sort, selection, Ctrl+A, persistence, and stale-archive recovery work in archive locations.
- [ ] Upload and Multi-Rename are disabled in archive locations; nested archives remain ordinary files.
- [ ] F5 previews selected archive entries into an immutable opposite filesystem destination; direct F5 on one unopened archive previews all root contents.
- [ ] Preview reports safe conflicts/policy/space violations as non-executable, rejects untrustworthy structural failures, and never starts work for unavailable/read-only/archive destinations, mixed selection, incomplete volume sets, or configured limit breaches.
- [ ] Execute is idempotent, capacity is bounded to one active operation, progress is polled, and staging-phase cancellation is safe and idempotent.
- [ ] Actual byte limits, ratio limits, timeouts, memory limits, working-set limits, and frame validation remain enforced when metadata is missing or dishonest.
- [ ] Finalization exposes no requested final name early, compensates partial moves, and preserves a safely named staging directory only when manual recovery is required.
- [ ] The worker is bundled with published/Docker output on Windows and Ubuntu, uses SharpCompress 0.50.4 only, receives no destination path, and starts no shell/listener/network function.
- [ ] All error codes/statuses match the approved contract without physical paths, worker diagnostics, stack traces, or library internals.
- [ ] Unit, integration, Angular, Playwright, publish-layout, Docker-definition, and Windows/Ubuntu CI checks pass.
- [ ] Fixture provenance, hashes, licenses, operator limits, supported behavior, and recovery steps are documented publicly.
