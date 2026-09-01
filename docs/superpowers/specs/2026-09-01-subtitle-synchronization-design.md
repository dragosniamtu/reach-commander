# ReachCommander Subtitle Synchronization Design

## Summary

ReachCommander will add an authenticated subtitle synchronization workspace for a video and a same-folder SRT subtitle. Opening a supported video automatically selects the SRT with the same base filename when one exists. The user can choose another SRT, play and seek the video, apply one constant subtitle offset, and save the corrected subtitle without modifying the video.

Saving is deliberately non-destructive. For `movie.srt`, ReachCommander first preserves the original as `movie_original.srt`; if that name exists it uses `movie_original (2).srt`, then the next available unique name. The corrected subtitle is published as `movie.srt`. A failed save rolls back to the original state.

## Scope

The first release supports:

- MP4, MKV, and AVI video selection.
- SRT subtitles only.
- Automatic same-base-name SRT selection, with a dropdown of the directory's non-symlink SRT files.
- One constant positive or negative offset applied to every cue.
- Direct browser playback when the browser-compatible container and codecs can be served safely.
- Temporary FFmpeg preview output for media that cannot be played directly.
- Authenticated playback, seeking, subtitle loading, and transactional subtitle saving.
- Read-only preview from read-only sources; saving requires a writable source.

The first release does not include automatic speech matching, waveform alignment, two-point drift correction, subtitle editing, permanent proxy videos, subtitle downloads, embedded subtitle extraction, or formats other than SRT.

## User Experience

Double-clicking a video or pressing Enter opens a blocking media workspace above the commander panels. ReachCommander looks in the same source directory for `<video-base-name>.srt`, using the source's existing case-sensitivity rules. If no exact match exists, the workspace opens without subtitles and offers a same-directory SRT picker. The picker never exposes host paths.

The workspace contains:

- A video player with play/pause, seek, volume, current time, and duration.
- The active subtitle rendered as an HTML overlay so the offset can change immediately without regenerating media.
- The current cue and adjacent cue timing below the player.
- Offset controls for `-1 s`, `-100 ms`, `+100 ms`, and `+1 s`, plus an exact millisecond field and Reset.
- Quick seek actions for the beginning, middle, and final portion of the video so the user can validate a constant offset across the file.
- “Choose subtitle”, “Save corrected subtitle”, and Close actions.

Offset changes are preview-only until Save is confirmed. Closing the workspace discards unsaved offset changes and ends the preview session. Save is disabled when the offset is zero, the SRT is invalid or stale, or the source is read-only.

## Playback Architecture

The browser never receives a host filesystem path. Angular creates a preview session using a source ID and logical video path. The API resolves the path through the configured-source boundary, rejects parent traversal and symbolic links, fingerprints the file, probes its container/codecs, and returns an opaque session ID plus a playback mode.

For browser-compatible MP4 content, the API serves the original through an authenticated, same-origin byte-range endpoint. Range support allows the video element to seek without loading the complete file. The service uses the current HttpOnly same-origin authentication cookie, applies a dedicated media-preview rate-limit policy, emits no-store/private cache headers, and accepts only the session ID at the streaming endpoint.

For unsupported MP4, MKV, or AVI content, a bounded media worker invokes FFprobe/FFmpeg and produces temporary HLS preview segments in application-managed temporary storage. Angular uses native HLS playback where available and a pinned `hls.js` client elsewhere. The worker receives only validated input and output paths, runs without shell interpolation, and has bounded concurrency, runtime, segment count, output bytes, process descendants, and log capture. Video is encoded to H.264 and audio to AAC for broad browser compatibility. The original media is opened read-only and is never modified.

Only one transcode preview may run for the single administrator in the first release. A newer request cancels an older idle or active preview. Direct-play sessions and completed HLS sessions expire after 20 minutes of inactivity. Closing the workspace explicitly deletes its temporary output; startup and periodic cleanup remove abandoned sessions after crashes. Temporary preview files are excluded from support bundles.

## Subtitle Loading and Preview

The API validates and fingerprints the selected SRT, parses its cue sequence, and returns normalized cue data rather than exposing the subtitle file directly. The parser accepts UTF-8 with or without BOM and UTF-16 with a BOM. Invalid byte sequences, malformed timestamps, negative durations, overlapping parser limits, or files above the configured cue/byte ceiling are rejected with a safe Problem Details response. The corrected output is UTF-8.

Angular keeps the offset in workspace state and renders cues against `video.currentTime`. Positive offset values display every cue later; negative values display every cue earlier. Cues shifted before zero are clipped at zero for preview and save. A cue whose shifted end is not later than its shifted start makes the result invalid and blocks Save.

The preview session stores the original video and subtitle fingerprints. Directory refreshes or external changes do not silently redirect the open workspace.

## Transactional Save

Saving uses a short-lived server-authoritative plan rather than accepting rewritten subtitle contents from the browser. Angular submits the preview session ID and offset milliseconds. The server regenerates the adjusted SRT from the validated original cues and returns the intended backup name and validation result. The user confirms the final operation.

Execution revalidates authentication, source write policy, containment, symbolic-link status, original SRT fingerprint, video/session ownership, offset bounds, and destination conflicts. It then:

1. Writes and flushes the corrected SRT to a hidden staging file in the subtitle directory.
2. Selects and reserves the next unique `_original` backup name.
3. Atomically renames the original SRT to the backup name.
4. Atomically publishes the staged corrected file at the original SRT name.
5. Flushes the directory where supported and returns both logical filenames.

If publication fails after the original was moved, the service restores the original name and removes the staging file. If automatic rollback cannot complete, the API reports an explicit recovery-required result and never claims success. The operation does not overwrite an existing backup and does not modify any video file.

## API Boundaries

The feature adds a dedicated media-preview application boundary with operations equivalent to:

- Create a video preview session from a source ID and logical path.
- Read session status until direct playback or HLS output is ready.
- Stream authenticated direct-play ranges or HLS manifests and segments.
- Select or replace the session's same-directory SRT.
- Create a subtitle-adjustment save plan from the session and offset.
- Execute the confirmed save plan.
- Close and clean up a preview session.

Request DTOs contain only source IDs, normalized logical paths, opaque IDs, and bounded numeric values. Responses contain logical display names and never include host paths, FFmpeg command lines, cookies, or unrestricted logs. Mutating requests use the existing antiforgery mechanism. All endpoints require the administrator session.

## Failure Handling

The workspace distinguishes:

- Video changed or disappeared: close the stale session and reopen the video.
- No matching subtitle: choose an SRT from the same directory.
- Invalid or unsupported SRT: show a safe validation message and keep video playback available.
- Direct playback rejected by the browser: offer the FFmpeg fallback within the same session.
- FFmpeg unavailable, timed out, or resource-limited: stop the worker, clean temporary files, and show a retryable preview error.
- Read-only source: allow synchronization preview but explain why Save is unavailable.
- Subtitle changed externally: invalidate the save plan and require reloading.
- Save rollback failure: report the backup/staging logical names and require administrator recovery without exposing host paths.

## Configuration and Deployment

The production container includes pinned FFmpeg/FFprobe packages for Linux amd64 and arm64. Windows and macOS development continue through the same Docker image, so host-specific codecs are not required. The container health check verifies application health but does not run a transcode. CI verifies the FFmpeg binaries and their expected version output in the built image.

Media-preview limits are configuration-bound with conservative defaults for maximum subtitle bytes, cue count, offset magnitude, transcode duration, idle timeout, temporary bytes, and captured diagnostic output. Unsupported architectures fail the preview capability explicitly rather than allowing an unbounded host process.

## Testing

Unit and integration coverage will verify:

- Same-name SRT discovery and dropdown-based same-directory selection.
- Source containment, symlink rejection, path redaction, authentication, antiforgery, and rate limiting.
- Direct-play classification and correct HTTP range behavior.
- FFmpeg argument construction without shell evaluation, bounded cancellation, cleanup, and failure diagnostics.
- SRT parsing, positive/negative offsets, zero clipping, timestamp formatting, size/cue limits, and encoding rules.
- Unique `_original`, `_original (2)`, and later backup naming.
- Successful transactional save plus failures before and after backup rename, including rollback.
- Read-only source behavior and stale video/subtitle fingerprints.
- Angular playback state, offset controls, cue rendering, unsaved-close confirmation, focus restoration, and narrow layouts.
- Browser acceptance for direct MP4 playback, mocked fallback state, subtitle synchronization, confirmation, corrected file publication, and preserved original backup.

Container smoke coverage uses a short generated fixture to prove FFprobe availability and a bounded FFmpeg transcode. Large copyrighted media is never committed to the repository.

## Acceptance Scenario

Given `Family Movie.mp4` and `Family Movie.srt` in a configured source, opening the video loads the subtitle automatically. The user plays and seeks the video, selects `+1.4 seconds`, and verifies cues at multiple positions. On a writable source, Save previews `Family Movie_original.srt` as the backup, asks for confirmation, and completes atomically. `Family Movie.srt` then contains the corrected UTF-8 timestamps, `Family Movie_original.srt` contains the byte-for-byte original subtitle, and `Family Movie.mp4` remains unchanged. The same flow previews from a read-only source but never enables Save.
