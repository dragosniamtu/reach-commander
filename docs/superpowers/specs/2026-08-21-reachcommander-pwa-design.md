# ReachCommander Progressive Web App Design

**Date:** 2026-08-21  
**Status:** Approved for implementation

## Goal

Make the Angular frontend installable as a Progressive Web App on supported desktop and mobile browsers while preserving ReachCommander's server-authoritative security model. The installed application must remain useful as a launchable shell when the server is unavailable, but it must never persist filesystem listings or API responses for offline access.

## Decisions

- Use Angular 22's official `@angular/service-worker` integration.
- Register the service worker only for production builds.
- Cache the versioned application shell, styles, scripts, fonts, and branded icons.
- Do not configure any service-worker data groups. Requests under `/api` and `/health` remain network-only.
- Expose an **Install app** action only while the browser supplies a deferred install prompt.
- Announce an available application update without forcing a reload. The user activates it with **Reload** so file operations are never interrupted unexpectedly.
- Show an offline/server-unavailable status in the existing application chrome. Do not display stale directory contents as though they were current.
- Use a ReachCommander-specific dual-pane icon set with regular and maskable 192 px and 512 px images, plus an Apple touch icon and favicon.

## Architecture

### Build and service worker

The Angular production build will enable `serviceWorker` and consume a checked-in `ngsw-config.json`. The configuration will prefetch the small application shell and lazily cache non-critical static assets. There will be no `dataGroups` entry, which keeps API responses outside Angular's cache.

`provideServiceWorker()` will register `ngsw-worker.js` after the application becomes stable, with a bounded delay so registration cannot be postponed indefinitely by telemetry polling. Development builds and `ng serve` remain service-worker-free.

### Web app manifest and branding

`manifest.webmanifest` will define:

- name: `ReachCommander`
- short name: `ReachCommander`
- root start URL and scope
- standalone display mode
- dark background and theme colors matching the current commander chrome
- regular and maskable 192 px and 512 px PNG icons

The HTML head will link the manifest, theme color, Apple touch icon, and updated favicon. Icons will use the existing two-pane visual language: a dark rounded square, cyan outline, and two contrasting pane columns. The artwork will retain safe padding for maskable icon crops.

### PWA state service

A focused `PwaService` will own browser integration and expose signals for:

- whether an install prompt is available;
- whether the browser reports offline status;
- whether a service-worker update is ready;
- whether install or update activation is in progress.

The service will capture `beforeinstallprompt`, clear it after use, respond to `appinstalled`, subscribe to browser online/offline events, and listen for Angular service-worker version events. Browser-only APIs will be guarded so unit tests and server-like environments remain safe.

The install method will invoke the captured prompt once. The update method will activate the ready version and reload only after the user explicitly chooses **Reload**. Failed install or update actions leave the application usable and expose a concise status message.

### User interface

The existing top-right action group will gain a compact **Install app** button when installation is supported. It disappears after installation or when the prompt is unavailable.

A slim, accessible notification in the existing shell will represent two independent states:

- **Offline / server unavailable:** explain that live file data and operations require the ReachCommander server.
- **Update available:** offer **Reload** and a dismiss action. The notification will not steal focus or obscure file-operation dialogs.

When initial source loading fails, the message will distinguish a disconnected browser from an unreachable server. Existing API errors remain authoritative once connectivity returns; the service worker never supplies cached API data.

## Security and caching boundaries

- No file listing, source metadata, hardware telemetry, upload response, rename plan, archive plan, or operation result is intentionally cached by the PWA.
- The service worker does not add offline mutation queues or background sync.
- Authentication and TLS remain deployment responsibilities. Service workers require HTTPS except on localhost, so production documentation will retain the authenticated HTTPS reverse-proxy recommendation.
- A newly deployed shell may coexist briefly with an older open tab until the user accepts the update. Hashed assets and explicit update activation prevent mixed-version asset replacement.

## Error handling

- Unsupported browsers simply omit the install action; the web application continues normally.
- A rejected install prompt clears the pending state without repeated prompting.
- Service-worker registration or update failures do not block application startup.
- Offline startup loads the cached shell and presents a network-required state rather than cached filesystem data.
- Update activation failures retain the current working version and show a retryable message.

## Testing

Implementation follows test-driven development:

1. Unit tests define install prompt capture, dismissal, installation completion, online/offline state, update readiness, and explicit activation behavior.
2. Shell component tests define conditional install controls and accessible offline/update notifications.
3. Build verification asserts the production output contains `ngsw-worker.js`, `ngsw.json`, the manifest, and every declared icon.
4. Browser acceptance serves the production build, verifies service-worker registration, reloads the shell offline, and confirms `/api` responses are absent from Cache Storage.
5. The existing Angular, .NET, Playwright, and publish-layout suites remain green.

## Documentation

The public README will document installation, offline limitations, update behavior, HTTPS requirements, and the guarantee that filesystem/API data is not cached for offline use. CI will verify the PWA artifacts as part of the production frontend build.

## Out of scope

- Offline directory browsing or file previews
- Cached API responses
- Background uploads, extraction, or rename operations
- Push notifications
- Background sync
- A custom service worker or browser-extension packaging
