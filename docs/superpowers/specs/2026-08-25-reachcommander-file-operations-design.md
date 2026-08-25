# ReachCommander File Operations Design

**Date:** 2026-08-25

**Status:** Approved

**Target:** Authenticated Copy, Move, Delete/Trash, Restore, Empty Trash, and Create Directory operations in the existing Angular and ASP.NET Core application

## Problem

ReachCommander has a safe read-only browser, uploads, batch rename, and archive extraction, but its conventional file-operation commands remain incomplete. The backend `FilesController` exposes only listing and information endpoints. In the Angular command bar, F5 is Copy only as a disabled label when archive extraction does not apply, while F6 Move, F7 MkDir, and F8 Delete are explicitly reserved.

The missing operations are central to a dual-pane file manager and must work safely with large family-media libraries, multiple selected files and folders, read-only sources, Docker bind mounts, Windows development, and Ubuntu deployment. Copy and Move need conflict handling, cancellation, progress, queueing, and browser reconnection. Delete needs recoverability despite the application running inside a Linux container that cannot reliably call the Windows Recycle Bin, macOS Trash, or a Linux desktop trash service.

## Goals

- Enable F5 Copy, F6 Move, F7 Create Directory, and F8 Delete.
- Preserve the existing F5 archive-extraction behavior when the active context is an eligible archive.
- Operate on all selected items, or the focused item when there is no selection.
- Default Copy and Move to the opposite panel's current filesystem directory.
- Allow the destination logical path to be edited before preview.
- Preview conflicts before execution.
- Offer Overwrite, Skip, Create Unique Name, and Cancel, with an option to apply one decision to remaining conflicts.
- Merge colliding directories; do not remove destination-only contents.
- Run Copy and Move through a one-at-a-time persistent FIFO queue.
- Show a blocking progress dialog that can be collapsed into a compact top-toolbar item and restored later.
- Continue background work across dialog dismissal, panel navigation, browser refresh, and browser reconnection.
- Provide consistent ReachCommander-managed Trash inside each writable source.
- Provide Trash listing, multi-select Restore, permanent Delete, and Empty Trash.
- Never remove Trash contents automatically.
- Require explicit confirmation and an unrecoverable warning for permanent deletion.
- Reuse existing logical-path security, source policies, authentication, antiforgery, and directory mutation locking.
- Keep physical host paths out of browser state, API contracts, errors, and normal logs.
- Recover predictably from cancellation, I/O errors, stale plans, and server interruption.

## Non-goals

- Calling the Windows Recycle Bin, macOS Trash, or a desktop Linux trash daemon from the container.
- Copying, moving, or deleting entries from inside an archive.
- Following, recreating, or dereferencing symbolic links, junctions, or reparse points.
- Preserving every platform-specific ACL, extended attribute, alternate data stream, sparse-file layout, hard-link relationship, or filesystem compression flag.
- Resuming a partially copied file after a server restart.
- Running multiple Copy or Move executors concurrently.
- Automatically expiring Trash contents.
- Adding a distributed job system, external message broker, database, SignalR, or operating-system shell-command dependency.
- Making operations atomic across an entire multi-item batch.
- Changing upload, batch-rename, or archive-extraction behavior beyond coordinating through the existing mutation lock.

## Decisions

- Use one unified mutation subsystem instead of independent synchronous endpoints or host `cp`/`mv`/`rm` commands.
- Separate planning, persistence/queueing, execution, and Trash responsibilities.
- Use REST polling for progress and reconnect behavior.
- Persist plans and operation status under app-owned `/data/file-operations`.
- Execute exactly one queued Copy, Move, or permanent recursive deletion at a time.
- Revalidate queued operations immediately before execution.
- Use source-local `.reachcommander-trash` storage so Trash works consistently through Docker bind mounts.
- Use destination-local hidden staging and quarantine entries for safe per-item commits.
- Treat completed items as committed when an operation is cancelled; clean only the current partial item.
- Keep the newest 100 completed/failed/cancelled operation records. This bound does not affect Trash.
- Hide and reserve internal Trash, staging, and quarantine namespaces from normal browsing and user mutations.

## Considered approaches

### Unified preview and queued mutation engine — selected

A planner produces an immutable logical operation plan, a persistent FIFO queue serializes large work, and one executor applies validated plans with progress and cancellation. A separate Trash service shares the same path-security and mutation primitives. This adds the most deliberate structure but avoids duplicating destructive filesystem logic and supports the approved UX.

### Separate synchronous endpoints — rejected

Individual Copy, Move, Delete, and Create Directory endpoints would be quicker initially. They would duplicate validation and locking, keep requests open for large transfers, provide weak reconnect behavior, and make consistent progress/cancellation much harder.

### Host utility processes — rejected

Invoking `cp`, `mv`, `rm`, PowerShell, `gio`, or platform trash tools would make quoting, progress parsing, cancellation, error normalization, and container portability weaker. ReachCommander already has safe .NET filesystem and logical-path boundaries, so shelling out is unnecessary.

## Architecture

```text
Angular command/dialog/store
  -> authenticated logical-path API
  -> FileOperationPlanner
       -> source catalog
       -> path security
       -> filesystem inspection
  -> FileOperationRepository (/data/file-operations)
  -> FileOperationQueue (FIFO)
  -> FileOperationWorker (BackgroundService)
       -> DirectoryMutationLock
       -> FileOperationExecutor
       -> TrashService
       -> operation status/progress journal
```

### Planner

The planner is a pure orchestration boundary over source lookup, logical-path resolution, entry inspection, and naming policy. It accepts only source IDs, logical source paths, a destination source ID/logical directory for Copy or Move, and an operation type.

It produces an immutable plan containing:

- a generated plan ID and expiry;
- operation type;
- ordered source entries with logical paths, types, sizes where known, modification timestamps, and stable fingerprints;
- destination source ID and logical directory;
- destination entry proposals;
- conflicts and allowed conflict decisions;
- total item and byte estimates where calculable;
- required mutation-lock targets; and
- whether managed Trash is available for each Delete item.

Preview does not create destination directories, staging files, Trash directories, or operation jobs. Plans have a short bounded lifetime and are revalidated during submission and immediately before execution.

### Repository and queue

The repository writes JSON atomically under `/data/file-operations`. It stores logical paths only. It owns plan, job, progress, cancellation, and bounded history documents with strict schemas. Files are written through same-directory temporary files, flushed, and replaced.

The queue is FIFO and starts one large mutation at a time. New Copy, Move, or permanent recursive deletion jobs enter `queued`. Managed Trash moves and simple permanent file deletions may finish quickly, but use the same job contract when progress or cancellation is relevant. Create Directory remains a synchronous mutation.

Queued records survive process restart. On startup, they are revalidated before they can run. A record found in `running` is marked `interrupted`; it is not silently resumed.

### Worker and executor

An ASP.NET Core `BackgroundService` takes the next valid queued job. Before filesystem mutation, it re-resolves logical paths, compares fingerprints, checks source availability/read-only policy, re-evaluates conflicts, verifies destination free-space information when available, and acquires the existing `DirectoryMutationLock` for every affected source directory.

The executor reports:

- job phase and queue position;
- current logical item name;
- completed and total files/directories;
- completed and estimated total bytes;
- percentage when a total is known;
- transfer rate;
- elapsed duration and estimated remaining duration when calculable;
- conflict/skipped/unique-name counts; and
- final per-item outcomes.

The worker observes cancellation between enumeration steps and throughout buffered file copy. It never deletes a Move source until the corresponding destination item is committed.

## Logical path and source policy

- API requests and stored plans contain source IDs and normalized logical paths only.
- Existing `IPathSecurityService` remains the authority for resolving configured sources and preventing escape.
- Copy source entries may belong to a read-only source; the destination source must be available and writable.
- Move requires both source and destination to be available and writable.
- Delete, Trash, Restore, Empty Trash, and Create Directory require their mutation destination/source to be writable.
- Archive locations are not valid mutation sources or destinations.
- The source root `/`, a synthetic parent row, empty selection, duplicate paths, and nested duplicate selections are rejected where they would make semantics ambiguous.
- A directory cannot be copied or moved to itself or one of its descendants.
- Symbolic links, junctions, and reparse points are rejected whether selected directly or encountered during recursive enumeration.
- Every existing path component and every final parent is revalidated immediately before mutation.
- Logical names preserve spaces and Unicode but reject separators, control characters, `.`/`..`, platform-invalid names, reserved device names, and internal ReachCommander namespaces.
- Physical paths may appear only in tightly scoped infrastructure objects and sanitized diagnostic logs that are not returned to clients. Normal operation logs identify source IDs and logical paths.

## Reserved internal namespaces

Each writable source reserves:

```text
/.reachcommander-trash/
/.reachcommander-operation-<operation-id>-*
```

The exact Trash root and operation-owned staging/quarantine prefix are hidden from `LocalFileBrowser` listings and rejected as user-entered logical paths, operation sources, operation destinations, upload names, rename targets, and archive-extraction targets. Existing pre-feature entries with those exact reserved names make the affected capability unavailable until an operator resolves the collision; ReachCommander never assumes ownership of unknown existing content.

## Selection and destination capture

Copy and Move use the active panel. If it has selected entries, the operation captures all selected non-parent rows in visible order. Otherwise it captures the focused non-parent row. It never changes the captured set after the dialog opens.

The initial destination is the opposite panel's current filesystem directory. An archive location is not a destination; when the opposite panel is inside an archive, Copy and Move are disabled with an explanatory reason. The confirmation dialog permits an editable normalized logical destination path within the captured opposite source.

Changing the active panel, tabs, selections, filters, or paths after dialog open does not mutate the captured context. Closing and reopening captures a fresh context.

## Conflict model

Preview reports every currently detectable destination conflict. The dialog offers:

- **Overwrite:** merge colliding directories and replace only colliding files;
- **Skip:** leave the existing destination item unchanged;
- **Create Unique Name:** choose the first free deterministic sibling name;
- **Cancel:** do not submit the plan.

The user may apply one choice to all remaining conflicts. Per-conflict choices are stored in the immutable submitted plan. New conflicts found during execution make the plan stale; the worker fails safely and requests a new preview instead of inventing a decision.

Unique names use:

```text
file.txt
file (2).txt
file (3).txt

Folder
Folder (2)
Folder (3)
```

The suffix is inserted before the final extension. Matching follows the destination filesystem behavior exposed by the runtime. The planner and executor share one naming policy so preview and execution cannot disagree.

Directory Overwrite is a merge. Destination-only descendants remain unchanged. Each colliding child file uses the applicable conflict decision. Skip on a top-level directory skips that entire selected tree. Create Unique Name on a top-level directory copies or moves the complete tree under the new name.

## Copy semantics

Each file is copied into a hidden operation-owned staging file in its destination directory. The executor flushes and closes the file, verifies expected length, applies supported basic timestamps/attributes, revalidates the destination decision, and atomically renames the staging file into place.

For Overwrite, the existing destination file is first atomically moved to an operation-owned quarantine name in the same directory. If the current item cannot commit, the quarantined destination is restored. After the replacement commits, the quarantine entry is removed. Completed overwritten items are not rolled back if a later item fails or the user cancels.

Directories are created/merged incrementally. A newly named top-level directory can be staged and renamed when the filesystem permits; merged directories commit children individually. Unsupported metadata preservation is reported as a warning without exposing physical paths.

## Move semantics

When source and destination permit an atomic rename, Move uses it after conflict preparation and revalidation. For directory merge, children move individually under the same conflict rules.

When an atomic rename is unavailable, Move uses copy-then-delete:

1. copy and commit the destination item using Copy semantics;
2. revalidate the source fingerprint;
3. remove only that committed source item; and
4. report success.

If source deletion fails after destination commit, the destination remains valid and the result is `move_source_not_removed` with a clear “copied but not removed” message. The executor does not delete the destination to hide the successful copy.

## Cancellation and partial completion

Cancellation is cooperative. A queued job cancels without touching files. A running job stops at the nearest safe check, deletes only its current operation-owned staging entry, restores the current item's quarantined destination when needed, and releases locks.

Items committed before cancellation remain committed. The final result lists completed, skipped, failed, copied-but-not-removed, and not-started items. The UI refreshes all affected panels and presents the partial outcome rather than claiming an all-or-nothing rollback.

## Managed Trash

### Rationale

ReachCommander runs in a Linux container on Windows, macOS, and Linux hosts. It cannot reliably access a host desktop's native Recycle Bin or Trash. A source-local managed Trash gives consistent semantics and keeps recovery data on the same bind-mounted source.

### Layout

```text
/.reachcommander-trash/
  manifests/
    <trash-id>.json
  items/
    <trash-id>/
      <original-name-or-tree>
  staging/
```

Each strict manifest stores:

- trash ID;
- source ID;
- original normalized logical path;
- original name and entry type;
- size where known;
- deletion timestamp in UTC;
- stored relative item path; and
- a stable content/fingerprint summary used for restore validation.

It never stores the physical source root. Manifest and item must agree before an entry is listed or restored.

### Capability and fallback

Preview tests whether the source is writable and the managed Trash namespace can be safely owned. It does not claim capability when the reserved root collides with unknown content, required directories cannot be created, a safe move/copy cannot be planned, or destination capacity is clearly insufficient.

When managed Trash is unavailable for any selected item, the Delete dialog disables the default Trash option for that selection and shows the permanent-deletion confirmation message. ReachCommander never silently converts a requested Trash operation into permanent deletion.

### Delete to Trash

The default F8 action is Move to Trash. The dialog shows selected names and total count. Within the same filesystem, each item is renamed atomically under a new trash ID and its manifest is committed. Across a nested mount/filesystem boundary, the worker uses staged copy-to-Trash and removes the source only after the Trash item and manifest commit.

If Trash commit fails, the original item remains. Partial multi-item Trash operations report which entries moved and which did not.

### Permanent deletion

The Delete dialog contains a **Permanent delete** checkbox. Selecting it reveals exactly:

> This deletion is permanent, cannot be undone, and is unrecoverable.

The user must confirm after the warning is visible. Recursive permanent deletion uses the queue/progress contract. A simple permanent file deletion may complete synchronously through the same validated service. F8 never deletes without confirmation.

### Trash view, Restore, and Empty Trash

A Trash button in the top toolbar opens a dedicated dialog. It supports source filtering, multi-selection, Restore, permanent Delete, and Empty Trash. Trash records are sorted newest first and show original logical location, size, type, and deletion time.

Restore targets the original logical parent. It uses Overwrite, Skip, Create Unique Name, or Cancel for conflicts. It revalidates the manifest/item pair, destination source availability, destination writability, and original parent. Missing parents may be recreated only after the restore preview shows them. Restore removes the manifest and now-empty trash item directory only after destination commit.

Permanent Delete from Trash and Empty Trash show the unrecoverable message and require confirmation. Empty Trash can target the current source filter or all sources only when the dialog labels the scope explicitly. No timer, retention job, startup cleanup, or installer action removes valid Trash contents automatically.

## Create Directory

F7 opens a small modal for the active filesystem directory. It displays the parent logical path, focuses the name field, validates inline, submits on Enter, and closes on Escape when not submitting.

The synchronous endpoint revalidates source availability/writability, parent identity, reserved names, absence of conflicts, and symbolic-link safety under the mutation lock. It creates exactly one directory and returns its logical entry. It never creates missing parent chains.

## API contracts

All mutating endpoints require the existing authenticated administrator session and antiforgery header.

### Copy and Move

```text
POST /api/file-operations/preview
POST /api/file-operations
GET  /api/file-operations
GET  /api/file-operations/{operationId}
POST /api/file-operations/{operationId}/cancel
POST /api/file-operations/{operationId}/acknowledge
```

Preview accepts operation type, source ID, ordered logical paths, destination source ID, and destination logical directory. It returns a plan ID, expiry, item summary, destination, estimates, conflicts, and warnings.

Submission accepts the plan ID and exact conflict decisions only. The server reloads the stored plan, rejects unknown/duplicate/missing decisions, revalidates staleness, creates the durable job record, and returns `202 Accepted` with operation status.

Status documents expose phases `queued`, `validating`, `running`, `cancelling`, `completed`, `completedWithErrors`, `cancelled`, `failed`, and `interrupted`. They include logical progress and sanitized outcomes only.

### Directory and Trash

```text
POST   /api/directories
POST   /api/trash/preview
POST   /api/trash
GET    /api/trash?sourceId=<optional>
POST   /api/trash/restore/preview
POST   /api/trash/restore
DELETE /api/trash/items
DELETE /api/trash
```

Delete request bodies use trash IDs and logical fields rather than physical paths. Permanent deletion requires an explicit boolean acknowledgement bound to the approved preview; a client cannot add it to an unrelated/stale request.

## Error contract

Expected errors use the existing sanitized Problem Details envelope with stable codes, including:

```text
source_read_only
source_unavailable
destination_unavailable
invalid_operation_selection
invalid_directory_name
unsafe_symbolic_link
operation_plan_not_found
operation_plan_expired
operation_plan_stale
destination_conflict
insufficient_storage
operation_cancelled
operation_interrupted
move_source_not_removed
trash_unavailable
trash_manifest_invalid
trash_restore_conflict
permanent_delete_confirmation_required
```

Unexpected `IOException`, access, storage, and platform errors map to a stable public detail. The full physical exception is not sent to the client. Logs use operation ID, source ID, logical path, phase, and exception type; physical paths are excluded from normal logging and must be redacted in any narrowly enabled diagnostics.

## Recovery and cleanup

- Submitted plans and jobs are atomically persisted before queue acknowledgement.
- Job phase transitions are monotonic and schema-validated.
- Queued jobs survive a process restart and re-enter FIFO order after revalidation.
- A job left `running`, `validating`, or `cancelling` at startup becomes `interrupted`.
- Startup cleanup reads only valid operation-owned journals and removes only the exact staging/quarantine paths recorded for interrupted jobs.
- Unknown, malformed, symlinked, reparse-point, or out-of-scope recovery paths are quarantined logically and require operator inspection; they are never recursively removed.
- Current-item quarantine restoration is attempted before an interrupted job is finalized.
- Staging cleanup failure is reported but does not trigger broad directory deletion.
- Completed/failed/cancelled/interrupted history retains the newest 100 records; older operation metadata and already-empty owned staging entries may be removed through strict allowlists.
- Valid Trash manifests and items are outside operation-history cleanup and are never auto-expired.

## UI design

### Function-key command bar

- **F5 Copy:** enabled for a valid filesystem selection/focus and a writable opposite filesystem destination. When the captured context is an eligible archive extraction, the existing Extract behavior and label take precedence.
- **F6 Move:** enabled only when source and opposite destination are available/writable filesystems.
- **F7 MkDir:** enabled when the active filesystem source is available/writable.
- **F8 Delete:** enabled for a valid selection/focus on an available/writable filesystem source.

Disabled commands expose a specific accessible reason instead of the old generic future-milestone description.

### Copy/Move confirmation

The modal shows operation type, immutable selected-item summary, count, known total size, source, editable destination logical path, and preview state. Conflicts show source/destination type and logical name without physical paths.

The choices are Overwrite, Skip, Create Unique Name, and Cancel. An **Apply to remaining conflicts** control becomes available only when more unresolved conflicts exist. Submit is disabled until destination validation and all conflict decisions succeed.

### Transfer progress

After submission, progress opens as a modal dialog that blocks the commander UI. It shows current item, file/directory count, bytes, percentage, speed, elapsed time, estimated remaining time, queue position, warnings, **Cancel**, and **Background**.

Background closes the blocking layer and creates a compact top-toolbar task item containing Copy/Move icon and label, percentage or indeterminate state, and queued count. Clicking it restores the full dialog. Cancel remains available from the restored dialog. The compact item uses an accessible progress label and live region without announcing every byte update.

Completed, cancelled, interrupted, and failed operations stay in the task surface until acknowledged. Success refreshes source and destination panels. Partial outcomes refresh all affected panels and show a result summary.

### Delete and Trash

The Delete modal lists a bounded set of selected names plus total count. Move to Trash is the default when capability is available. If it is unavailable, the dialog explains why and presents only permanent deletion with the unrecoverable message.

Selecting **Permanent delete** always reveals the unrecoverable message before confirmation. The checkbox and final confirmation are separate actions.

The top-toolbar Trash button opens the Trash dialog. Restore and permanent deletion support multi-selection. Empty Trash labels whether it affects one filtered source or all sources.

### Create Directory and focus

Create Directory focuses its name field and supports Enter/Escape. Every modal traps focus, names its title/description, returns focus to the invoking command, and prevents commander shortcuts from leaking through. Backgrounding transfer progress returns focus to the active panel. Restoring it returns focus inside the dialog.

All controls support the regular and Norton Commander themes, keyboard-only navigation, narrow PWA layouts, reduced motion, high contrast, and touch activation.

## Client state

A focused `FileOperationStore` owns:

- captured Copy/Move dialog context;
- preview request sequencing and stale-response rejection;
- conflict decisions;
- queue/status polling;
- modal versus background presentation;
- cancellation and acknowledgement; and
- completion refresh callbacks.

A focused `TrashStore` owns Trash capability, listing/filtering, restore preview, permanent deletion, and Empty Trash state. Create Directory may use a small component-local form calling the API port.

Stores register with the existing protected-state reset service. Logout clears browser state and stops polling but does not cancel server jobs. After login or browser refresh, the operation store queries active/recent jobs and restores the compact task surface. No physical or absolute host path is placed in local storage.

## Interaction with existing features

- Uploads, batch rename, archive extraction, and new operations use the same `DirectoryMutationLock`; overlapping directory mutations wait safely while disjoint work may proceed where existing services permit.
- The file-operation FIFO serializes Copy, Move, and long permanent deletion jobs with each other. It does not replace the lock or independently queue existing feature types.
- F5 archive extraction has priority when the captured context contains an eligible archive. Otherwise F5 is Copy.
- Multi-Rename continues to operate on the active filesystem selection.
- Panel filters, tabs, sort order, and active-panel identity do not change after mutation refresh.
- The hidden Trash/internal paths do not appear in search or selection results.

## Testing strategy

### Backend unit tests

Planner tests cover:

- single focus and ordered multi-selection;
- duplicate/nested selection normalization;
- opposite destination capture;
- RO/RW source policy;
- invalid and reserved names;
- same/descendant destination rejection;
- symbolic-link/reparse rejection;
- stable fingerprints and plan expiry;
- file and directory conflicts;
- directory merge semantics;
- deterministic unique naming; and
- Trash capability/fallback decisions.

Executor tests cover:

- staged file copy and atomic commit;
- overwrite quarantine restore on current-item failure;
- completed overwrite behavior;
- directory merge preserving destination-only content;
- atomic same-filesystem Move;
- cross-filesystem copy-then-delete;
- `move_source_not_removed` outcome;
- progress monotonicity;
- queued and running cancellation;
- partial outcome accounting;
- stale execution revalidation;
- mutation-lock acquisition/release; and
- interrupted-operation cleanup allowlists.

Trash tests cover:

- strict manifest serialization;
- atomic and staged Trash moves;
- no silent permanent fallback;
- listing with invalid-manifest isolation;
- Restore with all conflict choices;
- missing-parent preview;
- permanent item deletion;
- Empty Trash scope; and
- absence of automatic retention cleanup.

### API integration tests

Integration tests use temporary sources and authentication helpers. They cover authentication/antiforgery, logical-only payloads, read-only sources, unavailable sources, stale/malformed plans, queue order, status/cancel/acknowledge, directory creation, Trash lifecycle, permanent confirmation, malicious traversal, symlinks, sanitized Problem Details, and absence of physical paths in responses/logs.

### Angular unit tests

Tests cover function-key/button enablement, F5 Extract priority, immutable context capture, editable destination, preview sequencing, all conflict decisions, apply-to-remaining behavior, modal/background transitions, queue display, polling after refresh/login, cancellation, acknowledgement, Delete warnings, Trash filtering/actions, Create Directory validation, focus restoration, accessible announcements, and both themes.

### Browser acceptance tests

Playwright covers:

1. multi-file Copy into the opposite panel;
2. Move between sources;
3. merge Overwrite preserving destination-only content;
4. Skip and Create Unique Name;
5. large-transfer modal, Background, compact progress, restore, and Cancel;
6. FIFO display for two queued transfers;
7. browser refresh during a background operation;
8. Create Directory through F7;
9. default F8 Move to Trash;
10. Trash Restore with conflict handling;
11. permanent Delete warning;
12. Empty Trash confirmation/scope;
13. RO/archive disabled states; and
14. unchanged source canaries outside selected trees.

### Cross-platform coverage

Filesystem unit/integration suites run on Windows and Ubuntu CI. They use logical assertions that tolerate platform case/name differences where required, while keeping the security rules identical. Linux container smoke validates bind-mounted runtime behavior. Hosted Windows CI does not claim Docker Desktop end-to-end coverage; the existing manual Windows release checklist adds Copy/Move/Trash smoke cases.

## Operational and deployment impact

- No database or external service is added.
- The existing writable `/data` bind mount gains `/data/file-operations` for bounded job metadata.
- Each writable configured source may gain `.reachcommander-trash` only after the first confirmed Trash operation and successful capability validation.
- Source documentation explains the reserved internal namespace and the storage consumed by Trash.
- Backups should include application state plus any source-local Trash the operator wants to preserve.
- Uninstall never deletes source-local Trash because sources remain outside installer-owned removal allowlists.
- The application does not claim native host Recycle Bin/Trash integration.

## Risks and mitigations

- **Large recursive preview can be slow:** show a planning state, observe cancellation, enumerate iteratively, and enforce bounded API request item counts while allowing directory recursion server-side.
- **Destination changes after preview:** fingerprint and revalidate immediately before execution; fail stale rather than applying an unapproved conflict choice.
- **Cross-filesystem Move can duplicate on source-delete failure:** preserve the committed copy, keep the original, report the exact logical outcome, and never claim full Move success.
- **Overwrite can destroy prior destination data:** quarantine the current item until its replacement commits and require explicit conflict approval.
- **Trash consumes source capacity indefinitely:** show Trash size where calculable and provide explicit Empty Trash; never auto-delete.
- **Reserved namespace collision:** fail capability safely and never adopt or remove unknown existing content.
- **Browser disconnect:** keep server queue/status durable and restore through polling after authentication resumes.
- **Server interruption:** mark active jobs interrupted, clean only journaled owned staging artifacts, and never resume an uncertain partial file.
- **Concurrent existing mutations:** reuse `DirectoryMutationLock` for source and destination directory trees.
- **Physical path disclosure:** accept/store logical paths, map expected exceptions, sanitize logs, and test every error response.

## Acceptance criteria

1. F5 copies selected/focused filesystem entries to the opposite panel unless eligible archive extraction takes precedence.
2. F6 moves selected/focused entries to the opposite panel.
3. The destination is editable and revalidated as a logical path.
4. Copy/Move conflicts offer Overwrite, Skip, Create Unique Name, and Cancel with apply-to-remaining.
5. Directory Overwrite merges and preserves destination-only content.
6. Exactly one Copy/Move/long permanent-deletion job executes at a time; later jobs remain queued FIFO.
7. Transfer progress begins in a blocking modal, can background to the top toolbar, and can be restored.
8. Browser refresh/reconnection restores active and queued operation visibility.
9. Cancellation removes only current owned staging, restores current quarantine when needed, and reports completed items accurately.
10. A Move source is removed only after its destination commits.
11. F7 creates exactly one validated directory in the active writable filesystem directory.
12. F8 always displays a confirmation and defaults to ReachCommander-managed Trash when available.
13. Permanent deletion displays the exact unrecoverable warning and requires explicit confirmation.
14. Trash supports filtered listing, multi-select Restore, permanent Delete, and scoped Empty Trash.
15. Trash items never expire automatically.
16. Managed Trash unavailability never silently becomes permanent deletion.
17. RO sources and archive paths cannot be mutated.
18. Symbolic links, reparse points, traversal, source roots, reserved paths, duplicate/nested selections, and self-descendant destinations fail safely.
19. Operation API responses, browser state, and public logs never expose physical paths.
20. Existing uploads, batch rename, archive extraction, authentication, PWA, regular theme, and Norton theme remain functional.
21. Backend, Angular, integration, browser, Windows, Ubuntu, and container-smoke gates pass.
22. Source canaries outside selected trees remain byte-identical across success, conflict, cancellation, failure, recovery, Trash, Restore, Empty Trash, and permanent deletion tests.
