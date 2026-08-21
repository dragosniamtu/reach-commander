# ReachCommander Progressive Web App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Angular frontend installable, shell-offline-capable, explicitly updateable, and safe from filesystem/API response caching.

**Architecture:** Angular 22's official service worker owns only versioned static application assets. A focused signal-based `PwaService` adapts browser installation, connectivity, and `SwUpdate` events for the existing commander shell; all `/api` and `/health` requests remain network-only.

**Tech Stack:** Angular 22 standalone providers, Angular service worker, Signals, Vitest, Node's built-in test runner, Playwright, ASP.NET Core 10 static hosting.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Register the service worker only for production builds.
- Never cache file listings, source metadata, telemetry, uploads, rename/archive plans, operation results, `/api/**`, or `/health`.
- Do not add background sync, offline mutation queues, push notifications, or a custom service worker.
- An update must reload only after the user chooses **Reload**; do not call `SwUpdate.activateUpdate()` before reloading.
- Unsupported browsers must retain the normal web experience without an install action.
- Keep all existing Windows and Ubuntu build, test, publish, and browser behavior green.
- Follow red-green-refactor: every behavior test must be observed failing before production code is added.

---

## File Structure

- `client/reach-commander-ui/ngsw-config.json`: static-shell cache boundary; intentionally contains no data groups.
- `client/reach-commander-ui/public/manifest.webmanifest`: installation metadata and icon declarations.
- `client/reach-commander-ui/public/icons/reachcommander-mark.svg`: editable source for the dual-pane brand mark.
- `client/reach-commander-ui/public/icons/*.png`: checked-in regular, maskable, touch, and favicon raster outputs.
- `client/reach-commander-ui/tools/generate-pwa-icons.ps1`: deterministic ImageMagick rasterization command.
- `client/reach-commander-ui/tools/pwa-assets.test.mjs`: source manifest/config/icon contract tests.
- `client/reach-commander-ui/tools/verify-pwa-build.mjs`: production output and generated cache-boundary verification.
- `client/reach-commander-ui/src/app/core/pwa/pwa.service.ts`: browser/PWA state adapter.
- `client/reach-commander-ui/src/app/core/pwa/pwa.service.spec.ts`: install, connectivity, and update state tests.
- `client/reach-commander-ui/src/app/features/commander/commander-shell/*`: install button and accessible notices.
- `tests/e2e/specs/pwa.spec.ts`: real production service-worker/offline/cache acceptance.
- `.github/workflows/ci.yml`: PWA source and production-output verification.
- `README.md`: installation, offline, update, security, and test documentation.

---

### Task 1: Add the production PWA shell and branded assets

**Files:**
- Create: `client/reach-commander-ui/ngsw-config.json`
- Create: `client/reach-commander-ui/public/manifest.webmanifest`
- Create: `client/reach-commander-ui/public/icons/reachcommander-mark.svg`
- Create: `client/reach-commander-ui/public/icons/icon-192.png`
- Create: `client/reach-commander-ui/public/icons/icon-512.png`
- Create: `client/reach-commander-ui/public/icons/icon-maskable-192.png`
- Create: `client/reach-commander-ui/public/icons/icon-maskable-512.png`
- Create: `client/reach-commander-ui/public/icons/apple-touch-icon.png`
- Create: `client/reach-commander-ui/public/icons/favicon-32.png`
- Create: `client/reach-commander-ui/tools/generate-pwa-icons.ps1`
- Create: `client/reach-commander-ui/tools/pwa-assets.test.mjs`
- Create: `client/reach-commander-ui/tools/verify-pwa-build.mjs`
- Modify: `client/reach-commander-ui/package.json`
- Modify: `client/reach-commander-ui/package-lock.json`
- Modify: `client/reach-commander-ui/angular.json`
- Modify: `client/reach-commander-ui/src/index.html`
- Modify: `client/reach-commander-ui/src/app/app.config.ts`
- Create: `tests/e2e/specs/pwa.spec.ts`

**Interfaces:**
- Consumes: Angular's `provideServiceWorker(script, options)` production provider.
- Produces: `ngsw-worker.js`, `ngsw.json`, install manifest, icon files, `SwUpdate` injection support, `npm run test:pwa`, `npm run verify:pwa`, and browser proof of the shell-only cache boundary.

- [ ] **Step 1: Write the failing source-contract test**

Create `tools/pwa-assets.test.mjs` with Node's built-in test runner. The test must read from the Angular project root and assert the exact manifest identity, standalone display, four declared icons, correct PNG dimensions, production-only service-worker configuration, explicit navigation exclusions, and absence of data caching:

```js
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const readJson = (path) => JSON.parse(readFileSync(join(root, path), 'utf8'));

function pngSize(path) {
  const bytes = readFileSync(join(root, path));
  assert.deepEqual([...bytes.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

test('declares installable ReachCommander branding with correctly sized icons', () => {
  const manifest = readJson('public/manifest.webmanifest');
  assert.equal(manifest.name, 'ReachCommander');
  assert.equal(manifest.short_name, 'ReachCommander');
  assert.equal(manifest.start_url, '/');
  assert.equal(manifest.scope, '/');
  assert.equal(manifest.display, 'standalone');
  assert.deepEqual(
    manifest.icons.map(({ src, sizes, purpose }) => ({ src, sizes, purpose })),
    [
      { src: 'icons/icon-192.png', sizes: '192x192', purpose: 'any' },
      { src: 'icons/icon-512.png', sizes: '512x512', purpose: 'any' },
      { src: 'icons/icon-maskable-192.png', sizes: '192x192', purpose: 'maskable' },
      { src: 'icons/icon-maskable-512.png', sizes: '512x512', purpose: 'maskable' },
    ],
  );
  for (const [path, size] of [
    ['public/icons/icon-192.png', 192],
    ['public/icons/icon-512.png', 512],
    ['public/icons/icon-maskable-192.png', 192],
    ['public/icons/icon-maskable-512.png', 512],
    ['public/icons/apple-touch-icon.png', 180],
    ['public/icons/favicon-32.png', 32],
  ]) {
    assert.deepEqual(pngSize(path), { width: size, height: size });
  }
});

test('enables only static production caching and excludes server endpoints', () => {
  const angular = readJson('angular.json');
  const build = angular.projects['reach-commander-ui'].architect.build;
  assert.equal(build.configurations.production.serviceWorker, 'ngsw-config.json');
  assert.equal(build.configurations.development.serviceWorker, false);

  const config = readJson('ngsw-config.json');
  assert.equal(config.index, '/index.html');
  assert.equal(Object.hasOwn(config, 'dataGroups'), false);
  assert.ok(config.navigationUrls.includes('!/api/**'));
  assert.ok(config.navigationUrls.includes('!/health'));
});
```

Create `tests/e2e/specs/pwa.spec.ts` with the production-browser contract that can pass before the connectivity notice exists:

```ts
import { expect, test } from '@playwright/test';

test('registers the production shell, keeps API data out of caches, and reloads offline', async ({
  context,
  page,
}) => {
  test.setTimeout(60_000);
  await page.goto('/');
  await expect(page.getByText('ReachCommander', { exact: true })).toBeVisible();

  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect.poll(() => page.evaluate(() => Boolean(navigator.serviceWorker.controller)))
    .toBe(true);

  expect(await page.evaluate(async () => Boolean(await caches.match('/api/sources')))).toBe(false);

  await context.setOffline(true);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await expect(page.getByText('ReachCommander', { exact: true })).toBeVisible();
  await context.setOffline(false);
});
```

- [ ] **Step 2: Run the test and verify RED**

Run from `client/reach-commander-ui`:

```powershell
node --test tools/pwa-assets.test.mjs
```

Expected: FAIL with `ENOENT` for `public/manifest.webmanifest`; this proves the test detects the missing PWA assets.

Then run the new browser test from `tests/e2e` against the existing production build:

```powershell
npx playwright test specs/pwa.spec.ts --workers=1
```

Expected: FAIL while awaiting `navigator.serviceWorker.ready`; the current production build has no registered worker.

- [ ] **Step 3: Install the matching Angular service-worker package**

Run:

```powershell
npm install @angular/service-worker@^22.1.0 --save
```

Expected: `package.json` and `package-lock.json` include `@angular/service-worker` on Angular's existing `^22.1.x` version line, with no audit error requiring unrelated dependency changes.

- [ ] **Step 4: Add the manifest, cache boundary, and production registration**

Create `ngsw-config.json` exactly with static asset groups and no `dataGroups`:

```json
{
  "$schema": "./node_modules/@angular/service-worker/config/schema.json",
  "index": "/index.html",
  "assetGroups": [
    {
      "name": "app-shell",
      "installMode": "prefetch",
      "resources": {
        "files": [
          "/index.html",
          "/manifest.webmanifest",
          "/icons/favicon-32.png",
          "/*.css",
          "/*.js"
        ]
      }
    },
    {
      "name": "brand-assets",
      "installMode": "lazy",
      "updateMode": "prefetch",
      "resources": {
        "files": ["/icons/**"]
      }
    }
  ],
  "navigationUrls": [
    "/**",
    "!/**/*.*",
    "!/**/*__*",
    "!/**/*__*/**",
    "!/api/**",
    "!/health"
  ]
}
```

Create `manifest.webmanifest` exactly as:

```json
{
  "name": "ReachCommander",
  "short_name": "ReachCommander",
  "description": "A self-hosted dual-pane file manager.",
  "id": "/",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#071318",
  "theme_color": "#071318",
  "icons": [
    { "src": "icons/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any" },
    { "src": "icons/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any" },
    { "src": "icons/icon-maskable-192.png", "sizes": "192x192", "type": "image/png", "purpose": "maskable" },
    { "src": "icons/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

In `angular.json`, set production `serviceWorker` to `ngsw-config.json` and development `serviceWorker` to `false`.

In `app.config.ts`, add:

```ts
import { isDevMode } from '@angular/core';
import { provideServiceWorker } from '@angular/service-worker';

provideServiceWorker('ngsw-worker.js', {
  enabled: !isDevMode(),
  registrationStrategy: 'registerWhenStable:30000',
}),
```

In `index.html`, link `/manifest.webmanifest`, `/icons/favicon-32.png`, and `/icons/apple-touch-icon.png`, and add `theme-color` `#071318` plus `apple-mobile-web-app-capable` and `apple-mobile-web-app-title` metadata.

- [ ] **Step 5: Add reproducible ReachCommander icon artwork**

Create `reachcommander-mark.svg` with all foreground artwork inside the central 80% maskable safe area:

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="ReachCommander dual-pane mark">
  <rect width="512" height="512" rx="112" fill="#071318"/>
  <rect x="82" y="82" width="348" height="348" rx="38" fill="#0d2028" stroke="#43d6e2" stroke-width="18"/>
  <path d="M91 145h330" stroke="#43d6e2" stroke-width="14"/>
  <circle cx="120" cy="114" r="9" fill="#43d6e2"/>
  <circle cx="151" cy="114" r="9" fill="#43d6e2" opacity=".65"/>
  <rect x="124" y="184" width="104" height="194" rx="14" fill="#43d6e2"/>
  <rect x="284" y="184" width="104" height="194" rx="14" fill="#43d6e2" opacity=".48"/>
</svg>
```

Create `generate-pwa-icons.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
$magick = Get-Command magick -ErrorAction SilentlyContinue
if ($null -eq $magick) {
  throw 'ImageMagick (magick) is required to regenerate the PWA icons.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'public/icons/reachcommander-mark.svg'
$outputs = @(
  @{ Name = 'icon-192.png'; Size = 192 },
  @{ Name = 'icon-512.png'; Size = 512 },
  @{ Name = 'icon-maskable-192.png'; Size = 192 },
  @{ Name = 'icon-maskable-512.png'; Size = 512 },
  @{ Name = 'apple-touch-icon.png'; Size = 180 },
  @{ Name = 'favicon-32.png'; Size = 32 }
)

foreach ($output in $outputs) {
  $target = Join-Path $projectRoot "public/icons/$($output.Name)"
  & $magick.Source -background none $source -resize "$($output.Size)x$($output.Size)" -strip $target
  if ($LASTEXITCODE -ne 0) {
    throw "ImageMagick failed while creating $($output.Name)."
  }
}
```

Run the script once to perform these exact conversions:

```powershell
magick -background none public/icons/reachcommander-mark.svg -resize 192x192 public/icons/icon-192.png
magick -background none public/icons/reachcommander-mark.svg -resize 512x512 public/icons/icon-512.png
magick -background none public/icons/reachcommander-mark.svg -resize 192x192 public/icons/icon-maskable-192.png
magick -background none public/icons/reachcommander-mark.svg -resize 512x512 public/icons/icon-maskable-512.png
magick -background none public/icons/reachcommander-mark.svg -resize 180x180 public/icons/apple-touch-icon.png
magick -background none public/icons/reachcommander-mark.svg -resize 32x32 public/icons/favicon-32.png
```

Run the script once and check in its six PNG outputs. Add package scripts:

```json
"test:pwa": "node --test tools/pwa-assets.test.mjs",
"verify:pwa": "node tools/verify-pwa-build.mjs"
```

Create `verify-pwa-build.mjs`:

```js
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const output = join(root, 'dist', 'reach-commander-ui', 'browser');
const required = [
  'ngsw-worker.js',
  'ngsw.json',
  'manifest.webmanifest',
  'icons/icon-192.png',
  'icons/icon-512.png',
  'icons/icon-maskable-192.png',
  'icons/icon-maskable-512.png',
  'icons/apple-touch-icon.png',
  'icons/favicon-32.png',
];

for (const path of required) {
  assert.ok(existsSync(join(output, path)), `Missing production PWA asset: ${path}`);
}

const ngsw = JSON.parse(readFileSync(join(output, 'ngsw.json'), 'utf8'));
assert.equal((ngsw.dataGroups ?? []).length, 0, 'API data groups must stay empty.');
const navigationRules = ngsw.navigationUrls ?? [];
assert.ok(navigationRules.some((entry) => !entry.positive && entry.regex.includes('api')));
assert.ok(navigationRules.some((entry) => !entry.positive && entry.regex.includes('health')));
console.log('ReachCommander PWA build verified.');
```

- [ ] **Step 6: Verify GREEN and the production output**

Run:

```powershell
npm run test:pwa
npm run build
npm run verify:pwa
```

Then run from `tests/e2e`:

```powershell
npx playwright test specs/pwa.spec.ts --workers=1
```

Expected: two source-contract tests pass, Angular builds without warnings, production verification prints `ReachCommander PWA build verified.`, and the focused browser test proves the cached shell reloads offline while `/api/sources` is absent from Cache Storage.

- [ ] **Step 7: Commit Task 1**

Stage only Task 1 files and commit:

```powershell
git commit -m "feat: add installable PWA shell"
```

---

### Task 2: Implement the browser PWA state service

**Files:**
- Create: `client/reach-commander-ui/src/app/core/pwa/pwa.service.ts`
- Create: `client/reach-commander-ui/src/app/core/pwa/pwa.service.spec.ts`

**Interfaces:**
- Consumes: `SwUpdate.versionUpdates`, `DOCUMENT.defaultView`, `beforeinstallprompt`, `appinstalled`, `online`, and `offline` browser events.
- Produces: injectable `PwaService` signals `canInstall`, `online`, `updateReady`, `installing`, and `error`; methods `install(): Promise<void>`, `reloadForUpdate(): void`, `dismissUpdate(): void`, and token `PWA_RELOAD`.

- [ ] **Step 1: Write failing service tests**

Create a Vitest spec with a `Subject<VersionEvent>`-backed `SwUpdate` mock and injected reload spy. Add exactly six tests:

```ts
it('captures one deferred install prompt and clears it after dismissal', async () => {
  const prompt = vi.fn(() => Promise.resolve());
  window.dispatchEvent(installPromptEvent(prompt, 'dismissed'));
  expect(service.canInstall()).toBe(true);
  await service.install();
  expect(prompt).toHaveBeenCalledOnce();
  expect(service.canInstall()).toBe(false);
});

it('clears the install action after the app is installed', () => {
  window.dispatchEvent(installPromptEvent(vi.fn(), 'accepted'));
  window.dispatchEvent(new Event('appinstalled'));
  expect(service.canInstall()).toBe(false);
});

it('tracks browser offline and online transitions', () => {
  window.dispatchEvent(new Event('offline'));
  expect(service.online()).toBe(false);
  window.dispatchEvent(new Event('online'));
  expect(service.online()).toBe(true);
});

it('offers a ready version and reloads only after explicit acceptance', () => {
  versionEvents.next(versionReadyEvent());
  expect(service.updateReady()).toBe(true);
  expect(reload).not.toHaveBeenCalled();
  service.reloadForUpdate();
  expect(reload).toHaveBeenCalledOnce();
});

it('keeps the current app usable when installation fails', async () => {
  window.dispatchEvent(failingInstallPromptEvent());
  await service.install();
  expect(service.error()).toContain('installation');
});

it('keeps the current app usable when an update download fails', () => {
  versionEvents.next(versionInstallationFailedEvent());
  expect(service.error()).toContain('update');
});
```

The helper must attach `prompt` and `userChoice` to a cancellable `Event('beforeinstallprompt')`; ready/failure helpers must return valid Angular `VersionEvent` shapes.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
npm test -- --watch=false --include src/app/core/pwa/pwa.service.spec.ts
```

Expected: FAIL because `./pwa.service` does not exist.

- [ ] **Step 3: Implement the minimal signal-based service**

Define:

```ts
export interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
}

export const PWA_RELOAD = new InjectionToken<() => void>('PWA_RELOAD', {
  providedIn: 'root',
  factory: () => () => globalThis.location.reload(),
});
```

`PwaService` must:

- initialize `online` from `document.defaultView?.navigator.onLine ?? true`;
- add and remove the four window event listeners using `DestroyRef`;
- call `preventDefault()` and store only the latest deferred install prompt;
- clear the prompt before awaiting it so repeated clicks cannot open it twice;
- set and clear `installing` in `try/finally`;
- set a concise error on prompt rejection;
- subscribe to `versionUpdates` only when `SwUpdate.isEnabled`;
- set `updateReady` only for `VERSION_READY`;
- report `VERSION_INSTALLATION_FAILED` without disrupting the current version; Angular 22.1.2 does not expose the later `VERSION_FAILED` event in `VersionEvent`;
- call only `PWA_RELOAD` from `reloadForUpdate()` when `updateReady` is true;
- clear `updateReady` from `dismissUpdate()`.

Use `takeUntilDestroyed(inject(DestroyRef))` for the Angular subscription and explicit `removeEventListener` calls for browser events.

- [ ] **Step 4: Verify GREEN and the complete Angular suite**

Run:

```powershell
npm test -- --watch=false --include src/app/core/pwa/pwa.service.spec.ts
npm test -- --watch=false
```

Expected: six focused PWA service tests pass and the complete Angular suite passes with no warnings.

- [ ] **Step 5: Commit Task 2**

Stage the two PWA service files and commit:

```powershell
git commit -m "feat: manage PWA install and update state"
```

---

### Task 3: Integrate install, offline, and update states into the shell

**Files:**
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`
- Modify: `tests/e2e/specs/pwa.spec.ts`

**Interfaces:**
- Consumes: the complete `PwaService` interface from Task 2 and `CommanderStore.initialize(): Promise<void>`.
- Produces: conditional install button, offline/server-unavailable notice, update reload/dismiss notice, and `retryInitialization(): Promise<void>`.

- [ ] **Step 1: Add four failing shell behavior tests**

Provide a mutable PWA test double:

```ts
const pwa = {
  canInstall: signal(false),
  online: signal(true),
  updateReady: signal(false),
  installing: signal(false),
  error: signal<string | null>(null),
  install: vi.fn(() => Promise.resolve()),
  reloadForUpdate: vi.fn(),
  dismissUpdate: vi.fn(),
};
```

Register it as `{ provide: PwaService, useValue: pwa }`, reset every signal/mock in `beforeEach`, and add:

```ts
it('shows the supported install action and delegates one click', () => {
  pwa.canInstall.set(true);
  fixture.detectChanges();
  const button = fixture.nativeElement.querySelector('[data-testid="install-app"]');
  expect(button).not.toBeNull();
  button.click();
  expect(pwa.install).toHaveBeenCalledOnce();
});

it('shows an accessible offline notice without stale-data claims', () => {
  pwa.online.set(false);
  fixture.detectChanges();
  const notice = fixture.nativeElement.querySelector('[data-testid="connection-notice"]');
  expect(notice.getAttribute('role')).toBe('status');
  expect(notice.textContent).toContain('offline');
  expect(notice.textContent).toContain('Live file data and operations require the server');
});

it('reloads or dismisses a ready update only from the notice actions', () => {
  pwa.updateReady.set(true);
  fixture.detectChanges();
  fixture.nativeElement.querySelector('[data-testid="reload-update"]').click();
  expect(pwa.reloadForUpdate).toHaveBeenCalledOnce();
  fixture.nativeElement.querySelector('[data-testid="dismiss-update"]').click();
  expect(pwa.dismissUpdate).toHaveBeenCalledOnce();
});

it('reports an unreachable server and retries initialization explicitly', async () => {
  store.initialize.mockRejectedValueOnce(new Error('unreachable'));
  await fixture.componentInstance.retryInitialization();
  fixture.detectChanges();
  expect(fixture.nativeElement.querySelector('[data-testid="connection-notice"]').textContent)
    .toContain('server is unavailable');
  store.initialize.mockResolvedValueOnce(undefined);
  fixture.nativeElement.querySelector('[data-testid="retry-connection"]').click();
  await fixture.whenStable();
  expect(store.initialize).toHaveBeenCalledTimes(3);
});
```

Before changing the shell, extend the existing offline section of `tests/e2e/specs/pwa.spec.ts` with:

```ts
await expect(page.getByTestId('connection-notice')).toContainText('offline');
```

- [ ] **Step 2: Run the focused shell spec and verify RED**

Run:

```powershell
npm test -- --watch=false --include src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: FAIL because `PwaService`, install controls, PWA notices, and `retryInitialization()` are absent.

Build the unchanged shell and run the focused Playwright test. Expected: FAIL because `[data-testid="connection-notice"]` is absent even though the cached shell opens offline.

- [ ] **Step 3: Implement the shell integration**

Inject `readonly pwa = inject(PwaService)`. Replace the direct initialization call in `ngOnInit` with:

```ts
void this.retryInitialization();
```

Add:

```ts
async retryInitialization(): Promise<void> {
  this.initializationError.set(null);
  try {
    await this.store.initialize();
  } catch {
    this.initializationError.set('The ReachCommander server is unavailable.');
  }
}
```

Add the install button before the existing Transfers action:

```html
@if (pwa.canInstall()) {
  <button
    type="button"
    class="install-app"
    data-testid="install-app"
    [disabled]="pwa.installing()"
    (click)="pwa.install()"
  >
    {{ pwa.installing() ? 'Installing…' : 'Install app' }}
  </button>
}
```

Replace the old fatal-error block with a positioned `.pwa-notices` region. Render an offline notice when `!pwa.online()`, otherwise render the server-unavailable notice and a Retry button when `initializationError()` exists. Independently render the ready-update notice with **Reload** and **Later** actions, and render `pwa.error()` as an alert. Use `role="status" aria-live="polite"` for connection/update status and `role="alert"` for failures.

Style notices below the 50 px topbar, with the existing surface/line/accent tokens, compact buttons, a maximum width that does not cover both panes, and `z-index` below modal dialogs. Change the 760 px media rule from hiding every top-action button to hiding only disabled placeholder buttons so **Install app** remains reachable on narrow displays.

- [ ] **Step 4: Verify GREEN, accessibility behavior, and build**

Run:

```powershell
npm test -- --watch=false --include src/app/features/commander/commander-shell/commander-shell.component.spec.ts
npm test -- --watch=false
npm run build
npm run verify:pwa
```

Then run `npx playwright test specs/pwa.spec.ts --workers=1` from `tests/e2e`. Expected: all four new shell tests pass; the Angular total is 197 tests; the build is warning-free; PWA output verification passes; the focused browser test sees the offline notice.

- [ ] **Step 5: Commit Task 3**

Stage only the shell files and commit:

```powershell
git commit -m "feat: expose PWA install and connectivity UI"
```

---

### Task 4: Add production-browser acceptance, CI gates, and public documentation

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `README.md`

**Interfaces:**
- Consumes: production PWA assets and shell UI from Tasks 1–3, existing Playwright global setup on `http://127.0.0.1:8092`.
- Produces: CI artifact gates and user-facing install/deployment documentation for the browser proof established in Tasks 1 and 3.

- [ ] **Step 1: Add CI source/output gates**

In `.github/workflows/ci.yml`, add `npm run test:pwa` after Angular unit tests and `npm run verify:pwa` after the production Angular build. Keep the existing Ubuntu acceptance order so published ASP.NET Core assets include the already-verified PWA output. Do not add an operating-system package for the service worker or icons.

- [ ] **Step 2: Document installation and the security boundary**

Update the README feature/architecture summaries to mention installable PWA delivery. Add a **Progressive Web App** section that states:

- use the top-bar **Install app** action when supported, or the browser's install menu otherwise;
- production installation/service workers require HTTPS, with localhost as the development exception;
- the cached shell can reopen offline, but live file data and every operation require the ReachCommander server;
- no `/api` response, file listing, telemetry result, upload, rename plan, or archive operation is cached for offline use;
- a ready update is applied only after the user selects **Reload**;
- rejecting installation or dismissing an update does not affect normal browser use.

Update quality counts to 197 Angular tests, 19 Playwright scenarios, and two PWA source-contract tests, while retaining 477 .NET tests and 37 archive fixture hashes.

- [ ] **Step 3: Verify GREEN in the real browser and full project**

Run fresh commands:

```powershell
dotnet test ReachCommander.slnx -c Release --no-restore
npm run test:pwa
npm test -- --watch=false
npm run build
npm run verify:pwa
npx playwright test --workers=1
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/pwa-release -p:BuildAngularOnPublish=false
```

Run Angular commands from `client/reach-commander-ui` and Playwright from `tests/e2e`. Expected: 477 .NET tests, two PWA source-contract tests, 197 Angular tests, warning-free production build, successful PWA artifact verification, 19 Playwright scenarios, and a publish tree containing `wwwroot/ngsw-worker.js`, `wwwroot/ngsw.json`, `wwwroot/manifest.webmanifest`, and all declared icons.

- [ ] **Step 4: Request independent review and resolve findings**

Review the complete PWA diff for Critical and Important issues, specifically cache leakage under `/api`, unsafe mixed-version updates, install prompt lifecycle leaks, service-worker registration outside production, inaccessible notifications, and offline acceptance reliability. Fix every Critical or Important finding under TDD and repeat Step 3.

- [ ] **Step 5: Commit Task 4**

Stage only the PWA acceptance, CI, README, and any review fixes, then commit:

```powershell
git commit -m "test: verify PWA offline boundaries"
```

Finally confirm `git status --short --branch` is clean on `master` and report the local branch's relationship to `origin/master` without pushing unless separately authorized.
