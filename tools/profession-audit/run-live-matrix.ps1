param(
    [string]$Configuration = "NIGHT_04/client-config.json"
)

$ErrorActionPreference = "Continue"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$runner = Join-Path $root "tools/live-run/live-run.csproj"
$config = Join-Path $root $Configuration
$cases = @(
    @{ Name = "alchemy"; Character = "Nbpalhuman" },
    @{ Name = "blacksmithing"; Character = "Nbhundwarf" },
    @{ Name = "cooking"; Character = "Nbprihuman" },
    @{ Name = "engineering"; Character = "Nbshaorc" },
    @{ Name = "leatherworking"; Character = "Nbmaghuman" },
    @{ Name = "tailoring"; Character = "Nbwlkgnome" },
    @{ Name = "enchanting"; Character = "Nbdrunelf" },
    @{ Name = "first-aid"; Character = "Nbpalhuman" },
    @{ Name = "mining"; Character = "Nbhundwarf" },
    @{ Name = "poisons"; Character = "Nbroghuman" }
)

$failures = 0
foreach ($case in $cases) {
    $scenario = Join-Path $root "scenarios/night05/profession-$($case.Name)-10.txt"
    $output = Join-Path $root "live-runs/N5-profession-$($case.Name)-clean"
    & dotnet run --no-restore --project $runner -- $config `
        --live-protocol $scenario --out $output --timeout 180 --character $case.Character
    if ($LASTEXITCODE -ne 0) { $failures++ }
}

exit $failures
