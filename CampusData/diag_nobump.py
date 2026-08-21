"""Diagnostic: same view, but with every Bump node unplugged from the facade shaders.

    blender --background gt_campus.blend --python diag_nobump.py

Isolates whether the diagonal weave is the Bump node aliasing its height input.
"""

import math
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))

for mat in bpy.data.materials:
    if not mat.use_nodes:
        continue
    nt = mat.node_tree
    for node in nt.nodes:
        if node.type == "BUMP":
            for link in list(node.outputs["Normal"].links):
                nt.links.remove(link)

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1600
scene.render.resolution_y = 900
scene.eevee.taa_render_samples = 24
cam = scene.camera
cam.location = (-60, -150, 16)
cam.rotation_euler = tuple(math.radians(a) for a in (86, 0, -10))
cam.data.lens = 50
scene.render.filepath = os.path.join(HERE, "diag_nobump.png")
bpy.ops.render.render(write_still=True)
print("rendered diag_nobump.png")
