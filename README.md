# MUTAForTuneLab

A TuneLab voice extension that brings MUTA's singing voices to TuneLab: Chinese and
Japanese lyrics, rendered through the **worldline** vocoder (R1.1 by default, R2 optional).

> Status: it sings. Both chains render through the host end to end, every declared
> parameter reaches the audio, and the noise is reproducible across renders. See
> [docs/roadmap.md](docs/roadmap.md) for what is left.

## What's new

Compared with singing the same banks in MUTA itself. MUTA's own feature set could not be
audited item by item, so this lists what the integration adds, not everything upstream may
have.

**Editing**

- **Pitch is absolute.** Drawing a line sets each frame's F0 outright — no clamp, no glide,
  no model residue on the frames you drew; frames you did not draw keep the model's shaped
  contour. Vibrato rides the host's deviation channel and is audible and visible.
- **Pinned phonemes.** Any note can have its leading/body phonemes pinned and edited; the
  engine sings exactly those. Chinese rebuilds the syllable from them, Japanese reverses
  them to kana (`k`+`y`+`a` → きゃ) including youon.
- **Continuation marks.** `-` and `+` are passengers: no new slot, no second attack -- the
  chain head's vowel is stretched across the whole hold, and the editor shows it that way.
  Pinned notes are always content.
- **Manual breaths.** A note with the lyric `br` (or `sp`) is a breath: never pitched, taken
  from the bank's own `.inf` tail PCM (band-passed noise when the bank has none), mixed at
  the level MUTA uses (~-13 dB under the singing).
- **Portamento straddles the beat.** A pitch change glides from about 60-90 ms before the
  boundary to about 30 ms after it, in an S curve, instead of snapping up on the beat.
- **Seven automation lanes** (Energy, Timbre, Husky, Gender, Tension, Breathiness, Voicing),
  plus phoneme speed, output gain and a bank register offset in semitones.
- **Per-part language**, independent of timbre: one bank sings Chinese or Japanese; switching
  language changes only the front end and the labels.

**Rendering**

- **worldline** vocoder, R1.1 by default, R2 (external neural vocoder) optional.
- Blocks are planned as a pure function, cached per block, and re-rendered only where data
  changed; the analysis noise is seeded per block, so the same project renders the same
  waveform.
- **Failures are visible**: if the labels emit a different phone sequence than the front end
  predicted, the block is marked failed and the export is refused rather than shifted.

**Fixes that are ours, not MUTA's**

- A geminate's closure no longer eats the following note's vowel; the note after っ gets a
  shorter onset cap.
- A note is never cut at a barline (that used to make Sinsy sing it twice).
- The limiter leaves headroom for the host's 48 k→44.1 k resample (0.9 ceiling, not 0.95).
- The score reaches Sinsy in memory; no temporary files are written.

**Packaging**

- Two builds: **full** (with R2 + ONNX Runtime) and **lite** (worldline R1.1 only, ~4.9 MB
  vs 19.9 MB, no GPU driver surface).

## What this package is (and is not)

- **It is** the engine, three native libraries built from open source, and the Japanese
  assets that make Japanese singing self-contained: Sinsy's phoneme tables and the NIT
  SONG070 voice model (`sinsy/`), with their licence.
- **It is not** a MUTA voice bank, and it is not a copy of MUTA's engine: it is an
  independent implementation that reads MUTA's voice data. The `.voice/.inf/.bnk` files
  stay on your machine; the engine reads them out of an installed MUTA, or any folder you
  point it at. What ships for Japanese is the *front end* — Sinsy's tables and the NIT
  SONG070 model supply labels, durations and timing; the timbre still comes from your bank.
- **It contains no MUTA binaries.** No MUTA application and none of its libraries ship
  here. HTS, WORLD and Sinsy are compiled from source, and their notices travel with
  the package in `THIRD-PARTY-NOTICES.md`.

## Requirements

| Component | Requirement |
| --- | --- |
| TuneLab | 2.1.0 or newer (manifest `sdk-version` 1.1) |
| OS | Windows x64 |
| Voice data | a MUTA installation that has song voices |

## Build

```powershell
# TuneLab SDK assemblies are provided by the host, so point at an installation.
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir "C:\Program Files\TuneLab"
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

Output: `artifacts\mutafortunelab.tlx`. Drag it onto TuneLab, or install from the
Extensions sidebar.

Compile against the **lowest** TuneLab you support (2.1.0) — the manifest declares
`sdk-version` 1.1, so an accidental dependency on a newer API fails at compile time
instead of at load time on an older host. TuneLab 2.0.0 has no
`IVoiceSynthesisSession` at all, so a synthesis engine cannot run there.

## Test (headless, no UI)

TuneLab 2.1.1 and newer ship `TuneLab.Cli.exe`, which can install the package and
enumerate what it provides without a window:

```powershell
powershell -ExecutionPolicy Bypass -File tools\smoke.ps1
```

It installs the freshly packed `.tlx` into a throwaway data directory
(`TUNELAB_DATA_DIR`), then asserts that the engine loads, that a voice bank is found,
and that the declared parameter surface matches the spec.

## Layout

```text
src/MutaForTuneLab/     the plugin (net8.0)
  Assets/               .voice / .inf / .bnk access
  Native/               P/Invoke helpers
src/native/             C/C++ wrappers around HTS, worldline, Sinsy
third_party/            pinned upstream sources, data and patches (no binaries)
tools/                  build / pack / smoke
docs/roadmap.md         what works today and what is next
```

## Credits and external references

This project stands on other people's work. Thank you to:

| Project | Author / maintainer | Used for | Licence |
| --- | --- | --- | --- |
| [WORLDLINE R1.1/R2](https://github.com/stakira/OpenUtau) (`cpp/worldline`) | **StAkira** (OpenUtau) | the vocoder that turns our acoustic parameters into audio | MIT |
| [WORLD](https://github.com/mmorise/World) | Masanori Morise and contributors | spectral envelope / aperiodicity synthesis underneath worldline | modified BSD |
| [HTS — hts_engine API](http://hts-engine.sourceforge.net/) | HTS Working Group, Nagoya Institute of Technology | HMM acoustic model inference (durations, F0, mel-cepstrum) | modified BSD |
| [Sinsy](http://sinsy.sourceforge.net/) | Sinsy Working Group, Nagoya Institute of Technology | Japanese text → phonemes and full-context labels | modified BSD |
| HTS Voice "NIT SONG070 F001" 0.90 | Nagoya Institute of Technology, released by the Sinsy Working Group | Japanese labels, durations, F0 and envelope | **CC BY 3.0** |
| [TuneLab](https://github.com/LiuYunPlayer/TuneLab) | LiuYunPlayer and contributors | the host application and the plugin SDK | see upstream |

Full licence texts and per-component details: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

MUTA itself is not affiliated with this project; the extension reads voice data the
user already owns and ships none of it.

## Licence

See [LICENSE](LICENSE).
