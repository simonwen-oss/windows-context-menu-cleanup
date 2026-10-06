# CtxMenuCleaner.exe — native executable

A single-file native alternative to the PowerShell scripts. Built from
[`CtxMenuCleaner.cs`](../src/CtxMenuCleaner.cs) with the C# compiler that ships with
Windows. No .NET SDK, no NuGet, no internet access needed to build, and **no
PowerShell dependency at runtime** — which removes the PowerShell version question
entirely.

**Status: working, unreleased.** Verified on the machine described in the README's
"Test environment" section.

## Why it exists

The PowerShell scripts have to work around a real limitation: **Windows PowerShell 5.1
cannot read both registry views from one process** (`RegistryKey.OpenBaseKey` does not
exist there). They cover the 32-bit view by re-launching 32-bit PowerShell and merging
its JSON output — effective, but fragile.

C# has `OpenBaseKey`, so one process reads both views directly:

```csharp
RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
```

No child process, no JSON hand-off, no PowerShell version dependency.

## Build

```powershell
.\src\build.ps1              # build only
.\src\build.ps1 -Test        # build, then smoke-test --list and --dry-run
.\src\build.ps1 -OutputDir C:\Tools\CtxMenuCleaner
```

Output defaults to `%LOCALAPPDATA%\CtxMenuCleaner\CtxMenuCleaner.exe` — see the warning
below for why that matters.

If script execution is blocked:

```powershell
powershell -ExecutionPolicy Bypass -File .\src\build.ps1
```

## ⚠️ Never run the exe from a non-ASCII or OneDrive-synced folder

This is not a style preference; it was measured on the affected machine.

The repository checkout lived under `C:\Users\<user>\OneDrive\文档\...` (Chinese folder
name, OneDrive-synced). A binary **launched from that folder could not write to the
registry at all**, while the *identical binary* copied to two different ASCII paths
wrote to `HKCU` without complaint:

| Location the exe was launched from | Create a key in HKCU |
|---|---|
| `C:\Users\<user>\OneDrive\文档\...\src\dist\` | ❌ `UnauthorizedAccessException` / `SecurityException 0x8013150A` |
| `C:\Users\<user>\AppData\Local\Temp\...` | ✅ OK |
| `C:\Users\<user>\AppData\Local\CtxMenuCleaner\` | ✅ OK |

Symptom: every delete fails with
`OpenSubKey(writable) ... SecurityException HR=0x8013150A`, and listing still works
(listing only needs read access). Run `CtxMenuCleaner.exe --selftest` to confirm — it
prints the identity and whether the process can write to HKCU at all.

`csc.exe` has the same allergy at build time (it creates a Win32 resource temp file next
to its output), which is why `build.ps1` stages compilation in an ASCII-only temp
directory.

## Usage

```
CtxMenuCleaner.exe                     interactive: list, then pick numbers
CtxMenuCleaner.exe --list              list only (no admin needed)
CtxMenuCleaner.exe --all               include Windows built-ins in the list
CtxMenuCleaner.exe --grid              pick in a searchable window
CtxMenuCleaner.exe --select 3,7        remove those numbers
CtxMenuCleaner.exe --dry-run           show what would happen

  --yes            skip the YES confirmation
  --no-backup      do not export .reg backups (not recommended)
  --backup-dir D   where to write backups
  --no-restart     do not restart Explorer afterwards
  --show-elevate   show the elevated command instead of running it
  --selftest       report the process identity and whether it can write
                   to HKCU at all, then exit (diagnostic)
  --force-write    skip the elevation guard; HKLM will still fail
  --version        print the version
  --help           this text
```

Deleting needs an elevated process. Without it the tool offers to re-launch itself via
UAC (`ShellExecute` with the `runas` verb); `--show-elevate` prints the command instead
of running it.

## Behaviour carried over from the PowerShell version

- **Space-padded key names** (`"    sgshellext"`) are enumerated by name, never addressed
  by a fixed string.
- **Both registry views** are read in one pass.
- **A `.reg` backup is exported before every delete** (`reg.exe export`, UTF-16LE).
- **Every delete is verified** by re-reading the key; failures are reported, never
  swallowed.
- **Windows built-ins are never selected by default.** Anything unresolvable — a bare
  module name such as `@shell32.dll,-8506`, a generic verb such as `open`/`runas`, an
  unresolved CLSID — is classified as a built-in, because deleting a Windows shell
  extension can break Explorer.
- **Duplicate registrations are collapsed.** `HKCU\SOFTWARE\Classes` is a single store
  that both registry views expose, so the same key is discovered twice. Entries are
  deduplicated by logical path, preferring the view that can actually write (probing
  the read-only view would otherwise throw `SecurityException` at delete time).

## Verified

- Reads both registry views in one 64-bit process (e.g. a 7-Zip entry appears once as
  `7-zip.dll` 64-bit and once as `7-zip32.dll` 32-bit).
- Detects and flags space-padded key names.
- Deletes a 4-space-padded `HKCU` key end to end, writes a 348-byte `.reg` backup.
- `--dry-run` performs no deletion.
- `--selftest` correctly reports whether the process can write to HKCU.

## Not verified

- Deletion of `HKLM` keys with real elevation (the code path is the same as `HKCU`,
  which is verified, but the UAC flow has not been exercised).
- PowerShell-independent behaviour on Windows 11.
- Non-Chinese locales.
