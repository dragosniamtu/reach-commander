# ReachCommander Windows 95 Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an accessible, persistent Windows 95-inspired theme and replace the binary Norton toggle with an explicit Modern/Norton/Windows 95 selector.

**Architecture:** Extend the root Angular `ThemeService` from two validated values to three and keep applying alternate themes through the root `data-theme` attribute. Replace the toolbar toggle with a native selector, then express Windows 95 presentation through existing CSS tokens plus narrowly scoped global bevel/title/selection overrides. No API, backend, account, or deployment state changes.

**Tech Stack:** Angular 22 signals and standalone components, SCSS custom properties, Vitest, Playwright, PWA production build.

## Global Constraints

- Work directly on `master`; do not create a worktree.
- Preserve the untracked `NC-theme.png`; never modify, stage, or remove it.
- Use `reachcommander.theme.v1`; Modern removes the key, while `norton` and `windows95` store their exact value.
- Accept only `default | norton | windows95`; unknown or inaccessible storage restores Modern.
- Do not add Microsoft artwork, fonts, icons, sounds, logos, or binary assets.
- Preserve all file-operation behavior, 30 px rows, ellipsis/tooltips, authentication, PWA, compact layouts, keyboard behavior, focus visibility, and reduced-motion behavior.
- Do not push unless the user explicitly asks.

---

### Task 1: Three-state theme model and persistence

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/theme/theme.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/theme/theme.service.ts`
- Modify: `client/reach-commander-ui/src/app/app.spec.ts`

**Interfaces:**
- Consumes: `THEME_STORAGE`, root `DOCUMENT`, `reachcommander.theme.v1`.
- Produces: `ReachCommanderTheme = 'default' | 'norton' | 'windows95'`, `theme`, `isNorton`, `isWindows95`, and `setTheme(theme)`.

- [ ] **Step 1: Write failing state and persistence tests**

Add tests that restore `windows95`, expose `isWindows95`, persist both alternate values, remove storage for Modern, reject `solarized`, and continue changing the root attribute when storage throws. Use direct expectations:

```ts
storage.setItem(ThemeService.storageKey, 'windows95');
const service = TestBed.inject(ThemeService);
expect(service.theme()).toBe('windows95');
expect(service.isWindows95()).toBe(true);
expect(document.documentElement.dataset['theme']).toBe('windows95');
```

Extend the root application test to render an anonymous screen with stored `windows95` and assert the root attribute is already present.

- [ ] **Step 2: Run the focused tests and verify RED**

Run from `client/reach-commander-ui`:

```powershell
npx ng test --watch=false --include=src/app/core/theme/theme.service.spec.ts --include=src/app/app.spec.ts
```

Expected: FAIL because `windows95` and `isWindows95` are not part of the current service.

- [ ] **Step 3: Implement the minimal three-state model**

Use a strict allowlist and one apply path:

```ts
export type ReachCommanderTheme = 'default' | 'norton' | 'windows95';

const storedThemes = new Set<ReachCommanderTheme>(['norton', 'windows95']);

readonly isNorton = computed(() => this.theme() === 'norton');
readonly isWindows95 = computed(() => this.theme() === 'windows95');

setTheme(theme: ReachCommanderTheme): void {
  this.apply(theme);
  try {
    if (theme === 'default') this.storage.removeItem(ThemeService.storageKey);
    else this.storage.setItem(ThemeService.storageKey, theme);
  } catch {}
}
```

`readPreference` returns the stored alternate only when it is exactly `norton` or `windows95`; `apply` removes `data-theme` only for Modern and otherwise assigns the exact value. Remove the obsolete binary `toggle()` method.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Step 2 command. Expected: all focused tests PASS with no warnings.

- [ ] **Step 5: Commit the state slice**

```powershell
git add -- client/reach-commander-ui/src/app/core/theme/theme.service.ts client/reach-commander-ui/src/app/core/theme/theme.service.spec.ts client/reach-commander-ui/src/app/app.spec.ts
git commit -m "feat: support three persistent themes"
```

### Task 2: Accessible toolbar theme selector

**Files:**
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/styles.scss`

**Interfaces:**
- Consumes: Task 1 `ThemeService.setTheme` and readonly `theme` signal.
- Produces: `selectTheme(event: Event): void` and `[data-testid="theme-selector"]` with three options.

- [ ] **Step 1: Write the failing selector component test**

Assert the native selector exists, has accessible name `Theme`, exposes exact option values and labels, reflects the current theme, and changes the root theme:

```ts
const selector = fixture.nativeElement.querySelector(
  '[data-testid="theme-selector"]',
) as HTMLSelectElement;
expect([...selector.options].map(({ value, text }) => [value, text])).toEqual([
  ['default', 'Modern'],
  ['norton', 'Norton'],
  ['windows95', 'Windows 95'],
]);
selector.value = 'windows95';
selector.dispatchEvent(new Event('change'));
expect(document.documentElement.dataset['theme']).toBe('windows95');
```

- [ ] **Step 2: Run the selector test and verify RED**

```powershell
npx ng test --watch=false --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: FAIL because only `norton-theme-toggle` exists.

- [ ] **Step 3: Replace the toggle with a selector**

Render one labeled control in the existing toolbar position:

```html
<label class="theme-selector" title="Choose interface theme">
  <span>Theme</span>
  <select
    data-testid="theme-selector"
    aria-label="Theme"
    [value]="theme.theme()"
    (change)="selectTheme($event)"
  >
    <option value="default">Modern</option>
    <option value="norton">Norton</option>
    <option value="windows95">Windows 95</option>
  </select>
</label>
```

Implement `selectTheme` with an exact runtime check before calling the service. Replace `.theme-toggle` responsive exclusions and global styles with `.theme-selector`; keep the selector visible at compact widths and hide only its external label.

- [ ] **Step 4: Run the selector test and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the selector slice**

```powershell
git add -- client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts client/reach-commander-ui/src/styles.scss
git commit -m "feat: add toolbar theme selector"
```

### Task 3: Windows 95 visual system and browser acceptance

**Files:**
- Modify: `client/reach-commander-ui/src/styles.scss`
- Create: `tests/e2e/specs/windows95-theme.spec.ts`
- Modify: `tests/e2e/specs/norton-theme.spec.ts`
- Modify: `tests/e2e/specs/file-row-layout.spec.ts`
- Modify: `tests/e2e/specs/system-update.spec.ts`

**Interfaces:**
- Consumes: root `data-theme="windows95"`, selector hook from Task 2, existing CSS variables and component class names.
- Produces: Windows 95 token overrides and stable browser acceptance at desktop and 680 px.

- [ ] **Step 1: Write failing Playwright theme scenarios**

Create tests that choose `Windows 95` through the selector and assert:

```ts
await page.getByTestId('theme-selector').selectOption('windows95');
await expect(page.locator('html')).toHaveAttribute('data-theme', 'windows95');
expect(await page.locator('html').evaluate((root) => {
  const styles = getComputedStyle(root);
  return {
    appBackground: styles.getPropertyValue('--app-bg').trim(),
    surface: styles.getPropertyValue('--surface-1').trim(),
    title: styles.getPropertyValue('--title-bar').trim(),
    selection: styles.getPropertyValue('--selection').trim(),
  };
})).toEqual({
  appBackground: '#008080',
  surface: '#c0c0c0',
  title: '#000080',
  selection: '#000080',
});
```

Also assert a toolbar button has square corners and a raised bevel, the selected file row is navy/white, reload preserves `windows95`, the authentication screen uses the gray surface, compact width has both panes and at most one pixel horizontal overflow, and no console errors occur.

Update Norton, file-row, and system-update tests to select `norton` through `theme-selector` instead of clicking the removed toggle.

- [ ] **Step 2: Run focused Playwright and verify RED**

From `tests/e2e`:

```powershell
npx playwright test specs/windows95-theme.spec.ts specs/norton-theme.spec.ts specs/file-row-layout.spec.ts specs/system-update.spec.ts --project=chromium --workers=1
```

Expected: Windows 95 assertions FAIL because the token block and scoped styling do not exist.

- [ ] **Step 3: Add Windows 95 tokens and scoped presentation**

Add `:root[data-theme='windows95']` with:

```scss
--font-ui: Tahoma, 'MS Sans Serif', 'Segoe UI', system-ui, sans-serif;
--app-bg: #008080;
--surface-1: #c0c0c0;
--surface-2: #c0c0c0;
--surface-3: #dfdfdf;
--surface-4: #a0a0a0;
--title-bar: #000080;
--line: #808080;
--line-strong: #000000;
--text-1: #000000;
--text-2: #202020;
--text-3: #404040;
--text-4: #606060;
--accent: #000080;
--accent-text: #ffffff;
--selection: #000080;
--selection-text: #ffffff;
--shadow: none;
```

Use scoped selectors to remove radii/gradients, construct raised borders (`#fff #000 #000 #fff` with inner `#dfdfdf #808080 #808080 #dfdfdf`), construct pressed/inset states with reversed edges, make headings/title regions navy and white, keep dialogs gray over a non-blurred backdrop, render active panes with a black outline, and render the command bar as gray raised controls. Keep semantic warning/danger/success tokens distinct.

- [ ] **Step 4: Run focused browser tests and verify GREEN**

Run the Step 2 command. Expected: all focused scenarios PASS.

- [ ] **Step 5: Run visual checks without opening a browser UI**

Inspect Playwright screenshots saved beneath `artifacts/playwright-results` and verify desktop and compact layouts show teal background, gray chrome, navy title/selection, readable text, visible focus, square controls, and no overlap. Do not open an interactive browser window.

- [ ] **Step 6: Commit the visual slice**

```powershell
git add -- client/reach-commander-ui/src/styles.scss tests/e2e/specs/windows95-theme.spec.ts tests/e2e/specs/norton-theme.spec.ts tests/e2e/specs/file-row-layout.spec.ts tests/e2e/specs/system-update.spec.ts
git commit -m "feat: add Windows 95 visual theme"
```

### Task 4: Documentation and complete release verification

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: all prior tasks.
- Produces: public usage guidance and final release evidence.

- [ ] **Step 1: Document the three theme choices**

Replace the Norton-only feature text with a `Themes` section explaining the Modern, Norton, and Windows 95 choices, browser/PWA-only persistence, and original CSS-inspired treatment without Microsoft assets.

- [ ] **Step 2: Format and run the complete Angular gates**

```powershell
npx prettier --write "src/**/*.{ts,html,scss}" "../../../tests/e2e/specs/{norton-theme,windows95-theme,file-row-layout,system-update}.spec.ts"
npm test -- --watch=false
npm run test:pwa
npm run build
npm run verify:pwa
```

Expected: all Angular and PWA tests PASS; production build succeeds without warnings.

- [ ] **Step 3: Run complete browser acceptance**

From `tests/e2e`:

```powershell
npm test -- --workers=1
```

Expected: every Chromium scenario PASS, including existing Norton and new Windows 95 coverage.

- [ ] **Step 4: Review scope and commit**

```powershell
git diff --check
git status --short
git add -- README.md
git commit -m "docs: document interface themes"
```

Expected: only intended theme/docs files are committed; `NC-theme.png` remains untracked and untouched.

- [ ] **Step 5: Report completion without pushing**

Report commit hashes, test counts, screenshots inspected, and the remaining untracked `NC-theme.png`. Do not push.
