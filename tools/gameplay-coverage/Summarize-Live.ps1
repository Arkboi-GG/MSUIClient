param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
# Preserve the distinction between evidence and review. This never emits a gameplay PASS.
$runPath = (Resolve-Path -LiteralPath $RunDirectory).Path
$sequenceFiles = @(Get-ChildItem -LiteralPath $runPath -Filter 'spell-animation-sequence-*.csv')
foreach ($sequenceFile in $sequenceFiles) {
    $stamp = $sequenceFile.BaseName.Substring('spell-animation-sequence-'.Length)
    $wirePath = Join-Path $runPath ('spell-sweep-' + $stamp + '.csv')
    $wire = @(Import-Csv -LiteralPath $wirePath)
    $observations = [System.Collections.Generic.List[object]]::new()
    $samples = [System.Collections.Generic.List[object]]::new()
    foreach ($row in (Import-Csv -LiteralPath $sequenceFile.FullName)) {
        if ($row.row_kind -eq 'SAMPLE') {
            if ([int]$row.sample_index -eq 0) { $samples.Clear() }
            $samples.Add($row)
            continue
        }
        if ($row.row_kind -ne 'CELL' -or $samples.Count -eq 0) { continue }
        $firstTime = [double]::Parse($samples[0].time, [Globalization.CultureInfo]::InvariantCulture)
        $lastTime = [double]::Parse($row.time, [Globalization.CultureInfo]::InvariantCulture)
        $events = @($wire | Where-Object {
            $eventTime = [double]::Parse($_.time, [Globalization.CultureInfo]::InvariantCulture)
            $_.spell_id -eq $row.spell_id -and $eventTime -ge $firstTime -and $eventTime -le $lastTime -and $_.result_enum -notlike 'ROSTER*'
        })
        $observations.Add([pscustomobject]@{
            spell_id = $row.spell_id; cell = $row.cell; start = $firstTime; end = $lastTime
            samples = $samples.Count; server_go = [bool]($events.result_enum -contains 'SMSG_SPELL_GO')
            wire_events = ($events.result_enum -join '|')
            reported_animation = $row.caster_animation_verdict
            reported_visual = $row.spell_visual_verdict
            review = 'REQUIRES_FRAME_AND_MECHANIC_REVIEW'
            evidence_file = $sequenceFile.Name
        })
        $samples.Clear()
    }
    $outputPath = Join-Path $runPath ('attempt-evidence-' + $stamp + '.csv')
    $observations | Export-Csv -LiteralPath $outputPath -NoTypeInformation
    $observations | Format-Table spell_id,cell,samples,server_go,reported_visual -AutoSize
    Write-Output $outputPath
}
