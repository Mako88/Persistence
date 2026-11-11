"""cwd-exec: start a worker in a directory given as an open descriptor, never as a path (docs/design/task-claims.md,
section 1, step 3).

    python3 -I cwd_exec.py <fd> <program> [args...]

The harness verifies the directory, opens it, and passes only that descriptor (`pass_fds`, `close_fds`). This does
`fchdir(fd)`, `close(fd)`, then `exec`s the worker, and nothing else: no path string is read, so retargeting the
path after verification changes nothing, and the worker inherits no descriptor but 0, 1 and 2. A helper rather than
`preexec_fn`, since running Python between fork and exec in a multithreaded harness can deadlock.

Standard library only."""
import os
import sys


def main(argv):
    if len(argv) < 2:
        sys.stderr.write("usage: cwd_exec.py <fd> <program> [args...]\n")
        return 2
    fd = int(argv[0])
    os.fchdir(fd)
    os.close(fd)
    os.execvp(argv[1], argv[1:])
    return 127  # not reached: execvp replaces this process or raises


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
