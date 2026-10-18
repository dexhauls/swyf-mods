"""Finds a dance music loop's tempo and where its beats fall (what DanceMusic.Beats does in game).

    python beat_detect.py <loop.wav|ogg> [...]

The loop holds a whole number of bars, so only tempos with a multiple of 4 beats per loop (70-180 BPM) are tried;
the one whose beat grid lines up best with the drum hits wins.
"""
import struct
import sys
import wave


def load(path):
    w = wave.open(path)
    sr, ch, sw, n = w.getframerate(), w.getnchannels(), w.getsampwidth(), w.getnframes()
    data = struct.unpack("<%d%s" % (n * ch, {2: "h", 4: "i"}[sw]), w.readframes(n))
    return sr, [sum(data[i * ch:(i + 1) * ch]) / ch for i in range(n)]


def detect(sr, x):
    rate = 100
    hop = sr // rate
    hp = [x[i] - 2 * x[i - 1] + x[i - 2] for i in range(2, len(x))]
    env = [sum(abs(v) for v in hp[i:i + hop]) / hop for i in range(0, len(hp) - hop, hop)]
    flux = [max(0.0, env[i] - env[i - 1]) for i in range(1, len(env))]
    m = len(flux)
    length = len(x) / sr
    best = None
    for beats in range(4, int(180 * length / 60) + 5, 4):
        bpm = beats * 60 / length
        if not 70 <= bpm <= 180:
            continue
        period = m / beats
        for ph in range(int(period)):
            s = 0.0
            for k in range(beats):
                i = int(round(ph + k * period)) % m
                s += max(flux[(i + d) % m] for d in (-1, 0, 1))
            score = s / beats * (1.0 if 85 <= bpm <= 150 else 0.85)
            if best is None or score > best[0]:
                best = (score, bpm, (ph + 1) / rate, beats)
    return best, length


if __name__ == "__main__":
    for p in sys.argv[1:]:
        sr, x = load(p)
        (score, bpm, offset, beats), length = detect(sr, x)
        print("%-60s %5.1f s  %3d beats  %6.1f BPM  first beat %.2f s" % (p[-60:], length, beats, bpm, offset))
