# MUTAForTuneLab

A TuneLab voice extension that brings MUTA's singing voices to TuneLab: Chinese and
Japanese lyrics, rendered through the **worldline** vocoder (R1.1 by default, R2 optional).

> Status: it sings. Both chains render through the host end to end, every declared
> parameter reaches the audio, and the noise is reproducible across renders. See
> [docs/roadmap.md](docs/roadmap.md) for what is left.

## What this package is (and is not)

- **It is** the engine, three native libraries built from open source, and the Japanese
  assets that make Japanese singing self-contained: Sinsy's phoneme tables and the NIT
  SONG070 voice model (`sinsy/`), with their licence.
- **It is not** a MUTA voice bank. The `.voice/.inf/.bnk` files stay on your machine;
  the engine reads them out of an installed MUTA, or any folder you point it at. The
  bundled Japanese model is the whole Japanese chain: labels, durations, F0 and its own
  MGC/BAP envelope. It is used for Japanese only and is never mixed into a Chinese voice.
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
