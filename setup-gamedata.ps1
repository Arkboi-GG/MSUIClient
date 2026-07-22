#Requires -Version 5.1
# setup-gamedata.ps1 -- create the gitignored GameData folder and (optionally)
# copy the WoW MPQs into it, so the repo is fully self-contained.
#
# Run from the repo root:
#   cd C:\Users\nico\source\repos\MSUIClient
#   powershell -ExecutionPolicy Bypass -File .\setup-gamedata.ps1
#
# By default it only creates the folders and tells you what to drop where.
# Pass -CopyFrom to copy an existing WoW install's Data folder in:
#   .\setup-gamedata.ps1 -CopyFrom "C:\WoW Vanilla"

param(
    [string]$CopyFrom = "",
    [string]$VmapFrom = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

$gameData = Join-Path $root 'GameData'
$dataDir  = Join-Path $gameData 'Data'
$vmapDir  = Join-Path $gameData 'vmaps'

Write-Host "Repo root: $root" -ForegroundColor Cyan

foreach ($d in @($gameData, $dataDir, $vmapDir)) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Write-Host ("  created  " + $d.Substring($root.Length + 1)) -ForegroundColor DarkGray
    }
}

# --- optional: copy the MPQs in --------------------------------------------
if ($CopyFrom) {
    # Accept either the install root (has a Data subfolder) or the Data folder itself.
    $srcData = $CopyFrom
    if (Test-Path (Join-Path $CopyFrom 'Data')) { $srcData = Join-Path $CopyFrom 'Data' }

    if (-not (Test-Path $srcData)) {
        Write-Host ("  ERROR  source not found: " + $srcData) -ForegroundColor Red
    }
    else {
        $mpqs = Get-ChildItem -Path $srcData -Filter '*.MPQ' -File
        if ($mpqs.Count -eq 0) {
            Write-Host ("  ERROR  no .MPQ files in " + $srcData) -ForegroundColor Red
        }
        else {
            Write-Host ""
            Write-Host ("Copying " + $mpqs.Count + " MPQ(s) from " + $srcData + " ...") -ForegroundColor Cyan
            $i = 0
            foreach ($m in $mpqs) {
                $i++
                Write-Host ("  [" + $i + "/" + $mpqs.Count + "] " + $m.Name + " (" + [math]::Round($m.Length/1MB) + " MB)") -ForegroundColor DarkGray
                Copy-Item -Path $m.FullName -Destination $dataDir -Force
            }
            # locale subfolders (enUS etc.) hold the base+patch archives for text/UI
            $locales = Get-ChildItem -Path $srcData -Directory | Where-Object { $_.Name -match '^[a-z]{2}[A-Z]{2}$' }
            foreach ($loc in $locales) {
                Write-Host ("  locale " + $loc.Name + " ...") -ForegroundColor DarkGray
                Copy-Item -Path $loc.FullName -Destination $dataDir -Recurse -Force
            }
            Write-Host "  MPQ copy done." -ForegroundColor Green
        }
    }
}

if ($VmapFrom) {
    if (-not (Test-Path $VmapFrom)) {
        Write-Host ("  ERROR  vmap source not found: " + $VmapFrom) -ForegroundColor Red
    }
    else {
        Write-Host ""
        Write-Host ("Copying vmaps from " + $VmapFrom + " (this is ~580 MB, be patient) ...") -ForegroundColor Cyan
        Copy-Item -Path (Join-Path $VmapFrom '*') -Destination $vmapDir -Recurse -Force
        Write-Host "  vmap copy done." -ForegroundColor Green
    }
}

# --- report ----------------------------------------------------------------
$mpqCount  = (Get-ChildItem -Path $dataDir -Filter '*.MPQ' -File -ErrorAction SilentlyContinue).Count
$vmapCount = (Get-ChildItem -Path $vmapDir -Filter '*.vmtile' -File -ErrorAction SilentlyContinue).Count

Write-Host ""
Write-Host "GameData status:" -ForegroundColor Cyan
Write-Host ("  Data/   " + $mpqCount + " MPQ file(s)")
Write-Host ("  vmaps/  " + $vmapCount + " .vmtile file(s)")

if ($mpqCount -eq 0) {
    Write-Host ""
    Write-Host "No MPQs yet. Either:" -ForegroundColor Yellow
    Write-Host "  - copy your WoW 1.12 Data\*.MPQ into GameData\Data\ by hand, or"
    Write-Host "  - re-run:  .\setup-gamedata.ps1 -CopyFrom `"C:\WoW Vanilla`""
}

Write-Host ""
Write-Host "config: client-config.json now uses relative paths --" -ForegroundColor Cyan
Write-Host '  "clientDataPath": "GameData\\Data",'
Write-Host '  "vmapPath": null      (or "GameData\\vmaps" once populated)'
