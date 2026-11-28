# Batch Subtitle Generation Implementation Plan

**Goal:** Generate subtitles sequentially for explicitly selected videos in one durable job.

**Architecture:** Add an optional ordered `paths` collection alongside the existing first `path`. Validate the complete selection before enqueueing, persist pending statuses, and reuse the sequential processing loop. The UI captures and displays the selection and sends one request.

**Tech stack:** ASP.NET Core/C#, Angular/TypeScript, xUnit, Vitest and Playwright.

**Constraints:** Preserve single video/folder APIs and old job records; no worker or host-tool changes; reject unsafe or unsupported selections; reuse backups, language choice, skip receipts and cancellation.

## Tasks

- [x] Backend: extend `SubtitleGenerationRequest` and `SubtitleJobRequest` with optional `IReadOnlyList<string>? Paths`; map the field in `SubtitleJobsController`. Validate batch count/length/size, anchor, all paths and video-only entries in `SubtitleDiscovery` before `SubtitleGenerationService` enqueues. Save normalized selection; populate pending files in `SubtitleJobStore`; retain per-file revalidation and cancel pending statuses when cancelling queued jobs. Write regression tests first in `SubtitleJobTests` and `SubtitleJobsApiTests`; verify selected order, one active engine, failures, cancellation, persistence, invalid inputs, and backward compatibility.
- [x] Frontend: optional readonly `paths` in subtitle context/request; capture all selected eligible rows, reject hidden/stale or mixed selections; freeze a copied snapshot in the store; include `paths` only for batches. Show a count and scrollable path list in configuration and an appropriate batch label in history. Test models, store and dialog first; keep existing single/folder expectations intact.
- [x] End-to-end: add browser cases in `tests/e2e/specs/subtitle-generation.spec.ts` for toolbar and context menu multiselection, one request, per-file progress, restoration and phone layout. Update `docs/ai-subtitles.md` with selection and sequential processing behavior.
- [x] Verification: run targeted .NET and frontend suites, production frontend build, and subtitle browser cases. Review the final diff for request bounds, persistence compatibility, errors, and selection races. Report implemented behavior and deployment status without publishing unless requested.

## Verification results

- New frontend cases failed against the original single-selection implementation, then passed after adding batch capture, immutable submission and the list. Follow-up regression tests covered list keyboard access, wholly filtered selections and Linux filenames containing backslashes.
- Full frontend suite: 608 passing. Local Node 26 requires `NODE_OPTIONS=--no-experimental-webstorage` for the existing jsdom localStorage tests; CI uses Node 24.
- Production frontend build: passed.
- Full .NET suite: 961 unit and 165 integration tests passing (1,126 total). An initial run encountered a transient Windows sharing error in the existing `SubtitleSyncMailboxTests.Mailbox_validates_transform_and_cleans_only_repair_files`; the complete rerun passed.
- Browser acceptance: 10 passing, including real multi-selection through Menu and right-click, a single batch request, per-file progress, refresh restoration and 390px layout. Tests use mocked inference responses; no additional model download or transcription run is needed for these queue/UI changes.
- Review findings resolved and re-reviewed: private size/mtime snapshots for changed queued files, read-only checks before enqueue and processing, literal-backslash selection rejection, accessible list scrolling and hidden-selection guidance.
- `git diff --check`: clean. Feature remains on `codex/batch-subtitle-generation`; no release was published.
