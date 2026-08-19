# ReachCommander Milestone 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the production-quality, read-only ReachCommander dual-pane file manager foundation defined in the approved Milestone 1 design.

**Architecture:** A four-project .NET 10 modular monolith exposes source and browsing APIs, with physical path confinement isolated in infrastructure. An Angular 22 standalone client uses Signals for two independent pane states and is published into the ASP.NET Core host for a single-origin Docker deployment.

**Tech Stack:** .NET 10, ASP.NET Core 10 controllers, C# 14, Angular 22, TypeScript strict mode, Angular Signals, Angular CDK, xUnit, `WebApplicationFactory`, Vitest through Angular CLI, Playwright, Docker, and Docker Compose.

## Global Constraints

- Use .NET 10 and ASP.NET Core 10.
- Use Angular 22 standalone components and Angular Signals in TypeScript strict mode.
- Use `ReachCommander` consistently for product text, namespaces, projects, and container names.
- Implement Milestone 1 only; do not add mutation endpoints, transfers, SignalR, background workers, authentication, preview/download, upload, thumbnails, archives, recursive search, or device mounting.
- Browser requests contain only a source ID and logical relative path; physical filesystem paths never appear in API responses or client state.
- Every filesystem path passes through `IPathSecurityService` before use.
- Automated tests use temporary filesystem trees only.
- Production serves Angular, `/api/*`, and `/health` from one ASP.NET Core origin.
- Default production source configuration path is `/config/sources.json`.
- Docker exposes host port `8092` to container port `8080`, runs as `1000:1000`, and mounts only explicitly configured roots.
- No production code is written before its behavior test has failed for the expected reason.

---

## Planned File Map

```text
ReachCommander.slnx                       Solution membership
global.json                              Pins the .NET 10 SDK feature band
Directory.Build.props                    Nullable, analyzers, warnings policy
src/ReachCommander.Domain/               Immutable source and file concepts
src/ReachCommander.Application/          Browser use cases and ports
src/ReachCommander.Infrastructure/       JSON config, path security, filesystem
src/ReachCommander.Api/                  Controllers, errors, host, static SPA
client/reach-commander-ui/                Angular standalone application
tests/ReachCommander.UnitTests/           Domain/application/infrastructure tests
tests/ReachCommander.IntegrationTests/    In-process HTTP contract tests
tests/e2e/                                Playwright browser flows
config/sources.json                       Docker-oriented sample configuration
dev-sources/                              Git-ignored local demo source trees
Dockerfile                                Angular + .NET multi-stage image
compose.yaml                              Safe sample deployment
.dockerignore                             Small, deterministic Docker context
.gitignore                                Build output and local source exclusions
README.md                                 Operations and product documentation
```

### Task 1: Solution skeleton and validated source configuration

**Files:**

- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `.gitignore`
- Create: `ReachCommander.slnx`
- Create: `src/ReachCommander.Domain/ReachCommander.Domain.csproj`
- Create: `src/ReachCommander.Domain/Sources/SourceDefinition.cs`
- Create: `src/ReachCommander.Domain/Sources/SourceSnapshot.cs`
- Create: `src/ReachCommander.Application/ReachCommander.Application.csproj`
- Create: `src/ReachCommander.Application/Sources/ISourceCatalog.cs`
- Create: `src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj`
- Create: `src/ReachCommander.Infrastructure/Configuration/ReachCommanderOptions.cs`
- Create: `src/ReachCommander.Infrastructure/Configuration/SourcesFile.cs`
- Create: `src/ReachCommander.Infrastructure/Configuration/JsonSourceCatalog.cs`
- Create: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Create: `src/ReachCommander.Api/ReachCommander.Api.csproj`
- Create: `tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj`
- Create: `tests/ReachCommander.UnitTests/Sources/JsonSourceCatalogTests.cs`
- Create: `tests/ReachCommander.UnitTests/Support/TemporaryDirectory.cs`

**Interfaces:**

- Produces: `SourceDefinition(string Id, string Name, string RootPath, bool IsReadOnly, bool DefaultLeft, bool DefaultRight)`.
- Produces: `SourceSnapshot(string Id, string Name, bool IsAvailable, bool IsReadOnly, long? TotalBytes, long? UsedBytes, long? FreeBytes, bool DefaultLeft, bool DefaultRight)`.
- Produces: `ISourceCatalog.GetDefinitionsAsync(CancellationToken)`, `GetSnapshotsAsync(CancellationToken)`, and `GetRequiredAsync(string, CancellationToken)`.
- Produces: `ReachCommanderOptions.SourcesPath`, configured under section `ReachCommander`.

- [ ] **Step 1: Scaffold the solution and projects**

Run from the repository root:

```powershell
dotnet new globaljson --sdk-version 10.0.400 --roll-forward latestFeature
dotnet new sln --name ReachCommander --format slnx
dotnet new classlib --name ReachCommander.Domain --output src/ReachCommander.Domain --framework net10.0
dotnet new classlib --name ReachCommander.Application --output src/ReachCommander.Application --framework net10.0
dotnet new classlib --name ReachCommander.Infrastructure --output src/ReachCommander.Infrastructure --framework net10.0
dotnet new webapi --name ReachCommander.Api --output src/ReachCommander.Api --framework net10.0 --use-controllers
dotnet new xunit --name ReachCommander.UnitTests --output tests/ReachCommander.UnitTests --framework net10.0
dotnet new xunit --name ReachCommander.IntegrationTests --output tests/ReachCommander.IntegrationTests --framework net10.0
dotnet sln ReachCommander.slnx add src/ReachCommander.Domain src/ReachCommander.Application src/ReachCommander.Infrastructure src/ReachCommander.Api tests/ReachCommander.UnitTests tests/ReachCommander.IntegrationTests
dotnet add src/ReachCommander.Application reference src/ReachCommander.Domain
dotnet add src/ReachCommander.Infrastructure reference src/ReachCommander.Domain src/ReachCommander.Application
dotnet add src/ReachCommander.Api reference src/ReachCommander.Application src/ReachCommander.Infrastructure
dotnet add tests/ReachCommander.UnitTests reference src/ReachCommander.Domain src/ReachCommander.Application src/ReachCommander.Infrastructure
dotnet add tests/ReachCommander.IntegrationTests reference src/ReachCommander.Api
dotnet add tests/ReachCommander.IntegrationTests package Microsoft.AspNetCore.Mvc.Testing --version 10.0.11
```

Create `Directory.Build.props` with nullable enabled, implicit usings enabled, analyzers at the latest installed level, and warnings as errors outside generated Angular assets:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnalysisLevel>latest</AnalysisLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Create `.gitignore` before the first build with entries for `bin/`, `obj/`, `.vs/`, `node_modules/`, Angular output, Playwright output, `artifacts/`, `dev-sources/**` except `.gitkeep`, and local editor/system files. Remove generated WeatherForecast and `UnitTest1` files so the solution starts with only intentional code.

- [ ] **Step 2: Write failing source-validation tests**

Add theory data proving duplicate IDs, uppercase/invalid IDs, relative roots, empty names, multiple left defaults, multiple right defaults, and no enabled sources fail. Add a success test proving an unavailable enabled source remains in snapshots without exposing its root:

```csharp
[Theory]
[MemberData(nameof(InvalidFiles))]
public async Task GetDefinitionsAsync_rejects_invalid_configuration(
    string json,
    string expectedMessage)
{
    using var directory = new TemporaryDirectory();
    var path = directory.Write("sources.json", json);
    var catalog = JsonSourceCatalogTestsFactory.Create(path);

    var error = await Assert.ThrowsAsync<SourceConfigurationException>(
        () => catalog.GetDefinitionsAsync(CancellationToken.None));

    Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 3: Run the source tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests --filter FullyQualifiedName~JsonSourceCatalogTests
```

Expected: compilation fails because the source records, exception, and catalog do not exist.

- [ ] **Step 4: Implement the immutable models and JSON catalog**

Use this public contract:

```csharp
public interface ISourceCatalog
{
    ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken);
    ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken);
}
```

`JsonSourceCatalog` reads the configured file once, deserializes case-insensitively, validates all records as a set, stores an immutable enabled-source collection, and uses `DriveInfo` only when the root exists and is accessible. It catches platform capacity exceptions per source and returns null capacity rather than making discovery fail. `GetRequiredAsync` throws `SourceNotFoundException` for unknown IDs.

- [ ] **Step 5: Run tests and the solution build**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests --filter FullyQualifiedName~JsonSourceCatalogTests
dotnet build ReachCommander.slnx -c Release
```

Expected: all source tests pass and the Release build exits 0 with no warnings.

- [ ] **Step 6: Commit the source slice**

```powershell
git add .gitignore global.json Directory.Build.props ReachCommander.slnx src tests
git commit -m "feat: add validated source configuration"
```

### Task 2: Canonical path security

**Files:**

- Create: `src/ReachCommander.Application/Files/IPathSecurityService.cs`
- Create: `src/ReachCommander.Application/Files/ResolvedSourcePath.cs`
- Create: `src/ReachCommander.Application/Files/FileAccessExceptions.cs`
- Create: `src/ReachCommander.Infrastructure/Security/PathSecurityService.cs`
- Create: `tests/ReachCommander.UnitTests/Security/PathSecurityServiceTests.cs`
- Create: `tests/ReachCommander.UnitTests/Support/SymbolicLinkSupport.cs`

**Interfaces:**

- Consumes: `ISourceCatalog.GetRequiredAsync` and `SourceDefinition.RootPath`.
- Produces: `IPathSecurityService.ResolveAsync(string sourceId, string logicalPath, CancellationToken)`.
- Produces: `ResolvedSourcePath(SourceDefinition Source, string LogicalPath, string PhysicalPath)`; this type never leaves application/infrastructure code.
- Produces: `InvalidLogicalPathException`, `PathConfinementException`, `SourceUnavailableException`, and `EntryNotFoundException`.

- [ ] **Step 1: Write failing normalization and traversal tests**

Cover `/`, repeated separators, `.` segments, backslash input, `../`, encoded-looking literal segments, drive-qualified input, UNC input, null bytes, sibling-prefix roots, missing entries, and case behavior appropriate to the current platform:

```csharp
[Theory]
[InlineData("/../secret")]
[InlineData("../../secret")]
[InlineData("C:/Windows/System32")]
[InlineData("//server/share")]
public async Task ResolveAsync_rejects_paths_that_can_escape(string logicalPath)
{
    var service = CreateService(_sourceRoot);

    await Assert.ThrowsAnyAsync<FileAccessException>(
        () => service.ResolveAsync("media", logicalPath, CancellationToken.None).AsTask());
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests --filter FullyQualifiedName~PathSecurityServiceTests
```

Expected: compilation fails because `PathSecurityService` and its contract do not exist.

- [ ] **Step 3: Implement lexical confinement**

Normalize browser paths to `/segment/segment`, reject physical-root syntax before separator conversion, combine only decoded logical segments, call `Path.GetFullPath`, and verify containment using `Path.GetRelativePath`:

```csharp
private static bool IsWithin(string root, string candidate)
{
    var relative = Path.GetRelativePath(root, candidate);
    return relative.Length == 0 ||
        (!Path.IsPathRooted(relative) &&
         !relative.Equals("..", StringComparison.Ordinal) &&
         !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
```

Choose `StringComparison.OrdinalIgnoreCase` only on Windows; use `StringComparison.Ordinal` on Linux and macOS. Do not use string-prefix containment.

- [ ] **Step 4: Add failing symlink tests**

Create one link targeting a child inside the root and one targeting a sibling temporary directory. Assert the inside link resolves and the outside link throws `PathConfinementException`. If the OS denies link creation, use xUnit's runtime skip mechanism with the captured platform reason rather than treating the test as passed.

- [ ] **Step 5: Run the symlink tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests --filter "FullyQualifiedName~PathSecurityServiceTests&Name~symlink"
```

Expected: the escaping-link test fails because lexical containment alone accepts the link.

- [ ] **Step 6: Implement component-by-component link resolution**

Resolve the configured source root fully first. Walk each existing candidate component using `FileSystemInfo.LinkTarget` and `ResolveLinkTarget(returnFinalTarget: true)`. Re-check containment after every resolved component and once for the final physical path. Check cancellation between components. Preserve the normalized logical path in the result.

- [ ] **Step 7: Run all path-security tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests --filter FullyQualifiedName~PathSecurityServiceTests
```

Expected: all lexical, platform, and available symlink tests pass.

- [ ] **Step 8: Commit path security**

```powershell
git add src/ReachCommander.Application/Files src/ReachCommander.Infrastructure/Security tests/ReachCommander.UnitTests/Security tests/ReachCommander.UnitTests/Support
git commit -m "feat: confine filesystem paths to sources"
```

### Task 3: Read-only filesystem browser

**Files:**

- Create: `src/ReachCommander.Domain/Files/FileEntryType.cs`
- Create: `src/ReachCommander.Domain/Files/FileEntry.cs`
- Create: `src/ReachCommander.Application/Files/IFileBrowser.cs`
- Create: `src/ReachCommander.Infrastructure/FileSystem/LocalFileBrowser.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Create: `tests/ReachCommander.UnitTests/Files/LocalFileBrowserTests.cs`

**Interfaces:**

- Consumes: `IPathSecurityService.ResolveAsync`.
- Produces: `IFileBrowser.ListAsync(string sourceId, string logicalPath, CancellationToken)` and `GetInfoAsync(...)`.
- Produces: `FileEntry(string Name, string RelativePath, FileEntryType Type, long? Size, DateTimeOffset ModifiedAt, string? Extension, bool IsReadOnly, bool IsSymbolicLink, string Attributes)`.

- [ ] **Step 1: Write failing directory and metadata tests**

Seed a temporary source with a directory, extensionless file, `.hidden` file, normal file, read-only file, and safe symlink. Assert logical paths, null directory sizes, extensions without a leading dot, UTC-aware modified timestamps, attributes, read-only flags, and cancellation:

```csharp
[Fact]
public async Task ListAsync_returns_only_logical_metadata()
{
    var entries = await _browser.ListAsync("downloads", "/Complete", CancellationToken.None);

    Assert.All(entries, entry => Assert.StartsWith("/Complete/", entry.RelativePath));
    Assert.DoesNotContain(entries, entry => entry.RelativePath.Contains(_temporaryRoot, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 2: Run browser tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests --filter FullyQualifiedName~LocalFileBrowserTests
```

Expected: compilation fails because file records and `LocalFileBrowser` do not exist.

- [ ] **Step 3: Implement list and info behavior**

Enumerate with `Directory.EnumerateFileSystemEntries`. Before mapping each entry, call `cancellationToken.ThrowIfCancellationRequested()`. Map `FileInfo` and `DirectoryInfo` into immutable records; do not sort in the backend. Translate expected `DirectoryNotFoundException`, `FileNotFoundException`, and access failures into the typed application exceptions while letting unexpected failures reach centralized logging.

- [ ] **Step 4: Run browser and full backend unit tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests
```

Expected: all unit tests pass with no warnings.

- [ ] **Step 5: Commit read-only browsing**

```powershell
git add src/ReachCommander.Domain/Files src/ReachCommander.Application/Files src/ReachCommander.Infrastructure tests/ReachCommander.UnitTests/Files
git commit -m "feat: add read-only filesystem browsing"
```

### Task 4: HTTP API and Problem Details

**Files:**

- Create: `src/ReachCommander.Api/Contracts/SourceDto.cs`
- Create: `src/ReachCommander.Api/Contracts/FileEntryDto.cs`
- Create: `src/ReachCommander.Api/Controllers/SourcesController.cs`
- Create: `src/ReachCommander.Api/Controllers/FilesController.cs`
- Create: `src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/Program.cs`
- Create: `src/ReachCommander.Api/appsettings.json`
- Create: `src/ReachCommander.Api/appsettings.Development.json`
- Create: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Create: `tests/ReachCommander.IntegrationTests/SourcesApiTests.cs`
- Create: `tests/ReachCommander.IntegrationTests/FilesApiTests.cs`
- Create: `tests/ReachCommander.IntegrationTests/ErrorContractTests.cs`

**Interfaces:**

- Consumes: `ISourceCatalog`, `IFileBrowser`, and typed file-access exceptions.
- Produces: `GET /api/sources`, `GET /api/files`, `GET /api/files/info`, and `GET /health`.
- Produces: Problem Details extension `code` with values `invalid_path`, `path_forbidden`, `source_not_found`, `entry_not_found`, `source_unavailable`, and `unexpected_error`.

- [ ] **Step 1: Write failing integration contract tests**

Use `WebApplicationFactory<Program>` with a temporary JSON file and temporary source roots. Assert exact status codes, content types, DTO camel casing, unavailable-source behavior, and absence of configured physical root strings from successful and failure payloads:

```csharp
[Fact]
public async Task Files_does_not_expose_physical_paths()
{
    var response = await _client.GetAsync("/api/files?sourceId=media&path=/Movies");
    var body = await response.Content.ReadAsStringAsync();

    response.EnsureSuccessStatusCode();
    Assert.DoesNotContain(_factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run integration tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests
```

Expected: tests fail because the controllers and production service registrations are absent.

- [ ] **Step 3: Implement controllers and error mapping**

Controllers derive from `ControllerBase`, use `[ApiController]`, explicit routes, cancellation tokens, and immutable DTOs. They map domain/application records manually to ensure `RootPath` and `PhysicalPath` cannot be serialized. `FileAccessExceptionHandler` implements `IExceptionHandler`, logs stable source/path fields without resolved paths, and writes Problem Details with the status/code mapping above.

- [ ] **Step 4: Configure the ASP.NET Core 10 host**

Register controllers, `AddProblemDetails`, `AddExceptionHandler<FileAccessExceptionHandler>`, OpenAPI, health checks, options, and infrastructure. Validate the source catalog before accepting requests. Configure production exception handling, HTTPS behavior appropriate to direct local development versus container/reverse-proxy hosting, API controllers, health, and OpenAPI in development. Expose `public partial class Program;` for the test factory. Static SPA hosting is added only after its failing integration test in Task 10.

- [ ] **Step 5: Run integration tests and Release build**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests
dotnet build ReachCommander.slnx -c Release
```

Expected: all integration tests pass and the Release build exits 0.

- [ ] **Step 6: Commit the API slice**

```powershell
git add src/ReachCommander.Api tests/ReachCommander.IntegrationTests
git commit -m "feat: expose secure read-only file APIs"
```

### Task 5: Angular workspace, contracts, API client, and store foundation

**Files:**

- Create: `client/reach-commander-ui/**` with Angular CLI 22
- Create: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Create: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Create: `client/reach-commander-ui/src/app/core/state/commander.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Create: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/app.config.ts`

**Interfaces:**

- Produces: `ReachCommanderApi.getSources()`, `listFiles(sourceId, path)`, and `getInfo(sourceId, path)` returning typed Observables.
- Produces: `PanelSide = 'left' | 'right'`, `DirectoryTab`, `PanelState`, `PanelViewState`, and `CommanderStore`.
- Produces: readonly signals `sources`, `leftPanel`, `rightPanel`, and `activePanel`.

- [ ] **Step 1: Scaffold Angular 22 with supported Node**

Use Node 22.22.3 or newer; the workspace runtime at implementation time is Node 24.19.0. Run:

```powershell
npx --yes @angular/cli@22 new reach-commander-ui --directory client/reach-commander-ui --routing --style scss --standalone --strict --skip-git --package-manager npm --ssr=false
Set-Location client/reach-commander-ui
npm install @angular/cdk@22
Set-Location ../..
```

Remove the generated welcome UI while preserving the generated test and build configuration.

- [ ] **Step 2: Write failing API and initial-state tests**

Provide `HttpClientTesting` and assert URL encoding uses only logical fields. Use a fake API to assert initialization chooses `defaultLeft` and `defaultRight`, creates one root tab in each pane, and keeps an unavailable default selected:

```typescript
it('initializes panes from independent configured defaults', async () => {
  api.sources.resolve([downloads, media]);
  await store.initialize();

  expect(store.leftPanel().sourceId).toBe('downloads');
  expect(store.rightPanel().sourceId).toBe('media');
  expect(store.leftPanel().tabs[0].path).toBe('/');
  expect(store.rightPanel().tabs[0].path).toBe('/');
});
```

- [ ] **Step 3: Run Angular tests and verify RED**

Run:

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false
```

Expected: compilation fails because the typed API and store do not exist.

- [ ] **Step 4: Implement contracts, API client, and initialization**

Define `PanelState` with `sourceId`, `tabs`, `activeTabId`, `cursorIndex`, `selectedItems: ReadonlySet<string>`, `sortColumn`, `sortDirection`, `filter`, `selectionAnchor`, `entries`, `loading`, `errorCode`, and `requestToken`. Keep writable signals private and expose `.asReadonly()`. `initialize()` loads sources once, creates safe fallback panels, and launches independent active-tab loads.

- [ ] **Step 5: Run Angular tests and production build**

Run:

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false
npm --prefix client/reach-commander-ui run build
```

Expected: all tests pass and Angular emits a production browser bundle.

- [ ] **Step 6: Commit the Angular foundation**

```powershell
git add client/reach-commander-ui
git commit -m "feat: add Angular commander state foundation"
```

### Task 6: Independent sources, tabs, navigation, and persistence

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/panel-persistence.ts`
- Create: `client/reach-commander-ui/src/app/core/state/panel-persistence.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/path-utils.ts`
- Create: `client/reach-commander-ui/src/app/core/state/path-utils.spec.ts`

**Interfaces:**

- Produces store commands: `activatePanel`, `selectSource`, `createTab`, `closeActiveTab`, `activateTab`, `navigateTo`, `navigateParent`, `refresh`, and `setPathFromEditor`.
- Produces `PanelPersistence.load(sources)` and `save(left, right, activePanel)` using storage key `reachcommander.panel-state.v1`.

- [ ] **Step 1: Write failing independence and tab tests**

Assert a left source/path change leaves the right panel byte-for-byte unchanged; source selection resets only the active tab; switching a tab restores its own source/path; closing the last tab replaces it with a root tab; and stale API results do not overwrite a newer request.

- [ ] **Step 2: Write failing persistence-repair tests**

Cover valid round trip, invalid JSON, unsupported version, removed source, invalid logical path, and unavailable configured default. Persist only tab source/path, active tab IDs, sort settings, filter, and active-panel identity; exclude entries, loading state, errors, cursor, and selection.

- [ ] **Step 3: Run targeted tests and verify RED**

Run:

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false --include src/app/core/state/commander-store.spec.ts --include src/app/core/state/panel-persistence.spec.ts
```

Expected: new command and persistence assertions fail.

- [ ] **Step 4: Implement immutable panel transitions and request tokens**

Every command updates only its target signal. Generate a monotonically increasing request token before each load and apply success/failure only when side, active tab, source, path, and token still match. Normalize client paths without interpreting physical syntax; the server remains authoritative for security.

- [ ] **Step 5: Implement versioned storage repair**

Parse unknown storage through explicit type guards. Repair invalid panes to configured defaults, remove tabs whose sources no longer exist, and ensure at least one tab per pane. Save through an Angular `effect` only after initialization completes.

- [ ] **Step 6: Run all Angular tests and commit**

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false
git add client/reach-commander-ui/src/app/core/state
git commit -m "feat: persist independent commander panes"
```

### Task 7: Sorting, filtering, cursor, and selection behavior

**Files:**

- Create: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.viewmodel.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.viewmodel.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Create: `client/reach-commander-ui/src/app/shared/pipes/file-size.pipe.ts`
- Create: `client/reach-commander-ui/src/app/shared/pipes/file-size.pipe.spec.ts`

**Interfaces:**

- Produces `buildVisibleRows(panel: PanelState): readonly FileTableRow[]`.
- Produces store commands `sortBy`, `setFilter`, `moveCursor`, `moveCursorPage`, `moveCursorBoundary`, `toggleCursorSelection`, `selectAllVisible`, `selectWithPointer`, and `clearSelection`.

- [ ] **Step 1: Write failing pure view-model tests**

Assert parent first, directories before files, stable per-group ascending/descending sorts for name/extension/size/modified/attributes, case-insensitive filtering by name and extension, and a clamped cursor after filtering.

- [ ] **Step 2: Write failing selection tests**

Assert Insert toggles and advances, Ctrl+A excludes parent, plain click selects one item, Ctrl+click toggles, Shift+click extends from the anchor, selection uses logical relative paths, and inactive-pane selection remains unchanged when active pane switches.

- [ ] **Step 3: Run targeted tests and verify RED**

Run:

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false --include src/app/features/commander/file-table/file-table.viewmodel.spec.ts --include src/app/core/state/commander-store.spec.ts
```

Expected: sort, filter, and selection tests fail because the functions are missing.

- [ ] **Step 4: Implement pure row derivation and store commands**

Use `Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })` for names. Keep the synthetic parent row outside sorting and filtering. Sort copies rather than API arrays. Clamp cursor to `[-1, visibleRows.length - 1]`, never place selection on the parent row, and replace `Set` instances on every signal update.

- [ ] **Step 5: Implement and test `FileSizePipe`**

Return an em dash for null directory sizes, `0 B` for zero, and IEC values such as `1.5 KiB`, `2.0 MiB`, and `3.0 GiB` with locale formatting.

- [ ] **Step 6: Run Angular tests and commit**

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false
git add client/reach-commander-ui/src/app
git commit -m "feat: add commander sorting and selection"
```

### Task 8: Centralized keyboard workflow

**Files:**

- Create: `client/reach-commander-ui/src/app/core/keyboard/commander-command.ts`
- Create: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.ts`
- Create: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`

**Interfaces:**

- Produces `CommanderCommand` discriminated union for navigation, selection, tabs, path focus, refresh, quick-filter text, escape, and F3-F9 actions.
- Produces `CommanderKeyboardService.start()` and `stop()` with exactly one document listener.

- [ ] **Step 1: Write failing key-mapping tests**

Create real `KeyboardEvent` instances and assert ArrowUp/Down, PageUp/Down, Home/End, Enter, Backspace, Tab, Insert, Ctrl+A/L/R/T/W, Escape, F3-F9, and printable characters map to commands. Assert ordinary input editing is ignored and `preventDefault` occurs only for dispatched application commands.

- [ ] **Step 2: Run keyboard tests and verify RED**

Run:

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false --include src/app/core/keyboard/commander-keyboard.service.spec.ts
```

Expected: compilation fails because the keyboard service does not exist.

- [ ] **Step 3: Implement mapping and lifecycle**

Use one stable bound handler registered by `start()` and removed by `stop()`. Detect `HTMLInputElement`, `HTMLTextAreaElement`, `HTMLSelectElement`, and `contentEditable`; preserve their editing keys except Escape. Dispatch semantic commands through an injected command sink rather than importing pane components.

- [ ] **Step 4: Connect commands to store behavior**

Enter opens a directory or applies the path editor; Backspace edits an active filter before parent navigation; Escape clears active transient state, then filter, then selection; Tab changes panes instantly; printable characters extend the active pane filter. F3-F9 update a non-blocking status message because milestone commands are disabled.

- [ ] **Step 5: Run all Angular tests and commit**

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false
git add client/reach-commander-ui/src/app/core client/reach-commander-ui/src/app/features
git commit -m "feat: centralize commander keyboard controls"
```

### Task 9: Dense dual-pane UI and accessible components

**Files:**

- Create: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/directory-tabs/directory-tabs.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/path-bar/path-bar.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/quick-filter/quick-filter.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.{ts,html,scss,spec.ts}`
- Create: `client/reach-commander-ui/src/app/features/commander/command-bar/command-bar.component.{ts,html,scss,spec.ts}`
- Modify: `client/reach-commander-ui/src/app/app.ts`
- Modify: `client/reach-commander-ui/src/app/app.html`
- Modify: `client/reach-commander-ui/src/styles.scss`

**Interfaces:**

- Consumes only store signals/commands and typed component inputs/outputs.
- Produces `data-testid` hooks for both source selectors, panels, paths, tables, rows, tabs, filters, and command buttons.

- [ ] **Step 1: Write failing component behavior tests**

Assert both panels render the full source list, unavailable buttons remain disabled/visible, active pane exposes `aria-current`, tabs use tablist semantics, sortable headers expose `aria-sort`, selected rows expose `aria-selected`, path editing commits/cancels correctly, and disabled mutation command buttons carry an explanatory accessible description.

- [ ] **Step 2: Run component tests and verify RED**

Run:

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false --include src/app/features/commander/**/*.spec.ts
```

Expected: component tests fail because the UI components do not exist.

- [ ] **Step 3: Implement shell, source, tab, path, and filter controls**

Use standalone `OnPush` components, signal inputs where supported, native buttons, tablist/tab roles, visible labels/tooltips, and event outputs that call store commands. Source buttons stay compact and display capacity/read-only/availability in a native-accessible popover or title/description treatment.

- [ ] **Step 4: Implement the dense details table**

Use semantic table markup inside a scroll viewport, compact rows, sticky headers, flexible Name column, bounded metadata columns, distinct folder/file/link icons made from original inline SVG or CSS, and programmatic cursor scrolling. Avoid card layouts and proprietary assets.

- [ ] **Step 5: Implement the permanent command bar and visual system**

Create dark-default CSS custom properties with a restrained light-theme media override, 30-34px rows, strong cyan active-pane border, subtle inactive selection, visible focus rings, reduced-motion rules, two equal desktop columns, and stacked narrow layout. Keep the command bar fixed at the bottom without covering panel content.

- [ ] **Step 6: Run tests, build, and commit**

```powershell
npm --prefix client/reach-commander-ui test -- --watch=false
npm --prefix client/reach-commander-ui run build
git add client/reach-commander-ui/src
git commit -m "feat: build accessible dual-pane commander UI"
```

### Task 10: Production assets, safe Docker deployment, and configuration samples

**Files:**

- Modify: `src/ReachCommander.Api/ReachCommander.Api.csproj`
- Create: `config/sources.json`
- Create: `Dockerfile`
- Create: `compose.yaml`
- Create: `.dockerignore`
- Create: `dev-sources/downloads/.gitkeep`
- Create: `dev-sources/media/.gitkeep`
- Create: `src/ReachCommander.Api/wwwroot/.gitkeep`
- Create: `tests/ReachCommander.IntegrationTests/StaticHostingTests.cs`

**Interfaces:**

- Produces one origin on ASP.NET Core port `8080` with `/`, `/api/*`, and `/health`.
- Produces Docker host mapping `8092:8080` and configuration mount `/config:ro`.

- [ ] **Step 1: Write a failing static-host integration test**

Create a test-only `wwwroot/index.html`, request `/`, request an unknown client route such as `/settings`, and assert both return the SPA document while `/api/unknown` remains an API 404 rather than falling back to HTML.

- [ ] **Step 2: Run the static-host test and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests --filter FullyQualifiedName~StaticHostingTests
```

Expected: the fallback/static-host assertions fail before publish integration is configured.

- [ ] **Step 3: Wire Angular output into publish**

Configure static files and `MapFallbackToFile("index.html")` after controllers. Map an explicit `/api/{**unmatched}` JSON 404 before the SPA fallback so unknown API routes never return HTML. Configure the API project publish target to run `npm ci` and `npm run build` only for container/publish builds and copy `client/reach-commander-ui/dist/reach-commander-ui/browser/**` into publish `wwwroot`. Keep ordinary backend unit-test builds independent of Node.

- [ ] **Step 4: Create safe sample configuration and Compose file**

`config/sources.json` defines Downloads and Media under `/sources`. `compose.yaml` uses:

```yaml
services:
  reachcommander:
    build: .
    container_name: reachcommander
    user: "1000:1000"
    ports:
      - "8092:8080"
    volumes:
      - ./config:/config:ro
      - ./dev-sources/downloads:/sources/downloads:ro
      - ./dev-sources/media:/sources/media:ro
    restart: unless-stopped
```

The Dockerfile uses Node 24 Alpine for Angular, SDK 10 Alpine for publish, ASP.NET 10 Alpine for runtime, `ASPNETCORE_HTTP_PORTS=8080`, and BusyBox `wget` for a health check without installing another package.

- [ ] **Step 5: Run integration tests, publish, and Docker config validation**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests
dotnet publish src/ReachCommander.Api -c Release -o artifacts/publish
docker compose config
docker build -t reachcommander:milestone1 .
```

Expected: tests and publish pass, Compose renders successfully, and the image builds.

- [ ] **Step 6: Commit deployment assets**

```powershell
git add src/ReachCommander.Api config Dockerfile compose.yaml .dockerignore dev-sources tests/ReachCommander.IntegrationTests
git commit -m "build: add single-origin Docker deployment"
```

### Task 11: Playwright acceptance flow and operational documentation

**Files:**

- Create: `tests/e2e/package.json`
- Create: `tests/e2e/playwright.config.ts`
- Create: `tests/e2e/fixtures/sources.json`
- Create: `tests/e2e/specs/commander-milestone1.spec.ts`
- Create: `tests/e2e/support/seed-fixtures.ts`
- Create: `README.md`

**Interfaces:**

- Consumes the running single-origin app at `http://127.0.0.1:8092`.
- Produces repeatable browser acceptance coverage for the approved Milestone 1 definition of done.

- [ ] **Step 1: Install and configure Playwright**

Create a private npm package with `@playwright/test` and scripts `test`, `test:headed`, and `install:browsers`. Configure one Chromium project, screenshots and traces on failure, deterministic locale/timezone, and a `webServer` command that starts the application against temporary seeded sources.

- [ ] **Step 2: Write the failing acceptance spec**

Seed:

```text
Downloads/
├── Complete/
│   └── Project Hail Mary/
└── Incomplete/
Media/
├── Movies/
│   └── Gladiator II.mkv
├── Kids/
└── TV/
```

Test that both panes show Downloads and Media source buttons; the left selects Downloads while the right selects Media; the right navigates to `/Movies` without changing the left; Tab changes active pane; arrows, Enter, Backspace, Insert, and Ctrl+A work; Ctrl+T creates and Ctrl+W closes a tab; quick filter narrows rows; and a browser reload restores source/tab/path state.

- [ ] **Step 3: Run Playwright and verify RED before completing missing hooks**

Run:

```powershell
npm --prefix tests/e2e install
npm --prefix tests/e2e run install:browsers
npm --prefix tests/e2e test
```

Expected: the initial run identifies missing selectors, startup wiring, or behavior and fails for those explicit reasons.

- [ ] **Step 4: Complete only the production hooks required by acceptance**

Add stable `data-testid` attributes and deterministic test configuration without exposing test-only APIs or weakening filesystem checks. Re-run each failing scenario after its smallest production correction.

- [ ] **Step 5: Write the README**

Document product identity, architecture, prerequisites, local .NET and Angular development, single-origin startup, Docker/Compose, `/srv/downloads` and `/srv/media` host mapping to `/sources/downloads` and `/sources/media`, adding sources, read-only and unavailable sources, all keyboard shortcuts, physical-path security, symlink confinement, trusted-network warning, test commands, Milestone 1 exclusions, and the Milestones 2-5 roadmap.

- [ ] **Step 6: Run the full fresh verification matrix**

Run in this order and retain exit codes/output:

```powershell
dotnet test ReachCommander.slnx -c Release
npm --prefix client/reach-commander-ui test -- --watch=false
npm --prefix client/reach-commander-ui run build
dotnet publish src/ReachCommander.Api -c Release -o artifacts/publish
npm --prefix tests/e2e test
docker build -t reachcommander:milestone1 .
docker compose up -d
docker compose ps
curl.exe --fail http://localhost:8092/health
curl.exe --fail http://localhost:8092/api/sources
docker compose down
git diff --check
git status --short
```

Expected: all test/build/publish commands exit 0; the container becomes healthy; both HTTP probes return success; `git diff --check` is clean. If Docker or browser installation is unavailable, record the precise failed command and do not claim that portion passed.

- [ ] **Step 7: Commit acceptance coverage and documentation**

```powershell
git add tests/e2e README.md client/reach-commander-ui src tests config Dockerfile compose.yaml .dockerignore .gitignore
git commit -m "test: verify ReachCommander milestone 1"
```

## Plan Self-Review Checklist

- Every approved Milestone 1 requirement maps to Tasks 1-11.
- Later milestones appear only as explicit exclusions or disabled UI commands.
- All browser-facing types omit `RootPath` and `PhysicalPath`.
- Backend and frontend naming/signatures are consistent across tasks.
- Each production slice begins with a test that is run and observed failing.
- Security, integration, Angular, Playwright, publish, and Docker checks are all explicit.
- No task relies on the developer's actual filesystem contents.
