# Header Action Menu Implementation Plan

**Goal:** Replace header action buttons with one labeled Menu button.

**Architecture:** A small projected application-menu component owns popup state,
keyboard navigation and dismissal. The active-panel toolbar keeps search/context
and projects existing file actions plus shell-owned application controls into it.
Existing stores keep responsibility for operations and dialogs.

**Tech stack:** Angular, existing SVG icons, SCSS theme variables, Vitest, Playwright.

## Constraints

- Preserve command availability, source policies, update checks and account flows.
- Retain visible icon and text labels at every supported viewport.
- Use the persistent Menu button as the return-focus target for menu commands.
- Do not alter backend behavior, install configuration or release versions.

## Tasks

- [x] Add application-menu component with projected controls, nonmodal dialog,
  outside-click/Escape dismissal and keyboard navigation isolated from file shortcuts.
- [x] Move toolbar actions and shell controls into the menu. Add labeled presentation
  to update/metrics/transfer controls and external opener support to account dialogs.
- [x] Update affected tests and add focused keyboard and dialog-focus coverage.
- [x] Run frontend unit tests and production/PWA build, then browser acceptance;
  inspect screenshots at desktop/680px in Modern, Norton and Windows 95 themes.
- [x] Review the final diff and report verification and deployment status.

## Verification (2026-11-27)

- Frontend unit tests: 64 files, 585 tests passed. Node 26 requires
  `NODE_OPTIONS=--no-experimental-webstorage` for this repository's jsdom setup.
- Production build and `verify:pwa`: passed; PWA source contracts: 2 passed.
- Full Playwright acceptance suite: 83 tests passed with retries disabled.
  The final `artifacts/playwright-results/.last-run.json` records `passed` and
  no failed tests at 13:01 local time.
- Inspected menu screenshots in Modern, Norton and Windows 95 themes, including
  the supported 680px viewport. Desktop preview: `artifacts/header-menu-preview.png`.
- `git diff --check`: passed.

The implementation remains local on `codex/header-action-menu`. No commit, push,
version change, release publication or deployment was performed for this task.

## Subtitles grouping follow-up

- Added the user-requested **Menu → Subtitles** expandable item with icons and
  labels for Generate Subtitles, Repair subtitles and Subtitle jobs.
- Kept existing handlers, eligibility, active-pane context and Menu focus return.
  Expanding the group leaves Menu open; keyboard navigation skips collapsed actions.
- Updated operator instructions and the existing toolbar/browser checks.
- Verification: 597 frontend tests, production build and PWA verification passed;
  all 16 targeted browser cases passed, including generation, real repair saves,
  keyboard handling and screenshots in all three themes at 1440px and 680px.
- Inspected theme screenshots. Preview: `artifacts/subtitles-menu-preview.png`.
  `git diff --check` passed. Changes remain local and unpublished.
