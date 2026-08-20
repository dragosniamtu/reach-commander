# ReachCommander Secure Uploads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add bounded, streamed, all-or-nothing multi-file uploads into an explicitly writable active source directory.

**Architecture:** The application layer defines safe logical upload commands/results and a streaming service port. Infrastructure validates portable filenames, stages each multipart stream beside its destination, serializes finalization through a shared logical-directory lock, and compensates handled failures. ASP.NET Core parses multipart sections without `IFormFile` buffering; Angular owns review/progress/cancellation in a separate `UploadStore` and accessible dialog.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core controller APIs and `MultipartReader`, built-in options validation and Problem Details, `System.IO`, Angular 22 standalone components and Signals, Angular `HttpClient` progress events, Angular CDK A11y, xUnit, Vitest, and Playwright.

## Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Use TDD for every production slice and observe the focused test fail for the expected missing behavior before implementation.
- Preserve the checked-in read-only source and Docker defaults; upload requires `SourceDefinition.IsReadOnly == false` and actual filesystem write permission.
- Defaults are 10 GiB per file, 50 GiB per batch, 100 files per batch, and two concurrent batches; all values are configurable and validated on startup.
- Accept arbitrary file types intentionally, but never execute uploaded content or place it beneath the API's application/static-content directory unless an administrator explicitly configured that directory as a source.
- Never overwrite, replace, auto-rename, skip conflicts, upload directories, buffer an entire file in managed memory, or expose physical/staging paths.
- Compare batch names and existing destination names with `OrdinalIgnoreCase` on every platform.
- Handled failures must remove staged files and compensate final moves. Abrupt process/host/storage failure may leave reserved staging names but never a requested final filename that the batch did not report as completed.
- Upload and Multi-Rename must share `DirectoryMutationLock` keyed only by source ID and normalized logical directory. Same-directory and ancestor/descendant keys conflict; sibling directories may proceed concurrently. Upload holds its lease from authoritative destination resolution through staging, finalization, and cleanup.
- Add no runtime NuGet/npm package; use framework capabilities and the already-installed Angular CDK.
- Before every commit, inspect `git status --short` and stage only the task's planned files.

## File Structure

```text
src/ReachCommander.Application/Uploads/
├── IUploadService.cs                 Streaming application port
├── UploadExceptions.cs              Stable safe failures
└── UploadModels.cs                   Logical commands, parts, and results

src/ReachCommander.Infrastructure/
├── Mutations/DirectoryMutationLock.cs Shared logical-directory serialization
└── Uploads/
    ├── LocalUploadFileSystem.cs       Injectable System.IO boundary
    ├── UploadFilenameValidator.cs     Portable single-component policy
    ├── UploadOptions.cs               Limits and startup validation
    └── UploadService.cs               Stage, finalize, compensate

src/ReachCommander.Api/
├── Contracts/Uploads/UploadResultDto.cs
├── Contracts/Uploads/UploadLimitsDto.cs
├── Controllers/UploadsController.cs
└── Uploads/MultipartUploadReader.cs

client/reach-commander-ui/src/app/
├── core/api/api.models.ts
├── core/api/reach-commander-api.ts
├── core/state/upload.models.ts
├── core/state/upload-store.ts
└── features/uploads/upload-dialog.component.{ts,html,scss,spec.ts}

tests/ReachCommander.UnitTests/Uploads/
├── DirectoryMutationLockTests.cs
├── UploadFilenameValidatorTests.cs
├── UploadOptionsValidatorTests.cs
└── UploadServiceTests.cs

tests/ReachCommander.IntegrationTests/UploadsApiTests.cs
```

---

### Task 1: Upload contracts, stable failures, and validated limits

**Files:**

- Create: `src/ReachCommander.Application/Uploads/UploadModels.cs`
- Create: `src/ReachCommander.Application/Uploads/UploadExceptions.cs`
- Create: `src/ReachCommander.Application/Uploads/IUploadService.cs`
- Create: `src/ReachCommander.Infrastructure/Uploads/UploadOptions.cs`
- Test: `tests/ReachCommander.UnitTests/Uploads/UploadOptionsValidatorTests.cs`

**Interfaces:**

- Produces: `UploadBatchCommand`, `UploadFilePart`, `UploadedFile`, `UploadBatchResult`, and `IUploadService.UploadAsync`.
- Produces: upload exception types with safe logical fields and no HTTP/framework dependency.
- Produces: public configuration contract `UploadOptions` plus internal `UploadOptionsValidator`, consumed by Infrastructure and the API in Tasks 3–4.

- [ ] **Step 1: Write failing options-validation tests**

```csharp
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.UnitTests.Uploads;

public sealed class UploadOptionsValidatorTests
{
    private readonly UploadOptionsValidator _validator = new();

    [Fact]
    public void Defaults_match_the_approved_limits()
    {
        var options = new UploadOptions();

        Assert.Equal(10L * 1024 * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(50L * 1024 * 1024 * 1024, options.MaxBatchBytes);
        Assert.Equal(100, options.MaxFilesPerBatch);
        Assert.Equal(2, options.MaxConcurrentBatches);
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    public void Invalid_or_inconsistent_limits_fail_startup(
        long maxFileBytes,
        long maxBatchBytes,
        int maxFiles,
        int maxConcurrent)
    {
        var options = new UploadOptions
        {
            MaxFileBytes = maxFileBytes,
            MaxBatchBytes = maxBatchBytes,
            MaxFilesPerBatch = maxFiles,
            MaxConcurrentBatches = maxConcurrent,
        };

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
```

- [ ] **Step 2: Run the focused test and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~UploadOptionsValidatorTests
```

Expected: compilation fails because the upload options and validator do not exist.

- [ ] **Step 3: Add exact application contracts and safe exceptions**

```csharp
namespace ReachCommander.Application.Uploads;

public sealed record UploadBatchCommand(string SourceId, string DirectoryPath);

public sealed record UploadFilePart(
    string FileName,
    Stream Content,
    long? DeclaredLength);

public sealed record UploadedFile(
    string Name,
    string RelativePath,
    long Size);

public sealed record UploadBatchResult(
    int UploadedCount,
    long TotalBytes,
    IReadOnlyList<UploadedFile> Files);

public interface IUploadService
{
    ValueTask<UploadBatchResult> UploadAsync(
        UploadBatchCommand command,
        IAsyncEnumerable<UploadFilePart> files,
        CancellationToken cancellationToken);
}
```

Add an abstract `UploadException` with public `Code` and safe `Detail`. Add sealed failures for empty batch, invalid filename, duplicate/conflicting name(s), file too large, batch too large, too many files, read-only source, storage unavailable, unsupported media type, malformed multipart input, cancellation, and cleanup required. Conflict/cleanup exceptions expose logical filenames only through immutable collections. Do not store an inner filesystem exception in any browser-mapped property.

- [ ] **Step 4: Implement options and startup validation**

```csharp
namespace ReachCommander.Infrastructure.Uploads;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";
    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MaxBatchBytes { get; init; } = 50L * 1024 * 1024 * 1024;
    public int MaxFilesPerBatch { get; init; } = 100;
    public int MaxConcurrentBatches { get; init; } = 2;
}
```

`UploadOptionsValidator : IValidateOptions<UploadOptions>` fails when any value is non-positive, `MaxFileBytes > MaxBatchBytes`, `MaxFilesPerBatch > 10_000`, `MaxConcurrentBatches > 64`, or adding the approved multipart-overhead allowance would overflow `long`.

- [ ] **Step 5: Run focused and full tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~UploadOptionsValidatorTests
dotnet test ReachCommander.slnx -c Release
```

Expected: the options tests and existing suite pass.

- [ ] **Step 6: Commit the contract slice**

```powershell
git status --short
git add src/ReachCommander.Application/Uploads src/ReachCommander.Infrastructure/Uploads/UploadOptions.cs tests/ReachCommander.UnitTests/Uploads/UploadOptionsValidatorTests.cs
git commit -m "feat: define secure upload contracts"
```

---

### Task 2: Portable filenames and shared logical-directory locking

**Files:**

- Create: `src/ReachCommander.Infrastructure/Uploads/UploadFilenameValidator.cs`
- Create: `src/ReachCommander.Infrastructure/Mutations/DirectoryMutationLock.cs`
- Test: `tests/ReachCommander.UnitTests/Uploads/UploadFilenameValidatorTests.cs`
- Test: `tests/ReachCommander.UnitTests/Uploads/DirectoryMutationLockTests.cs`

**Interfaces:**

- Produces: internal `UploadFilenameValidator.Validate(string fileName)` returning the unchanged valid single-component name or throwing `UploadNameInvalidException`.
- Produces: singleton-safe `DirectoryMutationLock.AcquireAsync(string sourceId, string logicalDirectory, CancellationToken)` returning `IAsyncDisposable`.
- Consumed by: Task 3 UploadService and the updated Multi-Rename plan.

- [ ] **Step 1: Write failing filename-policy tests**

```csharp
public sealed class UploadFilenameValidatorTests
{
    private readonly UploadFilenameValidator _validator = new();

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData(".env")]
    [InlineData("Résumé 2026.pdf")]
    [InlineData("zero-byte")]
    public void Validate_preserves_safe_names(string name) =>
        Assert.Equal(name, _validator.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("C:\\file.txt")]
    [InlineData("CON.txt")]
    [InlineData("trailing. ")]
    [InlineData("nul\0name")]
    public void Validate_rejects_nonportable_or_path_bearing_names(string name) =>
        Assert.Throws<UploadNameInvalidException>(() => _validator.Validate(name));
}
```

Also test every Windows reserved device stem, invalid portable character, trailing dot/space, and a UTF-8 filename over the chosen 255-byte component ceiling.

- [ ] **Step 2: Write failing concurrency and cancellation tests**

```csharp
[Fact]
public async Task Same_logical_directory_is_serialized()
{
    var gate = new DirectoryMutationLock();
    await using var first = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);
    var secondEntered = false;
    var second = Task.Run(async () =>
    {
        await using var lease = await gate.AcquireAsync("MEDIA", "/Movies", CancellationToken.None);
        secondEntered = true;
    });

    await Task.Delay(50);
    Assert.False(secondEntered);
    await first.DisposeAsync();
    await second;
    Assert.True(secondEntered);
}
```

Add tests proving sibling directories can proceed concurrently, ancestor/descendant directories on the same source serialize, other sources remain independent, a cancelled waiter never enters, and a lease releases exactly once.

- [ ] **Step 3: Run both focused fixtures and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~UploadFilenameValidatorTests|FullyQualifiedName~DirectoryMutationLockTests"
```

Expected: compilation fails because validator and lock do not exist.

- [ ] **Step 4: Implement the filename validator**

Use explicit portable rules rather than host-only `Path.GetInvalidFileNameChars()`: reject controls, `<>:\"/\\|?*`, NUL, separators, rooted/drive/UNC forms, `.`/`..`, trailing dot/space, and `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9` before any extension. Enforce a maximum of 255 UTF-8 bytes. Return the original Unicode string unchanged after validation.

- [ ] **Step 5: Implement the keyed lock**

Use one short-held internal state gate, active logical keys, and FIFO waiters. Normalize source IDs with `OrdinalIgnoreCase` equality and require already-normalized logical paths. Two keys conflict only when their source IDs match and their paths are equal or one path is a segment-boundary ancestor of the other (`/Movies` conflicts with `/Movies/Incoming`, not `/Movies-Old`). A waiter enters only when it conflicts with neither an active lease nor an earlier queued waiter, preventing starvation while allowing unrelated siblings to proceed. Cancellation removes a waiter safely. The `IAsyncDisposable` lease uses `Interlocked.Exchange` to release once and wakes eligible waiters. Keys never contain physical paths.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~UploadFilenameValidatorTests|FullyQualifiedName~DirectoryMutationLockTests"
dotnet test ReachCommander.slnx -c Release
git status --short
git add src/ReachCommander.Infrastructure/Uploads/UploadFilenameValidator.cs src/ReachCommander.Infrastructure/Mutations/DirectoryMutationLock.cs tests/ReachCommander.UnitTests/Uploads/UploadFilenameValidatorTests.cs tests/ReachCommander.UnitTests/Uploads/DirectoryMutationLockTests.cs
git commit -m "feat: validate upload names and serialize mutations"
```

---

### Task 3: Stream, stage, finalize, and compensate upload batches

**Files:**

- Create: `src/ReachCommander.Infrastructure/Uploads/LocalUploadFileSystem.cs`
- Create: `src/ReachCommander.Infrastructure/Uploads/UploadService.cs`
- Create: `tests/ReachCommander.UnitTests/Support/UploadTestFixture.cs`
- Test: `tests/ReachCommander.UnitTests/Uploads/UploadServiceTests.cs`

**Interfaces:**

- Produces: internal `IUploadFileSystem` and `LocalUploadFileSystem` for directory enumeration, create-new staging streams, flush, move-without-overwrite, delete, and capacity inspection.
- Produces: singleton-safe `UploadService : IUploadService`.
- Consumes: `IPathSecurityService`, `UploadFilenameValidator`, `DirectoryMutationLock`, `IOptions<UploadOptions>`, `TimeProvider`, and `ILogger<UploadService>`.

- [ ] **Step 1: Write failing happy-path and zero-byte tests**

```csharp
[Fact]
public async Task Upload_stages_then_commits_multiple_files()
{
    await using var fixture = new UploadTestFixture();
    var result = await fixture.Service.UploadAsync(
        new("media", "/Movies"),
        Parts(("one.txt", "one"), ("empty.bin", "")),
        CancellationToken.None);

    Assert.Equal(2, result.UploadedCount);
    Assert.Equal(3, result.TotalBytes);
    Assert.Equal("one", fixture.Read("Movies/one.txt"));
    Assert.Equal(string.Empty, fixture.Read("Movies/empty.bin"));
    Assert.Empty(fixture.StagingEntries("Movies"));
}
```

- [ ] **Step 2: Add failing safety and compensation tests**

Cover these exact behaviors with temporary roots and an injected filesystem:

- read-only source fails before reading the first byte;
- missing/unavailable/not-directory destination fails safely;
- duplicate batch names differing only by case leave no final files;
- one existing destination rejects the complete batch;
- declared or actual per-file limit and actual aggregate limit stop streaming and clean staging;
- the 101st section fails with `upload_too_many_files`;
- cancellation during streaming deletes staged files;
- final destination appearing between staging and lock revalidation rejects the batch;
- a failure on final move N deletes earlier final files and remaining staging files;
- a cleanup failure returns `upload_cleanup_required` with logical names and no fixture root;
- two services targeting the same directory cannot both finalize the same destination;
- an in-process rename of the parent directory cannot enter while upload staging is active;
- a changed mount/resolved physical directory between initial resolution and finalization fails before final moves.

- [ ] **Step 3: Run the service fixture and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~UploadServiceTests
```

Expected: compilation fails because filesystem adapter, fixture, and service do not exist.

- [ ] **Step 4: Implement the injectable filesystem boundary**

`IUploadFileSystem` uses asynchronous `FileStream` operations with `FileMode.CreateNew`, `FileAccess.Write`, `FileShare.None`, and `FileOptions.Asynchronous | FileOptions.SequentialScan`. It exposes no public physical path. `Move` calls `File.Move(source, destination, overwrite: false)`. Enumeration returns entry names and link/attributes needed for final conflict checks. The test double counts operations and throws only configured safe filesystem exceptions.

- [ ] **Step 5: Acquire mutation scope and implement bounded streaming**

Acquire the configured global concurrency slot. Call `IPathSecurityService.ResolveAsync` once only to validate the request and obtain the catalog's canonical source ID plus normalized logical path; do not open a staging file or retain that physical result. Acquire `DirectoryMutationLock` with those logical values, resolve again under the lease, and use only the second result as the authoritative destination. Hold both leases through all streaming, finalization, and cleanup. This prevents an in-process rename of the destination or any ancestor from moving staging paths mid-request while still permitting work in sibling directories.

Resolve and validate source policy/destination before enumerating the first multipart part. When every declared length is present and reported free capacity is available, reject an obviously too-large batch before copying; actual streamed limits and filesystem write results remain authoritative.

For every `UploadFilePart`:

1. Stop before reading when the count exceeds `MaxFilesPerBatch` or declared length exceeds `MaxFileBytes`.
2. Validate the unchanged filename and add it to an `OrdinalIgnoreCase` set.
3. Generate `.reachcommander-upload-{batchId:N}-{index:D5}.partial`; retry with a new batch ID if any reserved name exists.
4. Copy in an 80 KiB pooled buffer while incrementing checked per-file and aggregate counters.
5. If either actual limit is exceeded, abort immediately.
6. Flush asynchronously, dispose the staging stream, and record the logical filename, stage path, final path, and actual bytes internally.

The service does not dispose caller-owned part streams. It always releases the directory and concurrency leases in `finally` after cleanup is complete.

- [ ] **Step 6: Implement locked all-or-nothing finalization**

After every part is staged:

1. Reject an empty batch.
2. While still holding the directory lease, resolve the logical directory again and require the same canonical physical directory under host-appropriate path comparison.
3. Re-enforce `SourceDefinition.IsReadOnly == false`, directory existence, and no symlink escape.
4. Enumerate existing names into an `OrdinalIgnoreCase` set and reject every requested collision together.
5. Log batch ID, source ID, logical directory, count, and bytes; never log physical paths or contents.
6. Move every staging file to its final name without overwrite.
7. On a handled exception, delete already-finalized files created by this batch and every remaining staging file before releasing the directory lease.
8. Throw the original safe upload failure if cleanup completes, otherwise `UploadCleanupRequiredException` containing only logical names.
9. Return immutable logical result rows in multipart order.

Catch expected `IOException`, `UnauthorizedAccessException`, `DirectoryNotFoundException`, and cancellation. Do not catch process-fatal exceptions. After the first final move, finish compensation even if the HTTP token is cancelled.

- [ ] **Step 7: Verify and commit**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~UploadServiceTests
dotnet test ReachCommander.slnx -c Release
git status --short
git add src/ReachCommander.Infrastructure/Uploads/LocalUploadFileSystem.cs src/ReachCommander.Infrastructure/Uploads/UploadService.cs tests/ReachCommander.UnitTests/Support/UploadTestFixture.cs tests/ReachCommander.UnitTests/Uploads/UploadServiceTests.cs
git commit -m "feat: stream upload batches safely"
```

---

### Task 4: Streaming multipart API and safe Problem Details

**Files:**

- Create: `src/ReachCommander.Api/Contracts/Uploads/UploadResultDto.cs`
- Create: `src/ReachCommander.Api/Contracts/Uploads/UploadLimitsDto.cs`
- Create: `src/ReachCommander.Api/Uploads/MultipartUploadReader.cs`
- Create: `src/ReachCommander.Api/Controllers/UploadsController.cs`
- Modify: `src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/appsettings.json`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Test: `tests/ReachCommander.IntegrationTests/UploadsApiTests.cs`

**Interfaces:**

- Produces: `GET /api/uploads/limits` returning the safe configured numeric limits.
- Produces: `POST /api/uploads?sourceId={id}&path={logicalPath}` accepting multipart sections named `files`.
- Produces: safe HTTP 201 `UploadResultDto` and stable Problem Details mappings.
- Consumes: Task 3 `IUploadService` and Task 1 limits.

- [ ] **Step 1: Write failing HTTP integration tests**

```csharp
[Fact]
public async Task Uploads_multiple_files_and_returns_safe_logical_results()
{
    using var content = new MultipartFormDataContent();
    content.Add(new ByteArrayContent("one"u8.ToArray()), "files", "one.txt");
    content.Add(new ByteArrayContent(Array.Empty<byte>()), "files", "empty.bin");

    var response = await _client.PostAsync("/api/uploads?sourceId=media&path=/Movies", content);
    var body = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    Assert.Contains("\"uploadedCount\":2", body);
    Assert.DoesNotContain(_factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
    Assert.Equal("one", File.ReadAllText(Path.Combine(_factory.MediaRoot, "Movies", "one.txt")));
}
```

Add a GET case proving the configured test limits are returned without infrastructure details. Add POST cases for non-multipart 415, empty 400, malformed filename 400, conflict 409 with zero final additions, read-only 403, missing source 404, unavailable source 503, configured file/batch/count limits 413, and a response/body scan proving no root/staging/exception strings leak.

- [ ] **Step 2: Run integration tests and verify RED**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~UploadsApiTests
```

Expected: requests return JSON route-not-found because the controller does not exist.

- [ ] **Step 3: Implement a bounded multipart reader**

`MultipartUploadReader.ReadAsync(HttpRequest, UploadOptions, CancellationToken)`:

- rejects missing/non-multipart content type with `UploadUnsupportedMediaTypeException`;
- parses and bounds the boundary using `MultipartRequestHelper.GetBoundary`-equivalent local code;
- sets `HeadersCountLimit`, `HeadersLengthLimit`, and `BodyLengthLimit = MaxFileBytes` on `MultipartReader`;
- accepts only file disposition sections with field name `files`;
- rejects form fields and empty/missing filename parameters;
- yields `UploadFilePart` in wire order with the section body and parsed declared section length when present;
- never reads a section body ahead of `UploadService`.

Make `ReadAsync` a non-iterator wrapper that validates content type and boundary synchronously, then returns a private async-iterator `ReadCoreAsync`. This guarantees malformed media is rejected before source resolution or staging instead of deferring validation until the service enumerates. Use `Microsoft.AspNetCore.WebUtilities` and `Microsoft.Net.Http.Headers` already in the shared framework; add no package.

- [ ] **Step 4: Implement the thin controller and dynamic request ceiling**

```csharp
[ApiController]
[Route("api/uploads")]
public sealed class UploadsController(
    IUploadService uploads,
    MultipartUploadReader reader,
    IOptions<UploadOptions> options) : ControllerBase
{
    [HttpGet("limits")]
    [ProducesResponseType<UploadLimitsDto>(StatusCodes.Status200OK)]
    public ActionResult<UploadLimitsDto> GetLimits() =>
        Ok(UploadLimitsDto.FromOptions(options.Value));

    [HttpPost]
    [ProducesResponseType<UploadResultDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UploadResultDto>> Upload(
        [FromQuery] string sourceId,
        [FromQuery(Name = "path")] string directoryPath,
        CancellationToken cancellationToken)
    {
        ConfigureBodyLimit(HttpContext, options.Value);
        var parts = reader.ReadAsync(Request, options.Value, cancellationToken);
        var result = await uploads.UploadAsync(
            new UploadBatchCommand(sourceId, directoryPath), parts, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, UploadResultDto.FromResult(result));
    }
}
```

`ConfigureBodyLimit` sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` before reading when the feature is writable. Compute `overhead = Math.Min(1L * 1024 * 1024 * 1024, checked(1L * 1024 * 1024 + (long)MaxFilesPerBatch * 16 * 1024))`, then use `checked(MaxBatchBytes + overhead)`. If a hosting server has already made the feature read-only, streaming limits remain authoritative.

- [ ] **Step 5: Register services, configuration, and errors**

Bind `Uploads`, validate on start, and register singleton-safe validator, shared lock, filesystem, service, plus transient `MultipartUploadReader`. Map safe failures:

| Code | HTTP |
|---|---:|
| `upload_name_invalid`, `upload_empty`, `upload_malformed` | 400 |
| `source_read_only`, `path_forbidden` | 403 |
| `source_not_found` | 404 |
| `upload_name_conflict` | 409 |
| size/count failures | 413 |
| `upload_unsupported_media_type` | 415 |
| `source_unavailable`, `upload_storage_unavailable` | 503 |
| `upload_cleanup_required` | 500 |

Problem Details include only stable title/detail/code and optional safe logical conflict names. `UploadLimitsDto` exposes only `maxFileBytes`, `maxBatchBytes`, and `maxFilesPerBatch`; global concurrency is operational server configuration and is not needed by the browser. Add approved defaults to `appsettings.json`. Integration tests override limits to small values and keep hardware metrics disabled.

- [ ] **Step 6: Verify API and commit**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~UploadsApiTests
dotnet test ReachCommander.slnx -c Release
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -p:BuildAngularOnPublish=false
git status --short
git add src/ReachCommander.Api/Contracts/Uploads src/ReachCommander.Api/Uploads src/ReachCommander.Api/Controllers/UploadsController.cs src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs src/ReachCommander.Api/appsettings.json src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs tests/ReachCommander.IntegrationTests/UploadsApiTests.cs
git commit -m "feat: expose streamed upload API"
```

---

### Task 5: Angular upload transport and isolated state

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/upload.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/upload-store.ts`
- Test: `client/reach-commander-ui/src/app/core/state/upload-store.spec.ts`

**Interfaces:**

- Produces: `UploadLimitsDto`, `UploadResultDto`, `UploadEvent`, `CommanderApiPort.getUploadLimits`, and `CommanderApiPort.uploadFiles`.
- Produces: root `UploadStore.open(context: UploadContext, files: readonly File[], onCompleted: () => void): void`, `start()`, `cancel()`, `close()`, readonly `state`, and readonly `isPending` signals consumed by Task 6 and the toolbar plan.

- [ ] **Step 1: Write failing transport tests**

Verify `getUploadLimits()` calls `GET /api/uploads/limits`. Verify `uploadFiles({sourceId:'media', directoryPath:'/Movies'}, files)` creates `FormData` with repeated `files`, URL-encodes logical query parameters, requests progress, maps `UploadProgressEvent`, and maps the final HTTP response to `UploadCompletedEvent`.

```typescript
const events = await firstValueFrom(
  api.uploadFiles(context, [new File(['one'], 'one.txt')]).pipe(toArray()),
);
expect(events.at(-1)).toEqual({ kind: 'completed', result });
```

- [ ] **Step 2: Write failing store lifecycle tests**

Cover:

- `open(context, files, onCompleted)` stores an immutable destination, copied file list, and one-shot completion callback;
- configured limits are fetched/cached from the API, rendered in review, and used for client preflight;
- removing a file recalculates totals;
- client preflight reports count/per-file/batch limit errors without replacing server authority;
- `start()` moves review → uploading, updates loaded/total progress, then completed;
- cancellation aborts the subscription and transitions to cancelled;
- late events from a cancelled/previous request token are ignored;
- API Problem Details map to safe specific copy and keep the review list available;
- `close()` is refused during finalization and otherwise clears browser `File` references;
- completion invokes exactly one captured-panel refresh callback while preserving filter/selection.

- [ ] **Step 3: Run focused Angular tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/reach-commander-api.spec.ts" --include="**/upload-store.spec.ts"
Pop-Location
```

Expected: compilation fails because upload types and methods do not exist.

- [ ] **Step 4: Add typed upload events**

```typescript
export interface UploadContext {
  readonly side: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly directoryPath: string;
}

export type UploadEvent =
  | { readonly kind: 'progress'; readonly loadedBytes: number; readonly totalBytes: number | null }
  | { readonly kind: 'completed'; readonly result: UploadResultDto };

export abstract class CommanderApiPort {
  abstract getSystemMetrics(): Promise<SystemMetricsDto>;
  abstract getSources(): Promise<readonly SourceDto[]>;
  abstract listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]>;
  abstract getInfo(sourceId: string, path: string): Promise<FileEntryDto>;
  abstract getUploadLimits(): Promise<UploadLimitsDto>;
  abstract uploadFiles(
    context: Pick<UploadContext, 'sourceId' | 'directoryPath'>,
    files: readonly File[],
  ): Observable<UploadEvent>;
}
```

Implement `HttpRequest('POST', url, formData, { reportProgress: true })`; filter `UploadProgress` and final `Response`, map them to the discriminated union, and preserve typed API errors.

- [ ] **Step 5: Implement `UploadStore`**

Use one signal containing `closed | review | uploading | finalizing | completed | failed | cancelled`, immutable context, files, configured limits/loading state, total size, progress, result, error code/message, and monotonically increasing request token. Prefetch and cache `GET /api/uploads/limits`; retry when a review opens after a failed load. Client preflight uses the returned deployment values, never hard-coded mirrored defaults. The server remains authoritative. `AbortController`/RxJS unsubscribe cancels the HTTP request. Browser `File` values never enter `CommanderStore`, persistence, logs, or URL state.

- [ ] **Step 6: Verify and commit**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/reach-commander-api.spec.ts" --include="**/upload-store.spec.ts"
npm test -- --watch=false
Pop-Location
git status --short
git add client/reach-commander-ui/src/app/core/api/api.models.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts client/reach-commander-ui/src/app/core/state/upload.models.ts client/reach-commander-ui/src/app/core/state/upload-store.ts client/reach-commander-ui/src/app/core/state/upload-store.spec.ts
git commit -m "feat: add upload client state"
```

---

### Task 6: Accessible upload review and progress dialog

**Files:**

- Create: `client/reach-commander-ui/src/app/features/uploads/upload-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/uploads/upload-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/uploads/upload-dialog.component.scss`
- Test: `client/reach-commander-ui/src/app/features/uploads/upload-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**

- Produces: `UploadDialogComponent` driven only by `UploadStore` state.
- Produces: shell methods `reviewUpload(files: readonly File[]): void`, `startUpload(): void`, and `closeUpload(): void` for the toolbar plan.
- Consumes: active-panel immutable context captured by the shell and `CommanderStore.refresh(side)`.

- [ ] **Step 1: Write failing component tests**

Test review destination/count/total, remove buttons, client-limit errors, Add files disabled state, progressbar value/text, cancellation, server conflict copy, completed file list, Escape rules, focus trap, and opener restoration. Use realistic `File` objects; do not mock DOM behavior that Angular CDK can exercise.

- [ ] **Step 2: Write failing shell-context tests**

Set left/right panels to different sources/paths. Call `reviewUpload` while left is active, switch the active signal to right, and prove the dialog still shows the captured left destination. Complete the fake upload and assert `store.refresh('left')` exactly once, with no right refresh/filter/selection mutation.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/upload-dialog.component.spec.ts" --include="**/commander-shell.component.spec.ts"
Pop-Location
```

Expected: component import/method failures because the dialog and shell hooks do not exist.

- [ ] **Step 4: Implement the dialog**

Use a fixed backdrop, `role="dialog"`, `aria-modal="true"`, CDK focus trap with initial focus on the primary action, and a scrollable file table. Header: `Add files`, source, logical directory, count, and close. Review rows show name and size with remove controls. Footer shows total/configured limits, Cancel, and Add files. Uploading shows determinate progress when total is known and byte text otherwise; finalizing disables close/cancel. Completed and failed states retain safe summaries. No physical path or browser fake-path is rendered.

- [ ] **Step 5: Integrate dialog lifecycle in the shell**

The shell computes active source/tab at the moment `reviewUpload` runs, rejects unavailable/read-only sources before opening, calls `uploadStore.open(context, files, () => store.refresh(capturedSide))`, and renders the dialog whenever state is not closed. Escape priority becomes metrics → upload dialog when closable → Multi-Rename/menu → filter/selection/status. Task 6 does not add a visible picker button; the next toolbar plan calls these public shell hooks.

- [ ] **Step 6: Verify, build, and commit**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/upload-dialog.component.spec.ts" --include="**/commander-shell.component.spec.ts"
npm test -- --watch=false
npm run build
Pop-Location
git status --short
git add client/reach-commander-ui/src/app/features/uploads client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts
git commit -m "feat: add upload review workflow"
```

---

### Task 7: Upload documentation, API acceptance, and release checks

**Files:**

- Modify: `README.md`
- Modify: `tests/e2e/fixtures/sources.json`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/specs/upload.spec.ts`

**Interfaces:**

- Consumes: Tasks 1–4 of the active-panel toolbar plan, including its Add files control, plus this plan's backend/dialog.
- Produces: writable temporary E2E source behavior without changing production/sample defaults.

- [ ] **Step 1: Add a deterministic upload Playwright scenario**

The E2E seed makes `downloads` writable, keeps `media` read-only, and exposes a fixture-creation hook only inside the seed process. The test:

1. activates writable Downloads;
2. opens the toolbar file chooser with `setInputFiles` for `new-one.txt` and `new-two.bin`;
3. verifies the review destination and starts;
4. verifies completion and refreshed file rows;
5. attempts `existing.txt` plus `another.txt` where `existing.txt` already exists;
6. verifies `upload_name_conflict` copy and that `another.txt` never appears;
7. activates read-only Media and verifies Add files is disabled with an explanation.

- [ ] **Step 2: Run the E2E scenario and verify RED before fixture/doc completion**

```powershell
Push-Location tests/e2e
npm test -- upload.spec.ts
Pop-Location
```

Expected: upload scenario fails until toolbar integration and writable fixture changes are complete.

- [ ] **Step 3: Update fixtures and operations documentation**

Keep `config/sources.json` and `compose.yaml` unchanged. Document:

- explicit source `"readOnly": false` plus a source-specific bind mount without `:ro`;
- approved defaults and ASP.NET configuration keys;
- arbitrary-type/no-execution policy;
- all-or-nothing conflict behavior and handled-failure compensation;
- abrupt-failure reserved staging recovery limitation;
- trusted-network/authenticated reverse-proxy requirement;
- no folder upload, overwrite, resume, or background transfer queue.

- [ ] **Step 4: Run the complete verification matrix**

```powershell
dotnet restore ReachCommander.slnx
dotnet test ReachCommander.slnx -c Release --no-restore
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -p:BuildAngularOnPublish=false
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
Push-Location tests/e2e
npm test
Pop-Location
rg -n "IFormFile|ReadToEnd|MemoryStream|File\.WriteAll|overwrite:\s*true|Process\.Start|cmd\.exe|powershell|/bin/sh" src tests client/reach-commander-ui/src/app
rg -n "physicalPath|rootPath|\.partial" src/ReachCommander.Api/Contracts src/ReachCommander.Api/Controllers client/reach-commander-ui/src/app/core/api
git diff --check
git status --short
```

Expected: every suite/build passes; upload production code uses streaming and no shell/process helper; physical/staging paths are absent from DTOs; only planned files are modified.

- [ ] **Step 5: Validate available container paths**

When Docker is installed, build and run both the hardened default and an explicit temporary writable override. Prove default Add files fails/disabled and the writable fixture upload succeeds. When Docker is unavailable, record that limitation without claiming Compose/build success.

- [ ] **Step 6: Commit the acceptance slice**

```powershell
git status --short
git add README.md tests/e2e/fixtures/sources.json tests/e2e/support/seed-fixtures.ts tests/e2e/specs/upload.spec.ts
git commit -m "docs: add secure upload operations"
git status --short
```
