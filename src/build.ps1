# Build CtxMenuCleaner.exe.
#
#   .\build.ps1              # build only
#   .\build.ps1 -Test        # build, then run --list and --dry-run as a smoke test
#
# Uses the C# compiler that ships with Windows (.NET Framework 4.x). No SDK, no
# NuGet, no internet access and no PowerShell version requirement to build.

[CmdletBinding()]
param(
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSCommandPath
$src = Join-Path $here 'CtxMenuCleaner.cs'
$outDir = Join-Path $here 'dist'
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

Write-Host "Compiler : $csc"
Write-Host "Source   : $src"
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
    "/out:$exe"
) + $refs + @($src)

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $exe)) { throw 'compile reported success but no exe was produced' }

$size = (Get-Item -LiteralPath $exe).Length
Write-Host ''
Write-Host ("Built " + $exe + "  (" + $size + " bytes)") -ForegroundColor Green

if ($Test) {
    Write-Host ''
    Write-Host '=== smoke test: --list ===' -ForegroundColor Cyan
    & $exe --list
    Write-Host ''
    Write-Host '=== smoke test: --dry-run --select 1 ===' -ForegroundColor Cyan
    & $exe --dry-run --select 1 --yes
    Write-Host ''
    Write-Host '=== smoke test: --help ===' -ForegroundColor Cyan
    & $exe --help | Select-Object -First 5
}
