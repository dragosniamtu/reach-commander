#!/usr/bin/env python3
"""Verify the immutable worker paired with every platform of the selected app image."""
import argparse
import hashlib
import json
import os
import re
import subprocess
import tempfile


WORKER_REPOSITORY = "ghcr.io/dragosniamtu/reach-commander"
APPLICATION_REPOSITORY = "ghcr.io/dragosniamtu/reach-commander"
LABEL = "io.reachcommander.subtitle-worker-image"
DIGEST = re.compile(r"sha256:[0-9a-f]{64}\Z")


def validate_worker_reference(value):
    if not isinstance(value, str) or not re.fullmatch(re.escape(WORKER_REPOSITORY) + r"@sha256:[0-9a-f]{64}", value):
        raise ValueError("Application worker label must name the fixed repository and an immutable digest")
    return value


def runnable_manifests(document):
    entries = document.get("manifests", [])
    runnable = [item for item in entries if item.get("platform", {}).get("os") == "linux"]
    if {item["platform"].get("architecture") for item in runnable} != {"amd64", "arm64"} or len(runnable) != 2:
        raise ValueError("Exactly the amd64 and arm64 worker/application platforms are required")
    attestations = {item.get("annotations", {}).get("vnd.docker.reference.digest")
                    for item in entries if item.get("annotations", {}).get("vnd.docker.reference.type") == "attestation-manifest"}
    if any(not DIGEST.fullmatch(item.get("digest", "")) or item["digest"] not in attestations for item in runnable):
        raise ValueError("Every runnable image must have a valid digest and provenance/SBOM descriptor")
    return runnable


def docker(*arguments):
    return subprocess.check_output(["docker", *arguments], timeout=600)


def manifests(reference):
    raw = docker("buildx", "imagetools", "inspect", "--raw", reference)
    expected = reference.rsplit("@", 1)[1]
    if "sha256:" + hashlib.sha256(raw).hexdigest() != expected:
        raise ValueError("Registry manifest bytes do not match the immutable image reference")
    return runnable_manifests(json.loads(raw))


def labels(repository, manifest):
    reference = repository + "@" + manifest["digest"]
    docker("pull", "--quiet", "--platform", "linux/" + manifest["platform"]["architecture"], reference)
    return json.loads(docker("image", "inspect", "--format", "{{json .Config.Labels}}", reference))


def verify(reference, revision):
    if not re.fullmatch(re.escape(APPLICATION_REPOSITORY) + r"@sha256:[0-9a-f]{64}", reference):
        raise ValueError("Application reference must be the trusted immutable image")
    references = set()
    for manifest in manifests(reference):
        metadata = labels(APPLICATION_REPOSITORY, manifest)
        if metadata.get("org.opencontainers.image.revision") != revision:
            raise ValueError("Selected application platform belongs to another revision")
        references.add(validate_worker_reference(metadata.get(LABEL)))
    if len(references) != 1:
        raise ValueError("Application platforms refer to different worker releases")
    worker_reference = references.pop()
    for manifest in manifests(worker_reference):
        metadata = labels(WORKER_REPOSITORY, manifest)
        if metadata.get("org.opencontainers.image.revision") != revision or metadata.get("io.reachcommander.component") != "subtitle-worker":
            raise ValueError("Worker platform belongs to another application revision")
    # End users must be able to pull the worker without GitHub credentials.
    with tempfile.TemporaryDirectory(prefix="reachcommander-anonymous-") as config:
        subprocess.check_output(["docker", "--config", config, "manifest", "inspect", worker_reference], timeout=120)
    return worker_reference


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference")
    parser.add_argument("revision")
    args = parser.parse_args()
    worker = verify(args.reference, args.revision)
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"reference={worker}\n")
    print(f"Verified both application platforms and their worker: {worker}")
