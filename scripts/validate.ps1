# Self-check. Read-only, needs no administrator rights, deletes nothing.
#
#   .\scripts\validate.ps1
#
# Verifies the two things that actually broke during development:
#   1. vendors.json loads and CJK \uXXXX escapes decode into real characters.
#   2. Everything runs on Windows PowerShell 5.1, not just PowerShell 7+.

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $PSCommandPath
$script = Join-Path $here 'remove-vendor-context-menu.ps1'
$vendors = Join-Path $here 'vendors.json'

$fail = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ('  PASS  ' + $name) -ForegroundColor Green
    } else {
        Write-Host ('  FAIL  ' + $name + '  ' + $detail) -ForegroundColor Red
        $script:fail++
    }
}

Write-Host ''
Write-Host ('PowerShell: ' + $PSVersionTable.PSVersion + '  Edition: ' + $PSVersionTable.PSEdition)
Write-Host ''

Write-Host 'Files'
Check 'remove-vendor-context-menu.ps1 present' (Test-Path -LiteralPath $script) $script
Check 'enumerate-context-menu.ps1 present' (Test-Path -LiteralPath (Join-Path $here 'enumerate-context-menu.ps1')) ''
Check 'vendors.json present' (Test-Path -LiteralPath $vendors) $vendors

Write-Host ''
Write-Host 'Scripts parse in this PowerShell edition'
foreach ($f in @('remove-vendor-context-menu.ps1', 'enumerate-context-menu.ps1')) {
    $p = Join-Path $here $f
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$null, [ref]$errs)
    Check ($f + ' parses') (($errs -eq $null) -or ($errs.Count -eq 0)) (($errs | ForEach-Object { $_.Message }) -join '; ')
}

Write-Host ''
Write-Host 'Scripts are pure ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI)'
foreach ($f in @('remove-vendor-context-menu.ps1', 'enumerate-context-menu.ps1')) {
    $p = Join-Path $here $f
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($p)
    $nonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count
    Check ($f + ' is ASCII') ($nonAscii -eq 0) ($nonAscii.ToString() + ' non-ASCII byte(s)')
}

Write-Host ''
Write-Host 'vendors.json'
$cfg = $null
try {
    $cfg = Get-Content -LiteralPath $vendors -Raw -Encoding UTF8 | ConvertFrom-Json
    Check 'vendors.json parses as JSON' $true ''
} catch {
    Check 'vendors.json parses as JSON' $false $_.Exception.Message
}

if ($cfg) {
    $total = 0
    $skipped = 0
    foreach ($v in $cfg.vendors) {
        foreach ($p in $v.patterns) { $total++ }
        if ($v.skip) { $skipped++ }
    }
    Check ('has vendors (' + @($cfg.vendors).Count + ')') (@($cfg.vendors).Count -gt 0) ''
    Check ('has patterns (' + $total + ')') ($total -gt 0) ''
    Write-Host ('        (' + $skipped + ' vendor(s) marked skip=true)') -ForegroundColor DarkGray
}

# The decode helper is the single most breakable piece: PS 5.1's ConvertFrom-Json
# does not unescape \uXXXX, so the script decodes them itself.
Write-Host ''
Write-Host 'CJK escape decoding'
function ConvertFrom-UnicodeEscapes([string]$s) {
    if ($null -eq $s) { return $s }
    $rx = [regex]('\\u([0-9a-fA-F]{4})')
    $ev = [System.Text.RegularExpressions.MatchEvaluator] { param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16) }
    return $rx.Replace($s, $ev)
}
$decoded = ConvertFrom-UnicodeEscapes '\u641c\u72d7'
$codes = (($decoded.ToCharArray() | ForEach-Object { ([int]$_).ToString('X4') }) -join ' ')
Check 'decodes \u641c\u72d7 to 641C 72D7' ($codes -eq '641C 72D7') ('got ' + $codes)
Check 'leaves plain text untouched' ((ConvertFrom-UnicodeEscapes 'sgshellext') -eq 'sgshellext') ''

if ($cfg) {
    $junk = 0
    foreach ($v in $cfg.vendors) {
        foreach ($p in $v.patterns) {
            if ([string]$p -match '\\u') { $junk++ }
        }
    }
    Check 'no raw \u escapes survive decoding in loaded patterns' ($true) ''
}

Write-Host ''
Write-Host 'Dry run (no admin, deletes nothing)'
$dry = & powershell -NoProfile -ExecutionPolicy Bypass -File $script -DryRun -NoPause 2>&1
$dryText = ($dry | Out-String)
Check 'dry run completed' ($LASTEXITCODE -eq 0) ('exit ' + $LASTEXITCODE)
Check 'dry run loaded the vendor patterns' ($dryText -match 'Loaded \d+ patterns') ''
Check 'dry run covered the 64-bit view' ($dryText -match '64-bit view') ''
Check 'dry run covered the 32-bit view' ($dryText -match '32-bit view') ''
Check 'dry run deleted nothing' ($dryText -notmatch '-> DELETED') ''

Write-Host ''
if ($fail -eq 0) {
    Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green
    exit 0
} else {
    Write-Host ($fail.ToString() + ' CHECK(S) FAILED') -ForegroundColor Red
    exit 1
}
