"""Diagnostic: show the facade window mask on its own.

    blender --background gt_campus.blend --python diag_glaze.py

The glass mix is the MixRGB placed at (700, 150) by make_blend, so its Fac input is the
glazing mask. Rendering that mask as base colour shows exactly how the window pattern
behaves with distance.
"""

import math
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))

found = 0
for mat in bpy.data.materials:
    if not mat.use_nodes:
        continue
    nt = mat.node_tree
    glass = next((n for n in nt.nodes
                  if n.type == "MIX_RGB"
                  and abs(n.location[0] - 700) < 1 and abs(n.location[1] - 150) < 1), None)
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if glass is None or bsdf is None or not glass.inputs["Fac"].links:
        continue
    found += 1
    src = glass.inputs["Fac"].links[0].from_socket
    for socket in ("Base Color", "Normal"):
        for link in list(bsdf.inputs[socket].links):
            nt.links.remove(link)
    nt.links.new(src, bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 1.0
    bsdf.inputs["Metallic"].default_value = 0.0
print(f"patched {found} facade materials")

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1600
scene.render.resolution_y = 900
scene.eevee.taa_render_samples = 8
cam = scene.camera
cam.location = (-60, -150, 16)
cam.rotation_euler = tuple(math.radians(a) for a in (86, 0, -10))
cam.data.lens = 50
scene.render.filepath = os.path.join(HERE, "diag_glaze.png")
bpy.ops.render.render(write_still=True)
print("rendered diag_glaze.png")
