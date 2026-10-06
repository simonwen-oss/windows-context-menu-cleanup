# Build CtxMenuCleaner.exe.
#
#   .\build.ps1              # build only
#   .\build.ps1 -Test        # build, then run --list and --dry-run as a smoke test
#
# Uses the C# compiler that ships with Windows (.NET Framework 4.x). No SDK, no
# NuGet, no internet access and no PowerShell version requirement to build.
#
# NOTE ON THE BUILD DIRECTORY
# Two path problems were observed on a machine whose checkout lived under a
# Chinese-named OneDrive folder (C:\Users\<user>\OneDrive\<CJK>\...):
#
#   1. csc.exe creates a Win32 resource temp file next to its output. On a non-ASCII
#      path it can fail with:
#          error CS1567: Error generating Win32 resource: The system cannot find the path
#          warning CS1610: unable to delete the temporary file used for default Win32 resource
#      So compilation is staged in an ASCII-only temp directory.
#
#   2. A binary LAUNCHED from that folder could not write to HKCU at all
#      (UnauthorizedAccessException / SecurityException 0x8013150A), while the very same
#      binary copied to an ASCII path wrote to HKCU fine. Relocating the build output
#      away from the OneDrive/ non-ASCII tree therefore matters at RUNTIME, not just at
#      build time.
#
# Default output is %LOCALAPPDATA%\CtxMenuCleaner\CtxMenuCleaner.exe (pure ASCII).
# Override with -OutputDir, but avoid non-ASCII and OneDrive-synced locations.

[CmdletBinding()]
param(
    [switch]$Test,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSCommandPath
$src = Join-Path $here 'CtxMenuCleaner.cs'

if (-not $OutputDir) {
    $OutputDir = Join-Path $env:LOCALAPPDATA 'CtxMenuCleaner'
}
$outDir = $OutputDir
$exe = Join-Path $outDir 'CtxMenuCleaner.exe'

if (-not (Test-Path -LiteralPath $src)) { throw "source not found: $src" }

# Prefer the 64-bit compiler so the produced exe is 64-bit and can read BOTH
# registry views through RegistryKey.OpenBaseKey.
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe not found (.NET Framework 4.x is required)' }

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# A running copy of the tool holds a lock on its own executable, which turns the final
# copy into an obscure "being used by another process" IOException. Detect it up front.
$running = @(Get-Process -Name 'CtxMenuCleaner' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and (Test-Path -LiteralPath $exe)) {
    Write-Host ''
    Write-Host ('ERROR: ' + $running.Count + ' CtxMenuCleaner process(es) are still running (pid ' +
                (($running | ForEach-Object { $_.Id }) -join ', ') + ').') -ForegroundColor Red
    Write-Host '       A running copy locks the executable, so it cannot be replaced.' -ForegroundColor Red
    Write-Host '       Close its window (or press Enter in it), then build again.' -ForegroundColor Red
    Write-Host '       To kill it:  Stop-Process -Name CtxMenuCleaner -Force' -ForegroundColor Yellow
    throw 'target executable is locked by a running instance'
}

if ($outDir -match '[^\x00-\x7F]') {
    Write-Host 'WARNING: the output directory contains non-ASCII characters.' -ForegroundColor Yellow
    Write-Host '         The executable may be refused registry write access at runtime.' -ForegroundColor Yellow
    Write-Host '         Prefer -OutputDir with a pure ASCII path.' -ForegroundColor Yellow
    Write-Host ''
}

# ASCII-only staging directory for the compiler.
$stage = Join-Path $env:TEMP ('ctxmenu-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$stagedSrc = Join-Path $stage 'CtxMenuCleaner.cs'
$stagedExe = Join-Path $stage 'CtxMenuCleaner.exe'
Copy-Item -LiteralPath $src -Destination $stagedSrc -Force

Write-Host "Compiler : $csc"
Write-Host "Source   : $src"
Write-Host "Staging  : $stage"
Write-Host "Output   : $exe"
Write-Host ''


$refs = @(
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Drawing.dll'
)

$cscArgs = @(
    '/nologo'
    '/target:exe'
    '/platform:anycpu'
    '/optimize+'
    '/warnaserror-'
    "/out:$stagedExe"
) + $refs + @($stagedSrc)

try {
    & $csc @cscArgs
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "compile failed with exit code $code" }
    if (-not (Test-Path -LiteralPath $stagedExe)) { throw 'compile reported success but no exe was produced' }

    # Windows lets a running executable be RENAMED even though it cannot be overwritten or
    # deleted. So if the target is locked, move it aside and write the new build in its
    # place instead of failing after a successful compile.
    if (Test-Path -LiteralPath $exe) {
        try {
            Copy-Item -LiteralPath $stagedExe -Destination $exe -Force -ErrorAction Stop
        } catch {
            $staleName = 'CtxMenuCleaner.old-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.exe'
            $stalePath = Join-Path $outDir $staleName
            try {
                Move-Item -LiteralPath $exe -Destination $stalePath -Force -ErrorAction Stop
                Copy-Item -LiteralPath $stagedExe -Destination $exe -Force -ErrorAction Stop
                Write-Host ''
                Write-Host 'NOTE: a running instance was holding the old executable.' -ForegroundColor Yellow
                Write-Host ('      It was moved aside to ' + $staleName) -ForegroundColor Yellow
                Write-Host '      The still-running process keeps the OLD code until it is closed.' -ForegroundColor Yellow
            } catch {
                Write-Host ''
                Write-Host ('ERROR: cannot replace ' + $exe) -ForegroundColor Red
                Write-Host ('       the old file was also not movable: ' + $_.Exception.Message) -ForegroundColor Red
                throw 'target executable is locked and could not be moved aside'
            }
        }
    } else {
        Copy-Item -LiteralPath $stagedExe -Destination $exe -Force
    }
    $built = $true
} finally {
    # Keep the staging directory when the build failed, so the compiler output can be
    # inspected instead of being thrown away.
    if ($built) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host ('Staging kept for inspection: ' + $stage) -ForegroundColor Yellow }
}

if (-not (Test-Path -LiteralPath $exe)) { throw "expected output missing: $exe" }
$size = (Get-Item -LiteralPath $exe).Length
Write-Host ''
Write-Host ("Built " + $exe + "  (" + $size + " bytes)") -ForegroundColor Green

if ($Test) {
    Write-Host ''
    Write-Host '=== smoke test: --version ===' -ForegroundColor Cyan
    & $exe --version
    Write-Host ''
    Write-Host '=== smoke test: --list (third-party only) ===' -ForegroundColor Cyan
    & $exe --list
    Write-Host ''
    Write-Host '=== smoke test: --dry-run --select 1 ===' -ForegroundColor Cyan
    & $exe --dry-run --select 1 --yes
    Write-Host ''
    Write-Host '=== smoke test: --help ===' -ForegroundColor Cyan
    & $exe --help | Select-Object -First 6
}
