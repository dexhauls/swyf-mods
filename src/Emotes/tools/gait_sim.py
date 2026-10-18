"""Simulates walking in each walkable dance with the mod's stepping logic and measures how good it looks.

    python gait_sim.py <mixamo rig .fbx> <dances.bin> [name filter]

For every dance: the body walks straight ahead at the pace the model asks for; feet follow FootPlanting's rules
(pinned while the dance has them down, carried ahead while the dance lifts them, fallback step when left too far
behind). Reported per dance:
    pace     average walking speed in leg lengths per second
    reach    how often the pinned foot is out of the leg's reach (the leg would stretch / the foot would slide)
    drift    worst horizontal distance between a pinned foot and where the dance has it (leg lengths)
    extra    fallback steps per 10 s (steps the dance didn't make itself)
    still    share of time the dancer can't really walk (pace at the floor)
"""
import math
import sys

import gait_report as gr
import humanoid2dance as h

STANDALONE_DRIFT = 0.5   # body may travel this many leg lengths while a foot stands
HIP_DROP = 0.12
FALLBACK_GAP = 0.55
FALLBACK_TIME = 0.3


def natural_pace(d, legs, leg, cs):
    """Leg lengths/s the dance itself carries the body over a standing foot (its foot slides back on the ground)."""
    n, fps = d["frames"], d["fps"]
    out = []
    for f in range(n):
        speeds = []
        for i in range(2):
            if not cs[i][f] or not cs[i][(f - 1) % n] or not cs[i][(f + 1) % n]:
                continue
            z = lambda g: legs[i][1][g % n][2] - d["offs"][g % n][2]
            speeds.append(-(z(f + 1) - z(f - 1)) * fps / 2 / leg)
        out.append(max(0.0, sum(speeds) / len(speeds)) if speeds else None)
    return out


def slides(d, legs, leg, cs):
    n, fps = d["frames"], d["fps"]
    out = []
    for i in range(2):
        z = lambda g: legs[i][1][g % n][2] - d["offs"][g % n][2]
        v = [(-(z(f + 1) - z(f - 1)) * fps / 2 / leg) if cs[i][f] else 0.0 for f in range(n)]
        # average over each contact run: the steady part matters, not frame noise
        f = 0
        while f < n:
            g = f
            while g < n and cs[i][g] == cs[i][f]:
                g += 1
            if cs[i][f]:
                m = sum(v[f:g]) / (g - f)
                for k in range(f, g):
                    v[k] = max(-0.5, min(2.0, m))
            f = g
        out.append(v)
    return out


def fk_legs(sk, d):
    t = {b: (-v[0], v[1], v[2]) for b, v in sk.trans.items()}
    out = []
    for side in ("Left", "Right"):
        hip, ankle, low = [], [], []
        for f in range(d["frames"]):
            pos = d["offs"][f]
            rot = d["rots"]["mixamorig:Hips"][f]
            joints = {}
            for b in ("Hips", side + "UpLeg", side + "Leg", side + "Foot", side + "ToeBase"):
                if b != "Hips":
                    pos = tuple(p + q for p, q in zip(pos, gr.qrot(rot, t[b])))
                    rot = gr.qmul(rot, d["rots"]["mixamorig:" + b][f])
                joints[b] = pos
            hip.append(joints[side + "UpLeg"])
            ankle.append(joints[side + "Foot"])
            low.append(min(joints[side + "Foot"][1], joints[side + "ToeBase"][1]))
        out.append((hip, ankle, low))
    leg = math.dist((0, 0, 0), t["LeftLeg"]) + math.dist((0, 0, 0), t["LeftFoot"])
    return out, leg


def contacts(low, leg):
    srt = sorted(low)
    lo, hi = srt[int(len(srt) * 0.05)], srt[int(len(srt) * 0.95)]
    amp = max(hi - lo, leg * 0.03)
    enter, exit_ = lo + max(amp * 0.22, leg * 0.02), lo + max(amp * 0.38, leg * 0.04)
    on = low[0] < exit_
    c = []
    for v in low:
        if on and v > exit_:
            on = False
        elif not on and v < enter:
            on = True
        c.append(on)
    n = len(c)
    for _ in range(2):  # remove 1-2 frame blips
        f = 1
        while f < n - 1:
            run = 0
            while f + run < n and c[f + run] != c[f - 1]:
                run += 1
            if 0 < run <= 2 and f + run < n:
                for k in range(run):
                    c[f + k] = c[f - 1]
            f += max(1, run)
    return c


def runs(c):
    """For each frame: length of the contact run containing it (contact) or of the next contact run (air)."""
    n = len(c)
    res = [0] * n
    f = 0
    spans = []
    while f < n:
        g = f
        while g < n and c[g] == c[f]:
            g += 1
        spans.append((f, g, c[f]))
        f = g
    for i, (a, b, on) in enumerate(spans):
        if on:
            for k in range(a, b):
                res[k] = b - a
        else:
            nxt = spans[(i + 1) % len(spans)]
            ln = (nxt[1] - nxt[0]) if nxt[2] else 1
            for k in range(a, b):
                res[k] = ln
    return res


def air_fraction(c, f, win):
    n = len(c)
    s = sum(0 if c[(f + k) % n] else 1 for k in range(-win, win + 1))
    return s / (2 * win + 1)


def pace_cadence(cs, fps, leg):
    n = len(cs[0])
    win = int(fps * 0.9)
    lifts = [[f for f in range(1, n) if c[f - 1] and not c[f]] for c in cs]
    out = []
    for f in range(n):
        rate, air = [], []
        for side in range(2):
            cnt = sum(1 for l in lifts[side] if min(abs(l - f), n - abs(l - f)) <= win)
            rate.append(cnt / (2 * win / fps))
            air.append(air_fraction(cs[side], f, win))
        l0, l1 = air[0] < 0.6, air[1] < 0.6
        sps = 2 * min(rate) if l0 and l1 else rate[0] if l0 else rate[1] if l1 else max(rate)
        out.append(sps * 0.42)  # leg lengths per second
    return out


def cycles(c):
    """For each frame: length of the step cycle (contact run + following air run) it belongs to."""
    n = len(c)
    res = [n] * n
    lifts = [f for f in range(1, n) if c[f - 1] and not c[f]]
    if len(lifts) < 2:
        return res
    for a, b in zip(lifts, lifts[1:] + [lifts[0] + n]):
        for k in range(a, b):
            res[k % n] = b - a
    return res


def pace_stance(cs, fps, leg, lift=(0.2, 0.2), sl=None):
    """Body may move STANDALONE_DRIFT leg lengths while a carrying foot stands."""
    n = len(cs[0])
    win = int(fps * 0.9)
    st = [runs(c) for c in cs]
    cy = [cycles(c) for c in cs]
    out = []
    for f in range(n):
        lim = []
        for side in range(2):
            # a foot can only fly as far per step as the dance lifts it (tiny lifts = small steps)
            base = sl[side][f] if sl else 0.0   # what the dance itself already walks over this foot
            travel = min(0.6, lift[side] * 4.0)
            lim.append(max(0.0, base) + travel / max(cy[side][f] / fps, 1e-3))
            if cs[side][f]:
                lim.append(base + STANDALONE_DRIFT / max(st[side][f] / fps, 1e-3))
        out.append(min(lim))
    return out


def min_filter(v, w):
    n = len(v)
    return [min(v[(f + k) % n] for k in range(-w, w + 1)) for f in range(n)]


def smooth(v, w):
    n = len(v)
    return [sum(v[(f + k) % n] for k in range(-w, w + 1)) / (2 * w + 1) for f in range(n)]


def land_ahead(c, pace, fps, sl=None):
    """For air frames: half the distance (leg lengths) the body travels during the stance that follows."""
    n = len(c)
    out = [0.0] * n
    for f in range(n):
        if c[f]:
            continue
        g = f
        while not c[g % n] and g - f < n:
            g += 1
        dist = 0.0
        k = g
        while c[k % n] and k - g < n:
            dist += (pace[k % n] - (sl[k % n] if sl else 0.0)) / fps
            k += 1
        out[f] = dist * 0.5
    return out


SL = [None, None]


def simulate(d, legs, leg, cs, pace, loops=3):
    fps = d["fps"]
    n = d["frames"]
    dt = 1 / fps
    st = [runs(c) for c in cs]
    ahead = [land_ahead(c, pace, fps, SL[i]) for i, c in enumerate(cs)]
    body = 0.0
    vel = 0.0
    pins = [None, None]
    in_air = [False, False]
    lift_off = [0.0, 0.0]
    planted_for = [0.0, 0.0]
    air_for = [0.0, 0.0]
    stepping = [None, None]
    reach_bad = drift_max = 0.0
    extra = 0
    frames_total = 0
    still = 0
    for it in range(n * loops):
        f = it % n
        target_v = max(pace[f], 0.2 * 1.1 * 0.9 / leg * 0)  # floor handled in game; keep raw here
        vel += (target_v - vel) * min(1, dt * 12)
        body += vel * dt
        if pace[f] < 0.15:
            still += 1
        frames_total += 1
        for i in range(2):
            hip, ankle, _ = legs[i]
            anim_z = ankle[f][2] - d["offs"][f][2] + body
            hip_z = hip[f][2] - d["offs"][f][2] + body
            if stepping[i] is not None:
                a, tt = stepping[i]
                tt += dt / FALLBACK_TIME
                if tt >= 1:
                    stepping[i] = None
                    pins[i] = anim_z + vel * FALLBACK_TIME * 0.5
                    planted_for[i] = 0
                else:
                    stepping[i] = (a, tt)
                continue
            if not cs[i][f]:
                air_for[i] = air_for[i] + dt if in_air[i] else 0.0
                if not in_air[i]:
                    in_air[i] = True
                    lift_off[i] = (pins[i] - anim_z) if pins[i] is not None else 0.0
                pins[i] = None
                continue
            if pins[i] is None or in_air[i]:
                pins[i] = anim_z + (ahead[i][f - 1] if f > 0 else 0.0)
                in_air[i] = False
                planted_for[i] = 0
            planted_for[i] += dt
            gap = abs(anim_z - pins[i])
            drift_max = max(drift_max, gap / leg)
            # reach: horizontal + vertical distance hip->pin vs leg length
            dy = hip[f][1] - ankle[f][1]
            dz = pins[i] - hip_z
            # the hips may drop up to HIP_DROP leg lengths so the leg reaches the pinned foot
            need = dy - math.sqrt(max(0.0, (leg * 0.99) ** 2 - dz * dz)) if abs(dz) < leg * 0.99 else dy
            if need > HIP_DROP * leg:
                reach_bad += 1
            other = 1 - i
            busy = stepping[other] is not None or in_air[other]
            if gap > leg * FALLBACK_GAP and planted_for[i] > 0.25 and not busy:
                stepping[i] = (pins[i], 0.0)
                extra += 1
    secs = n * loops / fps
    return dict(pace=sum(pace) / n, reach=reach_bad / (2 * frames_total), drift=drift_max,
                extra=extra / secs * 10, still=still / frames_total)


def main():
    sk = h.Skeleton(sys.argv[1])
    flt = sys.argv[3].lower() if len(sys.argv) > 3 else ""
    walkable = ["dirtbike", "fast feet", "fishin", "forget me not", "freedom wheels", "gangnam", "griddy", "mufasa",
                "in da party", "move it", "roller vibes", "rushin", "side shuffle", "slide stride", "smeeze",
                "airplane", "walkin", "wake up", "wind up"]
    print("%-22s | %-34s | %-34s" % ("dance", "cadence model: pace reach drift extra", "stance model: pace reach drift extra"))
    for d in gr.read_dances(sys.argv[2]):
        name = d["label"].lower()
        if flt and flt not in name:
            continue
        if not flt and not any(w in name for w in walkable):
            continue
        legs, leg = fk_legs(sk, d)
        cs = [contacts(legs[i][2], leg) for i in range(2)]
        w = int(d["fps"] * 0.3)
        rows = []
        nat = natural_pace(d, legs, leg, cs)
        for model in (pace_cadence, pace_stance):
            lifts = []
            for i in range(2):
                srt = sorted(legs[i][2])
                lifts.append((srt[int(len(srt) * 0.95)] - srt[int(len(srt) * 0.05)]) / leg)
            sl = slides(d, legs, leg, cs)
            raw = model(cs, d["fps"], leg) if model is pace_cadence else model(cs, d["fps"], leg, lifts, sl)

            p = smooth(raw, w) if model is pace_cadence else smooth(min_filter(raw, int(d["fps"] * 0.3)), int(d["fps"] * 0.15))
            p = [min(x, 2.6) for x in p]
            SL[0], SL[1] = (sl[0], sl[1]) if model is pace_stance else (None, None)
            r = simulate(d, legs, leg, cs, p)
            rows.append("%5.2f %5.0f%% %5.2f %5.1f" % (r["pace"], r["reach"] * 100, r["drift"], r["extra"]))
        print("%-22s | %-34s | %-34s" % (d["label"], rows[0], rows[1]))


if __name__ == "__main__":
    main()
