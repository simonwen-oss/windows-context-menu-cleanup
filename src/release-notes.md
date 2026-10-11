v1.1.3 - console colour, screenshots, and a rewritten English README.

### Added

- **Console colour in the native executable.** Component rows are white, detail rows dark
  grey, and rows whose **key name is space-padded are yellow** - the single most important
  thing to notice in the listing. `-> DELETED`, the deletion summary and `Explorer is
  running` are green; failures are red.
  Colour is applied **only when stdout is a real console**: redirected or piped output is
  plain text with no escape codes, so capturing it from a script is unaffected. `--no-color`
  forces plain text explicitly.
- `--no-color`.
- `docs/ctxmenu-list.png` and `docs/ctxmenu-delete-flow.png` - console previews rendered
  from the program's real output, wording and colour scheme, with machine-specific install
  paths replaced by `%ProgramFiles%` / `%APPDATA%` placeholders. Both READMEs show them.
- `README.en.md` rewritten to mirror the Chinese README section for section: table of
  contents, quick start, screenshots, consolidated FAQ, and the option reference moved out.

### Changed

- `CtxMenuCleaner.exe --help` is regrouped into **Usage / Options / Diagnostics / Misc /
  Notes** and now lists every option. The READMEs no longer duplicate that list - the binary
  is the source of truth, with a Chinese table in `references/exe.md`.
- Both READMEs restructured and trimmed. The interactive `YES` step is now described
  explicitly in both, because skipping it silently cancels and looks exactly like "typing
  the number did nothing".
- The executable's internal version is now `1.1.3`, matching the release. It had lagged
  behind: it still reported `1.0.0` while the release was `v1.1.0`.

### Verified

- Redirected and piped output contains **0 ESC bytes** - colour really is console-only.
- Every option claimed in `references/exe.md` (19) appears in `--help`.
- All anchors and relative links in both READMEs resolve, including the two image paths.
- Security scan of every file including the new PNGs: no credentials, no personal paths, no
  host identifiers.
- `--selftest` leaves no probe key behind.

### Notes

- The PowerShell scripts are unchanged from v1.1.2.
- The executable is still not committed: build it locally with `.\src\build.ps1 -Test`.
  Its HKLM deletion path under real elevation was exercised successfully on the test machine.
- PowerShell 7+ remains designed-for but untested.
