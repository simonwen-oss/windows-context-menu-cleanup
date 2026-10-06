v1.1.0 - adds a working native executable and the release tooling.

### Added

- `src/CtxMenuCleaner.cs` + `src/build.ps1` - CtxMenuCleaner.exe, a native C# build compiled with the C# compiler bundled with Windows. It reads both registry views in ONE process through RegistryKey.OpenBaseKey, which Windows PowerShell 5.1 cannot do, so it removes the PowerShell version dependency entirely. Build with `.\src\build.ps1 -Test`.
- `src/publish.ps1` - maintainer tool: publishes a release through the GitHub REST API (create repo, upload files, tag, Release, topics) with no git installation required. Idempotent and re-runnable.
- `src/release-notes.md` - the release body, kept out of the script so the script source stays pure ASCII.
- `references/exe.md` - build, usage, and the measured non-ASCII-path limitation.

### Fixed

- The native executable could not delete ANY key: every attempt failed with `OpenSubKey(writable) ... SecurityException HR=0x8013150A`. The cause was not the deletion logic but the LAUNCH LOCATION. On the test machine a binary started from a Chinese-named, OneDrive-synced folder could not write to HKCU at all, while the identical binary copied to an ASCII path wrote fine. `build.ps1` now stages compilation in an ASCII-only temp directory and outputs to `%LOCALAPPDATA%\CtxMenuCleaner\` by default, and the exe gained `--selftest` to expose the condition. Verified: a 4-space-padded HKCU key is now matched, backed up and deleted end to end.
- The native executable discovered the same key twice, because `HKCU\SOFTWARE\Classes` is one shared store that both registry views expose. Entries are now deduplicated by logical path, preferring the view that can actually write - probing the read-only view would otherwise throw SecurityException at delete time.

### Notes

- The PowerShell tooling is unchanged from v1.0.0 apart from `LICENSE` being restored to the plain MIT text so GitHub detects it as MIT.
- The executable is not committed; build it locally. Its HKLM deletion path under real elevation is not yet verified.
- PowerShell 7+ remains designed-for but untested.
