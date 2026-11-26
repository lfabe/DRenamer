# Compact PDF and external undo originals

**Goal:** Save only the current PDF revision without recompressing images, while preserving exact-byte undo during the current session.

**Architecture:** `PdfRenderer` performs a full save. `RenameService` keeps originals for moves and in-place edits in a session directory beside the journal, under `Undo`. Copies use the retained source. A small backup store owns session locks, durable recovery metadata, and conservative cleanup. Verified file handles continue to protect against external replacements.

**Scope:** The agreed option 1 from this chat. No image-quality changes, dependency changes, release, or GitHub merge.

## Tasks

- [x] Add a regression test for compact output and exact-byte undo; run it against the current code and observe the size failure.
- [x] Add tests for backup creation, copy-without-backup, undo cleanup, corrupted/missing originals, journal failures, and interrupted-session cleanup without affecting live sessions.
- [x] Implement full PDF saves and a session backup store. Keep uncertain interrupted operations for recovery; delete completed/verified backups on close or next startup.
- [x] Adapt save and undo to external originals; preserve hash checks, staged writes, rollback, and concurrent replacement protection.
- [x] Close the backup session when the window closes, waiting for active file operations, and document storage and cleanup.
- [x] Run the complete Release build and test suite; compare image streams and file sizes on a real scan; inspect the final diff.

## Review focus

- Never change/delete an externally replaced source or output.
- Never publish edits without a verified original available for undo.
- Journal-write failures must retain enough session state and backups for undo.
- Cleanup must skip active sessions and retain ambiguous interrupted writes.
- A failed in-place write must restore the previous contents or retain the original backup.

## Verification

- Release solution build: 0 warnings, 0 errors.
- Full test suite: 277 passed, 0 failed, 0 skipped.
- Independent read-only review found an interrupted in-place undo cleanup gap; completion markers are now cleared before restoration. Two added regressions cover blocked marker removal and retention after interrupted undo. Focused re-review confirmed the fix.
- Real scan: 1,732,627 bytes becomes 1,733,087 after alignment or 1,733,239 after cropping. Encoded image streams remain identical, and undo restores exact original bytes.
