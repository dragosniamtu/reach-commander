# ReachCommander CI Repair Design

**Date:** 2026-08-22
**Status:** Approved for implementation

## Goal

Restore actionable Ubuntu CI without weakening any installer, frontend, backend, container, or publication gate.

## Decisions

- Preserve the nested `sh -c` positional-argument behavior in `deploy/reachcommander` and rewrite the quoting to match the already clean source-path check instead of globally suppressing ShellCheck.
- Keep the Linux .NET failure separate from the installer-lint failure. The backend failure predates the authentication change and will not be changed without a failing test name and message.
- Add a dependency-free TRX reporter that converts failed .NET results into GitHub Actions error annotations. This keeps the retained TRX artifact while making the failure actionable from the public job page.
- Wrap the Linux-only renderer contract with a dependency-free subprocess adapter that preserves its exit code and emits bounded, escaped failure output as a GitHub annotation.
- Limit annotations to failed-test names and failure messages. Do not print account data, configuration, environment variables, or unrelated logs.
- Work directly on `master`, as explicitly requested. Do not create a branch or worktree.

## Flow

1. The existing ShellCheck command proves the quoting defect by reporting `SC2016`.
2. The command string changes from a single-quoted script to a double-quoted script with escaped inner quotes and escaped dollar signs. The nested shell still expands `$1`, while the outer shell does not.
3. `dotnet test` uses `LogFilePrefix` so every test project writes a distinct TRX file on every operating system.
4. If the Ubuntu test command fails, a post-step expands the TRX filename pattern, parses every result file, and emits one escaped `::error` annotation per failed test.
5. The retained diagnostic artifact remains the source for complete stack traces and attachments.

## Error handling

- A missing TRX file produces a warning annotation and does not mask the original test failure.
- A malformed TRX file produces a warning annotation and does not mask the original test failure.
- A TRX file with no failed result prints a short informational message.
- A filename pattern with no matches is reported as a missing diagnostic instead of silently succeeding.
- A wrapped command preserves its complete normal log and original exit code; only a bounded copy is added to the public annotation.
- Annotation properties and messages escape GitHub workflow-command delimiters and line breaks.

## Testing

- Run the exact production ShellCheck invocation before and after the quoting change.
- Unit-test TRX parsing with failed and passed results, GitHub escaping, missing input, and malformed XML.
- Run the reporter unit test in the Ubuntu acceptance contract step.
- Run the complete local .NET suite before pushing.
- Push to `master`, monitor the new workflow, and use the resulting annotation to diagnose the Linux-only backend failure.

## Out of scope

- Skipping or weakening Linux tests
- Treating the Node.js action-runtime deprecation warning as the test failure
- Changing application authentication behavior
- Guessing at a Linux backend fix before the failed test is identified
