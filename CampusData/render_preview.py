"""Render preview images from gt_campus.blend.

    blender --background gt_campus.blend --python render_preview.py

Camera heights are given as an eye height above the ground rather than an absolute Z.
Guessing absolute heights on a site with 73 m of relief put cameras inside hillsides
more than once; a downward ray onto the scene finds the real surface every time.
"""

import math
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))

# name, (x, y), eye height above ground, rotation, lens, hidden object prefixes
VIEWS = [
    ("preview_3d.png", (-950, -1150), 480, (64, 0, -40), 35, ()),
    ("preview_campanile.png", (-312, -282), 34, (79, 0, -45), 45, ()),
    ("preview_facade.png", (-60, -150), 12, (86, 0, -10), 50, ()),
    # Aimed at the stepped walkway at (-392, 259): 20 m of drop over a 77 m run, the
    # longest genuinely stair-like slope in the sidewalk layer (see find_stairs.py).
    # The tree layer is hidden for these two: 15,915 canopies bury the ground entirely,
    # and the ground is the thing being checked.
    ("preview_stairs.png", (-462, 192), 24, (79, 0, -40), 55, ("Trees_",)),
    # The same flight from close range, to check treads against the paving they meet.
    ("preview_stairs_close.png", (-404, 232), 5, (84, 0, -12), 50, ("Trees_",)),
    # The Koan, at (-20.7, -10.3) per OSM node 9837487327, with Tech Green behind it.
    ("preview_koan.png", (-50, -40), 6, (83, 0, -45), 50, ("Trees_Canopy",)),
    # Surveyed OSM steps, tables and bike racks in one frame, to check that the new
    # site content is scaled and oriented like real objects.
    ("preview_plaza.png", (120, -120), 5, (82, 0, 20), 40, ("Trees_Canopy",)),
]

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1600
scene.render.resolution_y = 900
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.eevee.taa_render_samples = 24

depsgraph = bpy.context.evaluated_depsgraph_get()


def ground_at(x, y, default=0.0):
    """Height of the highest surface under (x, y), by casting a ray straight down."""
    hit, loc, _, _, _, _ = scene.ray_cast(depsgraph, (x, y, 900.0), (0.0, 0.0, -1.0))
    return loc.z if hit else default


cam = scene.camera
for name, (x, y), eye, rotation, lens, hidden in VIEWS:
    for obj in scene.objects:
        obj.hide_render = obj.name.startswith(hidden) if hidden else False
    cam.location = (x, y, ground_at(x, y) + eye)
    cam.rotation_euler = tuple(math.radians(a) for a in rotation)
    cam.data.lens = lens
    scene.render.filepath = os.path.join(HERE, name)
    bpy.ops.render.render(write_still=True)
    print(f"rendered {name}  z={cam.location.z:.1f}")
