# ReachCommander CI Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the confirmed Ubuntu ShellCheck failure and make the separate Linux .NET failure publicly actionable without weakening CI.

**Architecture:** Preserve the installer command's nested-shell semantics with ShellCheck-clean quoting. Add a dependency-free Python TRX adapter that turns failed test results into escaped GitHub Actions annotations after an Ubuntu backend failure.

**Tech Stack:** Bash, ShellCheck, Python 3 standard library, Visual Studio TRX XML, GitHub Actions, .NET 10.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Keep every existing CI gate enabled and strict.
- Do not change backend production code until the Linux failure identifies a specific behavior.
- Do not expose configuration, credentials, environment variables, or account-state contents in annotations.
- Follow red-green-refactor and run fresh verification before committing or pushing.

---

### Task 1: Repair the installer ShellCheck failure

**Files:**
- Modify: `deploy/reachcommander:829`

**Interfaces:**
- Consumes: nested `sh -c` positional parameter `$1` supplied as `"$path"`.
- Produces: the same read/write/execute access check with ShellCheck-clean outer-shell quoting.

- [ ] Run the exact CI ShellCheck command and confirm `SC2016` at line 829.
- [ ] Replace the single-quoted nested script with `"test -r \"\$1\" && test -w \"\$1\" && test -x \"\$1\""`.
- [ ] Run the exact CI ShellCheck command and confirm exit code 0.
- [ ] Run `bash tests/installer/test_command.sh` on an available Bash environment, or rely on the unchanged positional-argument contract plus the Ubuntu workflow when Bash is unavailable locally.

### Task 2: Report failed TRX results as public annotations

**Files:**
- Create: `tools/report_trx.py`
- Create: `tests/ci/test_report_trx.py`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: one or more paths to Visual Studio TRX files.
- Produces: escaped GitHub workflow commands on standard output; returns zero so diagnostics never replace the original test result.

- [ ] Create a failing `unittest` contract covering a failed test, ignored passed results, workflow-command escaping, missing files, and malformed XML.
- [ ] Run `python -m unittest tests/ci/test_report_trx.py -v` and confirm import failure because the reporter does not exist.
- [ ] Implement `report(paths: Sequence[Path], output: TextIO) -> None` with `xml.etree.ElementTree`, namespace-independent result lookup, bounded failure messages, and safe GitHub escaping.
- [ ] Run `python -m unittest tests/ci/test_report_trx.py -v` and confirm all tests pass.
- [ ] Add the unit test to the Ubuntu release-contract step.
- [ ] Add an `if: failure() && matrix.os == 'ubuntu-latest'` backend step that runs the reporter against the Ubuntu TRX before artifact upload.
- [ ] Run the reporter unit tests again and inspect the workflow diff.

### Task 3: Verify, publish the diagnostic repair, and diagnose Linux

**Files:**
- Modify only the files required by the failed Linux test after its annotation supplies evidence.

**Interfaces:**
- Consumes: GitHub's new failed-test annotation.
- Produces: a focused regression test and minimal Linux fix, if required.

- [ ] Run `dotnet test ReachCommander.slnx -c Release --no-restore`.
- [ ] Run the reporter unit tests and exact ShellCheck command fresh.
- [ ] Inspect `git diff --check` and `git status --short --branch`.
- [ ] Commit and push the diagnostic repair to `master`.
- [ ] Monitor the new GitHub Actions run until the failed Linux test name and message are visible.
- [ ] Write a focused failing regression test for that behavior, implement the minimal fix, and re-run the relevant suite.
- [ ] Commit and push the Linux fix, then verify all required jobs pass.
