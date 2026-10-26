$ErrorActionPreference = 'Continue'
$repo = 'J:\MUTAForTuneLab'
$cli = 'J:\MUTA\TuneLab-2.1.1\TuneLab.Cli.exe'
$env:TUNELAB_DATA_DIR = "$repo\artifacts\sandbox-ja"
$out = "$repo\artifacts\renders\youon"
New-Item -ItemType Directory -Force $out | Out-Null
$dic = "$repo\artifacts\package\sinsy\dic"
$voice = "$repo\artifacts\package\sinsy\voice\nitech_jp_song070_f001.htsvoice"
$sinsy = 'J:\MUTA\build\sinsy_console\sinsy.exe'
$py = 'C:\Users\kakar\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
$ascii = New-Object System.Text.ASCIIEncoding
$utf8 = New-Object System.Text.UTF8Encoding($false)

# 拗音：每行一个辅音，三个 mora；两字符假名用空格分隔的十六进制
$rows = @(
    @('304D 3083','304D 3085','304D 3087'),
    @('304E 3083','304E 3085','304E 3087'),
    @('3057 3083','3057 3085','3057 3087'),
    @('3058 3083','3058 3085','3058 3087'),
    @('3061 3083','3061 3085','3061 3087'),
    @('306B 3083','306B 3085','306B 3087'),
    @('3072 3083','3072 3085','3072 3087'),
    @('3073 3083','3073 3085','3073 3087'),
    @('3074 3083','3074 3085','3074 3087'),
    @('307F 3083','307F 3085','307F 3087'),
    @('308A 3083','308A 3085','308A 3087')
)
$banks = @('yanxi','hupo','weiyang')

for ($r = 0; $r -lt $rows.Count; $r++) {
    $n = $rows[$r].Count
    $spec = (0..($n - 1) | ForEach-Object { "$($_ * 480),480,60,$($rows[$r][$_])" }) -join '~'
    foreach ($b in $banks) {
        $code = "const VOICE={type:'MUTA',id:'$b'}; const p=tl.currentProject(); const t=p.addTrack(); const part=t.addPart({pos:0,endOffset:$($n * 480)}); part.setSoundSource({kind:'voice',type:VOICE.type,id:VOICE.id}); part.setProperty('language','ja'); const spec='$spec'; for (const row of spec.split('~')) { const f=row.split(','); part.addNote({pos:+f[0],dur:+f[1],pitch:+f[2],lyric:f[3].split(' ').map(h=>String.fromCharCode(parseInt(h,16))).join('')}); }"
        $cmd = "script run --code `"$code`"`r`nproject export-audio --path `"$out\bank-$b-$r.wav`" --timeoutSeconds 120`r`n"
        $file = "$out\cmd-$b-$r.txt"
        [IO.File]::WriteAllText($file, $cmd, $ascii)
        & $cli --commands $file --headless --yes 2>&1 | Out-Null
    }
    Write-Host "bank row $r done"

    $ev = @('R') + @($rows[$r] | ForEach-Object { -join (($_ -split ' ') | ForEach-Object { [string][char][Convert]::ToInt32($_, 16) }) }) + @('R')
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
