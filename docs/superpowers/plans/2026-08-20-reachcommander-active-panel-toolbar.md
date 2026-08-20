# ReachCommander Active-Panel Toolbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a responsive active-panel toolbar with Multi-Rename, secure Add files, wildcard search, and explicit read-only/writable source indicators.

**Architecture:** `CommanderStore` remains the single owner of independent left/right panel state. A presentation-only `ActivePanelToolbarComponent` receives a computed immutable view of the active panel and emits user intents; `CommanderShellComponent` captures the active source/directory before handing rename or upload work to their isolated stores. Wildcard matching is a pure client-side function used by the existing file-table view model. The toolbar composes the separately planned Multi-Rename and secure-upload slices without duplicating their server logic.

**Tech Stack:** Angular 22 standalone components, Signals, RxJS, Angular CDK A11y already in the project, semantic HTML, local inline SVG icons, Vitest through Angular CLI, Playwright, .NET 10 verification.

## Prerequisites and Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Execute `2026-08-20-reachcommander-secure-uploads.md` Tasks 1–6 before the Add files integration in Task 4 below.
- Execute `2026-08-19-reachcommander-multi-rename.md` Tasks 1–9 before the Multi-Rename integration in Task 4 below.
- After this plan's Task 4, execute the upload plan's Task 7 and Multi-Rename plan's Task 10, then finish this plan's Task 5 as the combined release gate.
- Use TDD for each production slice and observe the focused test fail for the intended missing behavior before implementation.
- The toolbar always targets the active panel, but an opened upload or rename flow retains its captured side, source ID, source name, and logical directory.
- Multi-Rename and Add files are available only when the active source is available and explicitly writable. Multi-Rename additionally needs selected eligible entries or a non-parent cursor item.
- Preserve checked-in source and Docker read-only defaults. `RW` communicates application policy; the server still revalidates host/container permissions.
- Wildcards are not regular expressions: `*` means zero or more characters and `?` means exactly one character. A filter without either wildcard preserves current case-insensitive substring behavior.
- Use local inline SVG with `currentColor`; do not add an icon dependency.
- Preserve the hardware widget at the far right and prevent horizontal page overflow at supported viewport widths.
- Before every commit, inspect `git status --short` and stage only the files named by that task.

## File Structure

```text
client/reach-commander-ui/src/app/
├── core/
│   ├── keyboard/
│   │   ├── commander-command.ts
│   │   ├── commander-keyboard.service.ts
│   │   └── commander-keyboard.service.spec.ts
│   └── state/
│       ├── wildcard-filter.ts
│       └── wildcard-filter.spec.ts
└── features/commander/
    ├── active-panel-toolbar/
    │   ├── active-panel-toolbar.component.ts
    │   ├── active-panel-toolbar.component.html
    │   ├── active-panel-toolbar.component.scss
    │   └── active-panel-toolbar.component.spec.ts
    ├── commander-panel/
    │   ├── commander-panel.component.ts
    │   └── commander-panel.component.html
    ├── commander-shell/
    │   ├── commander-shell.component.ts
    │   ├── commander-shell.component.html
    │   ├── commander-shell.component.scss
    │   └── commander-shell.component.spec.ts
    ├── file-table/file-table.viewmodel.spec.ts
    ├── quick-filter/                         delete obsolete component
    └── source-selector/
        ├── source-selector.component.html
        ├── source-selector.component.scss
        └── source-selector.component.spec.ts

tests/e2e/specs/active-toolbar.spec.ts
README.md
```

---

### Task 1: Define and test literal wildcard matching

**Files:**

- Create: `client/reach-commander-ui/src/app/core/state/wildcard-filter.ts`
- Create: `client/reach-commander-ui/src/app/core/state/wildcard-filter.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/file-table.viewmodel.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/file-table/file-table.viewmodel.spec.ts`

**Interfaces:**

- Produces: `matchesFileFilter(name: string, extension: string | null, rawFilter: string): boolean`.
- Consumed by: `buildVisibleRows` only; no component compiles regex directly.

- [ ] **Step 1: Write failing pure wildcard tests**

Create `wildcard-filter.spec.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { matchesFileFilter } from './wildcard-filter';

describe('matchesFileFilter', () => {
  it.each([
    ['notes.txt', 'txt', '', true],
    ['notes.txt', 'txt', 'note', true],
    ['notes.txt', 'txt', 'TXT', true],
    ['archive.tar.gz', 'gz', '*.gz', true],
    ['archive.tar.gz', 'gz', '*.zip', false],
    ['report-01.pdf', 'pdf', 'report-??.pdf', true],
    ['report-1.pdf', 'pdf', 'report-??.pdf', false],
    ['photo', null, 'photo*', true],
    ['photo', null, '*photo', true],
    ['photo-1', null, 'photo', true],
    ['photo-1', null, 'photo.', false],
    ['a+b[1].txt', 'txt', 'a+b[1].*', true],
    ['A+B[1].TXT', 'TXT', 'a+b[1].*', true],
    ['Résumé.md', 'md', 'résumé.?d', true],
  ])('%s with %s filtered by %s is %s', (name, extension, filter, expected) => {
    expect(matchesFileFilter(name, extension, filter)).toBe(expected);
  });
});
```

- [ ] **Step 2: Run the focused test and confirm the missing module failure**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/wildcard-filter.spec.ts"
Pop-Location
```

Expected: FAIL because `wildcard-filter.ts` does not exist.

- [ ] **Step 3: Implement escaped, anchored wildcard matching**

Create `wildcard-filter.ts` with these rules:

```ts
const wildcard = /[*?]/;
const regexSyntax = /[\\^$.*+?()[\]{}|]/g;

export function matchesFileFilter(
  name: string,
  extension: string | null,
  rawFilter: string,
): boolean {
  const filter = rawFilter.trim();
  if (!filter) {
    return true;
  }

  if (!wildcard.test(filter)) {
    const needle = filter.toLocaleLowerCase();
    return name.toLocaleLowerCase().includes(needle) ||
      (extension?.toLocaleLowerCase().includes(needle) ?? false);
  }

  const source = [...filter]
    .map((character) => character === '*'
      ? '.*'
      : character === '?'
        ? '.'
        : character.replace(regexSyntax, '\\$&'))
    .join('');

  return new RegExp(`^${source}$`, 'iu').test(name);
}
```

Keep regex compilation inside the pure helper. Literal regex metacharacters are escaped, wildcard patterns match the full entry name, and non-wildcard text keeps the current name-or-extension substring semantics.

- [ ] **Step 4: Add view-model coverage before wiring the helper**

Extend `file-table.viewmodel.spec.ts` with fixtures proving:

- `*.exe` returns matching files and directories by full name;
- `report-??.pdf` is anchored;
- a non-wildcard filter remains substring-based;
- `..` remains the first row in a non-root directory even when it does not match;
- empty results do not move or remove the parent row.

Run:

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/file-table.viewmodel.spec.ts"
Pop-Location
```

Expected: the new wildcard cases FAIL under the old substring filter.

- [ ] **Step 5: Use the helper in `buildVisibleRows`**

Replace the inline lowercase predicate with:

```ts
import { matchesFileFilter } from './wildcard-filter';

const entries = panel.entries
  .filter((entry) => matchesFileFilter(entry.name, entry.extension, panel.filter))
  .map<FileTableRow>((entry) => ({ ...entry, isParent: false }));
```

Keep parent-row insertion after entry filtering.

- [ ] **Step 6: Run focused and complete frontend tests**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/wildcard-filter.spec.ts" --include="**/file-table.viewmodel.spec.ts"
npm test -- --watch=false
Pop-Location
```

Expected: PASS.

- [ ] **Step 7: Commit the matching slice**

```powershell
git status --short
git add client/reach-commander-ui/src/app/core/state/wildcard-filter.ts client/reach-commander-ui/src/app/core/state/wildcard-filter.spec.ts client/reach-commander-ui/src/app/core/state/file-table.viewmodel.ts client/reach-commander-ui/src/app/features/commander/file-table/file-table.viewmodel.spec.ts
git commit -m "feat: add wildcard file filtering"
```

---

### Task 2: Show explicit source access policy

**Files:**

- Modify: `client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.spec.ts`

**Interfaces:**

- Every source button renders exactly one visible `RO` or `RW` policy token.
- Unavailable sources additionally render the existing warning status and preserve policy in the accessible name.

- [ ] **Step 1: Write failing component tests**

Add tests with an available read-only source, an available writable source, and an unavailable writable source. Assert:

```ts
expect(buttons[0].querySelector('[data-access="read-only"]')?.textContent).toContain('RO');
expect(buttons[1].querySelector('[data-access="writable"]')?.textContent).toContain('RW');
expect(buttons[2].getAttribute('aria-label')).toContain('unavailable');
expect(buttons[2].getAttribute('aria-label')).toContain('read/write');
```

Also assert policy icons are `aria-hidden="true"` because the complete policy is already present in the button's accessible name.

- [ ] **Step 2: Run the focused test and confirm writable policy is missing**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/source-selector.component.spec.ts"
Pop-Location
```

Expected: FAIL because writable sources do not render an `RW` indicator.

- [ ] **Step 3: Render local lock/unlock SVG plus text**

Replace the read-only-only branch with mutually exclusive policy spans:

```html
@if (source.isReadOnly) {
  <span class="access-policy read-only" data-access="read-only" aria-hidden="true">
    <svg viewBox="0 0 16 16" focusable="false" fill="none" stroke="currentColor">
      <path d="M5 7V5a3 3 0 0 1 6 0v2M4 7.5h8v6H4z" vector-effect="non-scaling-stroke" />
    </svg>
    <b>RO</b>
  </span>
} @else {
  <span class="access-policy writable" data-access="writable" aria-hidden="true">
    <svg viewBox="0 0 16 16" focusable="false" fill="none" stroke="currentColor">
      <path d="M10.5 7V5a2.5 2.5 0 0 0-4.8-1M4 7.5h8v6H4z" vector-effect="non-scaling-stroke" />
    </svg>
    <b>RW</b>
  </span>
}
```

Use real compact SVG `path` data in implementation, `fill="none"`, `stroke="currentColor"`, and `vector-effect="non-scaling-stroke"`. Style `RO` with the neutral/warning palette, `RW` with the success palette, and retain a distinct disabled/unavailable presentation without hiding the policy token.

- [ ] **Step 4: Run focused tests and an accessibility DOM check**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/source-selector.component.spec.ts"
npm test -- --watch=false
Pop-Location
```

Expected: PASS; the accessible description remains the source of spoken policy semantics.

- [ ] **Step 5: Commit the source-policy slice**

```powershell
git status --short
git add client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.html client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.scss client/reach-commander-ui/src/app/features/commander/source-selector/source-selector.component.spec.ts
git commit -m "feat: show source access policy"
```

---

### Task 3: Build the presentation-only active-panel toolbar

**Files:**

- Create: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.html`
- Create: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.scss`
- Create: `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/active-panel-toolbar.component.spec.ts`

**Interfaces:**

```ts
export interface ActivePanelToolbarContext {
  readonly side: PanelSide;
  readonly sourceName: string;
  readonly logicalPath: string;
  readonly available: boolean;
  readonly readOnly: boolean;
  readonly hasRenameTargets: boolean;
  readonly uploadPending: boolean;
}
```

- Inputs: `context`, `filter`.
- Outputs: `renameRequested`, `filesSelected`, `filterChanged`.
- Public focus API: `focusSearch()`, `focusAddFiles()`.
- The component owns only its hidden browser file input and clears its `.value` after every selection so choosing the same files twice emits again.

- [ ] **Step 1: Write failing behavior and accessibility tests**

Cover all of the following:

- context chip visibly contains `LEFT · Media` and has an accessible label including `/incoming`;
- Rename and Add files are enabled only for an available writable source;
- Rename is disabled when no eligible target exists;
- disabled reasons are present in `title` and `aria-describedby` helper text;
- the search uses `type="search"`, label `Search active panel`, and the wildcard hint;
- `filterChanged` emits on input and clear;
- Add files activates one hidden `<input type="file" multiple>` with no `accept` restriction because arbitrary file types are explicitly supported;
- a selection emits a copied `readonly File[]`, clears the native value, and restores focus to Add files when requested;
- the component emits no request when the chosen file list is empty;
- local SVG icons are hidden from assistive technology while buttons retain text/labels.

Use a test host when necessary to bind signal inputs and capture outputs.

- [ ] **Step 2: Run the focused test and confirm the missing component failure**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/active-panel-toolbar.component.spec.ts"
Pop-Location
```

Expected: FAIL because the component does not exist.

- [ ] **Step 3: Implement the standalone component contract**

Use signal inputs/outputs and view queries:

```ts
readonly context = input.required<ActivePanelToolbarContext>();
readonly filter = input.required<string>();
readonly renameRequested = output<void>();
readonly filesSelected = output<readonly File[]>();
readonly filterChanged = output<string>();

@ViewChild('searchInput') private searchInput?: ElementRef<HTMLInputElement>;
@ViewChild('fileInput') private fileInput?: ElementRef<HTMLInputElement>;
@ViewChild('addFilesButton') private addFilesButton?: ElementRef<HTMLButtonElement>;
```

Use explicit helpers for `renameDisabledReason()` and `uploadDisabledReason()` so tests and the template share one policy decision. Do not infer write permission from the source name or path.

- [ ] **Step 4: Implement compact responsive markup and styles**

Required DOM order:

```text
context chip → Multi-Rename → Add files → Search active panel
```

The component must:

- expose real button text at wide widths, visually hidden text at compact widths, and always retain accessible names;
- keep disabled actions discoverable through a wrapper with `tabindex="0"`, `role="group"`, `aria-describedby` pointing at visible-or-visually-hidden reason text, and `title`; remove the wrapper from the tab order when its button is enabled because native disabled buttons do not receive hover/focus;
- make the search `min-width: 8rem`, allow it to shrink with `min-width: 0`, and use a visible clear button only when non-empty;
- use `currentColor` local SVGs for rename, upload, search, and clear;
- avoid absolute positioning so the shell can negotiate space with hardware metrics.

- [ ] **Step 5: Run component and full frontend tests**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/active-panel-toolbar.component.spec.ts"
npm test -- --watch=false
Pop-Location
```

Expected: PASS.

- [ ] **Step 6: Commit the toolbar component slice**

```powershell
git status --short
git add client/reach-commander-ui/src/app/features/commander/active-panel-toolbar
git commit -m "feat: add active panel toolbar"
```

---

### Task 4: Integrate active context, operation stores, search, and keyboard commands

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-command.ts`
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.ts`
- Modify: `client/reach-commander-ui/src/app/core/keyboard/commander-keyboard.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-panel/commander-panel.component.html`
- Delete: `client/reach-commander-ui/src/app/features/commander/quick-filter/quick-filter.component.ts`
- Delete: `client/reach-commander-ui/src/app/features/commander/quick-filter/quick-filter.component.html`
- Delete: `client/reach-commander-ui/src/app/features/commander/quick-filter/quick-filter.component.scss`

**Prerequisite interfaces:**

- Secure-upload plan Tasks 1–6 provide `UploadStore.open(context, files, onCompleted)`, upload dialog rendering, pending state, cancel/close, and opener restoration.
- Multi-Rename plan Tasks 1–9 provide `MultiRenameStore.open(context)` and its dialog lifecycle.
- Both stores receive an immutable operation context. Neither reads `CommanderStore.activePanel()` after opening.

- [ ] **Step 1: Write failing keyboard mapping tests**

Extend the command union with:

```ts
| { readonly type: 'focus-search' }
```

Add the Ctrl+F table case and retain the Multi-Rename plan's existing Ctrl+M case:

```ts
['f', { ctrlKey: true }, { type: 'focus-search' }],
['m', { ctrlKey: true }, { type: 'multi-rename' }],
```

Also prove that Ctrl+F/Ctrl+M inside an input return `null`, Escape inside an input remains application Escape, Alt/Meta combinations are ignored, and mapped application shortcuts call `preventDefault` once.

- [ ] **Step 2: Run keyboard tests and observe the missing commands**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/commander-keyboard.service.spec.ts"
Pop-Location
```

Expected: FAIL until the new commands are mapped.

- [ ] **Step 3: Map Ctrl+F without regressing Ctrl+M**

Add Ctrl+F to the existing Ctrl-only switch and preserve the Multi-Rename plan's Ctrl+M mapping. Preserve the text-control guard, so Ctrl+F only replaces browser Find while focus is in the ReachCommander application surface rather than an editable control.

- [ ] **Step 4: Write failing shell integration tests**

Use store/API substitutions and existing fixture helpers to assert:

- the toolbar renders between the brand block and `.top-actions`, with metrics still last inside `.top-actions`;
- the toolbar context changes immediately when the active side changes;
- independent left/right `PanelState.filter` values appear when switching panels;
- search output calls `CommanderStore.setFilter(activeSide, value)`;
- Ctrl+F calls the toolbar focus API;
- existing Ctrl+M and the Rename button both call the same `openMultiRename()` path, capture side/source/path/selection, and call `MultiRenameStore.open` once;
- Add files captures side/source/path and passes the file list to `UploadStore.open` once;
- switching panels after either call does not mutate the captured context;
- Rename/Add files remain disabled for unavailable/read-only sources;
- a selected `..` parent row is excluded from rename eligibility;
- upload completion refreshes only the captured side;
- Escape priority is metrics details → non-cancellable upload phase → cancellable upload dialog → Multi-Rename → command menu → active search → selection/status;
- the legacy `<app-quick-filter>` is absent from both panel templates.

- [ ] **Step 5: Run the shell tests and observe the missing integration**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/commander-shell.component.spec.ts"
Pop-Location
```

Expected: FAIL because the shell has no toolbar/store wiring.

- [ ] **Step 6: Compute the active toolbar context once in the shell**

Add computed values for the active source and tab, then build the presentation context:

```ts
readonly activeSource = computed(() =>
  this.store.sources().find((source) => source.id === this.activeState().sourceId),
);
readonly activeTab = computed(() =>
  this.activeState().tabs.find((tab) => tab.id === this.activeState().activeTabId),
);
readonly toolbarContext = computed<ActivePanelToolbarContext>(() => ({
  side: this.store.activePanel(),
  sourceName: this.activeSource()?.name ?? 'Source',
  logicalPath: this.activeTab()?.path ?? '/',
  available: this.activeSource()?.isAvailable ?? false,
  readOnly: this.activeSource()?.isReadOnly ?? true,
  hasRenameTargets: this.store.createMultiRenameContext(this.store.activePanel()) !== null,
  uploadPending: this.uploadStore.isPending(),
}));
```

Reuse the Multi-Rename plan's `CommanderStore.createMultiRenameContext(side)` for selection-order/cursor-fallback semantics; never derive targets again from raw entry or Set order.

- [ ] **Step 7: Wire toolbar intents and focus APIs**

Add `@ViewChild(ActivePanelToolbarComponent)` and handlers:

- `setActiveFilter(value)` calls `store.setFilter(store.activePanel(), value)`;
- `openMultiRename()` calls `store.createMultiRenameContext(activeSide)`, returns early with the existing selection message when null or policy-disabled, and otherwise passes that fresh context to `multiRenameStore.open(context)`;
- `reviewUpload(files)` returns early unless source is available/writable and files are non-empty, captures the active side/source/path, and calls `uploadStore.open(context, files, () => store.refresh(capturedSide))`;
- `focusSearch()` queues toolbar focus after Angular rendering;
- successful/closed flows return focus to the matching toolbar button through the already-planned store/dialog hook.

Render upload and rename dialogs at shell level, never inside either panel.

- [ ] **Step 8: Move search out of each panel and restructure the top bar**

Remove `QuickFilterComponent` from `CommanderPanelComponent.imports`, delete `<app-quick-filter>`, and delete its three implementation files. Preserve `PanelState.filter` and all persistence behavior.

Change the top bar to three grid/flex regions:

```html
<div class="brand-block">
  <span class="brand-mark" aria-hidden="true"><i></i><i></i></span>
  <div><strong>ReachCommander</strong><span>DUAL-PANE FILE OPERATIONS</span></div>
</div>
<app-active-panel-toolbar
  [context]="toolbarContext()"
  [filter]="activeState().filter"
  (filterChanged)="setActiveFilter($event)"
  (renameRequested)="openMultiRename()"
  (filesSelected)="reviewUpload($event)"
/>
<div class="top-actions">
  <span class="read-mode"><i></i> CONTROLLED FILE OPERATIONS</span>
  <button type="button" disabled title="Transfers arrive in Milestone 3">Transfers</button>
  <button type="button" disabled title="Settings arrive in a later milestone" aria-label="Settings">⚙</button>
  <app-system-metrics-widget
    [snapshot]="metricsStore.effectiveSnapshot()"
    [effectiveState]="metricsStore.effectiveState() ?? 'loading'"
    [expanded]="metricsOpen()"
    (openDetails)="openMetrics()"
  />
</div>
```

Desktop styling requirements:

- `.topbar { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; }`;
- toolbar left-aligns inside its flexible track;
- `.top-actions` stays right-aligned and metrics stays visible;
- at the current 900px breakpoint, stack panes as today but keep a one-row topbar;
- at compact widths, hide decorative brand copy and toolbar button text before hiding any functional control;
- maintain the current declared `min-width: 680px` unless visual testing proves a smaller supported minimum; do not introduce horizontal overflow above that minimum.

- [ ] **Step 9: Run focused and complete frontend verification**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include="**/commander-keyboard.service.spec.ts" --include="**/commander-shell.component.spec.ts" --include="**/active-panel-toolbar.component.spec.ts"
npm test -- --watch=false
npm run build
Pop-Location
```

Expected: PASS with no missing standalone imports or budget regressions.

- [ ] **Step 10: Commit the integrated toolbar slice**

```powershell
git status --short
git add client/reach-commander-ui/src/app/core/keyboard client/reach-commander-ui/src/app/features/commander/active-panel-toolbar client/reach-commander-ui/src/app/features/commander/commander-shell client/reach-commander-ui/src/app/features/commander/commander-panel client/reach-commander-ui/src/app/features/commander/quick-filter
git commit -m "feat: integrate active panel tools"
```

---

### Task 5: Browser acceptance, documentation, visual QA, and release verification

**Files:**

- Create: `tests/e2e/specs/active-toolbar.spec.ts`
- Modify: existing E2E specs that locate `Quick filter`
- Modify: `README.md`

**Fixtures:**

- Reuse the secure-upload plan fixture: one explicitly writable temporary `downloads` source and one read-only `media` source.
- Reuse Multi-Rename fixtures and reset helpers. Tests must never point at personal or production folders.

- [ ] **Step 1: Write failing browser acceptance tests**

Add scenarios that prove:

1. toolbar context initially names the active left side/source/path;
2. activating the right panel changes the context and restores that panel's independent filter;
3. `*.txt`, `report-??.pdf`, a literal `a+b[1].txt`, clear, typing, Backspace, Escape, and Ctrl+F behave as specified;
4. each source shows `RO` or `RW`, and unavailable source semantics remain accessible;
5. Add files is disabled with an explanatory reason for read-only `media`;
6. switching panels while upload review is open does not redirect its captured destination, then cancellation returns focus to Add files;
7. Multi-Rename opens from the button and Ctrl+M with the correct captured context, then Close restores focus;
8. toolbar actions remain keyboard operable and metrics details still open/close at desktop and compact viewports.

The upload plan's `upload.spec.ts` owns success/conflict execution coverage. The Multi-Rename plan's `multi-rename.spec.ts` owns preview/execute/Undo coverage. This file tests the shared contextual toolbar rather than duplicating those expensive workflows.

Use Playwright `setInputFiles` with in-memory buffers for small upload fixtures. Do not commit binary fixtures.

- [ ] **Step 2: Run the new E2E file and confirm missing behavior**

```powershell
Push-Location tests/e2e
npx playwright test specs/active-toolbar.spec.ts
Pop-Location
```

Expected: FAIL before the integrated toolbar is implemented.

- [ ] **Step 3: Update legacy E2E search locators**

Replace per-panel `Quick filter` locators with the single `Search active panel` locator. Explicitly activate the intended panel before each assertion so the test documents contextual behavior.

- [ ] **Step 4: Document operations and deployment boundaries**

Update `README.md` with:

- toolbar context and active-panel capture semantics;
- wildcard examples and literal-vs-wildcard behavior;
- Ctrl+F and Ctrl+M shortcuts;
- `RO`, `RW`, and unavailable meanings;
- Add files defaults, conflict rejection, progress/cancellation, and arbitrary-file-type policy;
- the requirement for both `readOnly: false` and actual host/container write permissions;
- a narrowly scoped writable source example while preserving checked-in Docker/source defaults as read-only;
- links to the Multi-Rename and secure-upload operational behavior.

- [ ] **Step 5: Run the complete verification matrix**

```powershell
dotnet restore ReachCommander.slnx
dotnet test ReachCommander.slnx -c Release --no-restore
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -p:BuildAngularOnPublish=false
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
Push-Location tests/e2e
npm test
Pop-Location
rg -n "app-quick-filter|Quick filter|new RegExp" client/reach-commander-ui/src/app tests/e2e
rg -n "IFormFile|ReadToEnd|MemoryStream|File\.WriteAll|overwrite:\s*true|Process\.Start|cmd\.exe|powershell|/bin/sh" src tests client/reach-commander-ui/src/app
git diff --check
git status --short
```

Expected:

- every suite, build, and publish passes;
- `new RegExp` occurs only inside `wildcard-filter.ts` and its tests;
- no legacy quick-filter component/reference remains;
- no full-file buffering, overwrite helper, or execution helper appears in upload production code;
- only planned files are modified.

- [ ] **Step 6: Perform visual and keyboard QA**

Run the production-like app and inspect at minimum 1440×900, 1024×768, and the supported 680px minimum. Capture screenshots for review, then verify:

- no toolbar/metrics overlap or page-level horizontal overflow;
- hardware metrics remain at the far right;
- context/source/action/search hierarchy matches the approved design;
- long source names and long logical paths truncate visually but remain available in title/accessibility text;
- focus order is brand skip target if present → toolbar context/actions/search → metrics → panels;
- visible focus, disabled explanations, live announcements, dialogs, Escape, and focus restoration work by keyboard only;
- `RO`, `RW`, unavailable, progress, error, and selection states do not rely on color alone.

- [ ] **Step 7: Validate Docker behavior when Docker is available**

Build/run the hardened checked-in configuration and prove Add files/Multi-Rename are disabled or server-rejected. Then run only a temporary explicit writable-source override and prove upload/rename succeed. If Docker is unavailable, record that limitation without claiming Compose success.

- [ ] **Step 8: Commit the acceptance slice**

```powershell
git status --short
git add README.md tests/e2e/specs/active-toolbar.spec.ts tests/e2e
git commit -m "docs: verify active panel operations"
git status --short
```

---

## Final Acceptance Checklist

- [ ] One left-aligned toolbar shares the top-bar level with right-aligned hardware monitoring.
- [ ] Context always reflects the active panel; opened operations retain immutable destination context.
- [ ] Multi-Rename works from the toolbar and Ctrl+M with preview/execute/Undo behavior from its approved plan.
- [ ] Add files streams bounded batches to explicit writable sources, rejects the entire batch on conflicts, and refreshes only the captured panel.
- [ ] Search supports `*` and `?`, treats all other characters literally, preserves substring behavior without wildcards, and keeps independent panel filters.
- [ ] Ctrl+F focuses search without breaking normal editing inside text controls.
- [ ] Every source exposes accessible RO/RW/unavailable semantics.
- [ ] Existing read-only defaults and hardware telemetry behavior remain intact.
- [ ] Unit, integration, frontend, E2E, build, publish, visual, keyboard, and security checks pass.
