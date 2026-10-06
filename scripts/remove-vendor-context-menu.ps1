# Remove Windows Explorer context-menu entries added by third-party vendors.
#
#   .\remove-vendor-context-menu.ps1                 # scan + remove (needs admin)
#   .\remove-vendor-context-menu.ps1 -DryRun         # scan only, delete nothing
#   .\remove-vendor-context-menu.ps1 -Pattern MyApp  # extra key-name substrings
#   .\remove-vendor-context-menu.ps1 -VendorsFile .\scripts\vendors.json
#
# Verified on Windows 10 22H2 (19045) with Windows PowerShell 5.1.
#
# WHY THIS SCRIPT IS WRITTEN THE WAY IT IS  (every point below cost real debugging time)
#   1. Vendors PAD the registry key name with LEADING SPACES
#      (e.g. "    sgshellext"). An exact-name lookup therefore finds nothing, and
#      the removal silently "succeeds" while deleting zero bytes.
#      -> Always ENUMERATE the container and match substrings.
#   2. Registry.OpenBaseKey does NOT exist in Windows PowerShell 5.1
#      (.NET Framework). Using it throws MethodNotFound on the very first line.
#      -> Use the PowerShell provider for reading and reg.exe for deleting.
#   3. The 32-bit and 64-bit registry views are separate stores. A given PowerShell
#      process only sees the view matching its own bitness, and reg.exe writes only
#      to the view named by /reg:32 or /reg:64.
#      -> Run once as 64-bit, then re-launch as 32-bit (this script does it).
#   4. "reg delete" needs the key path quoted when it contains spaces; PowerShell
#      passes a single quoted argument through correctly.
#   5. HKLM keys require an elevated process. Without elevation every deletion fails
#      with "Access is denied" - report that, never hide it.
#   6. Killing explorer.exe removes the desktop and taskbar until it is started
#      again. Always make sure it comes back.

[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$NoPause,
    [string[]]$Pattern,
    [string]$VendorsFile,
    [string]$BackupDir
)

$ErrorActionPreference = 'Continue'

$star = [char]42
$bs = [char]92

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$is32bit = [bool]$env:PROCESSOR_ARCHITEW6432
$regView = if ($is32bit) { '/reg:32' } else { '/reg:64' }

Write-Host ''
Write-Host '=================================================='
Write-Host (' Bitness : ' + $(if ($is32bit) { '32-bit (WOW64)' } else { '64-bit' }) + '   Elevated: ' + $isAdmin + '   DryRun: ' + [bool]$DryRun)
Write-Host '=================================================='

if (-not $isAdmin -and -not $DryRun) {
    Write-Host 'ERROR: run this script from an ELEVATED PowerShell.' -ForegroundColor Red
    Write-Host '       HKLM context-menu keys cannot be removed without administrator rights.' -ForegroundColor Red
    Write-Host '       Use -DryRun to scan without administrator rights.' -ForegroundColor Yellow
    if (-not $NoPause) { Read-Host 'Press Enter to exit' }
    exit 1
}

# --- vendor detection patterns ----------------------------------------------
# ASCII source on purpose: CJK keywords are stored as Unicode code points (or read
# from vendors.json, where they are \uXXXX escapes) so this file survives any
# code-page or transport mangling.
function U([int[]]$cp) { -join ($cp | ForEach-Object { [char]$_ }) }

# Windows PowerShell 5.1's ConvertFrom-Json does NOT decode \uXXXX escapes - it
# leaves them as literal text. Decode them ourselves so vendors.json can carry CJK
# keywords safely. The pattern needs \\u because inside a regex \u must be
# followed by exactly four hex digits.
function ConvertFrom-UnicodeEscapes([string]$s) {
    if ($null -eq $s) { return $s }
    $rx = [regex]('\\u([0-9a-fA-F]{4})')
    $ev = [System.Text.RegularExpressions.MatchEvaluator] { param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16) }
    return $rx.Replace($s, $ev)
}

$patternList = New-Object System.Collections.Generic.List[string]
foreach ($p in @(
        'sgshellext'
        'biz_shell'
        'YunShellExt'
        'YunShellExplorerCommand'
        'YunShellCommand'
        'UploadToThunderPan'
        'BaiduNetdisk'
        (U @(0x641C, 0x72D7))
        (U @(0x8BAF, 0x96F7))
        (U @(0x767E, 0x5EA6))
        (U @(0x7F51, 0x76D8))
    )) { $patternList.Add($p) }

if (-not $VendorsFile) {
    $cand = Join-Path (Split-Path -Parent $PSCommandPath) 'vendors.json'
    if (Test-Path -LiteralPath $cand) { $VendorsFile = $cand }
}

if ($VendorsFile -and (Test-Path -LiteralPath $VendorsFile)) {
    try {
        $cfg = Get-Content -LiteralPath $VendorsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $n = 0
        foreach ($v in $cfg.vendors) {
            if ($v.skip) { continue }
            foreach ($p in $v.patterns) { $patternList.Add((ConvertFrom-UnicodeEscapes ([string]$p))); $n++ }
        }
        Write-Host ('Loaded ' + $n + ' patterns from ' + $VendorsFile)
    } catch {
        Write-Host ('WARN: could not read ' + $VendorsFile + ': ' + $_.Exception.Message) -ForegroundColor Yellow
    }
}

foreach ($p in $Pattern) { if ($p) { $patternList.Add($p) } }
$patternList = @($patternList | Where-Object { $_ } | Select-Object -Unique)

# --- registration locations that can produce a context-menu item -------------
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

$roots = @(
    @{ Hive = 'HKCU'; PS = ('HKCU:\SOFTWARE\Classes' + $bs); Raw = ('HKCU\SOFTWARE\Classes' + $bs) },
    @{ Hive = 'HKLM'; PS = ('HKLM:\SOFTWARE\Classes' + $bs); Raw = ('HKLM\SOFTWARE\Classes' + $bs) }
)

if (-not $BackupDir) { $BackupDir = Join-Path $PWD ('context-menu-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$backupCount = 0

function Test-Offender([string]$name, $childKey) {
    $hay = $name
    try {
        $def = $childKey.GetValue($null)
        if ($def) { $hay = $hay + ' ' + [string]$def }
    } catch {}
    foreach ($n in $patternList) {
        if ($hay.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

function Save-RegKey([string]$rawKey, [string]$label) {
    if ($DryRun) { return }
    if (-not (Test-Path -LiteralPath $BackupDir)) {
        try { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null } catch {
            Write-Host ('       (backup skipped: ' + $_.Exception.Message + ')') -ForegroundColor Yellow
            return
        }
    }
    $safe = ($label -replace '[^A-Za-z0-9._-]', '_')
    $file = Join-Path $BackupDir ($safe + '.reg')
    # reg.exe writes UTF-16LE, which is exactly what "reg import" expects.
    & reg.exe export "$rawKey" "$file" /y $regView 2>&1 | Out-Null
    if (Test-Path -LiteralPath $file) {
        $script:backupCount++
        Write-Host ('       backup -> ' + $file) -ForegroundColor DarkGray
    }
}

$removed = 0
$failed = 0
$matched = 0

foreach ($root in $roots) {
    foreach ($cls in $classes) {
        foreach ($kind in $kinds) {
            $rel = $cls + $bs + $kind
            $psContainer = $root.PS + $rel
            if (-not (Test-Path -LiteralPath $psContainer)) { continue }

            $children = @()
            try { $children = @(Get-ChildItem -LiteralPath $psContainer -ErrorAction Stop) } catch { continue }

            foreach ($child in $children) {
                $real = $child.PSChildName
                if (-not (Test-Offender $real $child)) { continue }
                $matched++

                $codes = ($real.ToCharArray() | ForEach-Object { ([int]$_).ToString('X4') }) -join ' '
                $padded = ''
                if ($real -ne $real.Trim()) { $padded = '   <== PADDED NAME' }

                Write-Host ''
                Write-Host ('MATCH  ' + $root.Hive + ' ' + $regView + ' ' + $rel) -ForegroundColor Yellow
                Write-Host ('       key name : [' + $real + ']  length=' + $real.Length + $padded)
                Write-Host ('       charcodes: ' + $codes)

                if ($DryRun) { continue }

                $rawKey = $root.Raw + $rel + $bs + $real
                Save-RegKey $rawKey ($root.Hive + '_' + $cls + '_' + $kind + '_' + $real)

                $out = & reg.exe delete "$rawKey" /f $regView 2>&1
                $code = $LASTEXITCODE
                if ($code -eq 0 -and -not (Test-Path -LiteralPath ($psContainer + $bs + $real))) {
                    Write-Host '       -> DELETED' -ForegroundColor Green
                    $removed++
                } else {
                    Write-Host ('       -> FAILED (exit ' + $code + '): ' + ($out -join ' ')) -ForegroundColor Red
                    $failed++
                }
            }
        }
    }
}

$viewLabel = if ($is32bit) { '32-bit view' } else { '64-bit view' }
Write-Host ''
if ($DryRun) {
    Write-Host ('=== ' + $viewLabel + ': ' + $matched + ' match(es), nothing deleted (dry run) ===') -ForegroundColor Cyan
} else {
    Write-Host ('=== ' + $viewLabel + ': matched ' + $matched + ', deleted ' + $removed + ', failed ' + $failed + ' ===') -ForegroundColor Cyan
}

# --- cover the other registry view -----------------------------------------
if (-not $is32bit) {
    $sys32 = Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $sys32) {
        Write-Host ''
        Write-Host '=== Switching to the 32-bit view (re-launching in 32-bit PowerShell) ===' -ForegroundColor Cyan
        $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$PSCommandPath", '-NoPause')
        if ($DryRun) { $childArgs += '-DryRun' }
        if ($VendorsFile) { $childArgs += @('-VendorsFile', $VendorsFile) }
        if ($BackupDir) { $childArgs += @('-BackupDir', $BackupDir) }
        foreach ($p in $Pattern) { $childArgs += @('-Pattern', $p) }
        & $sys32 @childArgs
    } else {
        Write-Host 'WARN: 32-bit PowerShell not found; the 32-bit view was not cleaned.' -ForegroundColor Yellow
    }
}

# --- final re-check + explorer restart -------------------------------------
if (-not $is32bit) {
    Write-Host ''
    Write-Host '=== Final re-check (this view) ===' -ForegroundColor Cyan
    $left = 0
    foreach ($root in $roots) {
        foreach ($cls in $classes) {
            foreach ($kind in $kinds) {
                $rel = $cls + $bs + $kind
                $psContainer = $root.PS + $rel
                if (-not (Test-Path -LiteralPath $psContainer)) { continue }
                foreach ($child in @(Get-ChildItem -LiteralPath $psContainer -ErrorAction SilentlyContinue)) {
                    if (Test-Offender $child.PSChildName $child) {
                        Write-Host ('  REMAINS ' + $root.Hive + ' ' + $rel + $bs + '[' + $child.PSChildName + ']') -ForegroundColor Red
                        $left++
                    }
                }
            }
        }
    }
    if ($left -eq 0) { Write-Host ('CLEAN: nothing matched in the ' + $viewLabel + '.') -ForegroundColor Green }

    if (-not $DryRun) {
        Write-Host ''
        Write-Host ('Backups: ' + $backupCount + ' key(s) exported to ' + $BackupDir) -ForegroundColor DarkGray

        Write-Host ''
        Write-Host '=== Restarting Explorer ===' -ForegroundColor Cyan
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
        Start-Sleep -Seconds 2
        $e = Get-Process explorer -ErrorAction SilentlyContinue
        if ($e) { Write-Host ('Explorer running, pid=' + $e.Id) -ForegroundColor Green }
        else {
            Write-Host 'WARN: Explorer is not running; starting it again.' -ForegroundColor Yellow
            Start-Process explorer.exe
        }
    }

    Write-Host ''
    Write-Host 'NOTE: the vendor may re-register these keys the next time it runs or updates.'
    Write-Host '      See the README section "Making removal stick".'
    if (-not $NoPause) { Read-Host 'Press Enter to exit' }
}
