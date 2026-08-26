# ReachCommander Single-Item Rename Design

**Date:** 2026-08-26  
**Status:** Approved  
**Scope:** Safe F4 rename for one filesystem file or directory

## Summary

ReachCommander will enable the reserved F4 Rename command for a single focused file or directory. The command opens a compact modal containing the complete current name. The user can enter an exact replacement name, see server-authoritative validation, and execute without leaving the active dual-pane workspace.

Single-item rename will not create a second filesystem mutation mechanism. An exact-name preview path will create a one-entry plan consumed by the existing batch-rename executor. This preserves the current directory mutation lock, stale-plan validation, two-phase handling for case-only changes, compensation behavior, and safe logical error responses.

Multi-item rename remains available through Ctrl+M and the top toolbar. F4 never interprets a multiple selection as a batch operation.

## Goals

- Rename regular files and directories from the active filesystem panel.
- Make the reserved F4 command and command-bar Rename button functional.
- Accept the requested name literally, including valid bracket characters such as `[N]`.
- Refuse destination conflicts without overwriting or automatically changing the requested name.
- Reuse the existing hardened rename plan, execution, lock, and compensation infrastructure.
- Keep both panels consistent when they display the affected directory.
- Provide keyboard-efficient, accessible interaction and safe validation feedback.

## Non-Goals

- Renaming more than one item through F4.
- Renaming entries inside archives.
- Renaming symbolic links or unsupported filesystem entry types.
- Moving an entry to another directory; separators are not valid in the new name.
- Overwrite, skip, merge, or create-unique-name conflict modes.
- Inline editing inside the file table.
- A new persistent rename history or a single-rename Undo interface.
- Changing the existing Ctrl+M Multi-Rename behavior.

## Command Semantics

F4 targets the focused non-parent row in the active panel. The current selection does not change this rule. This avoids an ambiguous boundary between Rename and Multi-Rename: F4 always affects one focused entry, while Ctrl+M intentionally consumes the current selection or cursor fallback.

Rename is unavailable when:

- there is no focused non-parent row;
- the active location is an archive;
- the source is unavailable or configured read-only;
- the focused row is a symbolic link or has an unsupported type;
- another blocking file-operation dialog is active.

The command-bar button remains focusable only when enabled, following the existing command-bar pattern. Its title and accessible label explain the first applicable disabled reason. The F9 command menu and global shortcut hint identify F4 as Rename and Ctrl+M as Multi-Rename.

## Dialog Interaction

The dialog captures an immutable context when it opens:

- active panel side;
- source ID and display name;
- containing logical directory;
- original logical entry path;
- original name and entry type;
- the source availability and write-policy snapshot.

The complete original file or directory name is placed in one text input and selected after the dialog receives focus. The value represents the final literal entry name, including the extension for files.

The UI provides:

- a `Rename file` or `Rename folder` title;
- the source and containing logical path as context;
- one required `New name` field;
- inline preview/validation status;
- `Rename` and `Cancel` actions.

Changing the input invalidates the previous preview and starts a short debounced preview request. Rename is enabled only when the latest preview is valid, represents a changed name, and no preview or execution request is pending. Enter executes the current valid plan. Escape closes the dialog only while execution is not pending. Closing restores focus to the originating panel.

On successful execution, the dialog closes, stale selection state is cleared, and each panel currently displaying the same source and logical directory is refreshed. The originating panel moves its cursor to the returned new logical path when that row remains visible under the active filter. If the renamed entry is filtered out, focus returns to the panel without changing the filter.

## API Contract

### Exact-name preview

`POST /api/renames/preview`

Request:

```json
{
  "sourceId": "media",
  "directoryPath": "/movies",
  "entryPath": "/movies/Old name.mkv",
  "newName": "New name.mkv"
}
```

The request contains logical identifiers only. `newName` is a single literal child name, not a path, mask, token expression, or regular expression.

The response uses the existing `BatchRenamePreviewDto` contract. A successful changed preview contains one row and a `planId`. An unchanged name produces one `unchanged` row and cannot execute. Invalid or occupied destinations produce one invalid/conflict row and cannot execute.

### Execution

The client executes the returned plan through the existing endpoint:

`POST /api/batch-renames/{planId}/execute`

The response remains the existing `BatchRenameOperationDto`. The single-item UI consumes its one row and does not expose Undo. The existing operation record and expiration rules remain unchanged.

## Application and Infrastructure Design

`IBatchRenameService` gains a focused exact-name preview operation. The command contains source ID, containing directory, one entry path, and one literal destination name.

`BatchRenamePlanner` will share its directory resolution, entry snapshot, name validation, destination resolution, conflict detection, plan persistence, and revalidation steps between rule-based and exact-name previews. Rule evaluation remains exclusive to Multi-Rename. The exact-name path passes the literal name directly to `RenameNameValidator`, so characters with Multi-Rename token meaning are not interpreted.

The resulting stored plan is structurally identical to an existing one-entry batch plan. `BatchRenameExecutor` and the execute controller path require no alternate filesystem behavior.

Server validation must confirm:

- the source and containing directory resolve through `IPathSecurityService`;
- the source is available and writable by policy;
- the entry path is a distinct direct child of the containing directory;
- the entry exists and is a regular file or directory;
- the entry is not a symbolic link;
- the new name is non-empty, within configured length limits, and valid for the supported host filesystems;
- the new name contains no path separator, traversal segment, null character, reserved internal name, or platform-reserved name;
- the destination resolves as a child of the same logical directory;
- no unselected entry occupies the destination under the existing cross-platform comparison policy.

Execution revalidates the complete entry fingerprint and destination under the existing directory mutation lock. A case-only rename is a change when the ordinal name differs, even on a case-insensitive filesystem, and continues through the existing two-phase executor.

## Angular State and Components

### `SingleRenameStore`

A focused store owns:

- the immutable dialog context;
- the editable new name;
- debounce and request-generation tokens;
- the latest preview and execution result;
- preview and execution pending flags;
- safe error code/detail state;
- the completion callback used to refresh panel state.

Late preview responses are discarded when their request token or captured input no longer matches. Closing the dialog invalidates pending response tokens. Authentication reset closes the store and clears all protected state.

### `RenameDialogComponent`

The standalone dialog renders state and emits close/completion intents. It does not resolve paths or call HTTP directly. It follows the existing modal focus, backdrop, live-region, and opener-restoration conventions used by Create Directory and Multi-Rename.

### Commander integration

`CommanderStore` provides a single-rename context derived only from the active cursor row. `CommanderShellComponent` owns command availability, opens the store, blocks unrelated keyboard commands while the dialog is open, and performs post-success refresh/focus coordination. `CommandBarComponent` receives a new `rename` availability entry and enables F4 accordingly.

`CommanderApiPort` and `ReachCommanderApi` gain typed exact-name preview support. Existing batch execution support is reused.

## Error Handling

The server remains authoritative. Client-side required-field checks improve feedback but never enable execution without a matching server preview.

Expected safe problem codes include the existing source, path, entry, plan, and recovery codes. Preview-row validation handles invalid and conflict states without converting them into transport errors. In all validation and conflict cases, the dialog remains open and preserves the user's requested name.

Transport failures use the existing generic request-failed message. Server responses and logs must not expose physical host paths. Execution failure or recovery-required status keeps the dialog open and shows the logical operation result so the user does not mistake a partial/recovered operation for success.

## Accessibility

- The dialog uses `role="dialog"`, `aria-modal="true"`, an explicit title association, and trapped focus.
- The name field has a persistent visible label and its validation message is associated through `aria-describedby`.
- Preview and execution changes are announced through a polite live region; blocking failures use an assertive announcement.
- Enter and Escape behavior does not bypass disabled or pending states.
- On close, focus returns to the originating panel or its still-connected opener.
- Disabled F4 behavior has a concise accessible reason through the existing command-bar contract.

## Test Strategy

### Backend unit and HTTP tests

- exact file rename preview creates a valid one-entry plan;
- exact directory rename preview creates a valid one-entry plan;
- literal `[N]` and other valid bracket names are not expanded;
- same-name preview is unchanged and cannot execute;
- case-only preview and execution succeed;
- occupied destination is a conflict and never overwrites;
- invalid empty, separator, traversal, reserved, overlong, and platform-invalid names are rejected;
- non-direct-child, missing, read-only, unavailable, symbolic-link, and unsupported entries are rejected safely;
- a changed source fingerprint or newly occupied destination makes execution stale;
- exact plans use the existing lock, compensation, idempotency, expiry, and safe problem mapping;
- the authenticated/antiforgery/rate-limited controller conventions remain intact.

### Angular tests

- cursor-only context ignores a multiple selection;
- F4 availability covers writable file/folder and every disabled state;
- opening selects the complete current name and captures immutable context;
- input changes debounce preview and stale responses are ignored;
- Enter executes only the matching valid plan;
- Escape and Cancel respect pending execution;
- conflicts and validation errors preserve the input and keep the dialog open;
- successful execution refreshes matching panels, clears stale selection, and restores the renamed row cursor when visible;
- protected-state reset closes and clears the dialog;
- command menu and shortcut hint document F4 and Ctrl+M correctly.

### Browser acceptance

- rename a file with F4 and verify the new name appears;
- rename a directory with the command-bar button and verify it remains navigable;
- rename a file to a literal bracket-containing name;
- attempt an occupied name and verify both original entries remain unchanged;
- verify Rename is unavailable in archives and on read-only sources;
- verify keyboard focus returns to the affected panel after success and cancellation.

## Acceptance Criteria

1. A focused regular file or directory in a writable filesystem panel can be renamed through F4 or the Rename command-bar button.
2. The dialog edits the complete literal name and shows server-authoritative preview feedback.
3. Multiple selection does not cause F4 to rename multiple entries; Ctrl+M remains the batch workflow.
4. Existing destinations are never overwritten, merged, skipped, or automatically renamed.
5. Case-only and literal bracket-containing names work on supported Windows, Linux, and macOS deployments where the host filesystem permits them.
6. Archive entries, symbolic links, unsupported entries, unavailable sources, and read-only sources cannot be renamed.
7. Stale previews cannot execute against changed filesystem state.
8. Successful rename refreshes all matching open panels and returns keyboard focus predictably.
9. Authentication, authorization, antiforgery, rate limiting, logical-path containment, mutation locking, and safe error-response guarantees remain enabled.
10. Backend, Angular, and browser acceptance suites cover both files and directories and pass on the supported CI platforms.
