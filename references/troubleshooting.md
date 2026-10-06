# Troubleshooting notes

Symptoms observed on real machines, with the actual cause. Read this before
re-debugging the same thing.

## "The remove command reported success but the menu item is still there"

Cause: the key name is space-padded, so the operation matched nothing.

```
HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\[    sgshellext]
                                                      ^^^^ 4 spaces
```

```powershell
reg delete "HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\sgshellext" /f
# -> The operation completed successfully.     (deleted nothing - that key does not exist)
```

Enumerating the container is the only reliable way to learn the real name. This is
also why OneDrive's context-menu entry "cannot be removed" by the registry edits that
circulate online — its key is `" FileSyncEx"`, with a leading space.

## "MethodNotFound: Microsoft.Win32.Registry does not contain a method named OpenBaseKey"

Cause: running under Windows PowerShell 5.1 (.NET Framework). `OpenBaseKey` is
.NET Core / PowerShell 7 only. The script fails on its first line, before any registry
work, so **nothing is deleted** — but it can look like the removal ran.

Check the host up front:

```powershell
$PSVersionTable.PSEdition   # 'Desktop' = 5.1,  'Core' = 7+
```

Fix: read with the PowerShell provider, delete with `reg.exe`.

## `ConvertFrom-Json` leaves `\u641c` as literal text

Cause: Windows PowerShell 5.1 does not unescape `\uXXXX` sequences when parsing JSON.
Patterns loaded from a config file silently never match CJK key names.

Fix: decode them explicitly. Note that the regex needs a **doubled** backslash,
because inside a regex `\u` must be followed by exactly four hex digits — a single
`\u` raises `hexadecimal digits are insufficient`.

```powershell
function ConvertFrom-UnicodeEscapes([string]$s) {
    if ($null -eq $s) { return $s }
    $rx = [regex]('\\u([0-9a-fA-F]{4})')
    $ev = [System.Text.RegularExpressions.MatchEvaluator] {
        param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16)
    }
    return $rx.Replace($s, $ev)
}
```

## "Access is denied" from `reg delete`

Cause: the key is under `HKLM` and the process is not elevated. `HKCU` keys do not
need elevation. Verify with:

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

## The item disappeared for 64-bit apps only, or for 32-bit apps only

Cause: the 32-bit and 64-bit registry views are separate stores, and only the view
matching the process bitness is visible. `reg.exe` needs `/reg:32` or `/reg:64`
explicitly. Verified on an affected machine: after removing only the 64-bit entries,
the same keys were still `PRESENT` in the 32-bit view.

Note also that `OpenSubKey('SOFTWARE\Classes\...')` on an `HKLM` base key can return
`$null` where the `HKLM:\` provider path works. Read through the provider or through
`RegistryHive.ClassesRoot`, and delete through `reg.exe` so the view is explicit.

## `New-Item` created one weird key instead of two

Cause: the `*` class key is treated as a wildcard by the PowerShell registry provider,
so names can end up concatenated into a single key
(e.g. `[    sgshellext_TEST YunShellExt_TEST]`). Use `-LiteralPath` for reading and
`reg.exe` for creating/deleting keys that sit under a literal `*`.

## The desktop and taskbar vanished

Cause: the script killed `explorer.exe`. It is the shell, not the OS — starting it
again restores everything:

```powershell
Start-Process explorer.exe
```

Make any cleanup script verify afterwards that an `explorer.exe` process exists.

## Chinese text shows as mojibake

Two distinct causes, both about code pages:

1. **Child-process output.** A PowerShell 5.1 console on a Chinese system decodes
   child output as GBK while the child emitted UTF-8. Write reports to a file with an
   explicit `UTF8Encoding($false)` and read the file instead of the console.
2. **Script source.** PowerShell 5.1 reads a BOM-less UTF-8 script using the ANSI code
   page, so literal CJK in the source is mangled — including inside string literals
   that hold paths. Observed: a path containing `文档` became `鏂囨。`, and the script
   then failed with "Cannot find path". Keep script source ASCII and construct CJK
   from code points or from `\uXXXX` escapes decoded at runtime.

## The vendor keeps coming back

Not a bug. Vendors re-register on launch or update. See the README section
"Making removal stick" for the options, in order of how well they survive updates.
