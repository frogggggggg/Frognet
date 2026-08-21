"""One-off probe: report the walkway polygons with the most vertical drop.

Used only to aim the preview cameras at real staircases instead of guessing.
"""

import numpy as np
import os

import build_campus_obj as B

terrain = B.Terrain(os.path.join(B.HERE, "terrain.npz"), B.EXTENT, B.TERRAIN_STEP)
rows = []
for feature in B.load("sidewalks"):
    for polygon in B.polygons_of(feature.get("geometry")):
        flat, ends = B.prepare(polygon)
        if flat is None or len(flat) < 3:
            continue
        z = B.drape(terrain, flat[:, 0], flat[:, 1])
        drop = float(z.max() - z.min())
        span = float(max(np.ptp(flat[:, 0]), np.ptp(flat[:, 1])))
        if span < 4.0:
            continue
        rows.append((drop, drop / span, flat[:, 0].mean(), flat[:, 1].mean(), span))

rows.sort(reverse=True)
for drop, slope, cx, cy, span in rows[:12]:
    print(f"drop {drop:6.1f} m  slope {slope:5.2f}  centre ({cx:8.1f}, {cy:8.1f})  span {span:6.1f} m")
