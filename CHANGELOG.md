# Changelog

All notable changes to this project are recorded here.
This project follows [Semantic Versioning](https://semver.org/).

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
- Docs: `README.md`, `README.zh.md`, `SKILL.md`, `references/field-notes.md`,
  `references/troubleshooting.md`.

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
