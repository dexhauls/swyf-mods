"""Approximate Unity humanoid (muscle) clips -> Mixamo bone rotations, without Unity.

Unity stores humanoid animation as "muscles": per bone, swing/twist amounts in -1..1 scaled by angle limits,
relative to an internal neutral pose. This rebuilds that on the Mixamo skeleton (taken from any Mixamo FBX):
each muscle swings its bone toward a body direction (front, up, side) or twists it around itself. Unity's exact
internal frames aren't public, so the few uncertain choices (signs, the neutral arm angle) are picked by matching
the hand and foot positions the clip also stores (IK goals) - see calibrate().

    python humanoid2dance.py <mixamo rig .fbx> <out dances.bin> <clip.anim> [<clip.anim> ...]
"""
import math
import os
import struct
import sys

import mixamo2dance as mx
import unityanim as ua

# ---------------------------------------------------------------- small linear algebra (3x3 rows, FBX space)

def vadd(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def vsub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def vscale(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    l = math.sqrt(dot(a, a))
    return (a[0] / l, a[1] / l, a[2] / l) if l > 1e-9 else (0.0, 0.0, 0.0)
def mv(m, v): return tuple(m[i][0] * v[0] + m[i][1] * v[1] + m[i][2] * v[2] for i in range(3))
def mm(a, b): return mx.mul(a, b)
def mt(a): return mx.transpose(a)

I3 = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]


def axis_angle(axis, deg):
    x, y, z = norm(axis)
    if x == y == z == 0:
        return I3
    a = math.radians(deg)
    c, s, t = math.cos(a), math.sin(a), 1 - math.cos(a)
    return [[t * x * x + c, t * x * y - s * z, t * x * z + s * y],
            [t * x * y + s * z, t * y * y + c, t * y * z - s * x],
            [t * x * z - s * y, t * y * z + s * x, t * z * z + c]]


def quat_to_mat(q):
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1
    x, y, z, w = x / n, y / n, z / n, w / n
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def unity_to_fbx_q(q):  # mirror X: left-handed Unity <-> right-handed FBX
    return (q[0], -q[1], -q[2], q[3])


def unity_to_fbx_v(v):
    return (-v[0], v[1], v[2])


# FBX space for a Mixamo character: +X = the character's left, +Y up, +Z forward.
UP, FRONT, LEFT = (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)
BACK, DOWN, RIGHT = (0.0, 0.0, -1.0), (0.0, -1.0, 0.0), (-1.0, 0.0, 0.0)

# ---------------------------------------------------------------- rest skeleton


class Skeleton:
    def __init__(self, fbx_path):
        fbx = mx.read_fbx(fbx_path)
        objects, conns = fbx.find("Objects"), fbx.find("Connections")
        settings = mx.props70(fbx.find("GlobalSettings"))
        self.unit = float(settings.get("UnitScaleFactor", [1.0])[0]) / 100.0  # -> metres
        models, parent = {}, {}
        for o in objects.children:
            if o.name == "Model":
                models[o.props[0]] = (o.props[1].split("\x00")[0], mx.props70(o))
        for c in conns.all("C"):
            if c.props[0] == "OO" and c.props[1] in models and c.props[2] in models:
                parent[c.props[1]] = c.props[2]
        self.names, self.parent, self.local_rest, self.trans = [], {}, {}, {}
        for mid, (name, p) in models.items():
            if not name.startswith("mixamorig:"):
                continue
            short = name[len("mixamorig:"):]
            self.names.append(short)
            pn = models.get(parent.get(mid), ("", None))[0]
            self.parent[short] = pn[len("mixamorig:"):] if pn.startswith("mixamorig:") else None
            pre = mx.mat_euler_xyz(p.get("PreRotation", [0, 0, 0]))
            lcl = mx.mat_euler_xyz(p.get("Lcl Rotation", [0, 0, 0]))
            self.local_rest[short] = mm(pre, lcl)
            self.trans[short] = vscale(tuple(p.get("Lcl Translation", [0, 0, 0])), self.unit)
        self.order = []
        seen = set()

        def visit(n):
            if n in seen:
                return
            if self.parent[n]:
                visit(self.parent[n])
            seen.add(n)
            self.order.append(n)
        for n in self.names:
            visit(n)
        self.grest, self.prest = self.fk(self.local_rest)

    def fk(self, local, hips_rot=None, hips_pos=(0, 0, 0)):
        g, p = {}, {}
        for n in self.order:
            par = self.parent[n]
            if par is None:
                g[n] = mm(hips_rot, local[n]) if hips_rot else local[n]
                p[n] = vadd(self.trans[n], hips_pos)
            else:
                g[n] = mm(g[par], local[n])
                p[n] = vadd(p[par], mv(g[par], self.trans[n]))
        return g, p


# ---------------------------------------------------------------- muscle model

# (bone, child-for-direction, [(muscle, op, args...)]) for the left side; the right side is mirrored.
# Ops: ("swing", target, lo, hi) rotates the bone toward target by m*hi (m>0) or away by m*-lo (m<0);
#      ("bend", target, range)    rotates toward target by range*(1-m)  (Stretch muscles: 1 = straight);
#      ("twist", front, out, lo, hi) twists around the bone, positive turns `front` toward `out`;
#      ("offset", target, deg)    the neutral pose differs from Mixamo's T-pose.
# A '*' in a target means: flipped by calibration (see OPTIONS).
def specs(side, opt):
    S = LEFT if side == "Left" else RIGHT
    sgn = lambda key, v: v if opt[key] > 0 else vscale(v, -1)
    L = side
    body = lambda prefix, lim: [
        (prefix + " Front-Back", "swing", sgn("spine_fb", FRONT), -lim, lim),
        (prefix + " Left-Right", "swing", sgn("spine_lr", RIGHT), -lim, lim),
        (prefix + " Twist Left-Right", "twist", FRONT, sgn("spine_tw", RIGHT), -lim, lim)]
    out = []
    if side == "Left":  # the spine chain only once
        out += [
            ("Spine", "Spine1", body("Spine", 40)),
            ("Spine1", "Spine2", body("Chest", 40)),
            ("Spine2", "Neck", body("UpperChest", 20)),
            ("Neck", "Head", [("Neck Nod Down-Up", "swing", sgn("spine_fb", BACK), -40, 40),
                              ("Neck Tilt Left-Right", "swing", sgn("spine_lr", RIGHT), -40, 40),
                              ("Neck Turn Left-Right", "twist", FRONT, sgn("spine_tw", RIGHT), -40, 40)]),
            ("Head", "HeadTop_End", [("Head Nod Down-Up", "swing", sgn("spine_fb", BACK), -40, 40),
                                     ("Head Tilt Left-Right", "swing", sgn("spine_lr", RIGHT), -40, 40),
                                     ("Head Turn Left-Right", "twist", FRONT, sgn("spine_tw", RIGHT), -40, 40)]),
        ]
    out += [
        (L + "Shoulder", L + "Arm", [(L + " Shoulder Down-Up", "swing", UP, -15, 30),
                                     (L + " Shoulder Front-Back", "swing", sgn("arm_fb", FRONT), -15, 15)]),
        (L + "Arm", L + "ForeArm", [(None, "offset", DOWN, opt["arm_zero"]),
                                    (None, "offset", FRONT, opt["arm_fwd_zero"]),
                                    (L + " Arm Down-Up", "swing", UP, -60, 100),
                                    (L + " Arm Front-Back", "swing", sgn("arm_fb", FRONT), -100, 100),
                                    (L + " Arm Twist In-Out", "twist", FRONT, sgn("arm_tw", UP), -90, 90)]),
        (L + "ForeArm", L + "Hand", [(L + " Forearm Stretch", "bend", FRONT, opt["elbow"]),
                                     (L + " Forearm Twist In-Out", "twist", FRONT, sgn("arm_tw", UP), -90, 90)]),
        (L + "Hand", L + "HandMiddle1", [(L + " Hand Down-Up", "swing", UP, -80, 80),
                                         (L + " Hand In-Out", "swing", BACK, -40, 40)]),
        (L + "UpLeg", L + "Leg", [(None, "offset", FRONT, opt["leg_zero"]),
                                  (L + " Upper Leg Front-Back", "swing", sgn("leg_fb", FRONT), -opt["leg_front"], opt["leg_fwd"]),
                                  (L + " Upper Leg In-Out", "swing", S, -60, 60),
                                  (L + " Upper Leg Twist In-Out", "twist", FRONT, sgn("leg_tw", S), -60, 60)]),
        (L + "Leg", L + "Foot", [(L + " Lower Leg Stretch", "bend", BACK, opt["knee"]),
                                 (L + " Lower Leg Twist In-Out", "twist", FRONT, sgn("leg_tw", S), -90, 90)]),
        (L + "Foot", L + "ToeBase", [(L + " Foot Up-Down", "swing", DOWN, -50, 50),
                                     (L + " Foot Twist In-Out", "twist", FRONT, sgn("leg_tw", S), -30, 30)]),
        (L + "ToeBase", L + "Toe_End", [(L + " Toes Up-Down", "swing", DOWN, -50, 50)]),
    ]
    for unity, mixamo, spread in (("Index", "Index", FRONT), ("Middle", "Middle", None), ("Ring", "Ring", BACK), ("Little", "Pinky", BACK)):
        base = L + "Hand." + unity + "."
        for j, rng in ((1, 50), (2, 45), (3, 45)):
            ops = [(base + "%d Stretched" % j, "bend", DOWN, rng)]
            if j == 1 and spread:
                ops.append((base + "Spread", "swing", spread, -20, 20))
            out.append((L + "Hand" + mixamo + str(j), L + "Hand" + mixamo + str(j + 1), ops))
    base = L + "Hand.Thumb."
    out.append((L + "HandThumb1", L + "HandThumb2", [(base + "1 Stretched", "bend", DOWN, 20), (base + "Spread", "swing", FRONT, -25, 25)]))
    out.append((L + "HandThumb2", L + "HandThumb3", [(base + "2 Stretched", "bend", DOWN, 40)]))
    out.append((L + "HandThumb3", L + "HandThumb4", [(base + "3 Stretched", "bend", DOWN, 40)]))
    return out


DEFAULT = {"spine_fb": 1, "spine_lr": 1, "spine_tw": 1, "arm_fb": 1, "arm_tw": 1, "leg_fb": 1, "leg_tw": 1,
           "arm_zero": 40.0, "leg_fwd": 60.0, "elbow": 80.0, "knee": 80.0, "centre": 0.08, "leg_zero": 0.0, "arm_fwd_zero": 0.0, "leg_front": 50.0}


class Model:
    def __init__(self, skel, opt):
        self.skel, self.opt = skel, opt
        self.bones = []
        for side in ("Left", "Right"):
            for bone, child, ops in specs(side, opt):
                if bone in skel.grest and child in skel.prest:
                    self.bones.append((bone, child, ops))

    def pose(self, m):
        """Muscle dict -> local rotation matrices for every bone (FBX space)."""
        sk = self.skel
        local = dict(sk.local_rest)
        for bone, child, ops in self.bones:
            d = norm(vsub(sk.prest[child], sk.prest[bone]))  # bone direction at rest, global
            swing = I3
            twist = I3
            for op in ops:
                kind = op[1]
                if kind == "offset":
                    swing = mm(self._toward(d, swing, op[2], op[3]), swing)
                    continue
                v = m.get(op[0])
                if v is None:
                    continue
                if kind == "swing":
                    _, _, target, lo, hi = op
                    deg = v * hi if v >= 0 else v * -lo
                    swing = mm(self._toward(d, swing, target, deg), swing)
                elif kind == "bend":
                    _, _, target, rng = op
                    swing = mm(self._toward(d, swing, target, rng * (1 - v)), swing)
                elif kind == "twist":
                    _, _, front, outv, lo, hi = op
                    deg = v * hi if v >= 0 else v * -lo
                    axis = cross(project(front, d), project(outv, d))
                    sign = 1 if dot(axis, d) >= 0 else -1
                    twist = mm(axis_angle(d, sign * deg), twist)
            delta = mm(swing, twist)
            par = sk.parent[bone]
            gp = sk.grest[par] if par else I3
            local[bone] = mm(mt(gp), mm(delta, sk.grest[bone]))
        return local

    @staticmethod
    def _toward(d, current, target, deg):
        cd = mv(current, d)
        axis = cross(cd, target)
        if dot(axis, axis) < 1e-8:
            axis = cross(cd, FRONT) if abs(dot(cd, FRONT)) < 0.9 else cross(cd, UP)
        return axis_angle(axis, deg)


def project(v, d):
    return norm(vsub(v, vscale(d, dot(v, d))))


# ---------------------------------------------------------------- clips

def load_clip(path):
    name, rate, stop, loop, curves = ua.read_anim(path)
    frames = ua.sample(curves, rate, stop)
    return name, rate, frames


def body_rot(fr):
    return quat_to_mat(unity_to_fbx_q((fr.get("RootQ.x", 0), fr.get("RootQ.y", 0), fr.get("RootQ.z", 0), fr.get("RootQ.w", 1))))


def goal_dirs(fr):
    """Hand/foot IK goals: stored relative to the body centre, in body space, normalised by the human scale."""
    out = {}
    for g in ("LeftHand", "RightHand", "LeftFoot", "RightFoot"):
        if g + "T.x" in fr:
            out[g] = unity_to_fbx_v((fr[g + "T.x"], fr[g + "T.y"], fr[g + "T.z"]))
    return out


def calibration_error(skel, opt, clips, step=6, per_goal=None):
    model = Model(skel, opt)
    err, n = 0.0, 0
    hips_h = skel.prest["Hips"][1] or 1.0
    for _, _, frames in clips:
        for fr in frames[::step]:
            goals = goal_dirs(fr)
            local = model.pose(fr)
            local["Hips"] = skel.local_rest["Hips"]
            _, p = skel.fk(local, None, vscale(skel.trans["Hips"], -1))
            # Unity's body centre sits a little above the hips; compare directions from there.
            centre = vadd(p["Hips"], (0, opt["centre"] * hips_h, 0))
            for g, gv in goals.items():
                fk = norm(vsub(p[g], centre))
                e = math.degrees(math.acos(max(-1, min(1, dot(fk, norm(gv))))))
                err += e
                n += 1
                if per_goal is not None:
                    per_goal.setdefault(g, []).append(e)
    return err / max(n, 1)


def calibrate(skel, clips):
    opt = dict(DEFAULT)
    best = calibration_error(skel, opt, clips)
    print("calibration start: %.1f deg average hand/foot direction error" % best)
    for _ in range(3):
        for key in ("arm_fb", "leg_fb", "spine_fb", "spine_lr", "spine_tw", "arm_tw", "leg_tw"):
            trial = dict(opt)
            trial[key] = -opt[key]
            e = calibration_error(skel, trial, clips)
            if e < best - 0.05:
                best, opt = e, trial
        for key, values in (("arm_zero", (10.0, 20.0, 30.0, 40.0, 50.0)), ("leg_fwd", (50.0, 70.0, 90.0)),
                            ("elbow", (60.0, 80.0, 100.0, 120.0, 140.0)), ("knee", (40.0, 60.0, 80.0, 100.0)),
                            ("centre", (-0.1, 0.0, 0.08, 0.15)), ("leg_zero", (0.0, 10.0, 20.0, 30.0, 40.0, 50.0)), ("leg_front", (50.0, 70.0, 90.0)),
                            ("arm_fwd_zero", (-20.0, -10.0, 0.0, 10.0, 20.0, 30.0))):
            for v in values:
                trial = dict(opt)
                trial[key] = v
                e = calibration_error(skel, trial, clips)
                if e < best - 0.05:
                    best, opt = e, trial
    per = {}
    calibration_error(skel, opt, clips, per_goal=per)
    print("calibrated: %.1f deg, %s" % (best, opt))
    print("  per goal: " + ", ".join("%s %.1f" % (g, sum(v) / len(v)) for g, v in per.items()))
    return opt


# ---------------------------------------------------------------- output (same format as mixamo2dance)

def convert(skel, model, name, rate, frames):
    bones = [b for b in skel.order]
    rots = {b: [] for b in bones}
    offsets = []
    root0 = None
    yaw0 = None
    hips_h = skel.prest["Hips"][1]
    for fr in frames:
        local = model.pose(fr)
        body = body_rot(fr)
        if yaw0 is None:
            f = mv(body, FRONT)
            yaw0 = axis_angle(UP, -math.degrees(math.atan2(f[0], f[2])))  # face forward at the start
        local["Hips"] = mm(mm(yaw0, body), skel.local_rest["Hips"])
        for b in bones:
            x, y, z, w = mx.quat(local[b])
            rots[b].append((x, -y, -z, w))
        root = unity_to_fbx_v((fr.get("RootT.x", 0), fr.get("RootT.y", 0), fr.get("RootT.z", 0)))
        if root0 is None:
            root0 = root
        rel = mv(yaw0, vsub(root, root0))
        offsets.append((-rel[0] * hips_h, rel[1] * hips_h, rel[2] * hips_h))  # back to Unity axes
    for b in bones:
        r = rots[b]
        for i in range(1, len(r)):
            if sum(a * c for a, c in zip(r[i - 1], r[i])) < 0:
                r[i] = tuple(-v for v in r[i])
    leg = math.sqrt(dot(skel.trans["LeftLeg"], skel.trans["LeftLeg"]))
    return [("mixamorig:" + b, rots[b], None) for b in bones], offsets, leg


def main():
    rig, dst, clips_paths = sys.argv[1], sys.argv[2], sys.argv[3:]
    skel = Skeleton(rig)
    clips = []
    for p in clips_paths:
        name, rate, frames = load_clip(p)
        clips.append((name, rate, frames))
        print("read %s: %d frames" % (name, len(frames)))
    opt = calibrate(skel, clips)
    model = Model(skel, opt)
    dances = []
    for name, rate, frames in clips:
        bones, offsets, leg = convert(skel, model, name, rate, frames)
        dance_id = "swyf-dance:" + "".join(ch if ch.isalnum() else "-" for ch in name.lower())
        dances.append((dance_id, pretty(name), rate, len(frames), leg, bones, offsets))
    write(dst, dances)


def pretty(name):
    """"GangnamStyle" -> "Gangnam Style"."""
    out = ""
    for i, ch in enumerate(name):
        if i and ch.isupper() and (name[i - 1].islower() or name[i - 1] == "'"):
            out += " "
        out += ch
    return out.replace("'T", "'t")


def write(dst, dances):
    with open(dst, "wb") as out:
        out.write(b"SWYD")
        out.write(struct.pack("<II", 1, len(dances)))
        for dance_id, label, fps, frames, leg, bones, offsets in dances:
            mx.write_str(out, dance_id)
            mx.write_str(out, label)
            out.write(struct.pack("<fIfI", fps, frames, leg, 1))
            out.write(struct.pack("<I", len(bones)))
            for name, rots, _ in bones:
                mx.write_str(out, name)
                out.write(struct.pack("<%df" % (frames * 4), *[v for q in rots for v in q]))
            out.write(struct.pack("<%df" % (frames * 3), *[v for o in offsets for v in o]))
    print("wrote %s (%d dances, %d KB)" % (dst, len(dances), os.path.getsize(dst) // 1024))


if __name__ == "__main__":
    main()
