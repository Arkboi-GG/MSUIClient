#requires -version 5.1
<#
.SYNOPSIS
    Create the gitignored GameData folder and optionally populate it, so the
    repo is self-contained on any machine.

.DESCRIPTION
    GameData holds the WoW 1.12.1 client archives and, optionally, a copy of a
    vmangos build's extracted vmaps. It is gitignored and must stay that way -
    those are several gigabytes of Blizzard's files.

    Bulk copies go through robocopy rather than Copy-Item. Copy-Item gives no
    progress on a multi-gigabyte transfer, cannot resume, and is markedly slower
    across the WSL 9P share where a travel setup usually lives.

    Everything here is ASCII on purpose. A single non-ASCII character in a .ps1
    once made PowerShell report brace mismatches across the whole file, with the
    error pointing everywhere except the offending byte.

.EXAMPLE
    .\setup-gamedata.ps1
    Just creates the folders and reports what is missing.

.EXAMPLE
    .\setup-gamedata.ps1 -CopyFrom "C:\WoW Vanilla"
    Copies the MPQs and locale folders in.

.EXAMPLE
    .\setup-gamedata.ps1 -WslVmaps
    Pulls vmaps out of the default WSL distro. For anything more than the
    default path, prefer setup-vmaps.ps1 - it is much faster.
#>

param(
    [string] $CopyFrom = "",
    [string] $VmapFrom = "",
    [switch] $WslVmaps,
    [string] $Distro = "",
    [string] $WslVmapPath = "~/vmangos/run/data/vmaps"
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

# GameData must never be committed. Say so loudly if the rule is missing,
# because the failure otherwise shows up as a rejected push after the commit.
$ignorePath = Join-Path $root '.gitignore'
if (Test-Path $ignorePath) {
    $hasRule = Select-String -Path $ignorePath -Pattern '^\s*GameData/' -Quiet
    if (-not $hasRule) {
        Write-Host ""
        Write-Host "  WARNING .gitignore has no 'GameData/' rule." -ForegroundColor Yellow
        Write-Host "  Add it before committing, or several GB end up in git history." -ForegroundColor Yellow
    }
}

function Copy-Tree([string] $source, [string] $target, [string] $label) {
    Write-Host ""
    Write-Host ("Copying " + $label + " from " + $source) -ForegroundColor Cyan
    Write-Host "  (robocopy: resumable, shows progress, survives a flaky share)" -ForegroundColor DarkGray

    # /E recurse incl. empty, /NP no per-file percent spam, /R:2 /W:2 fail fast
    # on a locked file rather than retrying a million times.
    robocopy $source $target /E /NP /NFL /NDL /R:2 /W:2 | Out-Null

    # robocopy exit codes below 8 are success variants. 8 and above are real.
    if ($LASTEXITCODE -ge 8) {
        Write-Host ("  ERROR  robocopy failed with code " + $LASTEXITCODE) -ForegroundColor Red
        return $false
    }

    Write-Host "  done." -ForegroundColor Green
    return $true
}

# --- MPQs ------------------------------------------------------------------

if ($CopyFrom) {
    # Accept either the install root (has a Data subfolder) or Data itself.
    $srcData = $CopyFrom
    if (Test-Path (Join-Path $CopyFrom 'Data')) { $srcData = Join-Path $CopyFrom 'Data' }

    if (-not (Test-Path $srcData)) {
        Write-Host ("  ERROR  source not found: " + $srcData) -ForegroundColor Red
    }
    else {
        $mpqs = @(Get-ChildItem -Path $srcData -Filter '*.MPQ' -File)
        if ($mpqs.Count -eq 0) {
            Write-Host ("  ERROR  no .MPQ files in " + $srcData) -ForegroundColor Red
        }
        else {
            $totalGb = ($mpqs | Measure-Object -Property Length -Sum).Sum / 1GB
            Copy-Tree $srcData $dataDir ("{0} MPQ(s) plus locale folders, {1:N1} GB" -f $mpqs.Count, $totalGb) | Out-Null
        }
    }
}

# --- vmaps -----------------------------------------------------------------

if ($WslVmaps -and -not $VmapFrom) {
    # wsl.exe -l -q emits UTF-16LE; read raw and PowerShell 5.1 hands back
    # strings full of nulls that fail every comparison in confusing ways.
    if (-not $Distro) {
        $previousEncoding = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [System.Text.Encoding]::Unicode
            $distros = @(wsl.exe -l -q | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
        finally {
            [Console]::OutputEncoding = $previousEncoding
        }

        if ($distros.Count -eq 0) {
            Write-Host "  ERROR  no WSL distros installed." -ForegroundColor Red
        }
        else {
            $Distro = $distros[0]
        }
    }

    if ($Distro) {
        $checkCmd = 'p=$(eval echo ' + $WslVmapPath + '); if [ -d "$p" ]; then echo "$p"; else echo MISSING; fi'
        $resolved = (wsl.exe -d $Distro -- bash -lc $checkCmd | Select-Object -Last 1)
        if ($resolved) { $resolved = $resolved.Trim() }

        if (-not $resolved -or $resolved -eq 'MISSING') {
            Write-Host ("  ERROR  not found inside WSL: " + $WslVmapPath) -ForegroundColor Red
        }
        else {
            # \\wsl.localhost is the modern share; \\wsl$ is the older name and
            # still works everywhere it did before.
            $unc = "\\wsl.localhost\$Distro" + $resolved.Replace('/', '\')
            if (-not (Test-Path $unc)) {
                $unc = "\\wsl`$\$Distro" + $resolved.Replace('/', '\')
            }
            Write-Host ""
            Write-Host ("WSL vmaps: " + $unc) -ForegroundColor Cyan
            Write-Host "  For a full copy prefer setup-vmaps.ps1 -Wsl - it tars inside WSL" -ForegroundColor DarkGray
            Write-Host "  and moves one stream instead of ~4,700 files over the 9P share." -ForegroundColor DarkGray
            $VmapFrom = $unc
        }
    }
}

if ($VmapFrom) {
    if (-not (Test-Path $VmapFrom)) {
        Write-Host ("  ERROR  vmap source not found: " + $VmapFrom) -ForegroundColor Red
    }
    else {
        Copy-Tree $VmapFrom $vmapDir "vmaps (~580 MB, be patient)" | Out-Null
    }
}

# --- report ----------------------------------------------------------------

$mpqCount  = @(Get-ChildItem -Path $dataDir -Filter '*.MPQ' -File -ErrorAction SilentlyContinue).Count
$vmapCount = @(Get-ChildItem -Path $vmapDir -Filter '*.vmtile' -File -ErrorAction SilentlyContinue).Count
$vmoCount  = @(Get-ChildItem -Path $vmapDir -Filter '*.vmo' -File -ErrorAction SilentlyContinue).Count

Write-Host ""
Write-Host "GameData status:" -ForegroundColor Cyan
Write-Host ("  Data/   " + $mpqCount + " MPQ file(s)")
Write-Host ("  vmaps/  " + $vmapCount + " .vmtile, " + $vmoCount + " .vmo")

if ($mpqCount -eq 0) {
    Write-Host ""
    Write-Host "No MPQs yet. Either:" -ForegroundColor Yellow
    Write-Host "  - copy your WoW 1.12 Data\*.MPQ into GameData\Data\ by hand, or"
    Write-Host "  - re-run:  .\setup-gamedata.ps1 -CopyFrom `"C:\WoW Vanilla`""
}

if ($vmapCount -eq 0) {
    Write-Host ""
    Write-Host "No vmaps. That is optional - terrain collision still works from" -ForegroundColor DarkGray
    Write-Host "MCVT heights, you just walk through buildings. To populate:" -ForegroundColor DarkGray
    Write-Host "  .\setup-vmaps.ps1 -Wsl          (vmangos inside WSL on this machine)"
    Write-Host "  .\setup-vmaps.ps1               (vmangos on another box over SSH)"
}

Write-Host ""
Write-Host "config: client-config.json uses paths relative to the repo root --" -ForegroundColor Cyan
Write-Host '  "clientDataPath": "GameData\\Data",'
Write-Host '  "vmapPath": "GameData\\vmaps"     (or null to skip vmap collision)'
