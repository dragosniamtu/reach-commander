# AI Subtitle Generation Implementation Plan

**Goal:** Generate English and/or Romanian SRT files for an existing video or recursive folder selection, with durable background progress and safe preservation of existing subtitles.

**Architecture:** Keep the Angular shell and authenticated .NET controllers. Reuse `IPathSecurityService`, `DirectoryMutationLock`, `AtomicJsonFile`, the existing queue signal primitive, and SRT parsing. A dedicated single-consumer background service persists subtitle jobs under the authentication data root. A Python worker exchanges bounded JSON requests/results through a separate shared directory, reads media only, and performs FFmpeg + faster-whisper + English-to-Romanian translation locally. Only the API installs subtitles.

**Tech stack:** Existing .NET 10 / Angular 22; Python 3.11; faster-whisper small/int8; Marian English-to-Romanian model.

## Constraints

- English is selected initially; Romanian is optional; at least one language is required.
- Primary output is exactly `<video basename>.srt`; when both languages are selected Romanian is `<video basename>.ro.srt`.
- Preserve associated SRT, ASS, SSA, VTT sidecars, including language/forced/SDH suffixes; never touch another video's subtitles. Reject ambiguous ownership rather than guess.
- Validate every requested output before renaming originals. Stage beside the video; journal renames and publication; compensate failures and recover interrupted transactions without deleting unproven files.
- Browser sends source IDs/logical paths only. Worker receives server-resolved physical video paths and has an independent root allowlist.
- Non-root, read-only container roots, no Docker socket, no published worker ports, no broker/database, one transcription at a time.
- Persist jobs and success receipts; skip only when video fingerprint, selected languages, and installed output hashes still match. Cancellation and restart must preserve originals.
- Keep builds/tests mocked with respect to AI models and operate only on temporary fixtures.

## Tasks and verification

- [x] Backend orchestration: add `Application/SubtitleGeneration` contracts and `Infrastructure/SubtitleGeneration` path discovery, persistent job store, mailbox engine client, background service. Add authenticated API controller and stable Problem Details. Include active jobs in update/source-change eligibility. Tests cover video/folder selection, recursion/deduplication, authorization, cancellation, failures, reruns and restart.
- [x] Safe installation: add installer and tests in the same feature folders. Reuse the existing SRT parser, additionally enforce ordered timestamps/nonblank text/UTF-8 and matching bilingual timing. Test naming, sidecar ownership, backups/collisions, all requested outputs validated first, failed publication rollback, interrupted recovery and skip receipts.
- [x] Worker/deployment: add `worker/subtitles` implementation and dependency-free unit tests with mocked models/processes. Document and test the mailbox protocol. Add opt-in `compose.subtitles.yaml`, separate model download command/cache, CPU/thread/temp bounds and cleanup. Add setup/permissions/usage documentation.
- [x] UI: add minimal language dialog, progress/history/cancellation, context-menu and toolbar actions, refresh recovery and protected-state cleanup using existing Angular conventions. Test language defaults/validation, API payloads, selection eligibility, polling/recovery/cancel.
- [x] Integration review: run `dotnet test ReachCommander.slnx -c Release`, worker unittest suite, Angular tests/build/PWA checks and review security/rollback paths. Docker validation/model inference require available Docker/models; explicitly report any environment limit.

## API contract

`GET /api/subtitle-jobs` returns `SubtitleJobStatus[]`; `POST /api/subtitle-jobs` accepts `{sourceId,path,english,romanian,skipExisting}` and returns 202 plus status; `GET /api/subtitle-jobs/{jobId}` and `POST /api/subtitle-jobs/{jobId}/cancel` return status.

Status: `{jobId,sourceId,path,english,romanian,phase,stage,currentPath,currentPercent,files,createdAt,updatedAt,error}`. Phases: `queued`, `discovering`, `running`, `cancelling`, `completed`, `cancelled`, `failed`, `interrupted`. Files: `{path,phase,error}`; phases `pending`, `running`, `completed`, `skipped`, `failed`, `cancelled`. Error is a safe message or null. All paths in API results are logical.

## Worker protocol v1

Shared directory has `requests` and `results`. API atomically writes `requests/<32 hex id>.json`: `{version:1,id,videoPath,romanian}`. Worker independently validates the absolute video path against `SUBTITLE_MEDIA_ROOTS` (JSON string array), rejects symlinks and verifies audio. API writes `requests/<id>.cancel` for cancellation. Worker writes atomic `results/<id>.json`: `{version:1,id,state,stage,percent,englishSrt,romanianSrt,errorCode}`. State is `running`, `completed`, `failed`, or `cancelled`; SRT fields null until completion; percent 0..100; errorCode is safe enumerated code. API never accepts paths or filenames from worker results. Worker processes exactly one request at a time and does not reprocess a terminal result. API removes request/result/cancel only after a terminal result; abandoned work is cancelled on service restart. Request and result identifiers must match. Bounded result size 16 MiB; timeout default 6 hours. Model cache and audio temporary storage are separate from the mailbox.

## Progress ledger

- Repository inspection complete: layered .NET controllers, Angular standalone components/signals, existing atomic JSON jobs and directory locks; Docker/read-only sources and installer reviewed. No AGENTS.md found.
- User supplied full implementation specification and explicitly requested continuous execution; proceed without another design approval gate.
- Independent backend review complete: fixed restart record-size mismatch, wide-directory resource bound, late cancellation cleanup and transient maintenance failure handling. Regression tests confirmed failing before each change and passing afterward.
- Independent installer review complete: fixed Linux case alias ownership and Alpine invariant-globalization language matching. Installer suite: 58 passing; direct invariant production-code probe: 15 passing.
- Final backend suite: 891 unit + 147 integration tests passed. Angular: 565 tests, production build, 2 PWA tests and production PWA verification passed. Python worker: 25 tests including real FFmpeg fixtures passed. Workflow/documentation contracts: 32 passed before final deployment allowlist integration.
- Environment limits: Docker unavailable; no Docker image build or downloaded-model inference performed. Windows Node 26 tests use `NODE_OPTIONS=--no-experimental-webstorage` for jsdom storage compatibility. Full invariant-mode vstest runner could not connect on Windows; production naming was verified directly in an invariant process instead.
- Deployment integration complete: extended Ubuntu/macOS installer and CLI allowlists, permissions, backup and retained-data handling for exact subtitle state paths. Git Bash fixtures passed 111 cases with 10 explicit Windows platform skips. Official ShellCheck v0.11.0 passed all eight changed scripts; syntax and whitespace checks passed. New storage contract test and worker tests run in CI. Native Linux/macOS ownership and real mount behavior were not exercised on this Windows host.
