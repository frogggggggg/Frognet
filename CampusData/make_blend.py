"""Run headless via Blender to turn gt_campus.obj into a ready-to-open .blend.

    blender --background --python make_blend.py

Adds metric units, sun/sky, a camera, sensible viewport clipping, and a procedural
facade shader that generates window grids on any near-vertical surface.
"""

import math
import os
import shutil
import sys
import tempfile
import time

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
OBJ_PATH = os.path.join(HERE, "gt_campus.obj")
BLEND_PATH = os.path.join(HERE, "gt_campus.blend")

# Viewport/camera clipping. The old 0.1 m near plane against a 20 km far plane left the
# depth buffer with only a few centimetres of resolution a kilometre out, which is what
# made the roads and ground z-fight. 1 m / 8 km gives far more usable depth precision.
CLIP_START = 1.0
CLIP_END = 8000.0

FLOOR_HEIGHT = 3.9   # fallback storey height where a building has no storey count
WINDOW_PITCH = 2.7   # metres between window centres along a facade
DOOR_PITCH = 11.0    # metres between entrance doors along a ground floor


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.lights, bpy.data.cameras):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def math_node(nt, op, loc, a=None, b=None, c=None):
    """Create a Math node, wiring sockets or setting constants, and return its output."""
    node = nt.nodes.new("ShaderNodeMath")
    node.operation = op
    node.location = loc
    for index, value in ((0, a), (1, b), (2, c)):
        if value is None:
            continue
        if isinstance(value, bpy.types.NodeSocket):
            nt.links.new(value, node.inputs[index])
        else:
            node.inputs[index].default_value = value
    return node.outputs[0]


def facade_material(mat, wall_rgb, glass_rgb=(0.06, 0.10, 0.14, 1.0), variation=0.35,
                    brick_share=0.55):
    """Facade detail generated from per-building attributes baked into the UVs.

    The source scene layer is untextured massing, so windows have to be generated. Each
    vertex carries its building's own base elevation (U) and, packed into V, that
    building's height and true storey height. Bands are therefore measured from the
    building's own ground floor rather than a single global grid, which is what makes
    floor lines land where the real ones do. A taller glazed ground floor, doors and a
    solid parapet at roof level come from the same two numbers.
    """
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    link = nt.links.new

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (2000, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (1700, 0)
    link(bsdf.outputs["BSDF"], out.inputs["Surface"])

    geo = nt.nodes.new("ShaderNodeNewGeometry")
    geo.location = (-1800, 0)
    uvmap = nt.nodes.new("ShaderNodeUVMap")
    uvmap.location = (-1800, -400)
    sep_p = nt.nodes.new("ShaderNodeSeparateXYZ")
    sep_p.location = (-1600, 250)
    sep_n = nt.nodes.new("ShaderNodeSeparateXYZ")
    sep_n.location = (-1600, -150)
    sep_uv = nt.nodes.new("ShaderNodeSeparateXYZ")
    sep_uv.location = (-1600, -400)
    link(geo.outputs["Position"], sep_p.inputs[0])
    link(geo.outputs["Normal"], sep_n.inputs[0])
    link(uvmap.outputs["UV"], sep_uv.inputs[0])
    px, py, pz = sep_p.outputs["X"], sep_p.outputs["Y"], sep_p.outputs["Z"]
    nx, ny, nz = sep_n.outputs["X"], sep_n.outputs["Y"], sep_n.outputs["Z"]

    base_z = sep_uv.outputs["X"]
    packed = sep_uv.outputs["Y"]
    bld_h = math_node(nt, "FLOOR", (-1400, -500), math_node(nt, "DIVIDE", (-1500, -500), packed, 100.0))
    floor_h = math_node(nt, "MAXIMUM", (-1200, -560),
                        math_node(nt, "SUBTRACT", (-1300, -560), packed,
                                  math_node(nt, "MULTIPLY", (-1400, -640), bld_h, 100.0)), 2.6)

    wall = math_node(nt, "LESS_THAN", (-1150, -150),
                     math_node(nt, "ABSOLUTE", (-1330, -150), nz), 0.4)

    # A stable hash of the two baked attributes is constant over a whole building, so
    # each building draws its own cladding family and window rhythm instead of the whole
    # campus sharing one texture.
    #
    # The inputs MUST be quantised first. They are UV attributes that are constant per
    # face, but interpolation still delivers them with a part-per-million wobble, and
    # d(rnd)/d(base_z) here is about 5e5 - so that wobble came out as a full-range random
    # value at every pixel. The result was window bays and brick/precast choice flickering
    # per pixel, which read as a diagonal weave crawling over every facade.
    qb = math_node(nt, "FLOOR", (-1700, -860),
                   math_node(nt, "MULTIPLY_ADD", (-1850, -860), base_z, 2.0, 0.37))
    qp = math_node(nt, "FLOOR", (-1700, -960),
                   math_node(nt, "MULTIPLY_ADD", (-1850, -960), packed, 4.0, 0.11))
    rnd = math_node(nt, "FRACT", (-1000, -900),
                    math_node(nt, "MULTIPLY", (-1150, -900),
                              math_node(nt, "SINE", (-1300, -900),
                                        math_node(nt, "ADD", (-1450, -900),
                                                  math_node(nt, "MULTIPLY", (-1600, -860),
                                                            qb, 12.9898),
                                                  math_node(nt, "MULTIPLY", (-1600, -960),
                                                            qp, 0.0783))),
                              43758.5453))
    is_brick = math_node(nt, "LESS_THAN", (-850, -900), rnd, brick_share)
    pitch = math_node(nt, "MULTIPLY_ADD", (-850, 700), rnd,
                      WINDOW_PITCH * 0.55, WINDOW_PITCH * 0.78)

    # Height above this building's own ground floor.
    h = math_node(nt, "SUBTRACT", (-1150, 250), pz, base_z)
    ground_h = math_node(nt, "MULTIPLY", (-1150, 100), floor_h, 1.45)

    # Distance along the wall: project position onto the horizontal tangent (-ny, nx).
    u = math_node(nt, "SUBTRACT", (-1150, 520),
                  math_node(nt, "MULTIPLY", (-1330, 580), py, nx),
                  math_node(nt, "MULTIPLY", (-1330, 460), px, ny))

    # --- upper floors -----------------------------------------------------------
    fu = math_node(nt, "FRACT", (-800, 520), math_node(nt, "DIVIDE", (-970, 520), u, pitch))
    band_u = math_node(nt, "MULTIPLY", (-450, 520),
                       math_node(nt, "GREATER_THAN", (-630, 580), fu, 0.24),
                       math_node(nt, "LESS_THAN", (-630, 460), fu, 0.76))
    fv = math_node(nt, "FRACT", (-800, 300),
                   math_node(nt, "DIVIDE", (-970, 300),
                             math_node(nt, "SUBTRACT", (-1150, 380), h, ground_h), floor_h))
    band_v = math_node(nt, "MULTIPLY", (-450, 300),
                       math_node(nt, "GREATER_THAN", (-630, 360), fv, 0.26),
                       math_node(nt, "LESS_THAN", (-630, 240), fv, 0.78))
    upper = math_node(nt, "MULTIPLY", (-250, 400),
                      math_node(nt, "MULTIPLY", (-380, 400), band_u, band_v),
                      math_node(nt, "GREATER_THAN", (-450, 160), h, ground_h))

    # --- glazed ground floor and entrance doors ---------------------------------
    fg = math_node(nt, "FRACT", (-800, 20), math_node(nt, "DIVIDE", (-970, 20),
                                                      u, math_node(nt, "MULTIPLY", (-1100, 20),
                                                                   pitch, 0.75)))
    store_u = math_node(nt, "MULTIPLY", (-450, 20),
                        math_node(nt, "GREATER_THAN", (-630, 80), fg, 0.16),
                        math_node(nt, "LESS_THAN", (-630, -40), fg, 0.84))
    store_v = math_node(nt, "MULTIPLY", (-450, -140),
                        math_node(nt, "GREATER_THAN", (-630, -110), h, 0.55),
                        math_node(nt, "LESS_THAN", (-630, -190), h,
                                  math_node(nt, "MULTIPLY", (-800, -190), ground_h, 0.86)))
    ground = math_node(nt, "MULTIPLY", (-250, -60),
                       math_node(nt, "MULTIPLY", (-380, -60), store_u, store_v),
                       math_node(nt, "LESS_THAN", (-450, -260), h, ground_h))

    fd = math_node(nt, "FRACT", (-800, -380), math_node(nt, "DIVIDE", (-970, -380), u, DOOR_PITCH))
    door = math_node(nt, "MULTIPLY", (-250, -380),
                     math_node(nt, "MULTIPLY", (-450, -320),
                               math_node(nt, "GREATER_THAN", (-630, -300), fd, 0.40),
                               math_node(nt, "LESS_THAN", (-630, -380), fd, 0.60)),
                     math_node(nt, "LESS_THAN", (-450, -440), h, 2.35))

    # --- parapet: no glazing in the top 0.9 m of the building --------------------
    below_parapet = math_node(nt, "LESS_THAN", (-250, 620), h,
                              math_node(nt, "SUBTRACT", (-450, 680), bld_h, 0.9))

    glazing = math_node(nt, "MINIMUM", (100, 200),
                        math_node(nt, "ADD", (-50, 200), upper, ground), 1.0)
    glazing = math_node(nt, "MULTIPLY", (250, 200),
                        math_node(nt, "MULTIPLY", (100, 350), glazing, below_parapet), wall)
    door = math_node(nt, "MULTIPLY", (250, -380), door, wall)

    # --- window frames ----------------------------------------------------------
    # A slightly larger copy of each opening, minus the opening itself, is the frame. A
    # punched window without one reads as a hole cut in the wall, which is most of why
    # the glazing looked painted on rather than fitted.
    outer_u = math_node(nt, "MULTIPLY", (-450, 900),
                        math_node(nt, "GREATER_THAN", (-630, 940), fu, 0.195),
                        math_node(nt, "LESS_THAN", (-630, 860), fu, 0.805))
    outer_v = math_node(nt, "MULTIPLY", (-450, 780),
                        math_node(nt, "GREATER_THAN", (-630, 820), fv, 0.215),
                        math_node(nt, "LESS_THAN", (-630, 740), fv, 0.825))
    outer_up = math_node(nt, "MULTIPLY", (-250, 860),
                         math_node(nt, "MULTIPLY", (-350, 860), outer_u, outer_v),
                         math_node(nt, "GREATER_THAN", (-450, 700), h, ground_h))
    outer_gr = math_node(nt, "MULTIPLY", (-250, 1000),
                         math_node(nt, "MULTIPLY", (-350, 1040),
                                   math_node(nt, "GREATER_THAN", (-450, 1080), fg, 0.115),
                                   math_node(nt, "LESS_THAN", (-450, 1000), fg, 0.885)),
                         math_node(nt, "MULTIPLY", (-350, 960),
                                   math_node(nt, "GREATER_THAN", (-450, 940), h, 0.48),
                                   math_node(nt, "LESS_THAN", (-450, 880), h,
                                             math_node(nt, "MULTIPLY", (-630, 880),
                                                       ground_h, 0.91))))
    outer_gr = math_node(nt, "MULTIPLY", (-100, 1000), outer_gr,
                         math_node(nt, "LESS_THAN", (-250, 1080), h, ground_h))
    frame = math_node(nt, "MULTIPLY", (250, 900),
                      math_node(nt, "MULTIPLY", (100, 900),
                                math_node(nt, "MINIMUM", (-50, 900),
                                          math_node(nt, "ADD", (-150, 900),
                                                    outer_up, outer_gr), 1.0),
                                below_parapet), wall)
    frame = math_node(nt, "MAXIMUM", (400, 900),
                      math_node(nt, "SUBTRACT", (330, 900), frame, glazing), 0.0)

    # --- cladding ---------------------------------------------------------------
    wall_uv = nt.nodes.new("ShaderNodeCombineXYZ")
    wall_uv.location = (-850, -1120)
    link(u, wall_uv.inputs["X"])
    link(h, wall_uv.inputs["Y"])

    brick = nt.nodes.new("ShaderNodeTexBrick")
    brick.location = (-600, -1120)
    brick.offset = 0.5
    brick.squash = 1.0
    brick.inputs["Scale"].default_value = 1.0
    brick.inputs["Brick Width"].default_value = 0.215     # modular brick + 10 mm joint
    brick.inputs["Row Height"].default_value = 0.076
    brick.inputs["Mortar Size"].default_value = 0.030
    brick.inputs["Mortar Smooth"].default_value = 1.0
    brick.inputs["Bias"].default_value = 0.0
    # Georgia Tech's brick is a warm red, not a terracotta pink. These are linear values,
    # so they read much darker written down than they render. The mortar matters as much
    # as the brick: 3 cm of joint in every 21.5 cm is a sixth of the wall by area, and a
    # pale mortar averaged the whole facade towards salmon at any distance.
    brick.inputs["Color1"].default_value = (0.270, 0.097, 0.066, 1.0)
    brick.inputs["Color2"].default_value = (0.232, 0.083, 0.058, 1.0)
    brick.inputs["Mortar"].default_value = (0.300, 0.255, 0.225, 1.0)
    link(wall_uv.outputs["Vector"], brick.inputs["Vector"])

    # Not every brick building on campus is the same brick. A per-building tint spread
    # across warm red to buff stops a whole quad reading as one extruded material.
    tone = nt.nodes.new("ShaderNodeValToRGB")
    tone.location = (-600, -1320)
    link(rnd, tone.inputs["Fac"])
    ramp = tone.color_ramp
    ramp.elements[0].position = 0.0
    ramp.elements[0].color = (1.10, 0.92, 0.82, 1.0)
    ramp.elements[1].position = 1.0
    ramp.elements[1].color = (0.84, 0.88, 0.96, 1.0)
    ramp.elements.new(0.45).color = (1.00, 1.00, 1.00, 1.0)
    ramp.elements.new(0.72).color = (1.06, 1.02, 0.88, 1.0)

    brick_toned = nt.nodes.new("ShaderNodeMixRGB")
    brick_toned.location = (-380, -1200)
    brick_toned.blend_type = "MULTIPLY"
    brick_toned.inputs["Fac"].default_value = 1.0
    link(brick.outputs["Color"], brick_toned.inputs["Color1"])
    link(tone.outputs["Color"], brick_toned.inputs["Color2"])

    # Precast/limestone: mottled panels with control joints on the storey grid.
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.location = (-970, -700)
    noise.inputs["Scale"].default_value = 1.6
    noise.inputs["Detail"].default_value = 2.0
    link(geo.outputs["Position"], noise.inputs["Vector"])

    tint = nt.nodes.new("ShaderNodeMixRGB")
    tint.location = (-630, -700)
    tint.inputs["Color1"].default_value = (
        wall_rgb[0] * (1 - variation), wall_rgb[1] * (1 - variation),
        wall_rgb[2] * (1 - variation), 1.0)
    tint.inputs["Color2"].default_value = (
        min(wall_rgb[0] * (1 + variation), 1.0), min(wall_rgb[1] * (1 + variation), 1.0),
        min(wall_rgb[2] * (1 + variation), 1.0), 1.0)
    link(noise.outputs["Fac"], tint.inputs["Fac"])

    joint = math_node(nt, "MAXIMUM", (-450, -820),
                      math_node(nt, "LESS_THAN", (-630, -790),
                                math_node(nt, "FRACT", (-800, -790),
                                          math_node(nt, "DIVIDE", (-970, -790), u, 1.5)), 0.016),
                      math_node(nt, "LESS_THAN", (-630, -880),
                                math_node(nt, "FRACT", (-800, -880),
                                          math_node(nt, "DIVIDE", (-970, -880), h, floor_h)), 0.022))
    precast = nt.nodes.new("ShaderNodeMixRGB")
    precast.location = (-250, -780)
    link(joint, precast.inputs["Fac"])
    link(tint.outputs["Color"], precast.inputs["Color1"])
    precast.inputs["Color2"].default_value = (0.26, 0.25, 0.24, 1.0)

    cladding = nt.nodes.new("ShaderNodeMixRGB")
    cladding.location = (100, -820)
    link(is_brick, cladding.inputs["Fac"])
    link(precast.outputs["Color"], cladding.inputs["Color1"])
    link(brick_toned.outputs["Color"], cladding.inputs["Color2"])

    # Procedural textures have no mipmaps, so any repeating detail turns into a moire
    # weave once its module is about a pixel wide. Two ramps, because the two patterns
    # break down at very different ranges: a 215 mm brick course aliases within a few
    # metres, a 2.7 m window bay survives out to a couple of hundred. Past each range the
    # pattern is blended into its own average, which is what a mipmap would have done.
    cam = nt.nodes.new("ShaderNodeTexCoord")
    cam.location = (60, -1150)
    # Distance to the camera as the length of the camera-space position. Verified by
    # wiring it straight to Base Color: it produces a clean depth gradient.
    depth = nt.nodes.new("ShaderNodeVectorMath")
    depth.location = (120, -1150)
    depth.operation = "LENGTH"
    link(cam.outputs["Camera"], depth.inputs[0])

    def depth_ramp(near, span, y):
        return math_node(nt, "MINIMUM", (380, y),
                         math_node(nt, "MAXIMUM", (340, y),
                                   math_node(nt, "DIVIDE", (250, y),
                                             math_node(nt, "SUBTRACT", (180, y),
                                                       depth.outputs["Value"], near),
                                             span), 0.0), 1.0)

    fade = depth_ramp(6.0, 26.0, -1150)        # brick coursing / precast joints
    fade_w = depth_ramp(70.0, 190.0, -1300)    # window grid

    flat_mix = nt.nodes.new("ShaderNodeMixRGB")
    flat_mix.location = (450, -900)
    link(fade, flat_mix.inputs["Fac"])
    link(cladding.outputs["Color"], flat_mix.inputs["Color1"])
    far = nt.nodes.new("ShaderNodeMixRGB")
    far.location = (300, -1000)
    link(is_brick, far.inputs["Fac"])
    # The precast mottle is world-space noise with ~0.6 m features, so it aliases into a
    # speckle of its own once a building is a few hundred metres out. Flatten it too.
    tint_far = nt.nodes.new("ShaderNodeMixRGB")
    tint_far.location = (150, -1080)
    link(fade_w, tint_far.inputs["Fac"])
    link(tint.outputs["Color"], tint_far.inputs["Color1"])
    tint_far.inputs["Color2"].default_value = (wall_rgb[0], wall_rgb[1], wall_rgb[2], 1.0)
    link(tint_far.outputs["Color"], far.inputs["Color1"])
    far.inputs["Color2"].default_value = (0.256, 0.101, 0.073, 1.0)
    link(far.outputs["Color"], flat_mix.inputs["Color2"])
    cladding = flat_mix

    # glazing -> its own area average (~0.30 of a facade) as the bays stop resolving.
    glazing = math_node(nt, "ADD", (500, 300),
                        math_node(nt, "MULTIPLY", (380, 340), glazing,
                                  math_node(nt, "SUBTRACT", (250, 400), 1.0, fade_w)),
                        math_node(nt, "MULTIPLY", (380, 260), fade_w, 0.30))
    door = math_node(nt, "MULTIPLY", (500, -380), door,
                     math_node(nt, "SUBTRACT", (250, -320), 1.0, fade_w))
    frame = math_node(nt, "MULTIPLY", (500, 900), frame,
                      math_node(nt, "SUBTRACT", (250, 960), 1.0, fade_w))

    # A stone or precast base course and a coping at the parapet. Almost every building
    # on campus has both, and their absence is what made the walls look like extrusions
    # that had been given a texture rather than buildings that had been detailed.
    trim = math_node(nt, "MULTIPLY", (600, 640),
                     math_node(nt, "MINIMUM", (500, 640),
                               math_node(nt, "ADD", (400, 640),
                                         math_node(nt, "LESS_THAN", (250, 680), h, 0.95),
                                         math_node(nt, "GREATER_THAN", (250, 600), h,
                                                   math_node(nt, "SUBTRACT", (100, 600),
                                                             bld_h, 0.45))), 1.0), wall)
    trim = math_node(nt, "MULTIPLY", (700, 640), trim,
                     math_node(nt, "SUBTRACT", (600, 560), 1.0, glazing))
    trim_mix = nt.nodes.new("ShaderNodeMixRGB")
    trim_mix.location = (620, -700)
    link(trim, trim_mix.inputs["Fac"])
    link(cladding.outputs["Color"], trim_mix.inputs["Color1"])
    trim_mix.inputs["Color2"].default_value = (0.395, 0.385, 0.365, 1.0)

    frame_mix = nt.nodes.new("ShaderNodeMixRGB")
    frame_mix.location = (660, -560)
    link(frame, frame_mix.inputs["Fac"])
    link(trim_mix.outputs["Color"], frame_mix.inputs["Color1"])
    frame_mix.inputs["Color2"].default_value = (0.215, 0.215, 0.225, 1.0)
    cladding = frame_mix

    # Roofs are not made of the same thing as walls. Every horizontal face was inheriting
    # the brick or precast colour, which is what made the campus read pink from the air.
    # Ballasted membrane and gravel, with the precast noise reused as the aggregate mottle.
    roof = math_node(nt, "SUBTRACT", (560, 780), 1.0, wall)
    roof_mix = nt.nodes.new("ShaderNodeMixRGB")
    roof_mix.location = (700, -640)
    roof_mix.inputs["Color2"].default_value = (0.115, 0.113, 0.108, 1.0)
    link(roof, roof_mix.inputs["Fac"])
    link(cladding.outputs["Color"], roof_mix.inputs["Color1"])
    roof_grit = nt.nodes.new("ShaderNodeMixRGB")
    roof_grit.location = (760, -640)
    roof_grit.blend_type = "OVERLAY"
    link(math_node(nt, "MULTIPLY", (700, -760), roof, 0.35), roof_grit.inputs["Fac"])
    link(roof_mix.outputs["Color"], roof_grit.inputs["Color1"])
    link(noise.outputs["Fac"], roof_grit.inputs["Color2"])
    cladding = roof_grit

    glass_mix = nt.nodes.new("ShaderNodeMixRGB")
    glass_mix.location = (700, 150)
    link(glazing, glass_mix.inputs["Fac"])
    link(cladding.outputs["Color"], glass_mix.inputs["Color1"])
    glass_mix.inputs["Color2"].default_value = glass_rgb

    door_mix = nt.nodes.new("ShaderNodeMixRGB")
    door_mix.location = (1000, 100)
    link(door, door_mix.inputs["Fac"])
    link(glass_mix.outputs["Color"], door_mix.inputs["Color1"])
    door_mix.inputs["Color2"].default_value = (0.16, 0.15, 0.14, 1.0)
    link(door_mix.outputs["Color"], bsdf.inputs["Base Color"])

    # Relief: mortar beds and panel joints sink in, glazing sits back in its reveal.
    # Masking by (1 - glazing) matters: without it the brick coursing ran straight across
    # the windows and the glass looked like it was made of bricks.
    solid = math_node(nt, "SUBTRACT", (550, -480), 1.0, glazing)
    seam = math_node(nt, "MULTIPLY", (900, -560),
                     math_node(nt, "ADD", (850, -560),
                               math_node(nt, "MULTIPLY", (700, -520),
                                         brick.outputs["Fac"], is_brick),
                               math_node(nt, "MULTIPLY", (700, -620), joint,
                                         math_node(nt, "SUBTRACT", (550, -620), 1.0, is_brick))),
                     math_node(nt, "MULTIPLY", (800, -480), solid,
                               math_node(nt, "SUBTRACT", (700, -420), 1.0, fade)))
    recess = math_node(nt, "SUBTRACT", (1300, -600), 1.0,
                       math_node(nt, "ADD", (1150, -600),
                                 math_node(nt, "MULTIPLY", (1000, -560), seam, 0.30),
                                 math_node(nt, "MULTIPLY", (1000, -660), glazing, 0.70)))
    # Frames sit proud of the reveal they surround.
    recess = math_node(nt, "ADD", (1400, -600), recess,
                       math_node(nt, "MULTIPLY", (1300, -700), frame, 0.22))
    bump = nt.nodes.new("ShaderNodeBump")
    bump.location = (1450, -600)
    bump.inputs["Strength"].default_value = 0.35
    bump.inputs["Distance"].default_value = 0.03
    link(recess, bump.inputs["Height"])
    link(bump.outputs["Normal"], bsdf.inputs["Normal"])

    # roughness = glazing * -0.72 + 0.80  ->  wall 0.80, glass 0.08
    link(math_node(nt, "MULTIPLY_ADD", (1000, -200), glazing, -0.72, 0.80), bsdf.inputs["Roughness"])
    link(math_node(nt, "MULTIPLY", (1000, -400), glazing, 0.7), bsdf.inputs["Metallic"])
    return mat


def surface_material(mat, kind, rgb, rough=0.9, joint_pitch=0.0, ortho=None, photo=0.0):
    """Procedural ground surfaces: turf, asphalt grit and jointed concrete.

    Everything is driven from world position, so adjoining polygons in different layers
    share one continuous pattern instead of each slab carrying its own tiling.

    When an orthophoto is supplied it becomes the base colour and the procedural layer
    is demoted to close-up grain. That ordering matters: noise can invent detail but it
    cannot invent *location*, and what made the aerial view read as a toy was four square
    kilometres of turf green sitting where there is really scrub, gravel, bare clay,
    off-campus rooftops and the Downtown Connector.
    """
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    link = nt.links.new

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (900, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (600, 0)
    bsdf.inputs["Roughness"].default_value = rough
    link(bsdf.outputs["BSDF"], out.inputs["Surface"])

    geo = nt.nodes.new("ShaderNodeNewGeometry")
    geo.location = (-900, 0)
    pos = geo.outputs["Position"]

    coarse = nt.nodes.new("ShaderNodeTexNoise")
    coarse.location = (-700, 200)
    coarse.inputs["Scale"].default_value = 0.06 if kind == "turf" else 0.25
    coarse.inputs["Detail"].default_value = 4.0
    link(pos, coarse.inputs["Vector"])

    fine = nt.nodes.new("ShaderNodeTexNoise")
    fine.location = (-700, -150)
    fine.inputs["Scale"].default_value = {"turf": 6.0, "asphalt": 55.0}.get(kind, 12.0)
    fine.inputs["Detail"].default_value = {"turf": 2.0, "asphalt": 6.0}.get(kind, 5.0)
    fine.inputs["Roughness"].default_value = 0.7
    link(pos, fine.inputs["Vector"])

    lo = tuple(c * (0.86 if kind == "turf" else 0.72) for c in rgb) + (1.0,)
    hi = tuple(min(c * (1.14 if kind == "turf" else 1.30), 1.0) for c in rgb) + (1.0,)
    big = nt.nodes.new("ShaderNodeMixRGB")
    big.location = (-350, 120)
    big.inputs["Color1"].default_value = lo
    big.inputs["Color2"].default_value = hi
    link(coarse.outputs["Fac"], big.inputs["Fac"])

    grain = nt.nodes.new("ShaderNodeMixRGB")
    grain.location = (-100, 60)
    grain.blend_type = "OVERLAY"
    grain.inputs["Fac"].default_value = 0.30 if kind == "asphalt" else 0.22
    link(big.outputs["Color"], grain.inputs["Color1"])
    link(fine.outputs["Color"], grain.inputs["Color2"])
    colour = grain.outputs["Color"]

    if ortho is not None and photo > 0.0:
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.location = (-700, 520)
        tex.image = ortho
        tex.interpolation = "Cubic"
        tex.extension = "EXTEND"
        uvmap = nt.nodes.new("ShaderNodeUVMap")
        uvmap.location = (-900, 520)
        link(uvmap.outputs["UV"], tex.inputs["Vector"])
        # NAIP is flown for measurement, not for looks: it comes back flat and slightly
        # hazy. A small saturation and contrast lift puts it in the range the rest of the
        # scene lives in without inventing colour that is not there.
        hsv = nt.nodes.new("ShaderNodeHueSaturation")
        hsv.location = (-480, 520)
        hsv.inputs["Saturation"].default_value = 1.20
        hsv.inputs["Value"].default_value = 1.06
        link(tex.outputs["Color"], hsv.inputs["Color"])
        bc = nt.nodes.new("ShaderNodeBrightContrast")
        bc.location = (-300, 520)
        bc.inputs["Contrast"].default_value = 0.06
        link(hsv.outputs["Color"], bc.inputs["Color"])
        blend = nt.nodes.new("ShaderNodeMixRGB")
        blend.location = (-60, 300)
        blend.inputs["Fac"].default_value = photo
        link(colour, blend.inputs["Color1"])
        link(bc.outputs["Color"], blend.inputs["Color2"])
        colour = blend.outputs["Color"]

    height = fine.outputs["Fac"]
    if kind == "turf":
        # Voronoi clumping reads as mown turf rather than flat green paint.
        clump = nt.nodes.new("ShaderNodeTexVoronoi")
        clump.location = (-700, -450)
        clump.inputs["Scale"].default_value = 2.2
        link(pos, clump.inputs["Vector"])
        shade = nt.nodes.new("ShaderNodeMixRGB")
        shade.location = (150, -100)
        shade.inputs["Color2"].default_value = tuple(c * 0.74 for c in rgb) + (1.0,)
        link(colour, shade.inputs["Color1"])
        # Voronoi Distance runs to about 0.5 and was driving the mix almost to full
        # strength, which turned every lawn into camouflage. Halve it.
        link(math_node(nt, "MULTIPLY", (-400, -450), clump.outputs["Distance"], 0.5),
             shade.inputs["Fac"])
        colour = shade.outputs["Color"]
        height = clump.outputs["Distance"]

    if joint_pitch > 0.0:
        # Saw-cut control joints on a real slab grid, in world coordinates.
        sep = nt.nodes.new("ShaderNodeSeparateXYZ")
        sep.location = (-700, -650)
        link(pos, sep.inputs[0])
        seam = math_node(nt, "MAXIMUM", (-200, -650),
                         math_node(nt, "LESS_THAN", (-380, -600),
                                   math_node(nt, "FRACT", (-540, -600),
                                             math_node(nt, "DIVIDE", (-620, -600),
                                                       sep.outputs["X"], joint_pitch)), 0.020),
                         math_node(nt, "LESS_THAN", (-380, -700),
                                   math_node(nt, "FRACT", (-540, -700),
                                             math_node(nt, "DIVIDE", (-620, -700),
                                                       sep.outputs["Y"], joint_pitch)), 0.020))
        # A 20 mm cut on a 1.5 m grid is well under a pixel wide past about thirty metres,
        # and a sub-pixel binary pattern is exactly what turned every path into a moire
        # lattice. Fade it out over the range where it stops resolving.
        cam = nt.nodes.new("ShaderNodeTexCoord")
        cam.location = (-700, -900)
        depth = nt.nodes.new("ShaderNodeVectorMath")
        depth.location = (-540, -900)
        depth.operation = "LENGTH"
        link(cam.outputs["Camera"], depth.inputs[0])
        keep = math_node(nt, "SUBTRACT", (-100, -900), 1.0,
                         math_node(nt, "MINIMUM", (-200, -900),
                                   math_node(nt, "MAXIMUM", (-300, -900),
                                             math_node(nt, "DIVIDE", (-400, -900),
                                                       math_node(nt, "SUBTRACT", (-460, -900),
                                                                 depth.outputs["Value"], 12.0),
                                                       55.0), 0.0), 1.0))
        seam = math_node(nt, "MULTIPLY", (0, -700), seam, keep)
        cut = nt.nodes.new("ShaderNodeMixRGB")
        cut.location = (250, -60)
        link(seam, cut.inputs["Fac"])
        link(colour, cut.inputs["Color1"])
        cut.inputs["Color2"].default_value = tuple(c * 0.55 for c in rgb) + (1.0,)
        colour = cut.outputs["Color"]
        height = math_node(nt, "SUBTRACT", (250, -400), height,
                           math_node(nt, "MULTIPLY", (100, -400), seam, 0.8))

    link(colour, bsdf.inputs["Base Color"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.location = (400, -300)
    bump.inputs["Strength"].default_value = {"turf": 0.45, "asphalt": 0.18}.get(kind, 0.25)
    bump.inputs["Distance"].default_value = 0.02
    link(height, bump.inputs["Height"])
    link(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def simple_material(mat, roughness, variation=0.0, scale=0.1):
    """Principled surface, optionally broken up by large-scale positional noise."""
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    if not bsdf:
        return
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = 0.0
    if variation <= 0.0:
        return

    base = bsdf.inputs["Base Color"].default_value
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    geo.location = (-900, -300)
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.location = (-700, -300)
    noise.inputs["Scale"].default_value = scale
    noise.inputs["Detail"].default_value = 3.0
    nt.links.new(geo.outputs["Position"], noise.inputs["Vector"])

    mix = nt.nodes.new("ShaderNodeMixRGB")
    mix.location = (-400, -300)
    mix.inputs["Color1"].default_value = tuple(c * (1 - variation) for c in base[:3]) + (1.0,)
    mix.inputs["Color2"].default_value = tuple(min(c * (1 + variation), 1.0) for c in base[:3]) + (1.0,)
    nt.links.new(noise.outputs["Fac"], mix.inputs["Fac"])
    nt.links.new(mix.outputs["Color"], bsdf.inputs["Base Color"])


def roof_photo_material(mat, ortho):
    """Roof surfaces textured straight from the nadir orthophoto.

    A roof is the only part of a building an aerial photo actually measures well, and it
    is the part a procedural shader is worst at: real roofs are a patchwork of membrane,
    gravel ballast, HVAC decks, skylights, tar patches and stain. Sampling the photo
    gives every building its own correct roof for nothing.
    """
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    link = nt.links.new

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (700, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (420, 0)
    bsdf.inputs["Roughness"].default_value = 0.86
    link(bsdf.outputs["BSDF"], out.inputs["Surface"])

    if ortho is None:
        bsdf.inputs["Base Color"].default_value = (0.115, 0.113, 0.108, 1.0)
        return mat

    uvmap = nt.nodes.new("ShaderNodeUVMap")
    uvmap.location = (-600, 0)
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.location = (-400, 0)
    tex.image = ortho
    tex.interpolation = "Cubic"
    tex.extension = "EXTEND"
    link(uvmap.outputs["UV"], tex.inputs["Vector"])

    # The photo is taken in full sun and already carries its own baked lighting. Pulling
    # the value down stops roofs from glowing once the scene's own sun is added on top.
    hsv = nt.nodes.new("ShaderNodeHueSaturation")
    hsv.location = (-160, 0)
    hsv.inputs["Saturation"].default_value = 0.85
    hsv.inputs["Value"].default_value = 0.62
    link(tex.outputs["Color"], hsv.inputs["Color"])
    link(hsv.outputs["Color"], bsdf.inputs["Base Color"])

    # A little gravel-scale relief so the roof is not a mirror-flat plane in raking light.
    grit = nt.nodes.new("ShaderNodeTexNoise")
    grit.location = (-160, -320)
    grit.inputs["Scale"].default_value = 40.0
    grit.inputs["Detail"].default_value = 5.0
    bump = nt.nodes.new("ShaderNodeBump")
    bump.location = (160, -320)
    bump.inputs["Strength"].default_value = 0.14
    bump.inputs["Distance"].default_value = 0.02
    link(grit.outputs["Fac"], bump.inputs["Height"])
    link(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def load_ortho():
    """Load and pack the NAIP orthophoto, or return None if it has not been fetched.

    Packing costs 14 MB in the blend but keeps the file self-contained, which is the
    whole point of shipping a .blend rather than an .obj plus a pile of siblings.
    """
    path = os.path.join(HERE, "ortho.jpg")
    if not os.path.exists(path):
        print("no ortho.jpg - ground falls back to procedural turf (run fetch_imagery.py)")
        return None
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = "sRGB"
    try:
        img.pack()
    except RuntimeError as exc:
        print(f"could not pack ortho: {exc}")
    print(f"orthophoto {img.size[0]} x {img.size[1]} px (USGS NAIP, public domain)")
    return img


def main():
    if not os.path.exists(OBJ_PATH):
        sys.exit(f"missing {OBJ_PATH} - run build_campus_obj.py first")

    clear_scene()
    bpy.ops.wm.obj_import(filepath=OBJ_PATH, forward_axis="NEGATIVE_Z", up_axis="Y")
    imported = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    print(f"imported {len(imported)} objects")

    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.length_unit = "METERS"

    for obj in imported:
        smooth = obj.name.startswith(("Terrain", "Trees_Canopy"))
        obj.data.polygons.foreach_set("use_smooth", [smooth] * len(obj.data.polygons))
        obj.data.update()

    for name, rgb, glass, var, brick in (
        ("facade", (0.50, 0.47, 0.44), (0.06, 0.10, 0.14, 1.0), 0.30, 0.58),
        ("facade_simple", (0.48, 0.47, 0.45), (0.09, 0.11, 0.13, 1.0), 0.25, 0.45),
    ):
        if name in bpy.data.materials:
            facade_material(bpy.data.materials[name], rgb, glass, var, brick)

    # kind, base colour, roughness, control-joint pitch in metres, orthophoto weight.
    # The photo only drives the natural surfaces. Roads, bays and footways keep their
    # procedural look on purpose: at 0.5 m/px NAIP bakes in parked cars, tree shadows and
    # the aircraft's own view angle, and a permanently parked car is worse than plain
    # asphalt when the markings are already modelled as geometry.
    ortho = load_ortho()
    if "roof_photo" in bpy.data.materials:
        roof_photo_material(bpy.data.materials["roof_photo"], ortho)
    for name, kind, rgb, rough, pitch, photo in (
            ("terrain", "turf", (0.15, 0.20, 0.10), 1.00, 0.0, 0.92),
            ("grass", "turf", (0.17, 0.26, 0.10), 1.00, 0.0, 0.72),
            ("field", "turf", (0.14, 0.29, 0.12), 0.95, 0.0, 0.55),
            ("road", "asphalt", (0.045, 0.045, 0.048), 0.78, 0.0, 0.0),
            ("parking", "asphalt", (0.055, 0.055, 0.058), 0.80, 0.0, 0.0),
            ("sidewalk", "concrete", (0.44, 0.43, 0.41), 0.88, 1.50, 0.0),
            ("stairs", "concrete", (0.46, 0.45, 0.43), 0.88, 1.20, 0.0),
            ("foundation", "concrete", (0.38, 0.37, 0.36), 0.90, 2.40, 0.0),
            ("art_stone", "concrete", (0.60, 0.58, 0.54), 0.60, 0.0, 0.0),
            ("monument", "concrete", (0.60, 0.59, 0.56), 0.55, 0.0, 0.0)):
        if name in bpy.data.materials:
            surface_material(bpy.data.materials[name], kind, rgb, rough, pitch,
                             ortho=ortho, photo=photo)

    for name, rough, var, scale in (("furniture_wood", 0.65, 0.22, 0.9),
                                    ("furniture_metal", 0.45, 0.10, 0.5),
                                    ("tree_trunk", 0.95, 0.20, 0.30),
                                    ("tree_canopy", 0.90, 0.45, 0.11),
                                    ("shrub", 0.92, 0.38, 0.35),
                                    ("marking", 0.72, 0.10, 1.20),
                                    ("marking_yellow", 0.74, 0.12, 1.20),
                                    ("art_painted", 0.42, 0.06, 0.40)):
        if name in bpy.data.materials:
            simple_material(bpy.data.materials[name], rough, var, scale)

    # Weathered bronze: dark, fairly glossy, with enough metallic to catch the sun on the
    # shoulders the way a cast figure does. Flat diffuse made every statue read as mud.
    if "art_bronze" in bpy.data.materials:
        mat = bpy.data.materials["art_bronze"]
        simple_material(mat, 0.38, 0.14, 0.6)
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        if bsdf is not None:
            bsdf.inputs["Metallic"].default_value = 0.85

    if "water" in bpy.data.materials:
        mat = bpy.data.materials["water"]
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        if bsdf is not None:
            bsdf.inputs["Base Color"].default_value = (0.02, 0.07, 0.10, 1.0)
            bsdf.inputs["Roughness"].default_value = 0.06
            bsdf.inputs["Metallic"].default_value = 0.0

    # Blue-light phones read as blue from a long way off because the strobe housing is
    # lit, not because the plastic is a strong colour.
    if "callbox_blue" in bpy.data.materials:
        mat = bpy.data.materials["callbox_blue"]
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        if bsdf is not None:
            bsdf.inputs["Base Color"].default_value = (0.03, 0.09, 0.40, 1.0)
            bsdf.inputs["Roughness"].default_value = 0.35
            for socket in ("Emission Color", "Emission"):
                if socket in bsdf.inputs:
                    bsdf.inputs[socket].default_value = (0.05, 0.25, 1.0, 1.0)
                    bsdf.inputs["Emission Strength"].default_value = 2.5
                    break

    sun_data = bpy.data.lights.new("Sun", type="SUN")
    sun_data.energy = 2.6
    sun_data.angle = math.radians(2.0)
    sun = bpy.data.objects.new("Sun", sun_data)
    sun.rotation_euler = (math.radians(52), 0.0, math.radians(135))
    scene.collection.objects.link(sun)

    world = bpy.data.worlds[0] if bpy.data.worlds else bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs[0].default_value = (0.24, 0.32, 0.45, 1.0)
        bg.inputs[1].default_value = 1.0

    # AgX is Blender 4's default and it desaturates hard: it was pushing the brick towards
    # brown and veiling the whole campus in pale blue. Standard reproduces the material
    # colours as authored, which is what matters when the point is surface accuracy - the
    # sun and sky above are dialled down to keep concrete off the clipping point.
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0.0

    cam_data = bpy.data.cameras.new("Camera")
    cam_data.lens = 35
    cam_data.clip_start = CLIP_START
    cam_data.clip_end = CLIP_END
    cam = bpy.data.objects.new("Camera", cam_data)
    cam.location = (-950, -1150, 560)
    cam.rotation_euler = (math.radians(64), 0.0, math.radians(-40))
    scene.collection.objects.link(cam)
    scene.camera = cam

    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            for space in area.spaces:
                if space.type != "VIEW_3D":
                    continue
                space.clip_start = CLIP_START
                space.clip_end = CLIP_END
                space.shading.type = "MATERIAL"
                if space.region_3d:
                    space.region_3d.view_location = (0.0, 0.0, 0.0)
                    space.region_3d.view_distance = 1600.0

    save_blend()


def save_blend():
    """Write the .blend somewhere nothing is watching, then move it into place.

    Saving straight to BLEND_PATH kept failing with "Cannot change old file (file
    saved with @)". That is Blender failing to rename its own temp file: this folder
    sits inside a Unity project, and Unity's asset watcher (plus Defender) opens every
    large new file the moment it appears, so the rename hits a sharing violation.
    Staging through the system temp folder avoids the watcher entirely, and the final
    move is retried because the destination can be held briefly too.

    Compressed: an uncompressed campus of this size is ~650 MB.
    """
    staged = os.path.join(tempfile.gettempdir(), "gt_campus_stage.blend")
    for leftover in (staged, staged + "@", staged + "1"):
        if os.path.exists(leftover):
            os.remove(leftover)

    bpy.ops.wm.save_as_mainfile(filepath=staged, compress=True)

    # Blender keeps the previous save as .blend1; it is the largest thing competing for
    # the lock and we already have the staged copy, so clear it before the move.
    for leftover in (BLEND_PATH, BLEND_PATH + "1", BLEND_PATH + "@"):
        for _ in range(10):
            if not os.path.exists(leftover):
                break
            try:
                os.remove(leftover)
                break
            except OSError:
                time.sleep(1.0)

    last = None
    for attempt in range(10):
        try:
            shutil.move(staged, BLEND_PATH)
            last = None
            break
        except OSError as exc:
            last = exc
            time.sleep(2.0)
    if last is not None:
        raise RuntimeError(f"could not move {staged} to {BLEND_PATH}: {last}")

    print(f"saved {BLEND_PATH} ({os.path.getsize(BLEND_PATH) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
