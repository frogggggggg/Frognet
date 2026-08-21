"""Diagnostic: render the facade view with every material replaced by flat diffuse.

    blender --background gt_campus.blend --python diag_flat.py

If the diagonal weave on the walls survives this, it is not shading - it is two
coplanar surfaces fighting for the depth buffer.
"""

import math
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))

for mat in bpy.data.materials:
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfDiffuse")
    bsdf.inputs["Color"].default_value = (0.55, 0.53, 0.50, 1.0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1600
scene.render.resolution_y = 900
scene.eevee.taa_render_samples = 24
cam = scene.camera
cam.location = (-60, -150, 16)
cam.rotation_euler = tuple(math.radians(a) for a in (86, 0, -10))
cam.data.lens = 50
scene.render.filepath = os.path.join(HERE, "diag_flat.png")
bpy.ops.render.render(write_still=True)
print("rendered diag_flat.png")
