#Requires -Version 5.1
# organize.ps1 -- MSUI Client repo layout
#
# Run from the repo root (C:\Users\nico\source\repos\MSUIClient), where every
# file currently sits flat. Creates folders, moves each file home, and rewrites
# the namespace lines in the seven ported Formats readers.
#
# Safe to re-run: skips a move if already in place, skips a rewrite if the old
# text isn't there.
#
#   cd C:\Users\nico\source\repos\MSUIClient
#   powershell -ExecutionPolicy Bypass -File .\organize.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
Write-Host "Repo root: $root" -ForegroundColor Cyan

$proj = Join-Path $root 'MSUIClient'

$folders = @(
    $proj,
    (Join-Path $proj 'Engine'),
    (Join-Path $proj 'World'),
    (Join-Path $proj 'Formats'),
    (Join-Path $proj 'Formats\Mpq'),
    (Join-Path $proj 'Shaders')
)
foreach ($f in $folders) {
    if (-not (Test-Path $f)) {
        New-Item -ItemType Directory -Path $f -Force | Out-Null
        Write-Host ("  created  " + $f.Substring($root.Length + 1)) -ForegroundColor DarkGray
    }
}

# filename -> destination folder (relative to repo root)
$map = @{
    'MSUIClient.sln'             = '.'
    'PROJECT_HANDBOOK.md'        = '.'
    'SETUP.md'                   = '.'
    '.gitignore'                 = '.'
    '.gitattributes'             = '.'
    'MSUIClient.csproj'          = 'MSUIClient'
    'client-config.json.example' = 'MSUIClient'
    'client-config.json'         = 'MSUIClient'
    'Program.cs'                 = 'MSUIClient'
    'ClientConfig.cs'            = 'MSUIClient'
    'ClientWindow.cs'            = 'MSUIClient\Engine'
    'Camera.cs'                  = 'MSUIClient\Engine'
    'Shader.cs'                  = 'MSUIClient\Engine'
    'Texture.cs'                 = 'MSUIClient\Engine'
    'TerrainTile.cs'             = 'MSUIClient\World'
    'TerrainTextures.cs'         = 'MSUIClient\World'
    'TerrainRenderer.cs'         = 'MSUIClient\World'
    'terrain.vert'               = 'MSUIClient\Shaders'
    'terrain.frag'               = 'MSUIClient\Shaders'
    'MpqCrypto.cs'               = 'MSUIClient\Formats\Mpq'
    'MpqArchive.cs'              = 'MSUIClient\Formats\Mpq'
    'PkwareExplode.cs'           = 'MSUIClient\Formats\Mpq'
    'MpqArchiveWriter.cs'        = 'MSUIClient\Formats\Mpq'
    'BlpDecoder.cs'              = 'MSUIClient\Formats'
    'AdtTerrainReader.cs'        = 'MSUIClient\Formats'
    'VmapFormat.cs'              = 'MSUIClient\Formats'
}

Write-Host ""
Write-Host "Moving files..." -ForegroundColor Cyan
$moved = 0
$missing = @()

foreach ($name in $map.Keys) {
    $src = Join-Path $root $name
    $destDir = Join-Path $root $map[$name]
    $dest = Join-Path $destDir $name

    if (Test-Path $dest -PathType Leaf) {
        if (($src -ne $dest) -and (Test-Path $src -PathType Leaf)) {
            Write-Host ("  DUPLICATE  " + $name + " at both root and dest -- delete the root copy") -ForegroundColor Yellow
        }
        continue
    }

    if (Test-Path $src -PathType Leaf) {
        Move-Item -Path $src -Destination $dest -Force
        Write-Host ("  moved    " + $name + "  ->  " + $map[$name]) -ForegroundColor DarkGray
        $moved++
    }
    else {
        $missing += $name
    }
}

Write-Host ("  " + $moved + " file(s) moved.") -ForegroundColor Green

# namespace rewrites for the seven ported readers
$rewrites = @(
    @{ file = 'MSUIClient\Formats\Mpq\MpqCrypto.cs';        edits = @( ,@('namespace MangosSuperUI.Services.Mpq','namespace MSUIClient.Formats.Mpq')) }
    @{ file = 'MSUIClient\Formats\Mpq\MpqArchive.cs';       edits = @( ,@('namespace MangosSuperUI.Services.Mpq','namespace MSUIClient.Formats.Mpq')) }
    @{ file = 'MSUIClient\Formats\Mpq\PkwareExplode.cs';    edits = @( ,@('namespace MangosSuperUI.Services.Mpq','namespace MSUIClient.Formats.Mpq')) }
    @{ file = 'MSUIClient\Formats\Mpq\MpqArchiveWriter.cs'; edits = @( ,@('namespace MangosSuperUI.Services.Mpq','namespace MSUIClient.Formats.Mpq')) }
    @{ file = 'MSUIClient\Formats\BlpDecoder.cs';           edits = @( ,@('namespace MangosSuperUI.Services','namespace MSUIClient.Formats')) }
    @{ file = 'MSUIClient\Formats\AdtTerrainReader.cs';     edits = @(
            @('using MangosSuperUI.Services.Mpq;','using MSUIClient.Formats.Mpq;'),
            @('namespace MangosSuperUI.Services','namespace MSUIClient.Formats')) }
    @{ file = 'MSUIClient\Formats\VmapFormat.cs';           edits = @( ,@('namespace MangosSuperUI.Services.WorldExport','namespace MSUIClient.Formats')) }
)

Write-Host ""
Write-Host "Rewriting namespaces..." -ForegroundColor Cyan

foreach ($r in $rewrites) {
    $path = Join-Path $root $r.file
    if (-not (Test-Path $path -PathType Leaf)) {
        Write-Host ("  SKIP (not found)  " + $r.file) -ForegroundColor Yellow
        continue
    }

    $text = [System.IO.File]::ReadAllText($path)
    $changed = $false

    foreach ($e in $r.edits) {
        $old = $e[0]
        $new = $e[1]
        if ($text.Contains($old)) {
            $text = $text.Replace($old, $new)
            $changed = $true
        }
        elseif (-not $text.Contains($new)) {
            Write-Host ("  WARN  " + $r.file + ": '" + $old + "' not found") -ForegroundColor Yellow
        }
    }

    if ($changed) {
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)
        Write-Host ("  rewrote  " + $r.file) -ForegroundColor DarkGray
    }
    else {
        Write-Host ("  ok       " + $r.file + " (already correct)") -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "Expected but not found in the flat drop:" -ForegroundColor Yellow
    foreach ($m in $missing) {
        $note = ''
        if ($m -eq 'client-config.json')  { $note = '  (copy from .example after this runs)' }
        if ($m -eq 'MpqArchiveWriter.cs') { $note = '  (optional -- only needed to write patches)' }
        if (($m -eq '.gitignore') -or ($m -eq '.gitattributes')) { $note = '  (dotfiles may be hidden; verify they exist)' }
        Write-Host ("  - " + $m + $note) -ForegroundColor Yellow
    }
}

$leftovers = Get-ChildItem -Path $root -File | Where-Object {
    ($_.Extension -in '.cs', '.vert', '.frag') -or ($_.Name -like '*.csproj')
}
if ($leftovers) {
    Write-Host ""
    Write-Host "Still at root (unmapped -- check these):" -ForegroundColor Yellow
    foreach ($l in $leftovers) { Write-Host ("  - " + $l.Name) -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  1. copy MSUIClient\client-config.json.example  MSUIClient\client-config.json"
Write-Host "  2. edit client-config.json, set clientDataPath to your WoW 1.12.1 Data folder"
Write-Host "  3. dotnet build"
