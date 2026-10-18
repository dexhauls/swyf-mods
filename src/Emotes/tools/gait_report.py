"""Offline version of GaitTrack: prints, for each dance, when each foot is on the ground ('_') or in the air ('^').

    python gait_report.py <mixamo rig .fbx> <dances.bin> [name filter]

Uses the Mixamo skeleton from the FBX (Unity axes) and the per-bone rotations from the dance file, so the result
matches what the mod computes in game up to the character's proportions.
"""
import math
import struct
import sys

import humanoid2dance as h


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def read_dances(path):
    data = open(path, "rb").read()
    assert data[:4] == b"SWYD"
    version, count = struct.unpack_from("<II", data, 4)
    pos = 12

    def s():
        nonlocal pos
        n = struct.unpack_from("<H", data, pos)[0]
        pos += 2
        out = data[pos:pos + n].decode("utf-8")
        pos += n
        return out
    for _ in range(count):
        did, label = s(), s()
        fps, frames, leg, flags = struct.unpack_from("<fIfI", data, pos)
        pos += 16
        loop_start = 0
        if version >= 2:
            loop_start = struct.unpack_from("<I", data, pos)[0]
            pos += 4
        nb = struct.unpack_from("<I", data, pos)[0]
        pos += 4
        rots = {}
        for _ in range(nb):
            name = s()
            vals = struct.unpack_from("<%df" % (frames * 4), data, pos)
            pos += frames * 16
            rots[name] = [vals[i * 4:i * 4 + 4] for i in range(frames)]
        offs = struct.unpack_from("<%df" % (frames * 3), data, pos)
        pos += frames * 12
        yield dict(id=did, label=label, fps=fps, frames=frames, loop=loop_start, rots=rots,
                   offs=[offs[i * 3:i * 3 + 3] for i in range(frames)])


def analyse(sk, d):
    t = {b: (-v[0], v[1], v[2]) for b, v in sk.trans.items()}  # Unity axes
    legs = []
    for side in ("Left", "Right"):
        chain = ["Hips", side + "UpLeg", side + "Leg", side + "Foot", side + "ToeBase"]
        heights = []
        for f in range(d["frames"]):
            pos = d["offs"][f]
            rot = d["rots"]["mixamorig:Hips"][f]
            low = 1e9
            for b in chain[1:]:
                pos = tuple(p + q for p, q in zip(pos, qrot(rot, t[b])))
                rot = qmul(rot, d["rots"]["mixamorig:" + b][f])
                if b in (side + "Foot", side + "ToeBase"):
                    low = min(low, pos[1])
            heights.append(low)
        legs.append(heights)
    leg_len = math.dist((0, 0, 0), t["LeftLeg"]) + math.dist((0, 0, 0), t["LeftFoot"])
    out = []
    for hgt in legs:
        srt = sorted(hgt)
        lo, hi = srt[int(len(srt) * 0.05)], srt[int(len(srt) * 0.95)]
        amp = max(hi - lo, leg_len * 0.03)
        enter, exit_ = lo + max(amp * 0.22, leg_len * 0.02), lo + max(amp * 0.38, leg_len * 0.04)
        on = hgt[0] < exit_
        c = []
        for v in hgt:
            if on and v > exit_:
                on = False
            elif not on and v < enter:
                on = True
            c.append(on)
        out.append((c, amp / leg_len))
    return out


def main():
    sk = h.Skeleton(sys.argv[1])
    flt = sys.argv[3].lower() if len(sys.argv) > 3 else ""
    for d in read_dances(sys.argv[2]):
        if flt and flt not in d["label"].lower():
            continue
        res = analyse(sk, d)
        print("%s  (%d frames, loop from %d, %.0f fps)" % (d["label"], d["frames"], d["loop"], d["fps"]))
        for side, (c, amp) in zip("LR", res):
            line = "".join("_" if c[i] else "^" for i in range(0, len(c), 2))
            print("  %s lift %.2f leg  %s" % (side, amp, line))


if __name__ == "__main__":
    main()
