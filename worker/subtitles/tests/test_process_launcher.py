"""Parent-death protection must never execute Python in a forked threaded parent."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import worker


class LauncherContractTests(unittest.TestCase):
    def test_linux_commands_use_a_fresh_launcher_with_exact_arguments(self):
        arguments = [sys.executable, "-c", "print('safe')", "spaces & $(literal)"]
        with patch.object(worker.sys, "platform", "linux"):
            command = worker.guarded_command(arguments)
        self.assertEqual(command, [sys.executable, str(ROOT / "process_launcher.py"), str(os.getpid()), *arguments])

    def test_other_platforms_keep_the_existing_command(self):
        arguments = [sys.executable, "-c", "print('safe')"]
        with patch.object(worker.sys, "platform", "win32"):
            self.assertEqual(worker.guarded_command(arguments), arguments)

    def test_decoder_spawn_has_no_preexec_hook(self):
        actual = subprocess.Popen
        calls = []
        def checked(*args, **kwargs):
            self.assertNotIn("preexec_fn", kwargs)
            calls.append(args[0])
            return actual(*args, **kwargs)
        with patch.object(worker.subprocess, "Popen", side_effect=checked):
            self.assertEqual(worker.run_bounded([sys.executable, "-c", "print('ready')"], timeout=5), b"ready\n" if os.name == "posix" else b"ready\r\n")
        self.assertEqual(len(calls), 1)


@unittest.skipUnless(sys.platform == "linux", "Requires Linux parent-death signals")
class LinuxLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_parent_identity_mismatch_never_runs_command(self):
        marker = self.root / "must-not-run"
        result = subprocess.run([sys.executable, str(ROOT / "process_launcher.py"), str(os.getpid() + 1000000),
                                 sys.executable, "-c", "import pathlib,sys; pathlib.Path(sys.argv[1]).touch()", str(marker)],
                                timeout=5, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(marker.exists())

    def test_threaded_parent_preserves_inherited_descriptors_and_literal_arguments(self):
        payload = self.root / "payload"
        payload.write_bytes(b"inherited safely")
        stop = threading.Event()
        thread = threading.Thread(target=lambda: stop.wait(10))
        thread.start()
        try:
            with payload.open("rb") as source:
                arguments = [sys.executable, "-c", "import os,sys; sys.stdout.buffer.write(os.read(int(sys.argv[1]),100)+sys.argv[2].encode())",
                             str(source.fileno()), " & $(literal)"]
                result = worker.run_bounded(arguments, timeout=5, pass_fds=(source.fileno(),))
            self.assertEqual(result, b"inherited safely & $(literal)")
        finally:
            stop.set()
            thread.join(5)

    def test_abrupt_parent_death_kills_worker_and_decoder_after_exec(self):
        ready = self.root / "decoder-ready"
        survivor = self.root / "decoder-survived"
        child_pid = self.root / "worker-pid"
        decoder = "import os,pathlib,sys,time; pathlib.Path(sys.argv[1]).write_text(str(os.getpid())); time.sleep(1); pathlib.Path(sys.argv[2]).touch()"
        child = ("import os,pathlib,sys,time,subprocess; sys.path.insert(0,sys.argv[1]); import worker; "
                 "pathlib.Path(sys.argv[2]).write_text(str(os.getpid())); "
                 "subprocess.Popen(worker.guarded_command([sys.executable,'-c',sys.argv[3],sys.argv[4],sys.argv[5]])); time.sleep(30)")
        parent = ("import sys,subprocess,time,threading; sys.path.insert(0,sys.argv[1]); import worker; "
                  "threading.Thread(target=lambda: time.sleep(30),daemon=True).start(); "
                  "subprocess.Popen(worker.guarded_command([sys.executable,'-c',sys.argv[2],sys.argv[1],sys.argv[3],sys.argv[4],sys.argv[5],sys.argv[6]]),start_new_session=True); time.sleep(30)")
        process = subprocess.Popen([sys.executable, "-c", parent, str(ROOT), child, str(child_pid), decoder, str(ready), str(survivor)])
        try:
            deadline = time.monotonic() + 5
            while (not ready.exists() or not ready.read_text().isdigit()) and time.monotonic() < deadline:
                time.sleep(.01)
            self.assertTrue(ready.exists() and ready.read_text().isdigit(), "decoder did not start")
            process.kill()
            process.wait(timeout=5)
            time.sleep(1.2)
            self.assertFalse(survivor.exists(), "decoder survived abrupt supervisor death")
            for pid_path in (child_pid, ready):
                process_state = Path('/proc') / pid_path.read_text() / 'stat'
                if process_state.exists():
                    self.assertEqual(process_state.read_text().split()[2], 'Z', 'child remained running')
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            if child_pid.exists():
                try:
                    os.killpg(int(child_pid.read_text()), signal.SIGKILL)
                except ProcessLookupError:
                    pass


if __name__ == '__main__':
    unittest.main()
