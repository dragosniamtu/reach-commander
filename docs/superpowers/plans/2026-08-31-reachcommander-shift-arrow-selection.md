# ReachCommander Shift+Arrow Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Select an inclusive file/folder range in the active pane with Shift+Up and Shift+Down.

**Architecture:** Carry selection intent on the existing cursor command and centralize inclusive visible-row range calculation in `CommanderStore`, shared by pointer and keyboard selection. No API or persistence changes.

**Tech Stack:** Angular 22 signals, Vitest, Playwright.

## Constraints

- Work directly on `master`; do not create a worktree.
- Do not modify, stage, or remove untracked `NC-theme.png`.
- Parent rows are never selected.
- Keep existing Insert, Ctrl+A, Shift+click, filtering, sorting, and file-operation capture behavior.
- Do not push unless explicitly requested.

### Task 1: Keyboard command and store range semantics

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-command.ts`
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.ts`
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`

- [ ] Add failing command-mapping tests for Shift+Up/Down and regression coverage for normal arrows and text controls.
- [ ] Add failing store tests for inclusive extension, reversal/shrinking, bounds, separate panes, and parent-row exclusion.
- [ ] Add `extendSelection` to cursor commands and route it through the shell.
- [ ] Refactor pointer and keyboard ranges through one store helper, keeping the anchor stable during shifted movement and resetting the future anchor on ordinary movement.
- [ ] Run focused keyboard, store, and shell tests.
- [ ] Commit the behavior slice.

### Task 2: Browser acceptance and help copy

**Files:**
- Modify: `tests/e2e/specs/commander-milestone1.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `README.md`

- [ ] Add a failing browser scenario that selects a file/folder range with Shift+Down, shrinks with Shift+Up, and proves only the active pane changes.
- [ ] Add `Shift+Up/Down` to the shortcut hint and command help; document keyboard range selection in README.
- [ ] Run focused Playwright acceptance, the complete Angular suite, production/PWA gates, and the full Chromium suite.
- [ ] Review the implementation diff, commit documentation/acceptance changes, and report without pushing.
