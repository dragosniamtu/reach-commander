# ReachCommander Active-Panel Toolbar Design

**Date:** 2026-08-20  
**Status:** Approved for implementation planning  
**Scope:** Active-panel toolbar, source access indicators, wildcard search, secure multi-file upload, and the Multi-Rename entry point

## Summary

ReachCommander will add a single contextual toolbar to the left side of the application top bar, opposite hardware monitoring. The toolbar always targets the active file panel and exposes three primary tools: Multi-Rename, Add files, and a direct wildcard search field. Source buttons will also show explicit read-only, writable, and unavailable states through icons plus text.

The toolbar reuses the active-panel state already owned by `CommanderStore`. Wildcard search remains client-side and limited to the loaded current directory. Multi-Rename implements the separately approved server-authoritative design in `docs/superpowers/specs/2026-08-19-reachcommander-multi-rename-design.md`. Upload is a new streamed, bounded, all-or-nothing write slice that is enabled only for an explicitly writable source.

## Goals

- Put the most common active-panel actions in one discoverable top toolbar.
- Make the destination/target panel unambiguous before a write operation begins.
- Support direct case-insensitive wildcard filtering such as `*.exe`, `report-??.pdf`, and `photo*`.
- Add multiple files to the active panel's current directory without buffering complete files in application memory.
- Reject an entire upload batch if any filename conflicts or any item is invalid.
- Preserve the existing explicit `readOnly` source policy and hardened read-only Docker defaults.
- Show source write policy through accessible lock/unlock icons plus `RO`/`RW` text.
- Reuse the approved Multi-Rename preview, execute, compensation, and one-level Undo contract.

## Non-Goals

- Recursive search, indexed search, full-text content search, or backend directory scans.
- Raw regular-expression search. The accepted syntax is wildcard/glob only.
- Folder upload, drag-and-drop upload, clipboard paste, remote URL import, or archive extraction.
- Overwrite, replace, auto-rename, merge, or skip-conflict upload modes.
- Resumable/chunk-retry upload, background upload jobs, persistent transfer history, or SignalR progress.
- Uploading to a panel other than the destination captured when the upload review opens.
- Making the checked-in sample sources or Docker bind mounts writable.
- Authentication or per-user authorization; ReachCommander retains its existing trusted-network boundary.

## Top-Bar Interaction

### Desktop layout

The top bar contains three horizontal regions:

1. ReachCommander brand.
2. Left-aligned active-panel toolbar.
3. Existing right-aligned status/actions and hardware monitoring.

The toolbar order is fixed:

```text
[LEFT · Media] [Rename icon  Multi-Rename] [Upload icon  Add files] [Search icon  *.exe                 ×]
```

The context chip shows the active side and source name. Its tooltip and accessible label also include the active logical directory. Changing the active panel immediately changes the toolbar context; an operation that has already opened retains an immutable source/directory snapshot so later panel changes cannot redirect it.

Multi-Rename and Add files are enabled only when the active source is available and has `isReadOnly: false`. Multi-Rename additionally requires at least one selected eligible entry or a non-parent cursor entry. Disabled controls remain focusable through an explanatory wrapper/tooltip pattern so users can learn why the action is unavailable.

### Responsive behavior

- Wide layouts show the side, source name, button icons, and button labels.
- Medium layouts reduce the context to `L` or `R`, hide action labels, and keep accessible names/tooltips.
- Narrow layouts hide the brand subtitle, allow the search field to shrink to a usable minimum, and use the hardware widget's compact state.
- At the smallest supported viewport, the brand wordmark may hide while its mark and accessible application name remain; the toolbar actions and search input stay operable without horizontal page overflow.

All controls use visible keyboard focus and at least a 28-pixel desktop hit target consistent with the existing dense commander interface. Pointer, touch, and keyboard access remain equivalent.

## Source Access Indicators

Every source selector button shows one policy indicator:

- lock icon plus `RO` for a read-only source;
- unlocked/editable icon plus `RW` for a writable source;
- warning icon for an unavailable source, while retaining its `RO` or `RW` policy in the accessible description.

Icons use one consistent local outline SVG style and `currentColor`; no icon font or runtime icon dependency is added. Visible text, `title`, and `aria-label` descriptions ensure the policy is never communicated by icon or color alone.

`isReadOnly: false` is application policy, not proof that the operating system permits writes. Upload and Multi-Rename revalidate actual filesystem access on the server and surface a safe write-denied error when host permissions or container mounts remain read-only.

## Wildcard Search

### Matching semantics

Search replaces the duplicate quick-filter field currently rendered inside each panel. The existing `PanelState.filter` remains the source of truth, so the left and right panels continue to preserve independent search values across panel switches and browser persistence.

- Empty input shows every loaded entry.
- Input containing neither `*` nor `?` retains the current case-insensitive substring behavior.
- `*` matches zero or more characters.
- `?` matches exactly one character.
- A pattern containing a wildcard is matched against the complete entry name, including its extension.
- Matching is culture-invariant and case-insensitive.
- Every non-wildcard character is treated literally; regex metacharacters have no special meaning.
- Files and directories are matched by name. The synthetic parent row remains visible so navigation is never trapped by a filter.
- Search never traverses descendants and never sends a request to a new search endpoint.

Examples:

| Input | Result |
|---|---|
| `report` | Names containing `report` |
| `*.exe` | Complete names ending in `.exe` |
| `report-??.pdf` | `report-` plus exactly two characters and `.pdf` |
| `photo*` | Complete names beginning with `photo` |

The search field uses `type="search"`, an explicit `Search active panel` label, a clear button, and helper text/tooltip that says `Wildcards: * any characters, ? one character`. `Ctrl+F` prevents the browser find action only while ReachCommander has application focus and moves focus to this field. Existing type-to-filter and Escape-to-clear behavior update the same value and remain available.

## Multi-Rename Integration

Multi-Rename implements the complete approved design in `2026-08-19-reachcommander-multi-rename-design.md`; this specification changes only its discoverable entry point and toolbar context.

- The toolbar button and `Ctrl+M` target the active panel.
- Selected entries are captured in visible table order; when there is no selection, the non-parent cursor entry is used.
- Read-only, unavailable, empty-selection, and invalid-cursor states show an explanation without opening an executable plan.
- The server remains authoritative for preview rules, path policy, conflicts, two-phase execution, compensation, and Undo.
- After Execute or Undo, only the originating pane refreshes.
- Multi-Rename and Upload share the same per-source/per-directory mutation lock so their conflict checks cannot race each other inside one API process.

## Upload Review Flow

1. The user activates Add files for a writable, available active source.
2. A native picker accepts multiple files. Folder selection is not enabled.
3. A focused review panel opens and displays the captured side, source, logical directory, files, individual sizes, total size, and configured limits.
4. The user may remove entries or cancel. The primary `Add files` action starts the batch.
5. The review becomes a progress view. Closing is blocked while the request is actively finalizing; cancellation remains available while bytes are streaming.
6. Success refreshes only the captured destination panel, preserves its search and selection state, announces the result, and restores focus to the toolbar Add files control.
7. A validation or conflict failure leaves the review list visible with a safe explanation so the user can remove or rename files locally and retry.

Switching the active panel while the review is open never changes the captured destination. A new upload cannot start from the same toolbar while its batch is pending.

## Upload HTTP Contract

### Endpoint

```text
POST /api/uploads?sourceId={sourceId}&path={logicalDirectoryPath}
Content-Type: multipart/form-data
```

Each file is a multipart section named `files`. `sourceId` and `path` are logical values only. The response never contains a configured physical root, resolved physical path, staging name, exception text, or host identifier.

A successful response uses HTTP 201 and contains:

```json
{
  "uploadedCount": 2,
  "totalBytes": 1536,
  "files": [
    { "name": "one.txt", "relativePath": "/Movies/one.txt", "size": 512 },
    { "name": "two.bin", "relativePath": "/Movies/two.bin", "size": 1024 }
  ]
}
```

The browser treats the response as confirmation and refreshes the destination directory from the normal file-list endpoint.

### Configurable limits

The server adds validated `Uploads` options with these defaults:

```json
{
  "Uploads": {
    "MaxFileBytes": 10737418240,
    "MaxBatchBytes": 53687091200,
    "MaxFilesPerBatch": 100,
    "MaxConcurrentBatches": 2
  }
}
```

These are 10 GiB per file, 50 GiB per batch, 100 files, and two concurrent batches. Values are adjustable through normal ASP.NET Core configuration and must be positive, internally consistent, and bounded to supported numeric ranges. The request pipeline enforces a multipart body ceiling that includes reasonable framing overhead in addition to `MaxBatchBytes`; the streaming service independently enforces actual per-file and aggregate bytes and never trusts `Content-Length` alone.

## Upload Validation and Security

### Boundary validation

Before and during staging, the server enforces:

- the source exists, is enabled, available, and configured with `readOnly: false`;
- the logical directory resolves canonically beneath that source;
- the destination directory is not a symbolic-link escape and remains the captured directory;
- the batch contains 1–100 files and stays within configured byte limits;
- every multipart file section has a non-empty single-component filename;
- filenames contain no path separators, rooted/drive/UNC syntax, NUL, dot segments, trailing dot/space, invalid portable characters, or reserved Windows device name;
- filenames stay within the supported component-length limit;
- names are unique within the batch under `OrdinalIgnoreCase` comparison;
- no destination name already exists under conservative case-insensitive comparison;
- sufficient source capacity is checked when the platform reports it, while streamed byte limits remain authoritative;
- arbitrary file extensions and MIME types are accepted intentionally because ReachCommander stores files but never executes or serves them as application assets.

Caller-provided filenames are preserved after validation rather than silently sanitized or changed. Client MIME values are recorded nowhere and are not trusted as content proof.

### Streaming and all-or-nothing finalization

The API parses multipart sections as streams. Each accepted file is written with create-new semantics to an unpredictable reserved staging name inside the destination directory. Staging inside the destination keeps final moves on the same filesystem. Files are opened without execute permissions on platforms where the filesystem API exposes that distinction.

All filenames and existing destinations are revalidated under a shared directory mutation lock before finalization. Only after every section is completely staged and flushed does the service rename staged files to their final names with overwrite disabled.

For handled validation, cancellation, storage, or finalization failures, the service deletes staged files and reverses any final moves completed by the current batch. Because filesystem calls are not a real multi-file transaction, an abrupt process/host/storage failure may leave reserved staging files. Such files never use the requested final names; structured logs record the batch correlation identifier and logical directory for administrator recovery without logging physical paths or file contents.

Uploads are limited globally by `MaxConcurrentBatches` and serialized with Multi-Rename per source/logical directory. The endpoint shares the application's current trusted-network security boundary. It must not be exposed publicly without the authenticated HTTPS reverse proxy already required by ReachCommander's deployment guidance.

## Upload Errors

Global failures use `application/problem+json` with stable codes:

- `source_not_found`
- `source_unavailable`
- `source_read_only`
- `path_forbidden`
- `upload_empty`
- `upload_file_too_large`
- `upload_batch_too_large`
- `upload_too_many_files`
- `upload_name_invalid`
- `upload_name_conflict`
- `upload_storage_unavailable`
- `upload_cancelled`
- `upload_cleanup_required`

Conflict responses identify only the safe logical filename(s). A batch with any invalid or conflicting item produces no requested final filename. Unexpected errors use the existing exception-to-Problem-Details boundary and never expose stack traces or physical paths.

## Frontend Boundaries

### `ActivePanelToolbarComponent`

Consumes active side, source, logical path, filter, selection eligibility, and pending action state. Emits focused intents: open Multi-Rename, choose files, update search, and focus search. It does not make HTTP requests.

### `UploadStore`

Owns the immutable destination context, selected browser `File` objects, review/progress state, cancellation, stale-response protection, API result/error mapping, and refresh callback. It remains separate from `CommanderStore` so large `File` objects and transient write state are never persisted with panel navigation state.

### `CommanderApiPort`

Adds a typed upload method that emits progress and returns the safe result DTO. Production uses Angular `HttpClient` progress events; tests use the existing API-port substitution pattern. Multi-Rename adds its separately specified typed preview/execute/undo methods.

### Focus and announcements

The upload review uses Angular CDK focus trapping, Escape when safe, visible focus, and opener restoration. Destination, limits, validation failures, progress, completion, and cancellation use concise live-region announcements. Multi-Rename retains the same accessibility contract from its approved specification.

## Testing Strategy

### Pure frontend tests

- Plain substring matching remains compatible with existing behavior.
- `*` and `?` wildcard conversion, complete-name anchoring, literal regex metacharacters, Unicode, empty patterns, directories, and parent-row preservation.
- Independent left/right search state, panel switching, persistence, type-to-filter, clear, Escape, and `Ctrl+F` focus.
- Toolbar context, action eligibility, responsive labels, tooltips, and focus behavior.
- Source buttons render lock + `RO`, unlock + `RW`, and unavailable warning semantics.

### Upload backend unit tests

- Options validation for each limit and inconsistent totals.
- Portable filename policy, batch duplicates, reserved names, traversal/path-bearing names, and conservative case collisions.
- Per-file/count/aggregate streaming limits, zero-byte files, cancellation, short reads, and dishonest/missing `Content-Length`.
- Read-only, unavailable, missing, escaped, and symlinked directories.
- Existing destination conflicts reject the entire batch.
- Injected staging/finalization failures delete staged files and compensate completed final moves.
- Injected compensation failure returns `upload_cleanup_required` with logical details only.
- Shared directory locking prevents an upload/rename race.

### HTTP integration tests

- Multipart success returns HTTP 201 and the exact safe DTO shape.
- Empty, oversized, excessive-count, conflicting, malformed, read-only, unavailable, and forbidden requests map to stable Problem Details.
- No response or error contains configured roots, staging names, physical paths, multipart temporary paths, or exception details.
- A failed batch leaves every pre-existing fixture unchanged and creates no requested final filename.
- Tests use temporary writable/read-only fixture roots and never developer data.

### Multi-Rename tests

The complete unit, filesystem, HTTP, Angular, and Playwright matrix in `2026-08-19-reachcommander-multi-rename-design.md` remains required.

### Browser acceptance

Playwright covers these user flows:

1. Activate the left panel, enter `*.exe`, and verify only matching current-directory entries plus the parent row remain.
2. Switch right and verify its independent search; return left and confirm `*.exe` is restored.
3. Upload multiple non-conflicting files to a writable source, observe destination/progress, and verify the refreshed pane.
4. Attempt a batch containing one existing name and verify the entire batch is rejected with no new final files.
5. Verify Add files and Multi-Rename explain their disabled state on a read-only source.
6. Open Multi-Rename from the toolbar and complete its approved preview/execute/undo scenario.
7. Verify the toolbar and source policy indicators at desktop and narrow viewports with keyboard-only operation.

## Documentation and Deployment

README changes document wildcard syntax, `Ctrl+F`, `Ctrl+M`, upload limits, upload conflict behavior, writable-source opt-in, and the difference between application policy and host/container filesystem permissions. A writable example omits `:ro` only for a specifically named source. The checked-in `config/sources.json`, `compose.yaml`, and hardware telemetry overrides remain read-only.

No host agent, Docker socket, privileged container mode, additional Linux capability, vendor command, or hardware control is introduced.

## Acceptance Criteria

- A left-aligned top toolbar appears on the same top-bar level as hardware monitoring.
- The toolbar clearly names and always targets the active panel.
- Multi-Rename opens from the toolbar or `Ctrl+M` using selection-order/cursor-fallback semantics.
- Add files reviews and streams multiple files into the captured active directory.
- Any invalid, conflicting, oversized, or excessive item blocks the complete upload batch.
- Handled upload failures leave no staged file or requested final filename behind; cleanup failures are explicit.
- Uploaded content is never buffered as a complete batch in memory and is never executed by ReachCommander.
- Search supports `*` and `?`, retains plain substring compatibility, stays non-recursive, and preserves independent panel state.
- `Ctrl+F` focuses active-panel search and Escape clears it through existing command priority.
- Every source shows accessible `RO`, `RW`, or unavailable semantics.
- Read-only application policy is re-enforced at every write boundary, and actual OS write failures fail safely.
- Physical paths, staging names, file contents, and exception details never reach browser responses or client logs.
- Existing hardware monitoring remains at the far right and the toolbar does not obscure it at supported viewports.
- Backend, Angular, integration, Playwright, production build/publish, repository-hygiene, and available Docker checks pass before completion is reported.
