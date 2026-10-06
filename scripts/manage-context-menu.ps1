# List every Explorer context-menu entry, numbered, then delete the ones you pick.
#
#   .\manage-context-menu.ps1              # interactive picker
#   .\manage-context-menu.ps1 -ListOnly    # just show the list
#   .\manage-context-menu.ps1 -Grid        # pick in a searchable GUI window
#   .\manage-context-menu.ps1 -Select 1,4,7
#   .\manage-context-menu.ps1 -SelectWin   # remove all Windows built-ins (advanced)
#   .\manage-context-menu.ps1 -ExportMyVendors .\my-vendors.json
#
# Bridges BOTH registry views, because a 64-bit PowerShell cannot see the 32-bit
# view and vice versa. Windows built-ins are labelled and are never selected unless
# you ask for them with -SelectWin.
#
# Deleting needs an elevated PowerShell. -ListOnly / -Grid / -ExportMyVendors do not.

[CmdletBinding()]
param(
    [switch]$ListOnly,
    [switch]$Grid,
    [int[]]$Select,
    [switch]$SelectWin,
    [switch]$NoRestart,
    [switch]$NoBackup,
    [string]$ExportMyVendors,
    [string]$BackupDir
)

$ErrorActionPreference = 'Continue'

$star = [char]42
$bs = [char]92

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$is32bit = [bool]$env:PROCESSOR_ARCHITEW6432
$regView = if ($is32bit) { '/reg:32' } else { '/reg:64' }
$viewDword = if ($is32bit) { '0x0100' } else { '0x0000' }

$here = Split-Path -Parent $PSCommandPath

# --- where a context-menu entry can be registered ---------------------------
$classes = @(
    $star
    'AllFilesystemObjects'
    'Directory'
    ('Directory' + $bs + 'Background')
    'Folder'
    'Drive'
    'DesktopBackground'
    'lnkfile'
    'exefile'
)
$kinds = @(
    'shell',
    ('shellex' + $bs + 'ContextMenuHandlers')
)

$classLabel = @{}
$classLabel[$star] = 'file'
$classLabel['AllFilesystemObjects'] = 'file+folder'
$classLabel['Directory'] = 'folder'
$classLabel[('Directory' + $bs + 'Background')] = 'folder-bg'
$classLabel['Folder'] = 'folder'
$classLabel['Drive'] = 'drive'
$classLabel['DesktopBackground'] = 'desktop'
$classLabel['lnkfile'] = 'shortcut'
$classLabel['exefile'] = 'exe'

# --- is this a Windows built-in rather than a third-party add-on? -----------
# Anything we cannot positively attribute to a third-party location is treated as a
# Windows built-in and is excluded from the default selection. Deleting a Windows
# shell extension can break Explorer, so the default must be conservative.
function Test-IsWindowsTarget([string]$target) {
    if ([string]::IsNullOrWhiteSpace($target)) { return $true }   # unknown -> system
    if ($target -match '^\{') { return $true }                   # bare CLSID: unresolved -> system
    if ($target -match '(?i)@?[A-Z]:\\Windows\\') { return $true }
    if ($target -match '(?i)^@') { return $true }                # "@shell32.dll,-8506" style: module-relative
    if ($target -match '(?i)%SystemRoot%|%windir%') { return $true }
    if ($target -match '(?i)^[A-Za-z0-9_.-]+\.dll') { return $true }  # bare DLL name -> resolved from System32
    if ($target -match '(?i)^(cmd|powershell|pwsh|explorer|control|rundll32|cscript|wscript)(\.exe)?\s') { return $true }
    if ($target -match '(?i)^"%1"|^%1\s') { return $true }       # generic open/runas verbs
    # Components shipped by Windows/its vendors that nonetheless live in Program Files.
    if ($target -match '(?i)\\Program Files\\Windows Defender\\') { return $true }
    if ($target -match '(?i)\\Program Files\\WindowsApps\\') { return $true }
    if ($target -match '(?i)\\Program Files\\NVIDIA Corporation\\') { return $true }
    if ($target -match '(?i)\\Program Files\\Microsoft OneDrive\\') { return $true }
    return $false
}

# --- collect this view ------------------------------------------------------
$entries = New-Object System.Collections.Generic.List[object]
$roots = @(
    @{ Hive = 'HKCU'; PS = ('HKCU:\SOFTWARE\Classes' + $bs); Raw = ('HKCU\SOFTWARE\Classes' + $bs) },
    @{ Hive = 'HKLM'; PS = ('HKLM:\SOFTWARE\Classes' + $bs); Raw = ('HKLM\SOFTWARE\Classes' + $bs) }
)

function Get-DllTarget([string]$clsid, [string]$psPrefix) {
    if ([string]::IsNullOrWhiteSpace($clsid) -or $clsid -notmatch '^\{') { return '' }
    $p = $psPrefix + 'CLSID' + $bs + $clsid + $bs + 'InProcServer32'
    if (Test-Path -LiteralPath $p) {
        try { return [string](Get-ItemProperty -LiteralPath $p).'(default)' } catch { return '' }
    }
    return ''
}

foreach ($root in $roots) {
    foreach ($cls in $classes) {
        foreach ($kind in $kinds) {
            $rel = $cls + $bs + $kind
            $container = $root.PS + $rel
            if (-not (Test-Path -LiteralPath $container)) { continue }
            foreach ($child in @(Get-ChildItem -LiteralPath $container -ErrorAction SilentlyContinue)) {
                $real = $child.PSChildName

                $def = ''
                try { $d = $child.GetValue($null); if ($d) { $def = [string]$d } } catch {}

                # resolve target
                $target = ''
                $shape = ''
                if ($kind -eq 'shell') {
                    $shape = 'verb'
                    $c = $child.OpenSubKey('command')
                    if ($c -ne $null) {
                        try { $target = [string]$c.GetValue($null) } catch {}
                        $c.Close()
                    }
                    if (-not $target) { $target = Get-DllTarget $def $root.PS }
                } else {
                    $shape = 'COM'
                    $target = Get-DllTarget $def $root.PS
                    if (-not $target) { $target = $def }
                }

                $isWindows = Test-IsWindowsTarget $target

                $name = $def
                if (-not $name) { $name = $real.Trim() }

                $entries.Add([pscustomobject]@{
                        View     = $viewDword
                        ViewName = $(if ($is32bit) { '32' } else { '64' })
                        Hive     = $root.Hive
                        Class    = $cls
                        Kind     = $kind
                        Rel      = $rel
                        RealName = $real
                        RawKey   = $root.Raw + $rel + $bs + $real
                        Name     = $name
                        Shape    = $shape
                        Target   = $target
                        Windows  = $isWindows
                        Padded   = ($real -ne $real.Trim())
                    })
            }
        }
    }
}

# --- merge in the other registry view ---------------------------------------
# A 64-bit PowerShell cannot enumerate the 32-bit view in-process, and
# Registry.OpenBaseKey does not exist on PowerShell 5.1. So run the helper in
# 32-bit PowerShell and read its JSON back.
if (-not $ExportMyVendors -and -not $is32bit) {
    $sys32 = Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $helper = Join-Path $here '_list-view.ps1'
    if ((Test-Path -LiteralPath $sys32) -and (Test-Path -LiteralPath $helper)) {
        $tmpOut = [System.IO.Path]::GetTempFileName() + '.json'
        & $sys32 -NoProfile -ExecutionPolicy Bypass -File "$helper" "$tmpOut" 2>$null
        if (Test-Path -LiteralPath $tmpOut) {
            try {
                $raw = Get-Content -LiteralPath $tmpOut -Raw -Encoding UTF8
                if ($raw -and $raw.Trim().Length -gt 2) {
                    # NOTE: on PowerShell 5.1, ConvertFrom-Json emits a JSON array as ONE
                    # object. Wrapping it in @() nests it instead of enumerating, so read
                    # it through a variable and iterate that.
                    $objs = ConvertFrom-Json -InputObject $raw
                    foreach ($o in $objs) {
                        $entries.Add([pscustomobject]@{
                                View     = '0x0100'
                                ViewName = '32'
                                Hive     = $o.Hive
                                Class    = $o.Class
                                Kind     = $o.Kind
                                Rel      = $o.Rel
                                RealName = $o.RealName
                                RawKey   = $o.RawKey
                                Name     = $o.Name
                                Shape    = $o.Shape
                                Target   = $o.Target
                                Windows  = (Test-IsWindowsTarget $o.Target)
                                Padded   = [bool]$o.Padded
                            })
                    }
                }
            } catch {
                Write-Host ('WARN: could not read the 32-bit view listing: ' + $_.Exception.Message) -ForegroundColor Yellow
            }
            Remove-Item -LiteralPath $tmpOut -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host 'WARN: 32-bit PowerShell or _list-view.ps1 not found; only the 64-bit view is listed.' -ForegroundColor Yellow
    }
}


# --- de-duplicate: same vendor entry often appears in several class keys -----
$grouped = New-Object System.Collections.Generic.List[object]
$seen = @{}
foreach ($e in $entries) {
    $k = ($e.Hive + '|' + $e.Rel + '|' + $e.RealName + '|' + $e.ViewName)
    if ($seen.ContainsKey($k)) { continue }
    $seen[$k] = $true
    $grouped.Add($e)
}

$sorted = $grouped | Sort-Object Windows, @{ Expression = { $_.Name } }

if ($ExportMyVendors) {
    $pats = New-Object System.Collections.Generic.List[string]
    foreach ($e in $sorted) { if (-not $e.Windows) { $pats.Add($e.RealName.Trim()) } }
    $obj = [pscustomobject]@{
        _comment = 'Generated by manage-context-menu.ps1. Review and trim before use.'
        vendors  = @([pscustomobject]@{ vendor = 'My picks'; patterns = @($pats | Select-Object -Unique) })
    }
    $obj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ExportMyVendors -Encoding UTF8
    Write-Host ('Wrote ' + ($pats | Select-Object -Unique).Count + ' pattern(s) to ' + $ExportMyVendors) -ForegroundColor Green
    return
}

# --- display ---------------------------------------------------------------
# --- group entries by the component that owns them ---------------------------
# One shell extension normally registers itself in several class keys at once, so a
# flat list repeats the same component many times. Grouping by the resolved DLL/exe
# makes the list look like what the user actually sees in the menu.
function Get-GroupKey($e) {
    if ($e.Target) {
        if ($e.Shape -eq 'verb') {
            # Two verbs can share one exe ("play" vs "add"), so the full command line
            # is what distinguishes them. Include the key name too.
            return ('verb:' + $e.Target.Trim() + '|' + $e.RealName.Trim())
        }
        return ('dll:' + $e.Target)
    }
    return ('name:' + $e.RealName.Trim())
}

function Get-GroupLabel($e) {
    if ($e.Target) {
        if ($e.Shape -eq 'verb') {
            $t = $e.Target.Trim()
            $exe = if ($t -match '^"([^"]+)"') { $Matches[1] } else { ($t -split '\s+')[0] }
            return ((Split-Path -Leaf $exe) + '  [' + $e.RealName.Trim() + ']')
        }
        $leaf = Split-Path -Leaf $e.Target
        if ($leaf) { return $leaf }
        return $e.Target
    }
    return ('[' + $e.RealName.Trim() + ']')
}

$groups = New-Object System.Collections.Generic.List[object]
$byKey = @{}
foreach ($e in $sorted) {
    $k = (Get-GroupKey $e)
    if (-not $byKey.ContainsKey($k)) {
        $g = [pscustomobject]@{
            Key     = $k
            Label   = (Get-GroupLabel $e)
            Windows = $e.Windows
            Entries = (New-Object System.Collections.Generic.List[object])
            Sample  = $e
        }
        $byKey[$k] = $g
        $groups.Add($g)
    }
    $byKey[$k].Entries.Add($e)
    # a group counts as Windows-built-in only if every member is
    if ($byKey[$k].Windows -and -not $e.Windows) { $byKey[$k].Windows = $false }
}

function Show-Row($e) {
    # $e.Class is a char for '*', so force a string before padding.
    $cls = [string]$e.Class
    $pad = ''
    if ($e.Padded) { $pad = '   [padded key name]' }
    $shape = if ($e.Shape -eq 'verb') { 'static verb' } else { 'COM handler ' }
    Write-Host ('        ' + $cls.PadRight(24) + $shape + '  ' + $e.ViewName + '-bit ' + $e.Hive + $pad) -ForegroundColor DarkGray
}

$index = 0
$indexToGroup = @{}

Write-Host ''
Write-Host '=== Windows built-ins (NOT selected; only remove these if you know why) ===' -ForegroundColor DarkGray
foreach ($g in $groups) {
    if (-not $g.Windows) { continue }
    $index++
    $indexToGroup[$index] = $g
    Write-Host ('  [' + $index.ToString().PadLeft(3) + '] ' + $g.Label + '   (' + $g.Entries.Count + ' registration(s))') -ForegroundColor DarkGray
    foreach ($e in $g.Entries) { Show-Row $e }
}

Write-Host ''
Write-Host '=== Third-party add-ons ===' -ForegroundColor Yellow
$thirdCount = 0
foreach ($g in $groups) {
    if ($g.Windows) { continue }
    $thirdCount++
    $index++
    $indexToGroup[$index] = $g
    Write-Host ('  [' + $index.ToString().PadLeft(3) + '] ' + $g.Label + '   (' + $g.Entries.Count + ' registration(s))') -ForegroundColor White
    if ($g.Sample.Target -and $g.Sample.Shape -eq 'verb') {
        Write-Host ('        runs: ' + $g.Sample.Target) -ForegroundColor DarkGray
    } elseif ($g.Sample.Target) {
        Write-Host ('        from: ' + (Split-Path -Parent $g.Sample.Target)) -ForegroundColor DarkGray
    }
    foreach ($e in $g.Entries) { Show-Row $e }
}

if ($index -eq 0) {
    Write-Host ''
    Write-Host 'No context-menu entries found.' -ForegroundColor Yellow
    return
}

if ($ListOnly) {
    Write-Host ''
    Write-Host ('Listed ' + $groups.Count + ' component(s) in ' + $index + ' slot(s); ' + $thirdCount + ' third-party.') -ForegroundColor Cyan
    Write-Host 'Windows built-ins are numbered too but not printed; select a number to remove a component.' -ForegroundColor Cyan
    return
}

# --- choose ----------------------------------------------------------------
$picked = New-Object System.Collections.Generic.List[object]

if ($Grid) {
    $choice = $groups | Select-Object @{ n = 'Pick'; e = { $false } }, Label, Windows, @{ n = 'Registrations'; e = { $_.Entries.Count } }, Key |
        Out-GridView -Title 'Tick the components to DELETE, then click OK' -PassThru
    if ($choice) {
        foreach ($c in @($choice)) {
            $g = $byKey[$c.Key]
            if ($g) { foreach ($e in $g.Entries) { $picked.Add($e) } }
        }
    }
} elseif ($Select -and $Select.Count -gt 0) {
    foreach ($n in $Select) {
        if ($indexToGroup.ContainsKey($n)) {
            foreach ($e in $indexToGroup[$n].Entries) { $picked.Add($e) }
        } else {
            Write-Host ('Ignoring out-of-range selection: ' + $n) -ForegroundColor Yellow
        }
    }
} elseif ($SelectWin) {
    foreach ($g in $groups) { if ($g.Windows) { foreach ($e in $g.Entries) { $picked.Add($e) } } }
} else {
    Write-Host ''
    Write-Host 'Enter the numbers of the components to DELETE, separated by spaces or commas.' -ForegroundColor Cyan
    Write-Host '  e.g.   1 4 7   or   2,3        (empty = cancel)' -ForegroundColor DarkGray
    $answer = Read-Host 'Selection'
    if ([string]::IsNullOrWhiteSpace($answer)) { Write-Host 'Cancelled.' -ForegroundColor Yellow; return }
    foreach ($tok in ($answer -split '[,;\s]+')) {
        $n = 0
        if ([int]::TryParse($tok, [ref]$n)) {
            if ($indexToGroup.ContainsKey($n)) {
                foreach ($e in $indexToGroup[$n].Entries) { $picked.Add($e) }
            } else {
                Write-Host ('Ignoring out-of-range selection: ' + $tok) -ForegroundColor Yellow
            }
        } elseif ($tok -ne '') {
            Write-Host ('Ignoring unparsable selection: ' + $tok) -ForegroundColor Yellow
        }
    }
}

if ($picked.Count -eq 0) { Write-Host 'Nothing selected.' -ForegroundColor Yellow; return }

if (-not $isAdmin) {
    Write-Host ''
    Write-Host 'ERROR: deleting needs an elevated PowerShell.' -ForegroundColor Red
    Write-Host ('       You selected ' + $picked.Count + ' entr(ies); re-run elevated with:') -ForegroundColor Yellow
    Write-Host ('       -Select ' + (($picked | ForEach-Object { ($sorted.IndexOf($_)) + 1 }) -join ',')) -ForegroundColor Yellow
    return
}

if (-not $BackupDir) {
    $BackupDir = Join-Path (Get-Location) ('context-menu-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

# --- delete ----------------------------------------------------------------
Write-Host ''
$ok = 0
$bad = 0
foreach ($e in $picked) {
    Write-Host ('Deleting [' + $e.Name + ']  ' + $e.Hive + '\' + $e.Rel + '\' + $e.RealName) -ForegroundColor White

    if (-not $NoBackup) {
        if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
        $safe = (($e.Hive + '_' + $e.Class + '_' + $e.Kind + '_' + $e.RealName) -replace '[^A-Za-z0-9._-]', '_')
        $file = Join-Path $BackupDir ($safe + '.reg')
        $rv = if ($e.ViewName -eq '32') { '/reg:32' } else { '/reg:64' }
        & reg.exe export "$($e.RawKey)" "$file" /y $rv 2>&1 | Out-Null
        if (Test-Path -LiteralPath $file) { Write-Host ('        backup -> ' + $file) -ForegroundColor DarkGray }
    }

    $rv = if ($e.ViewName -eq '32') { '/reg:32' } else { '/reg:64' }
    $out = & reg.exe delete "$($e.RawKey)" /f $rv 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host '        -> DELETED' -ForegroundColor Green
        $ok++
    } else {
        Write-Host ('        -> FAILED: ' + ($out -join ' ')) -ForegroundColor Red
        $bad++
    }
}

Write-Host ''
Write-Host ('Deleted ' + $ok + ', failed ' + $bad + '. Backups in ' + $BackupDir) -ForegroundColor Cyan

if (-not $NoRestart) {
    Write-Host 'Restarting Explorer...' -ForegroundColor Cyan
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    Start-Sleep -Seconds 2
    if (Get-Process explorer -ErrorAction SilentlyContinue) { Write-Host 'Explorer is running.' -ForegroundColor Green }
    else { Write-Host 'WARN: starting Explorer again.' -ForegroundColor Yellow; Start-Process explorer.exe }
}
