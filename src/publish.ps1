# Publish v1.0.0 of windows-context-menu-cleanup to GitHub.
#
#   .\publish.ps1 -Token <PAT>
#   .\publish.ps1 -Token <PAT> -WhatIf     # show the plan, touch nothing
#
# Uses the GitHub REST API through Invoke-RestMethod, so no git installation is
# required. Creates the repository, uploads every file in the release set as its own
# commit, then tags v1.0.0, cuts a Release and applies repository topics.
#
# IMPORTANT: a fine-grained PAT (github_pat_...) needs "Administration: Read and
# write" to create a repository, and "Contents: Read and write" to upload files.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [string]$Owner = 'simonwenmail-oss',
    [string]$Repo = 'windows-context-menu-cleanup',
    [string]$Tag = 'v1.0.0',
    [switch]$Private,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)   # repo root (this script lives in src\)

function Api {
    param([string]$Method, [string]$Path, $Body)

    $uri = if ($Path -like 'http*') { $Path } else { "https://api.github.com$Path" }

    # Send the body as explicit UTF-8 BYTES. Passing a large string to
    # Invoke-RestMethod -Body produced an HTTP 400 "malformed request" once the payload
    # passed roughly 75 KB (a base64 file body). The identical request over HttpWebRequest
    # with an explicit ContentLength succeeds, and file uploads flow through here, so this
    # is not cosmetic.
    $jsonBytes = $null
    if ($Body) {
        $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10 -Compress))
    }

    try {
        $req = [System.Net.HttpWebRequest]::Create($uri)
        $req.Method = $Method
        $req.UserAgent = 'ctxmenu-publish'
        $req.Accept = 'application/vnd.github+json'
        $req.Headers.Add('Authorization', "Bearer $Token")
        $req.Headers.Add('X-GitHub-Api-Version', '2022-11-28')
        if ($jsonBytes) {
            $req.ContentType = 'application/json; charset=utf-8'
            $req.ContentLength = $jsonBytes.Length
            $s = $req.GetRequestStream()
            $s.Write($jsonBytes, 0, $jsonBytes.Length)
            $s.Close()
        }

        $resp = $req.GetResponse()
        $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $text = $sr.ReadToEnd()
        $sr.Close()
        $resp.Close()

        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return ($text | ConvertFrom-Json)
    } catch [System.Net.WebException] {
        $detail = $_.Exception.Message
        if ($_.Exception.Response) {
            try {
                $er = $_.Exception.Response
                $sr = New-Object System.IO.StreamReader($er.GetResponseStream())
                $detail = $detail + ' | HTTP ' + [int]$er.StatusCode + ' | ' + $sr.ReadToEnd()
            } catch { }
        }
        throw ("$Method $Path failed: $detail")
    }
}

# ---- files to publish ------------------------------------------------------
# Excluded on purpose: build output, backups, editor noise. .gitignore already
# encodes most of it; this list is the authoritative release set.
$excludeDirs = @('dist', 'bin', 'obj', '.git')
$excludeExt = @('.exe', '.pdb', '.reg', '.log', '.tmp', '.bak')
$excludeNames = @('Thumbs.db', 'desktop.ini')

$files = New-Object System.Collections.Generic.List[string]
foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\')
    $parts = $rel.Split('\')
    $skip = $false
    foreach ($p in $parts) { if ($excludeDirs -contains $p) { $skip = $true } }
    if ($excludeExt -contains $f.Extension.ToLowerInvariant()) { $skip = $true }
    if ($excludeNames -contains $f.Name) { $skip = $true }
    # exclude only a stray publish script at the repository root; src/publish.ps1 ships
    if ($f.Name -eq 'publish.ps1' -and $parts.Count -le 1) { $skip = $true }
    if (-not $skip) { $files.Add($rel) }
}
$files = $files | Sort-Object

Write-Host ''
Write-Host "Target : https://github.com/$Owner/$Repo  ($(if($Private){'private'}else{'public'}))"
Write-Host "Tag    : $Tag"
Write-Host "Files  : $($files.Count)"
$files | ForEach-Object { Write-Host "   $_" }
Write-Host ''

if ($WhatIf) { Write-Host 'WhatIf: nothing was sent to GitHub.' ; return }

# ---- 1. create the repository (idempotent) ---------------------------------
$repoUrl = "https://github.com/$Owner/$Repo"
$exists = $false
try { $null = Api 'GET' "/repos/$Owner/$Repo"; $exists = $true } catch { $exists = $false }

if ($exists) {
    Write-Host "Repository already exists, reusing it." -ForegroundColor Yellow
} else {
    Write-Host "Creating repository..." -ForegroundColor Cyan
    $body = @{
        name        = $Repo
        description = 'Reliably remove unwanted Windows Explorer right-click menu entries (Baidu Netdisk, Thunder/Xunlei, Sogou Input, ...) with verification and rollback. PowerShell, no dependencies.'
        homepage    = ''
        private     = [bool]$Private
        has_issues  = $true
        has_wiki    = $false
        auto_init   = $false
    }
    $null = Api 'POST' '/user/repos' $body
    Write-Host "  created $repoUrl" -ForegroundColor Green
}

# ---- 2. upload every file as its own commit --------------------------------
# The Contents API needs the existing blob SHA when a path is already present, so
# a re-run updates files instead of failing.
Write-Host ''
Write-Host "Uploading files..." -ForegroundColor Cyan
foreach ($rel in $files) {
    $path = Join-Path $root ($rel -replace '\\', '\')
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $b64 = [Convert]::ToBase64String($bytes)

    $apiPath = "/repos/$Owner/$Repo/contents/" + ($rel -replace '\\', '/')
    $sha = $null
    try { $cur = Api 'GET' $apiPath; $sha = $cur.sha } catch { $sha = $null }

    $body = @{
        message = "Add $rel"
        content = $b64
        branch  = 'main'
    }
    if ($sha) { $body.sha = $sha; $body.message = "Update $rel" }

    $null = Api 'PUT' $apiPath $body
    Write-Host ("  ok  {0,-46} {1} bytes" -f $rel, $bytes.Length)
}

# ---- 3. tag v1.0.0 + release ----------------------------------------------
Write-Host ''
Write-Host "Tagging $Tag..." -ForegroundColor Cyan
$ref = Api 'GET' "/repos/$Owner/$Repo/git/ref/heads/main"
$headSha = $ref.object.sha

$tagExists = $false
try { $null = Api 'GET' "/repos/$Owner/$Repo/git/ref/tags/$Tag"; $tagExists = $true } catch { }
if (-not $tagExists) {
    $null = Api 'POST' "/repos/$Owner/$Repo/git/refs" @{
        ref = "refs/tags/$Tag"
        sha = $headSha
    }
    Write-Host "  tag $Tag -> $headSha" -ForegroundColor Green
} else {
    Write-Host "  tag $Tag already exists" -ForegroundColor Yellow
}

$notesFile = Join-Path $root 'release-notes.md'
if (-not (Test-Path -LiteralPath $notesFile)) { throw "release notes not found: $notesFile" }
$notes = [System.IO.File]::ReadAllText($notesFile)

try {
    $null = Api 'POST' "/repos/$Owner/$Repo/releases" @{
        tag_name = $Tag
        name     = "v1.0.0"
        body     = $notes
        draft    = $false
        prerelease = $false
    }
    Write-Host "  release v1.0.0 created" -ForegroundColor Green
} catch {
    Write-Host "  release step: $($_.Exception.Message)" -ForegroundColor Yellow
}

# ---- 4. topics -------------------------------------------------------------
$topics = @('dsh-plugin', 'dsh-skill', 'windows', 'registry', 'context-menu',
            'right-click-menu', 'powershell', 'windows-10', 'shell-extension',
            'debloat', 'deepseek-harness')
try {
    $null = Api 'PUT' "/repos/$Owner/$Repo/topics" @{ names = $topics }
    Write-Host "  topics applied: $($topics -join ', ')" -ForegroundColor Green
} catch {
    Write-Host "  topics step: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ''
Write-Host "DONE -> $repoUrl" -ForegroundColor Green
Write-Host "Release -> $repoUrl/releases/tag/$Tag"
