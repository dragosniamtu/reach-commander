# ReachCommander Norton Theme Design

**Date:** 2026-08-23  
**Status:** Approved for implementation

## Purpose

Add an optional visual theme inspired by the classic Norton Commander interface while preserving ReachCommander's current behavior, information architecture, responsive layout, and accessibility. Users can activate or deactivate the theme from the top toolbar, and the selected theme persists in the current browser and installed PWA.

The supplied Norton Commander screenshot is visual inspiration only. The implementation will recreate its recognizable palette and terminal-like character with native HTML and CSS; it will not embed or copy the screenshot.

## Goals

- Provide `Default` and `Norton` visual themes.
- Place a compact theme toggle in the right-side top toolbar beside the existing global controls.
- Persist the selection in browser-local storage across reloads and PWA restarts.
- Apply the theme consistently across the authenticated shell, dialogs, overlays, metrics, file tables, and authentication screens.
- Preserve keyboard operation, focus visibility, readable contrast, supported compact layouts, and existing application behavior.

## Non-goals

- No server-side preference, account synchronization, or database change.
- No changes to file operations, authentication, authorization, API contracts, routing, or service-worker caching policy.
- No attempt to reproduce DOS screen dimensions, bitmap fonts, or Norton Commander branding pixel-for-pixel.
- No additional themes or general theme editor in this feature.

## Architecture

### Theme state

A root-provided Angular theme service owns a two-value state:

```text
default | norton
```

The service exposes the active theme as a readonly signal and provides explicit `setTheme` and `toggle` operations. It applies the state to the root `<html>` element through a `data-theme` attribute so both global styles and encapsulated component DOM can respond without duplicating component templates.

The service reads `reachcommander.theme.v1` from `localStorage` during application startup. Only the exact value `norton` activates the alternate theme; missing, inaccessible, or unrecognized values safely fall back to `default`. Setting the default removes the stored override, while setting Norton stores `norton`.

Storage failures must not block startup or theme switching. The in-memory state and root attribute remain authoritative for the current page even when persistence is unavailable.

### Startup behavior

Theme initialization occurs at the application root, not only inside the commander shell. This ensures a saved Norton selection also applies to the authentication and first-run screens and avoids components owning global presentation state.

### Styling boundary

The existing CSS custom-property system remains the primary styling mechanism. A global `:root[data-theme='norton']` block overrides palette, typography, focus, selection, and shadow tokens. A small set of scoped Norton selectors removes contemporary decoration that cannot be represented by the current tokens, such as gradients, rounded corners, and elevated shadows.

Component-specific duplicate theme stylesheets will not be introduced. Any new reusable presentation value should be expressed as a token where practical.

## User Interface

### Toolbar control

The toggle appears in the `.top-actions` group in the top toolbar, before account and hardware-monitoring controls. It uses the existing global-action dimensions so it does not disrupt the active-panel toolbar.

The control includes:

- A compact terminal-style icon and the visible label `Norton` where space permits.
- `aria-label` text describing the action: `Activate Norton theme` or `Deactivate Norton theme`.
- `aria-pressed="true"` only while the Norton theme is active.
- A tooltip matching the current action.
- A stable `data-testid="norton-theme-toggle"` hook for browser acceptance tests.

The control remains visible at supported compact widths. Its text may be visually hidden while its icon and accessible name remain available.

### Norton visual language

The alternate theme translates the reference image into ReachCommander's existing structure:

- Deep cobalt blue application and panel surfaces.
- Bright cyan borders, dividers, active-pane outlines, and primary text.
- White high-emphasis values and table content.
- Yellow accents for function-key labels and warnings.
- Cyan selection backgrounds with dark-blue selected text.
- Consolas/Cascadia Mono/Courier-style typography throughout the interface.
- Square corners, flat surfaces, and minimal or no elevation shadows.
- Stronger column separators and pane framing to evoke the original dual-pane grid.
- The existing bottom command bar styled as a DOS-like function-key strip.

Semantic colors remain distinct: errors stay visibly red, success remains green, and warnings use yellow. Focus indicators stay clearly visible in both themes.

## Data flow

```text
Application startup
  -> theme service reads local preference
  -> validates `default | norton`
  -> updates readonly signal and `<html data-theme>`

Toolbar toggle
  -> calls theme service toggle
  -> updates signal and root attribute immediately
  -> persists or removes browser-local override
  -> Angular updates label, tooltip, and aria-pressed
```

The theme preference contains no credentials, paths, filenames, telemetry, or other protected state. Authentication-state reset and logout do not clear it because it is a device-level visual preference.

## Responsive behavior

- Desktop retains the visible `Norton` label when space allows.
- Compact layouts keep the toggle reachable as an icon-only control.
- The theme does not change existing breakpoints, minimum supported width, pane stacking, or scrolling behavior.
- Activating either theme must not introduce horizontal overlap between the active-panel toolbar and top actions.

## Accessibility

- The toggle is a native button and works with keyboard activation.
- `aria-pressed`, the accessible label, and tooltip update with the active state.
- Focus-visible styling remains distinct against the cobalt palette.
- Color is not the only indication of toggle state; pressed state and accessible text provide equivalent information.
- Reduced-motion behavior remains unchanged, and theme switching adds no animation.

## Testing

### Unit tests

- Default safely when no preference exists.
- Restore a valid saved Norton preference during initialization.
- Apply the correct root `data-theme` attribute.
- Toggle both directions and persist/remove the preference.
- Ignore invalid stored values.
- Continue working when storage reads or writes throw.
- Render the toolbar control with correct label and `aria-pressed` state.

### Browser acceptance

- Activate Norton mode from the toolbar and verify root state plus representative computed palette values.
- Reload and confirm the preference persists.
- Deactivate it and confirm the default theme returns and remains after reload.
- Confirm the toggle remains visible and usable at the supported compact viewport.
- Confirm the top toolbar and panes do not overlap or overflow at existing acceptance-test viewports.

### Regression verification

- Run the complete Angular unit suite.
- Run the production Angular build and PWA contract checks.
- Run the headless Playwright acceptance suite.
- No backend test is required for the theme itself because the feature has no API or server changes; the normal project verification remains available before release.

## Acceptance criteria

The feature is complete when a user can activate a recognizable Norton Commander-inspired theme from the top toolbar, use every existing screen without behavioral regression, reload or restart the installed PWA and retain the choice, then deactivate the theme to restore the existing appearance. Both modes must pass automated accessibility-state, responsive-layout, unit, build, PWA, and headless browser checks.
