# MUTA for TuneLab

Sing with MUTA voice banks inside TuneLab.

- Chinese and Japanese from the same bank; language is a part setting.
- worldline R1.1 built in; R2 (external neural vocoder) optional in the full build.
- Pitch, vibrato, automation, pinned phonemes, per-note bank register offset and manual
  breaths are all driven by TuneLab's editor.
- No MUTA binaries or voice banks are bundled; the extension reads the `.voice`, `.inf`
  and `.bnk` files of your installed voices.
- Both index revisions are read: a MUTA 1.0 voice (its sample bank paired with a 2.0 model)
  installs as 嫣汐1.0 next to the 2.0 voice.

Full documentation, installation and R2 setup:
<https://github.com/KakaruHayate/MUTAForTuneLab>
