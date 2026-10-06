---
name: windows-context-menu-cleanup
description: Find and remove Windows Explorer right-click (context menu) entries added by third-party apps — Baidu Netdisk, Thunder/Xunlei, Sogou Input, 360, and similar — by locating their real registry keys. Use when the user wants to delete, disable, or clean up an unwanted "上传到…", "添加到…", compression/cloud/scan menu item, or when a previous removal reported success but the menu item is still there. Also use to audit which shell extensions are registered.
---

# Windows Context Menu Cleanup

Remove Explorer context-menu items by deleting the registry entries that register
them — and verify the deletion actually happened.

## The single most important fact

**These vendors pad the registry key name with leading spaces.**

Real capture from a live machine:

```
HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers
    key name: [    sgshellext]        length=14
    charcodes: 0020 0020 0020 0020 0073 0067 ...
               ^^^^ four leading spaces
```

An exact-name delete of `sgshellext` matches **nothing**. `reg delete` returns exit
code 0 and prints "The operation completed successfully"; `Remove-Item` raises no
error; the menu item survives. This is the standard anti-removal trick.

Also seen: `" FileSyncEx"` (OneDrive, one leading space) — so it is not limited to
Chinese vendors.

**Therefore: always enumerate the container and match on a substring. Never address
these keys by a fixed name, and never trust an unverified "success".**

## Workflow

1. **Enumerate (read-only, no admin needed).**
   Run [scripts/enumerate-context-menu.ps1](./scripts/enumerate-context-menu.ps1).
   It prints every context-menu registration with its **real** key name, char codes
   when padded, values, and the COM server DLL behind each `shellex` handler. Find
   the entry whose DLL / CLSID / command points at the vendor.
2. **Identify the mechanism.** Two shapes exist, and one app often uses both:
   - `...\shell\<Verb>` — a static verb with a `command` subkey holding the exe.
   - `...\shellex\ContextMenuHandlers\<Name>` — a COM shell extension whose default
     value is a CLSID, resolved through
     `HKCR\CLSID\{...}\InProcServer32` to a DLL.
     **Deleting the static verb does not touch the COM handler**, and it is the COM
     handler that usually draws the menu item.
3. **Scan and remove** with
   [scripts/remove-vendor-context-menu.ps1](./scripts/remove-vendor-context-menu.ps1).
   Start with `-DryRun` (no admin needed, deletes nothing), then run it elevated.
   It exports a `.reg` backup per key before deleting, covers **both** registry views
   automatically, and re-verifies afterwards.
4. **Verify** by re-enumerating: confirm the key is gone, not merely that the command
   exited 0.
5. **Restart Explorer** so the menu refreshes. The script does this and confirms a new
   `explorer.exe` is running.

## Where context-menu entries can live

Containers under `HKCR` (i.e. `HKLM\SOFTWARE\Classes` and `HKCU\SOFTWARE\Classes`):

| Class key | Menu appears when right-clicking |
|---|---|
| `*` | any file |
| `AllFilesystemObjects` | files and folders |
| `Directory` | a folder |
| `Directory\Background` | empty space inside a folder |
| `Drive` | a drive |
| `Folder` | a folder/shell folder |
| `DesktopBackground` | the desktop |
| `lnkfile` | a shortcut |
| `exefile` | an .exe |

Sub-keys to inspect, in decreasing order of how often they are the culprit:
`shellex\ContextMenuHandlers`, `shell`, `shellex\PropertySheetHandlers`,
`shellex\DragDropHandlers`.

A vendor may register in several class keys at once — Sogou, for instance, appeared
under `*`, `Directory`, `Directory\Background`, `Drive` and `lnkfile`.

## Pitfalls that cost real time

Full detail in [references/troubleshooting.md](./references/troubleshooting.md).
A longer and blunter list lives in
[references/lessons-learned.md](./references/lessons-learned.md) — it includes the
*wrong* conclusions, which are often more useful to read than the correct ones.

1. **`Registry.OpenBaseKey` does not exist in Windows PowerShell 5.1.** It is a
   .NET Core / PowerShell 7 API. Calling it in 5.1 throws `MethodNotFound` **on the
   first line**, so the script dies before doing any registry work. Check the host
   first: `$PSVersionTable.PSEdition -eq 'Desktop'` means 5.1. Use the PowerShell
   provider to read and `reg.exe` to delete.
2. **32-bit and 64-bit registry views are separate stores.** A process only sees the
   view matching its own bitness, and `reg.exe` only writes the view named by
   `/reg:32` or `/reg:64`. Cover both: run as 64-bit, then re-launch
   `$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe` for the 32-bit view.
3. **The `*` class key and the PowerShell registry provider do not mix.**
   `Test-Path 'HKLM:\...\Classes\*\...'` treats `*` as a wildcard, and
   `New-Item -Path '...\Classes\*\...\name'` can silently merge several names into one
   concatenated key. Read with `-LiteralPath`; delete with `reg.exe`.
4. **`ConvertFrom-Json` in PowerShell 5.1 does not decode `\uXXXX`.** It leaves the
   escapes as literal text, so CJK patterns loaded from a config file silently never
   match. Decode them yourself — and note the regex needs `\\u`, because inside a
   regex `\u` must be followed by exactly four hex digits.
5. **Quoting.** `reg delete "HKLM\...\ContextMenuHandlers\    sgshellext" /f /reg:64`
   works: PowerShell passes a single quoted argument through intact.
6. **HKLM needs elevation.** Without it every delete returns
   `ERROR: Access is denied.` Report that honestly — a script that prints "done"
   without verifying is worse than one that fails.
7. **Do not over-match.** A pattern like `*Thunder*` also hits Windows' own
   `EnhancedStorageShell`. Prefer specific vendor key names.
8. **Non-ASCII source is a trap.** PowerShell 5.1 reads a BOM-less UTF-8 script using
   the ANSI code page, so literal CJK in script source — including inside paths —
   gets mangled. Keep script source ASCII and build CJK from code points
   (`[char]0x641C`) or from `\uXXXX` escapes decoded at runtime.
9. **Non-ASCII console output.** A PS 5.1 console on a Chinese system decodes child
   output as ANSI/GBK, so UTF-8 text appears as mojibake. Write reports to a file with
   `UTF8Encoding($false)` and read the file instead of the console.
10. **Killing Explorer removes the desktop and taskbar.** Always confirm a new
    `explorer.exe` is running afterwards, or the user is left with a blank desktop.

## Backups and rollback

The removal script exports each key with `reg.exe export` before deleting it.
`reg.exe` writes UTF-16LE, which is exactly what `reg import` expects, so restoring a
single entry is `reg import <file>.reg` followed by an Explorer restart.

## Making removal stick

The vendor usually re-creates these keys on its next launch or update. In order of
preference:

1. Turn off the integration in the vendor's own settings — cleanest, survives updates.
2. Deny `Set Value` / `Create Subkey` on the specific keys for the current user
   (`regini` or an ACL edit). More forceful; must be redone if an installer recreates
   the key with fresh ACLs.
3. Re-run the removal script after the vendor updates.

## Field notes

Vendor registration points confirmed by direct inspection, with the exact real key
names: [references/field-notes.md](./references/field-notes.md).

## Requirements

Windows 10 / 11, Windows PowerShell 5.1 (built in) or PowerShell 7.
Administrator rights for removal; not needed for `-DryRun` or `enumerate`.
