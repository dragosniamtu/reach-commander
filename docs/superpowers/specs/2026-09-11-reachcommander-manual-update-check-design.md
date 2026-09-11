# ReachCommander Manual Update Check Design

**Status:** Approved by direct implementation request
**Date:** 2026-09-11
**Scope:** Manual update discovery from the top toolbar

## Summary

The update icon will remain actionable whenever no check or update operation is currently running, including after an unavailable or failed initial status request. Clicking it manually retries the existing trusted host-side update check. If the fresh response discovers an update, the control changes to the available state; selecting it again opens the existing confirmation dialog, where installation still requires explicit administrator confirmation.

This is not a force reinstall. An up-to-date deployment remains unchanged, and the browser still cannot select a version, channel, image, digest, repository, URL, command, or path.

## Considered approaches

1. **Force reinstall the current image.** Rejected after clarification; it changes deployment behavior beyond the request.
2. **Keep unavailable states disabled and require a page reload.** Rejected because a transient helper/network failure can strand the control.
3. **Allow a manual retry through the existing target-free Check endpoint (selected).** This is the smallest change and preserves every updater security boundary.

## Behavior

- Current, unavailable, and not-yet-loaded states allow a manual check while idle.
- Checking, applying, blocked, completed, rolled-back, and failed operation states remain non-interactive until their existing flow resolves or is dismissed.
- Exact-version pins remain disabled.
- Unsupported platforms may be manually probed again; the backend remains authoritative and returns the same safe unsupported status when capability is still absent.
- A newly discovered update changes the control to the available state; a second selection opens the immutable confirmation dialog.
- No update is installed without the existing confirmation, mutation drain, health check, and rollback flow.

## Testing

- Component tests cover manual retry from unsupported-unavailable and missing states, and keep pinned/busy states disabled.
- Browser acceptance verifies the Check request contains no target data and that a discovered update becomes available through the existing confirmation flow.

## Acceptance criteria

- The update icon is usable after an unavailable or failed initial capability check.
- Clicking the icon issues only `POST /api/system-update/check` with a null body.
- A discovered update enables the existing confirmation action; a current or unavailable response remains a manual Check action.
- Pinned and busy states remain protected.
- Existing authentication, antiforgery, rate limiting, update validation, and rollback behavior is unchanged.
