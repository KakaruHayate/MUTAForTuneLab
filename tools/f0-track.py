"""Rough F0 track over a time span: median pitch and its spread in semitones.

Autocorrelation on 46 ms frames, 10 ms hop, 60-1000 Hz. Enough to tell a vibrato
(half a semitone at 6 Hz) from a flat note, which is what it is for.
"""
import sys
import wave

import numpy as np

path = sys.argv[1]
start = float(sys.argv[2])
end = float(sys.argv[3])

with wave.open(path, "rb") as w:
    channels, rate, frames = w.getnchannels(), w.getframerate(), w.getnframes()
    data = np.frombuffer(w.readframes(frames), dtype=np.int16).astype(np.float32) / 32768.0
if channels > 1:
    data = data.reshape(-1, channels).mean(axis=1)

segment = data[int(start * rate):int(end * rate)]
window, hop = 2048, 441
lags = []

for i in range(0, max(0, len(segment) - window), hop):
    frame = segment[i:i + window]
    frame = frame - frame.mean()
    if np.sqrt((frame ** 2).mean()) < 0.004:
        continue
    corr = np.correlate(frame, frame, "full")[window - 1:]
    corr = corr / (corr[0] + 1e-9)
    low = int(rate / 1000)
    high = min(len(corr) - 1, int(rate / 60))
    if high <= low:
        continue
    lag = low + int(np.argmax(corr[low:high]))
    if corr[lag] < 0.3:
        continue
    lags.append(rate / lag)

if not lags:
    print("no voiced frames")
    sys.exit(1)

f0 = np.array(lags)
midi = 69 + 12 * np.log2(f0 / 440.0)
print(f"{path}: {len(f0)} frames, median {np.median(f0):.1f} Hz, "
      f"spread {np.std(midi):.3f} semitones, range {np.ptp(midi):.3f}")
