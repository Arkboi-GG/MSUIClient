#requires -version 5.1
<#
.SYNOPSIS
    Populate GameData\vmaps from a vmangos build - either a remote server over
    SSH, or a WSL distro on this same machine.

.DESCRIPTION
    Two modes.

    -Wsl      The vmangos fork lives in WSL on this machine. The archive is
              built INSIDE WSL and written straight to a Windows path, then
              extracted with Windows tar. That ordering matters: copying 4,700
              individual files across the WSL 9P share is painfully slow, while
              one large sequential write is not.

    default   The server is another box. Tars remotely, pulls one stream over
              scp, extracts locally.

    Neither mode is required for the client to run. Without vmaps you still get
    terrain collision from MCVT heights; you just walk through buildings.

    Everything here is ASCII on purpose. A single non-ASCII character in a .ps1
    once made PowerShell report brace mismatches across the whole file, with the
    error pointing everywhere except the offending byte.

.EXAMPLE
    .\setup-vmaps.ps1 -Wsl

.EXAMPLE
    .\setup-vmaps.ps1 -Wsl -Distro Ubuntu -WslPath /home/your-name/vmangos/run/data/vmaps

.EXAMPLE
    .\setup-vmaps.ps1 -Remote user@example-host -RemotePath /path/to/vmaps -Force
#>

param(
    [switch] $Wsl,
    [string] $Distro = "",
    [string] $WslPath = "~/vmangos/run/data/vmaps",

    [string] $Remote = "",
    [string] $RemotePath = "/path/to/vmaps",
    [string] $RemoteArchive = "/tmp/msui-vmaps.tar.gz",

    [string] $Destination,
    [switch] $Force,
    [switch] $KeepArchive
)

$ErrorActionPreference = "Stop"

function Fail([string] $message) {
    Write-Host ""
    Write-Host "FAILED: $message" -ForegroundColor Red
    exit 1
}

function Require([string] $name, [string] $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        Fail "'$name' not found on PATH. $hint"
    }
}

# --- locate the repo -------------------------------------------------------

$repoRoot = $PSScriptRoot
if (-not $repoRoot) { $repoRoot = (Get-Location).Path }

if (-not (Test-Path (Join-Path $repoRoot "MSUIClient.sln"))) {
    Fail "MSUIClient.sln is not next to this script. Run it from the repo root."
}

if (-not $Destination) {
    $Destination = Join-Path $repoRoot "GameData\vmaps"
}

$modeName = "SSH (remote)"
if ($Wsl) { $modeName = "WSL (local)" }

Write-Host "MSUI Client - vmap setup"
Write-Host ("  mode        " + $modeName)
Write-Host ("  destination " + $Destination)

Require "tar" "Windows 10 1803 and later ship bsdtar as tar.exe."

if (Test-Path $Destination) {
    $existing = @(Get-ChildItem -Path $Destination -File -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0 -and -not $Force) {
        Fail ("$Destination already has " + $existing.Count + " file(s). Re-run with -Force to replace them.")
    }
}

$gameData = Split-Path -Parent $Destination
if (-not (Test-Path $gameData)) {
    New-Item -ItemType Directory -Path $gameData -Force | Out-Null
}

$localArchive = Join-Path $env:TEMP "msui-vmaps.tar.gz"
$sourceLeaf = "vmaps"

# ===========================================================================
#  WSL MODE
# ===========================================================================
if ($Wsl) {
    Require "wsl" "WSL is not installed, or wsl.exe is not on PATH."

    # wsl.exe -l -q emits UTF-16LE. Read as-is, PowerShell 5.1 hands back
    # strings full of nulls that then fail every comparison in confusing ways.
    # Switching the console encoding around the call is the fix.
    if (-not $Distro) {
        $previousEncoding = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [System.Text.Encoding]::Unicode
            $distros = @(wsl.exe -l -q | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
        finally {
            [Console]::OutputEncoding = $previousEncoding
        }

        if ($distros.Count -eq 0) { Fail "no WSL distros installed." }
        $Distro = $distros[0]

        $note = ""
        if ($distros.Count -gt 1) { $note = " (first of " + $distros.Count + "; use -Distro to pick)" }
        Write-Host ("  distro      " + $Distro + $note)
    }
    else {
        Write-Host ("  distro      " + $Distro)
    }

    Write-Host ("  source      " + $WslPath)
    Write-Host ""

    # Resolve ~ and confirm the directory exists before doing any work.
    $checkCmd = 'p=$(eval echo ' + $WslPath + '); if [ -d "$p" ]; then echo "$p"; else echo MISSING; fi'
    $resolved = (wsl.exe -d $Distro -- bash -lc $checkCmd | Select-Object -Last 1)
    if ($resolved) { $resolved = $resolved.Trim() }

    if (-not $resolved -or $resolved -eq "MISSING") {
        Fail "vmaps directory not found inside WSL: $WslPath (distro $Distro)"
    }

    Write-Host ("[1/4] found " + $resolved)

    # The Windows temp path as WSL sees it, so tar writes straight to the
    # Windows drive instead of into the distro and back out again.
    $wslArchive = (wsl.exe -d $Distro -- wslpath -a "$localArchive" | Select-Object -Last 1)
    if ($wslArchive) { $wslArchive = $wslArchive.Trim() }
    if (-not $wslArchive) { Fail "wslpath could not translate $localArchive" }

    Write-Host "[2/4] packing inside WSL (one sequential write, not 4,700 small ones)"

    $sourceParent = $resolved.Substring(0, $resolved.LastIndexOf('/'))
    $sourceLeaf = $resolved.Substring($resolved.LastIndexOf('/') + 1)

    $packCmd = 'tar -czf "' + $wslArchive + '" -C "' + $sourceParent + '" "' + $sourceLeaf + '" && ls -lh "' + $wslArchive + '"'
    wsl.exe -d $Distro -- bash -lc $packCmd
    if ($LASTEXITCODE -ne 0) { Fail "tar inside WSL failed." }
}
# ===========================================================================
#  SSH MODE
# ===========================================================================
else {
    if (-not $Remote) { Fail "Remote is required in SSH mode (for example, user@example-host)." }
    Require "ssh" "Install the Windows OpenSSH client (Settings > Optional Features)."
    Require "scp" "Install the Windows OpenSSH client (Settings > Optional Features)."

    Write-Host ("  remote      " + $Remote)
    Write-Host ("  source      " + $RemotePath)
    Write-Host ""

    $trimmed = $RemotePath.TrimEnd('/')
    $slash = $trimmed.LastIndexOf('/')
    if ($slash -lt 1) { Fail "RemotePath does not look like an absolute path: $RemotePath" }

    $sourceParent = $trimmed.Substring(0, $slash)
    $sourceLeaf = $trimmed.Substring($slash + 1)

    Write-Host "[1/4] packing on $Remote"
    $packCmd = "test -d '$trimmed' && tar -czf '$RemoteArchive' -C '$sourceParent' '$sourceLeaf' && ls -l '$RemoteArchive'"
    ssh $Remote $packCmd
    if ($LASTEXITCODE -ne 0) {
        Fail "remote tar failed. Check that $trimmed exists and /tmp has room (see -RemoteArchive)."
    }

    Write-Host ""
    Write-Host "[2/4] downloading to $localArchive"
    scp "${Remote}:${RemoteArchive}" $localArchive
    if ($LASTEXITCODE -ne 0) { Fail "scp failed." }

    ssh $Remote "rm -f '$RemoteArchive'" | Out-Null
}

# --- extract ---------------------------------------------------------------

if (-not (Test-Path $localArchive)) { Fail "archive not found at $localArchive" }

if ((Test-Path $Destination) -and $Force) {
    Write-Host ""
    Write-Host "[3/4] clearing $Destination"
    Remove-Item -Path $Destination -Recurse -Force
}

Write-Host ""
Write-Host "[3/4] extracting into $gameData"

tar -xzf $localArchive -C $gameData
if ($LASTEXITCODE -ne 0) { Fail "tar extraction failed." }

$extracted = Join-Path $gameData $sourceLeaf
if ($extracted -ne $Destination) {
    if (Test-Path $Destination) { Remove-Item -Path $Destination -Recurse -Force }
    Move-Item -Path $extracted -Destination $Destination
}

if (-not $KeepArchive) {
    Remove-Item -Path $localArchive -Force -ErrorAction SilentlyContinue
}

# --- verify ----------------------------------------------------------------

Write-Host ""
Write-Host "[4/4] verifying"

$tiles = @(Get-ChildItem -Path $Destination -Filter "*.vmtile" -File).Count
$models = @(Get-ChildItem -Path $Destination -Filter "*.vmo" -File).Count
$bytes = (Get-ChildItem -Path $Destination -File | Measure-Object -Property Length -Sum).Sum

Write-Host ("  " + $tiles + " .vmtile")
Write-Host ("  " + $models + " .vmo")
Write-Host ("  {0:N1} GB on disk" -f ($bytes / 1GB))

if ($tiles -eq 0 -or $models -eq 0) {
    Fail "extraction produced no vmap files. Check the source path."
}

$northshire = Join-Path $Destination "000_32_48.vmtile"
if (Test-Path $northshire) {
    Write-Host "  000_32_48.vmtile present (Northshire)" -ForegroundColor Green
} else {
    Write-Host "  WARNING 000_32_48.vmtile missing - the start tile has no collision" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. Set vmapPath to GameData\vmaps in client-config.json." -ForegroundColor Green
Write-Host "Run: dotnet run --project MSUIClient"
