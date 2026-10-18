"""Reads Unity .anim (text YAML) humanoid clips: muscle and root curves sampled at a fixed rate."""
import re

_ATTR = re.compile(r"^\s+attribute: (.*)$")
_KEY_TIME = re.compile(r"^\s+time: ([-\d.eE+]+)")
_KEY_VALUE = re.compile(r"^\s+value: ([-\d.eE+]+)")
_SLOPE = re.compile(r"^\s+(inSlope|outSlope): ([-\d.eE+]+|Infinity|-Infinity)")


def _num(s):
    if s == "Infinity":
        return float("inf")
    if s == "-Infinity":
        return float("-inf")
    return float(s)


def read_anim(path):
    """Returns (name, sample_rate, stop_time, loop, {attribute: [(time, value, inSlope, outSlope), ...]})."""
    name, rate, stop, loop = None, 30.0, 0.0, False
    curves = {}
    keys = []
    key = None
    in_float_curves = False
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            s = line.strip()
            if s.startswith("m_Name:") and name is None:
                name = s.split(":", 1)[1].strip()
            elif s.startswith("m_FloatCurves:"):
                in_float_curves = True
            elif s.startswith("m_PPtrCurves:") or s.startswith("m_SampleRate:") or s.startswith("m_ClipBindingConstant:"):
                in_float_curves = False
                if s.startswith("m_SampleRate:"):
                    rate = float(s.split(":", 1)[1])
            elif s.startswith("m_StopTime:"):
                stop = float(s.split(":", 1)[1])
            elif s.startswith("m_LoopTime:"):
                loop = s.split(":", 1)[1].strip() == "1"
            if not in_float_curves:
                continue
            m = _KEY_TIME.match(line)
            if m:
                key = [float(m.group(1)), 0.0, 0.0, 0.0]
                keys.append(key)
                continue
            m = _KEY_VALUE.match(line)
            if m and key is not None:
                key[1] = float(m.group(1))
                continue
            m = _SLOPE.match(line)
            if m and key is not None:
                key[2 if m.group(1) == "inSlope" else 3] = _num(m.group(2))
                continue
            m = _ATTR.match(line)
            if m:
                curves[m.group(1).strip()] = keys
                keys = []
                key = None
    return name, rate, stop, loop, curves


def evaluate(keys, t):
    """Hermite evaluation like Unity's AnimationCurve (stepped where a slope is infinite)."""
    if not keys:
        return 0.0
    if t <= keys[0][0]:
        return keys[0][1]
    if t >= keys[-1][0]:
        return keys[-1][1]
    lo, hi = 0, len(keys) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if keys[mid][0] <= t:
            lo = mid
        else:
            hi = mid
    t0, v0, _, out0 = keys[lo]
    t1, v1, in1, _ = keys[hi]
    dt = t1 - t0
    if dt <= 0:
        return v0
    if out0 in (float("inf"), float("-inf")) or in1 in (float("inf"), float("-inf")):
        return v0
    u = (t - t0) / dt
    u2, u3 = u * u, u * u * u
    h00 = 2 * u3 - 3 * u2 + 1
    h10 = u3 - 2 * u2 + u
    h01 = -2 * u3 + 3 * u2
    h11 = u3 - u2
    return h00 * v0 + h10 * dt * out0 + h01 * v1 + h11 * dt * in1


def sample(curves, rate, stop):
    frames = max(1, int(round(stop * rate)) + 1)
    out = []
    for f in range(frames):
        t = f / rate
        out.append({a: evaluate(k, t) for a, k in curves.items()})
    return out
