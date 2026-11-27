# syntax=docker/dockerfile:1.7
FROM python:3.11-slim-bookworm AS dependencies
# ffsubsync's Linux WebRTC VAD dependency builds a native extension from source.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential ca-certificates \
    && rm -rf /var/lib/apt/lists/*
ENV PATH="/opt/subtitle-venv/bin:$PATH"
WORKDIR /build
COPY requirements.txt ./
RUN python -m venv /opt/subtitle-venv \
    && python -m pip install --no-cache-dir torch==2.9.1 --index-url https://download.pytorch.org/whl/cpu \
    && python -m pip install --no-cache-dir -r requirements.txt \
    && python -m pip check

FROM python:3.11-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 \
    HF_HOME=/tmp/huggingface \
    TOKENIZERS_PARALLELISM=false \
    OMP_NUM_THREADS=2 \
    OPENBLAS_NUM_THREADS=2 \
    PATH="/opt/subtitle-venv/bin:$PATH"
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg ca-certificates libgomp1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=dependencies /opt/subtitle-venv /opt/subtitle-venv
COPY worker.py download_models.py model_readiness.py process_launcher.py subtitle_sync.py ./
RUN mkdir -p /models /subtitle-work /tmp/subtitles \
    && chown 1000:1000 /models /subtitle-work /tmp/subtitles
USER 1000:1000
RUN python -c "from subtitle_sync import dependencies; _, vad, _, _ = dependencies(); assert not vad(3).is_speech(bytes(320), 16000)"
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 CMD ["python", "/app/worker.py", "healthcheck"]
ENTRYPOINT ["python", "/app/worker.py"]
CMD ["serve"]
