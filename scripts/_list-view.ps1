# Internal helper. Enumerates the context-menu entries of the CURRENT process's
# registry view and writes them as JSON (UTF-8, no BOM) to the path given as $1.
# Called by manage-context-menu.ps1 through 32-bit PowerShell so that the 32-bit
# registry view is covered too. Not meant to be run directly.
param([Parameter(Mandatory = $true)][string]$OutputPath)

$star = [char]42
$bs = [char]92

$classes = @($star, 'AllFilesystemObjects', 'Directory', ('Directory' + $bs + 'Background'), 'Folder', 'Drive', 'DesktopBackground', 'lnkfile', 'exefile')
$kinds = @('shell', ('shellex' + $bs + 'ContextMenuHandlers'))

$acc = @()

function Add-Entry($hive, $cls, $kind, $rel, $psPrefix, $rawPrefix, $child) {
    $real = $child.PSChildName
    $def = ''
    try { $d = $child.GetValue($null); if ($d) { $def = [string]$d } } catch {}

    $target = ''
    $shape = 'COM'
    if ($kind -eq 'shell') {
        $shape = 'verb'
        $c = $child.OpenSubKey('command')
        if ($c -ne $null) {
            try { $target = [string]$c.GetValue($null) } catch {}
            $c.Close()
        }
    } else {
        if ($def -match '^\{') {
            $p = $psPrefix + 'CLSID' + $bs + $def + $bs + 'InProcServer32'
            if (Test-Path -LiteralPath $p) {
                try { $target = [string](Get-ItemProperty -LiteralPath $p).'(default)' } catch {}
            }
        }
        if (-not $target) { $target = $def }
    }

    $name = $def
    if (-not $name) { $name = '[' + $real.Trim() + ']' }

    $script:acc += [pscustomobject]@{
        Hive     = $hive
        Class    = $cls
        Kind     = $kind
        Rel      = $rel
        RealName = $real
        RawKey   = $rawPrefix + $rel + $bs + $real
        Name     = $name
        Shape    = $shape
        Target   = $target
        Padded   = ($real -ne $real.Trim())
    }
}

foreach ($root in @(
        @{ H = 'HKCU'; PS = ('HKCU:\SOFTWARE\Classes' + $bs); R = ('HKCU\SOFTWARE\Classes' + $bs) },
        @{ H = 'HKLM'; PS = ('HKLM:\SOFTWARE\Classes' + $bs); R = ('HKLM\SOFTWARE\Classes' + $bs) }
    )) {
    foreach ($cls in $classes) {
        foreach ($kind in $kinds) {
            $rel = $cls + $bs + $kind
            $cont = $root.PS + $rel
            if (-not (Test-Path -LiteralPath $cont)) { continue }
            foreach ($ch in @(Get-ChildItem -LiteralPath $cont -ErrorAction SilentlyContinue)) {
                Add-Entry $root.H $cls $kind $rel $root.PS $root.R $ch
            }
        }
    }
}

# ConvertTo-Json with -InputObject expands an array into separate arguments on
# PowerShell 5.1 (producing ONE object with every property concatenated). Cast to
# [object[]] so the whole array is serialised as a JSON array.
$json = ConvertTo-Json -InputObject ([object[]]$acc) -Depth 4 -Compress
# UTF-8 without BOM: PowerShell 5.1's ConvertFrom-Json rejects a BOM.
[System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
