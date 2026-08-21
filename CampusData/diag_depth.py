"""Diagnostic: show the facade shader's distance ramp as greyscale.

    blender --background gt_campus.blend --python diag_depth.py

Black = ramp reading 0 (full procedural detail), white = 1 (flattened).
"""

import math
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))

for mat in bpy.data.materials:
    if not mat.use_nodes:
        continue
    nt = mat.node_tree
    length = next((n for n in nt.nodes
                   if n.type == "VECT_MATH" and n.operation == "LENGTH"), None)
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if length is None or bsdf is None:
        continue
    for link in list(bsdf.inputs["Base Color"].links):
        nt.links.remove(link)
    for link in list(bsdf.inputs["Normal"].links):
        nt.links.remove(link)
    scale = nt.nodes.new("ShaderNodeMath")
    scale.operation = "DIVIDE"
    scale.inputs[1].default_value = 200.0
    nt.links.new(length.outputs["Value"], scale.inputs[0])
    nt.links.new(scale.outputs[0], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 1.0
    bsdf.inputs["Metallic"].default_value = 0.0

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1600
scene.render.resolution_y = 900
scene.eevee.taa_render_samples = 8
cam = scene.camera
cam.location = (-60, -150, 16)
cam.rotation_euler = tuple(math.radians(a) for a in (86, 0, -10))
cam.data.lens = 50
scene.render.filepath = os.path.join(HERE, "diag_depth.png")
bpy.ops.render.render(write_still=True)
print("rendered diag_depth.png")
