# ReachCommander Shift+Arrow Selection Design

## Goal

Add conventional keyboard range selection to both file panes. Holding Shift while pressing Up or Down moves the cursor and selects the inclusive range between a stable anchor and the new cursor row. Files and directories are eligible; synthetic parent (`..`) rows are never selected.

## Interaction model

- `Shift+ArrowUp` and `Shift+ArrowDown` operate only on the active pane.
- The first shifted move anchors at the row that held the cursor before the move.
- Repeated shifted moves extend the range; reversing direction shrinks it.
- The selected set for a shifted range is replaced by the current inclusive range, matching existing Shift+click behavior.
- A normal cursor move starts a new future keyboard range from its destination without changing the current selected set.
- Navigation clamps at the first and last visible rows. If a boundary move cannot move the cursor, it does not invent an additional selection.
- Parent rows may carry the cursor but are filtered out of the selected set.
- Filtering and sorting continue to define the visible row order used by the range.

The bottom shortcut hint and command help will expose `Shift+Up/Down` as range selection.

## Architecture

The keyboard command keeps cursor movement semantic by adding an `extendSelection` flag to the existing `move-cursor` command. The keyboard service derives that flag only for Shift+Up/Down. Other Shift combinations remain unhandled and ordinary text controls keep their existing editing behavior.

`CommanderStore.moveCursor` accepts the selection intent and reuses the same inclusive-range calculation as pointer range selection. A shared private helper prevents Shift+click and Shift+arrow from drifting into different rules. The store owns the anchor because selection must remain stable across repeated commands and pane component lifecycles.

## Accessibility and compatibility

Existing `aria-selected` row state announces each selected row. The cursor and selected styling remain unchanged, so all Modern, Norton, and Windows 95 themes inherit the behavior. No backend, API, persistence, or file-operation contract changes are required.

## Testing

Coverage will prove:

1. the keyboard service maps Shift+Up/Down to movement commands with selection intent;
2. unshifted movement preserves its current command and text controls remain untouched;
3. the store anchors, extends, reverses, clamps, and excludes parent rows;
4. Shift+click continues to use the same range behavior;
5. browser acceptance exercises the active pane and verifies selected file and directory rows through `aria-selected`.

## Out of scope

- Shift+PageUp/PageDown or Shift+Home/End.
- Ctrl+Shift additive ranges.
- Persisting selection across reloads or directory navigation.
- Changing Insert, Ctrl+A, or pointer-selection behavior beyond sharing the range helper.
