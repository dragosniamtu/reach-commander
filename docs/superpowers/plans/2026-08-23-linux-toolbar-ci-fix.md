# Linux Toolbar CI Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent the active-panel search controls from extending underneath the right-side toolbar actions at the supported 1024 px desktop width on Linux.

**Architecture:** Keep the existing three-column topbar, control order, and Norton-label breakpoint. Allow the middle toolbar's search container to shrink by another 3rem when operating-system font metrics make the right action column wider, while preserving the search icon, clear button, keyboard behavior, and accessible label.

**Revision:** CI run 23 disproved the initial compact-Norton-label hypothesis: the 12.4 px overlap was unchanged. The right column width changes the grid track, but the actual overflow floor comes from `.search-box { min-width: 8rem; }` in the middle toolbar.

**Tech Stack:** Angular 20 templates and SCSS, Playwright 1.55, Chromium, GitHub Actions Ubuntu runner.

## Global Constraints

- Work directly on `master`; do not create a worktree or feature branch.
- Preserve the untracked `NC-theme.png` file and do not stage it.
- Make only the toolbar responsiveness change required by the failing CI job.
- Keep the theme toggle accessible through its existing full `aria-label` and `title` in both visual variants.

---

### Task 1: Test the initial compact-label hypothesis (disproved by CI run 23)

**Outcome:** Local checks passed, but Ubuntu CI reproduced the exact original geometry. Task 2 replaces this symptom-level change with the root fix and restores the original 760 px theme-label breakpoint.

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

- [x] **Step 5: Commit and push after verification**

```powershell
git add docs/superpowers/plans/2026-08-23-linux-toolbar-ci-fix.md tests/e2e/specs/active-toolbar.spec.ts client/reach-commander-ui/src/styles.scss
git commit -m "fix: keep desktop toolbar controls separated"
git push origin master
```

Expected: the push starts a new CI run for `master`; monitor the Ubuntu browser-acceptance job to completion.

### Task 2: Remove the middle toolbar's cross-platform overflow floor

**Files:**
- Modify: `tests/e2e/specs/active-toolbar.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.scss`
- Modify: `client/reach-commander-ui/src/styles.scss`

**Interfaces:**
- Consumes: the existing 1024 px toolbar hierarchy scenario and `.search-box` flex container.
- Produces: a 5rem minimum search width that keeps the clear button out of the right action column under a deterministic 510 px right-column stress case.

- [x] **Step 1: Replace the platform-dependent assertion with a deterministic failing stress case**

At the 1024 px iteration, reserve 510 px for `.top-actions` before measuring the existing non-overlap contract:

```ts
if (viewport.width === 1024) {
  await topActions.evaluate((element) => {
    (element as HTMLElement).style.minWidth = "510px";
  });
}
```

- [x] **Step 2: Run the focused test and verify the stress case fails**

```powershell
npx playwright test specs/active-toolbar.spec.ts --project=chromium --grep "toolbar hierarchy"
```

Expected: FAIL with the search clear button extending beyond the stressed action column boundary. Observed before implementation: boundary `501`, clear-button edge `519.1875`.

- [x] **Step 3: Implement the root fix and restore the original theme behavior**

Change the search flex item's minimum width without changing its preferred width:

```scss
.search-box {
  flex: 1 1 210px;
  min-width: 5rem;
}
```

Restore the global compact Norton-label query to `@media (max-width: 760px)`.

CI run 24 confirmed the constraint: `6rem` reduced the stressed overlap from 18.1875 px to 5.109375 px. The final `5rem` floor supplies another 16 px without changing the preferred 210 px width.

- [x] **Step 4: Run focused and full verification**

```powershell
npx playwright test specs/active-toolbar.spec.ts --project=chromium --grep "toolbar hierarchy"
npx playwright test specs/active-toolbar.spec.ts --project=chromium
npm test
```

Run the Angular unit suite and production build with a Node.js version supported by Angular 20, then run `git diff --check`.

- [ ] **Step 5: Commit, push, and monitor the replacement run**

```powershell
git add client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.scss client/reach-commander-ui/src/styles.scss tests/e2e/specs/active-toolbar.spec.ts docs/superpowers/plans/2026-08-23-linux-toolbar-ci-fix.md
git commit -m "fix: let active toolbar search shrink"
git push origin master
```

Expected: Ubuntu browser acceptance, both backend jobs, hardened container smoke, and multi-architecture image publication complete successfully.
