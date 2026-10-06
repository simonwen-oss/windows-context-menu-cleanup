# windows-context-menu-cleanup

Remove unwanted **Windows Explorer right-click menu entries** added by Baidu Netdisk,
Thunder/Xunlei, Sogou Input, 360, and similar third-party apps — reliably, with
verification and a working rollback path.

[中文说明](./README.md) · [Changelog](./CHANGELOG.md) · MIT

**Current release: v1.1.0.** PowerShell tooling plus a **working native executable**
(`CtxMenuCleaner.exe`; source under `src/`, the binary is not committed). See
[references/exe.md](./references/exe.md).

---

## 1. What problem this solves

You delete the registry key, `reg` prints **"The operation completed successfully."**,
and the menu item is **still there**. This is not a caching problem. There are three
real causes, and this project handles all three.

### 1.1 The key name is padded with spaces (the big one)

Real capture from a live machine:

```
HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers
    key name: [    sgshellext]        length=14
    charcodes: 0020 0020 0020 0020 0073 0067 ...
               ^^^^ four leading spaces
```

The extension's visible name is `sgshellext`, but the registry key is actually
`"    sgshellext"`. So this silently deletes nothing, while reporting success:

```powershell
Remove-Item 'HKLM:\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\sgshellext' -Force
reg delete "HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\sgshellext" /f
```

OneDrive does the same thing (`" FileSyncEx"`, one leading space), which is why the
"remove OneDrive from the right-click menu" registry edits found online so often
appear to work and change nothing.

**This project never addresses these keys by a fixed name.** It enumerates the
container and matches substrings.

### 1.2 The app registered two mechanisms, and you removed one

| Shape | Where it lives | What removing it does |
|---|---|---|
| Static verb | `...\shell\<Verb>` plus a `command` subkey | removes that verb |
| COM shell extension | `...\shellex\ContextMenuHandlers\<Name>`, default value = CLSID | **this is what usually draws the menu item** |

Baidu Netdisk registered **both** (`YunShellExplorerCommand` as a static verb *and*
`YunShellExt` as a COM handler). Deleting only the static verb changes nothing.

### 1.3 You removed it from one registry view only

The 32-bit and 64-bit registry views are separate stores. Removing only one leaves
the other live. Measured on the affected machine: after deleting the 64-bit entries,
the same keys were still `PRESENT` in the 32-bit view.

Worse, Windows PowerShell 5.1 cannot even *read* both views from one process
(`RegistryKey.OpenBaseKey` does not exist there), which is why the scripts here have
to re-launch 32-bit PowerShell to cover the other view.

---

## 2. Main features

### Three tools

| Tool | What it does |
|---|---|
| **`manage-context-menu.ps1`** | Lists every context-menu component from both registry views, grouped by the owning DLL/executable, numbered, each labelled third-party or Windows built-in. You pick by number and it removes **all registrations** of that component. |
| **`remove-vendor-context-menu.ps1`** | Repeatable, scriptable removal driven by vendor patterns from `vendors.json`. Supports adding your own patterns. |
| **`enumerate-context-menu.ps1`** | Read-only audit: prints the **real** registry key names, character codes for space-padded names, and the COM server DLL behind each handler. |

### Behaviour that matters

- **Enumeration instead of fixed names** — works around space-padded keys.
- **Both registry views in one run** — a 64-bit invocation re-launches itself in
  32-bit PowerShell and merges the result, so you do not have to remember `/reg:32`.
- **`.reg` backup before every delete** — via `reg.exe export`, which writes UTF-16LE,
  exactly what `reg import` expects. Rollback is one command.
- **Verified deletes** — after each removal the key is re-read. A failure is reported
  as `FAILED (exit N): ...` or `STILL PRESENT`; it is never swallowed.
- **Safe by default** — Windows built-ins are labelled and treated conservatively:
  anything that cannot be positively attributed to a third party (a bare module name
  like `@shell32.dll,-8506`, a generic verb like `open`/`runas`, an unresolved CLSID)
  is classified as a built-in. `manage-context-menu.ps1` does not print them and
  `-SelectWin` is needed to touch them at all.
- **No third-party dependencies** — plain Windows PowerShell 5.1 and `reg.exe`.
- **ASCII-only script sources** — PowerShell 5.1 reads a BOM-less UTF-8 script using
  the ANSI code page, which mangles literal non-ASCII text (including inside paths).
  Scripts here keep their source ASCII and build CJK patterns from `\uXXXX` escapes or
  code points.

### What it does *not* do

- It does **not** uninstall apps, delete their files, or change file associations. It
  only removes the context-menu *registration* keys.
- It does **not** prevent the vendor from re-registering on its next update. See
  [Making removal stick](#making-removal-stick).
- It does **not** cover Explorer's own toolbar, the "Send to" folder, or Internet
  Explorer `MenuExt` entries.

---

## 3. Installation

No installer, no dependencies beyond Windows itself.

**Requirements**

- Windows 10 or 11 — verified on Windows 10 22H2 (build 19045)
- Windows PowerShell 5.1 (built into Windows) — **verified**
- PowerShell 7+ — expected to work from the code, but **not verified on this project**
- Administrator rights for removal. Not needed for `-DryRun`, `-ListOnly`, or
  `enumerate-context-menu.ps1`

**Get the files**

```powershell
git clone https://github.com/<you>/windows-context-menu-cleanup.git
cd windows-context-menu-cleanup
```

Or download the ZIP from the Releases page and extract it anywhere. If you hit an
execution-policy error, run the scripts with `-ExecutionPolicy Bypass`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\validate.ps1
```

**Sanity check before you delete anything** (read-only, no admin needed):

```powershell
.\scripts\validate.ps1
```

It checks JSON loading, `\uXXXX` decoding, that the scripts parse in your PowerShell
edition, that their sources are ASCII-only, and then performs a dry run. Expected
output ends with `ALL CHECKS PASSED`.

---

## 4. Usage

### Pick items by hand (recommended)

```powershell
# 1. Look at everything (no admin needed, changes nothing)
.\scripts\manage-context-menu.ps1 -ListOnly

# 2. Pick numbers (elevated PowerShell)
.\scripts\manage-context-menu.ps1
```

`-ListOnly` prints third-party components by default. Windows built-ins are still
numbered, so you can target one deliberately, but they are not printed unless you use
`-Grid` (which lists everything) or `-SelectWin`.

**Parameters of `manage-context-menu.ps1`**

| Parameter | Meaning |
|---|---|
| `-ListOnly` | print the numbered list and exit; deletes nothing; no admin needed |
| `-Grid` | pick with a searchable window (checkboxes) instead of typing numbers |
| `-Select <int[]>` | remove these numbers directly, e.g. `-Select 3,7` |
| `-SelectWin` | select all Windows built-ins (advanced; see the warning below) |
| `-NoRestart` | do not restart Explorer afterwards |
| `-NoBackup` | skip the `.reg` backups (not recommended) |
| `-ExportMyVendors <path>` | write the third-party key names it found to a JSON file you can feed to `-VendorsFile`. Starting point, not a finished config: real key names can be bare CLSIDs or verb ids, so trim the list before relying on it. |
| `-BackupDir <path>` | where to write backups; default `.\context-menu-backup-<timestamp>` |

### Pattern-based removal

```powershell
# 1. See what matches the built-in + vendors.json patterns
.\scripts\remove-vendor-context-menu.ps1 -DryRun

# 2. Remove (elevated)
.\scripts\remove-vendor-context-menu.ps1
```

**Parameters of `remove-vendor-context-menu.ps1`**

| Parameter | Meaning |
|---|---|
| `-DryRun` | list matches, delete nothing, no admin needed |
| `-Pattern <string[]>` | add your own key-name substrings on top of the configured ones |
| `-VendorsFile <path>` | use a different vendor config (default `scripts\vendors.json`) |
| `-BackupDir <path>` | where to write backups |
| `-NoPause` | do not wait for Enter at the end (for scripted use) |

Configuration lives in [`scripts/vendors.json`](./scripts/vendors.json) — 7 vendor
entries, 25 key-name patterns. Write CJK keywords as `\uXXXX` escapes; the script
decodes them itself because PowerShell 5.1's `ConvertFrom-Json` does not.

### Audit only

```powershell
.\scripts\enumerate-context-menu.ps1
```

Read-only. Prints the real key name of every registration, flags padded names with
their character codes, resolves each CLSID to its DLL, and shows static-verb command
lines. It reads the **64-bit** registry view.

### About restarting Explorer

The scripts restart Explorer after a successful removal so the menu refreshes
immediately. **Your taskbar and desktop icons will disappear and come back** — that is
normal, not a crash. Use `-NoRestart` to skip it and restart Explorer yourself later.

### Warning about `-SelectWin`

`-SelectWin` removes Windows' own shell extensions, including things like "Open with",
"Send to", "Pin to Start" and sharing entries. This can break Explorer. It exists for
advanced troubleshooting only. Prefer `-Select` with specific numbers.

---

## 5. Examples

### Example A — see what is in your menu (safe, no admin)

```powershell
PS> .\scripts\manage-context-menu.ps1 -ListOnly

=== Windows built-ins (NOT selected; only remove these if you know why) ===
...
=== Third-party add-ons ===
  [ 47] shellext.dll   (3 registration(s))
        from: C:\Program Files\Windows Defender
        Directory               COM handler   64-bit HKLM
        Drive                   COM handler   64-bit HKLM
        *                       COM handler   64-bit HKLM
  [ 50] FileSyncShell64.dll   (4 registration(s))
        from: C:\Program Files\Microsoft OneDrive\26.168.0830.0006
        Directory               COM handler   64-bit HKLM   [padded key name]
        lnkfile                 COM handler   64-bit HKLM   [padded key name]
        ...
  [ 60] QQMusic.exe  [QQMusic.2.Add]   (2 registration(s))
        runs: "C:\Program Files\QQMusic\QQMusic.exe" /add "%1"
        Directory               static verb  64-bit HKLM
        Directory               static verb  32-bit HKLM
```

Note `[padded key name]` — that is the anti-removal trick being detected and flagged.

### Example B — remove one component interactively

```powershell
PS> .\scripts\manage-context-menu.ps1
...
Enter the numbers of the components to DELETE, separated by spaces or commas.
  e.g.   1 4 7   or   2,3        (empty = cancel)
Selection: 3

About to remove 4 registry key(s) from 1 component(s).
Type YES to confirm: YES

Deleting [OneDrive]  HKLM\Directory\shellex\ContextMenuHandlers\ FileSyncEx
        backup -> .\context-menu-backup-<timestamp>\HKLM_Directory_shellex_ContextMenuHandlers__FileSyncEx.reg
        -> DELETED
...
Deleted 4, failed 0.
Backups in .\context-menu-backup-<timestamp>
Restarting Explorer...
Explorer is running (pid 12345).
```

### Example C — scripted, no prompts

```powershell
# Elevated shell
.\scripts\manage-context-menu.ps1 -Select 3,7 -NoRestart
```

### Example D — remove known vendors by pattern

```powershell
PS> .\scripts\remove-vendor-context-menu.ps1 -DryRun
Loaded 24 patterns from ...\scripts\vendors.json

MATCH  HKLM /reg:64 *\shellex\ContextMenuHandlers
       key name : [    sgshellext]  length=14   <== PADDED NAME
       charcodes: 0020 0020 0020 0020 0073 0067 0073 0068 0065 006C 006C 0065 0078 0074
...
=== 64-bit view: matched 8, deleted 0, failed 0 (dry run) ===
```

Then, elevated and without `-DryRun`, each match becomes `-> DELETED` or
`-> FAILED (exit N): ...`.

### Example E — add a vendor of your own

```powershell
# 1. Find the key name with the audit tool
.\scripts\enumerate-context-menu.ps1 | Select-String -Pattern 'PADDED' -Context 3,0

# 2. Add the substring to scripts/vendors.json
#    { "vendor": "Some App", "patterns": ["SomeAppShellExt"] }

# 3. Dry-run, then remove
.\scripts\remove-vendor-context-menu.ps1 -DryRun
.\scripts\remove-vendor-context-menu.ps1
```

### Example F — roll back

```powershell
reg import ".\context-menu-backup-<timestamp>\HKLM_Directory_shellex_ContextMenuHandlers__FileSyncEx.reg"
# then restart Explorer
Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process explorer.exe
```

### Making removal stick

Vendors usually re-create these keys on the next launch or update. In order of
preference:

1. **Turn the integration off in the app's own settings.** Cleanest, and it survives
   updates. (Sogou: 属性设置 → 高级 → 右键菜单. Baidu Netdisk and Thunder have similar
   toggles.)
2. **Deny write access on the specific keys** for your user, so the app cannot
   re-create them — `regini` or an ACL edit. More forceful; must be redone if an
   installer recreates the key with fresh ACLs.
3. **Re-run this script** after the vendor updates.

---

## Using this as a DSH skill

This repository is also a valid [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)
skill. Copy the folder into `$DSH_HOME/skills/` (usually `~/.dsh/skills/`), keeping
`SKILL.md` at the root:

```powershell
Copy-Item . "$env:USERPROFILE\.dsh\skills\windows-context-menu-cleanup" -Recurse
```

## Troubleshooting

See [`references/troubleshooting.md`](./references/troubleshooting.md). The most
expensive ones:

| Symptom | Cause |
|---|---|
| `MethodNotFound: ... does not contain a method named OpenBaseKey` | You are on PowerShell 5.1; that API is .NET Core / PS 7 only. Check `$PSVersionTable.PSEdition`. |
| `Access is denied` | The key is under `HKLM` and the process is not elevated. |
| Item gone for 64-bit apps but not 32-bit (or vice versa) | The other registry view. |
| `New-Item` made one weird concatenated key | The `*` class key is a wildcard to the PowerShell registry provider. Use `-LiteralPath` to read and `reg.exe` to delete. |
| Desktop and taskbar disappeared | Explorer was killed; `Start-Process explorer.exe` brings it back. |
| Chinese text turned into mojibake | A PS 5.1 console decodes child output using the ANSI code page. Keep script source ASCII. |

Verified vendor registration points (with the exact real key names, including padding)
are recorded in [`references/field-notes.md`](./references/field-notes.md).

## Roadmap

- **v1.0.0** — PowerShell tooling, no compiled binary.
- **v1.1.0 (this release)** — adds the working native `CtxMenuCleaner.exe`, the
  `src/publish.ps1` release tool, and a batch of executable fixes (see
  [CHANGELOG](./CHANGELOG.md)).
- **Later** — interactive input timing can still be consumed by a preceding prompt on
  some terminals; prefer `--select N --yes` (see
  [references/exe.md](./references/exe.md)).

## The native executable (CtxMenuCleaner.exe)

Compiled with the C# compiler that ships with Windows: **single file, no dependencies,
no PowerShell**. It reads **both** registry views in one process via
`RegistryKey.OpenBaseKey` — which PowerShell 5.1 cannot do — so it has no PowerShell
version dependency at all.

Full details in [references/exe.md](./references/exe.md). The essentials:

```powershell
# Build (output goes to %LOCALAPPDATA%\CtxMenuCleaner\, deliberately avoiding non-ASCII paths)
.\src\build.ps1 -Test

$ctx = "$env:LOCALAPPDATA\CtxMenuCleaner\CtxMenuCleaner.exe"

# See the list (no admin needed, changes nothing)
& $ctx --list

# Dry run (deletes nothing)
& $ctx --select 4 --dry-run

# Remove (--yes skips the confirmation, so it reads no keyboard input at all - recommended)
& $ctx --select 4 --yes

# Interactive: it lists first, waits for your numbers at "Selection:",
# then requires you to type an upper-case YES at "Type YES to confirm:" before deleting.
& $ctx
```

> **One easy trap in interactive mode**: after you enter the numbers, the program stops
> and requires **`YES`** (three upper-case letters) before it deletes anything. Anything
> else — including a bare Enter — just cancels. Use `--yes` to skip that step.

**Common options**

| Option | Meaning |
|---|---|
| `--list` | list only |
| `--all` | include Windows built-ins in the list |
| `--select N[,M...]` | remove these slot numbers |
| `--dry-run` | show what would happen, delete nothing |
| `--yes` | skip the `Type YES` confirmation |
| `--grid` | pick with a searchable check-box window |
| `--auto-elevate` | re-launch elevated without asking `[Y/n]` |
| `--no-restart` | do not restart Explorer |
| `--no-backup` | skip the `.reg` backups (not recommended) |
| `--backup-dir D` | where to write backups |
| `--selftest` | report whether this process can write to HKCU (diagnoses the path issue) |
| `--stdin-info` | report whether stdin is a real terminal (diagnoses input issues) |
| `--log <file>` | record what every prompt actually read (diagnoses input issues) |
| `--keep-open` | wait for Enter before exiting, so the window cannot vanish |

⚠️ **Do not run the exe from a non-ASCII or OneDrive-synced folder.** On the test machine,
a binary launched from `C:\Users\<user>\OneDrive\文档\...` could not write to the registry
at all, while the identical binary copied to an ASCII path worked. That is why `build.ps1`
outputs to `%LOCALAPPDATA%` by default. Verify with `CtxMenuCleaner.exe --selftest`.

## Credits and provenance

Every pitfall documented here was hit for real while removing Baidu Netdisk, Thunder
and Sogou Input entries from a live machine — including a first attempt that reported
success with zero bytes deleted, a script that died on line 90 because of the
`OpenBaseKey` mistake, and a killed Explorer that left a blank desktop. The
troubleshooting notes exist so you do not have to rediscover them.

## License

MIT — see [LICENSE](./LICENSE).

> **Registry risk notice.** This project edits the Windows registry: it deletes the
> keys that register third-party Explorer context-menu entries and exports a `.reg`
> backup of each key beforehand. Registry edits carry inherent risk — review the
> scripts and the backup output before running them, and test on a non-critical
> machine first. The authors take no responsibility for damage resulting from its use.

---

## Test environment

All verification for this release was performed on the machine described below.
Anything marked *not verified* must not be assumed to work.

| Item | Value |
|---|---|
| **Verified on** | 2026-10-06 |
| **OS** | Windows 10 Pro 22H2 |
| **Build** | 10.0.19045.6466 (`CurrentBuild 19045`, `UBR 6466`, `ReleaseId 2009`) |
| **Architecture** | 64-bit |
| **CPU** | 12th Gen Intel Core i5-12600KF (10 cores / 16 threads) |
| **RAM** | 31.8 GB |
| **System locale** | Chinese (Simplified, China) · `zh-CN` |
| **ANSI code page** | 936 (GBK) — the reason the script sources must stay pure ASCII |
| **Windows PowerShell** | 5.1.19041.6456 (`PSEdition = Desktop`) **← all testing done here** |
| **CLR** | 4.0.30319.42000 |
| **.NET Framework** | 4.8.09037 |
| **C# compiler** | `csc.exe` 4.8.9232.0 (used only by the unreleased v2 executable) |
| **reg.exe** | `C:\Windows\system32\reg.exe` |
| **PowerShell 7+** | **not installed, not tested** |
| **Time zone** | China Standard Time (UTC+08:00) |

### Verified on this machine

- All 5 scripts parse under PowerShell 5.1 with **0 non-ASCII bytes** in their sources.
- `validate.ps1` passes completely, including the `\u641c\u72d7` → `641C 72D7` decode
  assertion.
- A key name padded with 4 leading spaces is enumerated, matched and **actually
  deleted** (proved with a temporary HKCU test key).
- A CJK key name (`百度网盘`) is matched correctly (proved with a temporary test key,
  then removed).
- Both registry views are covered; the 32-bit view is merged in from a child process.
- `reg.exe export` produces a 356-byte UTF-16LE backup that `reg import` accepts.
- All 13 documented parameters were invoked for real and resolve correctly.
- `-DryRun` was confirmed to perform no deletions.
- The original targets (8 Sogou + 8 Baidu keys, 16 in total) end at **0 remaining**.

### Not verified

- PowerShell 7+ (`pwsh`) behaviour — designed for, never executed.
- Windows 11, or any build other than 19045.6466.
- Non-Chinese system locales (the ASCII-source design should make these safe, but it
  has not been exercised).
