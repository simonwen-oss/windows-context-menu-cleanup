# windows-context-menu-cleanup

Remove the entries that third-party software adds to the Windows Explorer right-click menu
(Baidu Netdisk, Thunder/Xunlei, Sogou Input, 360 and friends) — **look before you delete,
delete with a rollback path, and never report a success that did not happen**.

[中文说明](./README.md) · [Changelog](./CHANGELOG.md) · [MIT licence](./LICENSE)

**Current release: v1.1.3.** Two ways to use it: a **native executable** (recommended —
single file, no PowerShell), and a set of **PowerShell scripts** (no build step, easy to
audit and extend).

---

## Contents

- [1. What problem this solves](#1-what-problem-this-solves)
- [2. Main features](#2-main-features)
- [3. Installation](#3-installation)
- [4. Usage](#4-usage)
- [5. Examples](#5-examples)
- [FAQ](#faq)
- [Safety and rollback](#safety-and-rollback)
- [Test environment](#test-environment)

**In a hurry?** Jump to [Quick start](#quick-start).

---

## 1. What problem this solves

You delete the registry key, `reg` prints **"The operation completed successfully."**, and
the menu item is **still there**. It is not a caching problem. There are three causes.

### 1.1 The key name is space-padded (the main one)

Captured from a real machine:

```
key name : [    sgshellext]        length=14
charcodes: 0020 0020 0020 0020 0073 0067 ...      <- four leading spaces
```

The visible name is `sgshellext`, but the key is **actually called** `"    sgshellext"`.
So both of these commands report success and **delete nothing**:

```powershell
Remove-Item '...\ContextMenuHandlers\sgshellext' -Force
reg delete "...\ContextMenuHandlers\sgshellext" /f
```

OneDrive does the same thing (`" FileSyncEx"`) — which is why the widely copied
"remove OneDrive from the right-click menu" registry edit appears to work and does not.

> **This project never addresses a fixed key name** — it enumerates the container and
> matches substrings.

### 1.2 A vendor registers two mechanisms; you removed one

| Shape | Location | Note |
|---|---|---|
| Static verb | `...\shell\<verb>` plus a `command` subkey | Removing the verb is enough |
| COM handler | `...\shellex\ContextMenuHandlers\<name>`, default value is a CLSID | **This is usually what actually draws the menu item** |

Baidu Netdisk **uses both** (`YunShellExplorerCommand` *and* `YunShellExt`); removing only
the first changes nothing.

### 1.3 You removed only one registry view

The 32-bit and 64-bit views are **two separate stores**. Removing one leaves the other in
force (measured: after deleting the 64-bit entry, the same key is still present in the
32-bit view).

PowerShell 5.1 also **cannot read both views in one process** (`RegistryKey.OpenBaseKey`
does not exist there), so the scripts re-launch a 32-bit child to cover the other view.
The native executable has that API and needs only one process.

---

## 2. Main features

### 2.1 Two entry points, pick one

| | Native `CtxMenuCleaner.exe` | PowerShell scripts |
|---|---|---|
| Needs compiling | One command (compiler ships with Windows) | No |
| Depends on PowerShell | **Not at all** | Needs the bundled 5.1 |
| Easy to audit | Requires reading C# | Plain text, open and read |
| Best for | Everyday use, batch cleanup | Reviewing code, extending it |

**Capabilities are identical**: enumerate-and-match (never a fixed key name), both registry
views, backup before delete, read-back verification after delete.

### 2.2 Five scripts

| Script | Purpose |
|---|---|
| **`manage-context-menu.ps1`** | Lists every component (grouped and numbered by owning DLL/exe, marked third-party or built-in). Pick numbers and it removes **all** of that component's registrations |
| **`remove-vendor-context-menu.ps1`** | **Repeatable batch removal** driven by `vendors.json` signatures; you can append your own |
| **`enumerate-context-menu.ps1`** | **Read-only audit**: real key names, character codes, the DLL behind each CLSID. Use it to answer "who owns this menu item?" |
| **`validate.ps1`** | **Self-check**: JSON loading, `\uXXXX` decoding, syntax, pure-ASCII source, plus a dry run. No admin needed |
| `_list-view.ps1` | Internal helper (enumerates the 32-bit view); not meant to be run by hand |

### 2.3 Two further guarantees

- **Read-back verification after every delete, errors never swallowed** — failures are
  reported explicitly as `-> FAILED: ...`. This is where the project started: the first
  version trusted `reg`'s exit code and falsely reported success once.
- **Safe by default** — Windows built-ins are marked and **never selected by default**.
  Anything not clearly attributable to a third party (bare module names such as
  `@shell32.dll,-8506`, generic verbs such as `open`/`runas`, unresolved CLSIDs) counts as
  built-in. Only `-SelectWin` touches them.

### 2.4 What it does **not** do

- It does **not uninstall** software, delete files, or change file associations — only the
  right-click menu **registrations**.
- It **cannot stop** software from re-registering on its next launch; see
  [preventing the menu from coming back](#preventing-the-menu-from-coming-back).
- It does **not cover** the Explorer toolbar, the "Send to" folder, or Internet Explorer's
  `MenuExt` entries.

---

## 3. Installation

No installer; no dependency beyond Windows itself.

| Item | Requirement |
|---|---|
| Operating system | Windows 10 / 11 — measured on Windows 10 22H2 (build 19045) |
| PowerShell | 5.1 (bundled) **measured**; 7+ should work by design but is **untested** |
| .NET Framework | 4.x (bundled with Windows 10), needed only to compile the exe |
| Administrator | Needed to delete; not needed for `--list` / `-ListOnly` / `--dry-run` / `-DryRun` / `--selftest` / `validate.ps1` |

Get the files:

```powershell
git clone https://github.com/simonwen-oss/windows-context-menu-cleanup.git
cd windows-context-menu-cleanup
```

Or download the ZIP from [Releases](https://github.com/simonwen-oss/windows-context-menu-cleanup/releases).

> If you hit `running scripts is disabled`, add `-ExecutionPolicy Bypass`, e.g.
> `powershell -ExecutionPolicy Bypass -File .\scripts\validate.ps1`.

**Self-check first** (read-only, no admin, expected to end with `ALL CHECKS PASSED`):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\validate.ps1
```

**Compile the executable** (optional):

```powershell
.\src\build.ps1 -Test        # output: %LOCALAPPDATA%\CtxMenuCleaner\CtxMenuCleaner.exe
```

> ⚠️ The default output location is deliberate — **do not move it into a folder whose path
> contains non-ASCII characters or is synced by OneDrive**. An exe launched from such a path
> cannot write to the registry at all. See
> [FAQ](#running-the-exe-from-a-non-ascii-or-onedrive-folder-makes-every-delete-fail).

---

## 4. Usage

### Quick start

```powershell
# ---------- native exe (recommended) ----------
$ctx = "$env:LOCALAPPDATA\CtxMenuCleaner\CtxMenuCleaner.exe"
& $ctx --list                 # 1. see the list (no admin, changes nothing)
& $ctx --select 4 --dry-run   # 2. rehearse; confirm what it would remove
& $ctx --select 4 --yes       # 3. really remove (elevated shell); --yes reads no keyboard

# ---------- PowerShell scripts ----------
.\scripts\manage-context-menu.ps1 -ListOnly      # 1. see the list (no admin)
.\scripts\manage-context-menu.ps1 -Select 4      # 2. really remove (elevated shell)
.\scripts\remove-vendor-context-menu.ps1 -DryRun # rehearse the signature-based removal
```

**Slot numbers**: third-party components start at `1`, Windows built-ins follow. Numbers
depend on enumeration order and **can change between runs** — look first, then delete.
`manage-context-menu.ps1` has no dry-run switch; use `-ListOnly` to look.

### Screenshots

**`--list` shows you what is there** (no admin, changes nothing):

![CtxMenuCleaner --list output](docs/ctxmenu-list.png)

The highlighted rows are entries whose **key name is space-padded** — those cannot be
removed by fixed name, which is the main reason this project exists.

**Interactive removal, and that `YES`:**

![CtxMenuCleaner interactive removal flow](docs/ctxmenu-delete-flow.png)

After entering the numbers you must still type `YES` in upper case before anything is
deleted. Add `--yes` to skip it (the command then reads no keyboard at all).

### Where the options are documented

- **exe**: run `CtxMenuCleaner.exe --help`. Every option is grouped under
  **Options / Diagnostics / Misc**.
- **scripts**: the sections below cover them by task; the full reference, including a
  Chinese-language table, is in [`references/exe.md`](./references/exe.md) and in each
  script's header.

Signatures live in [`scripts/vendors.json`](./scripts/vendors.json) (7 vendors, 25
patterns). Non-ASCII keywords must be written as `\uXXXX` escapes — PowerShell 5.1's
`ConvertFrom-Json` does not decode them, so the scripts decode them themselves.

`enumerate-context-menu.ps1` takes no parameters; it is read-only and reads the 64-bit view.

### ⚠️ Interactive mode: that `YES`

Running the exe with neither `--list` nor `--select` starts interactive mode: it asks for
slot numbers at `Selection:`, then **requires three upper-case letters `YES`** before it
deletes anything. Anything else — including a bare Enter — merely cancels.

**This is the easiest trap in the whole tool** — typing the next command at that prompt is
read as a cancel, which looks exactly like "typing 4 did nothing". Add `--yes` to avoid it
entirely. Full transcript in [example D](#example-d--interactive-removal-note-the-yes).

### About restarting Explorer

A successful removal restarts Explorer so the menu refreshes immediately. **Your taskbar
and desktop icons disappear for a moment and come back** — that is normal, not a crash. Use
`-NoRestart` / `--no-restart` to skip it:

```powershell
Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process explorer.exe
```

### ⚠️ `-SelectWin` warning

It selects Windows' own shell extensions ("Open with", "Send to", "Pin to Start", Sharing
and so on) and **can break Explorer**. It exists for advanced troubleshooting only — prefer
naming specific numbers with `-Select`. (The exe's `--all` merely **lists** built-ins; it
does not select them, so it is safe.)

---

## 5. Examples

### Example A — see what is in the menu (safe, no admin)

```powershell
PS> & $ctx --list

 CtxMenuCleaner 1.1.3    64-bit process: True    Elevated: False
Found 52 component(s): 7 third-party, 45 Windows built-in.

  [  1] FileSyncShell64.dll   (4 registration(s))
        from: C:\Program Files\Microsoft OneDrive\26.173.0906.0008
        *                       COM handler   64-bit HKLM   [padded key name]
        Directory               COM handler   64-bit HKLM   [padded key name]
        ...
  [  4] YunShellExtV164.dll   (2 registration(s))
        from: C:\Users\<user>\AppData\Roaming\baidu\BaiduNetdisk
        *                       COM handler   64-bit HKLM
        Directory               COM handler   64-bit HKLM

(7 slot(s) shown of 52 total. Windows built-ins are numbered too but not shown; --all shows them.)
```

Note `[padded key name]` — that is the anti-removal trick **being detected and flagged**.

### Example B — rehearse, confirm what would be removed

```powershell
PS> & $ctx --select 4 --dry-run
About to remove 2 registry key(s) from 1 component(s).
Dry run: nothing was deleted.
```

### Example C — really remove, in one non-interactive step

```powershell
# elevated shell
PS> & $ctx --select 4 --yes

Deleting HKLM\*\shellex\ContextMenuHandlers\YunShellExt
        backup -> C:\ctx-backup\HKLM___shellex_ContextMenuHandlers_YunShellExt.reg
        -> DELETED
Deleting HKLM\Directory\shellex\ContextMenuHandlers\YunShellExt
        backup -> C:\ctx-backup\HKLM_Directory_shellex_ContextMenuHandlers_YunShellExt.reg
        -> DELETED

Deleted 2, failed 0.   Backups in C:\ctx-backup
Restarting Explorer so the menu refreshes...
Explorer is running (pid 5600).
```

### Example D — interactive removal (note the `YES`)

```powershell
PS> & $ctx
...
Selection: 4

About to remove 2 registry key(s) from 1 component(s).

>>> Type YES (three letters, upper case) and press Enter to actually delete.
>>> Anything else - including just Enter - cancels. Nothing has been changed yet.
Type YES to confirm: YES          <- must be upper-case YES
```

### Example E — signature-based batch removal with the PowerShell script

```powershell
PS> .\scripts\remove-vendor-context-menu.ps1 -DryRun
Loaded 24 patterns from ...\scripts\vendors.json

MATCH  HKLM /reg:64 *\shellex\ContextMenuHandlers
       key name : [    sgshellext]  length=14   <== PADDED NAME
       charcodes: 0020 0020 0020 0020 0073 0067 0073 0068 0065 006C 006C 0065 0078 0074
...
=== 64-bit view: matched 8, deleted 0, failed 0 (dry run) ===
```

Drop `-DryRun` and run elevated, and each match becomes `-> DELETED` or
`-> FAILED (exit N): ...`.

### Example F — find out who owns a menu item

```powershell
PS> .\scripts\enumerate-context-menu.ps1 | Select-String 'PADDED' -Context 3,0
```

It prints the real key name, its character codes, and the DLL behind each CLSID. Take the
key-name substring, add it to `patterns` in
[`scripts/vendors.json`](./scripts/vendors.json), then `-DryRun` → remove:

```json
{ "vendor": "SomeApp", "patterns": ["SomeAppShellExt"] }
```

### Example G — roll back

```powershell
reg import ".\context-menu-backup-<timestamp>\HKLM_Directory_shellex_ContextMenuHandlers__FileSyncEx.reg"
Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process explorer.exe
```

### Preventing the menu from coming back

Software usually **writes the keys back** on its next launch or update. In order of
preference:

1. **Turn the integration off in the application's own settings** — cleanest, and it
   survives updates (Sogou: Properties → Advanced → context menu; Baidu Netdisk and Thunder
   have similar switches).
2. **Deny write access on the specific keys** (`regini` or an ACL change) — heavier handed;
   if an installer recreates the key with a fresh ACL you have to redo it.
3. **Re-run this script after each update.**

## FAQ

### Running the exe from a non-ASCII or OneDrive folder makes every delete fail

Symptom: `--list` works, but every delete reports
`-> FAILED: OpenSubKey(writable) ... SecurityException HR=0x8013150A`. This is not a
permissions misconfiguration but a **launch location** problem (same binary, measured):

| exe launched from | can write HKCU |
|---|---|
| `C:\Users\<user>\OneDrive\文档\...` (non-ASCII + OneDrive) | ❌ not at all |
| `C:\Users\<user>\AppData\Local\Temp\...` | ✅ fine |
| `C:\Users\<user>\AppData\Local\CtxMenuCleaner\` | ✅ fine |

**Fix**: confirm with the single command `--selftest`, then put the exe on a pure-ASCII path
(the default build location already is one). The PowerShell scripts are **not affected** —
`powershell.exe` runs them, they are not a process launched from that folder.

### A delete reported success but the menu item is still there

Check whether the `--list` row shows `[padded key name]`, and whether you removed **both**
the static verb and the COM handler
(see [1.2](#12-a-vendor-registers-two-mechanisms-you-removed-one)). Then confirm both
registry views were covered (the exe always does; the scripts add the 32-bit view
automatically).

### Typing a number in interactive mode does nothing

The program is waiting for an upper-case **`YES`** at `Type YES to confirm:`. Anything else
cancels (it now reports what it actually received). Use `--yes` to skip the step entirely.

### Non-ASCII text comes out as mojibake

A 5.1 console decodes child-process output using the ANSI code page (936 on the test
machine). Keeping the script sources pure ASCII is what avoids this class of problem.

### More traps

| Symptom | Cause |
|---|---|
| `MethodNotFound: ... OpenBaseKey` | That API is .NET Core / PS 7 only and you are on 5.1. Check `$PSVersionTable.PSEdition` |
| `Access is denied` | The key is under `HKLM` and the process is not elevated |
| 64-bit menu item gone, 32-bit still there (or vice versa) | The other registry view was not handled |
| `New-Item` creates an oddly concatenated key name | `*` is a wildcard to the PowerShell registry provider. Read with `-LiteralPath`, delete with `reg.exe` |
| Desktop and taskbar vanished | You killed Explorer; `Start-Process explorer.exe` restores it |

- Full guide: [references/troubleshooting.md](./references/troubleshooting.md)
- **The wrong conclusions reached during development**:
  [references/lessons-learned.md](./references/lessons-learned.md)
- Measured vendor registration points and real key names:
  [references/field-notes.md](./references/field-notes.md)
- Full executable reference: [references/exe.md](./references/exe.md)

---

## Safety and rollback

**Backup**: before deleting, every key is exported with `reg.exe export` into a `.reg` file
(UTF-16LE, which is exactly what `reg import` expects):

```
context-menu-backup-20261006-221917\
    HKLM___shellex_ContextMenuHandlers_YunShellExt.reg
```

**Restore**:

```powershell
reg import "context-menu-backup-20261006-221917\HKLM___shellex_ContextMenuHandlers_YunShellExt.reg"
Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process explorer.exe
```

**Registry risk**: this project modifies the registry, which carries inherent risk —
**review the scripts and the backups first, and test on a machine you do not depend on**.
The author accepts no liability for damage; see [LICENSE](./LICENSE).

**As a DSH skill**: this repository is also a valid
[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) skill. Copy the whole
directory into `$DSH_HOME/skills/` (usually `~/.dsh/skills/`), keeping `SKILL.md` at its
root:

```powershell
Copy-Item . "$env:USERPROFILE\.dsh\skills\windows-context-menu-cleanup" -Recurse
```

---

## Test environment

Every verification was performed on the machine below. **Anything marked untested should not
be treated as working.**

| Item | Value |
|---|---|
| Verification date | 2026-10-06 |
| Operating system | Windows 10 Pro 22H2, build 10.0.19045.6466 (64-bit) |
| Language / locale | Chinese (Simplified) · `zh-CN` |
| **ANSI code page** | **936 (GBK)** — the reason the scripts must stay pure ASCII |
| **Windows PowerShell** | 5.1.19041.6456 (`Desktop`) **← all testing done here** |
| .NET / csc.exe | .NET FW 4.8.09037 / csc 4.8.9232.0 (used to compile the exe) |
| Hardware | i5-12600KF (10 cores / 16 threads) · 31.8 GB |
| **PowerShell 7+** | **not installed, not tested** |

**Measured as working**

- All 8 scripts parse under 5.1; sources contain **0 non-ASCII bytes**
- `validate.ps1` passes fully (including the `\u641c\u72d7` → `641C 72D7` decoding assertion)
- A key name with **4 leading spaces** can be enumerated, matched and **really deleted**;
  the Chinese key name `百度网盘` matches correctly
- Backups produced by `reg.exe export` can be consumed by `reg import`
- exe: both views read in one process (7-Zip appears as both `7-zip.dll` and `7-zip32.dll`),
  padded key names flagged, `--dry-run` deletes nothing, `--selftest` reports write access
- exe: console colour works and **degrades to plain text when output is redirected**
  (`--no-color` also forces that)
- exe: **the interactive flow was completed in a real elevated terminal** (numbers parsed →
  `YES` → 2 HKLM keys deleted → backups written → Explorer restarted)
- Original target (Sogou 8 entries + Baidu 8 entries) left **0** behind

**Explicitly untested**

- PowerShell 7+ (`pwsh`) · Windows 11 · any non-Chinese locale · any build other than
  19045.6466

---

## Credits and provenance

Every trap documented here was hit **for real** while removing Baidu Netdisk, Thunder and
Sogou Input from an actual machine — including the first "reported success but deleted zero
bytes", the script dying on line 90 because of `OpenBaseKey`, and a blank desktop after
killing Explorer.

This documentation exists so you do not have to hit them again. Developers should start with
[references/lessons-learned.md](./references/lessons-learned.md).
