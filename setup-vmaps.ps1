#requires -version 5.1
<#
.SYNOPSIS
    Populate GameData\vmaps from the server's run/data/vmaps.

.DESCRIPTION
    The client needs a LOCAL copy of the server's extracted collision data to
    have buildings, trees and fences be solid. Without it terrain collision
    still works from MCVT heights, so this is optional - but you will walk
    through the abbey.

    It tars on the server and transfers one stream rather than scp-ing ~4,700
    individual files, which is dramatically faster over SSH.

    Everything here is ASCII on purpose. A single non-ASCII character in a .ps1
    once made PowerShell report brace mismatches across the whole file, with the
    error pointing everywhere except the offending byte.

.PARAMETER Remote
    SSH target for the server box.

.PARAMETER RemotePath
    Absolute path to the vmaps directory on the server.

.PARAMETER Destination
    Where to put them locally. Defaults to <repo>\GameData\vmaps.

.PARAMETER Force
    Overwrite a destination that already has files in it.

.PARAMETER KeepArchive
    Leave the downloaded .tar.gz in place instead of deleting it.

.EXAMPLE
    .\setup-vmaps.ps1

.EXAMPLE
    .\setup-vmaps.ps1 -Remote nico@192.168.0.2 -Force
#>

param(
    [string] $Remote = "wowvmangos@homeserver",
    [string] $RemotePath = "/home/wowvmangos/vmangos/run/data/vmaps",
    [string] $Destination,
    [string] $RemoteArchive = "/tmp/msui-vmaps.tar.gz",
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
if (-not (Test-Path (Join-Path $repoRoot "MSUIClient.sln"))) {
    Fail "MSUIClient.sln is not next to this script. Run it from the repo root."
}

if (-not $Destination) {
    $Destination = Join-Path $repoRoot "GameData\vmaps"
}

Write-Host "MSUI Client - vmap setup"
Write-Host "  remote      $Remote"
Write-Host "  remote path $RemotePath"
Write-Host "  destination $Destination"
Write-Host ""

# --- preflight -------------------------------------------------------------

Require "ssh" "Install the Windows OpenSSH client (Settings > Optional Features)."
Require "scp" "Install the Windows OpenSSH client (Settings > Optional Features)."
Require "tar" "Windows 10 1803 and later ship bsdtar as tar.exe."

if (Test-Path $Destination) {
    $existing = @(Get-ChildItem -Path $Destination -File -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0 -and -not $Force) {
        Fail "$Destination already has $($existing.Count) file(s). Re-run with -Force to replace them."
    }
}

# Split the remote path into parent + leaf so tar can be told to chdir. Doing
# it this way keeps the archive free of absolute paths, which bsdtar on Windows
# would otherwise strip with a warning.
$trimmed = $RemotePath.TrimEnd('/')
$slash = $trimmed.LastIndexOf('/')
if ($slash -lt 1) { Fail "RemotePath does not look like an absolute path: $RemotePath" }
$remoteParent = $trimmed.Substring(0, $slash)
$remoteLeaf = $trimmed.Substring($slash + 1)

# --- pack on the server ----------------------------------------------------

Write-Host "[1/5] packing on $Remote (this takes a minute; ~580 MB of input)"

$packCmd = "test -d '$trimmed' && tar -czf '$RemoteArchive' -C '$remoteParent' '$remoteLeaf' && ls -l '$RemoteArchive'"
ssh $Remote $packCmd
if ($LASTEXITCODE -ne 0) {
    Fail "remote tar failed. Check that $trimmed exists and that /tmp has room for the archive (use -RemoteArchive to put it elsewhere)."
}

# --- transfer --------------------------------------------------------------

$localArchive = Join-Path $env:TEMP "msui-vmaps.tar.gz"
Write-Host ""
Write-Host "[2/5] downloading to $localArchive"

scp "${Remote}:${RemoteArchive}" $localArchive
if ($LASTEXITCODE -ne 0) { Fail "scp failed." }

ssh $Remote "rm -f '$RemoteArchive'" | Out-Null

# --- extract ---------------------------------------------------------------

$gameData = Split-Path -Parent $Destination
if (-not (Test-Path $gameData)) {
    New-Item -ItemType Directory -Path $gameData -Force | Out-Null
}

if ((Test-Path $Destination) -and $Force) {
    Write-Host ""
    Write-Host "[3/5] clearing $Destination"
    Remove-Item -Path $Destination -Recurse -Force
}

Write-Host ""
Write-Host "[4/5] extracting into $gameData"

tar -xzf $localArchive -C $gameData
if ($LASTEXITCODE -ne 0) { Fail "tar extraction failed." }

# The archive contains a folder named after the remote leaf. Normalise it.
$extracted = Join-Path $gameData $remoteLeaf
if ($extracted -ne $Destination) {
    if (Test-Path $Destination) { Remove-Item -Path $Destination -Recurse -Force }
    Move-Item -Path $extracted -Destination $Destination
}

if (-not $KeepArchive) {
    Remove-Item -Path $localArchive -Force -ErrorAction SilentlyContinue
}

# --- verify ----------------------------------------------------------------

Write-Host ""
Write-Host "[5/5] verifying"

$tiles = @(Get-ChildItem -Path $Destination -Filter "*.vmtile" -File).Count
$models = @(Get-ChildItem -Path $Destination -Filter "*.vmo" -File).Count
$bytes = (Get-ChildItem -Path $Destination -File | Measure-Object -Property Length -Sum).Sum

Write-Host "  $tiles .vmtile"
Write-Host "  $models .vmo"
Write-Host ("  {0:N1} GB on disk" -f ($bytes / 1GB))

if ($tiles -eq 0 -or $models -eq 0) {
    Fail "extraction produced no vmap files. Check RemotePath."
}

# The Northshire tile specifically - the one every verification step uses.
$northshire = Join-Path $Destination "000_32_48.vmtile"
if (Test-Path $northshire) {
    Write-Host "  000_32_48.vmtile present (Northshire)" -ForegroundColor Green
} else {
    Write-Host "  WARNING 000_32_48.vmtile missing - the start tile has no collision" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. client-config.json already points vmapPath at GameData\vmaps." -ForegroundColor Green
Write-Host "Run: dotnet run --project MSUIClient"
