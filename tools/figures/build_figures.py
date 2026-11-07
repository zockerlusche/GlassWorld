"""Builds the GlassWorld figure bodies (A1436, Marcel F5257=A) in Blender, headless.

Usage (Blender 4.5 LTS):
    blender -b --factory-startup --python tools/figures/build_figures.py -- \
        game/assets/characters/quaternius-ual1/ual1_standard.glb tools/figures/bodies.json game/assets/characters/glassworld

The Quaternius Universal Animation Library (CC0) supplies the rig and all clips; its mannequin mesh is
dropped. Each body is modelled from simple round shapes after D355 (big round head with eyes, brows,
nose and ears, rounded torso, soft limbs, open mitten hands with a thumb, round shoes) and skinned to
the Quaternius bones with smooth weights at the joints. Hair styles are separate skinned meshes the
game switches by appearance data. Body regions are written into the vertex colour red channel
(index / 10) for the warm comic shader: 1 skin, 2 top, 3 bottom, 4 shoes, 5 hair, 6 shin, 7 forearm,
8 thigh, 9 eyes. The mesh is our own work (MIT, like the repository); the clips stay CC0.
"""

import json
import math
import sys

import bmesh
import bpy
from mathutils import Vector

SKIN, TOP, BOTTOM, SHOES, HAIR, SHIN, FOREARM, THIGH, EYES = 1, 2, 3, 4, 5, 6, 7, 8, 9
UP = Vector((0, 0, 1))
FWD = Vector((0, -1, 0))  # the rig faces -Y in Blender
LEFT = Vector((1, 0, 0))  # .L bones lie on +X


def smooth(a, b, x):
    t = max(0.0, min(1.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def orient_outward(bm, inside=None):
    """Makes every face point away from the inside point (default: the centroid of the part).

    The axis mappings into the rig frame include a mirror (+X left, -Y forward), which reverses the
    winding of generated primitives; Godot culls by winding, so inverted parts would show their inside.
    """
    bm.normal_update()
    if inside is None:
        inside = sum((v.co for v in bm.verts), Vector()) / max(1, len(bm.verts))
    flip = [f for f in bm.faces if f.normal.dot(f.calc_center_median() - inside) < 0]
    if flip:
        bmesh.ops.reverse_faces(bm, faces=flip)
        bm.normal_update()
    return bm


class Part:
    """One piece of a body: a bmesh plus functions for region and bone weights per vertex."""

    def __init__(self, bm, region, weights, inside=None):
        self.bm = orient_outward(bm, inside)
        self.region = region if callable(region) else (lambda p, r=region: r)
        self.weights = weights if callable(weights) else (lambda p, w=weights: {w: 1.0})


def frame(axis):
    """Two unit vectors perpendicular to axis."""
    axis = axis.normalized()
    helper = UP if abs(axis.dot(UP)) < 0.9 else FWD
    right = axis.cross(helper).normalized()
    other = axis.cross(right).normalized()
    return right, other


def loft(rings, segments=16, cap=True):
    """rings: list of (centre, axis_a, radius_a, axis_b, radius_b)."""
    bm = bmesh.new()
    rows = []
    for centre, ax_a, ra, ax_b, rb in rings:
        row = []
        for i in range(segments):
            a = 2 * math.pi * i / segments
            row.append(bm.verts.new(centre + ax_a * (math.cos(a) * ra) + ax_b * (math.sin(a) * rb)))
        rows.append(row)
    for r in range(len(rows) - 1):
        for i in range(segments):
            j = (i + 1) % segments
            bm.faces.new((rows[r][i], rows[r][j], rows[r + 1][j], rows[r + 1][i]))
    if cap:
        bm.faces.new(list(reversed(rows[0])))
        bm.faces.new(rows[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def capsule(a, b, ra, rb, segments=14, steps=8, cap_steps=4):
    """A tapered tube from a to b with round ends."""
    axis = b - a
    length = axis.length
    d = axis.normalized()
    right, other = frame(d)
    rings = []
    for k in range(cap_steps, 0, -1):  # start cap
        ang = (math.pi / 2) * k / (cap_steps + 1)
        rings.append((a - d * (ra * math.sin(ang)), right, ra * math.cos(ang), other, ra * math.cos(ang)))
    for k in range(steps + 1):
        t = k / steps
        r = ra + (rb - ra) * t
        rings.append((a + d * (length * t), right, r, other, r))
    for k in range(1, cap_steps + 1):  # end cap
        ang = (math.pi / 2) * k / (cap_steps + 1)
        rings.append((b + d * (rb * math.sin(ang)), right, rb * math.cos(ang), other, rb * math.cos(ang)))
    return loft(rings, segments)


def ellipsoid(centre, rx, ry, rz, u=20, v=14, right=LEFT, fwd=FWD, up=UP):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=u, v_segments=v, radius=1.0)
    for vert in bm.verts:
        x, y, z = vert.co
        vert.co = centre + right * (x * rx) + fwd * (y * ry) + up * (z * rz)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def along(bone, a, b, parent=None, child=None, child_share=0.5, parent_share=0.5):
    """Weights for a part spanning bone from a to b: blends into parent and child bones near the joints."""
    seg = b - a

    def weights(p):
        t = max(0.0, min(1.0, (p - a).dot(seg) / seg.length_squared))
        w = {}
        wc = smooth(0.72, 1.0, t) * child_share if child else 0.0
        wp = (1 - smooth(0.0, 0.28, t)) * parent_share if parent else 0.0
        if child:
            w[child] = wc
        if parent:
            w[parent] = wp
        w[bone] = 1.0 - wc - wp
        return w

    return weights


class Rig:
    def __init__(self, armature):
        self.arm = armature
        mw = armature.matrix_world
        self.head = {b.name: mw @ b.head_local for b in armature.data.bones}
        self.tail = {b.name: mw @ b.tail_local for b in armature.data.bones}

    def h(self, name):
        return self.head[name].copy()

    def t(self, name):
        return self.tail[name].copy()


def torso_weights(rig):
    chain = [("DEF-hips", rig.h("DEF-hips").z), ("DEF-spine.001", rig.h("DEF-spine.001").z),
             ("DEF-spine.002", rig.h("DEF-spine.002").z), ("DEF-spine.003", rig.h("DEF-spine.003").z),
             ("DEF-neck", rig.h("DEF-neck").z)]

    def weights(p):
        w = {}
        if p.z <= chain[0][1]:
            w[chain[0][0]] = 1.0
        elif p.z >= chain[-1][1]:
            w[chain[-2][0]] = 0.6
            w[chain[-1][0]] = 0.4
        else:
            for (b0, z0), (b1, z1) in zip(chain, chain[1:]):
                if z0 <= p.z <= z1:
                    t = (p.z - z0) / (z1 - z0)
                    w[b0] = 1 - t * 0.5
                    w[b1] = t * 0.5
                    break
        side = "L" if p.x > 0 else "R"
        share = smooth(0.08, 0.16, abs(p.x)) * smooth(1.36, 1.44, p.z) * 0.5
        if share > 0:
            w = {k: v * (1 - share) for k, v in w.items()}
            w[f"DEF-shoulder.{side}"] = w.get(f"DEF-shoulder.{side}", 0) + share
        return w

    return weights


def build_body(rig, spec):
    parts = []
    hips_z = rig.h("DEF-hips").z
    top_z = rig.h("DEF-neck").z
    sh = spec
    # torso: rings from below the hips to the shoulders, wider at the chest
    rings = []
    profile = [
        (hips_z - 0.07, 0.6), (hips_z - 0.03, 0.92), (hips_z + 0.02, 1.0), (hips_z + 0.12, 0.0),
        (hips_z + 0.24, 0.5), (hips_z + 0.36, 1.0), (top_z - 0.06, 1.0), (top_z - 0.01, 0.75), (top_z + 0.015, 0.38),
    ]
    for i, (z, k) in enumerate(profile):
        if i < 3:
            rx, ry = sh["hips"][0] * k, sh["hips"][1] * k
        elif i == 3:
            rx, ry = sh["waist"]
        else:
            rx = sh["waist"][0] + (sh["chest"][0] - sh["waist"][0]) * k if i < 7 else sh["chest"][0] * k
            ry = sh["waist"][1] + (sh["chest"][1] - sh["waist"][1]) * k if i < 7 else sh["chest"][1] * k
        rings.append((Vector((0, 0.012, z)), LEFT, rx, FWD, ry))
    parts.append(Part(loft(rings, 20), lambda p, hz=hips_z: BOTTOM if p.z < hz + 0.07 else TOP, torso_weights(rig)))

    # neck and head
    neck_a, neck_b = rig.h("DEF-neck"), rig.h("DEF-head")
    parts.append(Part(capsule(neck_a + Vector((0, 0, -0.02)), neck_b + Vector((0, 0, 0.03)), sh["neck_radius"], sh["neck_radius"] * 0.95), SKIN,
                      along("DEF-neck", neck_a, neck_b, parent="DEF-spine.003", child="DEF-head")))
    r = sh["head_radius"]
    centre = rig.h("DEF-head") + Vector((0, -0.012, r * 0.78))
    parts.append(Part(ellipsoid(centre, r * 0.97, r, r * sh["head_squash"], 24, 16), SKIN, "DEF-head"))
    # face: eyes, brows, nose, ears (eyes sit on the surface, slightly sunk in)
    for side in (-1, 1):
        eye = centre + FWD * (r * 0.9) + LEFT * (side * r * 0.34) + UP * (r * 0.02)
        parts.append(Part(ellipsoid(eye, r * 0.085, r * 0.05, r * 0.115, 10, 8), EYES, "DEF-head"))
        brow = centre + FWD * (r * 0.87) + LEFT * (side * r * 0.34) + UP * (r * 0.27)
        parts.append(Part(ellipsoid(brow, r * 0.12, r * 0.035, r * 0.03, 8, 6), HAIR, "DEF-head"))
        ear = centre + LEFT * (side * r * 0.96) + UP * (r * 0.0)
        parts.append(Part(ellipsoid(ear, r * 0.07, r * 0.11, r * 0.15, 8, 6), SKIN, "DEF-head"))
    parts.append(Part(ellipsoid(centre + FWD * (r * 0.98) + UP * (-r * 0.15), r * 0.075, r * 0.07, r * 0.065, 8, 6), SKIN, "DEF-head"))

    # arms with shoulder balls, open mitten hands with a thumb
    for s in ("L", "R"):
        sx = 1 if s == "L" else -1
        sho, ua, fa, hand = f"DEF-shoulder.{s}", f"DEF-upper_arm.{s}", f"DEF-forearm.{s}", f"DEF-hand.{s}"
        a, b, c = rig.h(ua), rig.h(fa), rig.h(hand)
        parts.append(Part(ellipsoid(a + Vector((-sx * 0.01, 0, 0)), sh["upper_arm"][0] * 1.08, sh["upper_arm"][0] * 1.05, sh["upper_arm"][0] * 1.02, 12, 8), TOP,
                          lambda p, sho=sho, ua=ua: {sho: 0.5, ua: 0.5}))
        parts.append(Part(capsule(a, b, sh["upper_arm"][0], sh["upper_arm"][1]), TOP, along(ua, a, b, parent=sho, child=fa)))
        parts.append(Part(capsule(b, c, sh["forearm"][0], sh["forearm"][1]), FOREARM, along(fa, b, c, parent=ua, child=hand)))
        hw, ht, hl = sh["hand"]
        d = (c - b).normalized()
        palm = c + d * (hl * 0.75)
        parts.append(Part(ellipsoid(palm, hl, hw, ht, 12, 8, right=d, fwd=FWD, up=UP), SKIN, hand))
        thumb_a = c + d * (hl * 0.35) + FWD * (hw * 0.6)
        thumb_b = thumb_a + d * (hl * 0.45) + FWD * (hw * 0.55)
        parts.append(Part(capsule(thumb_a, thumb_b, ht * 0.55, ht * 0.45, 8, 3, 2), SKIN, hand))

    # legs, round shoes
    for s in ("L", "R"):
        th, sn, ft = f"DEF-thigh.{s}", f"DEF-shin.{s}", f"DEF-foot.{s}"
        a, b, c = rig.h(th), rig.h(sn), rig.h(ft)
        parts.append(Part(capsule(a + Vector((0, 0, 0.04)), b, sh["thigh"][0], sh["thigh"][1]), THIGH, along(th, a, b, parent="DEF-hips", child=sn, parent_share=0.4)))
        parts.append(Part(capsule(b, c, sh["shin"][0], sh["shin"][1]), SHIN, along(sn, b, c, parent=th, child=ft)))
        fw, fl, fh = sh["foot"]
        toe = rig.h(f"DEF-toe.{s}")
        foot_centre = Vector((c.x, (c.y + toe.y) * 0.5 - 0.01, fh * 0.9))
        parts.append(Part(ellipsoid(foot_centre, fw, fl, fh, 14, 8), SHOES,
                          lambda p, ft=ft, tl=f"DEF-toe.{s}", ty=toe.y: {ft: 1.0 - smooth(ty + 0.03, ty - 0.03, p.y) * 0.5, tl: smooth(ty + 0.03, ty - 0.03, p.y) * 0.5}))

    if sh.get("skirt"):
        top = hips_z + 0.06
        bottom = rig.h("DEF-shin.L").z + 0.1
        rings = []
        for k in range(7):
            t = k / 6
            z = top + (bottom - top) * t
            rx = sh["hips"][0] * (1.02 + 0.55 * t)
            ry = sh["hips"][1] * (1.05 + 0.5 * t)
            rings.append((Vector((0, 0.012, z)), LEFT, rx, FWD, ry))

        def skirt_weights(p, top=top, bottom=bottom):
            t = max(0.0, min(1.0, (top - p.z) / (top - bottom)))
            side = "L" if p.x > 0 else "R"
            leg = t * 0.55 * smooth(0.0, 0.08, abs(p.x))
            return {"DEF-hips": 1 - leg, f"DEF-thigh.{side}": leg}

        parts.append(Part(loft(rings, 20, cap=False), BOTTOM, skirt_weights))
    return parts, centre, r


def hair_parts(style, centre, r):
    """Hair as a shell over the top and back of the head, open at the face; plus style volumes."""
    shell = ellipsoid(centre + UP * (r * 0.05) - FWD * (r * 0.04), r * 1.06, r * 1.08, r * 1.04, 24, 16)
    remove = []
    for v in shell.verts:
        rel = v.co - centre
        fwd = rel.dot(FWD) / r
        up = rel.dot(UP) / r
        side = abs(rel.dot(LEFT)) / r
        fringe = 0.42 if style != "tousled" else 0.3
        keep = up > fringe or (fwd < 0.05 and up > -0.55) or (side > 0.72 and up > 0.0 and fwd < 0.55)
        if style == "long":
            keep = keep or (fwd < 0.15 and up > -0.75)
        if not keep:
            remove.append(v)
    bmesh.ops.delete(shell, geom=remove, context="VERTS")
    parts = [Part(shell, HAIR, "DEF-head", inside=centre)]
    if style == "long":
        back = []
        steps = 10
        for k in range(steps + 1):
            t = k / steps
            z = centre.z - r * 0.1 - t * (r * 1.5)
            c = centre - FWD * (r * (0.6 + 0.12 * t))
            # full width at the head, then narrowing and rounding off at the tips
            round_off = math.sqrt(max(0.0, 1 - max(0.0, (t - 0.65) / 0.35) ** 2))
            width = r * (0.92 - 0.2 * t) * max(0.18, round_off)
            depth = r * (0.45 - 0.12 * t) * max(0.25, round_off)
            back.append((Vector((c.x, c.y, z)), LEFT, width, FWD, depth))
        parts.append(Part(loft(back, 18), HAIR, lambda p, z0=centre.z - r: {"DEF-head": 1.0 - smooth(z0, z0 - 0.3, p.z) * 0.6, "DEF-neck": smooth(z0, z0 - 0.3, p.z) * 0.6}))
    elif style == "bun":
        parts.append(Part(ellipsoid(centre - FWD * (r * 0.95) + UP * (r * 0.45), r * 0.4, r * 0.36, r * 0.38, 14, 10), HAIR, "DEF-head"))
    elif style == "tousled":
        for k, (dx, dz) in enumerate(((-0.45, 0.85), (0.0, 0.98), (0.45, 0.85), (-0.25, 0.7), (0.3, 0.72))):
            tuft = centre + LEFT * (dx * r) + UP * (dz * r) + FWD * (0.15 * r * (k % 2))
            parts.append(Part(ellipsoid(tuft, r * 0.3, r * 0.3, r * 0.22, 10, 6), HAIR, "DEF-head"))
    return parts


def make_object(name, parts, armature):
    bm = bmesh.new()
    regions, weights = [], []
    for part in parts:
        offset = len(bm.verts)
        part.bm.verts.ensure_lookup_table()
        mapping = {}
        for v in part.bm.verts:
            nv = bm.verts.new(v.co)
            mapping[v] = nv
            regions.append(part.region(v.co))
            weights.append(part.weights(v.co))
        for f in part.bm.faces:
            try:
                bm.faces.new([mapping[v] for v in f.verts])
            except ValueError:
                pass
        part.bm.free()
        del offset
    bm.normal_update()
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for poly in mesh.polygons:
        poly.use_smooth = True
    colors = mesh.color_attributes.new("Col", "FLOAT_COLOR", "POINT")
    for i, region in enumerate(regions):
        colors.data[i].color = (region / 10.0, 0.0, 0.0, 1.0)
    mesh.color_attributes.active_color = colors
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    groups = {}
    for i, w in enumerate(weights):
        total = sum(max(0.0, x) for x in w.values()) or 1.0
        for bone, value in w.items():
            if value <= 0:
                continue
            if bone not in groups:
                groups[bone] = obj.vertex_groups.new(name=bone)
            groups[bone].add([i], value / total, "REPLACE")
    obj.parent = armature
    modifier = obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = armature
    return obj


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    source, config_path, out_dir = argv[0], argv[1], argv[2]
    config = json.loads(open(config_path, encoding="utf-8").read())
    for spec in config["bodies"]:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=source)
        armature = next(o for o in bpy.data.objects if o.type == "ARMATURE")
        for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
            bpy.data.objects.remove(obj, do_unlink=True)
        rig = Rig(armature)
        body_parts, centre, r = build_body(rig, spec)
        objects = [make_object("Body", body_parts, armature)]
        for style in config["hair_styles"]:
            objects.append(make_object(f"Hair_{style}", hair_parts(style, centre, r), armature))
        bpy.ops.object.select_all(action="DESELECT")
        for obj in objects + [armature]:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = armature
        path = f"{out_dir}/{spec['id']}.glb"
        bpy.ops.export_scene.gltf(
            filepath=path,
            export_format="GLB",
            use_selection=True,
            export_animations=True,
            export_animation_mode="ACTIONS",
            export_skins=True,
            export_vertex_color="ACTIVE",
            export_yup=True,
        )
        counts = {o.name: len(o.data.vertices) for o in objects}
        print(f"FIGURE_BUILT {spec['id']} actions={len(bpy.data.actions)} meshes={counts}")


main()
