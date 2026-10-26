$ErrorActionPreference = 'Continue'
$repo = 'J:\MUTAForTuneLab'
$cli = 'J:\MUTA\TuneLab-2.1.1\TuneLab.Cli.exe'
$env:TUNELAB_DATA_DIR = "$repo\artifacts\sandbox-ja"
$out = "$repo\artifacts\renders\sweep"
New-Item -ItemType Directory -Force $out | Out-Null
$py = 'C:\Users\kakar\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
$stat = "$repo\tools\wav-stat.py"
$ascii = New-Object System.Text.ASCIIEncoding

$base = '0,480,62,4F60~480,480,64,597D~960,480,65,5417~1440,480,64,4E86'

function Curve($id, $value) { "part.setAutomation('$id',0,1920,[{tick:0,value:$value},{tick:1919,value:$value}]);" }

$cases = @(
    @{ Name = 'baseline'; Js = ''; Spec = $base; End = 1920 }
    @{ Name = 'energy_min'; Js = (Curve 'Energy' -36); Spec = $base; End = 1920 }
    @{ Name = 'energy_max'; Js = (Curve 'Energy' 24); Spec = $base; End = 1920 }
    @{ Name = 'timbre_min'; Js = (Curve 'Timbre' -4); Spec = $base; End = 1920 }
    @{ Name = 'timbre_max'; Js = (Curve 'Timbre' 4); Spec = $base; End = 1920 }
    @{ Name = 'husky_min'; Js = (Curve 'Husky' 0); Spec = $base; End = 1920 }
    @{ Name = 'husky_max'; Js = (Curve 'Husky' 40); Spec = $base; End = 1920 }
    @{ Name = 'gender_0'; Js = (Curve 'Gender' 0); Spec = $base; End = 1920 }
    @{ Name = 'gender_1'; Js = (Curve 'Gender' 1); Spec = $base; End = 1920 }
    @{ Name = 'tension_0'; Js = (Curve 'Tension' 0); Spec = $base; End = 1920 }
    @{ Name = 'tension_1'; Js = (Curve 'Tension' 1); Spec = $base; End = 1920 }
    @{ Name = 'breath_0'; Js = (Curve 'Breathiness' 0); Spec = $base; End = 1920 }
    @{ Name = 'breath_1'; Js = (Curve 'Breathiness' 1); Spec = $base; End = 1920 }
    @{ Name = 'voicing_low'; Js = (Curve 'Voicing' 0.2); Spec = $base; End = 1920 }
    @{ Name = 'voicing_high'; Js = (Curve 'Voicing' 1.0); Spec = $base; End = 1920 }
    @{ Name = 'bank_fixed'; Js = "part.setProperty('bank_pitch_mode','fixed'); part.setProperty('bank_pitch_note',72);"; Spec = $base; End = 1920 }
    @{ Name = 'bank_offset'; Js = "part.setProperty('bank_pitch_offset',7);"; Spec = $base; End = 1920 }
    @{ Name = 'pitch_up'; Js = "part.setPitchLine(0,1920,[{tick:0,value:62},{tick:960,value:66},{tick:1920,value:62}]);"; Spec = $base; End = 1920 }
    @{ Name = 'speed_slow'; Js = "part.setProperty('phoneme_speed',0.5);"; Spec = $base; End = 1920 }
    @{ Name = 'speed_fast'; Js = "part.setProperty('phoneme_speed',2.0);"; Spec = $base; End = 1920 }
    @{ Name = 'edge_tick1'; Js = ''; Spec = '0,1,60,4F60'; End = 480 }
    @{ Name = 'edge_pitch0'; Js = ''; Spec = '0,480,0,4F60~480,480,64,597D'; End = 960 }
    @{ Name = 'edge_pitch127'; Js = ''; Spec = '0,480,127,4F60~480,480,64,597D'; End = 960 }
    @{ Name = 'edge_gap'; Js = ''; Spec = '0,480,62,4F60~4000,480,64,597D'; End = 5000 }
    @{ Name = 'vibrato'; Js = "part.addVibrato({pos:480,dur:960,frequency:6,amplitude:0.5});"; Spec = $base; End = 1920 }
    @{ Name = 'multi_block'; Js = ''; Spec = '0,480,62,4F60~480,480,64,597D~960,480,65,5417~1440,480,64,4E86~1920,480,62,4F60~2400,480,64,597D~2880,480,65,5417~3360,480,64,4E86~5760,480,62,4F60~6240,480,64,597D~6720,480,65,5417~7200,480,64,4E86~7680,480,62,4F60~8160,480,64,597D~8640,480,65,5417~9120,480,64,4E86'; End = 9600 }
)

$results = @()
foreach ($case in $cases) {
    $name = $case.Name
    $wav = "$out\$name.wav"
    $cmd = "$out\case-$name.cmd.txt"
    $code = "const p=tl.currentProject(); const t=p.addTrack(); const part=t.addPart({pos:0,endOffset:$($case.End)}); part.setSoundSource({kind:'voice',type:'MUTA',id:'yanxi'}); $($case.Js) const spec='$($case.Spec)'; for (const row of spec.split('~')) { const f=row.split(','); part.addNote({pos:+f[0],dur:+f[1],pitch:+f[2],lyric:String.fromCharCode(parseInt(f[3],16))}); }"
    [IO.File]::WriteAllText($cmd, "script run --code `"$code`"`r`nproject export-audio --path `"$wav`" --timeoutSeconds 120`r`n", $ascii)
    if (Test-Path $wav) { Remove-Item $wav -Force }
    $log = & $cli --commands $cmd --headless --yes 2>&1 | Out-String
    $rendered = $log -match 'Rendered the mix to'
    $refused = ($log -match 'failed|SILENT') -or (-not $rendered)
    $peak = 0.0; $rms = 0.0; $seconds = 0.0
    if (Test-Path $wav) {
        $json = & $py $stat $wav | ConvertFrom-Json
        $peak = $json.peak; $rms = $json.rms; $seconds = $json.seconds
    }
    $ok = $rendered -and (-not ($log -match 'SILENT')) -and ($peak -lt 1.0) -and ($rms -ge 0.0001)
    $sat = $peak -ge 0.99
    $verdict = if (-not $ok) { 'FAIL' } elseif ($sat) { 'SAT ' } else { 'ok  ' }
    $results += [pscustomobject]@{ case = $name; ok = $ok; saturated = $sat; rendered = $rendered; peak = [math]::Round($peak, 4); rms = [math]::Round($rms, 5); seconds = [math]::Round($seconds, 2) }
    Write-Host ("{0,-14} {1} peak {2,6}  rms {3,7}  {4}s" -f $name, $verdict, [math]::Round($peak, 3), [math]::Round($rms, 4), [math]::Round($seconds, 2))
}

$results | ConvertTo-Json -Depth 3 | Set-Content "$out\report.json" -Encoding UTF8
$failed = @($results | Where-Object { -not $_.ok }).Count
Write-Host ""
Write-Host ("{0} cases, {1} failed" -f $results.Count, $failed)
exit $(if ($failed -eq 0) { 0 } else { 1 })
