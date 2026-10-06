# Enumerate every Explorer context-menu registration and reveal REAL key names.
#
# Read-only. Safe to run without administrator rights.
# Use this first when a right-click item must be identified: it prints the true
# key name (including any leading-space padding) and the char codes, plus the
# COM server DLL behind each shellex handler.

$ErrorActionPreference = 'Continue'

$sp = [char]32
$star = [char]42
$bs = [char]92
$is32bit = [bool]$env:PROCESSOR_ARCHITEW6432

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
    ('shellex' + $bs + 'ContextMenuHandlers'),
    ('shellex' + $bs + 'PropertySheetHandlers'),
    ('shellex' + $bs + 'DragDropHandlers')
)

function Show-Codes([string]$s) {
    return (($s.ToCharArray() | ForEach-Object { ([int]$_).ToString('X4') }) -join ' ')
}

function Resolve-Clsid([string]$clsid, [string]$hive) {
    if ([string]::IsNullOrWhiteSpace($clsid)) { return '' }
    if ($clsid -notmatch '^\{') { return '' }
    $p = $hive + ':\SOFTWARE\Classes\CLSID\' + $clsid + '\InProcServer32'
    if (Test-Path -LiteralPath $p) {
        try { return [string](Get-ItemProperty -LiteralPath $p).'(default)' } catch { return '' }
    }
    return ''
}

Write-Host ''
Write-Host ('=== Bitness: ' + $(if ($is32bit) { '32-bit (WOW64)' } else { '64-bit' }) + ' ===') -ForegroundColor Cyan

foreach ($hive in @('HKLM', 'HKCU')) {
    foreach ($cls in $classes) {
        foreach ($kind in $kinds) {
            $container = $hive + ':\SOFTWARE\Classes' + $bs + $cls + $bs + $kind
            if (-not (Test-Path -LiteralPath $container)) { continue }

            $children = @()
            try { $children = @(Get-ChildItem -LiteralPath $container -ErrorAction Stop) } catch { continue }
            if ($children.Count -eq 0) { continue }

            Write-Host ''
            Write-Host ('### ' + $hive + ' ' + $cls + $bs + $kind) -ForegroundColor White

            foreach ($child in $children) {
                $name = $child.PSChildName
                $vals = @()
                foreach ($vn in $child.GetValueNames()) {
                    $vals += ($vn + '=' + [string]$child.GetValue($vn))
                }
                $valText = ($vals -join '; ')

                $flag = ''
                if ($name -ne $name.Trim()) { $flag = '   <== PADDED (leading/trailing spaces!)' }

                Write-Host ('  [' + $name + ']' + $flag)
                if ($flag -ne '') { Write-Host ('        charcodes: ' + (Show-Codes $name)) }
                if ($valText -ne '') { Write-Host ('        values: ' + $valText) }

                # Resolve the COM server behind a shellex handler
                if ($kind -like '*ContextMenuHandlers*' -or $kind -like '*Handlers*') {
                    $clsid = $null
                    try { $clsid = [string]$child.GetValue($null) } catch {}
                    $dll = Resolve-Clsid $clsid $hive
                    if ($dll -ne '') { Write-Host ('        dll: ' + $dll) }
                } else {
                    $cmd = $null
                    try { $cmd = [string]$child.GetValue($null) } catch {}
                    $sub = $child.OpenSubKey('command')
                    if ($sub -ne $null) {
                        try { $cmd = [string]$sub.GetValue($null) } catch {}
                        $sub.Close()
                    }
                    if ($cmd) { Write-Host ('        command: ' + $cmd) }
                }
            }
        }
    }
}

Write-Host ''
Write-Host 'Done. Entries marked PADDED use the anti-removal trick: the real registry'
Write-Host 'key name differs from the visible extension name, so an exact-name delete'
Write-Host 'silently removes nothing. Delete them by real name (see the skill body).'
