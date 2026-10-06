# Changelog

All notable changes to this project are recorded here.
This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `src/CtxMenuCleaner.cs` + `src/build.ps1` — a native `CtxMenuCleaner.exe`, compiled
  with the C# compiler bundled with Windows. It reads **both** registry views in one
  process through `RegistryKey.OpenBaseKey`, which Windows PowerShell 5.1 cannot do, so
  it has no PowerShell version dependency. **Working but unreleased**; the binary is not
  committed. See [`references/exe.md`](./references/exe.md).
- `src/publish.ps1` — maintainer tool that publishes a release through the GitHub REST
  API (create repo, upload files, tag, Release, topics). No git installation required.
- `references/exe.md` — build, usage and the measured non-ASCII-path limitation.

### Fixed

- Native executable: deletion failed with
  `OpenSubKey(writable) ... SecurityException HR=0x8013150A` for **every** key. Root
  cause was not the logic but the launch location: a binary started from a
  non-ASCII/OneDrive-synced folder could not write to HKCU at all, while the identical
  binary copied to an ASCII path could. `build.ps1` now stages compilation in an
  ASCII-only temp directory and outputs to `%LOCALAPPDATA%\CtxMenuCleaner\` by default,
  and the exe gained `--selftest` to expose the condition.
- Native executable: the same key is discoverable through both registry views because
  `HKCU\SOFTWARE\Classes` is a single shared store. Entries are now deduplicated by
  logical path, preferring the view that can actually write.
- Native executable: an **interactive** selection was lost when the tool had to
  re-launch itself elevated — the elevated instance started with no selection and asked
  the user to choose again. Slot numbers are now assigned once, up front, and the
  selection is carried across the elevation boundary as `--select N,M`, so the elevated
  instance resolves exactly the same components without prompting.
- Native executable: the elevated instance runs in its own console window and returned
  before its output could be read. A new `--keep-open` flag holds any exit path — list,
  cancel, error, or completion — until Enter, and is added automatically when the tool
  spawns the elevated window. The parent also explains that a second window is expected
  and that it is waiting for it.
- Native executable: in interactive mode the elevation question was asked AFTER the user had already typed a selection, and [y/N] defaults to N - so answering normally ended the run with Aborted. and looked like "typing 4 did nothing". Elevation is now decided once, up front, before any selection; a pending --select N is preserved across the re-launch and the prompt says so. --yes / --force-write / --dry-run skip the question.
- Native executable: an answer containing no valid number was discarded as a quiet "Nothing selected." (exit 0) - including a pasted command that reached ReadLine before the prompt was ready. It now reports the offending input and exits 1, distinguishing "you typed nothing" from "I could not understand what you typed".
- build.ps1: a running instance locks the executable it was started from, which used to fail the final copy with an obscure IOException after a successful compile. The build now detects the lock, moves the old binary aside and writes the new one, and keeps the staging directory when a build fails so the compiler output can be inspected.
- Native executable: the delete output printed an internal diagnostic line (`view ...
  raw ... shape ...`) that was added while debugging the access-denied issue. Removed.

## [1.0.0] - 2026-10-04

First public release. **PowerShell only** — no compiled binary.

### Added

- `scripts/manage-context-menu.ps1` — lists every Explorer context-menu component
  across **both** registry views, grouped by the owning DLL/executable, numbered, and
  classifies each one as third-party or Windows built-in. Pick by number, or use
  `-Select` / `-Grid` / `-ListOnly`. Removes all registrations of a chosen component.
- `scripts/remove-vendor-context-menu.ps1` — pattern-driven removal for known vendors,
  configurable through `scripts/vendors.json`. Supports `-DryRun`, `-Pattern`,
  `-BackupDir`, `-KeepBackup`.
- `scripts/enumerate-context-menu.ps1` — read-only audit that prints the *real*
  registry key names, char codes for space-padded names, and the COM server DLL
  behind every `shellex` handler.
- `scripts/validate.ps1` — self-check: JSON loading, `\uXXXX` decoding, script
  parsing, ASCII-only source, and a dry run. Runs under Windows PowerShell 5.1.
- `scripts/vendors.json` — 7 vendor entries, 25 patterns.
- Docs: `README.md` (Chinese, shown by default on GitHub), `README.en.md` (English),
  `SKILL.md`, `references/field-notes.md`, `references/troubleshooting.md`.

### Key behaviours

- **Space-padded key names.** Vendors store the key as `"    sgshellext"` while the
  extension looks like `sgshellext`, so an exact-name delete matches nothing and
  reports success. Every lookup here enumerates and matches substrings.
- **Both registry views.** A 64-bit PowerShell cannot see the 32-bit view
  (`Registry.OpenBaseKey` does not exist on 5.1), so the scripts cover the other view
  by re-launching 32-bit PowerShell and merging its JSON output.
- **Backups.** Each key is exported with `reg.exe export` (UTF-16LE, exactly what
  `reg import` expects) before deletion.
- **Verification.** Every delete is confirmed by re-reading the key; a failure is
  reported, never swallowed.

### Known limitations

- Windows built-in detection is a heuristic and deliberately conservative: anything
  unresolvable is treated as built-in and excluded from the default selection.
- A vendor that re-registers its keys on update will bring the menu item back; see
  "Making removal stick" in the README.
- Only tested on Windows 10 22H2 (19045) with Windows PowerShell 5.1. PowerShell 7
  should work but has not been verified.

### Deferred to a future release

- A compiled `CtxMenuCleaner.exe` (native C#, no PowerShell dependency, both registry
  views in one process). Under development; not part of 1.0.0.
