$ErrorActionPreference = 'Continue'
$repo = 'J:\MUTAForTuneLab'
$cli = 'J:\MUTA\TuneLab-2.1.1\TuneLab.Cli.exe'
$env:TUNELAB_DATA_DIR = "$repo\artifacts\sandbox-ja"
$out = "$repo\artifacts\renders\gojuon"
New-Item -ItemType Directory -Force $out | Out-Null
$dic = "$repo\artifacts\package\sinsy\dic"
$voice = "$repo\artifacts\package\sinsy\voice\nitech_jp_song070_f001.htsvoice"
$sinsy = 'J:\MUTA\build\sinsy_console\sinsy.exe'
$py = 'C:\Users\kakar\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'

$rows = @(
    @('3042','3044','3046','3048','304A'),
    @('304B','304D','304F','3051','3053'),
    @('3055','3057','3059','305B','305D'),
    @('305F','3061','3064','3066','3068'),
    @('306A','306B','306C','306D','306E'),
    @('306F','3072','3075','3078','307B'),
    @('307E','307F','3080','3081','3082'),
    @('3084','3086','3088'),
    @('3089','308A','308B','308C','308D'),
    @('308F','3092','3093')
)
$banks = @('yanxi','hupo','weiyang')
$ascii = New-Object System.Text.ASCIIEncoding
$utf8 = New-Object System.Text.UTF8Encoding($false)

for ($r = 0; $r -lt $rows.Count; $r++) {
    $n = $rows[$r].Count
    $spec = (0..($n - 1) | ForEach-Object { "$($_ * 480),480,60,$($rows[$r][$_])" }) -join '~'
    foreach ($b in $banks) {
        $code = "const VOICE={type:'MUTA',id:'$b'}; const p=tl.currentProject(); const t=p.addTrack(); const part=t.addPart({pos:0,endOffset:$($n * 480)}); part.setSoundSource({kind:'voice',type:VOICE.type,id:VOICE.id}); part.setProperty('language','ja'); const spec='$spec'; for (const row of spec.split('~')) { const f=row.split(','); part.addNote({pos:+f[0],dur:+f[1],pitch:+f[2],lyric:String.fromCharCode(parseInt(f[3],16))}); }"
        $cmd = "script run --code `"$code`"`r`nproject export-audio --path `"$out\bank-$b-$r.wav`" --timeoutSeconds 120`r`n"
        $file = "$out\cmd-$b-$r.txt"
        [IO.File]::WriteAllText($file, $cmd, $ascii)
        & $cli --commands $file --headless --yes 2>&1 | Out-Null
    }
    Write-Host "bank row $r done"

    $ev = @('R') + @($rows[$r] | ForEach-Object { [string][char][Convert]::ToInt32($_, 16) }) + @('R')
    while ($ev.Count % 4 -ne 0) { $ev += 'R' }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?><score-partwise version="2.0" xml:lang="ja"><part-list><score-part id="P1"><part-name>V</part-name></score-part></part-list><part id="P1">')
    $m = 0
    for ($i = 0; $i -lt $ev.Count; $i += 4) {
        $m++
        [void]$sb.AppendLine("<measure number=`"$m`">")
        if ($m -eq 1) { [void]$sb.AppendLine('<attributes><divisions>480</divisions><key><fifths>0</fifths></key><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes><direction><sound tempo="120"/></direction>') }
        for ($j = $i; $j -lt [Math]::Min($i + 4, $ev.Count); $j++) {
            if ($ev[$j] -eq 'R') { [void]$sb.AppendLine('<note><rest/><duration>480</duration><type>quarter</type></note>') }
            else { [void]$sb.AppendLine('<note><pitch><step>C</step><octave>4</octave></pitch><duration>480</duration><type>quarter</type><lyric number="1"><syllabic>single</syllabic><text>' + $ev[$j] + '</text></lyric></note>') }
        }
        [void]$sb.AppendLine('</measure>')
    }
    [void]$sb.AppendLine('</part></score-partwise>')
    $xml = "$out\row-$r.xml"
    [IO.File]::WriteAllText($xml, $sb.ToString(), $utf8)
    & $sinsy -w j -x $dic -m $voice -o "$out\yoko-$r.wav" $xml 2>&1 | Out-Null
    Write-Host "yoko row $r done"
}

$pyCode = @'
import sys, wave
import numpy as np

def read(path):
    with wave.open(path, "rb") as w:
        ch, rate, n = w.getnchannels(), w.getframerate(), w.getnframes()
        d = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768.0
    if ch > 1:
        d = d.reshape(-1, ch).mean(axis=1)
    return d, rate

def resample(d, rate, target):
    if rate == target:
        return d
    count = int(round(len(d) * target / rate))
    return np.interp(np.arange(count) / target, np.arange(len(d)) / rate, d).astype(np.float32)

def write(path, d, rate):
    pcm = np.repeat((np.clip(d, -1, 1) * 32767).astype(np.int16)[:, None], 2, axis=1)
    with wave.open(path, "wb") as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(rate); w.writeframes(pcm.tobytes())

target = 44100
out = sys.argv[1]
files = sys.argv[2:]
gap = np.zeros(int(target * 0.6), dtype=np.float32)
parts = []
for i in range(0, len(files), 2):
    b, br = read(files[i]); b = resample(b, br, target)
    y, yr = read(files[i + 1]); y = resample(y, yr, target)
    parts += [b, gap, y, gap]
write(out, np.concatenate(parts), target)
print(out)
'@
[IO.File]::WriteAllText("$repo\tools\pair-rows.py", $pyCode, $utf8)

foreach ($b in $banks) {
    $pyArgs = @("$repo\tools\pair-rows.py", "$out\compare-$b.wav")
    for ($r = 0; $r -lt $rows.Count; $r++) {
        $pyArgs += "$out\bank-$b-$r.wav"
        $pyArgs += "$out\yoko-$r.wav"
    }
    & $py @pyArgs | Out-Null
    Write-Host "paired $b"
}
Write-Host "all done"
