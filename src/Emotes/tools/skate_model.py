"""Builds the roller skate for Roller Vibes in Blender and saves it for the mod (and a preview render).

    blender -b -P skate_model.py -- <out skate.bin> [preview.png]

The skate is modelled at a nominal size: sole at height 0, 1 long (toe at +0.5), boot 0.42 wide. Parts: "boot"
(fitted to the dancer's shoe, may stretch), "chassis" (frame, trucks, toe stop; scaled evenly) and "wheel0".."wheel3"
(each wheel with its hub, so the game can spin them). File format (little endian), Unity axes (x right, y up, z forward):
    "SWYK" u32 version=1, u32 parts
    per part: u16 name length + utf8 name, u32 materials
        per material: 3 x f32 colour, f32 smoothness, u32 vertex count, vertex count x (3 f32 position, 3 f32 normal),
                      u32 index count, index count x u32
"""
import math
import struct
import sys

import bmesh
import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else "skate.bin"
PREVIEW = argv[1] if len(argv) > 1 else None

WHITE = (0.95, 0.95, 0.96, 0.45)
PINK = (1.0, 0.33, 0.6, 0.5)
MINT = (0.33, 0.93, 0.78, 0.6)
GREY = (0.55, 0.57, 0.62, 0.35)
SILVER = (0.82, 0.84, 0.88, 0.8)

BOOT_W = 0.42 / 2
BOOT_H = 0.62
PLATE = 0.05
WHEEL_R = 0.12
WHEEL_W = 0.1


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def material(name, rgba):
    m = bpy.data.materials.new(name)
    m.diffuse_color = (rgba[0], rgba[1], rgba[2], 1)
    m["swyf_color"] = rgba
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (rgba[0], rgba[1], rgba[2], 1)
        bsdf.inputs["Roughness"].default_value = 1 - rgba[3]
    return m


def obj_from_bm(name, bm, mat, part):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(ob)
    me.materials.append(mat)
    ob["part"] = part
    return ob


def smooth(ob, levels=2, shade=True):
    mod = ob.modifiers.new("sub", "SUBSURF")
    mod.levels = levels
    mod.render_levels = levels
    for p in ob.data.polygons:
        p.use_smooth = shade


def bevel(ob, width, segments=3):
    mod = ob.modifiers.new("bev", "BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    for p in ob.data.polygons:
        p.use_smooth = True
    ob.data.shade_auto_smooth() if hasattr(ob.data, "shade_auto_smooth") else None


def smoothstep(a, b, x):
    t = max(0.0, min(1.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def boot_outline(t, n=40):
    """Ring of the boot at height t (0..1): a stadium shape; the toe box is low, above it the shaft at the back."""
    w = BOOT_W * (1.0 - 0.1 * t)
    back = -0.5 + 0.03 * t
    front = 0.5 - 0.06 * t * t
    front = front + (0.08 - front) * smoothstep(0.28, 0.62, t)   # instep slopes up to the shaft
    pts = []
    cf, cb = front - w, back + w
    if cf < cb:
        cf = cb = (front + back) / 2
    for i in range(n):
        a = 2 * math.pi * i / n
        x, y = math.cos(a) * w, math.sin(a) * w
        y += cf if y >= 0 else cb
        pts.append((x, y))
    return pts


def build_boot(mats):
    bm = bmesh.new()
    rings = []
    levels = 16
    for k in range(levels + 1):
        t = k / levels
        z = PLATE * 0.2 + t * (BOOT_H - PLATE * 0.2)
        rings.append([bm.verts.new((x, y, z)) for x, y in boot_outline(t)])
    n = len(rings[0])
    for k in range(levels):
        for i in range(n):
            a, b = rings[k][i], rings[k][(i + 1) % n]
            c, d = rings[k + 1][(i + 1) % n], rings[k + 1][i]
            bm.faces.new((a, b, c, d))
    bm.faces.new(list(reversed(rings[0])))   # sole
    # the opening at the top: a short lip inwards
    top = rings[-1]
    inner = [bm.verts.new((v.co.x * 0.8, v.co.y * 0.8 + 0.03, v.co.z)) for v in top]
    for i in range(n):
        bm.faces.new((top[i], top[(i + 1) % n], inner[(i + 1) % n], inner[i]))
    bm.faces.new(inner)
    ob = obj_from_bm("boot", bm, mats["white"], "boot")
    smooth(ob, 1)

    # collar round the opening
    bpy.ops.mesh.primitive_torus_add(major_radius=1, minor_radius=0.12, major_segments=32, minor_segments=8)
    col = bpy.context.active_object
    col.name = "collar"
    ring = boot_outline(1.0)
    ys = [p[1] for p in ring]
    col.scale = (BOOT_W * 0.95, (max(ys) - min(ys)) / 2 * 0.95, 0.35)
    col.location = (0, (max(ys) + min(ys)) / 2, BOOT_H - 0.01)
    col.data.materials.append(mats["pink"])
    col["part"] = "boot"
    for p in col.data.polygons:
        p.use_smooth = True

    # heel counter: a pink patch round the heel
    bm = bmesh.new()
    rings = []
    for k in range(7):
        t = k / 6 * 0.45
        z = PLATE * 0.2 + t * (BOOT_H - PLATE * 0.2)
        pts = boot_outline(t)
        ring = [(x * 1.03, y * 1.0 + (-0.005 if y < 0 else 0)) for x, y in pts]
        sel = [p for p in ring if p[1] < -0.18]
        rings.append([bm.verts.new((x, y - 0.006, z)) for x, y in sel])
    for k in range(len(rings) - 1):
        for i in range(len(rings[k]) - 1):
            bm.faces.new((rings[k][i], rings[k][i + 1], rings[k + 1][i + 1], rings[k + 1][i]))
    heel = obj_from_bm("heel", bm, mats["pink"], "boot")
    smooth(heel, 1)
    sol = heel.modifiers.new("thick", "SOLIDIFY")
    sol.thickness = 0.012

    # laces: bars across the instep
    for k in range(5):
        t = 0.36 + k * 0.1
        z = PLATE * 0.2 + t * (BOOT_H - PLATE * 0.2)
        front = max(p[1] for p in boot_outline(t))
        bpy.ops.mesh.primitive_cylinder_add(radius=0.011, depth=BOOT_W * 0.7, vertices=8,
                                            location=(0, front - 0.004, z), rotation=(0, math.pi / 2, 0))
        lace = bpy.context.active_object
        lace.data.materials.append(mats["pink"])
        lace["part"] = "boot"


def build_chassis(mats):
    # frame (plate) under the boot
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0.0, -PLATE / 2))
    plate = bpy.context.active_object
    plate.scale = (BOOT_W * 1.7, 0.94, PLATE)
    bpy.ops.object.transform_apply(scale=True)
    bevel(plate, 0.02)
    plate.data.materials.append(mats["grey"])
    plate["part"] = "chassis"
    wheel_z = -PLATE - WHEEL_R
    n = 0
    for y in (-0.3, 0.3):
        # truck: hanger and axle
        bpy.ops.mesh.primitive_cube_add(size=1, location=(0, y, -PLATE - WHEEL_R * 0.45))
        hanger = bpy.context.active_object
        hanger.scale = (BOOT_W * 1.3, 0.1, WHEEL_R * 0.8)
        bpy.ops.object.transform_apply(scale=True)
        bevel(hanger, 0.02)
        hanger.data.materials.append(mats["silver"])
        hanger["part"] = "chassis"
        bpy.ops.mesh.primitive_cylinder_add(radius=0.025, depth=BOOT_W * 2.2 + WHEEL_W * 2, vertices=12,
                                            location=(0, y, wheel_z), rotation=(0, math.pi / 2, 0))
        axle = bpy.context.active_object
        axle.data.materials.append(mats["silver"])
        axle["part"] = "chassis"
        for x in (-1, 1):
            cx = x * (BOOT_W * 1.05 + WHEEL_W / 2)
            bpy.ops.mesh.primitive_cylinder_add(radius=WHEEL_R, depth=WHEEL_W, vertices=24,
                                                location=(cx, y, wheel_z), rotation=(0, math.pi / 2, 0))
            wheel = bpy.context.active_object
            bevel(wheel, WHEEL_W * 0.35, 3)
            wheel.data.materials.append(mats["mint"])
            wheel["part"] = "wheel%d" % n
            bpy.ops.mesh.primitive_cylinder_add(radius=WHEEL_R * 0.45, depth=WHEEL_W * 1.08, vertices=20,
                                                location=(cx, y, wheel_z), rotation=(0, math.pi / 2, 0))
            hub = bpy.context.active_object
            bevel(hub, 0.01, 2)
            hub.data.materials.append(mats["white"])
            hub["part"] = "wheel%d" % n
            n += 1
    # toe stop
    bpy.ops.mesh.primitive_cylinder_add(radius=0.075, depth=0.11, vertices=24, location=(0, 0.47, -PLATE - 0.05))
    stop = bpy.context.active_object
    bevel(stop, 0.025, 3)
    stop.data.materials.append(mats["pink"])
    stop["part"] = "chassis"


def export(path):
    deps = bpy.context.evaluated_depsgraph_get()
    parts = {}
    for ob in bpy.context.scene.objects:
        if ob.type != "MESH":
            continue
        part = ob.get("part", "boot")
        ev = ob.evaluated_get(deps)
        me = ev.to_mesh()
        me.calc_loop_triangles()
        mat = ob.data.materials[0]
        col = tuple(mat["swyf_color"])
        mw = ob.matrix_world
        nm = mw.to_3x3().inverted().transposed()
        groups = parts.setdefault(part, {})
        verts, idx = groups.setdefault(col, ([], []))
        seen = groups.setdefault(("seen",) + col, {})
        for tri in me.loop_triangles:
            corners = []
            for li in tri.loops:
                loop = me.loops[li]
                p = mw @ me.vertices[loop.vertex_index].co
                n = (nm @ (loop.normal if tri.use_smooth else tri.normal)).normalized()
                corners.append(((p.x, p.z, p.y), (n.x, n.z, n.y)))   # Blender z-up -> Unity y-up
            ids = []
            for c in corners:
                key = tuple(round(v, 5) for v in c[0] + c[1])
                if key not in seen:
                    seen[key] = len(verts)
                    verts.append(c)
                ids.append(seen[key])
            idx.extend([ids[0], ids[2], ids[1]])                   # axis swap flips handedness: flip winding
        ev.to_mesh_clear()
    with open(path, "wb") as f:
        f.write(b"SWYK")
        f.write(struct.pack("<II", 1, len(parts)))
        for name, groups in parts.items():
            nb = name.encode("utf-8")
            f.write(struct.pack("<H", len(nb)) + nb)
            real = {k: v for k, v in groups.items() if k[0] != "seen"}
            f.write(struct.pack("<I", len(real)))
            for col, (verts, idx) in real.items():
                f.write(struct.pack("<4f", *col))
                f.write(struct.pack("<I", len(verts)))
                for p, n in verts:
                    f.write(struct.pack("<6f", *p, *n))
                f.write(struct.pack("<I", len(idx)))
                f.write(struct.pack("<%dI" % len(idx), *idx))
    total = sum(len(v[0]) for g in parts.values() for k, v in g.items() if k[0] != "seen")
    print("saved", path, "parts", list(parts), "corners", total)


def render(path):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_WORKBENCH"
    scene.render.resolution_x = 900
    scene.render.resolution_y = 700
    scene.render.film_transparent = False
    world = bpy.data.worlds.new("w")
    world.color = (0.18, 0.16, 0.2)
    scene.world = world
    cam_data = bpy.data.cameras.new("cam")
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (1.55, 1.25, 0.75)
    direction = Vector((0, 0.0, 0.18)) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    cam_data.lens = 45
    scene.camera = cam
    for loc, energy in (((2, 2, 3), 800), ((-2, -1, 2), 300), ((0, -3, 1), 200)):
        ld = bpy.data.lights.new("l", "POINT")
        ld.energy = energy
        lo = bpy.data.objects.new("l", ld)
        lo.location = loc
        scene.collection.objects.link(lo)
    if scene.render.engine == "BLENDER_WORKBENCH":
        scene.display.shading.light = "STUDIO"
        scene.display.shading.color_type = "MATERIAL"
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("rendered", path)


reset()
mats = {"white": material("white", WHITE), "pink": material("pink", PINK), "mint": material("mint", MINT),
        "grey": material("grey", GREY), "silver": material("silver", SILVER)}
build_boot(mats)
build_chassis(mats)
export(OUT)
if PREVIEW:
    render(PREVIEW)
