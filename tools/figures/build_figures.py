"""Builds the GlassWorld figure bodies (A1436, Marcel F5257=A) in Blender, headless.

Usage (Blender 4.5 LTS), from the repository root:
    blender -b --factory-startup --python tools/figures/build_figures.py -- SOURCE CONFIG OUT
with SOURCE = game/assets/characters/quaternius-ual1/ual1_standard.glb,
CONFIG = tools/figures/bodies.json and OUT = game/assets/characters/glassworld.

The Quaternius Universal Animation Library (CC0) supplies the rig and all clips; its mannequin mesh
is dropped. Each body is modelled from simple round shapes after D355 (big round head with eyes,
brows, nose and ears, rounded torso, soft limbs, open mitten hands with a thumb, round shoes) and
skinned to
the Quaternius bones with smooth weights at the joints. Hair styles are separate skinned meshes the
game switches by appearance data. Body regions are written into the vertex colour red channel
(index / 10) for the warm comic shader: 1 skin, 2 top, 3 bottom, 4 shoes, 5 hair, 6 shin, 7 forearm,
8 thigh, 9 eyes, 10 lower thigh. Forearm, thigh, lower thigh and shin are skin or cloth colour as
the worn clothing parts say (F4). Clothing parts (shirt, tunic, dress, skirts, trousers) are
separate skinned meshes slightly outside the body with straight hems; they carry the same body
and form blend shapes as the body, so they follow heavy/thin/strong/belly and the sex
proportions (bust, waist, hips, shoulders). The mesh is our own work (MIT, like the repository);
the clips stay CC0.
"""

import contextlib
import hashlib
import itertools
import json
import math
import sys

import bmesh
import bpy
from mathutils import Vector

SKIN, TOP, BOTTOM, SHOES, HAIR, SHIN, FOREARM, THIGH, EYES, LOWER_THIGH = (
    1,
    2,
    3,
    4,
    5,
    6,
    7,
    8,
    9,
    10,
)
UP = Vector((0, 0, 1))
FWD = Vector((0, -1, 0))  # the rig faces -Y in Blender
LEFT = Vector((1, 0, 0))  # .L bones lie on +X


def smooth(a, b, x):
    t = max(0.0, min(1.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def orient_outward(bm, inside=None):
    """Makes every face point away from the inside point (default: the centroid of the part).

    The axis mappings into the rig frame include a mirror (+X left, -Y forward), which reverses the
    winding of generated primitives; Godot culls by winding, so inverted parts would show their
    inside.
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
        rings.append(
            (a - d * (ra * math.sin(ang)), right, ra * math.cos(ang), other, ra * math.cos(ang))
        )
    for k in range(steps + 1):
        t = k / steps
        r = ra + (rb - ra) * t
        rings.append((a + d * (length * t), right, r, other, r))
    for k in range(1, cap_steps + 1):  # end cap
        ang = (math.pi / 2) * k / (cap_steps + 1)
        rings.append(
            (b + d * (rb * math.sin(ang)), right, rb * math.cos(ang), other, rb * math.cos(ang))
        )
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
    """Weights for a part along a bone, blending into parent and child near the joints."""
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


def reshape_rig(armature, spec):
    """Shorter legs and torso, narrower hips, arms closer to the body (D355 silhouette, TL #18955).

    Only bone heads and tails move (no new rolls), so every clip still applies; the ankles keep
    height, so the feet stay on the ground (clip locations are relative to the rest pose).
    """
    leg, torso = spec.get("leg_scale", 1.0), spec.get("torso_scale", 1.0)
    hip, arm_in = spec.get("hip_scale", 1.0), spec.get("arm_in", 0.0)
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bones = armature.data.edit_bones
    hips_z = bones["DEF-hips"].head.z
    ankle_z = bones["DEF-shin.L"].tail.z
    new_hips_z = ankle_z + (hips_z - ankle_z) * leg

    def is_leg(n):
        return any(k in n for k in ("thigh", "shin", "foot", "toe"))

    def is_arm(n):
        return any(k in n for k in ("upper_arm", "forearm", "hand", "f_", "thumb", "shoulder"))

    def move(name, p):
        p = p.copy()
        if is_leg(name):
            if p.z > ankle_z:
                p.z = ankle_z + (p.z - ankle_z) * leg
            p.x *= hip
        else:
            p.z = new_hips_z + (p.z - hips_z) * torso
            if is_arm(name) and abs(p.x) > 0.05:
                p.x -= math.copysign(arm_in, p.x)
        return p

    old = {b.name: (b.head.copy(), b.tail.copy()) for b in bones if b.name != "root"}
    for b in bones:
        if b.name == "root":
            continue
        b.use_connect = False
    for name, (h, tl) in old.items():
        bones[name].head = move(name, h)
        bones[name].tail = move(name, tl)
    bpy.ops.object.mode_set(mode="OBJECT")


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
    chain = [
        ("DEF-hips", rig.h("DEF-hips").z),
        ("DEF-spine.001", rig.h("DEF-spine.001").z),
        ("DEF-spine.002", rig.h("DEF-spine.002").z),
        ("DEF-spine.003", rig.h("DEF-spine.003").z),
        ("DEF-neck", rig.h("DEF-neck").z),
    ]

    def weights(p):
        w = {}
        if p.z <= chain[0][1]:
            w[chain[0][0]] = 1.0
        elif p.z >= chain[-1][1]:
            w[chain[-2][0]] = 0.6
            w[chain[-1][0]] = 0.4
        else:
            for (b0, z0), (b1, z1) in itertools.pairwise(chain):
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


MOUTHS = {
    # width, height, corner curve (all relative to the head radius); neutral is the base shape
    "neutral": (0.22, 0.028, 0.02),
    "smile": (0.27, 0.035, 0.1),
    "sad": (0.2, 0.03, -0.08),
    "surprised": (0.1, 0.12, 0.0),
    "talk": (0.19, 0.085, 0.01),
}

BODY_SHAPES = {
    # multipliers for the body radii; every shape keeps the topology, so they mix as blend shapes
    "heavy": {
        "chest": (1.25, 1.35),
        "waist": (1.45, 1.6),
        "hips": (1.25, 1.3),
        "limbs": 1.28,
        "neck": 1.2,
    },
    "thin": {
        "chest": (0.86, 0.86),
        "waist": (0.84, 0.84),
        "hips": (0.88, 0.88),
        "limbs": 0.8,
        "neck": 0.9,
    },
    "strong": {
        "chest": (1.12, 1.08),
        "waist": (1.05, 1.0),
        "hips": (1.0, 1.0),
        "arms": 1.35,
        "legs": 1.12,
    },
    "belly": {"chest": (1.0, 1.05), "waist": (1.1, 1.65), "hips": (1.05, 1.25)},
}


def shaped(spec, shape):
    """The body spec with one shape applied at full weight."""
    s = json.loads(json.dumps(spec))
    m = BODY_SHAPES[shape]
    for key in ("chest", "waist", "hips"):
        if key in m:
            s[key] = [s[key][0] * m[key][0], s[key][1] * m[key][1]]
    for key in ("upper_arm", "forearm", "thigh", "shin"):
        factor = m.get("limbs", 1.0) * m.get(
            "arms" if key in ("upper_arm", "forearm") else "legs", 1.0
        )
        s[key] = [v * factor for v in s[key]]
    s["neck_radius"] *= m.get("neck", 1.0)
    return s


FORMS = {
    # sex proportions as blend shapes (TL #19327, F4): subtle, comic, age 12+ (B2466); weight 0..1
    "bust": None,  # a soft forward hint on the chest (displacement, see bust_offset)
    "waist": {"waist": (0.88, 0.92)},
    "hips": {"hips": (1.1, 1.06)},
    "shoulders_narrow": {"shoulders": 0.9},
    "shoulders_broad": {"shoulders": 1.1, "chest": (1.04, 1.02)},
}


def formed(spec, form):
    """The body spec with one sex proportion at full weight."""
    s = json.loads(json.dumps(spec))
    s.setdefault("form", {})[form] = 1.0
    return s


def torso_profile(rig, spec):
    """Rings (z, rx, ry) of the torso from below the hips to the neck, with the sex proportions."""
    hips_z = rig.h("DEF-hips").z
    top_z = rig.h("DEF-neck").z
    form = spec.get("form", {})
    waist_k, hips_k, chest_k = [1.0, 1.0], [1.0, 1.0], [1.0, 1.0]
    shoulders = 1.0
    for name, weight in form.items():
        m = FORMS[name]
        if not m or weight == 0:
            continue
        for key, target in (("waist", waist_k), ("hips", hips_k), ("chest", chest_k)):
            if key in m:
                target[0] *= 1 + (m[key][0] - 1) * weight
                target[1] *= 1 + (m[key][1] - 1) * weight
        if "shoulders" in m:
            shoulders *= 1 + (m["shoulders"] - 1) * weight
    profile = [
        (hips_z - 0.07, 0.6),
        (hips_z - 0.03, 0.92),
        (hips_z + 0.02, 1.0),
        (hips_z + 0.12, 0.0),
        (hips_z + 0.24, 0.5),
        (hips_z + 0.36, 1.0),
        (top_z - 0.08, 1.0),
        (top_z - 0.03, 0.9),
        (top_z + 0.0, 0.66),
        (top_z + 0.02, 0.34),
    ]
    hips = [spec["hips"][0] * hips_k[0], spec["hips"][1] * hips_k[1]]
    waist = [spec["waist"][0] * waist_k[0], spec["waist"][1] * waist_k[1]]
    chest = [spec["chest"][0] * chest_k[0], spec["chest"][1] * chest_k[1]]
    rings = []
    for i, (z, k) in enumerate(profile):
        if i < 3:
            rx, ry = hips[0] * k, hips[1] * k
        elif i == 3:
            rx, ry = waist
        else:
            rx = waist[0] + (chest[0] - waist[0]) * k if i < 7 else chest[0] * k
            ry = waist[1] + (chest[1] - waist[1]) * k if i < 7 else chest[1] * k
        if i >= 6:
            rx *= shoulders
        rings.append((z, rx, ry))
    return rings


def profile_at(rings, z):
    """Torso radii at height z (linear between the rings, clamped at the ends)."""
    if z <= rings[0][0]:
        return rings[0][1], rings[0][2]
    for (z0, ax, ay), (z1, bx, by) in itertools.pairwise(rings):
        if z0 <= z <= z1:
            t = (z - z0) / (z1 - z0)
            return ax + (bx - ax) * t, ay + (by - ay) * t
    return rings[-1][1], rings[-1][2]


def bust_offset(rig, spec):
    """Forward offset of the chest front for the bust form: two soft, low bumps (comic, subtle)."""
    weight = spec.get("form", {}).get("bust", 0.0)
    zc = rig.h("DEF-hips").z + 0.39
    spread = spec["chest"][0] * 0.45

    def offset(p):
        if weight == 0 or p.y > 0.012:  # only the front half (the rig faces -Y)
            return Vector()
        front = smooth(0.012, -0.05, p.y)
        bump = 0.0
        for side in (-1, 1):
            dx, dz = (p.x - side * spread) / 0.075, (p.z - zc) / 0.07
            bump = max(bump, math.exp(-(dx * dx + dz * dz)))
        return FWD * (0.024 * weight * bump * front)

    return offset


def displace(bm, offset):
    for v in bm.verts:
        v.co += offset(v.co)
    return bm


def mouth(centre, r, shape):
    """A drawn-looking mouth on the face: a flat dark ellipse bent by the expression."""
    width, height, curve = MOUTHS[shape]
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=14, v_segments=6, radius=1.0)
    for vert in bm.verts:
        x, y, z = vert.co
        lx = x * width * r
        lz = z * height * r + curve * r * (x * x - 0.35)
        ly = y * 0.035 * r
        vert.co = centre + LEFT * lx + FWD * ly + UP * lz
    return bm


def build_body(rig, spec, expression="neutral", blink=False):
    parts = []
    hips_z = rig.h("DEF-hips").z
    sh = spec
    # torso: rings from below the hips to the shoulders, wider at the chest (with the sex forms)
    rings = [(Vector((0, 0.012, z)), LEFT, rx, FWD, ry) for z, rx, ry in torso_profile(rig, sh)]
    parts.append(
        Part(
            displace(loft(rings, 20), bust_offset(rig, sh)),
            lambda p, hz=hips_z: BOTTOM if p.z < hz + 0.07 else TOP,
            torso_weights(rig),
        )
    )

    # neck and head
    neck_a, neck_b = rig.h("DEF-neck"), rig.h("DEF-head")
    parts.append(
        Part(
            capsule(
                neck_a + Vector((0, 0, -0.02)),
                neck_b + Vector((0, 0, 0.03)),
                sh["neck_radius"],
                sh["neck_radius"] * 0.95,
            ),
            SKIN,
            along("DEF-neck", neck_a, neck_b, parent="DEF-spine.003", child="DEF-head"),
        )
    )
    r = sh["head_radius"]
    centre = rig.h("DEF-head") + Vector((0, -0.012, r * 0.78))
    parts.append(
        Part(ellipsoid(centre, r * 0.97, r, r * sh["head_squash"], 24, 16), SKIN, "DEF-head")
    )
    # face: eyes, brows, nose, ears (eyes sit on the surface, slightly sunk in)
    for side in (-1, 1):
        eye = centre + FWD * (r * 0.9) + LEFT * (side * r * 0.34) + UP * (r * 0.02)
        eye_height = r * (0.012 if blink else 0.115)
        parts.append(Part(ellipsoid(eye, r * 0.085, r * 0.05, eye_height, 10, 8), EYES, "DEF-head"))
        brow = centre + FWD * (r * 0.87) + LEFT * (side * r * 0.34) + UP * (r * 0.27)
        parts.append(Part(ellipsoid(brow, r * 0.12, r * 0.035, r * 0.03, 8, 6), HAIR, "DEF-head"))
        ear = centre + LEFT * (side * r * 0.96) + UP * (r * 0.0)
        parts.append(Part(ellipsoid(ear, r * 0.07, r * 0.11, r * 0.15, 8, 6), SKIN, "DEF-head"))
    parts.append(
        Part(
            ellipsoid(
                centre + FWD * (r * 0.98) + UP * (-r * 0.15), r * 0.075, r * 0.07, r * 0.065, 8, 6
            ),
            SKIN,
            "DEF-head",
        )
    )

    mouth_centre = centre + FWD * (r * 0.905) + UP * (-r * 0.4)
    parts.append(Part(mouth(mouth_centre, r, expression), EYES, "DEF-head", inside=centre))

    # arms with seamless sleeves, open mitten hands with a thumb
    for s in ("L", "R"):
        sho, ua, fa, hand = (
            f"DEF-shoulder.{s}",
            f"DEF-upper_arm.{s}",
            f"DEF-forearm.{s}",
            f"DEF-hand.{s}",
        )
        a, b, c = rig.h(ua), rig.h(fa), rig.h(hand)
        d_ua = (b - a).normalized()
        # the sleeve starts deep inside the torso and slimmer, so no ball stands out at the shoulder
        inner = a - d_ua * (sh["upper_arm"][0] * 1.7)
        sleeve = sh.get("sleeve", 0.5)
        parts.append(
            Part(
                capsule(inner, b, sh["upper_arm"][0] * 0.9, sh["upper_arm"][1]),
                lambda p, a=a, b=b, s=sleeve: (
                    TOP if (p - a).dot(b - a) / (b - a).length_squared < s else FOREARM
                ),
                along(ua, inner, b, parent=sho, child=fa, parent_share=0.6),
            )
        )
        parts.append(
            Part(
                capsule(b, c, sh["forearm"][0], sh["forearm"][1]),
                FOREARM,
                along(fa, b, c, parent=ua, child=hand),
            )
        )
        hw, ht, hl = sh["hand"]
        d = (c - b).normalized()
        palm = c + d * (hl * 0.75)
        parts.append(Part(ellipsoid(palm, hl, hw, ht, 12, 8, right=d, fwd=FWD, up=UP), SKIN, hand))
        thumb_a = c + d * (hl * 0.35) + FWD * (hw * 0.6)
        thumb_b = thumb_a + d * (hl * 0.45) + FWD * (hw * 0.55)
        parts.append(Part(capsule(thumb_a, thumb_b, ht * 0.55, ht * 0.45, 8, 3, 2), SKIN, hand))
        # open hand: four short fingers lie straight beyond the palm (weighted to the hand bone)
        for k in range(4):
            across = (k - 1.5) * hw * 0.5
            root = palm + d * (hl * 0.8) + FWD * across
            tip = root + d * (hl * 0.75)
            parts.append(Part(capsule(root, tip, ht * 0.42, ht * 0.36, 8, 3, 2), SKIN, hand))

    # legs, round shoes
    for s in ("L", "R"):
        th, sn, ft = f"DEF-thigh.{s}", f"DEF-shin.{s}", f"DEF-foot.{s}"
        a, b, c = rig.h(th), rig.h(sn), rig.h(ft)
        split = sh.get("thigh_split", 0.5)

        def thigh_region(p, a=a, b=b, s=split):
            return THIGH if (p - a).dot(b - a) / (b - a).length_squared < s else LOWER_THIGH

        parts.append(
            Part(
                capsule(a + Vector((0, 0, 0.04)), b, sh["thigh"][0], sh["thigh"][1]),
                thigh_region,
                along(th, a, b, parent="DEF-hips", child=sn, parent_share=0.4),
            )
        )
        parts.append(
            Part(
                capsule(b, c, sh["shin"][0], sh["shin"][1]),
                SHIN,
                along(sn, b, c, parent=th, child=ft),
            )
        )
        fw, fl, fh = sh["foot"]
        toe = rig.h(f"DEF-toe.{s}")
        foot_centre = Vector((c.x, (c.y + toe.y) * 0.5 - 0.01, fh * 0.9))
        shoe = ellipsoid(foot_centre, fw, fl, fh, 14, 8)
        for v in shoe.verts:
            v.co.z = max(v.co.z, 0.004)  # flat sole on the ground
        parts.append(
            Part(
                shoe,
                SHOES,
                lambda p, ft=ft, tl=f"DEF-toe.{s}", ty=toe.y: {
                    ft: 1.0 - smooth(ty + 0.03, ty - 0.03, p.y) * 0.5,
                    tl: smooth(ty + 0.03, ty - 0.03, p.y) * 0.5,
                },
            )
        )

    return parts, centre, r


HAIR_STYLES = ("short", "shoulder_length", "long", "braid", "bun", "curls")  # bald: no mesh


def hair_shell(style, centre, r):
    """A shell over the top and back of the head, open at the face."""
    shell = ellipsoid(
        centre + UP * (r * 0.05) - FWD * (r * 0.04), r * 1.06, r * 1.08, r * 1.04, 24, 16
    )
    remove = []
    for v in shell.verts:
        rel = v.co - centre
        fwd = rel.dot(FWD) / r
        up = rel.dot(UP) / r
        side = abs(rel.dot(LEFT)) / r
        fringe = 0.3 if style == "curls" else 0.42
        keep = (
            up > fringe
            or (fwd < 0.05 and up > -0.55)
            or (fwd < -0.3 and up > -0.85)
            or (side > 0.72 and up > 0.0 and fwd < 0.55)
        )
        if style in ("long", "shoulder_length"):
            keep = keep or (fwd < 0.15 and up > -0.75)
        if not keep:
            remove.append(v)
    bmesh.ops.delete(shell, geom=remove, context="VERTS")
    return shell


def neck_blend(centre, r):
    """Weights for hair hanging down the back: head, blending into the neck lower down."""

    def weights(p, z0=centre.z - r):
        share = smooth(z0, z0 - 0.3, p.z) * 0.6
        return {"DEF-head": 1.0 - share, "DEF-neck": share}

    return weights


def back_volume(centre, r, length):
    """Hair falling down the back, full width at the head, rounding off at the tips."""
    back = []
    steps = 10
    for k in range(steps + 1):
        t = k / steps
        z = centre.z - r * 0.1 - t * (r * length)
        c = centre - FWD * (r * (0.6 + 0.12 * t))
        round_off = math.sqrt(max(0.0, 1 - max(0.0, (t - 0.65) / 0.35) ** 2))
        width = r * (0.92 - 0.2 * t) * max(0.18, round_off)
        depth = r * (0.45 - 0.12 * t) * max(0.25, round_off)
        back.append((Vector((c.x, c.y, z)), LEFT, width, FWD, depth))
    return Part(loft(back, 18), HAIR, neck_blend(centre, r))


def hair_parts(style, centre, r):
    """One hair style as parts: shell plus the volumes of the style (F4 hair kit)."""
    parts = [Part(hair_shell(style, centre, r), HAIR, "DEF-head", inside=centre)]
    if style == "long":
        parts.append(back_volume(centre, r, 1.5))
    elif style == "shoulder_length":
        parts.append(back_volume(centre, r, 0.95))
    if style == "short":
        for k, side in enumerate((-0.38, 0.0, 0.38)):
            lobe = (
                centre - FWD * (r * 0.78) + LEFT * (side * r) + UP * (r * (-0.28 - 0.06 * (k % 2)))
            )
            parts.append(
                Part(ellipsoid(lobe, r * 0.34, r * 0.28, r * 0.36, 12, 8), HAIR, "DEF-head")
            )
    elif style == "bun":
        parts.append(
            Part(
                ellipsoid(
                    centre - FWD * (r * 0.95) + UP * (r * 0.45), r * 0.4, r * 0.36, r * 0.38, 14, 10
                ),
                HAIR,
                "DEF-head",
            )
        )
    elif style == "braid":
        # one plait down the back: overlapping, slightly alternating beads, a small tie at the end
        for k in range(7):
            t = k / 6
            bead = (
                centre
                - FWD * (r * (0.86 + 0.08 * t))
                + LEFT * (r * 0.05 * (1 if k % 2 else -1))
                + UP * (-r * (0.35 + 1.25 * t))
            )
            size = r * (0.2 - 0.07 * t)
            parts.append(
                Part(
                    ellipsoid(bead, size, size * 0.85, size * 1.25, 10, 8),
                    HAIR,
                    neck_blend(centre, r),
                )
            )
        tie = centre - FWD * (r * 0.95) + UP * (-r * 1.72)
        parts.append(
            Part(ellipsoid(tie, r * 0.1, r * 0.1, r * 0.08, 8, 6), HAIR, neck_blend(centre, r))
        )
    elif style == "curls":
        # round curls all over the top, sides and back (not in the face)
        for row, (up, count) in enumerate(((0.92, 3), (0.62, 6), (0.28, 7), (-0.12, 6))):
            for k in range(count):
                ang = (
                    math.pi * (0.15 + 0.7 * k / max(1, count - 1))
                    if row
                    else math.pi * (0.3 + 0.4 * k / max(1, count - 1))
                )
                ring = math.sqrt(max(0.0, 1 - up * up))
                direction = LEFT * (math.cos(ang) * ring) - FWD * (math.sin(ang) * ring) + UP * up
                if row == 0:
                    direction = LEFT * ((k - 1) * 0.35) + UP * up - FWD * 0.1
                curl = centre + direction.normalized() * (r * 1.02)
                parts.append(
                    Part(ellipsoid(curl, r * 0.26, r * 0.26, r * 0.24, 10, 6), HAIR, "DEF-head")
                )
    return parts


GARMENTS = (
    "shirt",
    "tunic",
    "dress",
    "skirt_short",
    "skirt_long",
    "trousers_short",
    "trousers_long",
)
TOP_OFFSET, BOTTOM_OFFSET = 1.1, 1.07


def skirt_weights(rig, top):
    """Below the hips a skirt follows the hips and, more and more towards the hem, the thighs."""
    torso = torso_weights(rig)
    knee = rig.h("DEF-shin.L").z

    def weights(p):
        if p.z >= top:
            return torso(p)
        t = max(0.0, min(1.0, (top - p.z) / max(0.05, top - knee)))
        side = "L" if p.x > 0 else "R"
        leg = t * 0.7 * smooth(0.0, 0.08, abs(p.x))
        return {"DEF-hips": 1 - leg, f"DEF-thigh.{side}": leg}

    return weights


def wrap(rig, spec, top_z, bottom_z, offset, flare, steps, collar=None):
    """A garment around the torso from top_z down to bottom_z: the torso shape scaled outwards.

    Below the hips it widens by flare towards the straight hem; collar is the neck opening radius.
    """
    profile = torso_profile(rig, spec)
    hips_z = rig.h("DEF-hips").z
    flare_from = hips_z - 0.03
    # round D355 hips: the thighs stand out of the torso, so a garment there must clear them
    thigh_x, thigh_r = abs(rig.h("DEF-thigh.L").x), spec["thigh"][0] * 1.22 * offset
    clear = thigh_x * offset + thigh_r
    rings = []
    if collar is not None:
        rings.append((Vector((0, 0.012, top_z + 0.012)), LEFT, collar, FWD, collar * 0.9))
    for k in range(steps + 1):
        z = top_z + (bottom_z - top_z) * k / steps
        if z >= flare_from:
            rx, ry = profile_at(profile, z)
            rx, ry = rx * offset, ry * offset
        else:
            hx, hy = profile_at(profile, flare_from)
            t = (flare_from - z) / max(0.01, flare_from - bottom_z)
            rx, ry = hx * (offset + flare * t), hy * (offset + flare * 0.8 * t)
        if z < hips_z + 0.1:
            rx = max(rx, clear * (1 + flare * max(0.0, (flare_from - z)) * 2))
            # the ellipse must also hold the thigh circle in depth: ry >= r / sqrt(1 - (x / rx)^2)
            inner = max(0.05, 1 - (thigh_x * offset / rx) ** 2)
            ry = max(ry, thigh_r / math.sqrt(inner))
        rings.append((Vector((0, 0.012, z)), LEFT, rx, FWD, ry))
    # straight hem with a small lip
    centre, _, rx, _, ry = rings[-1]
    rings.append((centre + Vector((0, 0, -0.004)), LEFT, rx * 1.03, FWD, ry * 1.03))
    return loft(rings, 20, cap=False)


def tube(points, radii, segments=14):
    """An open tube through the points, rings perpendicular to the path, hem lip at the end."""
    rings = []
    for i, (pt, r) in enumerate(zip(points, radii, strict=True)):
        a = points[max(0, i - 1)]
        b = points[min(len(points) - 1, i + 1)]
        right, other = frame(b - a)
        rings.append((pt, right, r, other, r))
    pt, right, r, other, _ = rings[-1]
    d = (points[-1] - points[-2]).normalized()
    rings.append((pt + d * 0.004, right, r * 1.06, other, r * 1.06))
    return loft(rings, segments, cap=False)


def sleeves(rig, spec, length):
    parts = []
    for s in ("L", "R"):
        sho, ua, fa = f"DEF-shoulder.{s}", f"DEF-upper_arm.{s}", f"DEF-forearm.{s}"
        a, b = rig.h(ua), rig.h(fa)
        d = (b - a).normalized()
        inner = a - d * (spec["upper_arm"][0] * 1.2)
        hem = a + (b - a) * length
        points = [inner + (hem - inner) * (k / 5) for k in range(6)]
        radii = [spec["upper_arm"][0] * (1.24 - 0.06 * k / 5) for k in range(6)]
        parts.append(
            Part(
                tube(points, radii),
                TOP,
                along(ua, inner, b, parent=sho, child=fa, parent_share=0.6),
            )
        )
    return parts


def legs(rig, spec, long):
    parts = []
    for s in ("L", "R"):
        th, sn, ft = f"DEF-thigh.{s}", f"DEF-shin.{s}", f"DEF-foot.{s}"
        a, b, c = rig.h(th) + Vector((0, 0, -0.03)), rig.h(sn), rig.h(ft)
        thigh_w = along(th, rig.h(th), b, parent="DEF-hips", child=sn, parent_share=0.4)
        shin_w = along(sn, b, c, parent=th, child=ft)
        if long:
            points = [a + (b - a) * (k / 4) for k in range(5)]
            points += [b + (c - b) * (k / 4) for k in range(1, 4)]
            radii = [spec["thigh"][0] * 1.16 - k * 0.002 for k in range(5)]
            radii += [spec["shin"][0] * (1.2 + 0.05 * k) for k in range(1, 4)]
        else:
            hem = a + (b - a) * 0.58
            points = [a + (hem - a) * (k / 4) for k in range(5)]
            radii = [spec["thigh"][0] * (1.16 + 0.02 * k / 4) for k in range(5)]

        def weights(p, a=a, b=b, tw=thigh_w, sw=shin_w):
            t = (p - a).dot(b - a) / (b - a).length_squared
            return tw(p) if t <= 1.0 else sw(p)

        parts.append(Part(tube(points, radii, 16), BOTTOM, weights))
    return parts


def garment_parts(kind, rig, spec):
    """One clothing part (F4) as skinned parts around the body; tops in the top colour, skirts and
    trousers in the bottom colour; hems are straight rings (no slanted painted hems)."""
    hips_z = rig.h("DEF-hips").z
    neck_z = rig.h("DEF-neck").z
    thigh_a, knee = rig.h("DEF-thigh.L"), rig.h("DEF-shin.L")
    ankle = rig.h("DEF-foot.L")
    mid_thigh = thigh_a.z + (knee.z - thigh_a.z) * 0.55
    collar = spec["neck_radius"] * 1.7
    bust = bust_offset(rig, spec)
    if kind in ("shirt", "tunic", "dress"):
        bottom = {"shirt": hips_z + 0.04, "tunic": mid_thigh, "dress": knee.z - 0.2}[kind]
        flare = {"shirt": 0.0, "tunic": 0.22, "dress": 0.42}[kind]
        steps = {"shirt": 12, "tunic": 18, "dress": 22}[kind]
        body = wrap(rig, spec, neck_z + 0.0, bottom, TOP_OFFSET, flare, steps, collar)
        parts = [Part(displace(body, bust), TOP, skirt_weights(rig, hips_z - 0.02))]
        return parts + sleeves(rig, spec, 0.58)
    if kind in ("skirt_short", "skirt_long"):
        bottom = knee.z + 0.05 if kind == "skirt_short" else ankle.z + 0.1
        flare = 0.38 if kind == "skirt_short" else 0.55
        steps = 10 if kind == "skirt_short" else 16
        body = wrap(rig, spec, hips_z + 0.09, bottom, BOTTOM_OFFSET, flare, steps)
        return [Part(body, BOTTOM, skirt_weights(rig, hips_z - 0.02))]
    if kind in ("trousers_short", "trousers_long"):
        pelvis = wrap(rig, spec, hips_z + 0.09, hips_z - 0.08, BOTTOM_OFFSET, 0.0, 5)
        return [Part(pelvis, BOTTOM, torso_weights(rig)), *legs(rig, spec, kind == "trousers_long")]
    raise ValueError(f"unknown garment '{kind}'")


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
            with contextlib.suppress(ValueError):  # duplicate faces where parts touch
                bm.faces.new([mapping[v] for v in f.verts])
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


def add_keys(obj, variants, build):
    """Blend shapes from rebuilt variants of the same parts (same topology, so they mix)."""
    obj.shape_key_add(name="Basis", from_mix=False)
    for name, variant in variants:
        coords = []
        for part in build(variant):
            part.bm.verts.ensure_lookup_table()
            coords.extend(v.co.copy() for v in part.bm.verts)
            part.bm.free()
        if len(coords) != len(obj.data.vertices):
            raise RuntimeError(
                f"{obj.name}: shape '{name}' changed the topology"
                f" ({len(coords)} vs {len(obj.data.vertices)})"
            )
        key = obj.shape_key_add(name=name, from_mix=False)
        for i, co in enumerate(coords):
            key.data[i].co = co


def shape_variants(spec, forms):
    """Body shapes (B2528) and the body's sex proportions (F4), named body_<id> on every mesh."""
    variants = [(f"body_{name}", shaped(spec, name)) for name in BODY_SHAPES]
    return variants + [(f"body_{form}", formed(spec, form)) for form in forms]


def add_shape_keys(body, rig, spec, forms):
    """Blend shapes of the body: shapes and forms, mouth expressions and blink."""
    variants = [((name, s), "neutral", False) for name, s in shape_variants(spec, forms)]
    variants += [((f"mouth_{m}", spec), m, False) for m in MOUTHS if m != "neutral"]
    variants.append((("blink", spec), "neutral", True))
    body.shape_key_add(name="Basis", from_mix=False)
    for (name, variant_spec), expression, blink in variants:
        parts, _, _ = build_body(rig, variant_spec, expression, blink)
        coords = []
        for part in parts:
            part.bm.verts.ensure_lookup_table()
            coords.extend(v.co.copy() for v in part.bm.verts)
            part.bm.free()
        if len(coords) != len(body.data.vertices):
            raise RuntimeError(
                f"shape '{name}' changed the topology ({len(coords)} vs {len(body.data.vertices)})"
            )
        key = body.shape_key_add(name=name, from_mix=False)
        for i, co in enumerate(coords):
            key.data[i].co = co


def main():
    argv = sys.argv[sys.argv.index("--") + 1 :]
    source, config_path, out_dir = argv[0], argv[1], argv[2]
    with open(config_path, encoding="utf-8") as file:
        config = json.load(file)
    for spec in config["bodies"]:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=source)
        armature = next(o for o in bpy.data.objects if o.type == "ARMATURE")
        for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
            bpy.data.objects.remove(obj, do_unlink=True)
        reshape_rig(armature, spec)
        rig = Rig(armature)
        body_parts, centre, r = build_body(rig, spec)
        body = make_object("Body", body_parts, armature)
        forms = spec.get("forms", [])
        add_shape_keys(body, rig, spec, forms)
        objects = [body]
        for style in config["hair_styles"]:
            objects.append(make_object(f"Hair_{style}", hair_parts(style, centre, r), armature))
        for kind in config["garments"]:
            cloth = make_object(f"Cloth_{kind}", garment_parts(kind, rig, spec), armature)
            add_keys(
                cloth,
                shape_variants(spec, forms),
                lambda s, k=kind, rg=rig: garment_parts(k, rg, s),
            )
            objects.append(cloth)
        bpy.ops.object.select_all(action="DESELECT")
        for obj in [*objects, armature]:
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
            export_morph=True,
            export_morph_normal=True,
        )
        counts = {o.name: len(o.data.vertices) for o in objects}
        keys = [k.name for k in body.data.shape_keys.key_blocks][1:]
        print(f"FIGURE_SHAPES {spec['id']} {','.join(keys)}")
        print(f"FIGURE_BUILT {spec['id']} actions={len(bpy.data.actions)} meshes={counts}")
    write_manifest(source, config_path, out_dir, [spec["id"] for spec in config["bodies"]])


def sha256(path):
    with open(path, "rb") as file:
        return hashlib.sha256(file.read()).hexdigest()


def write_manifest(source, config_path, out_dir, ids):
    """Records the inputs and outputs of a build, so a rebuild with other inputs shows in review.

    The GLB bytes differ from build to build (Blender export order); the body hashes identify the
    committed files, the input hashes and the Blender version say how to rebuild them.
    """
    manifest = {
        "tool": "tools/figures/build_figures.py",
        "tool_sha256": sha256(__file__),
        "blender": bpy.app.version_string,
        "source": source.replace("\\", "/"),
        "source_sha256": sha256(source),
        "config_sha256": sha256(config_path),
        "bodies": {i: sha256(f"{out_dir}/{i}.glb") for i in ids},
    }
    with open(f"{out_dir}/manifest.json", "w", encoding="utf-8", newline="\n") as file:
        json.dump(manifest, file, indent=2)
        file.write("\n")
    print(f"FIGURE_MANIFEST {out_dir}/manifest.json")


main()
