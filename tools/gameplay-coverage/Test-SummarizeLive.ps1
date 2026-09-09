$ErrorActionPreference = 'Stop'
$fixturePath = Join-Path ([IO.Path]::GetTempPath()) ('msui-attempt-evidence-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixturePath | Out-Null
@'
time,spell_id,cell,row_kind,sample_index,caster_animation_verdict,spell_visual_verdict
1,133,standing,SAMPLE,0,,
2,133,standing,CELL,1,ANIM-EXACT,SPELL-VISUAL-PRESENT
3,133,moving,SAMPLE,0,,
4,133,moving,CELL,1,ANIM-STATIC,SPELL-VISUAL-ABSENT
'@ | Set-Content -LiteralPath (Join-Path $fixturePath 'spell-animation-sequence-fixture.csv')
@'
time,spell_id,result_enum
1.5,133,SMSG_SPELL_GO
3.5,133,LOCAL_MOVING
3.6,116,SMSG_SPELL_GO
4.5,133,SMSG_SPELL_GO
'@ | Set-Content -LiteralPath (Join-Path $fixturePath 'spell-sweep-fixture.csv')
& (Join-Path $PSScriptRoot 'Summarize-Live.ps1') -RunDirectory $fixturePath | Out-Null
$result = @(Import-Csv -LiteralPath (Join-Path $fixturePath 'attempt-evidence-fixture.csv'))
if ($result.Count -ne 2 -or $result[0].server_go -ne 'True' -or $result[1].server_go -ne 'False' -or
    $result[1].wire_events -ne 'LOCAL_MOVING') {
    throw 'Attempt evidence borrowed another cast, another spell, or a late reply.'
}
Write-Output 'Attempt boundary regression PASS: prior, other-spell and late GO events excluded.'
