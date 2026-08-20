# ReachCommander Archive Browsing and Extraction Design

**Date:** 2026-08-20
**Status:** Approved for implementation planning
**Scope:** Read-only virtual browsing and safe extraction of ZIP, RAR, and 7z archives, including same-directory multi-volume sets

## Summary

ReachCommander will treat supported archives as read-only virtual folders. Enter or double-click opens a ZIP, RAR, or 7z file inside the active panel; the panel retains its normal sorting, filtering, selection, tab, and keyboard behavior while making the archive boundary explicit. Backspace or the synthetic parent row walks back through virtual folders and ultimately returns to the physical directory containing the archive.

Extraction follows the commander workflow. `F5` or an Extract toolbar action copies selected virtual entries to the opposite writable filesystem panel. `F5` on an unopened archive extracts the complete archive root to the opposite panel. Every extraction is server-authoritative, previews the complete expanded selection and destination conflicts, stages output beside the destination, and commits only after the complete batch succeeds.

SharpCompress 0.50.4 will provide managed ZIP, RAR, and 7z parsing and decompression behind ReachCommander-owned interfaces. Archive metadata parsing and decompression run in a bundled, one-shot .NET worker process so malformed or resource-intensive archives can be timed out or terminated without taking down the API process. No host agent, native archive program, or separately installed `7z` command is required.

## Confirmed Product Decisions

- Supported formats are ZIP, RAR, and 7z only.
- Archive contents are always read-only.
- Enter and double-click open an archive like a directory in the current panel.
- `F5` and an Extract action send selected contents to the opposite panel.
- With no selection inside an archive, the cursor entry is the extraction target.
- `F5` on an unopened archive extracts its complete root contents.
- Direct whole-archive F5 accepts exactly one primary archive candidate; multi-archive batch extraction is deferred.
- Complete-archive extraction does not add an automatic wrapper directory.
- Any destination conflict rejects the complete extraction; there is no overwrite, skip, merge, or automatic rename.
- Password-protected archives are detected and rejected. Password prompting is not included.
- Archives inside archives are regular virtual files and cannot be opened until extracted.
- Same-directory multi-volume ZIP, RAR, and 7z sets are supported.
- Only a primary volume can open or directly extract a volume set.
- The archive source may be read-only. The captured opposite-panel destination must be available, writable, and outside an archive.
- Plans and operations are process-local and do not survive an API restart.

## Goals

- Make archive navigation feel native to the existing dual-pane commander interface.
- Preserve a strict distinction between configured filesystem paths and untrusted archive entry paths.
- Support common single-file and multi-volume ZIP, RAR, and 7z archives on Windows and Ubuntu.
- Extract complete batches without partial visible success under handled failures.
- Protect older home-server hardware from archive bombs, excessive dictionaries, concurrency spikes, and malformed inputs.
- Keep all physical paths, library exceptions, and archive contents out of public API responses.
- Maintain keyboard accessibility, panel independence, immutable operation destinations, and safe focus restoration.
- Add archive support without introducing a host archive dependency or shell command execution.

## Non-Goals

- Creating or compressing archives.
- Adding, deleting, renaming, or updating archive entries.
- Password-protected archive access.
- Nested archive browsing.
- Self-extracting executable archives.
- Previewing, downloading, or streaming file contents directly from an archive.
- Extracting into another archive.
- Overwrite, skip, merge, or automatic conflict-renaming modes.
- Grouped rename or deletion of multi-volume sets.
- Durable jobs, distributed workers, or extraction recovery across API restarts.
- TAR, GZip, BZip2, XZ, comic-book aliases, or other formats supported by SharpCompress.
- General F5 filesystem copy. F5 is implemented only for archive extraction in this slice.

## Dependency Decision

The infrastructure project will pin `SharpCompress` version `0.50.4`. The package is MIT-licensed, supports .NET 10, exposes random-access archive APIs for ZIP, RAR, and 7z, and exposes multi-archive overloads that accept an ordered set of files or streams.

References reviewed for this decision:

- [SharpCompress supported-format matrix](https://github.com/adamhathcock/sharpcompress/blob/master/docs/FORMATS.md)
- [SharpCompress archive and multi-volume API](https://github.com/adamhathcock/sharpcompress/blob/master/docs/API.md#opening-archives)
- [SharpCompress extraction guidance](https://github.com/adamhathcock/sharpcompress/blob/master/docs/USAGE.md#extract-solid-rar-or-7zip-archives-with-progress-reporting)
- [SharpCompress 0.50.4 on NuGet](https://www.nuget.org/packages/SharpCompress/0.50.4)

SharpCompress types will not escape the infrastructure adapter. ReachCommander owns its archive models, validation, worker protocol, API errors, progress model, and extraction transaction. A later dependency replacement must not require an API or UI rewrite.

## Location Model

Filesystem paths and archive paths remain separate concepts.

```text
FilesystemLocation
  sourceId: downloads
  logicalPath: /backups

ArchiveLocation
  sourceId: downloads
  archivePath: /backups/photos.7z
  internalPath: /Family/2025
```

`archivePath` is an existing configured-source logical path and is resolved only by `IPathSecurityService`. `internalPath` is a normalized virtual path interpreted only by the archive subsystem. It is never passed to `Path.Combine`, `FileInfo`, `DirectoryInfo`, or the configured-source resolver.

Each frontend tab stores one discriminated location:

```typescript
type PanelLocation =
  | { readonly kind: 'filesystem'; readonly path: string }
  | {
      readonly kind: 'archive';
      readonly archivePath: string;
      readonly internalPath: string;
    };
```

Persisted archive tabs may be restored. A missing, changed, unsupported, or incomplete archive produces a recoverable tab error with a Return to containing folder action. It never silently redirects the tab to a different file.

## Filesystem Classification

Normal directory listing remains fast and does not parse archive headers. A deterministic filename classifier adds archive hints to filesystem entries:

- Single or primary `.zip`, `.rar`, and `.7z` names are openable archive candidates.
- `name.part1.rar`, including zero-padded `part01` variants, is a primary RAR volume.
- `name.rar` is the primary entry for legacy `.r00`, `.r01`, and later volumes.
- `name.7z.001` is the primary entry for consecutively numbered 7z parts.
- `name.zip.001` is the primary entry for consecutively numbered ZIP parts.
- `name.zip` is the primary entry for classic `.z01`, `.z02`, and later split ZIP parts.
- Secondary parts remain visible as ordinary files with archive-volume metadata and an explanation that the primary volume must be opened.
- Symbolic links and reparse-point files are never classified as openable archives.

Classification is an interaction hint, not proof of format. Enter, double-click, preview, and F5 verify the archive signature and volume structure server-side. A renamed non-archive receives `archive_invalid`, not an empty virtual folder.

The public file-entry contract gains optional archive metadata rather than exposing physical volume details:

```text
archiveFormatHint: zip | rar | sevenZip | null
archiveRole: single | primary | secondary | null
```

The existing file type and sorting behavior remain unchanged. Directories still sort before files; archive candidates sort with files.

## Multi-Volume Resolution

`ArchivePartResolver` accepts the resolved physical primary file and enumerates only its containing directory. It recognizes the approved naming families, requires contiguous numbering, orders parts in the format-specific order expected by the adapter, and rejects ambiguous sets.

Every discovered part must:

- be a direct sibling of the primary volume;
- resolve through the same configured source and remain inside its canonical root;
- be a regular non-symbolic-link file;
- match the expected case-insensitive naming family on Windows and case-sensitive physical name on Linux;
- be unique after platform path comparison;
- fit the configured volume count and total compressed-byte limits.

Missing middle or final volumes, duplicate indexes, mixed naming schemes, and mismatched archive identities produce `archive_volume_set_invalid`. The response includes safe volume indexes or expected logical filenames, never physical paths.

RAR part discovery may use SharpCompress discovery as a cross-check, but ReachCommander remains authoritative for confinement and completeness. Numbered ZIP and 7z parts are supplied explicitly to SharpCompress's multi-archive API.

## Virtual Catalog

`IArchiveBrowser` asks the worker to inspect the verified part set and return entry metadata. `ArchiveCatalogBuilder` then creates a platform-neutral immutable catalog.

Catalog construction:

1. Normalize separators to `/`.
2. Reject absolute, rooted, drive-letter, UNC, null-byte, dot, and parent-traversal segments.
3. Reject symbolic links, hard links, device entries, and other special types.
4. Validate path depth, component length, total length, and supported destination-name rules.
5. Detect duplicates after Unicode normalization and target-platform case comparison.
6. Synthesize missing parent directories for archives that store only file entries.
7. Aggregate directory descendant counts and uncompressed sizes with checked arithmetic.
8. Enforce entry, byte, ratio, and metadata limits before exposing any catalog.

The catalog stores normalized virtual entry identifiers, names, types, sizes, timestamps, extensions, attributes safe for display, and source entry indexes required by the adapter. It does not store decompressed data.

A bounded cache is keyed by the ordered volume fingerprint. The fingerprint contains source ID, logical primary path, each logical volume identity, length, and last-write time. The default cache keeps at most 16 catalogs for five minutes and is also bounded by the configured total cached entry count. A changed fingerprint creates a new catalog; it never reuses stale metadata.

Archive directory listing returns only immediate children of `internalPath`. Search and sorting remain client-side over the loaded virtual directory, matching normal panel behavior.

## Panel Interaction

Enter and double-click use the same open command.

- A real directory navigates through the existing filesystem path flow.
- A primary archive candidate verifies and opens `ArchiveLocation(..., internalPath: '/')`.
- A secondary volume leaves the panel in place and announces which primary volume must be used.
- A file inside an archive is not opened; existing file-preview deferral remains.
- An archive-looking file inside an archive remains a regular virtual file and reports that nested archive browsing is unavailable.

The archive path bar uses a visible `!` boundary:

```text
Downloads:/backups/photos.7z!/Family/2025
```

The panel exposes an `Archive · RO` state independent of the containing source's `RO` or `RW` policy. Add files and Multi-Rename are disabled while the active location is an archive. Search, sorting, selection, tabs, cursor movement, `Ctrl+A`, Escape, `..`, and Backspace remain available.

The archive boundary is represented through text, icon, tooltip, and accessible name; it is never communicated by color alone. Loading, empty, unsupported, and failure states use polite live-region announcements. Focus stays on the active panel after navigation.

## Extraction Target Semantics

The target is captured when the extraction review opens and never follows later panel changes. It contains the opposite panel side, source ID, source name, and filesystem logical directory.

The target is invalid when the opposite panel:

- is inside an archive;
- has no active tab;
- refers to an unavailable source;
- refers to a source with `isReadOnly: true`;
- cannot be resolved as a writable physical directory at preview or execution time.

Inside an archive, selected entries are roots. When selection is empty, the non-parent cursor entry is the sole root. Paths are extracted relative to the current virtual directory:

- selecting `photo.jpg` creates `destination/photo.jpg`;
- selecting `Albums` creates `destination/Albums/...`;
- selecting entries inside `/Family/2025` does not recreate `Family/2025` above those selected roots.

Duplicate or overlapping selected roots are collapsed so each output path is produced once. Selecting a directory includes all descendants. The synthetic parent row is never extractable.

Direct F5 from the filesystem view selects the complete archive root contents. It does not create a directory named after the archive. Any top-level destination collision blocks the complete operation.

Direct whole-archive extraction accepts exactly one primary archive candidate. If the normal filesystem selection contains multiple archives or mixes an archive with other files, ReachCommander explains that archive batch extraction is not included in the first release.

## Extraction Preview

`POST /api/archive-extractions/preview` accepts:

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

`entryPaths` contains normalized catalog paths. `extractAll: true` is allowed only for direct whole-archive extraction and requires an empty `entryPaths` array.

Preview performs all of the following:

- resolves and fingerprints every source volume;
- verifies format, completeness, and absence of encryption;
- builds or retrieves the validated catalog;
- resolves and expands the selected roots;
- calculates checked file, directory, compressed, and uncompressed totals;
- validates the destination source policy and canonical path;
- derives every destination-relative path;
- applies destination-platform name and collision rules;
- checks actual destination conflicts;
- applies configured resource limits.

An entry with no trustworthy declared size remains marked unknown in the preview. Known sizes are aggregated with checked arithmetic and used for the free-space preflight; unknown sizes never bypass the actual streamed-byte ceilings enforced during execution. The review labels an incomplete total instead of presenting it as exact.

The response includes a cryptographically random plan ID, expiry, format, volume count, selected roots, expanded counts, total uncompressed bytes, conflicts, violations, and `canExecute`. Physical paths and library-specific data are excluded. Plans expire after ten minutes and are held in a bounded in-memory store.

Invalid previews return a complete review result when safe and practical. Structural failures such as invalid format, encryption, missing volumes, or unsafe catalog entries use Problem Details because no trustworthy catalog exists to review.

## Execution Operation

`POST /api/archive-extractions/{planId}/execute` revalidates the plan and returns `202 Accepted` with an operation resource. Execution status is read through `GET /api/archive-extractions/{operationId}`. A cancellable operation accepts `POST /api/archive-extractions/{operationId}/cancel`.

Operation states are:

```text
queued
extracting
finalizing
completed
cancelled
failed
recoveryRequired
```

Status contains counts, streamed bytes, total expected bytes when known, percent when safely derivable, current logical entry name, cancellation availability, compensation state, and safe recovery names. Progress is monotonic. Polling is sufficient for the first release; SignalR is not added.

The operation service defaults to one active extraction. Requests beyond capacity return `archive_capacity_reached`; they are not placed in an unbounded queue.

## Worker Isolation and Protocol

`ReachCommander.ArchiveWorker` is a bundled .NET executable launched with `UseShellExecute = false`; no shell command is constructed. Physical part paths and operation data are sent over redirected standard input, not command-line arguments. The worker has no HTTP listener and initiates no network traffic.

One worker process handles one inspection or extraction request and exits. The API applies:

- a .NET managed-heap hard limit derived from configuration;
- a total working-set watchdog;
- inspection and extraction wall-clock deadlines;
- cancellation through a short grace period followed by process termination;
- bounded standard-error capture used only for internal diagnostics;
- a maximum framed-message size.

Inspection returns a length-prefixed metadata stream. Extraction returns framed entry-start, data-chunk, entry-end, progress, and completion messages. The API validates the normalized entry identity against the approved plan, creates the staging file itself, and writes counted bytes. The worker never receives or constructs a final destination path.

Solid RAR and 7z archives are processed sequentially. Unselected entries may still need to be decompressed or advanced through, but their data is discarded rather than sent to the API. This follows SharpCompress's sequential extraction guidance and avoids repeatedly restarting solid streams.

Unexpected protocol messages, duplicate entry starts, excess bytes, premature completion, checksum failures, worker crashes, memory excess, and timeouts fail the operation and trigger staging cleanup.

## Staging, Commit, and Compensation

The API creates a reserved sibling staging directory inside the destination:

```text
.reachcommander-extract-{operationId}.partial/
```

Staging inside the destination source keeps final moves on the same filesystem. During extraction, the API:

1. Opens only previously validated relative output paths under staging.
2. Creates directories deliberately; it never follows existing links.
3. creates files with create-new semantics;
4. counts actual bytes and validates them against entry and operation limits;
5. applies timestamps only after file content succeeds and only when the value fits the destination platform's supported range;
6. closes every handle before finalization.

After the worker succeeds, the API revalidates source volume fingerprints, destination source policy, canonical confinement, and the absence of every destination top-level name. It then enters non-cancellable finalization and moves staged top-level entries to their final names.

No extracted output becomes visible under its final name before the complete staging phase succeeds. Handled extraction, validation, or cancellation failures delete staging. A handled finalization failure compensates already-moved top-level entries back into staging. If compensation is incomplete, the operation becomes `recoveryRequired` and reports logical recovery names only.

An abrupt API, worker host, operating-system, or power failure can leave the reserved staging directory. ReachCommander does not automatically delete unknown post-crash data. The README instructs an administrator to confirm no extraction is active before inspecting or removing it.

## Locking and Staleness

Execution acquires mutation locks for the source archive's containing directory and the destination directory in deterministic source/path order. This prevents ReachCommander uploads, renames, and extractions from changing either location during the operation without deadlocking cross-panel operations.

External filesystem processes remain possible. Every volume is reopened with read sharing only, fingerprinted before worker launch, and fingerprinted again before finalization. Destination conflicts are also rechecked immediately before commit. A mismatch produces `archive_plan_stale` or `archive_destination_changed` and leaves final names untouched.

The operation does not claim crash-level atomicity. Its contract is all-or-nothing for handled failures, with explicit recovery reporting if compensation itself cannot complete.

## Security Rules

The archive subsystem assumes every header, path, size, timestamp, link flag, volume name, and decompressed byte count is attacker-controlled.

It rejects:

- absolute Unix paths;
- Windows drive, UNC, device, and alternate-data-stream paths;
- `.` and `..` segments;
- null bytes and invalid separators;
- empty path components after normalization where they create ambiguity;
- Windows reserved device names;
- trailing Windows dots or spaces;
- components or full paths beyond configured limits;
- duplicate paths after Unicode normalization;
- case collisions for a case-insensitive destination;
- file/directory ancestor collisions;
- symbolic links, hard links, junctions, FIFOs, sockets, block devices, and character devices;
- output paths that intersect the staging control directory;
- encrypted headers or encrypted entries;
- nested archive-open requests.

The API never calls a library helper that writes an archive entry directly to a directory. It never trusts client-provided entry sizes, destination paths, archive format, volume lists, or plan contents.

Logs contain operation ID, source ID, archive logical path, destination source ID, counts, status, timing, and safe error codes. They do not contain physical paths, passwords, decompressed contents, raw worker output, or complete private entry lists.

## Configurable Safe Defaults

Archive options are bound from the `Archives` configuration section and validated at startup.

| Option | Default |
|---|---:|
| `Enabled` | `true` |
| `MaxEntries` | `100000` |
| `MaxVolumes` | `100` |
| `MaxTotalCompressedBytes` | `500 GiB` |
| `MaxTotalExtractedBytes` | `500 GiB` |
| `MaxSingleExtractedFileBytes` | `200 GiB` |
| `MaxExpansionRatio` | `1000` |
| `MaxPathDepth` | `64` |
| `MaxPathCharacters` | `4096` |
| `MaxComponentCharacters` | `255` |
| `MaxConcurrentExtractions` | `1` |
| `InspectionTimeout` | `30 seconds` |
| `ExtractionTimeout` | `6 hours` |
| `WorkerManagedMemoryBytes` | `1 GiB` |
| `WorkerWorkingSetBytes` | `1536 MiB` |
| `PlanLifetime` | `10 minutes` |
| `CatalogLifetime` | `5 minutes` |
| `MaxCachedCatalogs` | `16` |
| `MaxCachedEntries` | `250000` |

An administrator may lower limits for constrained hardware. Raising them cannot disable path confinement, link rejection, encryption rejection, conflict rejection, checksum validation, actual streamed-byte counting, worker isolation, or destination write-policy checks.

## Error Contract

Archive failures use existing RFC 9457-style Problem Details with stable codes and safe details.

| Code | HTTP status | Meaning |
|---|---:|---|
| `archive_unsupported` | 415 | Extension or detected format is outside the approved set |
| `archive_invalid` | 400 | Signature or archive structure is invalid |
| `archive_encrypted` | 422 | Header or entry requires a password |
| `archive_volume_secondary` | 409 | User opened a non-primary part |
| `archive_volume_set_invalid` | 422 | Required parts are missing, duplicated, mixed, or mismatched |
| `archive_entry_unsafe` | 422 | Catalog contains a forbidden entry |
| `archive_limit_exceeded` | 413 | Metadata, volume, size, ratio, path, time, or memory limit was exceeded |
| `archive_destination_invalid` | 400 | Opposite panel is absent or points inside an archive |
| `archive_destination_read_only` | 403 | Destination policy or storage permission rejects writes |
| `archive_destination_conflict` | 409 | One or more final names already exist |
| `archive_plan_not_found` | 404 | Preview plan does not exist |
| `archive_plan_expired` | 410 | Preview plan expired |
| `archive_plan_stale` | 409 | Archive volumes changed after preview |
| `archive_destination_changed` | 409 | Destination state changed after preview |
| `archive_capacity_reached` | 429 | Active extraction limit is reached |
| `archive_worker_failed` | 500 | Isolated worker failed without safe detail |
| `archive_extraction_cancelled` | 499 | Cancellation completed during the staging phase |
| `archive_recovery_required` | 500 | Compensation could not restore the complete staged state |

Conflict and recovery responses may include safe logical names capped by count and length. They never include physical paths.

## API Surface

The first-release routes are:

```text
GET  /api/archives/entries
POST /api/archive-extractions/preview
POST /api/archive-extractions/{planId}/execute
GET  /api/archive-extractions/{operationId}
POST /api/archive-extractions/{operationId}/cancel
```

The browsing route accepts `sourceId`, `archivePath`, and virtual `path`. Extraction routes use JSON DTOs with bounded collections and body sizes. Operation IDs and plan IDs are unguessable random identifiers. Execute is idempotent for a plan ID, cancel is idempotent for an operation ID, and status reads are side-effect free.

No route accepts a physical path, user-supplied volume list, SharpCompress type name, arbitrary output name, overwrite flag, or password.

## Frontend State and Components

The Angular slice adds:

- discriminated filesystem/archive panel locations;
- archive metadata on filesystem file rows;
- archive-aware navigation in `CommanderStore`;
- archive entry loading through `CommanderApiPort`;
- an archive extraction store with review, execute, polling, cancellation, and terminal states;
- an accessible extraction review/progress dialog;
- archive and secondary-volume icon/description treatment;
- active-toolbar Extract state;
- F5 routing based on cursor/selection and panel location.

`CommanderStore` remains the owner of independent panel and tab navigation. Extraction context is an immutable snapshot, as with upload and Multi-Rename. Changing panels, paths, selections, or tabs after the review opens cannot redirect the operation.

The file table continues to emit one open event for Enter and double-click. Shell/store logic decides whether the row is a directory, primary archive candidate, secondary part, or regular file.

## Accessibility

- Primary archives, secondary volumes, directories, and ordinary files have distinct visible and spoken descriptions.
- `Archive · RO` appears in text and in the panel accessible name.
- The `!` archive boundary has an accessible phrase such as “inside archive.”
- Disabled Add files, Multi-Rename, and invalid Extract controls remain explainable through focusable descriptions.
- Extraction review is a named modal dialog with focus trap, initial focus, Escape rules, and opener focus restoration.
- Progress uses a labelled progress indicator plus a throttled polite live region.
- Errors identify the archive operation and safe corrective action without relying on color.
- Keyboard and pointer behavior remain equivalent at desktop and compact widths.

## Test Strategy

Implementation follows test-driven development.

### Backend unit tests

- ZIP, RAR, and 7z primary/secondary filename classification.
- New and legacy RAR parts, numbered 7z/ZIP parts, and classic split ZIP parts.
- Missing, duplicate, mismatched, ambiguous, and out-of-order volume sets.
- Canonical source confinement and symbolic-link rejection for every volume.
- Virtual folder synthesis when directory entries are absent.
- Selection expansion and overlapping-root collapse.
- Absolute, drive, UNC, traversal, ADS, null, reserved, link, device, depth, length, Unicode, case, file/directory, and staging-name attacks.
- Checked aggregation, entry limits, actual streamed-byte limits, ratio limits, timeouts, and memory termination.
- Encrypted header and encrypted entry rejection.
- Preview destination validation and complete conflict reporting.
- Plan expiry, execute idempotency, stale fingerprints, capacity, and cancel idempotency.
- Worker framing, malformed messages, crash, timeout, excess data, and checksum failure.
- Staging cleanup, non-cancellable finalization, compensation, and recovery reporting.
- Deterministic multi-directory lock ordering.

### Integration tests

- Every archive and extraction endpoint.
- Stable Problem Details codes and safe bounded extensions.
- No physical paths, worker arguments, stack traces, or raw library errors in responses.
- Read-only archive source to writable destination success.
- Read-only, unavailable, archive, or externally changed destination rejection.
- Real small ZIP, RAR, and 7z catalogs and extractions.
- Real small multi-volume sets for each approved naming family.

Tiny binary fixtures contain only generated sample names and bytes. `tests/fixtures/archives/README.md` records how each fixture was created, its source or generator, its license/provenance, expected contents, and whether it is intentionally malformed. No personal archive or downloaded private content is committed.

### Angular tests

- Archive location persistence and recovery.
- Archive path rendering and parent navigation.
- Enter, double-click, Backspace, parent row, and F5 routing.
- Archive read-only state and disabled mutation actions.
- Secondary-volume and nested-archive explanations.
- Immutable opposite-panel destination capture.
- Review, progress, cancellation, conflict, error, completed, and recovery-required states.
- Poll cleanup, stale response protection, live regions, focus trap, and focus restoration.

### Playwright acceptance

- Browse ZIP, RAR, and 7z as virtual folders.
- Navigate out through `..` and Backspace.
- Extract one selected file and one selected directory.
- Direct F5 whole-archive extraction.
- Multi-volume browse and extraction.
- Missing-volume and encrypted-archive messages.
- Complete conflict rejection with no destination changes.
- Read-only and archive destination rejection.
- Keyboard-only operation and compact-width usability.

Backend archive tests run on Windows and Ubuntu CI runners. Browser acceptance remains on Ubuntu and is also run locally on Windows during development.

## Deployment and Operations

The API publish includes the archive worker beside the server output. Docker copies both published executables and adds no system archive package. Windows development uses the same worker binary and protocol as Ubuntu deployment.

Health checks remain API-focused. Archive capacity and worker failures are reported through structured logs and operation status, not by marking the entire application unhealthy after one malformed archive.

Operators must reserve sufficient free destination space for the staged output plus final commit. Because staging is renamed rather than copied during successful finalization, the normal peak is one extracted copy, but compensation and external filesystem behavior can affect recovery. ReachCommander will fail early when available storage is less than the declared extracted size plus a configurable safety margin, then continue enforcing actual streamed bytes.

## Scaling and Deferred Evolution

At 10× home-server usage, catalog limits, cache bounds, one active worker, polling, and staged extraction remain predictable; extra requests receive an explicit capacity error. The first bottlenecks are archive header parsing, solid-stream decompression, destination throughput, and worker startup latency.

At 100× usage, the process-local plan and operation stores, single-host filesystem locks, and polling model become the limiting architecture. A future migration may add a durable job store, dedicated worker pool, server-sent progress, and per-source quotas. ReachCommander-owned interfaces and DTOs allow that migration without exposing SharpCompress or changing archive location semantics.

Password support, additional formats, nested archives, and conflict-resolution modes remain independent future designs. They are not latent flags in this implementation.

## Documentation Updates

Implementation updates:

- README feature summary and screenshots;
- Windows, Ubuntu, and Docker behavior;
- archive configuration and safe limits;
- F5 and archive keyboard behavior;
- supported format and volume naming table;
- error and recovery guidance;
- security policy discussion of untrusted archives and trusted-network deployment;
- API route reference;
- test counts and fixture provenance.

## Acceptance Criteria

The feature is complete when:

1. Enter and double-click browse valid ZIP, RAR, and 7z archives as read-only virtual directories.
2. Approved single and multi-volume naming families work on Windows and Ubuntu.
3. Backspace and the parent row return through archive directories and out to the containing filesystem directory.
4. F5 inside an archive extracts the selection or cursor entry to the captured opposite filesystem panel.
5. F5 on an unopened primary archive extracts its complete root contents.
6. Any destination conflict leaves every final destination name unchanged.
7. Encrypted, nested-open, invalid, incomplete, unsafe, or over-limit archives fail with stable safe errors.
8. Handled decompression and cancellation failures remove staging; handled finalization failures compensate or report safe recovery names.
9. No public contract or log leaks a physical path, password, raw worker error, or file content.
10. Windows and Ubuntu backend tests, Angular tests, production builds, API publish, and Playwright acceptance all pass.
