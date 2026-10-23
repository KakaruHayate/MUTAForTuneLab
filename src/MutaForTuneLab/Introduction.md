# MUTA for TuneLab

MUTA singing-voice engine for TuneLab. Chinese and Japanese synthesis chains, rendered
through the WORLD / worldline vocoder.

**This package contains code only.** It does not ship any MUTA binary and does not ship
voice banks: it reads the `.voice/.inf/.bnk` files from a MUTA installation already present
on your machine. Install MUTA first, or point the engine at a folder that holds the voice
folders (Settings → Extensions → MUTA → Extra voice directories).

## Voices

A voice is a folder with `<name>.voice`, `<name>.inf` and `<name>.bnk`. The default search
path is `Program Files (x86)\Muta\voice\song\*\`.

## Parameters

Editable curves (per part, over time): Energy, Timbre, Husky (these act on the acoustic
model output), and Gender, Tension, Breathiness, Voicing (passed straight to the vocoder).
Pitch and vibrato use TuneLab's own pitch and deviation channels.

Part settings include the sample region switch (which recorded pitch of the bank a note is
built from) and the vocoder mode.

## Status

Early. The engine currently enumerates voices and declares its parameter surface; synthesis
is being ported stage by stage. See the repository README for the roadmap.

## Credits

The voices are the user's own; nothing of MUTA ships here. The synthesis stack is built on
other people's work: worldline by StAkira (MIT), WORLD by Masanori Morise (modified BSD),
the HTS engine and Sinsy by Nagoya Institute of Technology (modified BSD), and
HTS Voice "NIT SONG070 F001" by Nagoya Institute of Technology (CC BY 3.0). Full notices
ship alongside this file in THIRD-PARTY-NOTICES.md.
