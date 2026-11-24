# MUTA for TuneLab

[![DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/KakaruHayate/MUTAForTuneLab)
[![Latest release](https://img.shields.io/github/v/release/KakaruHayate/MUTAForTuneLab)](https://github.com/KakaruHayate/MUTAForTuneLab/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A TuneLab voice plugin that sings with MUTA voice banks. It is an independent
implementation: the plugin reads the voice data of an installed MUTA and synthesises the
audio itself with the bundled worldline vocoder. No MUTA binary or voice data is included.

[Releases](https://github.com/KakaruHayate/MUTAForTuneLab/releases) · [中文说明](README.zh-CN.md)

## Features

- Chinese and Japanese singing from the same voice bank; language is a part setting.
- worldline R1.1 vocoder built in. R2, an external neural vocoder, is optional.
- Adjustable parameters per part, note and phoneme: pronunciation, preutterance, bank
  register offset, phoneme speed, output gain, and seven automation lanes (energy, timbre,
  husky, gender, tension, breathiness, voicing).
- Pinned phonemes, melisma continuations, manual breaths, portamento.
- Incremental rendering: only the edited blocks are re-rendered.

## Requirements

| Component | Requirement |
| --- | --- |
| TuneLab | 2.1.0 or newer |
| OS | Windows x64, Linux x64, macOS arm64 |
| Voice data | MUTA installed, or a folder containing `<name>.voice`, `<name>.inf`, `<name>.bnk` |

## Install

1. Download a `.tlx` from Releases and install it in TuneLab.
2. Restart TuneLab.
3. Choose a MUTA voice for the part. If the voice is not in MUTA's own folder, add its
   directory under **Settings → Extensions → MUTA for TuneLab → Extra voice directories**.

Two builds are published: **full** (R1.1 and R2) and **lite** (R1.1 only). They sound the
same on R1.1.

## Use

1. Add a voice part, choose a voice, set **Language**.
2. Enter lyrics: pinyin syllables for Chinese, kana for Japanese.
3. Edit the notes. Drag a phoneme boundary to pin a note's phonemes.
4. Draw pitch and vibrato with TuneLab's tools.
5. Shape the take with the automation lanes and the part settings.

## R2 vocoder

R2 is optional and only in the full build. Point **Settings → Extensions → MUTA for
TuneLab → R2 vocoder folder** at a folder containing `vocoder.yaml` and the `.onnx` model
it names (44.1 kHz, hop 512, 128 mel bins, fft 2048, for example an OpenUtau
`pc_nsf_hifigan_44.1k_hop512_*` package). Windows uses DirectML; Linux and macOS use the
WebGPU plugin execution provider; CPU is the fallback.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-native.ps1
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir <TuneLab folder>
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

Tests: `dotnet run --project tests\UnitTests\UnitTests.csproj -c Release`.

## License

MIT for this project; see [LICENSE](LICENSE). Third-party components keep their own
licences, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). MUTA is a separate
product and this project is not affiliated with its authors.
