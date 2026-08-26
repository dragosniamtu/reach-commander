# ReachCommander File Row Alignment and Name Overflow Design

**Date:** 2026-08-26  
**Status:** Approved for implementation

## Objective

Make every directory-table value vertically centered within its existing 30px row. Long file and folder names must truncate with an ellipsis and expose the complete name on hover without changing row density, selection, navigation, sorting, or theme behavior.

## Scope

The change is limited to the dual-pane directory table:

- name, extension, size, modified date, and attributes are vertically centered;
- the file/folder icon, name, accessibility explanation, and symbolic-link marker remain grouped in the first cell;
- long names truncate visually with `…`;
- hovering the visible name exposes the complete untruncated name;
- entry-specific guidance remains available in the tooltip and screen-reader text;
- default and Norton themes use the same layout behavior.

The change does not alter row height, column widths, file data, sorting, filtering, selection, keyboard behavior, or other lists and dialogs.

## Selected Approach

Keep every `<td>` as a native table cell and set body cells to `vertical-align: middle`. The name cell receives an inner `.name-content` flex wrapper containing the icon, visible name, screen-reader explanation, and optional symbolic-link marker.

This avoids converting a table cell into a flex box, which currently prevents normal table-cell vertical alignment. It also keeps table semantics intact while retaining the existing horizontal icon/name layout.

Alternative approaches were rejected:

- assigning a fixed height to the current flex-based `<td>` duplicates the row height and preserves unusual table layout behavior;
- replacing the table with CSS Grid is disproportionate to a focused alignment correction.

## Name Overflow and Tooltip

The `.name-content` wrapper fills the available width and declares `min-width: 0`. The visible `.file-name` element also declares `min-width: 0`, remains single-line, and uses `overflow: hidden` plus `text-overflow: ellipsis`.

The visible name keeps flexible width while the icon and symbolic-link marker remain non-shrinking. Ellipsis appears only when the name exceeds the available first-column width.

A component method produces the native tooltip text:

- normal entry: the exact complete `row.name`;
- entry with operational guidance: the exact complete `row.name`, followed by a newline and the existing explanation.

The full name is therefore always the first tooltip line. The existing screen-reader-only explanation remains unchanged.

## Accessibility and Interaction

- The file table remains a semantic `<table>`.
- Rows and cells keep their existing selection and cursor attributes.
- The tooltip is attached to the visible `.file-name` only.
- Truncation is visual; the DOM and accessible name retain the complete text.
- No additional focus target, event handler, or custom overlay is introduced.
- Native hover behavior works in both browser and installed PWA contexts.

## Testing

Component tests will verify:

- the name cell contains the new `.name-content` wrapper;
- a normal entry tooltip equals the complete name;
- an entry with guidance includes the complete name first and retains the guidance;
- the DOM text is not shortened manually.

Browser acceptance will use a deliberately long file name in a constrained pane and verify:

- the visible name box is narrower than its scroll width, proving ellipsis conditions;
- the full name remains in `textContent` and `title`;
- every body cell reports `vertical-align: middle`, and the name wrapper's center matches the 30px row center within a small pixel tolerance;
- the behavior remains valid in the default and Norton themes at supported compact width.

The focused component test, relevant browser acceptance test, complete Angular suite, and production build must pass before release.

## Release

The change will be committed directly to `master` as requested. It will be pushed only after local verification. A new stable version will be created only after the pushed `master` CI is fully green; the next unused patch version after v1.0.2 is v1.0.3.
