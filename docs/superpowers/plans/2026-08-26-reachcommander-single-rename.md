# ReachCommander Single-Item Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable safe F4 rename for one focused file or directory while reusing ReachCommander's existing rename planner, executor, locking, compensation, and security controls.

**Architecture:** Add an exact-literal preview command to the batch-rename application service and expose it through `POST /api/renames/preview`; the returned plan continues through the existing batch execute endpoint. Add a focused Angular store and modal, then connect it to cursor-only Commander state and F4 availability, refreshing every panel showing the affected directory after success.

**Tech Stack:** .NET 10, ASP.NET Core controllers and Problem Details, xUnit, Angular 22 signals/CDK a11y, Vitest, Playwright, TypeScript 6.

## Global Constraints

- Work directly on `master`; do not create an extra worktree.
- Preserve the unrelated untracked `NC-theme.png` file and never stage it.
- Single Rename targets only the active cursor row; selections remain the domain of Ctrl+M Multi-Rename.
- The requested new name is literal, including valid bracket characters such as `[N]`; it is never interpreted as a mask, regular expression, or path.
- Existing destinations are never overwritten, merged, skipped, or automatically renamed.
- Only regular files and directories that are direct children of an available writable filesystem source are eligible; archive entries, parent rows, symbolic links, and unsupported types are blocked.
- Reuse the existing 10-minute rename plan, directory mutation lock, two-phase executor, compensation, stale fingerprint validation, authentication, antiforgery, and rate limiting.
- Do not add a dependency or expose physical host paths in API or UI errors.
- Prefix every git invocation on this Windows checkout with `git -c safe.directory='D:/Work/Personal/Reach Commander'`.

## File Structure

### Backend

- Modify `src/ReachCommander.Application/BatchRenames/BatchRenamePreview.cs` — define the exact-name preview command.
- Modify `src/ReachCommander.Application/BatchRenames/IBatchRenameService.cs` — expose exact preview through the existing rename service boundary.
- Modify `src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanner.cs` — share plan construction between mask evaluation and literal exact-name evaluation.
- Modify `src/ReachCommander.Infrastructure/BatchRenames/BatchRenameService.cs` — delegate exact previews to the planner.
- Create `src/ReachCommander.Api/Contracts/BatchRenames/ExactRenamePreviewRequestDto.cs` — logical-only request mapping.
- Create `src/ReachCommander.Api/Controllers/RenamesController.cs` — authenticated exact preview route using existing global API policies.
- Modify `tests/ReachCommander.UnitTests/BatchRenames/BatchRenamePlannerTests.cs` — exact file/folder/literal/conflict/stale unit coverage.
- Modify `tests/ReachCommander.IntegrationTests/BatchRenamesApiTests.cs` — exact preview and execute HTTP coverage.
- Modify `tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs` — anonymous and antiforgery protection coverage.

### Angular

- Modify `client/reach-commander-ui/src/app/core/api/api.models.ts` — exact request type and API-port method.
- Modify `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts` — exact preview HTTP implementation.
- Modify `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts` — logical-only request test.
- Modify `client/reach-commander-ui/src/app/testing/commander-api-test-base.ts` — safe unsupported default for test fakes.
- Create `client/reach-commander-ui/src/app/core/state/single-rename.models.ts` — captured context, state, and completion contract.
- Create `client/reach-commander-ui/src/app/core/state/single-rename-store.ts` — debounced preview/execution state machine.
- Create `client/reach-commander-ui/src/app/core/state/single-rename-store.spec.ts` — store race, conflict, and execution tests.
- Create `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.ts` — accessible interaction logic.
- Create `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.html` — compact dialog markup.
- Create `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.scss` — existing-theme-compatible modal styling.
- Create `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.spec.ts` — focus, keyboard, and error rendering tests.
- Modify `client/reach-commander-ui/src/app/core/state/commander-store.ts` and `.spec.ts` — cursor-only capture and matching-panel refresh/focus.
- Modify `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.ts` and `.spec.ts` — F4 availability.
- Modify `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`, `.html`, and `.spec.ts` — modal lifecycle and command routing.

### Acceptance and documentation

- Modify `tests/e2e/support/seed-fixtures.ts` — deterministic single-rename files and folders.
- Create `tests/e2e/specs/single-rename.spec.ts` — file, folder, literal-name, conflict, and read-only scenarios.
- Modify `README.md` — current feature list, Rename behavior, shortcut table, security list, and roadmap.

---

### Task 1: Exact-name planning in the existing rename engine

**Files:**
- Modify: `src/ReachCommander.Application/BatchRenames/BatchRenamePreview.cs`
- Modify: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanner.cs`
- Test: `tests/ReachCommander.UnitTests/BatchRenames/BatchRenamePlannerTests.cs`

**Interfaces:**
- Consumes: existing `RenameNameValidator`, `IPathSecurityService`, `BatchRenamePlanStore`, `BatchRenameEntrySnapshot`, and `BatchRenamePreview`.
- Produces: `ExactRenamePreviewCommand` and `BatchRenamePlanner.PreviewExactAsync(ExactRenamePreviewCommand, CancellationToken)`.

- [ ] **Step 1: Write failing exact-name planner tests**

Add tests that prove literal evaluation, folder support, conflict refusal, and shared stale revalidation:

```csharp
[Fact]
public async Task PreviewExact_treats_the_requested_file_name_as_literal()
{
    _fixture.WriteFile("Movies/holiday.txt", "holiday");
    var planner = _fixture.CreatePlanner();

    var preview = await planner.PreviewExactAsync(new ExactRenamePreviewCommand(
        "media", "/Movies", "/Movies/holiday.txt", "[N]-literal.txt"),
        CancellationToken.None);

    var row = Assert.Single(preview.Rows);
    Assert.Equal("[N]-literal.txt", row.NewName);
    Assert.Equal(BatchRenamePreviewStatus.Ready, row.Status);
    Assert.True(preview.CanExecute);
}

[Fact]
public async Task PreviewExact_supports_directories_and_refuses_an_occupied_name()
{
    _fixture.CreateDirectory("Movies/Drafts");
    _fixture.CreateDirectory("Movies/Published");
    var planner = _fixture.CreatePlanner();

    var preview = await planner.PreviewExactAsync(new ExactRenamePreviewCommand(
        "media", "/Movies", "/Movies/Drafts", "Published"),
        CancellationToken.None);

    Assert.Equal(BatchRenamePreviewStatus.Conflict, Assert.Single(preview.Rows).Status);
    Assert.False(preview.CanExecute);
}

[Fact]
public async Task PreviewExact_creates_a_plan_rejected_after_the_source_changes()
{
    _fixture.WriteFile("Movies/original.txt", "original");
    var planner = _fixture.CreatePlanner();
    var preview = await planner.PreviewExactAsync(new ExactRenamePreviewCommand(
        "media", "/Movies", "/Movies/original.txt", "renamed.txt"),
        CancellationToken.None);
    _fixture.WriteFile("Movies/original.txt", "changed and longer");

    await Assert.ThrowsAsync<RenamePlanStaleException>(() => planner.RevalidateAsync(
        _fixture.PlanStore.GetRequiredPlan(preview.PlanId), CancellationToken.None).AsTask());
}

[Fact]
public async Task PreviewExact_allows_a_case_only_name_change()
{
    _fixture.WriteFile("Movies/Case.txt", "case");
    var preview = await _fixture.CreatePlanner().PreviewExactAsync(
        new ExactRenamePreviewCommand(
            "media", "/Movies", "/Movies/Case.txt", "case.txt"),
        CancellationToken.None);

    Assert.Equal(BatchRenamePreviewStatus.Ready, Assert.Single(preview.Rows).Status);
    Assert.True(preview.CanExecute);
}

[Theory]
[InlineData("")]
[InlineData(".")]
[InlineData("bad/name.txt")]
[InlineData("CON")]
public async Task PreviewExact_returns_a_non_executable_row_for_invalid_names(string newName)
{
    _fixture.WriteFile("Movies/original.txt", "original");
    var preview = await _fixture.CreatePlanner().PreviewExactAsync(
        new ExactRenamePreviewCommand(
            "media", "/Movies", "/Movies/original.txt", newName),
        CancellationToken.None);

    Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(preview.Rows).Status);
    Assert.False(preview.CanExecute);
}

[Fact]
public async Task PreviewExact_reuses_read_only_and_symbolic_link_policy()
{
    _fixture.WriteFile("Movies/link.txt", "target");
    _fixture.MarkEntryAsSymbolicLink("Movies/link.txt");
    var symbolic = await _fixture.CreatePlanner().PreviewExactAsync(
        new ExactRenamePreviewCommand(
            "media", "/Movies", "/Movies/link.txt", "renamed.txt"),
        CancellationToken.None);
    Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(symbolic.Rows).Status);

    await Assert.ThrowsAsync<SourceReadOnlyException>(() =>
        _fixture.CreatePlanner(sourceReadOnly: true).PreviewExactAsync(
            new ExactRenamePreviewCommand(
                "media", "/Movies", "/Movies/link.txt", "renamed.txt"),
            CancellationToken.None).AsTask());
}
```

- [ ] **Step 2: Run the unit tests and verify the missing contract fails**

Run:

```powershell
dotnet test tests\ReachCommander.UnitTests\ReachCommander.UnitTests.csproj --filter FullyQualifiedName~BatchRenamePlannerTests
```

Expected: compilation fails because `ExactRenamePreviewCommand` and `PreviewExactAsync` do not exist.

- [ ] **Step 3: Add the exact command and share plan construction**

Add to `BatchRenamePreview.cs`:

```csharp
public sealed record ExactRenamePreviewCommand(
    string SourceId,
    string DirectoryPath,
    string EntryPath,
    string NewName);
```

Refactor `BatchRenamePlanner` so the public methods select a name generator and the current candidate-building loop lives once:

```csharp
public ValueTask<BatchRenamePreview> PreviewAsync(
    BatchRenamePreviewCommand command,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(command);
    return PreviewCoreAsync(
        command.SourceId,
        command.DirectoryPath,
        command.EntryPaths,
        (entry, index) => _ruleEvaluator.Evaluate(
            entry.Name,
            entry.Extension,
            entry.Type,
            command.Rules,
            index).CompleteName,
        cancellationToken);
}

public ValueTask<BatchRenamePreview> PreviewExactAsync(
    ExactRenamePreviewCommand command,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(command);
    return PreviewCoreAsync(
        command.SourceId,
        command.DirectoryPath,
        [command.EntryPath],
        (_, _) => command.NewName,
        cancellationToken);
}

private async ValueTask<BatchRenamePreview> PreviewCoreAsync(
    string sourceId,
    string directoryPath,
    IReadOnlyList<string> entryPaths,
    Func<BatchRenameEntrySnapshot, int, string> newName,
    CancellationToken cancellationToken)
{
    ValidateBatchSize(entryPaths);
    var directory = await ResolveWritableDirectoryAsync(sourceId, directoryPath, cancellationToken);
    var entries = await ResolveSelectedEntriesAsync(directory, entryPaths, cancellationToken);
    var directoryChildren = _fileSystem.ListChildren(directory.LogicalPath, directory.PhysicalPath);
    var candidates = new List<RenameCandidate>(entries.Count);

    for (var index = 0; index < entries.Count; index++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = entries[index];
        var completeName = newName(entry, index);
        var validation = _nameValidator.Validate(completeName);
        var status = BatchRenamePreviewStatus.Ready;
        string? message = null;

        if (entry.IsSymbolicLink || entry.Type is not (FileEntryType.File or FileEntryType.Directory))
        {
            status = BatchRenamePreviewStatus.Invalid;
            message = "Symbolic links and unsupported entry types cannot be renamed.";
        }
        else if (!validation.IsValid)
        {
            status = BatchRenamePreviewStatus.Invalid;
            message = validation.Message;
        }
        else if (entry.Name.Equals(completeName, StringComparison.Ordinal))
        {
            status = BatchRenamePreviewStatus.Unchanged;
        }

        ResolvedSourcePath? destination = null;
        if (validation.IsValid)
        {
            destination = await _pathSecurity.ResolveChildAsync(
                directory.Source.Id,
                directory.LogicalPath,
                completeName,
                cancellationToken);
        }

        candidates.Add(new RenameCandidate(entry, completeName, destination, status, message));
    }

    MarkDuplicateDestinations(candidates);
    MarkOccupiedDestinations(candidates, directoryChildren);
    var now = _clock.GetUtcNow();
    var planId = Guid.NewGuid();
    var expiresAt = now.Add(PlanLifetime);
    var plannedEntries = candidates.Select(candidate => candidate.ToPlannedRename()).ToArray();
    var rows = candidates.Select(candidate => candidate.ToPreviewRow()).ToArray();
    var changedCount = rows.Count(row => row.Status == BatchRenamePreviewStatus.Ready);
    var unchangedCount = rows.Count(row => row.Status == BatchRenamePreviewStatus.Unchanged);
    var invalidCount = rows.Length - changedCount - unchangedCount;
    var preview = new BatchRenamePreview(
        planId,
        expiresAt,
        rows,
        changedCount > 0 && invalidCount == 0,
        changedCount,
        unchangedCount,
        invalidCount);
    _planStore.AddPlan(new StoredBatchRenamePlan(
        planId,
        now,
        expiresAt,
        directory.Source.Id,
        directory.LogicalPath,
        directory.PhysicalPath,
        plannedEntries,
        preview));
    return preview;
}
```

Remove the old duplicated candidate-building body from `PreviewAsync`; leave `RevalidateAsync` and all existing helpers unchanged.

- [ ] **Step 4: Run the planner suite and confirm existing mask behavior did not regress**

Run:

```powershell
dotnet test tests\ReachCommander.UnitTests\ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~BatchRenamePlannerTests|FullyQualifiedName~RenameRuleEvaluatorTests"
```

Expected: all selected tests pass, including swaps, case-only changes, batch limits, literal `[N]`, conflict, and stale exact plans.

- [ ] **Step 5: Commit the planning-engine slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- src/ReachCommander.Application/BatchRenames/BatchRenamePreview.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenamePlanner.cs tests/ReachCommander.UnitTests/BatchRenames/BatchRenamePlannerTests.cs
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: plan literal single-item renames"
```

### Task 2: Exact rename API protected by existing security boundaries

**Files:**
- Modify: `src/ReachCommander.Application/BatchRenames/IBatchRenameService.cs`
- Modify: `src/ReachCommander.Infrastructure/BatchRenames/BatchRenameService.cs`
- Create: `src/ReachCommander.Api/Contracts/BatchRenames/ExactRenamePreviewRequestDto.cs`
- Create: `src/ReachCommander.Api/Controllers/RenamesController.cs`
- Modify: `tests/ReachCommander.IntegrationTests/BatchRenamesApiTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs`

**Interfaces:**
- Consumes: `ExactRenamePreviewCommand`, `BatchRenamePlanner.PreviewExactAsync`, `BatchRenamePreviewDto`, and the existing execute endpoint.
- Produces: `IBatchRenameService.PreviewExactAsync(ExactRenamePreviewCommand, CancellationToken)` and authenticated `POST /api/renames/preview`.

- [ ] **Step 1: Write failing HTTP and security tests**

Add an integration test that previews and executes both literal file and folder names without exposing physical paths:

```csharp
[Fact]
public async Task Exact_preview_and_existing_execute_rename_files_and_directories_literally()
{
    var (logicalDirectory, physicalDirectory) = CreateCaseDirectory();
    File.WriteAllText(Path.Combine(physicalDirectory, "alpha.txt"), "alpha");
    Directory.CreateDirectory(Path.Combine(physicalDirectory, "Drafts"));
    using var client = factory.CreateClient();

    var fileResponse = await client.PostAsJsonAsync("/api/renames/preview", new
    {
        sourceId = "media",
        directoryPath = logicalDirectory,
        entryPath = $"{logicalDirectory}/alpha.txt",
        newName = "[N]-literal.txt",
    });
    var fileBody = await fileResponse.Content.ReadAsStringAsync();
    var filePreview = await fileResponse.Content.ReadFromJsonAsync<PreviewResponse>();
    Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
    Assert.Equal("[N]-literal.txt", Assert.Single(filePreview!.Rows).NewName);
    Assert.DoesNotContain(factory.MediaRoot, fileBody, StringComparison.OrdinalIgnoreCase);

    var fileExecute = await client.PostAsync(
        $"/api/batch-renames/{filePreview.PlanId}/execute", content: null);
    Assert.Equal(HttpStatusCode.OK, fileExecute.StatusCode);
    Assert.True(File.Exists(Path.Combine(physicalDirectory, "[N]-literal.txt")));

    var folderResponse = await client.PostAsJsonAsync("/api/renames/preview", new
    {
        sourceId = "media",
        directoryPath = logicalDirectory,
        entryPath = $"{logicalDirectory}/Drafts",
        newName = "Published",
    });
    var folderPreview = await folderResponse.Content.ReadFromJsonAsync<PreviewResponse>();
    await client.PostAsync($"/api/batch-renames/{folderPreview!.PlanId}/execute", content: null);
    Assert.True(Directory.Exists(Path.Combine(physicalDirectory, "Published")));
}
```

Add `(HttpMethod.Post, "/api/renames/preview", true)` to the anonymous endpoint matrix. In the antiforgery test, send the exact preview request after removing `X-ReachCommander-CSRF` and assert `400 BadRequest` with no mutation.

- [ ] **Step 2: Run integration tests and verify the route is missing**

```powershell
dotnet test tests\ReachCommander.IntegrationTests\ReachCommander.IntegrationTests.csproj --filter "FullyQualifiedName~BatchRenamesApiTests|FullyQualifiedName~AuthorizationBoundaryTests"
```

Expected: the exact preview test receives JSON 404 or the project fails to compile against the missing service method.

- [ ] **Step 3: Add the service method, DTO, and controller**

Extend `IBatchRenameService` and `BatchRenameService`:

```csharp
ValueTask<BatchRenamePreview> PreviewExactAsync(
    ExactRenamePreviewCommand command,
    CancellationToken cancellationToken);
```

```csharp
public ValueTask<BatchRenamePreview> PreviewExactAsync(
    ExactRenamePreviewCommand command,
    CancellationToken cancellationToken) =>
    planner.PreviewExactAsync(command, cancellationToken);
```

Create the request DTO:

```csharp
using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Api.Contracts.BatchRenames;

public sealed record ExactRenamePreviewRequestDto(
    string SourceId,
    string DirectoryPath,
    string EntryPath,
    string NewName)
{
    public ExactRenamePreviewCommand ToCommand() => new(
        SourceId,
        DirectoryPath,
        EntryPath,
        NewName);
}
```

Create the controller:

```csharp
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.BatchRenames;
using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/renames")]
public sealed class RenamesController(IBatchRenameService service) : ControllerBase
{
    [HttpPost("preview")]
    [ProducesResponseType<BatchRenamePreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BatchRenamePreviewDto>> Preview(
        ExactRenamePreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(BatchRenamePreviewDto.FromModel(
            await service.PreviewExactAsync(request.ToCommand(), cancellationToken)));
}
```

Do not add controller-local auth, antiforgery, or rate-limit bypasses; the application's global API conventions must protect this route exactly like batch rename.

- [ ] **Step 4: Run API tests and the complete backend suite**

```powershell
dotnet test ReachCommander.slnx
```

Expected: all unit and integration tests pass, including anonymous rejection, antiforgery rejection, literal file/folder execution, conflict behavior, and existing Multi-Rename.

- [ ] **Step 5: Commit the API slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- src/ReachCommander.Application/BatchRenames/IBatchRenameService.cs src/ReachCommander.Infrastructure/BatchRenames/BatchRenameService.cs src/ReachCommander.Api/Contracts/BatchRenames/ExactRenamePreviewRequestDto.cs src/ReachCommander.Api/Controllers/RenamesController.cs tests/ReachCommander.IntegrationTests/BatchRenamesApiTests.cs tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: expose protected single rename previews"
```

### Task 3: Typed Angular exact-rename API

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Modify: `client/reach-commander-ui/src/app/testing/commander-api-test-base.ts`

**Interfaces:**
- Consumes: backend `POST /api/renames/preview` and existing `BatchRenamePreviewDto`/`executeBatchRename`.
- Produces: `ExactRenamePreviewRequestDto` and `CommanderApiPort.previewRename(request)`.

- [ ] **Step 1: Write the failing HTTP adapter test**

```typescript
it('posts only literal logical values when previewing one rename', async () => {
  const body: ExactRenamePreviewRequestDto = {
    sourceId: 'media library',
    directoryPath: '/Movies & TV',
    entryPath: '/Movies & TV/[old].mkv',
    newName: '[N]-literal.mkv',
  };
  const expected = previewResponse();
  const result = api.previewRename(body);
  const request = http.expectOne('/api/renames/preview');

  expect(request.request.method).toBe('POST');
  expect(request.request.body).toEqual(body);
  expect(JSON.stringify(request.request.body)).not.toContain('physical');
  request.flush(expected);
  await expect(result).resolves.toEqual(expected);
});
```

- [ ] **Step 2: Run the focused test and verify the typed method is missing**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/core/api/reach-commander-api.spec.ts
```

Expected: TypeScript compilation fails for the missing request type and `previewRename` method.

- [ ] **Step 3: Add the request type and adapter method**

Add to `api.models.ts`:

```typescript
export interface ExactRenamePreviewRequestDto {
  readonly sourceId: string;
  readonly directoryPath: string;
  readonly entryPath: string;
  readonly newName: string;
}
```

Add to `CommanderApiPort`:

```typescript
abstract previewRename(
  request: ExactRenamePreviewRequestDto,
): Promise<BatchRenamePreviewDto>;
```

Add to `ReachCommanderApi` beside the batch methods:

```typescript
previewRename(request: ExactRenamePreviewRequestDto): Promise<BatchRenamePreviewDto> {
  return firstValueFrom(
    this.http.post<BatchRenamePreviewDto>('/api/renames/preview', request),
  );
}
```

Add an unsupported default to `CommanderApiTestBase` so unrelated fakes stay focused:

```typescript
override previewRename(
  _request: ExactRenamePreviewRequestDto,
): Promise<BatchRenamePreviewDto> {
  return unsupported();
}
```

- [ ] **Step 4: Run the API adapter tests**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/core/api/reach-commander-api.spec.ts
```

Expected: all `ReachCommanderApi` tests pass and the request contains exactly four logical fields.

- [ ] **Step 5: Commit the typed API slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- client/reach-commander-ui/src/app/core/api/api.models.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts client/reach-commander-ui/src/app/testing/commander-api-test-base.ts
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: add typed single rename API"
```

### Task 4: SingleRenameStore with race-safe live preview

**Files:**
- Create: `client/reach-commander-ui/src/app/core/state/single-rename.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/single-rename-store.ts`
- Create: `client/reach-commander-ui/src/app/core/state/single-rename-store.spec.ts`

**Interfaces:**
- Consumes: `CommanderApiPort.previewRename`, `executeBatchRename`, `BatchRenamePreviewDto`, and `BatchRenameOperationDto`.
- Produces: `SingleRenameContext`, `SingleRenameCompletion`, `SingleRenameState`, and injectable `SingleRenameStore` with `open`, `setName`, `execute`, `close`, and `setCompletionHandler`.

- [ ] **Step 1: Write failing store tests for debounce, stale responses, conflict, success, and reset**

Use fake timers and a `CommanderApiTestBase` fake. Cover these exact assertions:

```typescript
it('debounces literal names and ignores a late preview', async () => {
  const first = deferred<BatchRenamePreviewDto>();
  const second = deferred<BatchRenamePreviewDto>();
  api.previewHandler = (request) =>
    request.newName === 'first.txt' ? first.promise : second.promise;
  store.open(context());
  store.setName('first.txt');
  await vi.advanceTimersByTimeAsync(250);
  store.setName('[N]-literal.txt');
  await vi.advanceTimersByTimeAsync(250);
  second.resolve(previewResponse('[N]-literal.txt'));
  await settlePromises();
  first.resolve(previewResponse('first.txt'));
  await settlePromises();

  expect(store.state().preview?.rows[0]?.newName).toBe('[N]-literal.txt');
  expect(api.previewRequests.at(-1)?.newName).toBe('[N]-literal.txt');
});

it('keeps conflicts non-executable and preserves the requested name', async () => {
  api.previewHandler = () => Promise.resolve(previewResponse('taken.txt', {
    canExecute: false,
    invalidCount: 1,
    rows: [previewRow('taken.txt', 'conflict', 'The destination name is already in use.')],
  }));
  store.open(context());
  store.setName('taken.txt');
  await vi.advanceTimersByTimeAsync(250);

  expect(store.canExecute()).toBe(false);
  expect(store.state().newName).toBe('taken.txt');
});

it('executes the current plan and emits one logical completion', async () => {
  const completed = vi.fn();
  store.setCompletionHandler(completed);
  api.previewHandler = () => Promise.resolve(previewResponse('renamed.txt'));
  api.executeHandler = () => Promise.resolve(operationResponse('/Movies/renamed.txt'));
  store.open(context());
  store.setName('renamed.txt');
  await vi.advanceTimersByTimeAsync(250);

  expect(await store.execute()).toBe(true);
  expect(completed).toHaveBeenCalledWith(expect.objectContaining({
    newLogicalPath: '/Movies/renamed.txt',
  }));
});
```

- [ ] **Step 2: Run the new store spec and verify it fails to compile**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/core/state/single-rename-store.spec.ts
```

Expected: missing store/models compilation failure.

- [ ] **Step 3: Implement the state contracts and store**

Define the contracts:

```typescript
export interface SingleRenameContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
  readonly entry: FileEntryDto;
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
}

export interface SingleRenameCompletion {
  readonly context: SingleRenameContext;
  readonly newLogicalPath: string;
}

export interface SingleRenameState {
  readonly open: boolean;
  readonly context: SingleRenameContext | null;
  readonly newName: string;
  readonly preview: BatchRenamePreviewDto | null;
  readonly operation: BatchRenameOperationDto | null;
  readonly previewPending: boolean;
  readonly actionPending: boolean;
  readonly errorCode: string | null;
  readonly requestToken: number;
}
```

Implement `SingleRenameStore` with a 250 ms timer and request tokens. Its executable check must require `state.preview?.canExecute === true`, `changedCount === 1`, no pending action, and an open context. `setName` clears the previous preview immediately and does not request an empty string. Preview uses exactly:

```typescript
await this.api.previewRename({
  sourceId: context.sourceId,
  directoryPath: context.directoryPath,
  entryPath: context.entry.relativePath,
  newName: state.newName,
});
```

On execution, accept success only when `operation.status === 'completed'` and exactly one completed row exists. Emit:

```typescript
this.completionHandler?.({
  context,
  newLogicalPath: row.newPath,
});
```

Map only the stable rename/source/path Problem Details codes already accepted by `MultiRenameStore`; never retain server `detail`. Mark an expired preview non-executable using its existing expiry timestamp. `close()` clears both timers, invalidates response tokens, callback-safe state, preview, operation, and names.

- [ ] **Step 4: Run store tests**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/core/state/single-rename-store.spec.ts
```

Expected: all debounce, literal, conflict, current-plan execution, expiry, close, and safe-error tests pass.

- [ ] **Step 5: Commit the state slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- client/reach-commander-ui/src/app/core/state/single-rename.models.ts client/reach-commander-ui/src/app/core/state/single-rename-store.ts client/reach-commander-ui/src/app/core/state/single-rename-store.spec.ts
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: add single rename state machine"
```

### Task 5: Accessible compact Rename dialog

**Files:**
- Create: `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/single-rename/rename-dialog.component.spec.ts`

**Interfaces:**
- Consumes: `SingleRenameStore`.
- Produces: standalone `RenameDialogComponent` with `closeRequested` output and `data-testid="single-rename-dialog"`.

- [ ] **Step 1: Write failing dialog behavior tests**

Create a signal-backed fake store and assert:

```typescript
it('labels the entry type and selects the complete current name', () => {
  fixture.detectChanges();
  const dialog = fixture.nativeElement.querySelector('[role="dialog"]');
  const input = fixture.nativeElement.querySelector('#single-rename-name') as HTMLInputElement;

  expect(dialog.getAttribute('aria-modal')).toBe('true');
  expect(fixture.nativeElement.textContent).toContain('Rename file');
  expect(input.value).toBe('holiday.txt');
  expect(document.activeElement).toBe(input);
  expect(input.selectionStart).toBe(0);
  expect(input.selectionEnd).toBe('holiday.txt'.length);
});

it('keeps a conflict visible and Rename disabled', () => {
  fakeStore.state.set(openState({
    newName: 'taken.txt',
    preview: previewResponse('taken.txt', 'conflict', 'The destination name is already in use.'),
  }));
  fakeStore.canExecute.set(false);
  fixture.detectChanges();

  expect(fixture.nativeElement.textContent).toContain('already in use');
  expect(button('single-rename-submit').disabled).toBe(true);
});

it('maps Enter to execute and Escape to close only when safe', () => {
  fakeStore.canExecute.set(true);
  fixture.detectChanges();
  fixture.componentInstance.handleKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
  expect(fakeStore.execute).toHaveBeenCalledOnce();

  fakeStore.state.update((state) => ({ ...state, actionPending: false }));
  const closed = vi.fn();
  fixture.componentInstance.closeRequested.subscribe(closed);
  fixture.componentInstance.handleKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));
  expect(closed).toHaveBeenCalledOnce();
});
```

- [ ] **Step 2: Run the component spec and verify the component is missing**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/features/commander/single-rename/rename-dialog.component.spec.ts
```

Expected: import/compilation failure because the component does not exist.

- [ ] **Step 3: Implement the standalone dialog**

Use `A11yModule`, `cdkTrapFocus`, `cdkTrapFocusAutoCapture`, and an input `ViewChild`. In `ngAfterViewInit`, call `focus({ preventScroll: true })` and `select()`. Stop propagation for all dialog keydown events; Escape emits `closeRequested` only when `actionPending` is false, and Enter calls `store.execute()` only when `canExecute()` is true.

The template must use this structure and stable test IDs:

```html
<div class="single-rename-backdrop" data-testid="single-rename-dialog">
  <dialog open aria-modal="true" aria-labelledby="single-rename-title"
    cdkTrapFocus [cdkTrapFocusAutoCapture]="true">
    <header>
      <div>
        <span class="eyebrow">Controlled file operation</span>
        <h2 id="single-rename-title">
          Rename {{ store.state().context?.entry?.type === 'directory' ? 'folder' : 'file' }}
        </h2>
      </div>
      <button type="button" aria-label="Close Rename"
        [disabled]="store.state().actionPending" (click)="requestClose()">×</button>
    </header>
    <div class="dialog-body">
      <section class="destination">
        <span>Location</span>
        <code>{{ store.state().context?.sourceName }}:{{ store.state().context?.directoryPath }}</code>
      </section>
      <label for="single-rename-name">New name</label>
      <input #nameInput id="single-rename-name" autocomplete="off"
        [value]="store.state().newName" [disabled]="store.state().actionPending"
        aria-describedby="single-rename-status"
        (input)="store.setName($any($event.target).value)" />
      <div id="single-rename-status" aria-live="polite">
        @if (store.state().previewPending) { <p>Checking name…</p> }
        @if (previewMessage(); as message) {
          <p [class.error]="previewBlocked()" [attr.role]="previewBlocked() ? 'alert' : 'status'">
            {{ message }}
          </p>
        }
      </div>
    </div>
    <footer>
      <button type="button" [disabled]="store.state().actionPending" (click)="requestClose()">Cancel</button>
      <button type="button" class="primary" data-testid="single-rename-submit"
        [disabled]="!store.canExecute()" (click)="submit()">
        {{ store.state().actionPending ? 'Renaming…' : 'Rename' }}
      </button>
    </footer>
  </dialog>
</div>
```

Render preview row messages directly for `invalid`, `conflict`, and `unchanged`; map transport codes to safe copy like the Multi-Rename dialog. Use the existing Create Directory CSS variables and modal dimensions, plus Norton-theme-compatible variables only—no hardcoded light-theme surface colors.

- [ ] **Step 4: Run the dialog and store specs together**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/core/state/single-rename-store.spec.ts --include=src/app/features/commander/single-rename/rename-dialog.component.spec.ts
```

Expected: both specs pass with focus inside the modal, complete-name selection, conflict feedback, and safe keyboard behavior.

- [ ] **Step 5: Commit the dialog slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- client/reach-commander-ui/src/app/features/commander/single-rename
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: add accessible rename dialog"
```

### Task 6: F4 command, cursor-only context, and matching-panel refresh

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Consumes: `SingleRenameStore`, `RenameDialogComponent`, `SingleRenameContext`, and `SingleRenameCompletion`.
- Produces: `CommanderStore.createSingleRenameContext(side)`, `refreshAfterRename(completion)`, enabled F4 command, and modal lifecycle.

- [ ] **Step 1: Write failing cursor and refresh state tests**

```typescript
it('captures only the cursor row for single rename even with a different selection', async () => {
  const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
  api.entries.set('downloads:/', [entry('alpha.txt'), entry('beta.txt')]);
  const store = new CommanderStore(api);
  await store.initialize();
  store.selectWithPointer('left', 0, 'replace');
  store.moveCursor('left', 1);

  expect(store.createSingleRenameContext('left')?.entry.name).toBe('beta.txt');
});

it('refreshes every matching panel and focuses the renamed row in the origin', async () => {
  const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
  api.entries.set('downloads:/', [entry('old.txt')]);
  const store = new CommanderStore(api);
  await store.initialize();
  api.entries.set('downloads:/', [entry('new.txt')]);

  await store.refreshAfterRename({
    context: store.createSingleRenameContext('left')!,
    newLogicalPath: '/new.txt',
  });

  expect(store.leftPanel().entries[0]?.name).toBe('new.txt');
  expect(store.rightPanel().entries[0]?.name).toBe('new.txt');
  expect(buildVisibleRows(store.leftPanel())[store.leftPanel().cursorIndex]?.relativePath)
    .toBe('/new.txt');
});
```

Add shell/command-bar tests that assert writable file and folder contexts enable F4, read-only/archive/symlink/parent/other contexts disable it with an exact reason, clicking F4 opens the single store, modal state blocks commander navigation, completion refreshes matching panels, and close restores the opener/panel focus.

- [ ] **Step 2: Run the focused Commander tests and verify missing interfaces fail**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false --include=src/app/core/state/commander-store.spec.ts --include=src/app/features/commander/command-bar/command-bar.component.spec.ts --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: compilation/test failures for missing single-rename context, `rename` availability, and F4 behavior.

- [ ] **Step 3: Implement cursor-only state and refresh coordination**

Add `createSingleRenameContext` using only `buildVisibleRows(panel)[panel.cursorIndex]`. Return null for parent rows or archive locations, otherwise capture source metadata and the exact entry. Add `refreshAfterRename`:

```typescript
async refreshAfterRename(completion: SingleRenameCompletion): Promise<void> {
  const matching = (['left', 'right'] as const).filter((side) => {
    const state = this.panel(side)();
    const tab = activeTab(state);
    return tab?.location.kind === 'filesystem' &&
      tab.location.sourceId === completion.context.sourceId &&
      tab.location.path === completion.context.directoryPath;
  });

  for (const side of matching) {
    this.clearSelection(side);
  }
  await Promise.all(matching.map((side) => this.refresh(side)));

  if (matching.includes(completion.context.panelSide)) {
    const state = this.panel(completion.context.panelSide)();
    const cursorIndex = buildVisibleRows(state).findIndex(
      (row) => !row.isParent && row.relativePath === completion.newLogicalPath,
    );
    if (cursorIndex >= 0) {
      this.updatePanel(completion.context.panelSide, { ...state, cursorIndex });
    }
  }
}
```

- [ ] **Step 4: Enable F4 and integrate modal lifecycle**

Extend `FileCommandAvailability` with:

```typescript
readonly rename: { readonly enabled: boolean; readonly reason: string | null };
```

Use it for the F4 action instead of the reserved disabled action. In the shell, compute the first disabled reason in this order: no cursor target, archive, unavailable source, read-only source, symbolic link, unsupported type. Inject `SingleRenameStore`, register it with protected-state reset, import `RenameDialogComponent`, and route `handleFunctionKey('F4')` to `openSingleRename()`.

Add the modal to the shell template:

```html
@if (singleRename.state().open) {
  <app-rename-dialog (closeRequested)="closeSingleRename()" />
}
```

On store completion, call `store.refreshAfterRename(completion)`, close the dialog only after a completed operation has been captured, and restore focus to the originating F4 button or panel. While the dialog is open, the component HostListener consumes Enter/Escape and the shell must ignore all other Commander commands.

Update the status hint and F9 menu copy to include `<kbd>F4</kbd> rename` while retaining `<kbd>Ctrl+M</kbd> multi-rename`.

- [ ] **Step 5: Run all Angular unit tests and production build**

```powershell
Set-Location client\reach-commander-ui
npm test -- --watch=false
npm run build
```

Expected: every Angular test passes and the production build completes without template/type errors.

- [ ] **Step 6: Commit the Commander integration slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- client/reach-commander-ui/src/app/core/state/commander-store.ts client/reach-commander-ui/src/app/core/state/commander-store.spec.ts client/reach-commander-ui/src/app/features/commander/command-bar client/reach-commander-ui/src/app/features/commander/commander-shell
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: enable F4 file and folder rename"
```

### Task 7: Browser acceptance, README, and full verification

**Files:**
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/specs/single-rename.spec.ts`
- Modify: `README.md`

**Interfaces:**
- Consumes: complete authenticated exact-rename API and F4 UI.
- Produces: deterministic end-to-end acceptance and public feature documentation.

- [ ] **Step 1: Seed isolated single-rename fixtures and write failing browser tests**

Add:

```typescript
mkdirSync(join(downloadsRoot, 'Single Rename Lab', 'Folder Before'), { recursive: true });
writeFileSync(join(downloadsRoot, 'Single Rename Lab', 'file-before.txt'), 'file rename\n');
writeFileSync(join(downloadsRoot, 'Single Rename Lab', 'literal-before.txt'), 'literal rename\n');
writeFileSync(join(downloadsRoot, 'Single Rename Lab', 'conflict-source.txt'), 'source\n');
writeFileSync(join(downloadsRoot, 'Single Rename Lab', 'taken.txt'), 'destination\n');
```

Create browser tests:

```typescript
import { expect, test } from '@playwright/test';

async function openLab(page: import('@playwright/test').Page) {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByText('Single Rename Lab', { exact: true }).dblclick();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/Single Rename Lab');
  return left;
}

test('F4 renames one file and accepts a literal bracket name', async ({ page }) => {
  const left = await openLab(page);
  await left.getByText('literal-before.txt', { exact: true }).click();
  await page.keyboard.press('F4');
  const dialog = page.getByTestId('single-rename-dialog');
  await dialog.getByLabel('New name').fill('[N]-literal.txt');
  await expect(dialog.getByTestId('single-rename-submit')).toBeEnabled();
  await dialog.getByTestId('single-rename-submit').click();
  await expect(dialog).toBeHidden();
  await expect(left.getByText('[N]-literal.txt', { exact: true })).toBeVisible();
});

test('the F4 command-bar action renames a folder', async ({ page }) => {
  const left = await openLab(page);
  await left.getByText('Folder Before', { exact: true }).click();
  await page.locator('[data-key="F4"]').click();
  const dialog = page.getByTestId('single-rename-dialog');
  await dialog.getByLabel('New name').fill('Folder After');
  await dialog.getByTestId('single-rename-submit').click();
  await expect(left.getByText('Folder After', { exact: true })).toBeVisible();
  await left.getByText('Folder After', { exact: true }).dblclick();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/Single Rename Lab/Folder After');
});

test('an occupied name never overwrites either file', async ({ page }) => {
  const left = await openLab(page);
  await left.getByText('conflict-source.txt', { exact: true }).click();
  await page.keyboard.press('F4');
  const dialog = page.getByTestId('single-rename-dialog');
  await dialog.getByLabel('New name').fill('taken.txt');
  await expect(dialog).toContainText('already in use');
  await expect(dialog.getByTestId('single-rename-submit')).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(left.getByText('conflict-source.txt', { exact: true })).toBeVisible();
  await expect(left.getByText('taken.txt', { exact: true })).toBeVisible();
});
```

Add a read-only assertion using the existing Archive source and `locked.txt`: F4 is disabled and its title includes `read-only`.

- [ ] **Step 2: Build and run the new browser spec**

```powershell
Set-Location client\reach-commander-ui
npm run build
Set-Location ..\..\tests\e2e
npm test -- --grep "rename"
```

Expected before any missed integration fix: the focused scenario identifies the exact gap. Expected after correction: all single-rename and existing Multi-Rename scenarios pass.

- [ ] **Step 3: Update public documentation**

In `README.md`:

- add single-item file/folder rename to the controlled-operation feature list;
- remove F4 rename from exclusions and Multi-Rename limitations;
- add a short `Single Rename` section describing cursor-only F4, literal complete names, no overwrite, and server-authoritative preview;
- change the shortcut table from reserved F4 to `F4 | Rename the focused file or directory` while leaving F3 reserved;
- include Single Rename in the current write-path security list and Milestone 2 description.

- [ ] **Step 4: Run final cross-layer verification**

```powershell
dotnet test ReachCommander.slnx -c Release
Set-Location client\reach-commander-ui
npm test -- --watch=false
npm run build
Set-Location ..\..\tests\e2e
npm test
```

Expected: backend, Angular, production build, authentication setup, existing browser scenarios, and new file/folder rename acceptance all pass.

- [ ] **Step 5: Inspect the final diff and preserve unrelated files**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' diff --check
git -c safe.directory='D:/Work/Personal/Reach Commander' status --short
```

Expected: no whitespace errors; only intended rename/docs files are staged or modified, and `?? NC-theme.png` remains untracked and untouched.

- [ ] **Step 6: Commit the acceptance and documentation slice**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add -- tests/e2e/support/seed-fixtures.ts tests/e2e/specs/single-rename.spec.ts README.md
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "test: cover single file and folder rename"
```

- [ ] **Step 7: Record the final verification evidence**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' status --short --branch
git -c safe.directory='D:/Work/Personal/Reach Commander' log -8 --oneline
```

Expected: `master` contains the plan plus the seven implementation commits, the tracked worktree is clean, and only the pre-existing untracked `NC-theme.png` remains.
