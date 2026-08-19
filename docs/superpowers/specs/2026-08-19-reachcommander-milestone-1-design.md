# ReachCommander Milestone 1 Design

## Purpose

ReachCommander is a self-hosted, browser-based, dual-pane file manager inspired by Total Commander's interaction model. Milestone 1 delivers the safe, read-only foundation: configurable filesystem sources, two independent panes, directory tabs, dense file tables, keyboard navigation, selection, filtering, source capacity, and Docker deployment.

All product names, .NET namespaces, projects, container names, UI copy, and documentation use `ReachCommander` consistently.

## Scope

Milestone 1 includes:

- a complete .NET 10 and Angular 22 solution;
- configurable source discovery from external JSON;
- compact source selectors above both panes;
- independent source, tab, path, cursor, selection, sorting, and filtering state per pane;
- safe directory listing and file-information APIs;
- dense sortable details tables;
- centralized keyboard commands;
- versioned browser persistence for panel state;
- source availability, read-only state, and capacity reporting;
- unit, integration, Angular, and Playwright tests;
- a single-origin multi-stage Docker deployment; and
- operational and security documentation.

Milestone 1 does not include mutation endpoints, transfers, SignalR, background jobs, authentication, preview or download, uploads, thumbnails, archives, recursive search, or Linux device mounting. Function-bar commands belonging to later milestones are visible but disabled and explain their future availability.

## Architecture

`ReachCommander.slnx` contains a pragmatic modular monolith:

- `src/ReachCommander.Domain` contains source and file-entry concepts and has no infrastructure dependencies.
- `src/ReachCommander.Application` contains read-only use cases and interfaces for source discovery, secure path resolution, directory browsing, and file information.
- `src/ReachCommander.Infrastructure` loads JSON source configuration, accesses the filesystem, discovers capacity, and enforces canonical path confinement.
- `src/ReachCommander.Api` contains thin controllers, API DTO mapping, Problem Details, OpenAPI, health checks, structured logging, and Angular static-file hosting.
- `client/reach-commander-ui` is an Angular 22 standalone application using Signals.
- `tests/ReachCommander.UnitTests` and `tests/ReachCommander.IntegrationTests` cover backend behavior.
- `tests/e2e` contains Playwright browser tests.

Dependencies point inward:

```text
ReachCommander.Api ─────────► ReachCommander.Application ─────────► ReachCommander.Domain
        │                              ▲
        └────► ReachCommander.Infrastructure
```

Production runs as one ASP.NET Core process. Angular production assets are published into `wwwroot`, controllers serve `/api/*`, the health endpoint is explicit, and non-file UI routes fall back to `index.html`. Milestone 1 does not register transfer workers or SignalR hubs.

## Source Configuration

The application reads the configuration path from `ReachCommander:SourcesPath`. Its production default is `/config/sources.json`; development configuration points to the repository sample.

Each enabled source defines at least:

```json
{
  "id": "downloads",
  "name": "Downloads",
  "path": "/sources/downloads",
  "enabled": true,
  "readOnly": false,
  "defaultLeft": true,
  "defaultRight": false
}
```

Startup validation rejects:

- duplicate IDs using case-insensitive comparison;
- IDs outside the lowercase `a-z`, `0-9`, hyphen, and underscore alphabet;
- empty display names;
- non-absolute physical paths;
- more than one default source for either pane; and
- configurations with no enabled source.

An enabled but missing or inaccessible root remains visible as unavailable. Source discovery reports ID, name, availability, read-only state, optional total/used/free capacity, and default-pane flags. It never returns the configured physical path.

If a configured default is unavailable, the client keeps it selected and shows the unavailable state rather than silently navigating to another source. If a persisted source no longer exists in configuration, the client falls back to the configured default and then to the first enabled source.

## Filesystem Security

Every browser request uses only `sourceId` and a slash-separated logical path whose root is `/`. `IPathSecurityService` owns all translation from this logical identity to a physical path.

Resolution performs these steps:

1. Resolve the source by validated ID.
2. Reject null bytes, drive-qualified paths, UNC paths, and other rooted physical syntax.
3. Normalize separators and `.` segments while identifying traversal attempts.
4. Combine the logical path beneath the configured source root.
5. Canonicalize the configured root and candidate with platform-correct path semantics.
6. Walk existing path components and resolve symbolic links.
7. Reject a candidate if the resolved path or any resolved link target is outside the canonical source root.
8. Return the physical path only to the infrastructure filesystem service.

The configured source root itself may be a symbolic link; its fully resolved target becomes the confinement root. A symlink inside that root may be listed, but navigation through a link that escapes the root is rejected. Containment checks compare complete path segments, so a sibling such as `/sources/media-backup` is not accepted as a child of `/sources/media`.

Milestone 1 registers no mutation endpoints. Source `readOnly` metadata is still returned because it affects the UI and defines future authorization policy, but even nominally writable sources remain API-read-only in this milestone.

Automated tests create isolated temporary source trees and never operate on the developer's normal filesystem.

## API Contracts

Milestone 1 exposes:

```text
GET /api/sources
GET /api/files?sourceId={id}&path={logicalPath}
GET /api/files/info?sourceId={id}&path={logicalPath}
GET /health
```

File-entry responses contain logical metadata only: name, relative path, type, optional size, modified time, optional extension, read-only state, and displayable attributes or permissions where portable. The parent entry is a client-side navigation row and is not modeled as a physical directory entry.

Directory enumeration is synchronous because .NET exposes no genuine asynchronous directory-enumeration API. The infrastructure service enumerates once, checks cancellation between entries, avoids an unnecessary `Task.Run` wrapper, and returns immutable DTO-ready results. Large-directory virtualization in the Angular table limits rendered DOM work; backend pagination is deferred until measurements justify its API complexity.

Errors use RFC 9457-style Problem Details with stable extension codes and no physical paths:

- `400` for a malformed logical path or request;
- `403` for root or symlink-confinement violations;
- `404` for an unknown source or missing entry;
- `503` for a configured source that is currently unavailable; and
- `500` for a sanitized unexpected failure.

## Angular State and Components

A singleton `CommanderStore` built with Angular Signals owns the source collection, active-panel identity, and isolated left and right panel states. Each panel state contains its tabs, active tab ID, cursor, selection, sort, filter, and request status.

Each `DirectoryTab` owns a `sourceId` and logical path. The panel's current source mirrors its active tab. Selecting a source changes only the active tab to that source's root; other tabs retain their own source and path. Switching tabs restores both source and path.

Persisted state uses a versioned local-storage envelope. Invalid JSON, schema-version mismatches, removed sources, and invalid paths are repaired to safe defaults without preventing application startup. Selection and transient loading or error state are not persisted.

Each pane composes focused standalone components:

```text
CommanderPanelComponent
├── SourceSelectorComponent
├── DirectoryTabsComponent
├── PathBarComponent
├── QuickFilterComponent
└── FileTableComponent
```

The shell also owns the application header, panel splitter/layout, status region, and permanent command bar. Components dispatch user intent to the store and receive signal-derived view state; they do not call HTTP directly.

Each panel has its own request sequence. A response is applied only if it still matches the active tab's source, path, and request token, preventing slow earlier navigation from replacing newer results.

## Commander Interaction Model

The desktop view uses two equal, dense panes. Each pane presents compact source buttons, directory tabs, an editable logical path, filter control, sortable details headers, compact rows, and a status line. A strong accent border and header treatment identify the active pane. Inactive-pane selection remains visible with reduced emphasis.

The file table keeps the synthetic `[..]` parent row first, then directories, then files. Sorting applies within the directory and file groups. Name receives flexible width; extension, size, modified time, and attributes use compact fixed or bounded widths.

Cursor and selection are distinct. Mouse click moves the cursor and establishes selection, Ctrl+click toggles an item, Shift+click extends from the selection anchor, and Insert toggles the cursor item then advances. Parent rows are never selectable.

One document-level `CommanderKeyboardService` converts browser events into semantic commands. Components do not install competing global listeners. It supports:

- Arrow keys, PageUp/PageDown, Home/End for cursor movement;
- Enter to open the cursor item;
- Backspace to edit an active quick filter, otherwise navigate to the parent;
- Tab to switch the active pane;
- Insert to toggle selection and advance;
- Ctrl+A to select all visible real entries;
- Escape to clear the filter, selection, or active transient UI in priority order;
- Ctrl+L to edit the active path;
- Ctrl+R to refresh;
- Ctrl+T and Ctrl+W to create and close tabs; and
- F3 through F9 to activate the command bar, with unavailable milestone commands disabled.

When focus is inside a text input, the service preserves normal editing except for explicit application-level commands such as Escape. Printable input while a pane has focus starts or extends its quick filter. Browser defaults are prevented only for handled commands.

The default desktop layout keeps panes side by side. Narrow layouts stack both panes while preserving access to both and the permanent command bar. Semantic tables, labeled controls, tablist semantics, current/selected ARIA state, visible keyboard focus, sufficient contrast, reduced motion, and touch-operable command buttons provide accessibility.

## Error and Empty States

Loading, empty, unavailable, and error states belong to each panel. A left-panel failure never clears or blocks the right panel. Unavailable source buttons remain visible with warning treatment and an accessible explanation. Capacity is shown as unknown when the host platform cannot provide it.

Retry refreshes only the affected active tab. Client errors show logical source and path information but never assume or display host paths. Unexpected API details are logged server-side using structured fields and presented to users as a concise recoverable message.

## Testing Strategy

Backend unit tests cover:

- source parsing and startup validation;
- default and fallback source choice;
- source availability and capacity fallback;
- logical-path normalization;
- traversal, sibling-prefix, root-confinement, and symlink-escape cases;
- directory listing and metadata mapping; and
- read-only metadata exposure.

Backend integration tests use `WebApplicationFactory<Program>` with temporary configuration and source roots. They cover the sources, listing, information, unavailable-source, invalid-path, missing-entry, and sanitized-error contracts and assert that physical paths are absent from responses.

Angular unit tests cover active-panel switching, source changes, tab lifecycle and persistence, independent navigation, stale-response rejection, sorting, filtering, cursor movement, Insert, Ctrl+A, path editing, and supported shortcuts.

Playwright tests seed temporary Downloads and Media trees and verify source buttons above both panes, independent navigation, active-pane switching with Tab, keyboard navigation and selection, tab creation and restoration, filter behavior, and browser refresh persistence. Copy/move E2E scenarios are deferred until the corresponding APIs and transfer execution exist.

## Deployment and Operations

The multi-stage Dockerfile:

1. installs and builds the Angular 22 client with Node;
2. restores and publishes the .NET 10 API;
3. copies Angular's browser output into the published `wwwroot`; and
4. runs the ASP.NET Core application on port `8080` from the official runtime image.

Compose exposes `8092:8080`, runs as `1000:1000`, mounts configuration read-only, mounts only explicitly declared source roots, and includes a health check. It never mounts `/` or `/var/run/docker.sock`. The same image is configured for different hosts only through bind mounts and `sources.json`.

Milestone 1 has no authentication. Documentation therefore states that it must remain on a trusted network or behind an administrator-controlled authenticated reverse proxy until the authentication milestone is implemented.

## Documentation and Acceptance

The README documents purpose, architecture, Total Commander-inspired interaction, prerequisites, local development, Docker deployment, source configuration, read-only and unavailable sources, keyboard shortcuts, security boundaries, testing, known Milestone 1 limitations, and the roadmap.

Acceptance requires fresh evidence from:

- all backend unit and integration tests;
- all Angular unit tests;
- an Angular production build;
- Playwright's Milestone 1 browser suite;
- a .NET Release build and publish;
- a Docker image build;
- Docker Compose startup and health verification; and
- manual or automated browser verification that both source selectors render, panels navigate independently, and required keyboard commands behave correctly.

If an environmental dependency such as Docker is unavailable, the limitation and exact failed or unavailable verification step are reported rather than described as successful.
