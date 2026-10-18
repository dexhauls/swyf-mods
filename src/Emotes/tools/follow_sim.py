"""Simulates FollowDance walking (DanceTuning.FollowDance) for each walkable dance and reports how well it fits.

    python follow_sim.py <mixamo rig .fbx> <dances.bin> [name filter]

Body speed = the dance's own push (planted foot dragged back), coasting between pushes (GaitTrack.BuildScrapePace).
A planted foot follows the dance backwards but never forwards. Reported per dance:
    avg/min/max   walking speed, leg lengths per second
    gap           worst / average distance between a planted foot and where the dance has it (leg lengths)
    scrape        how fast planted feet slide back on the floor on average (the dance pushing faster than the body)
    stuck         share of planted time a foot is held back (the dance moves it forward while it stands)
    hips          up/down hip motion of the dance (leg lengths)
"""
import math
import sys

import gait_report as gr
import gait_sim as gs
import humanoid2dance as h

COAST = 0.25


def scrape_pace(d, legs, leg, cs):
    n, fps = d["frames"], d["fps"]
    loop = d["loop"]

    def z(i, q):
        q = min(max(q, 0), n - 1)
        return (legs[i][1][q][2] - d["offs"][q][2]) / leg
    raw = [-1.0] * n
    for f in range(1, n - 1):
        for i in range(2):
            if cs[i][f] and cs[i][f - 1] and cs[i][f + 1]:
                raw[f] = max(raw[f], max(0.0, -(z(i, f + 1) - z(i, f - 1)) * fps / 2))
    decay = math.exp(-1 / (fps * COAST))
    v, filled = 0.0, [0.0] * n
    for p in range(2):
        for f in range(0 if p == 0 else loop, n):
            v = raw[f] if raw[f] >= 0 else v * decay
            filled[f] = v
    ln = max(1, n - loop)

    def wrap(g):
        return 0 if g < 0 else g if g < n else loop + (g - loop) % ln
    return [min(4.0, sum(filled[wrap(f + k)] for k in range(-2, 3)) / 5) for f in range(n)], raw


def simulate(d, legs, leg, cs, pace, loops=3, slide_forward=True):
    """Planted foot: the dance's backward drag keeps it still (the body walks over it); where the dance slides it
    forward it slides forward just as far as standing (slide_forward); it never ends up ahead of the dance."""
    n, fps = d["frames"], d["fps"]
    dt = 1 / fps
    body = 0.0
    pins = [None, None]
    last_rel = [None, None]
    gaps = []
    extra = 0.0   # foot motion on the floor beyond what the dance does standing (leg lengths)
    for it in range(n * loops):
        f = it % n
        body += pace[f] * leg * dt
        for i in range(2):
            rel = legs[i][1][f][2] - d["offs"][f][2]
            anim = rel + body
            if not cs[i][f]:
                pins[i] = None
                last_rel[i] = rel
                continue
            if pins[i] is None:
                pins[i] = anim
            else:
                drel = rel - last_rel[i]
                if slide_forward and drel > 0:
                    pins[i] += drel
                if anim < pins[i]:          # body slower than the drag: the foot scrapes back like standing
                    pins[i] = anim
            last_rel[i] = rel
            gaps.append(abs(anim - pins[i]) / leg)
    return dict(gap=max(gaps) if gaps else 0, gapavg=sum(gaps) / len(gaps) if gaps else 0, scrape=0, stuck=0)


def main():
    sk = h.Skeleton(sys.argv[1])
    flt = sys.argv[3].lower() if len(sys.argv) > 3 else ""
    walkable = ["dirtbike", "fast feet", "fishin", "forget me not", "freedom wheels", "gangnam", "griddy", "mufasa",
                "in da party", "move it", "roller vibes", "rushin", "side shuffle", "slide stride", "smeeze",
                "airplane", "walkin", "wake up", "wind up", "zombie", "mashed", "chicken", "shuffle", "maraschino"]
    print("%-22s %5s %5s %5s | %5s %5s | %6s %5s | %5s" % ("dance", "avg", "min", "max", "gap", "gapav", "scrape", "stuck", "hips"))
    for path in sys.argv[2].split(";"):
        for d in gr.read_dances(path):
            name = d["label"].lower()
            if flt and flt not in name:
                continue
            if not flt and not any(w in name for w in walkable):
                continue
            legs, leg = gs.fk_legs(sk, d)
            cs = [gs.contacts(legs[i][2], leg) for i in range(2)]
            pace, _ = scrape_pace(d, legs, leg, cs)
            loop = d["loop"]
            lp = pace[loop:] or pace
            r = simulate(d, legs, leg, cs, pace)
            ys = [o[1] for o in d["offs"]]
            print("%-22s %5.2f %5.2f %5.2f | %5.2f %5.2f | %6.2f %4.0f%% | %5.2f" % (
                d["label"], sum(lp) / len(lp), min(lp), max(lp), r["gap"], r["gapavg"], r["scrape"], r["stuck"] * 100,
                (max(ys) - min(ys)) / leg))


if __name__ == "__main__":
    main()
