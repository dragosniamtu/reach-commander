"""Linux-only exec launcher: process_launcher.py EXPECTED_PARENT_PID COMMAND [ARGS...].

Run in a fresh interpreter so parent-death setup never runs in the unsafe
post-fork/pre-exec state of the supervisor's multithreaded interpreter.
"""
import ctypes
import os
import signal
import sys


def main(arguments):
    if sys.platform != "linux" or len(arguments) < 2:
        return 2
    try:
        parent_pid = int(arguments[0])
        if parent_pid <= 0:
            return 2
        libc = ctypes.CDLL(None, use_errno=True)
        libc.prctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong]
        libc.prctl.restype = ctypes.c_int
        # Setting the signal first, then checking identity, closes the race where
        # the expected parent dies before the launcher attaches its death signal.
        if libc.prctl(1, signal.SIGKILL, 0, 0, 0) != 0 or os.getppid() != parent_pid:
            return 1
        # exec retains PID, process group, inherited descriptors and PDEATHSIG.
        os.execvpe(arguments[1], arguments[1:], os.environ)
    except (OSError, ValueError):
        return 1
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
