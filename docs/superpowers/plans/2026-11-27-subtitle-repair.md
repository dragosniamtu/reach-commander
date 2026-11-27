# Subtitle Repair Implementation Plan

> **For agentic workers:** Use subagent-driven-development for the isolated worker task and reviews; execute coordinated API/UI changes in this session. Track evidence below.

**Goal:** Repair existing SRT encoding and timing in any language without changing text, preserving original bytes before save.

**Architecture:** Extend the media workspace's session, encoding review and transactional save. A versioned repair mailbox in the existing CPU worker computes a global time scale and offset; only the API applies that transform to the original decoded cues. Separate repair request/result directories prevent generation maintenance from cancelling repair jobs, while one supervisor consumes both queues sequentially.

**Tech Stack:** .NET 10, Angular, Python 3.11, ffsubsync/WebRTC VAD, xUnit, Vitest, Playwright.

## Global constraints

- Retain all existing uncommitted Menu changes. Work on the current feature branch; do not publish or deploy.
- Preserve decoded cue text in any language; output UTF-8 and retain byte-exact original backups.
- Strict decoding with preview/explicit confirmation for ambiguous legacy encodings; never replace invalid bytes silently.
- Offline CPU operation, existing two-thread limits, one worker child at a time; no Whisper models required for repair.
- Only validated configured video paths reach the worker. Only the API writes media-directory subtitles.
- Sync transform: `newTimeMs = round(oldTimeMs * timeScale) + offsetMilliseconds`; finite scale 0.8..1.25, offset -600000..600000. Invalid/clipped-empty cues block save.
- Cleanup, cancellation, stale snapshots, source policy and transactional backups remain mandatory.

## Task 1: CPU alignment worker

Files: `worker/subtitles/subtitle_sync.py`, `worker.py`, `requirements.txt`, `Dockerfile`, `tests/test_sync.py`, existing worker tests.

Interface: repair-requests/<id>.json carries `{version:2,id,kind:"synchronize",videoPath,subtitleSrt}`. `subtitleSrt` is bounded normalized UTF-8 SRT (at most 4 MiB / 20000 cues). repair-results/<id>.json uses the existing v1 result envelope; successful synchronization additionally includes integer `offsetMilliseconds` and finite `timeScale`. No rewritten subtitle text is needed. Existing generation requests/results and status.json remain unchanged.

Publish independent `repair-status.json`: `{version:1,updatedAt,synchronizationAvailable}` every heartbeat, true only if the pinned alignment dependency is importable. Do not require language models. Repair cancellation markers use repair-requests/<id>.cancel. Consume both queues in one supervisor, with cleanup before terminal publication and parent-death protection unchanged.

- [x] Add failing tests for strict protocol, dependency readiness, transform direction, audio cleanup and failure/cancellation.
- [x] Implement bounded extraction using existing validated FFmpeg path, then ffsubsync with WebRTC against private local audio and normalized SRT. Pin a verified published dependency version. Never allow it to open arbitrary input URLs/paths.
- [x] Run worker tests and a real dependency alignment fixture with known offset and rate change. Review worker diff.

## Task 2: Encoding, preview, save and asynchronous repair API

Files: Application/MediaPreviews models/interfaces; Infrastructure/MediaPreviews service/session/save/parser and new repair service/mailbox; Api/Controllers/MediaPreviewsController and contracts; Infrastructure dependency registration; related xUnit tests.

Keep existing media endpoints compatible. Add repair load request with source encoding and explicit reviewed flag; response includes media session plus encoding sample/confirmation requirement. Add capabilities, start/status/cancel sync endpoints. Session-owned task results carry stage, status, transform and safe error code. Closing/replacing a session cancels work; no background operation can install subtitles. Encoding-only save and transformed save use the existing review/execute transaction. Loading a malformed same-name SRT does not prevent opening video.

- [x] Red/green tests for Romanian legacy input, non-Latin Unicode, ambiguous encoding, constant offset + scale, zero-offset conversion and backups.
- [x] Add reusable decoding options for common scripts, retaining strict fallback behavior. Validate unchanged original/video snapshots at load, synchronization completion, planning and execution.
- [x] Add bounded repair mailbox polling, separate readiness, cancellation and startup cleanup. Reuse common safe filesystem exchange behavior where practical.
- [x] API integration and unit tests for authentication, safe errors, stale/read-only files, worker unavailable, and successful preview/save; retain existing rate-limit policies. Review API diff.

## Task 3: Menu and repair workspace

Files: toolbar/shell; core API models/client/fake; media-preview state/models/dialog; focused unit and browser tests.

- [x] Add failing tests for Repair subtitles Menu item and video selection, decoding preview, automatic sync progress/cancel, transformed preview and save.
- [x] Add icon and label; open existing player with encoding controls and Auto-sync. Keep manual adjustment, all themes and stable focus restoration. Confirm ambiguous text before sync/save.
- [x] Show UTF-8 output, original backup name, and estimated correction. Retain original language text; no generation/translation calls.
- [x] Run UI tests, build, and browser acceptance for repair and existing menu/media/encoding flows. Inspect screenshots.

## Task 4: Release integration, documentation and verification

- [x] Update worker container/publication checks and operator docs for model-free repair readiness, cleanup and the required host installer refresh.
- [x] Run full relevant local .NET, worker, frontend, PWA and browser suites; inspect final diff and independent review. Linux image verification is gated in CI and could not run on this Windows host.
- [x] Report completed behavior, exact validation and environment limitations. Leave release publication to a separate user instruction.

## Progress

- Approved spec and broader language requirement recorded. Existing Menu work retained; implementation starting.
- Worker complete; independent review approved after native Linux dependency build and overlapping-cue rasterization fixes. 45 tests passed / four platform skips, real signed offset/rate fixtures and silent-audio rejection passed with ffsubsync 0.4.31. Docker/WSL are unavailable locally; native runtime import is asserted during the image build and a real Linux container alignment test is now a publication prerequisite.
- Backend final review approved after six regression cases failed before their fixes. 939 unit tests and 161 integration tests passed, including stale confirmation, malformed mailbox recovery, snapshot invalidation/cancellation and same-session second-save content checks.
- UI final review approved after repeat-save rebasing, bounded failed polling and previous-cue suppression fixes. 597 unit tests and production build passed. Browser repair proved two consecutive saves, preview agreement and both byte-exact backups. Final 86-test browser suite passed with retries disabled, including complete repair/save flows in Modern, Norton and Windows 95. Screenshots inspected and retained under artifacts/subtitle-repair/preview*.png.
- Installer allowlist now accepts only the defined repair queue/status files and atomic temporary names; 204 installer Python tests passed with nine platform skips. Added migration instructions for existing hosts because image-only updates do not replace the host helper.
- Independent final integration review approved. The browser helper now waits for native modal dialogs before reopening Menu; the original failure reproduced once in 12 runs and all 12 repetitions passed after correction. Repeat-save coverage runs once while each theme runs the full repair/save flow, keeping the test burst within the unchanged real 20/minute mutation limit.
- Workflow contracts 21 passed; documentation contracts 13 passed; PWA contracts 2 passed and production PWA verification passed.

## Final commands and limits

- `dotnet test tests/ReachCommander.UnitTests --no-restore`: 939 passed, zero skips (Debug/net10.0).
- `dotnet test tests/ReachCommander.IntegrationTests --no-restore`: 161 passed, zero skips (Debug/net10.0).
- Frontend `npm test -- --watch=false` with `NODE_OPTIONS=--no-experimental-webstorage`: 597 passed; `npm run build` and `npm run verify:pwa` passed.
- `python -m unittest discover -s worker/subtitles/tests`: 45 passed, four platform skips. Real ffsubsync fixture recovered known positive/negative offsets and rate changes; rejected silence and left no temporary audio/models.
- `python -m unittest discover -s tests/installer -p "test_*.py"`: 195 passed, nine platform skips.
- `node --test tests/installer/docs-contract.test.mjs tests/installer/workflow-contract.test.mjs`: 34 passed.
- Browser `npm test -- --max-failures=1`: 86 passed, zero skips, retries disabled (2.2 minutes). Worker progress/results are mocked in browser tests; encoding, preview and save use the real API. Alignment itself is covered by the separate real worker fixture.
- `git diff --check`: passed. Scoped worker/backend/UI and final integration reviews approved.

Implementation remains local on `codex/header-action-menu` together with the prior Menu changes. No commit, push, version bump, publication or deployment. Docker/WSL are unavailable here, so the Linux worker image build and native runtime test remain mandatory CI release gates. The user's actual film/subtitle was not provided and has not been tested.
