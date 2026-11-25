"""Explicit, network-enabled setup command. Never called by the runtime worker."""
import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path

MODELS = {
    "whisper-small": {
        "repository": "Systran/faster-whisper-small",
        "revision": "536b0662742c02347bc0e980a01041f333bce120",
        "license": "MIT",
        "files": ["config.json", "model.bin", "tokenizer.json", "vocabulary.txt", "README.md"],
    },
    "marian-en-ro": {
        "repository": "Helsinki-NLP/opus-mt-en-ro",
        "revision": "96e8fea9e44dae942c2e8a9078dc1615dd871ed9",
        "license": "Apache-2.0",
        "files": ["config.json", "generation_config.json", "pytorch_model.bin", "source.spm",
                  "target.spm", "tokenizer_config.json", "vocab.json", "README.md"],
    },
}


def download(destination, romanian=False):
    # Set before importing Hub: runtime offline variables must not accidentally block
    # this deliberately separate setup command.
    os.environ["HF_HUB_OFFLINE"] = "0"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    from huggingface_hub import snapshot_download
    destination.mkdir(parents=True, exist_ok=True)
    selected = MODELS if romanian else {"whisper-small": MODELS["whisper-small"]}
    for directory, model in selected.items():
        target = destination / directory
        snapshot_download(repo_id=model["repository"], revision=model["revision"],
                          allow_patterns=model["files"], local_dir=str(target), max_workers=2)
        hashes = {}
        for name in model["files"]:
            with (target / name).open("rb") as stream:
                hashes[name] = hashlib.file_digest(stream, "sha256").hexdigest()
        manifest = model | {"sha256": hashes, "packages": {
            name: importlib.metadata.version(name)
            for name in ("faster-whisper", "transformers", "torch", "huggingface-hub")}}
        temporary = target / "download-manifest.json.tmp"
        temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        temporary.replace(target / "download-manifest.json")
        print(f"Prepared {model['repository']} at {model['revision']} ({model['license']})", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", type=Path, default=Path(os.environ.get("SUBTITLE_MODEL_PATH", "/models")))
    parser.add_argument("--romanian", action="store_true", help="Also prepare local English-to-Romanian translation")
    args = parser.parse_args()
    download(args.models, args.romanian)
