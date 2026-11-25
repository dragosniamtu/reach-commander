#!/usr/bin/env python3
"""Exercise the shipped systemd unit through a persistent read-only bind mount.

Run as root on a disposable Linux CI runner. Uses unique service/runtime names and
never starts, stops, or modifies an installed ReachCommander deployment.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import uuid


REPO = Path(__file__).resolve().parents[1]
FIXTURE = """import os
import sys
import threading
from updater_service import UpdaterSocketServer, install_signal_handlers

class ProbeRuntime:
    def handle(self, request):
        return {"pid": os.getpid()}

server = UpdaterSocketServer(ProbeRuntime(), sys.argv[1], runtime_gid=1000)
stop = threading.Event()
server.start()
install_signal_handlers(server, stop)
try:
    server.serve_forever(stop)
finally:
    server.close()
"""


def run(*args: str, check: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(args, check=check, text=True, capture_output=True, timeout=30)


def probe(path: Path) -> int:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
        client.settimeout(2)
        client.connect(str(path))
        client.sendall(json.dumps({
            "protocolVersion": 3, "requestId": str(uuid.uuid4()), "action": "check",
        }).encode() + b"\n")
        with client.makefile("rb") as response:
            return json.loads(response.readline(4096))["pid"]


def ready(path: Path) -> int:
    deadline = time.monotonic() + 10
    while True:
        try:
            return probe(path)
        except (OSError, ValueError):
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.1)


def main() -> None:
    if os.name != "posix" or os.geteuid() != 0 or not Path("/run/systemd/system").is_dir():
        raise RuntimeError("This smoke test requires root and a running Linux systemd")

    name = "rc-updater-smoke-" + uuid.uuid4().hex[:12]
    unit_name = name + ".service"
    unit_path = Path("/run/systemd/system") / unit_name
    runtime = Path("/run") / name
    assert not unit_path.exists() and not runtime.exists()
    mounted = False
    with tempfile.TemporaryDirectory(prefix=name + "-", dir="/opt") as directory:
        root = Path(directory)
        app = root / "application"
        observer = root / "observer"
        for child in (app / "bin", app / "lib", app / "data", observer):
            child.mkdir(parents=True, exist_ok=True)
        lock = root / "deployment.lock"
        lock.touch()
        for source in (REPO / "deploy").glob("*.py"):
            shutil.copyfile(source, app / "lib" / source.name)
        shutil.copyfile(REPO / "deploy/updater_service.py", app / "bin/updater_service.py")
        (app / "bin/probe.py").write_text(FIXTURE, encoding="utf-8")
        unit = (REPO / "deploy/systemd/reachcommander-updater.service").read_text()
        unit = unit.replace("/opt/reachcommander", str(app))
        unit = unit.replace("/opt/.reachcommander.lock", str(lock))
        unit = unit.replace("reachcommander-updater", name)
        unit = unit.replace(
            f"ExecStart=/usr/bin/python3 {app}/bin/updater_service.py",
            f"ExecStart=/usr/bin/python3 {app}/bin/probe.py {runtime}/updater.sock",
        )
        unit_path.write_text(unit, encoding="utf-8")
        try:
            run("systemctl", "daemon-reload")
            run("systemctl", "start", unit_name)
            first_pid = ready(runtime / "updater.sock")
            run("mount", "--bind", str(runtime), str(observer))
            mounted = True
            run("mount", "-o", "remount,bind,ro", str(observer))
            inode = observer.stat().st_ino
            assert probe(observer / "updater.sock") == first_pid

            run("systemctl", "restart", unit_name)
            second_pid = ready(runtime / "updater.sock")
            assert second_pid != first_pid, "The helper did not restart"
            assert runtime.stat().st_ino == inode, "Restart replaced the mounted runtime directory"
            assert probe(observer / "updater.sock") == second_pid
            print("PASS: the existing bind mount reaches the restarted helper", flush=True)

            run("systemctl", "stop", unit_name)
            assert runtime.is_dir(), "Stop removed the mounted runtime directory"
            assert runtime.stat().st_ino == inode
            assert not (observer / "updater.sock").exists(), "Stopped helper left a stale socket"
            run("systemctl", "start", unit_name)
            third_pid = ready(runtime / "updater.sock")
            assert third_pid != second_pid
            assert runtime.stat().st_ino == inode
            assert probe(observer / "updater.sock") == third_pid
            print("PASS: the existing bind mount reaches the helper after stop/start", flush=True)
        except Exception:
            print(run("journalctl", "-u", unit_name, "--no-pager", "-n", "30", check=False).stdout)
            raise
        finally:
            run("systemctl", "stop", unit_name, check=False)
            if mounted:
                run("umount", str(observer))
            unit_path.unlink()
            run("systemctl", "daemon-reload")
            run("systemctl", "reset-failed", unit_name, check=False)
            # Delete only our unique runtime directory, after releasing the bind mount.
            if runtime.exists():
                (runtime / "updater.sock").unlink(missing_ok=True)
                runtime.rmdir()


if __name__ == "__main__":
    main()
