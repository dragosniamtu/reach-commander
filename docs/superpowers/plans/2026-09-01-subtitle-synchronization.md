# Subtitle Synchronization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a secure ReachCommander workspace that previews MP4/MKV/AVI video with a same-name SRT, applies one constant subtitle offset, and transactionally preserves the original SRT before saving the corrected file.

**Architecture:** A new `MediaPreviews` application boundary owns opaque preview sessions, subtitle cues, direct byte-range playback, temporary FFmpeg HLS fallback, and server-authoritative subtitle save plans. Angular opens the workspace from Enter/double-click, renders adjusted cues over the video, uses native HLS or pinned `hls.js`, and never handles host paths or rewritten subtitle content.

**Tech Stack:** .NET 10, ASP.NET Core cookie authentication/antiforgery/rate limiting, Angular 22 signals, `hls.js` 1.7.1, FFmpeg/FFprobe 6.1.2-r2 from Alpine 3.22, Vitest, xUnit, Playwright, Docker Buildx.

## Global Constraints

- Work directly on `master`; do not create a worktree.
- Support `.mp4`, `.mkv`, and `.avi` video plus `.srt` subtitles only.
- Use one constant signed offset; positive values make cues appear later and negative values make them appear earlier.
- Auto-select the same-base-name SRT in the video's directory and allow only another same-directory SRT to be selected manually.
- Play H.264/AAC MP4 directly; use temporary H.264/AAC HLS for other validated inputs.
- Never modify the original video or create a persistent proxy beside it.
- Rename `movie.srt` to the first free `movie_original.srt`, `movie_original (2).srt`, and so on; publish corrected UTF-8 content as `movie.srt`.
- Preview is allowed on read-only sources; saving is not.
- Never expose host paths, process command lines, unrestricted logs, or temporary filenames to the browser.
- Reject symbolic links and revalidate source containment and fingerprints before every mutation.
- Require the administrator cookie for every endpoint and existing antiforgery protection for every mutation.
- Limit SRT input to 4 MiB and 20,000 cues, offset magnitude to 600,000 ms, save plans to 10 minutes, preview inactivity to 20 minutes, captured process output to 64 KiB, temporary output to 8 GiB, and concurrent transcodes to one.
- Accept strict UTF-8 with or without BOM and UTF-16 LE/BE with BOM; emit corrected subtitles as UTF-8 without BOM.
- Leave untracked `NC-theme.png` untouched.

---

### Task 1: SRT parsing and constant-offset transformation

**Files:**
- Create: `src/ReachCommander.Application/MediaPreviews/MediaPreviewModels.cs`
- Create: `src/ReachCommander.Application/MediaPreviews/MediaPreviewExceptions.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/SrtDocument.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/SrtParser.cs`
- Test: `tests/ReachCommander.UnitTests/MediaPreviews/SrtParserTests.cs`

**Interfaces:**
- Produces: `SubtitleCue`, `SrtDocument`, `SrtParser.Parse(ReadOnlyMemory<byte>)`, and `SrtDocument.RenderWithOffset(long)`.
- `SubtitleCue` uses zero-based `Index`, `StartMilliseconds`, `EndMilliseconds`, and plain `Text`.
- Parser failures throw `MediaPreviewException` with bounded public codes such as `subtitle_invalid`, `subtitle_too_large`, and `subtitle_encoding_unsupported`.

- [ ] **Step 1: Write failing parser and offset tests**

```csharp
[Fact]
public void RenderWithOffset_shifts_every_cue_and_clips_at_zero()
{
    var source = Encoding.UTF8.GetBytes(
        "1\r\n00:00:00,500 --> 00:00:02,000\r\nHello\r\n\r\n" +
        "2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld\r\n");
    var document = new SrtParser(4 * 1024 * 1024, 20_000).Parse(source);

    var corrected = document.RenderWithOffset(-750);

    Assert.Equal(
        "1\r\n00:00:00,000 --> 00:00:01,250\r\nHello\r\n\r\n" +
        "2\r\n00:00:02,250 --> 00:00:03,250\r\nWorld\r\n",
        Encoding.UTF8.GetString(corrected));
}

[Theory]
[InlineData("00:61:00,000")]
[InlineData("00:00:01.000")]
[InlineData("00:00:02,000 --> 00:00:01,000")]
public void Parse_rejects_invalid_timestamps(string timestampLine)
{
    var source = Encoding.UTF8.GetBytes($"1\r\n{timestampLine}\r\nText\r\n");
    var error = Assert.Throws<MediaPreviewException>(() =>
        new SrtParser(4 * 1024 * 1024, 20_000).Parse(source));
    Assert.Equal("subtitle_invalid", error.Code);
}
```

- [ ] **Step 2: Run the focused test and verify the red state**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~SrtParserTests`

Expected: compilation fails because `SrtParser` and `SrtDocument` do not exist.

- [ ] **Step 3: Implement strict decoding, parsing, and rendering**

```csharp
public sealed record SubtitleCue(int Index, long StartMilliseconds, long EndMilliseconds, string Text);

internal sealed class SrtParser(long maxBytes, int maxCues)
{
    public SrtDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > maxBytes)
            throw MediaPreviewException.SubtitleTooLarge();
        var text = StrictSubtitleEncoding.Decode(bytes.Span);
        return SrtDocument.Parse(text, maxCues);
    }
}

internal byte[] RenderWithOffset(long offsetMilliseconds)
{
    var adjusted = _blocks.Select(block => block.Shift(offsetMilliseconds)).ToArray();
    if (adjusted.Any(block => block.EndMilliseconds <= block.StartMilliseconds))
        throw MediaPreviewException.SubtitleOffsetInvalid();
    return new UTF8Encoding(false, true).GetBytes(RenderCrLf(adjusted));
}
```

Preserve cue payload text and ordering, normalize only timing lines and CRLF separators, use checked arithmetic, clip starts and ends below zero, and reject a cue whose adjusted end is not later than its adjusted start.

- [ ] **Step 4: Cover UTF-8/UTF-16, BOM, limits, multiline cues, overflow, and malformed blocks**

Run the same focused command. Expected: all `SrtParserTests` pass with zero failures.

- [ ] **Step 5: Commit the parser slice**

```powershell
git add src/ReachCommander.Application/MediaPreviews src/ReachCommander.Infrastructure/MediaPreviews tests/ReachCommander.UnitTests/MediaPreviews/SrtParserTests.cs
git commit -m "feat: parse and offset SRT subtitles"
```

---

### Task 2: Secure media probe, preview sessions, and temporary HLS worker

**Files:**
- Create: `src/ReachCommander.Application/MediaPreviews/IMediaPreviewService.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewOptions.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewOptionsValidator.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaProbeRunner.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaTranscodeRunner.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewSessionStore.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewQueue.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewWorker.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewService.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewCleanupService.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Test: `tests/ReachCommander.UnitTests/MediaPreviews/MediaPreviewServiceTests.cs`
- Test: `tests/ReachCommander.UnitTests/MediaPreviews/MediaProcessRunnerTests.cs`

**Interfaces:**
- Consumes: `SrtParser` and existing `IPathSecurityService`.
- Produces:

```csharp
public interface IMediaPreviewService
{
    ValueTask<MediaPreviewSession> CreateAsync(CreateMediaPreviewCommand command, CancellationToken ct);
    ValueTask<MediaPreviewSession> GetAsync(Guid sessionId, CancellationToken ct);
    ValueTask<MediaPreviewSession> RequestFallbackAsync(Guid sessionId, CancellationToken ct);
    ValueTask<MediaPreviewSession> SelectSubtitleAsync(Guid sessionId, string subtitlePath, CancellationToken ct);
    ValueTask<MediaAsset> OpenDirectContentAsync(Guid sessionId, CancellationToken ct);
    ValueTask<MediaAsset> OpenHlsAssetAsync(Guid sessionId, string assetName, CancellationToken ct);
    ValueTask CloseAsync(Guid sessionId, CancellationToken ct);
    ValueTask<SubtitleSavePlan> PlanSubtitleSaveAsync(Guid sessionId, long offsetMilliseconds, CancellationToken ct);
    ValueTask<SubtitleSaveResult> ExecuteSubtitleSaveAsync(Guid planId, CancellationToken ct);
}
```

- `MediaPreviewSession` exposes only opaque IDs, logical names, `probing|transcoding|ready|failed`, `direct|hls`, duration, subtitle logical path, cues, read-only state, and safe failure code/detail.
- `MediaAsset` owns a readable stream, content type, length, and an `EnableRanges` flag.

- [ ] **Step 1: Write failing session-security tests**

Cover `.mp4/.mkv/.avi` allowlisting, directory/symlink rejection, exact same-name SRT discovery, same-directory manual selection, source read-only projection, stale fingerprints, physical-path redaction, and session expiry.

```csharp
[Fact]
public async Task CreateAsync_auto_selects_same_name_srt_without_exposing_physical_paths()
{
    var session = await _service.CreateAsync(new("media", "/Movies/Family Movie.mp4"), default);
    Assert.Equal("/Movies/Family Movie.srt", session.SubtitlePath);
    Assert.DoesNotContain(_sourceRoot, JsonSerializer.Serialize(session), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Write failing process-boundary tests**

Assert `ProcessStartInfo.UseShellExecute == false`, `ArgumentList` contains each argument separately, standard input is disabled, standard error is bounded to 64 KiB, cancellation kills the process tree, probe JSON is capped, and FFmpeg arguments are constructed exactly as follows:

```csharp
var expectedArguments = new[]
{
    "-nostdin", "-hide_banner", "-loglevel", "warning",
    "-i", inputPhysicalPath,
    "-map", "0:v:0", "-map", "0:a:0?",
    "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
    "-c:a", "aac", "-b:a", "160k",
    "-f", "hls", "-hls_time", "4", "-hls_list_size", "0",
    "-hls_segment_filename", Path.Combine(outputDirectory, "segment-%06d.ts"),
    Path.Combine(outputDirectory, "index.m3u8"),
};
```

- [ ] **Step 3: Run focused tests and verify they fail**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~MediaPreviewServiceTests|FullyQualifiedName~MediaProcessRunnerTests"`

Expected: compilation fails because media-preview services do not exist.

- [ ] **Step 4: Implement session state and safe direct-play classification**

Parse bounded FFprobe JSON and classify direct playback only when `format_name` includes `mov,mp4,m4a,3gp,3g2,mj2`, video is `h264`, and absent/first audio is `aac`. Everything else in the allowed extension set queues HLS. Store only protected server-side physical paths; map browser-facing records without them.

- [ ] **Step 5: Implement the single-slot worker and cleanup service**

Use a bounded `Channel<Guid>` with capacity 8 and one hosted consumer. Create per-session output only below `AuthenticationDataPaths.RootPath/media-previews/<session-id>`, publish HLS readiness after `index.m3u8` and the first segment exist, enforce 8 GiB/90-minute ceilings while FFmpeg runs, and delete output on Close, 20-minute inactivity, startup recovery, cancellation, or failure.

- [ ] **Step 6: Register validated options and hosted services**

```csharp
services.AddOptions<MediaPreviewOptions>()
    .Bind(configuration.GetSection(MediaPreviewOptions.SectionName))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<MediaPreviewOptions>, MediaPreviewOptionsValidator>();
services.AddSingleton<MediaPreviewSessionStore>();
services.AddSingleton<MediaPreviewQueue>();
services.AddSingleton<MediaProbeRunner>();
services.AddSingleton<MediaTranscodeRunner>();
services.AddSingleton<IMediaPreviewService, MediaPreviewService>();
services.AddHostedService<MediaPreviewWorker>();
services.AddHostedService<MediaPreviewCleanupService>();
```

- [ ] **Step 7: Run focused tests and commit**

Expected: all media session/process tests pass; no test output contains a fixture physical path.

```powershell
git add src/ReachCommander.Application/MediaPreviews src/ReachCommander.Infrastructure/MediaPreviews src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/MediaPreviews
git commit -m "feat: add secure media preview sessions"
```

---

### Task 3: Server-authoritative transactional subtitle save

**Files:**
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/SubtitleSavePlanStore.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/SubtitleSavePlanner.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/SubtitleSaveExecutor.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/IMediaPreviewFileSystem.cs`
- Create: `src/ReachCommander.Infrastructure/MediaPreviews/LocalMediaPreviewFileSystem.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewService.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Test: `tests/ReachCommander.UnitTests/MediaPreviews/SubtitleSaveTests.cs`

**Interfaces:**
- Consumes: session subtitle fingerprint, `SrtDocument.RenderWithOffset`, `DirectoryMutationLock`, and `IPathSecurityService`.
- Produces: `SubtitleSavePlan(Guid PlanId, DateTimeOffset ExpiresAt, string SubtitlePath, string BackupPath, long OffsetMilliseconds, bool CanExecute)` and `SubtitleSaveResult(string SubtitlePath, string BackupPath, bool RecoveryRequired)`.

- [ ] **Step 1: Write failing plan and transaction tests**

Cover zero-offset rejection, ±600,000 ms bounds, read-only rejection, `_original`, `_original (2)`, case-insensitive conflicts, ten-minute expiry, changed fingerprint, successful byte-for-byte backup, failure before backup move, failure after backup move with successful rollback, and rollback failure with `RecoveryRequired=true`.

```csharp
[Fact]
public async Task Execute_preserves_original_and_publishes_corrected_name()
{
    var plan = await _planner.PlanAsync(_sessionId, 1_400, default);
    var result = await _executor.ExecuteAsync(plan.PlanId, default);
    Assert.Equal(_originalBytes, File.ReadAllBytes(Path.Combine(_root, "movie_original.srt")));
    Assert.Contains("00:00:02,400", File.ReadAllText(Path.Combine(_root, "movie.srt")));
    Assert.False(result.RecoveryRequired);
}
```

- [ ] **Step 2: Run the focused test and verify the red state**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~SubtitleSaveTests`

- [ ] **Step 3: Implement immutable plans and unique backup naming**

Generate the complete corrected bytes during planning, store them only server-side, and freeze the original fingerprint, source/directory, logical names, physical names, offset, and expiry. Search backup candidates from `base_original.ext` through `base_original (999).ext`; fail closed after 1,000 occupied names.

- [ ] **Step 4: Implement locked staging/publish/rollback**

Inside one `DirectoryMutationLock` lease: re-resolve and revalidate the SRT, create `.reachcommander-subtitle-<plan-id>.partial` with create-new semantics, write/flush corrected bytes, move original to the reserved backup without overwrite, move staging to the original name without overwrite, and flush the directory where supported. Reverse the original move on publication failure and surface `subtitle_recovery_required` if rollback fails.

- [ ] **Step 5: Run focused tests and commit**

```powershell
git add src/ReachCommander.Infrastructure/MediaPreviews src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/MediaPreviews/SubtitleSaveTests.cs
git commit -m "feat: save corrected subtitles transactionally"
```

---

### Task 4: Authenticated media-preview HTTP API

**Files:**
- Create: `src/ReachCommander.Api/Contracts/MediaPreviews/MediaPreviewDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/MediaPreviewsController.cs`
- Create: `src/ReachCommander.Api/Errors/MediaPreviewExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`
- Modify: `src/ReachCommander.Api/Program.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Create: `tests/ReachCommander.IntegrationTests/MediaPreviewsApiTests.cs`

**Interfaces:**
- Produces these routes:

```text
POST   /api/media-previews
GET    /api/media-previews/{sessionId}
GET    /api/media-previews/{sessionId}/content
GET    /api/media-previews/{sessionId}/hls/{assetName}
PUT    /api/media-previews/{sessionId}/subtitle
POST   /api/media-previews/{sessionId}/fallback
POST   /api/media-previews/{sessionId}/subtitle-save-plans
POST   /api/media-previews/subtitle-save-plans/{planId}/execute
DELETE /api/media-previews/{sessionId}
```

- [ ] **Step 1: Write failing integration tests**

Prove anonymous requests return 401, mutation without `X-ReachCommander-CSRF` returns 400, rate-limit overflow returns 429 with `media_preview_rate_limited`, DTOs omit physical paths, direct content supports `Range: bytes=0-3` with 206/Content-Range, the explicit fallback action queues HLS for a direct session, HLS asset names accept only `index.m3u8` or `segment-######.ts`, and execution returns the logical corrected/backup names.

- [ ] **Step 2: Run the focused integration test and verify failure**

Run: `dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~MediaPreviewsApiTests`

- [ ] **Step 3: Implement narrow DTOs and controller actions**

Return `AcceptedAtAction` while a fallback session is probing/transcoding, `Ok` for ready direct sessions, `File(stream, contentType, enableRangeProcessing: true)` for direct content, no-store headers for all assets, and 204 for session close. Do not accept asset paths containing separators or percent-decoded separators.

- [ ] **Step 4: Add dedicated error mapping and rate limiting**

Map invalid format/encoding to 415/422, stale/expired plans to 409/410, capacity to 429, unavailable FFmpeg/source to 503, and recovery-required to 500. Log only source ID, logical path, opaque IDs, error code, exception type, and HResult.

- [ ] **Step 5: Run integration and full .NET tests, then commit**

Run: `dotnet test ReachCommander.slnx -c Release --no-restore`

Expected: all unit and integration tests pass with zero failures.

```powershell
git add src/ReachCommander.Api tests/ReachCommander.IntegrationTests
git commit -m "feat: expose authenticated media preview API"
```

---

### Task 5: Angular API client and subtitle synchronization store

**Files:**
- Modify: `client/reach-commander-ui/package.json`
- Modify: `client/reach-commander-ui/package-lock.json`
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Modify: `client/reach-commander-ui/src/app/testing/commander-api-test-base.ts`
- Create: `client/reach-commander-ui/src/app/core/state/media-preview.models.ts`
- Create: `client/reach-commander-ui/src/app/core/state/media-preview.store.ts`
- Create: `client/reach-commander-ui/src/app/core/state/media-preview.store.spec.ts`

**Interfaces:**
- Produces API methods matching Task 4 and `MediaPreviewStore.open(context, opener)`, `selectSubtitle(path)`, `setOffset(ms)`, `planSave()`, `executeSave()`, `retryWithFallback()`, and `close()`.
- `activeCue` is computed from video time plus the signed offset; the browser never sends adjusted SRT text.

- [ ] **Step 1: Install the pinned HLS client**

Run: `npm install --save-exact hls.js@1.7.1`

Expected: both package files record exactly `1.7.1`.

- [ ] **Step 2: Write failing API and store tests**

```typescript
it('sends only the opaque session and offset when planning a save', async () => {
  store.open(readyContext(), opener);
  store.setOffset(1400);
  await store.planSave();
  expect(api.savePlanRequests).toEqual([{ sessionId, offsetMilliseconds: 1400 }]);
});
```

Cover request encoding, stale async response suppression, polling transition from transcoding to ready, cue boundary selection, offset clipping display, zero/read-only save disablement, fallback retry, close cleanup, authentication reset, and safe Problem Details projection.

- [ ] **Step 3: Run focused Angular tests and verify failure**

Run: `npm test -- --watch=false --include src/app/core/api/reach-commander-api.spec.ts --include src/app/core/state/media-preview.store.spec.ts`

- [ ] **Step 4: Implement typed DTOs, client methods, and store state**

```typescript
export interface MediaPreviewContext {
  readonly sourceId: string;
  readonly videoPath: string;
  readonly videoName: string;
  readonly sourceReadOnly: boolean;
}

readonly activeCue = computed(() => this.state().cues.find(cue =>
  this.state().videoTimeMilliseconds >= cue.startMilliseconds + this.state().offsetMilliseconds &&
  this.state().videoTimeMilliseconds < cue.endMilliseconds + this.state().offsetMilliseconds,
) ?? null);
```

Use a request generation token for every open/select/plan/execute operation and bounded polling with cancellation on close or authentication reset.

- [ ] **Step 5: Run focused tests and commit**

```powershell
git add client/reach-commander-ui/package.json client/reach-commander-ui/package-lock.json client/reach-commander-ui/src/app/core
git commit -m "feat: add subtitle synchronization client state"
```

---

### Task 6: Media workspace UI and commander integration

**Files:**
- Create: `client/reach-commander-ui/src/app/features/media-preview/media-preview-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/media-preview/media-preview-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/media-preview/media-preview-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/media-preview/media-preview-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`

**Interfaces:**
- Consumes: `MediaPreviewStore` and `FileEntryDto` rows.
- Produces: `mediaPreviewRequested` from the panel and a blocking, focus-trapped dialog with opener restoration.

- [ ] **Step 1: Write failing component and shell tests**

Verify Enter/double-click opens only `.mp4/.mkv/.avi` filesystem files, archive entries retain current behavior, unsupported files retain the existing milestone message, the same-name SRT is announced, offset buttons and exact field update the cue immediately, beginning/middle/end buttons seek correctly, Space toggles playback only inside the dialog, Escape asks before discarding a non-zero unsaved offset, read-only state hides execution, and focus returns to the originating panel row.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `npm test -- --watch=false --include src/app/features/media-preview/media-preview-dialog.component.spec.ts --include src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

- [ ] **Step 3: Implement video/HLS lifecycle and subtitle overlay**

```typescript
private attachPlayback(video: HTMLVideoElement, session: MediaPreviewSessionDto): void {
  if (session.playbackMode === 'direct') {
    video.src = `/api/media-previews/${session.sessionId}/content`;
  } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
    video.src = `/api/media-previews/${session.sessionId}/hls/index.m3u8`;
  } else {
    this.hls = new Hls({ enableWorker: true });
    this.hls.loadSource(`/api/media-previews/${session.sessionId}/hls/index.m3u8`);
    this.hls.attachMedia(video);
  }
}
```

Destroy `Hls`, clear the video source, call `load()`, and close the server session on every close/destroy path. Use DOM text for subtitles rather than injecting HTML from the SRT.

- [ ] **Step 4: Implement the save confirmation and completion state**

Show the exact logical mapping `movie.srt → movie_original.srt`, state that the video is untouched, require a confirmation click, show rollback/recovery messaging, refresh both panels showing the directory after success, and keep the dialog open on failure.

- [ ] **Step 5: Run the complete Angular suite and production build**

Run: `npm test -- --watch=false`

Run: `npm run build`

Run: `npm run test:pwa && npm run verify:pwa`

Expected: all Angular tests and PWA checks pass; production build has no budget error.

- [ ] **Step 6: Commit the UI slice**

```powershell
git add client/reach-commander-ui/src/app
git commit -m "feat: add subtitle synchronization workspace"
```

---

### Task 7: Docker FFmpeg packaging, licensing, and container contracts

**Files:**
- Modify: `Dockerfile`
- Modify: `.github/workflows/ci.yml`
- Create: `THIRD-PARTY-NOTICES-FFMPEG.md`
- Create: `tools/container_media_preview_smoke.py`
- Modify: `tests/installer/workflow-contract.test.mjs`
- Modify: `tests/installer/docs-contract.test.mjs`
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `docs/deployment/ubuntu.md`

**Interfaces:**
- Produces a multi-architecture runtime containing `/usr/bin/ffmpeg` and `/usr/bin/ffprobe` from Alpine 3.22 package `ffmpeg=6.1.2-r2`.

- [ ] **Step 1: Write failing Docker/workflow/documentation contracts**

Require the fixed Alpine 3.22 runtime tag, exact FFmpeg package version, container smoke invocation, media temporary directory as writable `/data` state rather than a new host mount, FFmpeg license notice, supported formats, read-only behavior, backup naming, resource limits, and troubleshooting messages.

- [ ] **Step 2: Run contracts and verify failure**

Run: `node --test tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs`

- [ ] **Step 3: Pin and verify FFmpeg in the runtime image**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.22 AS runtime
ARG FFMPEG_PACKAGE_VERSION=6.1.2-r2
RUN apk add --no-cache "ffmpeg=${FFMPEG_PACKAGE_VERSION}" \
    && ffmpeg -version | grep -F 'ffmpeg version 6.1.2' \
    && ffprobe -version | grep -F 'ffprobe version 6.1.2'
```

Retain `USER 1000:1000`, read-only root filesystem compatibility, dropped capabilities, and `/data` ownership.

- [ ] **Step 4: Add a generated-fixture container smoke**

Generate a two-second color/test-tone MP4 and a two-cue SRT inside the temporary smoke source, authenticate through the existing helper, create a preview session, assert direct range response, plan/save a `+1000 ms` correction, verify the byte-for-byte `_original` backup and shifted output, then run an FFmpeg command in the container to prove the fallback binary is executable. Never commit media binaries.

- [ ] **Step 5: Run contracts and commit**

```powershell
git add Dockerfile .github/workflows/ci.yml THIRD-PARTY-NOTICES-FFMPEG.md tools/container_media_preview_smoke.py tests/installer README.md docs
git commit -m "build: package bounded FFmpeg media previews"
```

---

### Task 8: Browser acceptance, regression verification, and release readiness

**Files:**
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/support/media-preview-fixture.ts`
- Create: `tests/e2e/specs/subtitle-synchronization.spec.ts`
- Modify: `README.md`

**Interfaces:**
- Validates the complete browser workflow while API integration and container smoke validate real bytes/processes.

- [ ] **Step 1: Add deterministic browser fixtures**

Seed logical `Family Movie.mp4` and `Family Movie.srt` entries without committing a large video. Route media session/content endpoints in `media-preview-fixture.ts` using a one-second browser-playable data fixture, record all mutation requests, and emulate direct, transcoding, ready, read-only, stale, rollback, and recovery-required states.

- [ ] **Step 2: Write the browser acceptance scenario**

```typescript
test('synchronizes an SRT while preserving the original mapping', async ({ page }) => {
  await openPath(page, 'right', '/Movies');
  await page.locator('[data-path="/Movies/Family Movie.mp4"]').dblclick();
  await expect(page.getByRole('dialog', { name: 'Synchronize subtitles' })).toBeVisible();
  await page.getByRole('button', { name: '+1 second' }).click();
  await page.getByRole('button', { name: 'Save corrected subtitle' }).click();
  await expect(page.getByText('Family Movie.srt → Family Movie_original.srt')).toBeVisible();
  await page.getByRole('button', { name: 'Confirm save' }).click();
  await expect(page.getByText('Original subtitle preserved')).toBeVisible();
});
```

Also cover keyboard opening, manual SRT choice, MKV fallback progress, read-only preview, unsaved-close confirmation, failure retry, no host paths in UI/network bodies, and a 390×844 viewport.

- [ ] **Step 3: Run focused and full browser acceptance**

Run: `npm test -- specs/subtitle-synchronization.spec.ts`

Run: `npm test`

Working directory for both: `tests/e2e`.

- [ ] **Step 4: Run the complete release gate**

```powershell
dotnet test ReachCommander.slnx -c Release --no-restore
python -m unittest discover -s tests/installer -p "test_*.py"
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_command.sh
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
```

Expected: zero failures, only documented platform skips, successful production/PWA output, and no diff-check warnings.

- [ ] **Step 5: Commit acceptance coverage and inspect repository state**

```powershell
git add tests/e2e README.md
git commit -m "test: cover subtitle synchronization workflow"
git status --short --branch
```

Expected: `master` contains the feature commits and only the pre-existing untracked `NC-theme.png` remains outside version control.
