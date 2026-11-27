# Local AI subtitles

To fix an existing subtitle's encoding and timing while keeping its text in any
language, use [Repair subtitles](subtitle-repair.md). Automatic repair uses the
local CPU worker without requiring Whisper or translation model downloads.

Select a video or folder in ReachCommander, choose **Menu → Subtitles → Generate Subtitles**, leave English checked and optionally check Romanian, then choose **Generate**. At least one language is required. Folder jobs recursively discover MKV, MP4, AVI, MOV, M4V and WebM files, process one video at a time, and continue after individual failures. Progress and results survive a browser refresh. Cancel stops the active worker process tree; interruption does not install partially generated subtitles.

| Selection | Output for `Movie.mkv` |
| --- | --- |
| English | `Movie.srt` |
| English + Romanian | `Movie.srt`, `Movie.ro.srt` |
| Romanian only | `Movie.srt` (Romanian) |

The API validates every requested output before preserving existing associated SRT, ASS, SSA and VTT files under unique `.existing-YYYYMMDD-NNN.ext.bak` names and installing the new files. Embedded subtitles and videos are untouched. Ambiguous sidecar ownership fails safely. Successful videos can be skipped on rerun while their video fingerprint, requested languages, and installed output hashes still match.

Generation uses Whisper word timestamps to set both the beginning and end of each
caption, preserving silent gaps instead of extending captions until the next line.
Romanian translation retains the same timing. Previously generated SRT files keep
their old timestamps until regenerated; turn off **Skip successfully processed
videos** when rerunning an unchanged video. The existing subtitles are backed up.

## Installed Ubuntu releases

Refresh the installation once using the [checksum-verified Ubuntu installer](deployment/ubuntu.md). An application-only update cannot add the new host management command. Then enable the CPU worker and explicitly download its free models:

```bash
sudo reachcommander subtitles enable
sudo reachcommander subtitles download-models
sudo reachcommander subtitles status
sudo reachcommander doctor
```

The worker uses Whisper small/INT8, two CPU threads and a 6 GB memory limit. No GPU, subscription or API key is required. Enable uses the immutable worker image recorded by the installed application; both images are published in the existing public GHCR package using separate application and `subtitles-*` tags. The installer manages the private mailbox/model cache and mirrors existing source mappings into the worker read-only. Only configured writable sources permit the API to install subtitles.

Models and jobs survive normal updates and disable/enable cycles. Source changes regenerate both services' mappings. Disable subtitles before deliberately downgrading to an older application image without worker support. `sudo reachcommander subtitles disable` stops generation without deleting models or sidecars. See the Ubuntu guide for recovery and retained-data details. The macOS bootstrap and manual deployments use the source-build instructions below; the new host manager targets Ubuntu.

## Docker from a source checkout

The optional `compose.subtitles.yaml` overlay adds a Python worker. The base application remains usable without it. Run commands from the repository root; use an API image built from this revision.

1. Choose the existing media directories and an isolated state directory. The examples use the repository's development mounts:

   ```bash
   export SUBTITLE_STATE_ROOT="$PWD/artifacts/subtitles"
   export SUBTITLE_MEDIA_HOST_PATH="$PWD/dev-sources/media"
   export SUBTITLE_DOWNLOADS_HOST_PATH="$PWD/dev-sources/downloads"
   export REACHCOMMANDER_UID=1000 REACHCOMMANDER_GID=1000
   sudo install -d -m 0750 -o "$REACHCOMMANDER_UID" -g "$REACHCOMMANDER_GID" \
     "$SUBTITLE_STATE_ROOT/mailbox" "$SUBTITLE_STATE_ROOT/models"
   ```

   Persist these settings in the deployment `.env` when appropriate. Do not put the mailbox or model cache inside an authorized media source or the API authentication directory (`/data`). The API and worker need the same UID/GID, or a private shared group with suitable directory permissions. Keep these directories inaccessible to other host users.

2. Enable writes only on sources where subtitle installation is intended. Set that source's `readOnly` to `false` in `config/sources.json`, or use the existing source management controls. The API service UID needs directory write/rename permission alongside the videos. The overlay changes the API's `/sources/media` and `/sources/downloads` mounts to writable; the worker always receives read-only copies. If only one source should permit writes, keep the other API mount `read_only: true` in your deployment overlay. Use ownership or a narrowly scoped group/ACL; do not use `chmod 777`. Filesystem permissions and the source configuration must both allow installation.

3. Build the CPU image and explicitly download models. This is the only worker command that needs the internet:

   ```bash
   docker compose -f compose.yaml -f compose.subtitles.yaml build subtitle-worker
   docker compose -f compose.yaml -f compose.subtitles.yaml run --rm subtitle-model-download
   ```

   This downloads the pinned Whisper small and Marian English-to-Romanian models, approximately 800 MB combined. Each model directory receives `download-manifest.json` containing repository, exact revision, license, file SHA-256 hashes and installed package versions. To prepare English only, replace the last command with:

   ```bash
   docker compose -f compose.yaml -f compose.subtitles.yaml run --rm subtitle-model-download --models /models
   ```

4. Start the application and worker:

   ```bash
   docker compose -f compose.yaml -f compose.subtitles.yaml --profile subtitles up -d --build
   ```

   The runtime worker has `network_mode: none`, no ports, no Docker socket, no authentication data mount, a read-only root, read-only models/media, no capabilities, and `no-new-privileges`. It writes only the mailbox and bounded temporary audio under `/tmp`. The separate download service mounts only the writable model cache and temporary storage. Its setup profile is not started by `--profile subtitles`.

For custom deployments, Compose resolves relative paths against the first Compose file. Use an absolute worker build context or prebuild `reachcommander-subtitles:local` and omit `build`, and include the overlay in subsequent deployment commands. Keep existing API authentication/configuration mounts. Additional source roots must be mounted read-only into the worker at the API's exact container paths and added to `SUBTITLE_MEDIA_ROOTS`. Installer-managed Ubuntu deployments use the commands above instead of adapting this development overlay.

## Local development

Python 3.11 is the container target. Install FFmpeg and ffprobe on `PATH`. In a dedicated environment:

```bash
python3.11 -m venv artifacts/subtitles/venv
. artifacts/subtitles/venv/bin/activate
python -m pip install torch==2.9.1 --index-url https://download.pytorch.org/whl/cpu
python -m pip install -r worker/subtitles/requirements.txt
python worker/subtitles/download_models.py --models "$PWD/artifacts/subtitles/models" --romanian
export SUBTITLE_SHARED_PATH="$PWD/artifacts/subtitles/mailbox"
export SUBTITLE_MODEL_PATH="$PWD/artifacts/subtitles/models"
export SUBTITLE_TEMP_PATH="$PWD/artifacts/subtitles/tmp"
export SUBTITLE_MEDIA_ROOTS='["/absolute/path/to/media","/absolute/path/to/downloads"]'
python worker/subtitles/worker.py serve
```

Use corresponding PowerShell `$env:NAME = 'value'` assignments on Windows, absolute Windows paths in the JSON root array (escape backslashes), and the virtual environment's `Scripts/Activate.ps1`. Configure the API with `SubtitleGeneration__Enabled=true` and `SubtitleGeneration__SharedPath` equal to the same local mailbox path. Its existing authorized sources must resolve to paths in the worker's allowlist. Stop the worker with Ctrl+C. Linux is the hardened production target: it pins input descriptors through `openat`/`O_NOFOLLOW` and uses parent-death signals for child/decoder crash cleanup. Windows development rejects symlinks/reparse points and starts each suspended child inside a kill-on-close job object before resuming it; cancellation and supervisor death terminate its descendants. It lacks Linux descriptor-based race protection and should use media directories that other processes cannot concurrently rename.

## Resource bounds and behavior

| Setting | Default |
| --- | --- |
| `SUBTITLE_THREADS` | 2; range 1–32 |
| `SUBTITLE_JOB_TIMEOUT` | 21600 seconds per video |
| `SUBTITLE_MAX_DURATION` | 14400 seconds (4 hours) |
| `SUBTITLE_CPU_LIMIT` / `SUBTITLE_MEMORY_LIMIT` | 2 CPUs / 6 GB in Compose |
| Temporary audio | Mono PCM, 16 kHz, about 115 MB/hour; `/tmp` tmpfs 768 MB |
| ffprobe / FFmpeg timeout | 30 seconds / 30 minutes |
| Mailbox request / result bounds | 32 KiB / 16 MiB; each SRT at most 8 MiB |

The API defaults to eight active/queued jobs, 10,000 discovered videos and 10,000 discovered directories per selection. It also caps escaped path metadata at 4 MiB and each persisted job at 8 MiB; a larger selection fails with guidance to select a smaller folder. Each new submission trims older terminal history to the latest 100 jobs. These bounds keep recursive discovery and refresh/restart state manageable. Worker cancellation acknowledgements are collected after jobs and every ten seconds while idle; unacknowledged requests are bounded by the configured queue capacity.

Only one supervisor may hold the mailbox lock, and it starts exactly one model child at a time. Increase tmpfs and memory limits together if increasing the maximum media duration. CPU subtitles may take minutes or longer than playback time on low-power hardware. The worker chooses the audio stream flagged as default, otherwise the lowest-index audio stream. It detects speech language using Whisper, transcribes English, and translates non-English speech into English. Romanian is translated locally from that English transcript using Marian. Whisper does not produce Romanian translation output.

SRT output is UTF-8 with ordered, non-overlapping, nonempty cues and at most two 42-character lines. Long cues are split for readability; when bilingual text requires splitting, both outputs receive identical times. Additional split times are interpolated within the model's segment boundaries, so they are approximate. This MVP does not offer manual audio-track selection, speaker labels, GPU execution, automatic watchers, subtitle downloading or quality guarantees. Mixed-language dialogue, music, overlapping speech, accents and names can need manual corrections. A missing model, audio-less video, malformed/unrecognized duration, insufficient temporary space or failed translation fails that video and preserves existing subtitles. English-only setup must download Marian before Romanian jobs can succeed.

## Mailbox v1

The API owns durable jobs and installation journals under its existing authentication data root. Only inference messages use the independent shared directory:

* `status.json`: atomic `{ "version": 1, "updatedAt": "<UTC ISO timestamp>", "englishAvailable": true, "romanianAvailable": true }`, refreshed every five seconds. The supervisor validates pinned model files and manifest hashes offline. A fresh heartbeat proves the serving loop is responsive even while model setup is incomplete; the model flags separately control which jobs can start.

* `requests/<32 lowercase hex id>.json`: atomically published `{ "version": 1, "id": "...", "videoPath": "/sources/media/Movie.mkv", "romanian": true }`.
* `requests/<id>.cancel`: cancellation marker. Pre-cancelled requests never start a child. Runtime cancellation or request disappearance kills the process tree and removes temporary audio before publishing `cancelled`.
* `results/<id>.json`: atomic `{ "version": 1, "id": "...", "state": "running|completed|failed|cancelled", "stage": "...", "percent": 0, "englishSrt": null, "romanianSrt": null, "errorCode": null }`.

SRT fields are populated only for completion. English is always computed, including Romanian-only jobs; the API chooses the installed outputs and filenames. The worker never returns output paths. It independently rejects paths outside its explicit roots, traversal, symlinks/reparse points, unsupported extensions, nonregular inputs, malformed schemas and duplicate JSON keys. FFmpeg/ffprobe receive argument arrays, local protocol and format allowlists, and bounded execution/output. Worker errors contain codes, never raw exception strings or decoder logs. Codes include `invalid_request`, `path_denied`, `no_audio`, `probe_failed`, `media_too_long`, `insufficient_space`, `extraction_failed`, `models_missing`, `transcription_failed`, `translation_failed`, `invalid_subtitles`, `timeout` and `worker_failed`.

The supervisor alone publishes terminal results after the child exits and temporary files are cleaned. Terminal results are not reprocessed. The API removes request/result/cancel files after consuming a terminal result and cancels abandoned work during API startup. A worker-only restart may retry an interrupted request safely because it never installs sidecars. The supervisor holds an OS lock and cleans only its strictly named private temporary directories on startup. Do not run multiple API instances against this single-host mailbox.

The authenticated `/api/subtitle-jobs/capabilities` endpoint reports enabled state, worker availability and language readiness without physical paths. The dialog explains disabled/offline/missing-model states and offers Recheck. Heartbeats older than 30 seconds are unavailable; an active job allows 15 additional seconds of worker-loss grace before failing, independent of the six-hour limit for a responsive inference worker. Healthy long-running transcription does not need to emit segment progress to remain alive.

After an API restart, previously running jobs are marked interrupted. Select the same folder again: verified completed files are skipped and unfinished files can be processed. Queued jobs remain queued. Installation journals recover before work resumes. If an original, backup, staged file, video or source mapping changed externally during a transaction, recovery fails closed for that basename and retains the files and private journal for administrator inspection. Stop ReachCommander before inspecting its `subtitle-generation/transactions` state and the matching media directory; do not delete a journal or backup merely to dismiss an error. Ordinary write failures roll back automatically. ReachCommander's mutation locks coordinate its own operations; host processes should not concurrently replace media directories or sidecars during installation.

An abrupt process kill during an incomplete staging write can leave a partial file without a persisted content proof; recovery conservatively retains that file for inspection. Video skip fingerprints use path, size and modification time, while generated subtitles are SHA-256 checked. A video replaced with different content but identical size and modification time may require explicit regeneration (`skipExisting: false` through the API).

## Verification and first movie

The test suite requires only Python and never imports/downloads AI dependencies:

```bash
python -m unittest discover -s worker/subtitles/tests -v
```

Tests cover strict protocol/path checks, symlink rejection, default audio selection, no audio, English/translated transcription selection, bounded subprocess calls, readable matching bilingual SRT timing, mocked generation, cancellation, terminal publication, readiness and temporary cleanup. The CI job runs these contracts under Python 3.11 on Linux. Tests that use FFmpeg skip when it is unavailable. A separate required Linux Docker job builds the real worker, downloads pinned models, then runs transcription/translation with runtime networking disabled. It verifies actual API installation, preservation of the video and original subtitle, matched bilingual timing and rerun skipping. A real inference fixture verifies captions end before long internal and trailing silences, including after Romanian translation. Publication verifies both architectures and the application's exact paired worker before promoting release tags.

To try one movie after setup, select an existing short movie in a writable source, choose **Generate Subtitles**, select English and Romanian, and watch its job reach completion. Check `Movie.srt` and `Movie.ro.srt`, play them in Jellyfin, and verify any previous sidecars have unique `.bak` backups. Rerun with skipping enabled and confirm the file is skipped. `docker compose -f compose.yaml -f compose.subtitles.yaml --profile subtitles ps` confirms the worker container is running. For a missing-model error rerun the explicit download command; do not enable runtime internet access.

## Models and upstream references

The selected model revisions are fixed in `worker/subtitles/download_models.py`, independently of package version pins:

| Model | Revision | License |
| --- | --- | --- |
| [Systran/faster-whisper-small](https://huggingface.co/Systran/faster-whisper-small) | `536b0662742c02347bc0e980a01041f333bce120` | MIT |
| [Helsinki-NLP/opus-mt-en-ro](https://huggingface.co/Helsinki-NLP/opus-mt-en-ro) | `96e8fea9e44dae942c2e8a9078dc1615dd871ed9` | Apache-2.0 |

Primary references checked for this implementation: [faster-whisper CPU/int8 and transcription API](https://github.com/SYSTRAN/faster-whisper), [Marian translation model API](https://huggingface.co/docs/transformers/en/model_doc/marian), [Hub revision-pinned downloads](https://huggingface.co/docs/huggingface_hub/en/guides/download), and [PyTorch CPU wheel installation](https://pytorch.org/get-started/previous-versions/). The worker uses direct Marian model/tokenizer classes and local files only. PyTorch's restricted `weights_only` loader reads the pinned upstream Marian checkpoint. Repository model cards are downloaded with the weights. faster-whisper is MIT, Transformers/Hub are Apache-2.0, and PyTorch is BSD-style; consult the installed packages for their full notices and transitive dependency licenses. The Debian FFmpeg package includes its distribution copyright/license documentation in the worker image; this image is distinct from the application's Alpine FFmpeg package.
