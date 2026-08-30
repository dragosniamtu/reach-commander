# ReachCommander UI Source Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent Ubuntu host-folder source from the authenticated UI without exposing Docker or arbitrary root execution to the container.

**Architecture:** Add a versioned source-management action to the existing restricted Unix-socket helper. The API validates and forwards one narrow request, the root helper performs a serialized durable Compose/config transaction, and Angular reconnects after the managed container restart. Unsupported platforms expose a read-only capability response.

**Tech Stack:** ASP.NET Core/.NET 10, Angular 22, Python 3 host helper, Bash installer/CLI, Docker Compose v2, systemd, Vitest, xUnit, Playwright, shell harnesses.

## Global constraints

- Work directly on `master`; do not create a worktree.
- Do not modify, stage, or remove untracked `NC-theme.png`.
- Never mount `/var/run/docker.sock` into the application container.
- Never execute browser-supplied commands, Compose fragments, image references, or environment values.
- Preserve authentication, antiforgery, rate limiting, non-root runtime identity, update/reconfiguration recovery, and data persistence.
- Initial support is installer-managed Ubuntu only; every other deployment reports unsupported.
- Do not push unless explicitly requested.

### Task 1: Strict source-management host protocol

**Files:**
- Modify: `deploy/updater_protocol.py`
- Modify/Create: Python protocol tests under `tests/updater/`

- [ ] Add failing tests for the new exact request/response schemas, payload size, duplicate fields, UUID matching, protocol mismatch, action allowlist, name/path/access bounds, and public error sanitization.
- [ ] Introduce a separate source-management protocol version and typed request/response models without weakening update or diagnostics protocol parsing.
- [ ] Run the focused Python protocol tests and existing updater tests.
- [ ] Commit the protocol slice.

### Task 2: Durable source transaction and fixed management command

**Files:**
- Modify/Create: `deploy/source_management.py` and focused tests
- Modify: `deploy/reachcommander`
- Reuse/Modify: `deploy/render_config.py`, `deploy/lib/common.sh` only where shared invariants belong
- Modify: `tests/installer/test_install.sh`, management CLI tests, and fixtures

- [ ] Write failing tests for canonicalization, protected/broad/duplicate/overlapping paths, generated IDs, runtime UID/GID access, source-count bounds, unsafe installer state, and concurrent lock rejection.
- [ ] Write failing transaction tests for staged Compose validation, atomic replacement, service-only recreation, health verification, durable completion, rollback, failed recovery, and interrupted-transaction recovery.
- [ ] Implement a fixed `reachcommander source add` action accepting bounded structured input through stdin; use no shell evaluation.
- [ ] Reuse the installer renderer and validation invariants, and store a protected source-operation journal/transaction backup.
- [ ] Run installer, renderer, CLI, recovery, ShellCheck, and package tests.
- [ ] Commit the host transaction slice.

### Task 3: Restricted socket runtime action

**Files:**
- Modify: `deploy/updater_service.py`
- Modify: `deploy/systemd/reachcommander-updater.service`
- Modify: updater service/socket tests and package contracts

- [ ] Add failing tests for support discovery, one serialized source operation, fixed command/stdin invocation, status reads, timeout/failure mapping, update/source mutual exclusion, and bounded diagnostics.
- [ ] Route only the new source protocol to the fixed source command; keep update and diagnostic handlers unchanged.
- [ ] Persist operation state so a restarted application can retrieve the result.
- [ ] Confirm systemd write paths include only required installer state and source-transaction paths; keep the Docker socket absent from the container.
- [ ] Run all updater and installer packaging tests.
- [ ] Commit the host-service slice.

### Task 4: .NET application, gateway, and authenticated API

**Files:**
- Create/Modify: application source-management models, exceptions, coordinator, and operation-eligibility interfaces
- Create/Modify: infrastructure Unix-socket gateway and dependency registration
- Create/Modify: API contracts/controller/error mapping
- Modify: `src/ReachCommander.Api/Program.cs` only for required registrations/error mapping
- Create/Modify: xUnit tests in the corresponding test projects

- [ ] Add failing tests for supported/unsupported capability, strict response parsing, request ID/version matching, active-operation blocking, concurrent request rejection, cancellation, and sanitized failures.
- [ ] Add controller tests proving authentication fallback, antiforgery mutation enforcement, request validation, accepted/status responses, and unsupported deployments.
- [ ] Implement platform-neutral interfaces and an unavailable gateway default; bind the Unix gateway only when the configured socket is enabled.
- [ ] Reuse the existing file-operation/update probes to prevent restarting during active work.
- [ ] Run targeted and full backend tests on Windows; retain Linux-specific behavior behind abstractions.
- [ ] Commit the backend slice.

### Task 5: Angular Add source dialog and reconnect flow

**Files:**
- Modify: API models/port/client and their tests
- Create: source-management store/dialog components and tests
- Modify: commander toolbar/shell template, TypeScript, SCSS, help copy, and tests

- [ ] Add failing tests for capability loading, unsupported tooltip, dialog focus/escape behavior, name/path/access validation, RW warning/confirmation, duplicate submission prevention, and public errors.
- [ ] Add failing store tests for accepted operation polling, disconnect/reconnect, terminal success/failure/rollback, timeout messaging, and refreshing the source catalog.
- [ ] Implement one compact top-toolbar Add source control and a theme-compatible blocking dialog/overlay.
- [ ] On completion, reload the application/source catalog so the new source appears in both pane selectors without preserving stale cached definitions.
- [ ] Run focused Angular tests, all Angular tests, production build, and PWA verification.
- [ ] Commit the frontend slice.

### Task 6: End-to-end installer-managed acceptance and documentation

**Files:**
- Create/Modify: Playwright source-management specs and host fixtures
- Modify: installer/package tests and CI workflow only if new gates are required
- Modify: `README.md` and Ubuntu deployment documentation

- [ ] Add browser acceptance for unsupported installs and a fake installer-managed successful RO/RW source transaction with restart/reconnect.
- [ ] Prove generated source IDs, new source visibility, operation blocking, rollback messaging, and absence of Docker-socket mounts.
- [ ] Document prerequisites, specific-path requirement, UID/GID permissions, read-only default, restart behavior, troubleshooting, and CLI fallback.
- [ ] Run complete backend matrices available locally, all Python/Bash installer gates, Angular/PWA/build gates, and full Chromium acceptance.
- [ ] Review security boundaries and full implementation diff; fix findings before completion.
- [ ] Commit documentation/acceptance changes and report without pushing.
