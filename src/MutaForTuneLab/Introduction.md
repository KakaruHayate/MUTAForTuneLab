# MUTA for TuneLab

MUTA singing-voice engine for TuneLab. Chinese and Japanese synthesis chains, rendered
through the **worldline** vocoder (R1.1 continuous noise by default; R2 is optional and
needs a vocoder model you supply yourself).

**This package contains code only.** It does not ship any MUTA binary and does not ship
MUTA voice banks: it reads the `.voice/.inf/.bnk` files from a MUTA installation already
present on your machine. Install MUTA first, or point the engine at a folder that holds the
voice folders (Settings → Extensions → MUTA → Extra voice directories).

Japanese is self-contained: Sinsy's phoneme tables and the HTS Voice "NIT SONG070 F001"
model ship with the package, and the `sinsy-jp` voice sings with **that model's own
timbre**, never mixed into a Chinese voice.

## Voices

A Chinese voice is a folder with `<name>.voice`, `<name>.inf` and `<name>.bnk`. The default
search path is `Program Files (x86)\Muta\voice\song\*\`.

The Japanese voice `sinsy-jp` ships with the package and needs no extra files.

## Parameters

Editable curves (per part, over time): Energy, Timbre, Husky (these act on the acoustic
model output), and Gender, Tension, Breathiness, Voicing (passed straight to the vocoder).
Pitch and vibrato use TuneLab's own pitch and deviation channels.

Part settings include the sample-region switch — `auto` follows each note, `offset` shifts
it in semitones, `fixed` pins one region — which chooses the recorded register a note is
built from, plus the vocoder mode, phoneme speed and output gain.

## Status

Both chains sing end to end, every declared parameter reaches the audio, and the same
project renders the same waveform again.

## Credits

The voices are the user's own; nothing of MUTA ships here. The synthesis stack is built on
other people's work: worldline by StAkira (MIT), WORLD by Masanori Morise (modified BSD),
the HTS engine and Sinsy by Nagoya Institute of Technology (modified BSD), and
HTS Voice "NIT SONG070 F001" by Nagoya Institute of Technology (CC BY 3.0). Full notices
ship alongside this file in THIRD-PARTY-NOTICES.md.
