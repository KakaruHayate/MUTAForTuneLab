"""Pair each bank's gojuon with the same phrase sung by YOKO (native Sinsy)."""
import sys
import wave

import numpy as np


def read(path):
    with wave.open(path, "rb") as wav:
        channels, rate, frames = wav.getnchannels(), wav.getframerate(), wav.getnframes()
        data = np.frombuffer(wav.readframes(frames), dtype=np.int16).astype(np.float32) / 32768.0
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    return data, rate


def resample(data, rate, target):
    if rate == target:
        return data
    count = int(round(len(data) * target / rate))
    source = np.arange(len(data)) / rate
    wanted = np.arange(count) / target
    return np.interp(wanted, source, data).astype(np.float32)


def write(path, data, rate):
    pcm = np.clip(data, -1.0, 1.0)
    stereo = np.repeat((pcm * 32767).astype(np.int16)[:, None], 2, axis=1)
    with wave.open(path, "wb") as wav:
        wav.setnchannels(2)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(stereo.tobytes())


target = 44100
gap = np.zeros(int(target * 0.6), dtype=np.float32)
yoko, yoko_rate = read(sys.argv[1])
yoko = resample(yoko, yoko_rate, target)

for bank in sys.argv[2:]:
    data, rate = read(bank)
    data = resample(data, rate, target)
    write(bank.replace("-bank.wav", "-compare.wav"), np.concatenate([data, gap, yoko]), target)
    print(f"{bank} -> {bank.replace('-bank.wav', '-compare.wav')}")
