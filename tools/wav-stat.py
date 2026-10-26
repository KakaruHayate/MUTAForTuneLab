import json
import sys
import wave

import numpy as np

with wave.open(sys.argv[1], "rb") as w:
    channels, rate, frames = w.getnchannels(), w.getframerate(), w.getnframes()
    data = np.frombuffer(w.readframes(frames), dtype=np.int16).astype(np.float32) / 32768.0
if channels > 1:
    data = data.reshape(-1, channels).mean(axis=1)

rms = float(np.sqrt(np.mean(data ** 2))) if len(data) else 0.0
peak = float(np.max(np.abs(data))) if len(data) else 0.0
print(json.dumps({"seconds": len(data) / rate, "rms": rms, "peak": peak}))
