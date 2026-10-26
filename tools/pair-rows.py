import sys, wave
import numpy as np

def read(path):
    with wave.open(path, "rb") as w:
        ch, rate, n = w.getnchannels(), w.getframerate(), w.getnframes()
        d = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768.0
    if ch > 1:
        d = d.reshape(-1, ch).mean(axis=1)
    return d, rate

def resample(d, rate, target):
    if rate == target:
        return d
    count = int(round(len(d) * target / rate))
    return np.interp(np.arange(count) / target, np.arange(len(d)) / rate, d).astype(np.float32)

def write(path, d, rate):
    pcm = np.repeat((np.clip(d, -1, 1) * 32767).astype(np.int16)[:, None], 2, axis=1)
    with wave.open(path, "wb") as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(rate); w.writeframes(pcm.tobytes())

target = 44100
out = sys.argv[1]
files = sys.argv[2:]
gap = np.zeros(int(target * 0.6), dtype=np.float32)
parts = []
for i in range(0, len(files), 2):
    b, br = read(files[i]); b = resample(b, br, target)
    y, yr = read(files[i + 1]); y = resample(y, yr, target)
    parts += [b, gap, y, gap]
write(out, np.concatenate(parts), target)
print(out)