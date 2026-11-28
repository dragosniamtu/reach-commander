# Batch subtitle generation

The user requested selecting multiple videos, reviewing their list, and processing them one at a time using the existing subtitle process.

## Behavior

- Generate Subtitles accepts one focused video, one folder (existing recursive behavior), or multiple selected video files from the active writable filesystem source.
- Multi-selection is captured in visible row order. Reject unsupported, hidden/stale, read-only, symbolic-link or mixed folder/file selections rather than silently omitting entries.
- The configuration dialog shows the selected paths and count. One choice of languages and skip-existing applies to the entire batch.
- Submit the whole selection atomically as one durable server job. Populate its pending file list immediately. Use the existing sequential engine/install loop, per-file errors, skip receipts, original backups, cancellation and progress.
- Closing the browser does not stop the job. Queued jobs retain their selection after restart; interrupted running jobs retain the existing explicit-rerun policy.
- Single-file and folder requests, historical records and worker/installer formats remain compatible. No model, inference, subtitle timing or host deployment changes.

## Approach

Extend the existing job request with an optional ordered `paths` list; retain `path` as its first entry for old clients and history. Validate and normalize all batch entries before enqueueing; reject mismatched first path and invalid entries; deduplicate canonical file paths. Bound count, path length and metadata size. Discover a batch from its saved list, never the parent folder. Revalidate each video immediately before processing so disappeared/changed files fail individually while remaining videos continue.

Persist each batch video's size and last-write timestamp privately with the job to detect changes before its turn, including after a restart. Keep these snapshots out of public requests and responses. Old records without snapshots remain readable. Batch files with read-only attributes are rejected before enqueueing and checked again before processing. The picker rejects names containing literal backslashes because the logical-path protocol treats them as separators.

A browser-side queue would stop when the page closes. Separate jobs per video would hit the eight-job capacity and permit partial submission. A single server job reuses the established folder pipeline and provides one list, progress total and Cancel action.

## Verification

Regression coverage includes selected-only processing, order, one-at-a-time execution, invalid selection rejection before queueing, deduplication, persistence, cancellation, per-file failures and existing single/folder behavior. UI tests cover selection snapshots, one request, selected paths, queued progress and closing/reopening. Browser coverage uses real multiselection, the Subtitles menu, and a narrow viewport. Run relevant .NET, frontend and browser checks and build before reporting completion.
