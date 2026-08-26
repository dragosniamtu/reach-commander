# ReachCommander File Row Alignment and Name Overflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Vertically center every dual-pane directory-table value and make long file/folder names ellipsize while preserving their complete names in native hover tooltips.

**Architecture:** Preserve the semantic table and its existing 30px rows. Restore each name `<td>` to native table-cell layout, place its icon/name/markers in an inner flex wrapper, and use the existing row model to build a full-name-first tooltip. Prove the CSS behavior with a real long-name filesystem fixture and Playwright geometry checks in both themes.

**Tech Stack:** Angular 22 standalone components, TypeScript 6, SCSS, Vitest, Playwright, ASP.NET Core E2E host, GitHub Actions.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Preserve the unrelated untracked `NC-theme.png`; do not stage, modify, or remove it.
- Keep the existing table, 30px body-row height, column widths, sorting, filtering, selection, cursor, keyboard, and double-click behavior.
- Apply vertical centering to name, extension, size, modified date, and attributes.
- Keep icon, name, screen-reader explanation, and symbolic-link marker grouped in the first cell.
- Ellipsis is visual only; `row.name`, DOM text, accessible text, and tooltip keep the complete value.
- The native tooltip always starts with the exact complete `row.name`.
- If guidance exists, append it after a newline without removing the screen-reader-only explanation.
- Do not add a custom tooltip component, focus target, dependency, API change, or backend model.
- Default and Norton themes must use identical layout behavior.
- Use test-driven development: establish focused component and browser failures before production edits.
- Do not push until local verification passes.
- Do not create v1.0.3 until pushed `master` CI is completely green.

---

### Task 1: Establish component and browser regressions

**Files:**

- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.spec.ts`
- Create: `tests/e2e/support/fixture-names.ts`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/specs/file-row-layout.spec.ts`

**Interfaces:**

- Consumes: `FileTableComponent`, its existing `PanelState` test factory, real E2E filesystem sources, `data-testid="left-panel"`, and the Norton theme toggle.
- Produces: failing contracts for `.name-content`, full-name-first native tooltips, computed `vertical-align: middle`, real overflow, and row-center geometry.

- [ ] **Step 1: Add the component DOM and normal-tooltip contract**

Extend the first component test after the existing selected-row assertion:

```typescript
const name = selected.querySelector('.file-name') as HTMLElement;
const nameContent = selected.querySelector('.name-content');

expect(nameContent).not.toBeNull();
expect(name.textContent?.trim()).toBe('movie.mkv');
expect(name.title).toBe('movie.mkv');
```

This verifies that the complete name remains in the DOM and that a normal entry's tooltip is exactly the full name.

- [ ] **Step 2: Add the guidance-tooltip contract**

In the archive/volume test, after the secondary-volume row is rendered, add:

```typescript
const volumeName = fixture.nativeElement.querySelector(
  'tr[data-path="/photos.7z.002"] .file-name',
) as HTMLElement;

expect(volumeName.title).toBe(
  'photos.7z.002\nArchive volume part. Open the primary volume instead.',
);
```

Use the exact current explanation returned by `fileTableRowExplanation`. If the existing wording differs, copy that exact stable public string into the assertion rather than weakening the test to a substring.

- [ ] **Step 3: Add a deterministic long-name filesystem fixture**

Create `tests/e2e/support/fixture-names.ts` so the test can share a fixture name without importing the global setup module:

```typescript
export const longFileNameFixture =
  'ReachCommander-directory-table-name-that-is-intentionally-too-long-for-the-available-panel-column-and-must-be-truncated-with-an-ellipsis.mkv';
```

Import it into `tests/e2e/support/seed-fixtures.ts` and, after the existing `Gladiator II.mkv` fixture, create the long file:

```typescript
writeFileSync(
  join(mediaRoot, 'Movies', longFileNameFixture),
  'long filename layout fixture\n',
);
```

The component length remains below Windows and Linux single-component filename limits.

- [ ] **Step 4: Add real-browser geometry coverage**

Create `tests/e2e/specs/file-row-layout.spec.ts`:

```typescript
import { expect, test } from '@playwright/test';
import { longFileNameFixture } from '../support/fixture-names';

for (const norton of [false, true]) {
  test(`centers file rows and ellipsizes long names in ${norton ? 'Norton' : 'default'} theme`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 680, height: 800 });
    await page.goto('/');

    if (norton) {
      await page.getByTestId('norton-theme-toggle').click();
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'norton');
    }

    const panel = page.getByTestId('right-panel');
    await panel.getByTestId('source-media').click();
    await panel.locator('tr[data-path="/Movies"]').dblclick();

    const row = panel.locator(`tr[data-path="/Movies/${longFileNameFixture}"]`);
    const name = row.locator('.file-name');
    await expect(row).toBeVisible();
    await expect(name).toHaveText(longFileNameFixture);
    await expect(name).toHaveAttribute('title', longFileNameFixture);

    const layout = await row.evaluate((element) => {
      const rowRect = element.getBoundingClientRect();
      const nameElement = element.querySelector('.file-name') as HTMLElement;
      const nameContent = element.querySelector('.name-content') as HTMLElement | null;
      const contentRect = nameContent?.getBoundingClientRect();

      return {
        rowHeight: rowRect.height,
        truncated: nameElement.scrollWidth > nameElement.clientWidth,
        verticalAlignments: [...element.querySelectorAll('td')].map(
          (cell) => getComputedStyle(cell).verticalAlign,
        ),
        nameCenterDelta: contentRect
          ? Math.abs(
              contentRect.top + contentRect.height / 2 -
              (rowRect.top + rowRect.height / 2),
            )
          : Number.POSITIVE_INFINITY,
      };
    });

    expect(layout.rowHeight).toBeCloseTo(30, 0);
    expect(layout.truncated).toBe(true);
    expect(layout.verticalAlignments).toEqual(['middle', 'middle', 'middle', 'middle', 'middle']);
    expect(layout.nameCenterDelta).toBeLessThanOrEqual(1);
  });
}
```

Use `dblclick()` so the scenario does not depend on cursor history from another test.

- [ ] **Step 5: Run the focused component test and confirm RED**

From `client/reach-commander-ui`:

```powershell
$env:PATH='C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin;' + $env:PATH
.\node_modules\.bin\ng.cmd test --watch=false --include=src/app/features/commander/file-table/file-table.component.spec.ts
```

Expected: FAIL because `.name-content` does not exist and the secondary-volume tooltip currently replaces the name with its explanation.

- [ ] **Step 6: Build the unchanged production UI and run the browser test to confirm RED**

```powershell
Set-Location client/reach-commander-ui
npm run build
Set-Location ../../tests/e2e
npm test -- --grep="centers file rows and ellipsizes long names"
Set-Location ../..
```

Expected: FAIL because body cells compute to baseline alignment, `.name-content` is absent, and the long name does not have a reliable shrink boundary.

---

### Task 2: Implement semantic centering, ellipsis, and full-name tooltips

**Files:**

- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.scss`
- Test: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.spec.ts`
- Test: `tests/e2e/specs/file-row-layout.spec.ts`

**Interfaces:**

- Consumes: `FileTableRow`, `rowExplanation(row: FileTableRow): string | null`, and the Task 1 regression contracts.
- Produces: `rowTooltip(row: FileTableRow): string`, `.name-content`, native table-cell centering, and a shrinkable `.file-name`.

- [ ] **Step 1: Add the full-name-first tooltip method**

In `file-table.component.ts`, directly after `rowExplanation`, add:

```typescript
rowTooltip(row: FileTableRow): string {
  const explanation = this.rowExplanation(row);
  return explanation ? `${row.name}\n${explanation}` : row.name;
}
```

Do not trim, shorten, encode, or otherwise transform `row.name`; Angular's attribute binding safely assigns it to the native `title` property.

- [ ] **Step 2: Restore native table-cell semantics and add the inner wrapper**

Replace the first body cell content in `file-table.component.html` with:

```html
<td class="name-cell">
  <span class="name-content">
    @let kind = iconKind(row);
    <span class="type-icon" [class]="kind" aria-hidden="true">
      @if (kind === 'parent') { ↑ }
      @else if (kind === 'folder') { ◆ }
      @else if (kind === 'archive') { ▣ }
      @else if (kind === 'volume') { ◫ }
      @else { ▪ }
    </span>
    <span class="file-name" [title]="rowTooltip(row)">{{ row.name }}</span>
    @if (rowExplanation(row); as explanation) {
      <span class="sr-only">{{ explanation }}</span>
    }
    @if (row.isSymbolicLink) {
      <span class="link-mark" title="Symbolic link">↗</span>
    }
  </span>
</td>
```

Use an inline element for `.name-content`; its CSS supplies flex layout. Keep the existing icon glyph and conditional behavior byte-for-byte.

- [ ] **Step 3: Apply vertical alignment and reliable flex overflow**

In `file-table.component.scss`, replace the current cell/name rules with:

```scss
td {
  overflow: hidden;
  padding: 0 8px;
  vertical-align: middle;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.name-content {
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  max-width: 100%;
}

.file-name {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.link-mark {
  flex: 0 0 auto;
  color: var(--accent-soft);
}
```

Delete `display: flex`, `align-items`, and `gap` from `.name-cell`. Retain `.name-cell` only if another scoped rule still needs it; otherwise remove the empty selector. Keep `.type-icon` at `flex: 0 0 15px`.

- [ ] **Step 4: Run the focused component test and confirm GREEN**

```powershell
Set-Location client/reach-commander-ui
$env:PATH='C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin;' + $env:PATH
.\node_modules\.bin\ng.cmd test --watch=false --include=src/app/features/commander/file-table/file-table.component.spec.ts
Set-Location ../..
```

Expected: PASS with all file-table component tests green.

- [ ] **Step 5: Build and run the focused browser regression**

```powershell
Set-Location client/reach-commander-ui
npm run build
Set-Location ../../tests/e2e
npm test -- --grep="centers file rows and ellipsizes long names"
Set-Location ../..
```

Expected: two scenarios PASS, one for the default theme and one for Norton. Each reports a 30px row, five middle-aligned cells, true overflow, and a name center delta no greater than 1px.

- [ ] **Step 6: Commit the feature and its regressions**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.ts client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.html client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.scss client/reach-commander-ui/src/app/features/commander/file-table/file-table.component.spec.ts tests/e2e/support/fixture-names.ts tests/e2e/support/seed-fixtures.ts tests/e2e/specs/file-row-layout.spec.ts
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "fix: center file rows and truncate names"
```

Expected: the commit contains exactly the file-table implementation and its component/browser regression coverage.

---

### Task 3: Verify, push, and publish v1.0.3

**Files:**

- Verify: all files changed by Tasks 1 and 2
- Preserve: `NC-theme.png`

**Interfaces:**

- Consumes: the committed semantic table layout and regression suites.
- Produces: a green `master`, verified edge image, and stable v1.0.3 release if every gate succeeds.

- [ ] **Step 1: Run complete frontend and browser verification**

```powershell
Set-Location client/reach-commander-ui
$env:PATH='C:\Users\Dragos\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin;' + $env:PATH
.\node_modules\.bin\ng.cmd test --watch=false
npm run test:pwa
npm run build
npm run verify:pwa
Set-Location ../../tests/e2e
npm test
Set-Location ../..
```

Expected: every Angular test, both PWA checks, production build, and all browser acceptance scenarios pass.

- [ ] **Step 2: Run backend safety verification**

```powershell
dotnet test ReachCommander.slnx -c Release
```

Expected: all backend unit and integration tests pass with zero failures.

- [ ] **Step 3: Review scope and layout contracts**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' status --short
git -c safe.directory='D:/Work/Personal/Reach Commander' diff origin/master...HEAD --check
git -c safe.directory='D:/Work/Personal/Reach Commander' diff --stat origin/master...HEAD
rg -n "name-content|rowTooltip|vertical-align|text-overflow|longFileNameFixture" client tests/e2e
```

Confirm:

- `NC-theme.png` is the only unrelated untracked path;
- no row height or column width changed;
- every body cell receives `vertical-align: middle`;
- `.file-name` has both `min-width: 0` and ellipsis properties;
- the full raw name appears first in every native tooltip;
- no custom overlay, focus behavior, dependency, API, or backend model was added;
- default and Norton tests execute the same assertions.

- [ ] **Step 4: Push `master` and wait for exact-commit CI**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' push origin master
```

Use the public GitHub Actions API for the pushed HEAD if `gh` is unauthenticated. Expected: Ubuntu and Windows backend, frontend/browser acceptance including ShellCheck, macOS installer contracts, hardened amd64 smoke, and verified multi-architecture edge publication all complete successfully.

- [ ] **Step 5: Create v1.0.3 only after green `master`**

Verify local and remote `v1.0.3` do not exist, then:

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' tag -a v1.0.3 -m "ReachCommander v1.0.3"
git -c safe.directory='D:/Work/Personal/Reach Commander' push origin v1.0.3
```

Expected: the tag peels to the same fully green commit as `master`.

- [ ] **Step 6: Verify stable release publication**

Wait for tag CI to finish successfully. Confirm:

- GitHub Release `v1.0.3` is published and non-prerelease;
- `reachcommander-installer.tar.gz` and `SHA256SUMS` are uploaded;
- downloaded installer bytes match `SHA256SUMS`;
- the bundle `VERSION` is `v1.0.3`;
- GHCR `v1.0.3` reports `org.opencontainers.image.version=v1.0.3`;
- the manifest contains runnable `linux/amd64` and `linux/arm64` images;
- remote `master` and peeled `v1.0.3` point to the same commit.

## Coverage Matrix

| Requirement | Verification |
|---|---|
| All table values vertically centered | Playwright checks five computed `vertical-align` values |
| Icon/name group centered in 30px row | Playwright `nameCenterDelta <= 1` |
| Long names display ellipsis | Real long fixture has `scrollWidth > clientWidth` |
| Full name remains available | Component and Playwright assert full DOM text and `title` |
| Guidance is preserved | Component asserts full-name-first multiline tooltip and existing SR text |
| Table semantics remain intact | `.name-content` is inside unchanged native `<td>` |
| Row density and column widths remain unchanged | 30px geometry assertion and scope review |
| Both themes behave the same | Parameterized default/Norton browser scenarios |
| No unrelated changes | Exact staging, diff review, and preserved `NC-theme.png` |
