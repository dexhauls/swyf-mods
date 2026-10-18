"""Converts Mixamo animations (FBX Binary, "Without Skin") into the dance file the Emoty mod plays.

    python ModSource/Emotes/tools/mixamo2dance.py ModSource/Emotes/mixamo ModSource/Emotes/dances.bin

The game's characters use Mixamo skeletons (bones named "mixamorig:Hips" ...) and its own emotes are Mixamo
clips, so each bone's local rotation is copied as is, converted to Unity's coordinates the same way Unity's FBX
importer does it (mirror on X). The hips also get their movement relative to the first frame.

Output format (little endian):
    "SWYD" u32 version=1  u32 danceCount
    per dance: str id, str label, f32 fps, u32 frames, f32 legLength, u32 flags (1 = loop)
               u32 boneCount, per bone: str name, frames * 4 f32 (x, y, z, w)
               frames * 3 f32 hips offset from frame 0 (metres, Unity axes)
    str = u16 byte length + UTF-8
"""
import math
import os
import struct
import sys
import zlib

FBX_TICKS = 46186158000  # FBX time units per second

# Czech names for well-known Mixamo dances; anything else keeps its file name.
LABELS = {
    "gangnam style": "Gangnam",
    "macarena dance": "Macarena",
    "twerk": "Twerk",
    "dancing twerk": "Twerk",
    "thriller part 1": "Thriller",
    "thriller part 2": "Thriller",
    "thriller part 3": "Thriller",
    "thriller part 4": "Thriller",
    "robot hip hop dance": "Robot",
    "silly dancing": "Blbý tanec",
    "samba dancing": "Samba",
    "snake hip hop dance": "Had",
    "wave hip hop dance": "Vlna",
    "breakdance freezes": "Breakdance",
    "breakdance 1990": "Breakdance",
    "twist dance": "Twist",
    "house dancing": "House",
    "belly dance": "Břišní tanec",
    "salsa dancing": "Salsa",
    "jazz dancing": "Jazz",
    "tut hip hop dance": "Tut",
    "bboy hip hop move": "B-boy",
    "chicken dance": "Kuře",
    "flair": "Flair",
    "breakdance flair": "Flair",
    "capoeira": "Capoeira",
}


# ---------------------------------------------------------------- FBX binary reader

class Node:
    __slots__ = ("name", "props", "children")

    def __init__(self, name, props, children):
        self.name, self.props, self.children = name, props, children

    def find(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None

    def all(self, name):
        return [c for c in self.children if c.name == name]


def read_fbx(path):
    data = open(path, "rb").read()
    if not data.startswith(b"Kaydara FBX Binary"):
        raise ValueError("not a binary FBX (in Mixamo choose Format: FBX Binary)")
    version = struct.unpack_from("<I", data, 23)[0]
    wide = version >= 7500
    pos = 27
    roots = []
    while True:
        node, pos = read_node(data, pos, wide)
        if node is None:
            break
        roots.append(node)
    return Node("root", [], roots)


def read_node(data, pos, wide):
    if wide:
        end, nprops, plen = struct.unpack_from("<QQQ", data, pos)
        pos += 24
    else:
        end, nprops, plen = struct.unpack_from("<III", data, pos)
        pos += 12
    if end == 0:
        return None, pos + data[pos]  # null record
    nlen = data[pos]
    name = data[pos + 1:pos + 1 + nlen].decode("ascii", "replace")
    pos += 1 + nlen
    props = []
    for _ in range(nprops):
        value, pos = read_prop(data, pos)
        props.append(value)
    children = []
    while pos < end:
        child, npos = read_node(data, pos, wide)
        if child is None:
            pos = end
            break
        children.append(child)
        pos = npos
    return Node(name, props, children), end


ARRAY = {"f": ("f", 4), "d": ("d", 8), "l": ("q", 8), "i": ("i", 4), "b": ("?", 1)}


def read_prop(data, pos):
    t = chr(data[pos])
    pos += 1
    if t == "Y": return struct.unpack_from("<h", data, pos)[0], pos + 2
    if t == "C": return bool(data[pos]), pos + 1
    if t == "I": return struct.unpack_from("<i", data, pos)[0], pos + 4
    if t == "F": return struct.unpack_from("<f", data, pos)[0], pos + 4
    if t == "D": return struct.unpack_from("<d", data, pos)[0], pos + 8
    if t == "L": return struct.unpack_from("<q", data, pos)[0], pos + 8
    if t in ("S", "R"):
        n = struct.unpack_from("<I", data, pos)[0]
        raw = data[pos + 4:pos + 4 + n]
        return (raw.decode("utf-8", "replace") if t == "S" else raw), pos + 4 + n
    if t in ARRAY:
        n, enc, clen = struct.unpack_from("<III", data, pos)
        pos += 12
        raw = data[pos:pos + clen]
        if enc == 1:
            raw = zlib.decompress(raw)
        fmt, size = ARRAY[t]
        return list(struct.unpack("<%d%s" % (n, fmt), raw[:n * size])), pos + clen
    raise ValueError("unknown FBX property type %r" % t)


def props70(node):
    out = {}
    p70 = node.find("Properties70")
    if p70:
        for p in p70.all("P"):
            out[p.props[0]] = p.props[4:]
    return out


# ---------------------------------------------------------------- math

def mat_euler_xyz(deg):
    """FBX eEulerXYZ: rotate about X, then Y, then Z (column vectors: R = Rz Ry Rx)."""
    x, y, z = (math.radians(v) for v in deg)
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]
    ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]
    rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    return mul(rz, mul(ry, rx))


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def transpose(a):
    return [[a[j][i] for j in range(3)] for i in range(3)]


def quat(m):
    tr = m[0][0] + m[1][1] + m[2][2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        return ((m[2][1] - m[1][2]) / s, (m[0][2] - m[2][0]) / s, (m[1][0] - m[0][1]) / s, 0.25 * s)
    if m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2
        return (0.25 * s, (m[0][1] + m[1][0]) / s, (m[0][2] + m[2][0]) / s, (m[2][1] - m[1][2]) / s)
    if m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2
        return ((m[0][1] + m[1][0]) / s, 0.25 * s, (m[1][2] + m[2][1]) / s, (m[0][2] - m[2][0]) / s)
    s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2
    return ((m[0][2] + m[2][0]) / s, (m[1][2] + m[2][1]) / s, 0.25 * s, (m[1][0] - m[0][1]) / s)


# ---------------------------------------------------------------- animation

class Curve:
    def __init__(self, node):
        times = node.find("KeyTime").props[0]
        self.t = [v / FBX_TICKS for v in times]
        self.v = node.find("KeyValueFloat").props[0]

    def at(self, time):
        t, v = self.t, self.v
        if time <= t[0]: return v[0]
        if time >= t[-1]: return v[-1]
        lo, hi = 0, len(t) - 1
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if t[mid] <= time: lo = mid
            else: hi = mid
        f = (time - t[lo]) / (t[hi] - t[lo]) if t[hi] > t[lo] else 0
        return v[lo] + (v[hi] - v[lo]) * f


def convert(path):
    fbx = read_fbx(path)
    objects = fbx.find("Objects")
    conns = fbx.find("Connections")
    settings = props70(fbx.find("GlobalSettings"))
    unit = float(settings.get("UnitScaleFactor", [1.0])[0])  # centimetres per unit
    if settings.get("UpAxis", [1])[0] != 1:
        print("  warning: not a Y-up FBX, results may be rotated")

    models, curvenodes, curves = {}, {}, {}
    for o in objects.children:
        if o.name == "Model":
            models[o.props[0]] = {"name": o.props[1].split("\x00")[0], "p": props70(o), "parent": None, "anim": {}}
        elif o.name == "AnimationCurveNode":
            curvenodes[o.props[0]] = {"kind": o.props[1].split("\x00")[0], "default": props70(o), "axes": {}}
        elif o.name == "AnimationCurve":
            curves[o.props[0]] = Curve(o)

    for c in conns.all("C"):
        kind, child, parent = c.props[0], c.props[1], c.props[2]
        if kind == "OO" and child in models and parent in models:
            models[child]["parent"] = parent
        elif kind == "OP" and child in curves and parent in curvenodes:
            curvenodes[parent]["axes"][c.props[3]] = curves[child]
        elif kind == "OP" and child in curvenodes and parent in models:
            models[parent]["anim"][c.props[3]] = curvenodes[child]

    end = 0.0
    for cv in curves.values():
        end = max(end, cv.t[-1])
    fps = 30.0
    frames = max(1, int(round(end * fps)) + 1)

    def channel(model, prop, frame_time):
        base = list(model["p"].get(prop, [0.0, 0.0, 0.0]))
        node = model["anim"].get(prop)
        if node:
            for i, axis in enumerate(("d|X", "d|Y", "d|Z")):
                if axis in node["axes"]:
                    base[i] = node["axes"][axis].at(frame_time)
                elif axis in node["default"]:
                    base[i] = node["default"][axis][0]
        return base

    bones = []
    for mid, m in models.items():
        if not m["name"].startswith("mixamorig:"):
            continue
        pre = mat_euler_xyz(m["p"].get("PreRotation", [0, 0, 0]))
        post_inv = transpose(mat_euler_xyz(m["p"].get("PostRotation", [0, 0, 0])))
        if m["p"].get("RotationOrder", [0])[0] not in (0, None):
            print("  warning: %s uses rotation order %s, treated as XYZ" % (m["name"], m["p"]["RotationOrder"][0]))
        rots = []
        for f in range(frames):
            r = mul(pre, mul(mat_euler_xyz(channel(m, "Lcl Rotation", f / fps)), post_inv))
            x, y, z, w = quat(r)
            rots.append((x, -y, -z, w))  # right-handed FBX -> left-handed Unity (mirror X)
        # Keep each quaternion on the same hemisphere as the previous frame so interpolation never flips.
        for i in range(1, len(rots)):
            a, b = rots[i - 1], rots[i]
            if a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3] < 0:
                rots[i] = tuple(-v for v in b)
        bones.append((m["name"], rots, mid))

    names = {m["name"]: mid for mid, m in models.items()}
    hips = models.get(names.get("mixamorig:Hips"))
    if hips is None:
        raise ValueError("no mixamorig:Hips bone (is this a Mixamo animation?)")
    scale = unit / 100.0  # metres
    start = channel(hips, "Lcl Translation", 0.0)
    offsets = []
    for f in range(frames):
        p = channel(hips, "Lcl Translation", f / fps)
        offsets.append((-(p[0] - start[0]) * scale, (p[1] - start[1]) * scale, (p[2] - start[2]) * scale))

    leg = models.get(names.get("mixamorig:LeftLeg"))
    leg_len = 0.0
    if leg:
        t = leg["p"].get("Lcl Translation", [0, 0, 0])
        leg_len = math.sqrt(sum(v * v for v in t)) * scale
    return fps, frames, leg_len, bones, offsets


def write_str(out, s):
    b = s.encode("utf-8")
    out.write(struct.pack("<H", len(b)))
    out.write(b)


def main():
    src, dst = sys.argv[1], sys.argv[2]
    files = sorted(f for f in os.listdir(src) if f.lower().endswith(".fbx"))
    if not files:
        sys.exit("no .fbx files in " + src)
    dances = []
    used_labels = {}
    for f in files:
        stem = os.path.splitext(f)[0]
        key = stem.lower().replace("_", " ").strip()
        while key[-1:].isdigit() or key.endswith(" ") or key.endswith("("):
            key = key.rstrip("0123456789 (")
        key = key.rstrip(")").strip()
        label = LABELS.get(key, stem.replace("_", " "))
        used_labels[label] = used_labels.get(label, 0) + 1
        if used_labels[label] > 1:
            label = "%s %d" % (label, used_labels[label])
        try:
            fps, frames, leg, bones, offsets = convert(os.path.join(src, f))
        except Exception as e:
            print("SKIP %s: %s" % (f, e))
            continue
        dance_id = "swyf-dance:" + "".join(ch if ch.isalnum() else "-" for ch in stem.lower())
        dances.append((dance_id, label, fps, frames, leg, bones, offsets))
        print("%-40s -> %-14s %5.1f s, %d bones" % (f, label, frames / fps, len(bones)))

    with open(dst, "wb") as out:
        out.write(b"SWYD")
        out.write(struct.pack("<II", 1, len(dances)))
        for dance_id, label, fps, frames, leg, bones, offsets in dances:
            write_str(out, dance_id)
            write_str(out, label)
            out.write(struct.pack("<fIfI", fps, frames, leg, 1))
            out.write(struct.pack("<I", len(bones)))
            for name, rots, _ in bones:
                write_str(out, name)
                out.write(struct.pack("<%df" % (frames * 4), *[v for q in rots for v in q]))
            out.write(struct.pack("<%df" % (frames * 3), *[v for o in offsets for v in o]))
    print("wrote %s (%d dances, %d KB)" % (dst, len(dances), os.path.getsize(dst) // 1024))


if __name__ == "__main__":
    main()
