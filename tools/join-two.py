import sys
import wave

import numpy as np

out = sys.argv[1]
parts = []
rate = 44100
for path in sys.argv[2:]:
    with wave.open(path, "rb") as w:
        ch, r, n = w.getnchannels(), w.getframerate(), w.getnframes()
        d = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768.0
    if ch > 1:
        d = d.reshape(-1, ch).mean(axis=1)
    parts.append(d)
    rate = r
data = np.concatenate(parts)
pcm = np.repeat((np.clip(data, -1, 1) * 32767).astype(np.int16)[:, None], 2, axis=1)
with wave.open(out, "wb") as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(rate)
    w.writeframes(pcm.tobytes())
print(out)
