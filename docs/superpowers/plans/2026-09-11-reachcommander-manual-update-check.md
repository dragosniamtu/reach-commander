# ReachCommander Manual Update Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the toolbar update control available for a manual trusted update check and expose the existing confirmation action when that check finds a release.

**Architecture:** Reuse the existing target-free Check endpoint and `SystemUpdateStore`. Broaden only the toolbar's retry eligibility; the returned store state naturally changes the same control from Check to the existing confirmed Apply flow.

**Tech Stack:** Angular, TypeScript, Vitest, Playwright

## Global Constraints

- This is a manual discovery retry, not a force reinstall.
- The browser cannot select any update target.
- Apply still requires explicit confirmation and the existing backend safety flow.
- Pinned and active-operation states remain disabled.

---

### Task 1: Make manual update discovery recoverable

**Files:**
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.ts`
- Test: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts`

**Interfaces:**
- Consumes: existing `check` output and updater status.
- Produces: idle retry access for missing/current/unavailable states.

- [ ] Add failing tests for unavailable and missing-state manual checks plus pinned/busy protection.
- [ ] Run the focused component spec and confirm the disabled-control failures.
- [ ] Broaden `canCheck` only for idle missing/current/unavailable states.
- [ ] Run the focused spec and confirm it passes.

### Task 2: Verify the browser discovery transition

**Files:**
- Test: `tests/e2e/specs/system-update.spec.ts`

**Interfaces:**
- Consumes: the existing `SystemUpdateStore.check()` status transition.
- Produces: browser acceptance for unavailable to available without any browser-selected target.

- [ ] Add a failing browser test for target-free Check followed by the available state.
- [ ] Run the focused test and confirm the unavailable control is disabled.
- [ ] Reuse the existing store transition and confirmation behavior without adding a second update path.
- [ ] Run focused tests, full Angular tests, production/PWA build, and system-update Playwright acceptance.

### Task 3: Document and verify

**Files:**
- Modify: `README.md`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `docs/operations.md`

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces: operator-facing explanation of manual retry behavior.

- [ ] Update documentation without changing host-side security claims.
- [ ] Run documentation contracts, full relevant verification, and `git diff --check`.
- [ ] Confirm `NC-theme.png` remains untouched.
