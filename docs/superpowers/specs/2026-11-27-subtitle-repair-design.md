# Existing subtitle repair

Status: approved on 2026-11-27, including preservation of subtitles in any language.

## Problem and existing behavior

The user has an existing Romanian subtitle that fails to load, possibly because
of its encoding, and also has incorrect timing. Preserve its existing translation.
The exact file and timing defect have not been inspected. Repair is language-independent;
Romanian is an acceptance example, not a language restriction. Unicode supports any
subtitle language; explicit legacy encoding choices must cover other common scripts.

ReachCommander already provides an Encoding tool with strict decoding, a text
preview, source-encoding override, UTF-8 conversion and byte-exact backups. Its
Synchronize subtitles workspace accepts same-directory SRT files and supports
manual constant offsets. Automatic timing estimation and gradual drift correction
are missing. Invalid SRT structure and unsupported encoding are separate problems.

## Approaches

1. **Recommended: reuse the encoding codec and add local automatic synchronization.**
   One guided repair flow preserves Romanian text and estimates timestamps from
   audio speech activity. It addresses both reported symptoms.
2. **Connect the existing manual tools.** Lower implementation cost, but the user
   must find the offset and cannot correct progressive drift with a single offset.
3. **Regenerate subtitles with Whisper and translation.** Already available, but
   produces new wording and takes more processing; it does not preserve the user's
   existing translation, so it does not meet this request as well.

## User flow

- Select a supported video and choose **Repair subtitles** from Menu, or open its
  existing synchronization workspace. Choose an SRT beside that video.
- Analyze encoding using the existing codec. Show decoded sample text, the detected
  encoding and a source-encoding selector. Ambiguous legacy detection requires
  explicit review before continuing. Preserve Romanian diacritics and do not
  silently replace undecodable characters. Reuse the Encoding tool's codec and
  extend its explicit source choices for common Cyrillic, Greek, Arabic, Hebrew,
  Turkish, Baltic, Vietnamese, Thai and East Asian legacy encodings.
- Validate SRT structure separately. Explain malformed timestamps or unsupported
  formats without claiming that encoding conversion will repair them. Keep video
  playback available when a subtitle cannot be loaded.
- **Auto-sync** runs a cancellable CPU job against the video's audio. Retain manual
  offset controls for fine tuning. Support a constant offset and global time-scale
  correction for progressive drift; arbitrary edits between different cuts of a
  film are outside this first version's promised capability.
- Preview the proposed timing at the beginning, middle and end. Report estimated
  offset and time-scale adjustment, rather than claiming guaranteed accuracy.
- **Review save** shows the target and backup. **Save repaired subtitle** writes
  UTF-8 to the selected SRT and preserves its original bytes at the next available
  `_original` name. Encoding-only repairs are allowed. No write happens during
  analysis or preview; video contents and decoded subtitle wording remain unchanged.

## Implementation boundaries

Extend the existing media-preview flow, rather than adding a second player or
encoding implementation. Decode into session memory, retain original fingerprints,
and render saved cues from the original decoded text using server-validated timing
parameters. Extend the transactional subtitle save path to support encoding-only
and offset-plus-scale changes, retaining rollback, unique backups, read-only source
policy, stale-file checks and mutation coordination.

Use a pinned ffsubsync dependency with WebRTC voice activity detection in the
existing restricted Python worker. Extend its versioned mailbox with an explicit
synchronization job kind and bounded normalized subtitle input. Retain generation
compatibility, cancellation, process limits, read-only media mounts and the API's
exclusive responsibility for final subtitle writes. Automatic repair readiness
depends on the worker and synchronization dependency, not downloaded Whisper or
translation models. Missing capabilities produce actionable UI messages.

Process one worker job at a time using the existing conservative CPU limits. Work
offline, without an API subscription or GPU. Reject invalid alignment output and
out-of-range timestamps. Keep failed or cancelled jobs from publishing changes;
clean temporary audio and scratch files after completion, failure, cancellation
and recovery from interrupted work. Publish matching application and worker images
through the existing release process when release publication is authorized.

## Verification

Cover Windows-1250 Romanian text to UTF-8, Unicode input, ambiguous decoding review,
malformed SRT, encoding-only saves, offset and scale transforms, unchanged decoded
text, unique byte-exact backups, read-only sources and stale-file rejection.

Exercise a real local synchronization dependency with synthetic speech-activity
fixtures and known shifts/drift, plus cancelled/failed jobs and temporary cleanup.
Check worker compatibility and readiness without transcription models. Browser
coverage must prove selection, encoding review, progress, preview, save and focus
restoration in all three existing themes. Regression checks include existing manual
subtitle synchronization, generation, encoding conversion and Menu actions.

## Sources consulted

- [FFsubsync documentation](https://ffsubsync.readthedocs.io/en/latest/):
  language-independent alignment and offset/framerate correction.
- [Advanced options](https://ffsubsync.readthedocs.io/en/latest/advanced.html):
  WebRTC detection and limits of a global timing transform.
- [Project and MIT license](https://github.com/smacke/ffsubsync).

The specific user's subtitle has not been supplied or tested. Encoding and timing
repair cannot reconstruct a missing translation or guarantee alignment to a
different movie edit.
