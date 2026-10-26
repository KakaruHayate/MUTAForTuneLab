# MUTA for TuneLab

[![DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/KakaruHayate/MUTAForTuneLab)
[![Latest release](https://img.shields.io/github/v/release/KakaruHayate/MUTAForTuneLab)](https://github.com/KakaruHayate/MUTAForTuneLab/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A TuneLab voice extension that sings with **MUTA voice banks**. It reads the `.voice`,
`.inf` and `.bnk` files of an installed MUTA voice, and renders Chinese or Japanese lyrics
inside TuneLab through the **worldline** vocoder (R1.1 by default, R2 optional).

The plugin contains no MUTA binaries, source code or voice banks: it is an independent
implementation that reads the voice-data formats and drives its own HTS + worldline chain.

**[中文说明](README.zh-CN.md)**

---

## Features

### Singing

- **Chinese and Japanese from one voice bank.** Language is a part setting: switching it
  changes only the front end (pinyin labels vs. Sinsy labels), not the timbre.
- **MUTA-style consonant timing.** Consonants use MUTA's own length table, capped at half
  the note, instead of the model's longer state durations. This is what makes short notes
  articulate instead of smearing.
- **Continuations.** `-` and `+` are melisma passengers: the head note's vowel is stretched
  across the hold, with no second attack. The editor shows the same span the audio uses.
- **Portamento across the beat.** Pitch changes start 60–90 ms before the beat and settle
  about 30 ms after it, instead of snapping on the beat.
- **Manual breaths.** A note with lyric `br` or `sp` is a breath: it takes a fragment of the
  bank's own `.inf` tail PCM when available (band-passed noise otherwise) and is mixed
  underneath the singing at roughly the level MUTA uses.
- **Pinned phonemes.** Any note can be pinned to explicit leading/body phonemes. The engine
  sings those symbols, and the bank follows their durations, stretch weights and body offset.
  Dragging a phoneme boundary re-renders the affected block.
- **Per-note bank register offset.** Each note can select a different recorded register from
  the bank by a semitone offset; the note's own pitch is unchanged.

### Editing integration

- **Absolute pitch is authoritative.** Where the host's pitch channel has a value, that value
  sets the frame's F0 exactly. Unpainted frames keep the note/model contour. Vibrato rides
  the host's deviation channel and is applied additively.
- **Seven automation lanes:** Energy, Timbre and Husky act on the acoustic model output;
  Gender, Tension, Breathiness and Voicing go to the vocoder. Phoneme speed and output gain
  are part settings.
- **Phoneme geometry** is resolved by TuneLab's own `PhonemeLayout`, so the band in the
  editor and the rendered audio come from the same numbers.
- **Incremental rendering.** Blocks are planned as a pure function and cached. A note edit
  invalidates its block; a curve edit invalidates only blocks overlapping the edited time
  range; a part setting invalidates everything.
- **Deterministic noise.** The analysis noise is seeded from the voice and block start, so
  the same project renders the same waveform.

### Vocoder

- **worldline R1.1** (default, built in): continuous-noise synthesis at 48 kHz. No extra
  installation.
- **worldline R2** (optional, full build): a user-supplied neural vocoder at 44.1 kHz,
  running through ONNX Runtime; DirectML is used when available, otherwise CPU. See
  [R2 vocoder](#r2-vocoder-optional).

---

## Requirements

| Component | Requirement |
| --- | --- |
| TuneLab | 2.1.0 or newer (SDK 1.1) |
| OS | Windows x64 |
| Voice data | an installed MUTA with song voices, or a folder containing `<name>.voice`, `<name>.inf`, `<name>.bnk` |
| .NET (build only) | .NET 8 SDK, CMake, Visual Studio 2022 C++ tools |

The plugin does not include a voice bank. Install MUTA separately, or point the extension at
a directory containing a bank.

## Installation

1. Download **`MUTAForTuneLab-<version>-win-x64.tlx`** (full) or
   **`...-win-x64-lite.tlx`** (R1.1 only) from
   [Releases](https://github.com/KakaruHayate/MUTAForTuneLab/releases).
2. In TuneLab, open the extension manager and install the `.tlx` file.
3. Restart TuneLab if it was already running.
4. Select a MUTA voice for the part. If the bank is not in the default MUTA directory, add
   its folder under **Settings → Extensions → MUTA for TuneLab → Voice folders**.

### Full vs. lite

| Build | Contents | Size |
| --- | --- | --- |
| Full | worldline R1.1 + R2 + ONNX Runtime + `mel.onnx` | ~20 MB |
| Lite | worldline R1.1 only, no ONNX/GPU surface | ~5 MB |

Both builds produce the same output on R1.1.

## Usage

1. Create a voice part and choose a MUTA voice.
2. Set the part **Language** to Chinese or Japanese. The lyrics should be the language's
   normal text (pinyin syllables for Chinese, kana for Japanese); the front end supplies
   the phonemes.
3. Draw notes and lyrics. The phoneme band shows what will actually be sung.
4. Drag a phoneme boundary to lock the note's phonemes and adjust their durations; edit a
   symbol in place by double-clicking it.
5. Use the note properties for **Pronunciation override**, **Preutterance**, and
   **Bank pitch offset** (semitones). The offset chooses which recorded register the bank
   uses; it does not transpose the note.
6. Draw pitch and vibrato with TuneLab's own tools. Pitch is absolute where painted.
7. Shape the take with the automation lanes, phoneme speed and output gain.
8. Choose the vocoder in the part's **Vocoder** setting.

Very long notes are rendered in one block up to 60 seconds; a block longer than that fails
with a clear message instead of exhausting memory. If a block fails, its status is shown in
the editor rather than silently shifted.

## R2 vocoder (optional)

R2 is only in the **full** build. It needs a vocoder package you provide:

1. Create a folder containing:
   - `vocoder.yaml`
   - the `.onnx` model named by `model:` in that yaml
2. Use a model compatible with the bundled mel front end (44.1 kHz, hop 512, 128 mel bins,
   fft 2048), for example an OpenUtau `pc_nsf_hifigan_44.1k_hop512_*` package.
3. Example `vocoder.yaml`:

```yaml
name: pc_nsf_hifigan
model: pc_nsf_hifigan.onnx
sample_rate: 44100
hop_size: 512
fft_size: 2048
num_mel_bins: 128
```

4. In TuneLab: **Settings → Extensions → MUTA for TuneLab → R2 vocoder folder**, choose that
   folder.
5. Set the part **Vocoder** to *WORLDLINE R2 (external vocoder)*.

`mel.onnx` ships with the full package; do not copy it into the vocoder folder. If R2 is
missing or misconfigured, the render falls back to R1.1 and the reason is logged.

## How this differs from MUTA

This is not a re-hosted copy of MUTA. It is a TuneLab-native engine that reads MUTA voice
data and reproduces the parts of MUTA's singing behaviour that matter for a bank to sound
right.

| Area | MUTA standalone | MUTA for TuneLab |
| --- | --- | --- |
| Host | MUTA editor/project | TuneLab editor, notes, pitch/vibrato, automation, phoneme band |
| Project files | MUTA projects | TuneLab projects; MUTA project files are not imported |
| Pitch | MUTA's pitch editing | TuneLab pitch channel; absolute where painted |
| Phoneme editing | MUTA's phoneme tools | TuneLab pinned phonemes; durations/weights/body offset are honoured by the bank |
| Vocoder | MUTA's built-in chain | worldline R1.1 / optional R2 |
| Japanese front end | MUTA's | Sinsy tables + NIT SONG070 voice for labels/timing; timbre still from your bank |
| Rendering | one engine instance | incremental blocks, per-block cache, deterministic noise, 60 s cap |
| Binaries | MUTA application | no MUTA binary, source or voice bank is included |

Deliberate behaviour compatibility includes the consonant length table, the slot assembly
and overlap rules, continuation handling, breath level and portamento shape. Everything is
implemented independently; the voice data is read from your installation.

Known limitations:

- Windows x64 only.
- A single block is capped at 60 s; longer notes fail rather than stream.
- R2 quality depends entirely on the vocoder package you provide.
- Japanese needs the bundled Sinsy dictionary/voice; no external Sinsy install is required.

## Build from source

```powershell
git clone https://github.com/KakaruHayate/MUTAForTuneLab.git
cd MUTAForTuneLab

# native: HTS wrapper, worldline, Sinsy label builder
powershell -ExecutionPolicy Bypass -File tools\build-native.ps1

# managed + package, pointing at a TuneLab installation
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir "C:\Program Files\TuneLab"

# build the .tlx
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

Lite build:

```powershell
dotnet build src\MutaForTuneLab\MutaForTuneLab.csproj -c Release `
  -p:TuneLabDir="C:\Program Files\TuneLab" -p:MutaLite=true `
  -p:OutputPath=artifacts\package-lite\ -t:Rebuild
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1 `
  -Package artifacts\package-lite -Output artifacts\MUTAForTuneLab-lite.tlx
```

Tests:

```powershell
dotnet run --project tests\UnitTests\UnitTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 -TuneLabDir "C:\Program Files\TuneLab"
```

## Documentation

- [Technical overview](docs/technical.md)
- [Implementation notes](docs/implementation.md)
- [Japanese/Chinese phone table](docs/ja-zh-phone-table.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## License

The plugin is MIT-licensed; see [LICENSE](LICENSE). Third-party components (HTS, WORLD,
worldline, Sinsy, NIT SONG070, ONNX Runtime) keep their own licences and are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

MUTA is a separate product. This project is not affiliated with or endorsed by its authors.
