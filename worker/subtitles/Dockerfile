# syntax=docker/dockerfile:1.7
FROM python:3.11-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 \
    HF_HOME=/tmp/huggingface \
    TOKENIZERS_PARALLELISM=false \
    OMP_NUM_THREADS=2 \
    OPENBLAS_NUM_THREADS=2
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg ca-certificates libgomp1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY requirements.txt ./
RUN python -m pip install --no-cache-dir torch==2.9.1 --index-url https://download.pytorch.org/whl/cpu \
    && python -m pip install --no-cache-dir -r requirements.txt \
    && python -m pip check
COPY worker.py download_models.py model_readiness.py process_launcher.py ./
RUN mkdir -p /models /subtitle-work /tmp/subtitles \
    && chown 1000:1000 /models /subtitle-work /tmp/subtitles
USER 1000:1000
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 CMD ["python", "/app/worker.py", "healthcheck"]
ENTRYPOINT ["python", "/app/worker.py"]
CMD ["serve"]
