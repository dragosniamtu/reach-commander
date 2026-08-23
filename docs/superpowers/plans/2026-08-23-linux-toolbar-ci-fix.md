# Linux Toolbar CI Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent the Norton theme toggle from forcing the active-panel search controls underneath the right-side toolbar actions at the supported 1024 px desktop width on Linux.

**Architecture:** Keep the existing three-column topbar and its current control order. Reuse the toggle's existing compact `N` presentation at medium desktop widths so the right action column has deterministic headroom across operating-system font metrics, while retaining the full `Norton` label on wider screens.

**Tech Stack:** Angular 20 templates and SCSS, Playwright 1.55, Chromium, GitHub Actions Ubuntu runner.

## Global Constraints

- Work directly on `master`; do not create a worktree or feature branch.
- Preserve the untracked `NC-theme.png` file and do not stage it.
- Make only the toolbar responsiveness change required by the failing CI job.
- Keep the theme toggle accessible through its existing full `aria-label` and `title` in both visual variants.

---

### Task 1: Give the Norton toggle medium-width headroom

**Files:**
- Modify: `tests/e2e/specs/active-toolbar.spec.ts`
- Modify: `client/reach-commander-ui/src/styles.scss`

**Interfaces:**
- Consumes: the existing `.theme-label`, `.theme-label-compact`, and `.theme-toggle` elements rendered by `CommanderShellComponent`.
- Produces: a compact visual toggle at widths up to 1120 px, with the existing accessible name unchanged.

- [x] **Step 1: Write the failing browser assertion**

In the 1024 px iteration of `keeps the toolbar hierarchy clear at desktop widths`, assert that the full label is hidden, the compact label is visible, and the button retains its accessible name:

```ts
const themeToggle = page.getByTestId("norton-theme-toggle");
if (viewport.width <= 1120) {
  await expect(themeToggle.locator(".theme-label")).toBeHidden();
  await expect(themeToggle.locator(".theme-label-compact")).toBeVisible();
  await expect(themeToggle).toHaveAccessibleName("Activate Norton theme");
}
```

- [x] **Step 2: Run the focused test and verify the new assertion fails**

Run from `tests/e2e`:

```powershell
npm test -- --project=chromium specs/active-toolbar.spec.ts --grep "toolbar hierarchy"
```

Expected: FAIL at 1024 px because `.theme-label` is currently visible until 760 px.

- [x] **Step 3: Extend the existing compact-label breakpoint**

Move the label-switching rules into a medium-width media query while keeping the small-width action-hiding rule separate:

```scss
@media (max-width: 1120px) {
  :root .theme-label {
    display: none;
  }

  :root .theme-label-compact {
    display: inline;
  }

  :root .top-actions .theme-toggle {
    width: 31px;
    padding: 0;
  }
}
```

The existing `@media (max-width: 760px)` block will continue to hide the other optional top actions and will no longer duplicate these three declarations.

- [x] **Step 4: Run focused and full verification**

Run:

```powershell
npm test -- --project=chromium specs/active-toolbar.spec.ts
npm test
```

Expected: the focused active-toolbar scenarios pass at 680, 1024, 1200, and 1440 px; then all browser acceptance scenarios pass.

Run the frontend checks from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false
npm run build
```

Expected: all Angular tests pass and the production build completes.

Run repository hygiene checks:

```powershell
git diff --check
git status --short
```

Expected: only the plan, Playwright spec, and global theme stylesheet are tracked changes; `NC-theme.png` remains untracked.

- [ ] **Step 5: Commit and push after verification**

```powershell
git add docs/superpowers/plans/2026-08-23-linux-toolbar-ci-fix.md tests/e2e/specs/active-toolbar.spec.ts client/reach-commander-ui/src/styles.scss
git commit -m "fix: keep desktop toolbar controls separated"
git push origin master
```

Expected: the push starts a new CI run for `master`; monitor the Ubuntu browser-acceptance job to completion.
