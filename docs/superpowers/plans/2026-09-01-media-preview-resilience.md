# Media Preview Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent abandoned FFmpeg previews from blocking later users while making queue state and diagnostics explicit.

**Architecture:** Keep the existing bounded single-worker channel, but model queued and active work separately. Use the browser's existing status polling as a heartbeat, cancel stale pending work server-side, and preserve the existing 20-minute lifetime for ready sessions.

**Tech Stack:** .NET/ASP.NET Core hosted services, xUnit, Angular signals, Vitest, Bash installer tests.

## Global Constraints

- Work directly on `master`; do not create a worktree.
- Preserve authentication, antiforgery, rate limiting, path containment, and the single bounded worker.
- Do not touch the untracked `NC-theme.png` file.
- Do not expose physical source paths, credentials, or tokens in API responses.

---

### Task 1: Backend lifecycle and cancellation

**Files:**
- Modify: `src/ReachCommander.Application/MediaPreviews/MediaPreviewModels.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewSessionStore.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewService.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewCleanupService.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewOptions.cs`
- Test: `tests/ReachCommander.UnitTests/MediaPreviews/MediaPreviewServiceTests.cs`

**Interfaces:**
- `MediaPreviewPhase.Queued` is serialized as `queued` by the existing JSON enum configuration.
- `DeleteAbandonedPendingOutputs()` removes queued/transcoding sessions whose `LastAccessedAt` exceeds `PendingSessionInactivity`.

- [ ] Add tests asserting an HLS create returns `Queued`, worker processing changes it to `Transcoding`, explicit close cancels a running token, and stale pending work is removed.
- [ ] Run the focused unit tests and confirm failures are caused by the missing phase and lifecycle methods.
- [ ] Add the minimal lifecycle fields, transitions, cancellation, and cleanup implementation.
- [ ] Run the focused tests until green.

### Task 2: Process and service observability

**Files:**
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaTranscodeRunner.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewWorker.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewService.cs`
- Test: `tests/ReachCommander.UnitTests/MediaPreviews/MediaPreviewServiceTests.cs`

**Interfaces:**
- Existing `ILogger<T>` dependencies emit structured events keyed by `SessionId`.
- The runner logs process start, readiness, successful exit, timeout/cancellation, and bounded failure diagnostics.

- [ ] Add a log-capturing test for queued, started, ready, closed, and abandoned transitions.
- [ ] Run it and confirm the lifecycle messages are absent.
- [ ] Add structured lifecycle logs and cleanup warnings without changing public error details.
- [ ] Run focused tests until green.

### Task 3: Frontend queue state

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/media-preview.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/media-preview.store.ts`
- Modify: `client/reach-commander-ui/src/app/features/media-preview/media-preview-dialog.component.ts`
- Test: `client/reach-commander-ui/src/app/core/state/media-preview.store.spec.ts`
- Test: `client/reach-commander-ui/src/app/features/media-preview/media-preview-dialog.component.spec.ts`

**Interfaces:**
- API and client phase unions include `queued`.
- `applySession()` polls `queued`, `probing`, and `transcoding`.
- `statusLabel()` maps queued to `Waiting for preview worker`.

- [ ] Add store and component tests for queued polling, busy state, and label.
- [ ] Run focused Vitest tests and confirm the missing queued behavior fails.
- [ ] Implement the phase union, polling, and label changes.
- [ ] Run focused tests until green.

### Task 4: Installer data-tree contract

**Files:**
- Modify: `deploy/install.sh`
- Modify: `tests/installer/test_install.sh`

**Interfaces:**
- `validate_application_data_tree()` accepts only `media-previews`, a lowercase 32-hex session directory, `index.m3u8`, and `segment-[0-9]{6}.ts` below it.

- [ ] Add valid media-preview fixtures and verify the current installer rejects them.
- [ ] Extend the exact allowlist and directory permission preparation.
- [ ] Add invalid-name and symlink cases and run the installer suite until green.

### Task 5: Verification and documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `docs/deployment/ubuntu.md`

- [ ] Document queued/transcoding states, heartbeat cancellation, log commands, and the doctor allowlist.
- [ ] Run focused .NET and Angular tests.
- [ ] Run the complete backend, frontend, installer, PWA, and browser acceptance checks available locally.
- [ ] Review `git diff --check`, the final diff, and `git status --short`; leave `NC-theme.png` untouched.
