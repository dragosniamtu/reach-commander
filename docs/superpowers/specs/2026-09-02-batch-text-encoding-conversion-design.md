# ReachCommander Batch Text Encoding Conversion Design

## Summary

ReachCommander will add an authenticated **Encoding** tool for converting selected text files in the active file panel. The tool detects each source encoding, allows a manual source override, previews decoded text, and converts valid files to UTF-8, UTF-8 with BOM, UTF-16 LE, Windows-1250, or Windows-1252.

Conversion is deliberately non-destructive. Before publishing a converted file, ReachCommander preserves its exact original bytes as `<name>_original<extension>`. If that name exists, it uses `<name>_original (2)<extension>`, followed by the next available number. Every file is handled as an independent transactional operation, so one failure does not damage a file or prevent other valid rows from completing.

## Scope

The first release supports these case-insensitive extensions:

- `.srt`
- `.sub`
- `.txt`
- `.csv`
- `.nfo`
- `.md`
- `.json`

The tool accepts files only, from one writable configured source, with at most 100 selected files and at most 32 MiB per file. Directories, symbolic links, read-only sources, unsupported extensions, and binary-looking content are rejected. A binary-content guard rejects NUL characters and excessive non-text control characters, including binary `.sub` files.

The first release does not recurse into selected directories, combine files, change line endings, normalize Unicode, rename converted files, delete original backups, or expose every platform encoding. It does not accept arbitrary extensions merely because their bytes can be decoded.

## User Experience

The top toolbar gains an **Encoding** action beside Multi-Rename. It is enabled when the active panel has at least one selected file and its source is writable. Activating it opens a blocking batch conversion dialog above the commander panels.

The dialog contains:

- A source encoding selector, defaulting to `Auto`, with manual UTF-8, UTF-8 with BOM, UTF-16 LE, UTF-16 BE, Windows-1250, and Windows-1252 choices.
- An output encoding selector with UTF-8, UTF-8 with BOM, UTF-16 LE, Windows-1250, and Windows-1252 choices. UTF-8 is the default.
- A table containing filename, detected source encoding, confidence, status, and a short decoded-text preview for each selected file.
- A persistent explanation that the original bytes will be preserved using an `_original` backup name.
- **Convert files** and **Cancel** actions.

Changing the source override or output encoding requests a new preview. A low-confidence legacy detection is displayed as a warning and remains convertible; the preview lets the user verify characters before proceeding. Hard errors disable conversion for their rows. The primary action is enabled when at least one row is ready and no preview request is active.

During execution, the dialog shows overall progress and the current filename. The user may close only after confirming cancellation while work remains. Completion keeps the dialog open with per-file results and refreshes the active panel so converted files and backups appear immediately. Keyboard focus returns to the Encoding toolbar action when the dialog closes.

## Encoding Detection and Conversion

Automatic detection uses a deterministic precedence:

1. A UTF-8, UTF-16 LE, or UTF-16 BE byte-order mark selects that encoding with high confidence.
2. BOM-less bytes that pass strict UTF-8 decoding select UTF-8 with high confidence.
3. Other text-like bytes are decoded strictly as Windows-1250 and Windows-1252. If only one decoder succeeds, Auto selects it with medium confidence.
4. When both legacy decoders succeed, Auto selects Windows-1250 with low confidence and explicitly marks the row as a manual-review warning.

All decoders and encoders use exception fallbacks. ReachCommander never replaces invalid or unrepresentable characters with `?` or the Unicode replacement character. Manual source selection still requires the complete file to decode successfully. Before execution, preview fully validates that the decoded text can be represented by the selected output encoding.

Conversion preserves every decoded character and existing line ending. UTF-8 output has no BOM, UTF-8 with BOM includes its standard BOM, UTF-16 LE includes its BOM, and Windows code-page output has no BOM. Preview text is capped, escaped for safe HTML rendering, and never returned with host paths.

## Architecture and API Boundaries

The feature adds a dedicated text-encoding application boundary with three operations:

- Create or refresh a conversion preview from a source ID, logical file paths, a source selection, and an output encoding.
- Execute a short-lived server-authoritative preview plan.
- Read the tracked operation status and per-file results until it reaches a terminal state.

Angular sends only the active source ID and selected logical paths. The API resolves paths through the configured-source boundary and returns logical filenames, encoding labels, bounded preview samples, warnings, and opaque plan or operation IDs. Host filesystem paths and original full file content are never exposed.

The preview service records each file's normalized logical path, length, last-write timestamp, and content fingerprint. It validates count, size, extension, containment, source policy, symbolic-link status, text-likeness, strict decoding, and strict output encoding. Plans expire after 10 minutes, matching the other preview-first file operations.

Execution is a sequential background operation owned by the authenticated administrator session. Its state includes queued, running, cancel-requested, completed, completed-with-errors, canceled, and failed outcomes, along with current filename, completed count, total count, and per-file result. Terminal operation results remain available for one hour. Angular polls through the existing authenticated API client pattern. Mutating requests use the existing antiforgery protection and a dedicated rate-limit policy.

## Transactional File Conversion

Immediately before changing a file, execution repeats containment, source-policy, symbolic-link, extension, size, and fingerprint validation. A stale or externally changed file is skipped with an explicit result.

For each valid file, the backend:

1. Strictly decodes the original using the previewed or manually selected source encoding.
2. Strictly encodes the complete text using the selected output encoding.
3. Writes and flushes the converted bytes to a hidden staging file in the same directory.
4. Selects and reserves the next available `_original` backup filename, trying the base name and numbered variants through `(999)`.
5. Atomically renames the original file to the backup filename.
6. Atomically publishes the staging file at the original filename.
7. Flushes directory metadata where the platform supports it and records the logical backup name.

The backup retains the byte-for-byte original content. If publishing fails after the original was moved, the service restores the original name and removes the staging file. If rollback cannot complete, the result is `recovery_required` and reports only the relevant logical filenames. Existing backups are never overwritten.

Cancellation takes effect between files. A file whose transactional replacement has started is allowed to finish or roll back before execution stops. Conversion is sequential to bound memory, disk pressure, and collision handling.

## Failure Handling

Preview rows distinguish warnings from hard failures. Hard failures include unsupported extension, directory selection, symbolic link, read-only source, missing or inaccessible file, file too large, binary-looking content, invalid byte sequence, output encoding loss, and exhausted limits.

Execution results distinguish stale file, backup collision exhaustion, staging write failure, publish failure with successful rollback, and recovery-required rollback failure. Safe Problem Details responses use stable error codes and never expose physical paths, exception details, decoded full contents, or partial staging names outside the affected logical directory.

Temporary staging files are removed after success, ordinary failure, or cancellation. Startup cleanup removes encoding-conversion staging files older than 24 hours using their private naming contract, without touching user-created files or `_original` backups.

## Security and Resource Limits

Every endpoint requires the existing administrator session. Preview and execution enforce configured-source containment independently, reject parent traversal and symbolic links, and never trust extensions or frontend eligibility checks alone. Read-only sources cannot create plans or execute conversion.

The request is bounded to 100 logical paths, each logical path uses existing length limits, and each file is bounded to 32 MiB. Detection and conversion use bounded buffers and sequential processing. Preview samples contain at most 4 KiB of decoded text per row. Logs contain operation IDs, logical display names, encodings, byte counts, durations, and safe result codes, but not file contents or host paths.

## Testing

Unit and integration coverage will verify:

- BOM and BOM-less UTF-8 detection.
- UTF-16 LE and UTF-16 BE input detection.
- Windows-1250 Romanian diacritics and Windows-1252 smart punctuation.
- Ambiguous legacy detection, low-confidence warning, and manual source override.
- UTF-8, UTF-8 BOM, UTF-16 LE, Windows-1250, and Windows-1252 output bytes.
- Strict rejection when target encoding cannot represent a character.
- Exact original-byte backups and `_original`, `_original (2)`, and later naming.
- Successful atomic publication, failure before backup, failure after backup with rollback, and recovery-required handling.
- Stale-file detection, cancellation between files, and partial batch results.
- Count, size, extension, binary-content, traversal, symlink, and read-only rejection.
- Authentication, antiforgery, rate limiting, path redaction, plan ownership, and plan expiry.
- Angular selection eligibility, preview refresh, confidence warnings, sample rendering, progress, completion, cancellation, error states, panel refresh, keyboard focus, and narrow layouts.

## Acceptance Scenario

Given several writable `.srt` files selected in the active panel, including a Romanian Windows-1250 file with no BOM, the user opens **Encoding**. Auto marks the legacy file as likely Windows-1250, shows its Romanian characters correctly in the preview, and selects UTF-8 as output. After confirmation, the operation completes without replacement characters. For `episode.srt`, `episode_original.srt` contains the exact original Windows-1250 bytes and `episode.srt` contains valid BOM-less UTF-8 with identical subtitle text and timestamps. If a backup already exists, the new original is preserved as `episode_original (2).srt`. Unsupported, stale, read-only, or binary-looking rows remain unchanged and show an explicit result.
