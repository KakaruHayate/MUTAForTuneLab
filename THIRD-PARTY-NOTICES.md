# Third-party notices

MUTA for TuneLab bundles the components below. Each is redistributed unmodified except
where a patch is explicitly listed. The verbatim licence texts follow at the end.

Nothing here comes from MUTA itself: no MUTA executable, library or voice bank is
included or redistributed.

---

## Provenance of engine behaviour

This is an independent implementation. It reads MUTA's voice data through the formats those
files define (HTS `.voice`, plus the `.inf`/`.bnk` index), and reproduces the engine's
behaviour by **measuring** it: full-context label dumps, state durations, bank slot probes
and waveform analysis. No MUTA source code is used, and no MUTA binary, table or voice bank
is redistributed.

A few modules are deliberately behaviour-compatible with the original, because that
compatibility is what makes a MUTA bank sing at all:

| Module | Mirrors |
|---|---|
| `F0Mechanism` | per-note F0 assembly: trimmed mean, ±0.22 semitone band, onset glide |
| `BankAssembler` | three-part slot assembly, overlap and extension rules |
| `BankContext` | slot scoring by phone class, context, pitch and duration |
| `InfFile` | the byte permutation applied to `.inf` |
| `LabelBuilder` | the full-context label field layout |

These are reimplementations written from observed behaviour, not translations of source.
Everything else — host integration, blocking and caching, the vocoder pipeline, the
parameter layer, pinned phonemes, continuations, packaging — has no counterpart upstream.

---

## 1. Sinsy — Japanese text/phoneme front end

- Upstream: Sinsy 0.92, Sinsy Working Group, Nagoya Institute of Technology
  (<http://sinsy.sourceforge.net/>), release 2015-12-25.
- What we ship: compiled from source into `muta_sinsy.dll`, plus the phoneme tables
  in `sinsy/dic/`.
- Licence: Modified BSD (3-clause). Full text: `sinsy/LICENSE-sinsy.txt` and below.

Only the label-generation part is used: we drive Sinsy with an in-memory score and ask
it for phonemes and full-context labels. It never produces audio in our pipeline.

## 2. hts_engine API — HMM acoustic model inference

- Upstream: hts_engine API 1.10, HTS Working Group, Nagoya Institute of Technology
  (<http://hts-engine.sourceforge.net/>).
- What we ship: compiled from source into `muta_hts.dll`, with the patches documented
  in `third_party/hts_engine_API/README.md`.
- Licence: Modified BSD (3-clause), included in the Sinsy licence text below (Sinsy
  ships hts_engine API and reproduces its notice).

## 3. HTS Voice "NIT SONG070 F001" — Japanese singing voice

- File: `sinsy/voice/nitech_jp_song070_f001.htsvoice`
- Work title: **HTS Voice "NIT SONG070 F001" version 0.90** (release 2013-12-25)
- Author / licensor: **Nagoya Institute of Technology, Department of Computer Science**,
  released by the Sinsy Working Group (<http://sinsy.sourceforge.net/>)
- Origin: the official distribution `hts_voice_nitech_jp_song070_f001-0.90.tar.gz`;
  its `COPYING`, `README` and `INSTALL` are shipped unmodified next to the model.
- SHA-256: `25253E329DF090E0452C8F63D660346B3B1BDC27F429E1958A5162DDE018F7B2`
- **Licence: Creative Commons Attribution 3.0**
  (<https://creativecommons.org/licenses/by/3.0/>), not a BSD licence.

Attribution: *HTS Voice "NIT SONG070 F001" version 0.90, Copyright (c) 2009-2013 Nagoya
Institute of Technology, Department of Computer Science, released by the Sinsy Working
Group, licensed under CC BY 3.0. Trained on the Nitech Japanese Speech Database
"NIT SONG070 F001" (Copyright (c) 2004-2013 Nagoya Institute of Technology, HTS Working
Group, also CC BY 3.0).*

The model has not been modified. The full notice is in
[`sinsy/voice/COPYING`](third_party/sinsy/voice/COPYING) and reproduced at the end of
this file.

The model provides the whole Japanese chain: phoneme durations and F0 from its decision
trees, and the MGC/BAP envelope that gives sinsy-jp its timbre. It is used for Japanese
only -- the Chinese voices sing with MUTA's own model and bank, and this one is never
mixed into them.

## 4. worldline — the vocoder

- Upstream: `cpp/worldline` in the OpenUtau project, by **StAkira**
  (<https://github.com/stakira/OpenUtau>).
- What we ship: compiled from source into `worldline.dll` (added with the vocoder
  stage), together with its C API.
- Licence: MIT.

worldline is the piece that actually turns our acoustic parameters into sound; its
`WorldSynthesis` / `WorldSynthesisContinuousNoise` entry points are what the engine
calls. Upstream sources are used unmodified apart from the build wiring needed to link
them without Bazel.

## 5. WORLD — signal processing underneath worldline

- Upstream: WORLD, by Masanori Morise and contributors
  (<https://github.com/mmorise/World>), pinned to commit `f8dd5fb2`.
- What we ship: compiled into `worldline.dll`.
- Licence: modified BSD (no patent claims, per the upstream README).

Both notices will be reproduced here verbatim once the sources are pinned into
`third_party/`.

---

# Verbatim licence texts

## Sinsy and hts_engine API

Reproduced from `sinsy/LICENSE-sinsy.txt` (upstream `LICENSE.txt`).

```text
#######################################################################
# hts_engine API                                                      #
#######################################################################

/* ----------------------------------------------------------------- */
/*           The HMM-Based Speech Synthesis Engine "hts_engine API"  */
/*           developed by HTS Working Group                          */
/*           http://hts-engine.sourceforge.net/                      */
/* ----------------------------------------------------------------- */
/*                                                                   */
/*  Copyright (c) 2001-2015  Nagoya Institute of Technology          */
/*                           Department of Computer Science          */
/*                                                                   */
/*                2001-2008  Tokyo Institute of Technology           */
/*                           Interdisciplinary Graduate School of    */
/*                           Science and Engineering                 */
/*                                                                   */
/* All rights reserved.                                              */
/*                                                                   */
/* Redistribution and use in source and binary forms, with or        */
/* without modification, are permitted provided that the following   */
/* conditions are met:                                               */
/*                                                                   */
/* - Redistributions of source code must retain the above copyright  */
/*   notice, this list of conditions and the following disclaimer.   */
/* - Redistributions in binary form must reproduce the above         */
/*   copyright notice, this list of conditions and the following     */
/*   disclaimer in the documentation and/or other materials provided */
/*   with the distribution.                                          */
/* - Neither the name of the HTS working group nor the names of its  */
/*   contributors may be used to endorse or promote products derived */
/*   from this software without specific prior written permission.   */
/*                                                                   */
/* THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND            */
/* CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,       */
/* INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF          */
/* MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE          */
/* DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS */
/* BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,          */
/* EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED   */
/* TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,     */
/* DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON */
/* ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,   */
/* OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY    */
/* OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE           */
/* POSSIBILITY OF SUCH DAMAGE.                                       */
/* ----------------------------------------------------------------- */

#######################################################################
# Sinsy                                                               #
#######################################################################

/* ----------------------------------------------------------------- */
/*           The HMM-Based Singing Voice Synthesis System "Sinsy"    */
/*           developed by Sinsy Working Group                        */
/*           http://sinsy.sourceforge.net/                           */
/* ----------------------------------------------------------------- */
/*                                                                   */
/*  Copyright (c) 2009-2015  Nagoya Institute of Technology          */
/*                           Department of Computer Science          */
/*                                                                   */
/* All rights reserved.                                              */
/*                                                                   */
/* Redistribution and use in source and binary forms, with or        */
/* without modification, are permitted provided that the following   */
/* conditions are met:                                               */
/*                                                                   */
/* - Redistributions of source code must retain the above copyright  */
/*   notice, this list of conditions and the following disclaimer.   */
/* - Redistributions in binary form must reproduce the above         */
/*   copyright notice, this list of conditions and the following     */
/*   disclaimer in the documentation and/or other materials provided */
/*   with the distribution.                                          */
/* - Neither the name of the Sinsy working group nor the names of    */
/*   its contributors may be used to endorse or promote products     */
/*   derived from this software without specific prior written       */
/*   permission.                                                     */
/*                                                                   */
/* THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND            */
/* CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,       */
/* INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF          */
/* MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE          */
/* DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS */
/* BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,          */
/* EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED   */
/* TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,     */
/* DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON */
/* ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,   */
/* OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY    */
/* OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE           */
/* POSSIBILITY OF SUCH DAMAGE.                                       */
/* ----------------------------------------------------------------- */


```

## HTS Voice "NIT SONG070 F001"

Reproduced from `sinsy/voice/COPYING`, the file shipped by the official distribution.

```text
# ----------------------------------------------------------------- #
#           The Nitech Japanese Speech Database "NIT SONG070 F001"  #
#           released by HTS Working Group                           #
#           http://hts.sp.nitech.ac.jp/                             #
# ----------------------------------------------------------------- #
#                                                                   #
#  Copyright (c) 2004-2013  Nagoya Institute of Technology          #
#                           Department of Computer Science          #
#                                                                   #
# Some rights reserved.                                             #
#                                                                   #
# This work is licensed under the Creative Commons Attribution 3.0  #
# license.                                                          #
#                                                                   #
# You are free:                                                     #
#  * to Share - to copy, distribute and transmit the work           #
#  * to Remix - to adapt the work                                   #
# Under the following conditions:                                   #
#  * Attribution - You must attribute the work in the manner        #
#    specified by the author or licensor (but not in any way that   #
#    suggests that they endorse you or your use of the work).       #
# With the understanding that:                                      #
#  * Waiver - Any of the above conditions can be waived if you get  #
#    permission from the copyright holder.                          #
#  * Public Domain - Where the work or any of its elements is in    #
#    the public domain under applicable law, that status is in no   #
#    way affected by the license.                                   #
#  * Other Rights - In no way are any of the following rights       #
#    affected by the license:                                       #
#     - Your fair dealing or fair use rights, or other applicable   #
#       copyright exceptions and limitations;                       #
#     - The author's moral rights;                                  #
#     - Rights other persons may have either in the work itself or  #
#       in how the work is used, such as publicity or privacy       #
#       rights.                                                     #
#  * Notice - For any reuse or distribution, you must make clear to #
#    others the license terms of this work. The best way to do this #
#    is with a link to this web page.                               #
#                                                                   #
# See http://creativecommons.org/ for details.                      #
# ----------------------------------------------------------------- #

# ----------------------------------------------------------------- #
#           HTS Voice "NIT SONG070 F001"                            #
#           released by Sinsy Working Group                         #
#           http://sinsy.sourceforge.net/                           #
# ----------------------------------------------------------------- #
#                                                                   #
#  Copyright (c) 2009-2013  Nagoya Institute of Technology          #
#                           Department of Computer Science          #
#                                                                   #
# Some rights reserved.                                             #
#                                                                   #
# This work is licensed under the Creative Commons Attribution 3.0  #
# license.                                                          #
#                                                                   #
# You are free:                                                     #
#  * to Share - to copy, distribute and transmit the work           #
#  * to Remix - to adapt the work                                   #
# Under the following conditions:                                   #
#  * Attribution - You must attribute the work in the manner        #
#    specified by the author or licensor (but not in any way that   #
#    suggests that they endorse you or your use of the work).       #
# With the understanding that:                                      #
#  * Waiver - Any of the above conditions can be waived if you get  #
#    permission from the copyright holder.                          #
#  * Public Domain - Where the work or any of its elements is in    #
#    the public domain under applicable law, that status is in no   #
#    way affected by the license.                                   #
#  * Other Rights - In no way are any of the following rights       #
#    affected by the license:                                       #
#     - Your fair dealing or fair use rights, or other applicable   #
#       copyright exceptions and limitations;                       #
#     - The author's moral rights;                                  #
#     - Rights other persons may have either in the work itself or  #
#       in how the work is used, such as publicity or privacy       #
#       rights.                                                     #
#  * Notice - For any reuse or distribution, you must make clear to #
#    others the license terms of this work. The best way to do this #
#    is with a link to this web page.                               #
#                                                                   #
# See http://creativecommons.org/ for details.                      #
# ----------------------------------------------------------------- #

```

## ONNX Runtime

Bundled as `onnxruntime.dll`, `Microsoft.ML.OnnxRuntime.dll` and
`System.Numerics.Tensors.dll`; used only by the optional WORLDLINE R2 backend.

Copyright (c) Microsoft Corporation. All rights reserved.
Licensed under the MIT License.

Source: https://github.com/microsoft/onnxruntime

## mel.onnx

Bundled as `mel.onnx`. Maps WORLD features (F0, spectral envelope, aperiodicity) to a
log mel spectrogram for the WORLDLINE R2 backend.

From OpenUtau (`OpenUtau.Core/Classic/Data/mel.onnx`).
Copyright (c) OpenUtau contributors. Licensed under the MIT License.

Source: https://github.com/stakira/OpenUtau

## WORLDLINE R2 vocoder packages are not bundled

The R2 backend runs a vocoder the user installs. No model weights are distributed with
this plugin; the plugin only reads a folder the user points it at. The reference package
(`pc_nsf_hifigan_44.1k_hop512_128bin_2025.02`) is published by OpenVPI under
CC BY-NC-SA 4.0, which is not a licence this plugin can redistribute under.
