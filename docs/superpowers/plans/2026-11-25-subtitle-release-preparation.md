# Subtitle Release Preparation Implementation Plan

**Goal:** Ship the CPU subtitle feature through the existing verified Ubuntu installation and update flow, with observable readiness and real inference verification before stable publication.

**Architecture:** Keep the single local CPU worker, private filesystem mailbox and API-owned subtitle transactions. Publish the worker separately and bind its immutable GHCR digest to the application through the `io.reachcommander.subtitle-worker-image` image label. The installed helper derives the worker configuration from trusted application metadata and existing source mappings.

**Tech stack:** Existing .NET 10, Angular 22, Python 3.11, Docker Compose, Bash installer and GitHub Actions.

## Constraints

- CPU only: Whisper small/INT8, two threads, two CPU quota, 6 GB worker memory; local Marian English-to-Romanian translation.
- Subtitle generation remains opt-in. Download models only on explicit setup; serving stays offline.
- Preserve authentication, source configuration, operation journals, media and existing subtitles across upgrades.
- Worker image references must match `ghcr.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}`. Separate `subtitles-*` tags share the existing public package, avoiding a newly private GHCR package. Verify the worker component label and anonymous pulls before promotion. Never execute browser-selected commands or image references.
- Older application images without a valid worker label require disabling subtitles before downgrade. Preserve models and jobs.
- No stable publication until all release gates pass. Docker/real Linux evidence must not be inferred from mocked tests.

## Task 1 — Installed lifecycle

Owned files: `deploy/`, deployment tests, Ubuntu installation documentation.

- [x] Add tested `subtitle_management.py` and package it with the installer.
- [x] Support `reachcommander subtitles enable|disable|status|download-models|recover`.
- [x] Generate hardened worker configuration using the application's existing source IDs and container paths; mirror sources read-only and keep private mailbox/models outside authentication data.
- [x] Integrate source changes, updates, rollback, start/stop/restart, installer refresh, doctor and retained-data handling.
- [x] Test missing/invalid image metadata, model preservation, source synchronization, transactional failure and unsupported downgrade behavior.

## Task 2 — Worker and API readiness

Owned files: worker, Application/Infrastructure subtitle generation, API controller, backend tests.

- [x] Publish atomic bounded `/subtitle-work/status.json`: `{version:1,updatedAt:UTC,englishAvailable:boolean,romanianAvailable:boolean}` every five seconds.
- [x] Validate pinned model manifests/files offline; cache verification without inference in the supervisor.
- [x] Add `worker.py healthcheck` for fresh serving-loop heartbeat, separately from model readiness.
- [x] Add authenticated `GET /api/subtitle-jobs/capabilities`: `{enabled,workerAvailable,englishAvailable,romanianAvailable,reasonCode,detail}`.
- [x] Reject unavailable submissions promptly, and detect a disappeared worker without waiting for the six-hour inference timeout.
- [x] Test disabled/no-directory-creation, invalid/stale/future heartbeat, unavailable selected language, recovery and long-running valid work.

## Task 3 — Readiness UI

Owned files: Angular subtitle feature/commander integration and subtitle-generation browser acceptance spec.

- [x] Load capabilities without blocking ordinary file-manager initialization.
- [x] Explain disabled, offline and missing-model states with Recheck; prevent submission when selected languages are unavailable.
- [x] Keep history/cancel available; stop unnecessary disabled-state polling.
- [x] Verify unit and browser flows for readiness, submission, progress, cancellation and restored jobs.

## Task 4 — Real inference and publication gates

Owned files: CI workflow, tools, CI/workflow tests, release notes and subtitle runbook.

- [x] Add a Linux Docker smoke using real pinned models, short speech, offline/non-root worker, and actual API installation/backup/skip behavior.
- [x] Test smoke output validation independently, including bilingual timing and preserved video/original subtitle hashes.
- [x] Configure worker amd64/arm64 publication with attestations before the application candidate; attach its digest label to both application platforms.
- [x] On retry, use the worker digest recorded in the existing immutable application image; verify architecture manifests and revision before promotion.
- [x] Keep publication conditional on all real-container and existing application gates; add real installed lifecycle verification before promotion.
- [x] Exercise local model inference in an isolated workspace virtual environment if Docker remains unavailable; record it separately from Linux evidence.

## Task 5 — Release candidate verification

- [x] Run .NET, Angular unit/build/PWA, worker, installer, workflow and browser suites locally.
- [ ] Review merged implementation and operational instructions, then commit to a `codex/` branch and run hosted CI before release promotion.
- [x] Prepare v1.5.0 release notes and verified installer-refresh instructions.
- [ ] Publish only after required checks and real-container validation pass; report unresolved environmental or external blockers accurately.

## Evidence ledger

- Baseline release audit: 25 worker tests and 41 release/documentation tests passed; feature changes were uncommitted; v1.4.1 was latest public release.
- Local Windows host has neither Docker nor an installed WSL distribution. Do not install a system hypervisor or reboot it as part of this task.
- Parallel ownership: installer, backend/worker readiness, UI; root owns CI/smoke/release integration and final verification.
- Local verification: 1,066 .NET tests, 580 Angular tests, 83 browser tests and 43 release/documentation contracts passed. Worker tests passed with four Windows-inapplicable skips; deployment Python and Bash fixtures passed with documented POSIX-specific skips. Production UI/PWA and ShellCheck passed.
- Real local CPU inference: pinned Whisper small and Marian models produced English/Romanian SRT with aligned timing; an actual API job preserved the original subtitle/video and skipped the verified rerun. This is Windows evidence, separate from the pending Linux Docker gates.
- Hosted verification still required: Linux inference, Linux process lifetime/permissions, installed Docker lifecycle, multi-platform registry checks and publication. Do not mark stable publication complete until these pass.
