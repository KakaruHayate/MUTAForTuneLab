"""Per-window level report for a rendered WAV: where the sound actually is."""
import sys
import wave

import numpy as np

path = sys.argv[1]
window = float(sys.argv[2]) if len(sys.argv) > 2 else 0.1

with wave.open(path, "rb") as wav:
    channels = wav.getnchannels()
    rate = wav.getframerate()
    frames = wav.getnframes()
    raw = wav.readframes(frames)

data = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
if channels > 1:
    data = data.reshape(-1, channels).mean(axis=1)

n = int(rate * window)
rows = []
for start in range(0, len(data), n):
    block = data[start:start + n]
    if len(block) == 0:
        continue
    rms = float(np.sqrt(np.mean(block ** 2)))
    peak = float(np.max(np.abs(block)))
    rows.append((start / rate, rms, peak))

print(f"{path}: {len(data)/rate:.3f}s @ {rate} Hz, {channels} ch, window {window*1000:.0f} ms")
for t, rms, peak in rows:
    bar = "#" * int(min(60, rms * 400))
    print(f"  {t:7.2f}s  rms {rms:.4f}  peak {peak:.3f}  {bar}")

voiced = [t for t, rms, _ in rows if rms > 0.005]
print(f"voiced windows: {len(voiced)} / {len(rows)}; "
      f"span {voiced[0]:.2f}..{voiced[-1]+window:.2f}s" if voiced else "silent")
