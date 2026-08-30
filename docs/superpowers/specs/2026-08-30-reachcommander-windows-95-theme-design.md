# ReachCommander Windows 95 Theme Design

## Goal

Add a Windows 95-inspired visual theme without changing ReachCommander's information architecture, file-operation behavior, keyboard model, responsive layout, authentication, or PWA behavior. The theme joins the existing Modern and Norton choices and remains a browser-local visual preference.

## Theme selection

Replace the binary Norton toggle with one compact, labeled theme selector in the same top-toolbar position. It exposes three explicit choices:

- Modern
- Norton
- Windows 95

The selector uses a native `select` with an accessible `Theme` label so keyboard, touch, and assistive-technology behavior remains predictable. At compact widths the visible label collapses while the control remains usable and identifies itself through its accessible name and tooltip.

The theme service expands its validated state to `default | norton | windows95`. It keeps the existing `reachcommander.theme.v1` storage key, accepts only those exact values, removes the stored value for Modern, and stores the exact alternate-theme value otherwise. It applies alternate choices through `<html data-theme="…">`. Storage failures never block changing the in-memory theme.

## Windows 95 visual language

The theme is an original CSS treatment inspired by common Windows 95 interface conventions; it does not ship Microsoft artwork, fonts, icons, sounds, or binary assets.

- Desktop/background: muted teal.
- Application surfaces: classic light gray.
- Title and active heading areas: navy with white text.
- Text: black and dark gray, with navy selections and white selected text.
- Controls: square corners and raised/inset bevels composed from white, light gray, dark gray, and black borders.
- Typography: `Tahoma`, `MS Sans Serif`, `Segoe UI`, and system sans-serif fallbacks.
- Shadows and gradients: removed in favor of crisp one-pixel edges.
- Semantic state: danger, warning, success, focus, writable/read-only, and disabled states remain distinguishable and meet practical contrast requirements.

The existing CSS custom properties remain the primary integration point. A global `:root[data-theme='windows95']` block overrides palette and typography tokens. Scoped selectors handle the few structural details tokens cannot express: bevel borders, navy title strips, button pressed states, dialog backdrops, active pane treatment, table selection, and the bottom command bar.

## Component scope

The theme applies consistently to:

- authenticated shell and top toolbar;
- dual panes, tabs, paths, tables, selections, and scrollable content;
- command bar and keyboard hints;
- authentication and password screens;
- rename, upload, extraction, file-operation, Trash, directory, metrics, and update dialogs/overlays;
- empty, loading, disabled, warning, error, and focus states.

No component template is duplicated for a theme. The selector is the only markup change beyond stable test hooks.

## Compatibility and migration

Existing stored `norton` preferences continue to restore Norton. Missing or unknown values restore Modern. There is no server configuration, account migration, API change, database, or deployment setting. The preference remains on the current browser/PWA device and survives login/logout.

## Responsive and accessibility requirements

- The selector must remain visible and operable at the existing 680 px compact acceptance width.
- No horizontal page overflow may be introduced.
- Focus indicators remain visible in all three themes.
- Theme names are exposed as real option labels, not color-only controls.
- Reduced-motion behavior is unchanged because theme switching adds no animation.
- Windows 95 styling must not reduce the existing 30 px file-row alignment or filename ellipsis behavior.

## Testing

Test-first coverage will prove:

1. theme state restores only the three allowlisted values and rejects unknown data;
2. selecting Modern, Norton, and Windows 95 updates state, root attributes, and browser persistence;
3. storage failure still permits an in-memory theme change;
4. the toolbar selector exposes all options and changes the active theme;
5. Playwright verifies Windows 95 palette tokens, beveled controls, navy selection, persistence, authentication/shell coverage, compact layout, and no horizontal overflow;
6. existing Norton acceptance remains green through the new selector;
7. the complete Angular, production build, PWA, and browser suites pass.

## Documentation

README theme guidance will describe all three choices, clarify that Windows 95 is an inspired visual treatment, and repeat that preferences never leave the browser.

## Out of scope

- Pixel-for-pixel Windows reproduction.
- Microsoft logos, Start menu, desktop icons, sounds, boot screens, or copyrighted assets.
- Theme editor, custom colors, automatic OS theme mapping, per-user server persistence, or synchronized preferences.
