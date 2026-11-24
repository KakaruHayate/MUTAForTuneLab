# Changelog

## 0.2.0

**MUTA 1.0 voice banks.** The extension now reads the old index revision as well, so a
MUTA 1.0 voice can be sung in TuneLab.

- `.inf` revision 100 (MUTA 1.0 / 0.2.alpha) is parsed next to revision 200: a ten-int
  header line, nine fields per record, and unit names that are themselves the context
  (`b_a` syllable, `a-b` boundary, `a` bare final). Rows are mapped onto the same
  eleven-field view the 2.0 reader produces.
- `BankLayout` turns both revisions into "phone → units": a 1.0 recorded syllable supplies
  the consonant (its first segment) and the vowel onset, the long bare-final recordings
  supply the vowel body, and boundary takes are used when the previous phone matches —
  including coda aliases, so an `n`/`g` transition is reachable from `an`/`ang`/… finals.
- `tools/repack-muta10.ps1` builds an installable 1.0 voice: the 1.0 `.inf` as-is, the 1.0
  `.voice` (which is the sample bank, not a model) renamed to `.bnk`, a 2.0 acoustic model,
  and a GBK `.chr` that names it 嫣汐1.0. It writes the folder and a zip of it; the voice
  pack is distributed separately from the `.tlx`.
- Bilingual README sections for the 1.0 pack, `docs/technical.md` format notes, and a new
  `tests/UnitTests` case set that parses synthetic indexes of both revisions.

## 0.1.0

First public release: Chinese and Japanese singing from MUTA 2.0 voice banks, worldline
R1.1 (built in) and optional R2 (ONNX) vocoders, pinned phonemes, continuations, manual
breaths, per-note bank register offset, seven automation lanes, incremental rendering.
