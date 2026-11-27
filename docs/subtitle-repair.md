# Repair existing subtitles

Select an MP4, MKV or AVI video, open **Menu → Subtitles → Repair subtitles**, and choose an SRT
in the video's directory. The existing synchronization player also provides repair
controls. Repair keeps the subtitle's wording in its original language. It does
not generate a translation or change the video.

Review the decoded text first. UTF-8 and BOM-marked UTF-16 are recognized directly.
Legacy encodings can be ambiguous: select the source encoding whose preview shows
the correct characters, then confirm the text. Options cover Windows Central and
Eastern European, Cyrillic, Greek, Turkish, Hebrew, Arabic, Baltic and Vietnamese
encodings, Thai, Shift-JIS, GBK, Big5, Korean and Latin-1/2. Any language already
encoded as valid Unicode can retain its text. Unsupported byte encodings and
malformed SRT timestamps produce separate errors; changing encoding cannot repair
missing or corrupt subtitle content.

**Auto-sync** compares the subtitle intervals with speech activity in the video's
default audio track (or first audio track when no default is marked). It estimates
a constant offset and a global timing rate, allowing correction of progressive
drift. It uses local WebRTC speech detection and the pinned ffsubsync aligner on
CPU. It requires neither Whisper transcription nor downloaded translation models,
internet access during processing, an API key or a GPU.

Preview the result at several points before saving. Manual offset adjustment can
fine-tune the estimate. Noise, sparse dialogue or subtitles for a different edit
can produce an unusable or inaccurate estimate; one global transform cannot repair
arbitrary missing/inserted scenes. Cancelling or closing the preview publishes no
subtitle changes.

**Review save** shows the target and its unique backup name. Saving writes UTF-8 to
the original subtitle path and preserves its original bytes as `<stem>_original.srt`,
then `<stem>_original (2).srt` when needed. An encoding-only repair is also possible.
Read-only sources allow preview but cannot save. File changes after loading or
review invalidate the operation rather than overwrite a newer file.

## Deployment

Automatic repair requires the matching application and updated subtitle worker
image. **Existing installations must first refresh the host tools using the
checksum-verified installer from the release that includes repair.** Follow
[Upgrade host tools without reconfiguration](deployment/ubuntu.md#upgrade-host-tools-without-reconfiguration)
and run `sudo ./install.sh --keep-config` from that verified bundle. This preserves
configuration, accounts, media mappings and downloaded models. It also updates the
storage allowlist to recognize repair requests, results and readiness files.

An image-only `reachcommander update` does not refresh that allowlist. If an enabled
installation has already received the new images, old host tools can report
unexpected subtitle storage and block later updates. Refresh the verified
installer with `--keep-config`, then run `sudo reachcommander doctor`; retain the
mailbox and model files.

The refreshed installer selects the pinned worker image from the application
release. If the service is not already enabled, enable it with:

```bash
sudo reachcommander subtitles enable
```

No model download is required for repair. Model downloads remain necessary for
**Generate Subtitles**. A running older worker does not advertise synchronization;
the repair UI explains that it must be updated. Manual encoding and offset repair
remain available without the worker.

The single supervisor consumes generation and repair requests sequentially. Repair
uses separate `repair-requests` and `repair-results` mailboxes so generation cleanup
cannot cancel repairs, plus a model-independent `repair-status.json` heartbeat.
Only normalized timing cues enter the alignment request; the original decoded text
remains in the API. Only the API can save subtitles beside videos.

Temporary extracted audio is removed after completion, failure or cancellation;
the supervisor removes abandoned job directories on restart. Closing a browser
without closing the workspace leaves its existing session inactivity timeout to
cancel abandoned work. Repair has bounded file sizes, cue count, offset, timing
scale, worker runtime and CPU use. Updating or reconfiguring sources is blocked
while repair work remains active.

## Verification

Run worker contracts with `python -m unittest discover -s worker/subtitles/tests`.
With FFmpeg (including its flite test source) and the pinned synchronization
dependencies installed, `python tools/subtitle_sync_smoke.py` exercises the actual
worker mailbox against synthesized speech with known positive/negative shifts and
rate mismatches, and rejects silent audio. The worker image build checks the native
WebRTC dependency under its nonroot runtime identity.
