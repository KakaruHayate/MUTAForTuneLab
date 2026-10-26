"""Numerical regression for the native HTS engine, against a reference build.

Synthesises a fixed label set and folds every number the engine produces — all four
parameter streams plus the state durations — into one digest. Two builds that agree
on the digest produce the same parameters, which is what the C# side depends on.

The digest is compiler independent only if the arithmetic is: it is not (different
compilers may reassociate), so treat a digest difference as "look at the numbers",
not "broken". The script therefore also prints per-stream max|diff| when asked for a
reference dump.

Usage:
    python tests/reference/hts_regress.py --dll build/out/muta_hts.dll \
        --voice <voice.htsvoice> [--labels tests/fixtures/yueguang.labels] \
        [--compare <another.dll>]

Nothing here is TuneLab specific: it talks to the C ABI directly.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
from pathlib import Path

import numpy as np

STREAM_COUNT = 4


class MutaHts:
    def __init__(self, dll: Path, voice: Path) -> None:
        self.lib = ctypes.CDLL(str(dll))
        lib = self.lib
        lib.muta_hts_create.argtypes = [ctypes.c_char_p]
        lib.muta_hts_create.restype = ctypes.c_void_p
        lib.muta_hts_destroy.argtypes = [ctypes.c_void_p]
        lib.muta_hts_destroy.restype = None
        lib.muta_hts_synthesize.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_char_p), ctypes.c_int]
        lib.muta_hts_synthesize.restype = ctypes.c_int
        lib.muta_hts_total_frame.argtypes = [ctypes.c_void_p]
        lib.muta_hts_total_frame.restype = ctypes.c_int
        lib.muta_hts_total_state.argtypes = [ctypes.c_void_p]
        lib.muta_hts_total_state.restype = ctypes.c_int
        lib.muta_hts_num_streams.argtypes = [ctypes.c_void_p]
        lib.muta_hts_num_streams.restype = ctypes.c_int
        lib.muta_hts_vector_length.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.muta_hts_vector_length.restype = ctypes.c_int
        lib.muta_hts_state_duration.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.muta_hts_state_duration.restype = ctypes.c_int
        lib.muta_hts_get_stream.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_double)]
        lib.muta_hts_get_stream.restype = ctypes.c_int

        self.handle = lib.muta_hts_create(str(voice).encode())
        if not self.handle:
            raise RuntimeError(f"muta_hts_create failed: {voice}")

    def synthesize(self, labels: list[str]) -> tuple[list[np.ndarray], np.ndarray]:
        encoded = [line.encode("utf-8") for line in labels]
        array = (ctypes.c_char_p * len(encoded))(*encoded)
        frames = self.lib.muta_hts_synthesize(self.handle, array, len(encoded))
        if frames < 0:
            raise RuntimeError("synthesis failed")

        streams: list[np.ndarray] = []
        for stream in range(self.lib.muta_hts_num_streams(self.handle)):
            width = self.lib.muta_hts_vector_length(self.handle, stream)
            out = np.zeros(frames * width, dtype=np.float64)
            written = self.lib.muta_hts_get_stream(
                self.handle, stream, out.ctypes.data_as(ctypes.POINTER(ctypes.c_double)))
            if written != frames * width:
                raise RuntimeError(f"stream {stream}: wrote {written}, expected {frames * width}")
            streams.append(out.reshape(frames, width))

        states = self.lib.muta_hts_total_state(self.handle)
        durations = np.array([self.lib.muta_hts_state_duration(self.handle, s)
                              for s in range(states)], dtype=np.int32)
        return streams, durations

    def close(self) -> None:
        self.lib.muta_hts_destroy(self.handle)


def digest(streams: list[np.ndarray], durations: np.ndarray) -> str:
    h = hashlib.sha256()
    for values in streams:
        h.update(np.ascontiguousarray(values, dtype=np.float64).tobytes())
    h.update(np.ascontiguousarray(durations, dtype=np.int32).tobytes())
    return h.hexdigest().upper()


def main() -> int:
    here = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--voice", type=Path, required=True)
    parser.add_argument("--labels", type=Path, default=here / "fixtures" / "yueguang.labels")
    parser.add_argument("--compare", type=Path, default=None,
                        help="another build of the DLL to diff against element-wise")
    args = parser.parse_args()

    labels = [line for line in args.labels.read_text(encoding="utf-8").splitlines() if line.strip()]

    engine = MutaHts(args.dll, args.voice)
    streams, durations = engine.synthesize(labels)
    engine.close()

    print(f"labels {len(labels)}  frames {streams[0].shape[0]}  "
          f"streams {[tuple(s.shape) for s in streams]}  states {len(durations)}")
    print(f"sha256={digest(streams, durations)}")

    if args.compare is not None:
        other = MutaHts(args.compare, args.voice)
        streams_b, durations_b = other.synthesize(labels)
        other.close()
        print(f"compare: durations equal {np.array_equal(durations, durations_b)}")
        for i, (a, b) in enumerate(zip(streams, streams_b)):
            if a.shape != b.shape:
                print(f"  stream {i}: SHAPE MISMATCH {a.shape} vs {b.shape}")
                continue
            print(f"  stream {i}: max|diff| {np.max(np.abs(a - b)):.3e}  "
                  f"bit-identical {np.array_equal(a, b)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
