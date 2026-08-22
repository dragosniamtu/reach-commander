# ReachCommander Norton Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent, accessible Norton Commander-inspired visual theme that users can activate or deactivate from ReachCommander's top toolbar.

**Architecture:** A root-provided Angular service owns the `default | norton` preference, safely persists it in browser storage, and applies a `data-theme` attribute to `<html>`. The existing CSS-token system supplies the palette change, with narrowly scoped global overrides for the square, flat DOS-like treatment; the commander shell only renders and invokes the toggle.

**Tech Stack:** Angular 22 standalone components, Angular Signals, TypeScript 6, SCSS custom properties, Vitest, Playwright Chromium, browser `localStorage`.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Keep the existing visual mode as the default and support exactly `default | norton`.
- Store only the exact value `norton` under `reachcommander.theme.v1`; remove the key for the default theme.
- Do not add server state, API changes, account synchronization, database storage, or dependencies.
- Apply a saved theme at the application root so authentication, the commander shell, dialogs, and the installed PWA use one preference.
- Preserve all keyboard behavior, focus visibility, supported breakpoints, minimum viewport width, and file-operation behavior.
- Use the supplied screenshot as visual inspiration only; do not embed it as an application asset.
- Use test-driven development: every behavior change begins with a failing focused test.

## File Map

- Create `client/reach-commander-ui/src/app/core/theme/theme.service.ts`: validate, apply, and persist the global theme.
- Create `client/reach-commander-ui/src/app/core/theme/theme.service.spec.ts`: unit-test restoration, toggling, invalid values, and storage failures.
- Modify `client/reach-commander-ui/src/app/app.ts`: construct the theme service at the root before authenticated content is selected.
- Modify `client/reach-commander-ui/src/app/app.spec.ts`: prove a saved theme is initialized even on an unauthenticated screen.
- Modify `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`: expose theme state and toggle behavior to the shell template.
- Modify `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`: add the accessible top-toolbar toggle.
- Modify `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`: style the toggle and keep it available at compact widths.
- Modify `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`: test placement, accessible state, and activation.
- Modify `client/reach-commander-ui/src/styles.scss`: define Norton palette tokens and the small set of global flat/square overrides.
- Create `tests/e2e/specs/norton-theme.spec.ts`: verify computed styling, persistence, deactivation, accessibility, and compact layout.
- Modify `README.md`: document the theme alongside existing UI capabilities.

---

### Task 1: Root theme state and persistence

**Files:**
- Create: `client/reach-commander-ui/src/app/core/theme/theme.service.ts`
- Create: `client/reach-commander-ui/src/app/core/theme/theme.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/app.ts`
- Modify: `client/reach-commander-ui/src/app/app.spec.ts`

**Interfaces:**
- Produces: `type ReachCommanderTheme = 'default' | 'norton'`.
- Produces: `THEME_STORAGE: InjectionToken<Storage>` for deterministic tests.
- Produces: `ThemeService.theme: Signal<ReachCommanderTheme>`, `ThemeService.isNorton: Signal<boolean>`, `ThemeService.setTheme(theme): void`, and `ThemeService.toggle(): void`.
- Consumes: Angular `DOCUMENT`, Signals, dependency injection, and browser `Storage`.

- [ ] **Step 1: Write the failing service tests**

Create `client/reach-commander-ui/src/app/core/theme/theme.service.spec.ts`:

```ts
import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { THEME_STORAGE, ThemeService } from './theme.service';

describe('ThemeService', () => {
  let document: Document;
  let storage: Storage;

  beforeEach(() => {
    storage = memoryStorage();
    TestBed.configureTestingModule({
      providers: [{ provide: THEME_STORAGE, useValue: storage }],
    });
    document = TestBed.inject(DOCUMENT);
    document.documentElement.removeAttribute('data-theme');
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  it('defaults safely without a stored override', () => {
    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('default');
    expect(service.isNorton()).toBe(false);
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('restores only the valid Norton value', () => {
    storage.setItem(ThemeService.storageKey, 'norton');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('norton');
    expect(document.documentElement.dataset['theme']).toBe('norton');
  });

  it('ignores an unrecognized stored value', () => {
    storage.setItem(ThemeService.storageKey, 'solarized');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('default');
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('toggles, persists Norton, and removes the default override', () => {
    const service = TestBed.inject(ThemeService);

    service.toggle();
    expect(service.theme()).toBe('norton');
    expect(storage.getItem(ThemeService.storageKey)).toBe('norton');
    expect(document.documentElement.dataset['theme']).toBe('norton');

    service.toggle();
    expect(service.theme()).toBe('default');
    expect(storage.getItem(ThemeService.storageKey)).toBeNull();
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('still applies in-memory state when storage access fails', () => {
    const unavailableStorage = memoryStorage();
    vi.spyOn(unavailableStorage, 'getItem').mockImplementation(() => {
      throw new DOMException('Blocked');
    });
    vi.spyOn(unavailableStorage, 'setItem').mockImplementation(() => {
      throw new DOMException('Blocked');
    });
    vi.spyOn(unavailableStorage, 'removeItem').mockImplementation(() => {
      throw new DOMException('Blocked');
    });
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: THEME_STORAGE, useValue: unavailableStorage }],
    });
    document = TestBed.inject(DOCUMENT);

    const service = TestBed.inject(ThemeService);
    service.setTheme('norton');
    expect(service.isNorton()).toBe(true);
    expect(document.documentElement.dataset['theme']).toBe('norton');

    service.setTheme('default');
    expect(service.isNorton()).toBe(false);
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });
});

function memoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() { return values.size; },
    clear: () => values.clear(),
    getItem: (key) => values.get(key) ?? null,
    key: (index) => [...values.keys()][index] ?? null,
    removeItem: (key) => { values.delete(key); },
    setItem: (key, value) => { values.set(key, value); },
  };
}
```

- [ ] **Step 2: Run the service tests and confirm the red state**

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false --include src/app/core/theme/theme.service.spec.ts
```

Expected: FAIL because `./theme.service` does not exist.

- [ ] **Step 3: Implement the minimal service**

Create `client/reach-commander-ui/src/app/core/theme/theme.service.ts`:

```ts
import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';

export type ReachCommanderTheme = 'default' | 'norton';

export const THEME_STORAGE = new InjectionToken<Storage>('ReachCommander theme storage', {
  providedIn: 'root',
  factory: () => localStorage,
});

@Injectable({ providedIn: 'root' })
export class ThemeService {
  static readonly storageKey = 'reachcommander.theme.v1';

  private readonly document = inject(DOCUMENT);
  private readonly storage = inject(THEME_STORAGE);
  private readonly mutableTheme = signal<ReachCommanderTheme>('default');

  readonly theme = this.mutableTheme.asReadonly();
  readonly isNorton = computed(() => this.theme() === 'norton');

  constructor() {
    this.apply(this.readPreference());
  }

  toggle(): void {
    this.setTheme(this.isNorton() ? 'default' : 'norton');
  }

  setTheme(theme: ReachCommanderTheme): void {
    this.apply(theme);
    try {
      if (theme === 'norton') {
        this.storage.setItem(ThemeService.storageKey, theme);
      } else {
        this.storage.removeItem(ThemeService.storageKey);
      }
    } catch {
      // A disabled or full browser store must not prevent a visual preference change.
    }
  }

  private readPreference(): ReachCommanderTheme {
    try {
      return this.storage.getItem(ThemeService.storageKey) === 'norton' ? 'norton' : 'default';
    } catch {
      return 'default';
    }
  }

  private apply(theme: ReachCommanderTheme): void {
    this.mutableTheme.set(theme);
    if (theme === 'norton') {
      this.document.documentElement.dataset['theme'] = theme;
    } else {
      this.document.documentElement.removeAttribute('data-theme');
    }
  }
}
```

- [ ] **Step 4: Run the focused service tests and confirm green**

Run:

```powershell
npm test -- --watch=false --include src/app/core/theme/theme.service.spec.ts
```

Expected: 5 tests PASS.

- [ ] **Step 5: Write the failing application-root initialization test**

In `client/reach-commander-ui/src/app/app.spec.ts`, insert the second line below immediately after the existing `localStorage.clear()` call, then add the `afterEach` and test:

```ts
beforeEach(async () => {
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});

afterEach(() => document.documentElement.removeAttribute('data-theme'));

it('initializes a saved theme before rendering an unauthenticated screen', async () => {
  localStorage.setItem('reachcommander.theme.v1', 'norton');
  auth.setState(authState({ phase: 'anonymous' }));

  const fixture = TestBed.createComponent(App);
  fixture.detectChanges();
  await fixture.whenStable();

  expect(document.documentElement.dataset['theme']).toBe('norton');
  expect(fixture.nativeElement.querySelector('app-authentication-screen')).not.toBeNull();
});
```

- [ ] **Step 6: Run the application test and confirm the root-initialization failure**

Run:

```powershell
npm test -- --watch=false --include src/app/app.spec.ts
```

Expected: the new test FAILS because creating `App` does not construct `ThemeService`.

- [ ] **Step 7: Initialize the service from `App`**

In `client/reach-commander-ui/src/app/app.ts`, import and inject the service:

```ts
import { ThemeService } from './core/theme/theme.service';

export class App implements OnInit {
  readonly auth = inject(AuthenticationStore);
  readonly theme = inject(ThemeService);

  ngOnInit(): void {
    void this.auth.initialize();
  }
}
```

The field initializer is intentional: constructing the root component initializes the theme before the template chooses the authentication or commander screen.

- [ ] **Step 8: Run both focused suites**

Run:

```powershell
npm test -- --watch=false --include src/app/core/theme/theme.service.spec.ts --include src/app/app.spec.ts
```

Expected: 12 tests PASS.

- [ ] **Step 9: Commit the state layer**

```powershell
git add client/reach-commander-ui/src/app/core/theme client/reach-commander-ui/src/app/app.ts client/reach-commander-ui/src/app/app.spec.ts
git commit -m "feat: add persistent UI theme state"
```

---

### Task 2: Accessible top-toolbar toggle

**Files:**
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Consumes: `ThemeService.isNorton(): boolean` and `ThemeService.toggle(): void` from Task 1.
- Produces: `[data-testid="norton-theme-toggle"]`, dynamic `aria-pressed`, and dynamic activate/deactivate accessible copy.

- [ ] **Step 1: Write the failing shell integration test**

In `commander-shell.component.spec.ts`, import `ThemeService` and add this mutable theme stub beside the existing test doubles:

```ts
import { ThemeService } from '../../../core/theme/theme.service';

const nortonActive = signal(false);
const theme = {
  isNorton: nortonActive.asReadonly(),
  toggle: vi.fn(() => nortonActive.update((active) => !active)),
};
```

Insert `nortonActive.set(false);` immediately after the existing `vi.clearAllMocks();` call. Add `{ provide: ThemeService, useValue: theme }` to the existing TestBed `providers` array. Then add this test:

```ts
it('places an accessible persistent-theme toggle before account and metrics controls', () => {
  const actions = fixture.nativeElement.querySelector('.top-actions') as HTMLElement;
  const button = actions.querySelector(
    '[data-testid="norton-theme-toggle"]',
  ) as HTMLButtonElement;

  expect(button).not.toBeNull();
  expect(button.getAttribute('aria-pressed')).toBe('false');
  expect(button.getAttribute('aria-label')).toBe('Activate Norton theme');
  expect(button.nextElementSibling?.tagName).toBe('APP-ACCOUNT-MENU');

  button.click();
  fixture.detectChanges();

  expect(theme.toggle).toHaveBeenCalledOnce();
  expect(button.getAttribute('aria-pressed')).toBe('true');
  expect(button.getAttribute('aria-label')).toBe('Deactivate Norton theme');
});
```

- [ ] **Step 2: Run the shell test and confirm the missing-toggle failure**

Run:

```powershell
npm test -- --watch=false --include src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: FAIL because `[data-testid="norton-theme-toggle"]` is absent.

- [ ] **Step 3: Expose theme state from the shell**

In `commander-shell.component.ts`, add:

```ts
import { ThemeService } from '../../../core/theme/theme.service';

export class CommanderShellComponent implements OnInit {
  readonly theme = inject(ThemeService);
}
```

- [ ] **Step 4: Add the toggle before the account menu**

In `commander-shell.component.html`, insert this button immediately before `<app-account-menu />`:

```html
<button
  type="button"
  class="theme-toggle"
  data-testid="norton-theme-toggle"
  [class.active]="theme.isNorton()"
  [attr.aria-pressed]="theme.isNorton()"
  [attr.aria-label]="theme.isNorton() ? 'Deactivate Norton theme' : 'Activate Norton theme'"
  [title]="theme.isNorton() ? 'Deactivate Norton theme' : 'Activate Norton theme'"
  (click)="theme.toggle()"
>
  <span class="theme-icon" aria-hidden="true">C:\&gt;</span>
  <span class="theme-label">Norton</span>
</button>
```

- [ ] **Step 5: Style the control and preserve it at compact widths**

Add to `commander-shell.component.scss` after the existing `.install-app` rules:

```scss
.top-actions .theme-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text-2);
  cursor: pointer;
}

.top-actions .theme-toggle:hover,
.top-actions .theme-toggle.active {
  border-color: var(--accent);
  color: var(--accent-text);
  background: var(--accent-dim);
}

.theme-icon {
  color: var(--accent);
  font: 700 9px/1 var(--font-mono);
}

.theme-label {
  font: 700 9px/1 var(--font-ui);
}
```

Replace the existing compact selector:

```scss
@media (max-width: 760px) {
  .brand-block > div {
    display: none;
  }
  .top-actions > button:not(.install-app):not(.theme-toggle) {
    display: none;
  }
  .theme-label {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
    white-space: nowrap;
  }
  .top-actions .theme-toggle {
    width: 31px;
    padding: 0;
  }
}
```

- [ ] **Step 6: Run the focused shell suite**

Run:

```powershell
npm test -- --watch=false --include src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: all commander-shell tests PASS, including the new toggle integration test and the existing account/metrics ordering test.

- [ ] **Step 7: Commit the toolbar control**

```powershell
git add client/reach-commander-ui/src/app/features/commander/commander-shell
git commit -m "feat: add Norton theme toolbar toggle"
```

---

### Task 3: Norton visual system and browser acceptance

**Files:**
- Modify: `client/reach-commander-ui/src/styles.scss`
- Create: `tests/e2e/specs/norton-theme.spec.ts`

**Interfaces:**
- Consumes: `<html data-theme="norton">` and `[data-testid="norton-theme-toggle"]` from Tasks 1 and 2.
- Produces: stable custom properties `--app-bg: #000080`, `--surface-1: #0000aa`, `--line-strong: #00ffff`, and `--selection: #55ffff` while Norton mode is active.

- [ ] **Step 1: Write the failing browser scenarios**

Create `tests/e2e/specs/norton-theme.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

const storageKey = 'reachcommander.theme.v1';

test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.evaluate((key) => localStorage.removeItem(key), storageKey);
  await page.reload();
});

test('activates, persists, and deactivates the Norton theme', async ({ page }) => {
  const root = page.locator('html');
  const toggle = page.getByTestId('norton-theme-toggle');

  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await toggle.click();

  await expect(root).toHaveAttribute('data-theme', 'norton');
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(toggle).toHaveAccessibleName('Deactivate Norton theme');
  expect(await root.evaluate((element) => {
    const styles = getComputedStyle(element);
    return {
      appBackground: styles.getPropertyValue('--app-bg').trim(),
      panelBackground: styles.getPropertyValue('--surface-1').trim(),
      frame: styles.getPropertyValue('--line-strong').trim(),
      selection: styles.getPropertyValue('--selection').trim(),
    };
  })).toEqual({
    appBackground: '#000080',
    panelBackground: '#0000aa',
    frame: '#00ffff',
    selection: '#55ffff',
  });

  await page.reload();
  await expect(root).toHaveAttribute('data-theme', 'norton');
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  expect(await page.evaluate((key) => localStorage.getItem(key), storageKey)).toBe('norton');

  await toggle.click();
  await expect(root).not.toHaveAttribute('data-theme', 'norton');
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  expect(await page.evaluate((key) => localStorage.getItem(key), storageKey)).toBeNull();

  await page.reload();
  await expect(root).not.toHaveAttribute('data-theme', 'norton');
});

test('keeps the Norton toggle and dual-pane shell usable at compact width', async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 680, height: 800 });
  await page.reload();

  const toggle = page.getByTestId('norton-theme-toggle');
  await expect(toggle).toBeVisible();
  await toggle.click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'norton');
  await expect(page.getByTestId('left-panel')).toBeVisible();
  await expect(page.getByTestId('right-panel')).toBeVisible();

  expect(await page.evaluate(
    () => document.documentElement.scrollWidth - window.innerWidth,
  )).toBeLessThanOrEqual(1);
  await page.screenshot({
    path: testInfo.outputPath('norton-theme-680.png'),
    fullPage: true,
  });
});
```

- [ ] **Step 2: Run the new browser spec and confirm the missing-style failure**

After building the current frontend, run from `tests/e2e`:

```powershell
npx playwright test specs/norton-theme.spec.ts --project=chromium
```

Expected: the activation flow reaches `data-theme="norton"`, then FAILS because the expected Norton custom-property values are not defined.

- [ ] **Step 3: Add the Norton token palette after the light-scheme media block**

Append this block to `client/reach-commander-ui/src/styles.scss` so it wins over the operating-system light preference:

```scss
:root[data-theme='norton'] {
  color-scheme: dark;
  --font-ui: 'Cascadia Mono', Consolas, 'Courier New', monospace;
  --font-mono: 'Cascadia Mono', Consolas, 'Courier New', monospace;
  --app-bg: #000080;
  --surface-1: #0000aa;
  --surface-2: #000080;
  --surface-3: #0000aa;
  --surface-4: #0018c8;
  --line: #00a8c8;
  --line-strong: #00ffff;
  --text-1: #ffffff;
  --text-2: #7fffff;
  --text-3: #55ffff;
  --text-4: #00b8d4;
  --accent: #00ffff;
  --accent-soft: #ffffff;
  --accent-text: #ffff55;
  --accent-dim: rgb(0 255 255 / 0.18);
  --focus-ring: rgb(255 255 255 / 0.42);
  --success: #55ff55;
  --warning: #ffff55;
  --danger: #ff5555;
  --danger-text: #ffaaaa;
  --danger-dim: rgb(255 85 85 / 0.2);
  --folder: #55ffff;
  --file: #ffffff;
  --row-hover: #0018c8;
  --selection: #55ffff;
  --selection-text: #000080;
  --shadow: none;
}
```

- [ ] **Step 4: Add the flat DOS-like component treatment**

Append immediately after the token block:

```scss
:root[data-theme='norton'] body {
  font-family: var(--font-mono);
}

:root[data-theme='norton'] :is(.app-shell, .auth-shell) {
  background: var(--app-bg) !important;
}

:root[data-theme='norton'] :is(
  button,
  input,
  select,
  progress,
  .brand-mark,
  .context-chip,
  .action-wrapper,
  .search-box,
  .source-button,
  .tab-shell,
  .path-display,
  .path-input,
  .mode,
  .panel,
  .loading,
  .command-menu,
  .metrics-trigger,
  .metrics-panel,
  .auth-card,
  .password-dialog,
  .rename-workspace,
  .upload-panel,
  .extraction-dialog
) {
  border-radius: 0 !important;
  box-shadow: none !important;
}

:root[data-theme='norton'] :is(
  .topbar,
  .panel-heading,
  .global-status,
  .command-bar,
  .auth-brand,
  .workspace-header,
  .dialog-header,
  .dialog-footer
) {
  background: var(--surface-2) !important;
  box-shadow: none !important;
}

:root[data-theme='norton'] :is(
  .dialog-backdrop,
  .rename-backdrop,
  .upload-backdrop,
  .extraction-backdrop,
  .metrics-backdrop
) {
  background: rgb(0 0 64 / 0.92) !important;
  backdrop-filter: none !important;
}

:root[data-theme='norton'] .panes {
  padding: 3px 3px 2px;
}

:root[data-theme='norton'] .panel {
  outline: 1px solid var(--line-strong) !important;
}

:root[data-theme='norton'] .panel.active {
  outline: 2px solid #ffffff !important;
}

:root[data-theme='norton'] app-file-table :is(th, td) {
  border-right: 1px solid var(--line);
}

:root[data-theme='norton'] app-file-table tbody tr {
  border-bottom-color: transparent;
}

:root[data-theme='norton'] app-file-table tbody tr.cursor {
  outline-color: #ffffff;
}

:root[data-theme='norton'] .command-bar button {
  color: var(--text-1);
  background: #000000;
}

:root[data-theme='norton'] .command-bar kbd {
  border: 0;
  border-radius: 0;
  color: #000080;
  background: #00ffff;
}
```

- [ ] **Step 5: Build the frontend and run the focused browser spec**

Run:

```powershell
npm run build
Set-Location ../../../tests/e2e
npx playwright test specs/norton-theme.spec.ts --project=chromium
```

Expected: 2 tests PASS. The browser remains headless; no visible browser window is opened.

- [ ] **Step 6: Commit the visual theme and acceptance coverage**

```powershell
git add client/reach-commander-ui/src/styles.scss tests/e2e/specs/norton-theme.spec.ts
git commit -m "feat: add Norton Commander visual theme"
```

---

### Task 4: Public documentation and complete verification

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: the complete toggle and styling behavior from Tasks 1-3.
- Produces: public usage documentation without changing runtime interfaces.

- [ ] **Step 1: Document the user-facing capability**

In `README.md`, add this item under `What ReachCommander includes` after the contextual top-toolbar item:

```markdown
- A persistent Norton Commander-inspired theme, activated from the top toolbar and stored only in the current browser or installed PWA.
```

Add this short section before `Hardware monitoring`:

```markdown
## Norton Commander theme

Use the **Norton** control in the right side of the top toolbar to switch between ReachCommander's default interface and a cobalt-blue, cyan-framed, monospace theme inspired by classic Norton Commander. The preference stays in the current browser or installed PWA; it is not stored in the administrator account or sent to the server.
```

- [ ] **Step 2: Run formatting and focused checks**

Run from `client/reach-commander-ui`:

```powershell
npx prettier --check "src/**/*.{ts,html,scss}" "../../../tests/e2e/specs/norton-theme.spec.ts"
npm test -- --watch=false --include src/app/core/theme/theme.service.spec.ts --include src/app/app.spec.ts --include src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: Prettier reports all matched files use its style; all focused unit tests PASS.

- [ ] **Step 3: Run the complete frontend and PWA verification**

Run:

```powershell
npm test -- --watch=false
npm run test:pwa
npm run build
npm run verify:pwa
```

Expected: the complete Angular suite, both PWA contract tests, production build, and built-asset PWA verification all PASS.

- [ ] **Step 4: Run the complete headless browser suite**

Run from `tests/e2e`:

```powershell
npm test
```

Expected: every Playwright scenario PASS, including the two Norton-theme scenarios and all existing authentication, toolbar, rename, upload, archive, and PWA scenarios.

- [ ] **Step 5: Review the resulting visual artifact without opening a browser**

Locate `norton-theme-680.png` beneath `artifacts/playwright-results` and inspect that local PNG. Confirm deep cobalt surfaces, cyan pane frames, white file values, yellow command accents, square controls, readable focus, and no toolbar overlap. If any criterion is not met, adjust only the scoped Norton selectors and rerun Tasks 3 Step 5 and Task 4 Steps 2-4.

- [ ] **Step 6: Check the repository diff and commit documentation**

Run:

```powershell
git diff --check
git status --short
git diff -- README.md
git add README.md
git commit -m "docs: document Norton theme"
```

Expected: no whitespace errors; only intended feature files and ignored test artifacts are present before the documentation commit.

- [ ] **Step 7: Final repository verification**

Run:

```powershell
git status --short --branch
git log -5 --oneline --decorate
```

Expected: `master` has no uncommitted tracked changes and contains the theme state, toggle, visual theme, and documentation commits. Do not push unless the user explicitly requests it.
