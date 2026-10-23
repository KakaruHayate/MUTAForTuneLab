# Roadmap

What works today, and what is next. No dates — this is a spare-time port.

## Now

- [x] Extension package loads in TuneLab (V1 voice entry, `sdk-version` 1.0).
- [x] Voice discovery: finds the MUTA voice folders on the machine, reads their
      display names, never ships or copies the voice data.
- [x] Parameter surface: 7 editable curves, 7 part settings (including the sample-region
      switch), 3 note settings, per-phoneme schema.
- [x] Headless acceptance (`tools/smoke.ps1`) covering all of the above.

## Next

1. **Acoustic model inference** — drive the HMM engine from our full-context labels and
   read back state durations, F0 and mel-cepstrum.
2. **Text front ends** — Chinese: characters → pinyin → phones; Japanese: an in-process
   label front end driven by an in-memory score.
3. **Bank assembly** — pick the recorded unit for each phoneme and stitch the acoustic
   parameters into one timeline.
4. **Vocoder** — WORLD/worldline: our parameters → spectrum + aperiodicity → audio.
5. **Session behaviour** — chunking at rests, dirty tracking, incremental re-synthesis,
   audio segments and reportable phonemes.
6. **Wire the parameters up** — the three model-level curves and the four vocoder curves.
7. **Package** — the three native libraries from pinned sources, and the third-party
   notices that go with them.

## Known issues

- **Timbre is expensive when it moves.** The timbre control is a warping factor applied
  per frame; naively that means a full spectral evaluation per frame, so a fast sweep
  costs far more than a held value. The plan is to quantise the factor into a handful of
  buckets per chunk and evaluate the spectrum with an FFT-based transform instead of a
  dense cosine sum, so a curve costs roughly the same as a constant.
- Japanese reuses the MUTA bank, so the articulation is approximate by construction: the
  language timing and F0 are Japanese, the sound is the Chinese bank.
