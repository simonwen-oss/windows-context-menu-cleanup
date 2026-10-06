v1.1.1 - the working native executable, plus a pre-release audit of the published content.

### Added

- `CtxMenuCleaner.exe` (`src/CtxMenuCleaner.cs` + `src/build.ps1`) - a native C# build compiled with the C# compiler bundled with Windows. It reads both registry views in ONE process through RegistryKey.OpenBaseKey, which Windows PowerShell 5.1 cannot do, so it removes the PowerShell version dependency entirely. Build with `.\src\build.ps1 -Test`.
- `src/publish.ps1` - maintainer tool: publishes a release through the GitHub REST API (create repo, upload files, tag, Release, topics) with no git installation required. Idempotent and re-runnable.
- `src/release-notes.md` - the release body, kept out of the script so the script source stays pure ASCII.
- `references/exe.md` - build, usage, and the measured non-ASCII-path limitation.

### Fixed

- The native executable could not delete ANY key: every attempt failed with `OpenSubKey(writable) ... SecurityException HR=0x8013150A`. The cause was not the deletion logic but the LAUNCH LOCATION. On the test machine a binary started from a Chinese-named, OneDrive-synced folder could not write to HKCU at all, while the identical binary copied to an ASCII path wrote fine. `build.ps1` now stages compilation in an ASCII-only temp directory and outputs to `%LOCALAPPDATA%\CtxMenuCleaner\` by default, and the exe gained `--selftest` to expose the condition. Verified: a 4-space-padded HKCU key is matched, backed up and deleted end to end.
- The native executable discovered the same key twice, because `HKCU\SOFTWARE\Classes` is one shared store that both registry views expose. Entries are now deduplicated by logical path, preferring the view that can actually write.
- Interactive mode asked for elevation AFTER the user had already typed a selection, and `[y/N]` defaults to N - so answering normally ended the run and looked like "typing 4 did nothing". Elevation is now decided once, up front; a pending `--select N` is preserved across the re-launch and the prompt says so.
- An answer containing no valid number was discarded as a quiet "Nothing selected." (exit 0), including a pasted command that reached ReadLine before the prompt was ready. It now reports the offending input and exits 1, distinguishing "you typed nothing" from "I could not understand what you typed".
- The `Type YES to confirm:` step is now announced explicitly, since skipping it silently cancels.
- `publish.ps1`: passing a large body to `Invoke-RestMethod` produced an HTTP 400 once the payload passed roughly 75 KB; requests are now sent over HttpWebRequest with explicit UTF-8 bytes. Also fixed a hard-coded release title and a wrong release-notes path.
- `build.ps1`: a running instance locks the executable it was started from. The build now detects that, moves the old binary aside and writes the new one, and keeps the staging directory when a build fails.
- `--selftest` now states its one side effect before running it (it creates and removes a single throwaway key), and its probe key was renamed so it cannot be mistaken for a leftover test artefact.

### Verified

- Every option listed in the README exists in the source, and no documented option is missing.
- `vendors.json` holds exactly the 7 vendors / 25 patterns the README claims.
- Security scan of all published files: no credentials, no personal paths, no temporary or build artefacts, no test leftovers, no host identifiers.
- The interactive flow was exercised end to end in a real elevated terminal: selection parsed, `YES` confirmed, two HKLM keys deleted with `.reg` backups written, Explorer restarted.

### Notes

- The PowerShell tooling is unchanged from v1.0.0, apart from `LICENSE` being restored to the plain MIT text so GitHub detects it as MIT.
- The executable is not committed; build it locally. Its HKLM deletion path under real elevation was exercised successfully on the test machine.
- PowerShell 7+ remains designed-for but untested.
