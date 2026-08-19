# ReachCommander Multi-Rename Tool Design

**Date:** 2026-08-19  
**Status:** Approved for implementation planning  
**Scope:** Milestone 2A — safe multi-item rename

## Summary

ReachCommander will add a Total Commander-inspired Multi-Rename Tool for renaming selected files and folders in one active directory. `Ctrl+M` opens a dense modal workspace with separate name and extension masks, token helpers, search/replace, regular expressions, case conversion, counter configuration, and a server-authoritative live preview of every resulting filename.

The operation is intentionally conservative. Every preview row must be valid and conflict-free before Start is enabled. Execution revalidates the preview against the filesystem, performs a two-phase rename that supports swaps, cycles, and case-only changes, and compensates completed steps if a runtime failure occurs. One-level safe Undo is available while the tool remains open and only while all renamed entries are unchanged.

## Goals

- Provide the core Multi-Rename workflow familiar to Total Commander users.
- Show the complete proposed new filename for every selected entry before mutation.
- Keep the server authoritative for rule evaluation, path validation, conflict detection, execution, and undo.
- Support selected files and folders together without recursively renaming folder contents.
- Support case-only renames, swaps, and multi-entry rename cycles.
- Enforce source availability, source read-only policy, canonical path confinement, and no-overwrite behavior.
- Prevent a known invalid row from producing a partial batch.
- Offer one-level, revalidated, idempotent Undo for the most recently completed batch in the open tool.
- Preserve the existing single-origin ASP.NET Core and Angular modular-monolith architecture.

## Non-Goals

- F4 single-item rename; F4 remains reserved for a later Milestone 2 slice.
- Recursive traversal or renaming descendants inside selected directories.
- Cross-directory moves or changing an entry's parent directory.
- Overwriting or deleting existing destination entries.
- Symbolic-link rename.
- Date/time tokens, plugin tokens, saved presets, or manual per-row name overrides.
- Persistent rename history or Undo after server restart.
- Background jobs, SignalR progress, or transfer-queue integration.
- Authentication and per-user ownership of plans; those remain Milestone 4 concerns.

## Entry Point and Selection

- `Ctrl+M` is the dedicated Multi-Rename shortcut, matching Total Commander.
- The command targets the active panel and its active directory tab.
- If the panel has selected entries, the tool receives those entries in their current visible table order.
- If the panel has no selection, the current non-parent cursor entry is used.
- If neither a selection nor a valid cursor entry exists, the command reports that there is nothing to rename.
- The synthetic parent row is never eligible.
- Files and directories may be mixed. Every entry must be a direct child of the same active logical directory.
- A source that is unavailable or configured with `readOnly: true` opens an explanatory disabled state and cannot create a preview plan.

## Architecture

### Backend boundaries

The application layer adds `IBatchRenameService` with focused preview, execute, and undo operations. It owns the behavior contract but does not access physical paths directly.

Infrastructure implements:

- mask parsing and deterministic rule evaluation;
- source and path-policy enforcement through `ISourceCatalog` and `IPathSecurityService`;
- filesystem inspection and entry fingerprinting;
- destination and collision validation;
- per-directory execution serialization;
- two-phase rename and reverse-order compensation;
- bounded short-lived preview and operation-result storage;
- revalidated one-level undo.

Controllers map request/response DTOs explicitly. Physical roots, canonical physical paths, temporary physical names, and exception details never enter browser-facing DTOs or Problem Details.

### Frontend boundaries

Angular adds a focused `MultiRenameStore` rather than growing `CommanderStore`. It owns:

- dialog open/closed state;
- the immutable source/directory/selection snapshot used to request previews;
- editable rename rules;
- debounced preview requests and stale-response suppression;
- execute/undo pending and result states;
- enabling and disabling Start and Undo.

The `multi-rename-dialog` is composed from focused controls for masks, search/replace, casing, counter configuration, the preview table, result summary, and action footer. It consumes typed methods added to `CommanderApiPort`.

### Plan and operation lifetime

- Preview plans are stored in a bounded in-memory cache for 10 minutes.
- Plans use cryptographically random opaque identifiers.
- A plan records logical source/destination names, entry fingerprints, rule inputs, and validation results.
- An entry fingerprint contains the logical path, entry type, last-write timestamp, attributes, and file length when applicable. It is a stale-entry guard, not a content hash.
- Execute consumes the plan for mutation but retains its final result so a network retry returns the same result instead of executing twice.
- A successful operation retains the one-level undo mapping while the tool remains open, subject to a bounded expiration.
- Restarting the API invalidates preview plans and undo records.
- Plan identifiers are correlation identifiers, not an authorization boundary.

## HTTP API

### Preview

`POST /api/batch-renames/preview`

The request contains only logical data:

```json
{
  "sourceId": "media",
  "directoryPath": "/Movies",
  "entryPaths": [
    "/Movies/holiday-photo.jpg",
    "/Movies/holiday-video.mp4"
  ],
  "rules": {
    "nameMask": "Trip-[C]",
    "extensionMask": "[E]",
    "searchFor": "holiday",
    "replaceWith": "vacation",
    "useRegex": false,
    "matchCase": true,
    "replaceInExtension": false,
    "caseMode": "unchanged",
    "counterStart": 1,
    "counterStep": 1,
    "counterDigits": 3
  }
}
```

The response contains:

- `planId` and `expiresAt`;
- `canExecute`, `changedCount`, `unchangedCount`, and `invalidCount`;
- rows in authoritative execution order;
- each row's old name, old extension, complete proposed new name, logical source path, file type, size, modified timestamp, status, and safe explanatory message.

The response never contains a physical location. The UI derives the displayed logical location from the already-known source name and directory path.

### Execute

`POST /api/batch-renames/{planId}/execute`

Execute accepts no replacement paths or revised rules. The server revalidates and runs exactly the named plan. The response contains:

- `operationId`;
- per-row old and new logical names and final result;
- overall status;
- whether compensation was needed;
- whether manual recovery is required;
- whether Undo is currently available.

Repeating Execute with the same completed `planId` returns the stored result. It never applies the rename twice.

### Undo

`POST /api/batch-renames/{operationId}/undo`

Undo accepts no caller-supplied paths. It revalidates the stored reverse mapping and current entry fingerprints. The response contains per-row results and the overall undo status. Repeating a completed Undo returns the same result.

### Global error codes

Global request failures use `application/problem+json` with stable codes:

- `invalid_rename_rule`
- `batch_too_large`
- `source_not_found`
- `source_unavailable`
- `source_read_only`
- `path_forbidden`
- `rename_plan_not_found`
- `rename_plan_expired`
- `rename_plan_stale`
- `rename_recovery_required`

Row-specific invalid names and conflicts normally return a successful preview with `canExecute: false`, allowing the browser to display the complete proposed result set. Malformed requests and source/path policy failures use Problem Details.

## Rule Language

### Name and extension model

- The default name mask is `[N]`.
- The default extension mask is `[E]`.
- For a regular file, `[N]` is the name without its final extension and `[E]` is the extension without the dot.
- For a directory or extensionless file, `[N]` is the complete name and `[E]` is empty.
- A leading-dot filename with no other dot, such as `.env`, is treated as an extensionless name.
- When the final extension is empty, no trailing dot is emitted.

### Tokens

- `[N]` — original name segment.
- `[E]` — original extension segment.
- `[C]` — configured counter value.
- `[N1-5]` and `[E1-3]` — one-based inclusive character ranges.
- `[N3-]` and `[E2-]` — one-based range from the start index through the end.

Ranges clamp their end to the available segment length. A start beyond the segment produces an empty expansion. Zero, negative, reversed, malformed, or unknown token ranges make the rule invalid. Counter padding is a minimum width and never truncates a larger value.

Date/time tokens, plugin tokens, and arbitrary expression evaluation are excluded from this version.

### Search and replace

- An empty `searchFor` disables replacement.
- Plain mode replaces every non-overlapping occurrence in the generated name segment.
- `matchCase` controls culture-invariant case-sensitive or case-insensitive matching in both plain and regex modes; it defaults to true.
- `replaceInExtension: true` applies the same replacement independently to the generated extension segment; it defaults to false.
- Regex mode uses the server's bounded .NET regular-expression engine with a short match timeout.
- Regex replacement supports .NET capture references such as `$1`.
- Invalid patterns, replacement syntax, or regex timeouts make the preview invalid; they never reach execution.

### Case conversion

One case mode is applied independently to the generated name and extension after replacement:

- `unchanged`
- `lowercase`
- `uppercase`
- `capitalizeWords`
- `sentenceCase`

Culture-invariant casing is used so preview and execution remain deterministic across servers.

### Transformation order

Every row uses the same visible order:

1. Parse and expand name and extension masks.
2. Apply plain or regex replacement to the name and optionally the extension.
3. Apply case conversion.
4. Join non-empty name and extension segments.
5. Validate the complete destination name and batch-wide collisions.

The counter is assigned from zero-based preview-row position using:

```text
counterStart + (rowIndex × counterStep)
```

The visible table order captured when `Ctrl+M` opens therefore controls numbering.

## Multi-Rename Workspace

The workspace is a large modal dialog designed for desktop productivity. It uses the supplied Total Commander screenshot as interaction inspiration without copying its native-window chrome.

### Layout

- Header: `Multi-Rename Tool`, source name, logical directory, item count, and Close.
- First control group: name mask input and token insertion buttons.
- Second control group: extension mask input and token insertion buttons.
- Third control group: search, replacement, regex, include-extension, and case mode.
- Fourth control group: counter start, step, and digits.
- Main area: a dense, independently scrollable preview table.
- Sticky footer: preview/validation summary, Undo, Start, and Close.

The initial focus is the name mask. The modal traps focus, restores focus to the originating panel when closed, supports Escape when no execute/undo action is pending, and announces preview and result summaries through an appropriate live region.

### New-filename preview

The preview table always includes:

- Old name
- Ext
- New name
- Size
- Modified
- Status

`New name` displays the complete proposed filename, including its final extension, before any mutation occurs. Changed portions receive a restrained visual highlight. Truncated names expose their full value through accessible text and a tooltip. Status values distinguish Ready, Unchanged, Invalid, Conflict, Stale, Completed, Failed, Rolled back, and Recovery required.

Rule edits debounce server previews. Each request receives a monotonically increasing client token; a late response cannot replace a newer preview. Start is disabled while a preview is pending, when any row is invalid/conflicted/stale, when the plan has expired, or when every row is unchanged.

### Commands

- `Ctrl+M` — open Multi-Rename from the active pane.
- `Ctrl+Enter` — Start when the authoritative preview is executable.
- `Escape` — close when safe, otherwise remain open during an active request.
- `Enter` in a single-line rule field does not execute accidentally.
- Pointer/touch access is available for every control.

F4 remains reserved for ordinary single rename. F2 presets and saved settings are not included.

## Validation and Security

### Source policy

- The source must exist, be enabled, be available, and have `readOnly: false` at preview, execute, and undo time.
- A writable source also requires operating-system write permissions and a writable container bind mount.
- The checked-in Docker sample remains read-only by default.

### Path policy

- Every caller-supplied source path is resolved through `IPathSecurityService`.
- Every entry must be a single direct child of the declared logical directory.
- Every destination is constructed by the server from the validated parent and a validated single filename; callers never supply destination paths.
- Parent changes, separators, rooted names, dot segments, NUL characters, and traversal are rejected.
- Symbolic-link entries are rejected in this version.
- Canonical containment is checked again before execution and undo.

### Filename policy

Validation rejects:

- empty final names;
- `.` and `..`;
- directory separators and NUL characters;
- platform-invalid filename characters;
- Windows reserved device names regardless of host platform;
- trailing dots or spaces;
- names that exceed the supported component-length limit;
- case-insensitive duplicates within the proposed batch;
- destinations already occupied by an entry outside the same rename plan.

Destination comparisons are conservatively case-insensitive on every platform. A case-only rename of one entry is allowed, but two distinct entries cannot end at names that differ only by case.

### Resource bounds

- A batch contains at most 5,000 entries.
- Rule and filename fields have explicit length limits.
- Regex evaluation has a short per-match timeout.
- Preview plans and operation records have count and time bounds.
- Execute and Undo are serialized by source and logical directory through an asynchronous keyed lock.

## Execute Algorithm

Before mutation, Execute acquires the source/directory lock and revalidates:

- plan lifetime and final preview validity;
- source availability and writable policy;
- source entry presence, canonical containment, type, and fingerprint;
- destination freedom and batch-wide uniqueness;
- temporary-name freedom.

Execution then uses two phases in the same directory:

1. Rename every changed source entry to a unique reserved temporary name.
2. Rename every temporary entry to its final previewed name.

Unchanged rows are reported but are not renamed. Keeping all temporary names in the same directory avoids cross-device behavior and supports swaps, cycles, and case-only changes.

If an expected runtime error occurs, the service compensates completed steps in reverse order while it still holds the directory lock. A fully successful compensation returns a failed operation with every entry restored. If compensation cannot completely restore the original mapping, the operation returns `rename_recovery_required` plus logical per-entry recovery details and logs structured diagnostic context without returning physical paths.

No filesystem API can provide a true transaction across multiple rename calls. The product therefore never describes the operation as atomic; it promises complete prevalidation, serialized execution, and best-effort compensation with explicit recovery reporting.

An abrupt process, host, or storage failure can interrupt execution before compensation runs. Reserved temporary names include the operation identifier and row index, and the service logs the logical original/temporary/final mapping before the first mutation. After such a crash, an administrator may need to recover entries manually from durable service logs or backups; physical paths remain absent from browser responses.

Execute completion refreshes the originating pane, clears its selection, and leaves the dialog open on a result view. Other open tabs are not force-refreshed.

## Undo

One-level Undo is enabled only after a successful operation in the current tool session.

Before Undo, the server acquires the same directory lock and verifies:

- the operation exists, is successful, and has not already been undone;
- every renamed destination still exists at the expected logical path;
- type, size where applicable, and modified timestamp still match the post-rename fingerprint;
- every original name remains free;
- source policy and canonical containment still permit mutation.

If any check fails, Undo is blocked for the entire batch with a safe explanation. It does not skip rows. A valid Undo uses unique temporary names and the same two-phase algorithm in reverse. Undo also compensates runtime failures and can report Recovery required.

Operation records are in memory. Closing the dialog removes the client's Undo access, and an API restart invalidates the operation record. Persistent history is intentionally deferred.

## Error Handling

- Preview-level row errors are shown inline without toast spam.
- Global source, plan, and policy errors use stable Problem Details codes.
- Execute and Undo show a persistent result summary and per-row result statuses.
- Network errors preserve the last preview but disable Start until a fresh authoritative preview succeeds.
- A lost Execute or Undo response can be retried safely using the same identifier.
- Recovery-required results cannot be dismissed without a confirmation that the user has seen the logical recovery list.

## Testing Strategy

### Backend unit tests

- Token parsing and expansion for name, extension, counter, and ranges.
- Extensionless files, leading-dot names, directories, Unicode, and long names.
- Counter start, step, padding, overflow width, and row ordering.
- Plain and regex replacement, capture references, invalid patterns, and timeout behavior.
- Every case-conversion mode and fixed transformation order.
- Complete filename validation and conservative case-insensitive collision rules.
- Batch size and field-length limits.

### Backend filesystem and service tests

- Mixed files and directories in one source directory.
- Traversal, rooted input, non-child paths, source escape, and symbolic-link rejection.
- Read-only and unavailable-source enforcement at preview, execute, and undo.
- Existing destinations, duplicate destinations, unchanged entries, and stale fingerprints.
- Case-only rename, two-way swap, and three-entry cycle.
- Expired plans and idempotent Execute/Undo retry.
- Successful one-level Undo and refusal after an entry changes.
- Injected failures in temporary and final phases proving reverse compensation.
- Injected compensation failure proving Recovery-required reporting.
- No developer filesystem data is used; all tests use temporary fixtures.

### HTTP integration tests

- Request/response casing and content types for all three endpoints.
- Preview returns complete proposed new filenames and row statuses.
- Global error/status mappings and JSON 404 behavior.
- No successful or failed response contains configured physical roots.
- A completed Execute followed by Undo restores the original fixture tree.

### Angular tests

- `Ctrl+M` uses active-panel selection order or cursor fallback.
- Read-only/no-selection states are explained and disabled.
- Rule edits debounce previews and stale responses are ignored.
- Complete new filenames render and changed segments are highlighted accessibly.
- Pending, invalid, conflicted, stale, expired, and unchanged states disable Start.
- Ctrl+Enter, Escape, focus trap/restoration, and result announcements.
- Successful execution refreshes only the originating pane and clears its selection.
- Undo availability, success, refusal, and idempotent retry states.

### Playwright acceptance

Use temporary writable and read-only sources. The primary scenario:

1. Select mixed files and a directory in the active pane.
2. Press `Ctrl+M`.
3. Enter `Archive-[C]` with preserved extensions and a padded counter.
4. Verify every complete proposed new filename in the preview before Start.
5. Execute and verify the refreshed pane contains the new names.
6. Undo and verify the exact original names return.

A second scenario proves the tool cannot start on a read-only source. A conflict scenario proves one bad row blocks the entire batch and leaves every fixture unchanged.

## Operations and Documentation

README changes will document:

- `Ctrl+M` and the supported rule syntax;
- preview, conflict, execution, and Undo semantics;
- the difference between source `readOnly` metadata and operating-system/container permissions;
- a writable-source example using `"readOnly": false` and a bind mount without `:ro`;
- the risk boundary and trusted-network warning;
- the new backend, Angular, and Playwright test commands.

The default `config/sources.json` and `compose.yaml` remain read-only. Administrators must opt a specific source into writes deliberately; the application never broadens existing mounts automatically.

## Acceptance Criteria

- `Ctrl+M` opens the tool for active-panel selection or cursor fallback.
- Both files and directories can be previewed together, without recursive traversal.
- `[N]`, `[E]`, `[C]`, supported ranges, search/replace, regex, casing, and counter settings produce deterministic server previews.
- The complete new filename for every row is visible before execution.
- Any invalid, conflicting, stale, or expired row disables Start for the entire batch.
- No overwrite, path escape, symbolic-link rename, or read-only-source mutation is possible.
- Execute supports case-only changes, swaps, and cycles and never runs twice on retry.
- Runtime failure either restores the original mapping or reports explicit logical Recovery-required details.
- One-level Undo restores the entire unchanged batch or does nothing.
- The originating pane refreshes after Execute and Undo.
- Physical paths never reach the browser or Problem Details.
- Backend, Angular, integration, Playwright, production publish, and available Docker checks pass before completion is reported.

## Future Extensions

Later slices may add single-item F4 rename, date/time tokens, saved presets, manual row overrides, persistent audit/undo history, controlled symbolic-link handling, and per-user authorization. Those additions must preserve the server-authoritative preview and path-security boundaries established here.
