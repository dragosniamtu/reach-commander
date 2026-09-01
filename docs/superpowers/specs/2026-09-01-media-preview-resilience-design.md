# Media Preview Resilience Design

## Problem

ReachCommander has one bounded FFmpeg worker. A session is currently labelled `Transcoding` as soon as it enters the queue, so the UI cannot distinguish waiting from active work. If a browser disappears before its `DELETE` request reaches the API, its FFmpeg process can occupy the worker until the general 20-minute session expiry. New sessions then appear to transcode forever even though FFmpeg is processing a different session.

## Approved behavior

- HLS sessions start in `Queued` and change to `Transcoding` only when the worker begins them.
- Closing or replacing a preview cancels that session. Queued or active sessions that stop polling are treated as abandoned after a short, configurable heartbeat window and are canceled independently of the 20-minute ready-session lifetime.
- Cancellation terminates the FFmpeg process tree and removes the session output. Startup recovery, failures, explicit close, heartbeat expiry, and ordinary expiry all leave no orphaned HLS directory.
- Lifecycle logs identify the session, source, display filename, transition, duration, process exit, cancellation reason, and cleanup outcome without exposing API tokens or credentials.
- The UI shows separate `Waiting for preview worker` and `Preparing browser-compatible video` states and continues polling both.
- The installer and `doctor` accept only the exact runtime shape below `/data/media-previews`: the root directory, 32-character lowercase hexadecimal session directories, `index.m3u8`, and `segment-NNNNNN.ts`. Symlinks, mount points, and every other entry remain rejected.

## Architecture

`MediaPreviewSessionStore` remains the in-memory authority. Stored sessions gain an explicit active-transcode flag so the first playable HLS segment does not hide that FFmpeg still owns the worker. `MediaPreviewService` owns transitions and logging. `MediaPreviewCleanupService` checks the normal expiry and the shorter pending/running heartbeat expiry on the existing periodic loop. `MediaTranscodeRunner` remains responsible for the child process boundary and emits process-level diagnostics. No database, SignalR channel, second worker, or durable media queue is introduced.

## Failure handling

An explicitly closed or abandoned session is removed, its lifetime token is canceled, and the worker treats cancellation as expected. A timeout, FFmpeg error, size-limit violation, or unexpected exception becomes a safe `Failed` API state and retains only bounded diagnostic output in server logs. A canceled queued item may remain briefly in the channel, but the worker skips it because the session no longer exists.

## Testing

- Unit tests prove the initial queued state, the worker transition, close cancellation, heartbeat removal, and safe cleanup.
- Process-runner tests preserve shell-free FFmpeg invocation and cover diagnostic behavior through a controllable fake runner where process execution is not required.
- Angular store/component tests prove polling and labels for both queued and transcoding states.
- Installer tests prove valid media-preview assets are preserved and unexpected entries or symlinks are rejected.
- Full .NET, Angular, installer, and browser acceptance suites remain green.
