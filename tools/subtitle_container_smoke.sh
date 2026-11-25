#!/usr/bin/env bash
# Runs on disposable Linux CI hosts, against images built from this exact checkout.
set -Eeuo pipefail

umask 077
root="$(mktemp -d "${RUNNER_TEMP:-/tmp}/reachcommander-subtitles.XXXXXX")"
diagnostics='artifacts/subtitle-inference'
mkdir -p "$diagnostics"
cleanup() {
  local exit_code=$?
  trap - EXIT
  for service in reachcommander-subtitle-api reachcommander-subtitle-worker; do
    docker logs --tail 100 "$service" 2>&1 |
      sed -E 's/(ReachCommander first-run setup code: )[A-Za-z0-9_-]+/\1[REDACTED]/g' \
      >"$diagnostics/$service.log" || true
    docker rm --force "$service" >/dev/null 2>&1 || true
  done
  case "$root" in
    "${RUNNER_TEMP:-/tmp}"/reachcommander-subtitles.*) sudo rm -rf -- "$root" ;;
    *) printf 'Refusing unexpected smoke cleanup path\n' >&2; exit 1 ;;
  esac
  exit "$exit_code"
}
trap cleanup EXIT

mkdir -p "$root"/{config,data,media,models,mailbox}
# Fixed, checksum-verified test speech from the MIT-licensed upstream Whisper repo.
curl --fail --location --silent --show-error \
  https://raw.githubusercontent.com/openai/whisper/8cf36f3508c9acd341a45eb2364239a3d81458b9/tests/jfk.flac \
  --output "$root/media/jfk.flac"
printf '%s  %s\n' '63a4b1e4c1dc655ac70961ffbf518acd249df237e5a0152faae9a4a836949715' "$root/media/jfk.flac" | sha256sum --check
# Render the application's normal source schema before handing private paths to its UID.
python3 deploy/render_config.py create-request --output "$root/request.json" --uid 1000 --gid 1000 \
  --port 8092 --bind-address 0.0.0.0 --image ghcr.io/dragosniamtu/reach-commander:stable \
  --access-mode trusted-lan-http --cpu-limit 2.0
python3 deploy/render_config.py add-source --request "$root/request.json" --id speech --name Speech \
  --host-path "$root/media" --access rw --default-left true --default-right true
python3 deploy/render_config.py render --request "$root/request.json" --template deploy/compose.release.yaml --output "$root/deployment"
sudo chown -R 1000:1000 "$root"
sudo chmod 0755 "$root"
docker run --rm --user 1000:1000 --read-only --network none --cap-drop ALL --security-opt no-new-privileges \
  --mount "type=bind,source=$root/media,target=/media" --entrypoint ffmpeg reachcommander-subtitles:smoke \
  -hide_banner -loglevel error -f lavfi -i color=c=black:s=320x180:r=10 -i /media/jfk.flac \
  -shortest -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac /media/speech.mp4

# Use the same downloader shipped to users; only this setup container has network access.
docker run --rm --user 1000:1000 --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:size=256m,mode=1777 --memory 2g --cpus 2 \
  --mount "type=bind,source=$root/models,target=/models" \
  --entrypoint python reachcommander-subtitles:smoke /app/download_models.py --models /models --romanian

docker run --detach --name reachcommander-subtitle-worker --user 1000:1000 --read-only --network none \
  --cap-drop ALL --security-opt no-new-privileges --memory 6g --cpus 2 --pids-limit 256 \
  --tmpfs /tmp:size=768m,mode=1777 \
  --mount "type=bind,source=$root/models,target=/models,readonly" \
  --mount "type=bind,source=$root/mailbox,target=/subtitle-work" \
  --mount "type=bind,source=$root/media,target=/sources/speech,readonly" \
  --env 'SUBTITLE_MEDIA_ROOTS=["/sources/speech"]' reachcommander-subtitles:smoke >/dev/null

docker run --detach --name reachcommander-subtitle-api --user 1000:1000 --read-only \
  --tmpfs /tmp:size=32m,mode=1777 --cap-drop ALL --security-opt no-new-privileges \
  --env Authentication__AllowInsecureHttp=true --env SubtitleGeneration__Enabled=true \
  --env SubtitleGeneration__SharedPath=/subtitle-work --publish 127.0.0.1::8080 \
  --mount "type=bind,source=$root/deployment/config,target=/config,readonly" \
  --mount "type=bind,source=$root/data,target=/data" \
  --mount "type=bind,source=$root/mailbox,target=/subtitle-work" \
  --mount "type=bind,source=$root/media,target=/sources/speech" reachcommander:subtitle-smoke >/dev/null

healthy=false
for ((attempt=0; attempt<90; attempt++)); do
  api_health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' reachcommander-subtitle-api)"
  worker_health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' reachcommander-subtitle-worker)"
  if [[ "$api_health" == healthy && "$worker_health" == healthy ]]; then healthy=true; break; fi
  sleep 2
done
[[ "$healthy" == true ]]
port="$(docker port reachcommander-subtitle-api 8080/tcp)"
port="${port##*:}"
setup_code="$(docker logs reachcommander-subtitle-api 2>&1 | sed -n 's/.*ReachCommander first-run setup code: \([A-Za-z0-9_-]*\).*/\1/p' | tail -n 1)"
[[ -n "$setup_code" ]]
REACHCOMMANDER_SMOKE_SETUP_CODE="$setup_code" \
  sudo --preserve-env=REACHCOMMANDER_SMOKE_SETUP_CODE \
    python3 tools/subtitle_release_smoke.py --api-url "http://127.0.0.1:$port" \
      --source-root "$root/media" --container-source /sources/speech
printf 'Real Linux Docker CPU inference and API installation passed.\n' >"$diagnostics/result.txt"
