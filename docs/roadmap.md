# Roadmap

What works today, and what is next. No dates — this is a spare-time port.

## Now

- [x] **Extension package** loads in TuneLab 2.1.0+ (V1 voice entry, `sdk-version` 1.1).
      TuneLab 2.0.0 has no `IVoiceSynthesisSession`, so a synthesis engine cannot run there.
- [x] **Voice discovery**: finds the MUTA song voices on the machine and reads their names.
      Nothing of MUTA ships: the `.voice/.inf/.bnk` files stay where the user installed them.
- [x] **Chinese chain**: characters → pinyin → full-context labels → HTS → bank assembly →
      worldline. The recorded layer replaces the model's parameters outright, as the original
      does; no level or tone matching is applied between them.
- [x] **Japanese chain**: kana → Sinsy labels → the bundled NIT SONG070 model, whose own
      MGC/BAP gives the voice its timbre. Self-contained; nothing else needs installing.
- [x] **Vocoder**: worldline R1.1 continuous noise by default, R2 as an optional tier
      (mel.onnx ships, the vocoder weights are the user's own). DirectML is used when the
      driver supports it, with a CPU fallback.
- [x] **Parameters**: energy, timbre and husky act on the model output; gender, tension,
      breathiness and voicing go to the vocoder; the sample-region switch
      (`auto`/`offset`/`fixed`), phoneme speed, output gain and vocoder tier are part settings.
      An untouched project renders bit-identically to one without the curves.
- [x] **Session behaviour**: blocks cut in the silence between phrases, incremental
      re-synthesis driven by a note-and-curve signature, phoneme geometry resolved by the
      host's own layout, pitch readback, deterministic noise per block.
- [x] **Legato**: `-`/`+` are melisma passengers -- no new slot, the head vowel covers the
      hold in labels, bank, phoneme geometry and readback.
- [x] **Portamento**: pitch changes glide across the beat (lead 90 ms, tail 30 ms) with the
      carrier's own micro-shape kept.
- [x] **Manual breath**: `br`/`sp` notes, `.inf` finals or band-passed noise, mixed at
      MUTA's relative level.
- [x] **Lyric whitelist**: only syllables the voice actually has reach the native engine;
      anything else is replaced with a vowel it can sing, with one log line per replacement.
      The engine does not validate, and a phone it cannot place takes the process down.
- [x] **Headless acceptance** (`tools/smoke.ps1`) and a windowed real-project runner
      (`tools/ustx-to-script.py`).

## Next

1. **R2 in the packaged path.** The tier works when driven directly; it has not been
   exercised through the host with a real vocoder model installed.
2. **Two full songs, end to end.** One Chinese (259 notes, 116 s) and one Japanese
   (540 notes, 228 s) render window by window with no failures; a single-pass run needs the
   host's per-call dispatch budget to be larger than the sandbox allows.
3. **Stretch weight and note properties.** The three declared note settings are read but do
   not change the render yet.
4. **Bank level.** The recorded layer sits about 12 dB below the model layer, exactly as the
   original does. Users who want them matched can raise `output_gain_db`; a proper
   calibration against the original vocoder is recorded in the plan as a later enhancement.

## Known issues

- **Timbre is expensive when it moves.** The control is a warping factor read per frame.
  Frames are bucketed by quantised factor, so a slow curve costs little, but a fast sweep
  still evaluates many distinct spectra.
- **Japanese consonant length barely follows speed.** The onset takes the model's duration
  and the vowel fills the rest of the note, so speeding a phrase shortens consonants only
  slightly. That is the data, not the wiring.
- **Long projects are tested in windows.** The headless sandbox allows roughly sixty
  dispatches per call, which covers about forty seconds of audio at these block sizes.
- **Voice switches used to crash in native HTS.** The host disposes the synthesis context
  before the old session's render finishes; test8 cancels and waits for those renders and
  refuses to publish into a disposed context. A native access violation after a switch is
  still unproven at the instruction level; a LocalDumps capture is the next step if it
  recurs.
- **Polyphones follow the host's G2P.** The host hands the engine the first pronunciation of
  a Han character (地 → de), so 原地 comes out as "yuan de" by default. A per-note
  `pronunciation` override now works; automatic word-context disambiguation needs the Han
  lyrics, which only arrive when the host's auto-pronunciation is off.
- **Level and timbre still differ from MUTA.** The recorded layer sits 2-3 dB under the
  reference and its harmonic balance is different (measured, see
  `docs/behavior-comparison.md` §5.1). The continuous-noise backend matches WORLD's own
  synthesis in level, so this is not a missing output gain; it is worldline vs the original
  STRAIGHT-family vocoder (and selection differences). No compensation is applied.
