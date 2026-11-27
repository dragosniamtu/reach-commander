# Windows 95 theme progress

Plan: docs/superpowers/plans/2026-08-30-reachcommander-windows-95-theme.md
Baseline: 297effd33a81db3ce11f6b3b7852439db0f1e0e2

Task 1: complete (commits 297effd..e2d16ec, review clean)
Task 2: complete (commits e2d16ec..f4f4baf, review clean)
Task 3: complete (commits f4f4baf..4735c8b, review clean after fixes 3cd4f6b and 4735c8b)
Task 4: complete (commits 4735c8b..00faf41, final review clean after fixes 576b21b and 00faf41)

# Shift+Arrow selection progress

Plan: docs/superpowers/plans/2026-08-31-reachcommander-shift-arrow-selection.md
Baseline: 7568d49

Task 1: complete (commits 7568d49..791b5f3, review clean after fix 791b5f3)
Task 2: complete (commits 00faf41..676af97, review clean)
Final integration review: complete (fix 7acce97, review clean)

# UI source mapping progress

Plan: docs/superpowers/plans/2026-08-31-reachcommander-ui-source-mapping.md
Baseline: fd0c748

Task 1: complete (commits 84548fa, faea25d, a37e752; review clean)
Task 2: complete (commits 8d58ca1, 42ee4c1, 1dc0916, a184993, 9187a94, 302292e; review clean)
Task 3: complete (commits 0a8bd10, e9b652b, 59fa7ca; review clean)
Task 4: complete (commits 50ebe32, 9a799b8, c50d5a8, 9190630, f878b0a; final review clean)
Task 5: complete (commits 83107b8, 8a3e421, 8f2eda8, 4617687; final review clean)
Task 6: complete (commits 7e81a9e, a8877c6, c620630, 4128566; final integration/security review clean)

# Subtitle repair progress (2026-11-27)
User approved any-language SRT repair, UTF-8 conversion, CPU auto timing, original backup. Preserve prior uncommitted Menu work; no publication/commits performed.
Plan: docs/superpowers/plans/2026-11-27-subtitle-repair.md
Task1 worker complete, review approved (artifacts/subtitle-repair/worker-review.md).
Task2 backend complete and review approved: 939 unit and 161 API integration tests passed.
Task3 UI complete and review approved: 597 unit tests and production/PWA build verification passed. Browser repair verifies two consecutive saves, preview timing and both byte-exact backups.
Task4 docs/CI and host mailbox allowlist complete; independent final review approved. Installer Python suite: 204 tests, nine platform skips. Documentation/workflow contracts: 34 passed. Linux Docker unavailable locally; new CI gate executes real sync fixture in image.
Final browser run: 86 passed, zero skips/retries (2.2 minutes). Repair/save covered in all three themes; repeat-save regression covered once to respect the real shared 20/minute limit. Native modal-dialog test wait corrected and verified with 12 consecutive repetitions. All three repair screenshots inspected and retained under artifacts/subtitle-repair/preview*.png. No running test server remains.
No commits or publication. Existing hosts need the verified release installer with --keep-config to refresh the storage allowlist; documented in Ubuntu and subtitle repair guides.

# Subtitles menu grouping (2026-11-27)
User requested one Subtitles item containing Generate and Repair. Implemented an expandable group with both actions and existing job history, icons and labels, hidden-child keyboard exclusion and unchanged handlers. 597 frontend tests, build/PWA and 16 targeted browser cases passed. Inspected all three theme screenshots; artifacts/subtitles-menu-preview.png retained. Local only; no commit or publication.

# Generated subtitle ending times (2026-11-27)
User observed captions remaining through silence in Jellyfin. Reproduced with the real pinned CPU Whisper model: segment-only timestamps mapped the first caption end across an eight-second silent gap to 11.09 seconds. Enabling word timestamps for both transcription and translation places the end at 2.72 seconds. Romanian translation retains identical corrected times. No Jellyfin instance or user movie was available for playback verification.
Added a real offline Whisper/Marian regression fixture with internal and trailing silence, wired into the existing required Linux container smoke gate. The fixture failed before the fix and passes afterward. Existing real CPU mailbox smoke also passes, including source preservation and temporary cleanup.
Added a default-on "Skip successfully processed videos" checkbox so users can explicitly regenerate affected files through the UI; existing server backup behavior remains in use. Tests verify the submitted setting and reset to the safe default for the next selection.
Verification: worker suite 45 passed, four platform skips; 598 frontend tests; targeted subtitle-generation browser run passed; production build/PWA verification; 34 installer documentation/workflow contracts; Bash script syntax and diff whitespace checks. Linux Docker execution remains for CI because Docker/WSL are unavailable locally. Local changes only; no commit, publication, or server deployment.
