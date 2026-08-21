"""Sanity-check gt_campus.obj: bounds, index validity, height stats, and a top-down preview."""

import os
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
OBJ = os.path.join(HERE, "gt_campus.obj")
PREVIEW = os.path.join(HERE, "preview_topdown.png")

verts = []
objects = defaultdict(list)  # name -> list of face index tuples
current = None

with open(OBJ, "r", encoding="utf-8") as fh:
    for line in fh:
        if line.startswith("v "):
            _, x, y, z = line.split()
            verts.append((float(x), float(y), float(z)))
        elif line.startswith("o "):
            current = line[2:].strip()
        elif line.startswith("f "):
            objects[current].append(tuple(int(p.split("/")[0]) - 1 for p in line.split()[1:]))

print(f"vertices parsed : {len(verts):,}")
print(f"objects         : {len(objects)}")

bad = 0
for name, faces in objects.items():
    for f in faces:
        if any(i < 0 or i >= len(verts) for i in f):
            bad += 1
print(f"out-of-range idx: {bad}")

xs = [v[0] for v in verts]
ys = [v[1] for v in verts]
zs = [v[2] for v in verts]
print(f"east  (X) range : {min(xs):9.1f} .. {max(xs):9.1f} m  span {max(xs)-min(xs):8.1f} m")
print(f"up    (Y) range : {min(ys):9.1f} .. {max(ys):9.1f} m  span {max(ys)-min(ys):8.1f} m")
print(f"south (Z) range : {min(zs):9.1f} .. {max(zs):9.1f} m  span {max(zs)-min(zs):8.1f} m")
print()

for name in sorted(objects):
    faces = objects[name]
    idx = {i for f in faces for i in f}
    if not idx:
        continue
    top = max(verts[i][1] for i in idx)
    ngons = sorted({len(f) for f in faces})
    print(f"  {name:22s} {len(faces):7,d} faces  max height {top:6.1f} m  n-gons {ngons}")

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.collections import PolyCollection
except ImportError:
    print("\nmatplotlib not installed - skipping preview")
    raise SystemExit(0)

STYLE = {
    "LandscapeAreas": ("#33452a", 0),
    "Roads": ("#3a3a3d", 1),
    "Parking": ("#4a4a4d", 2),
    "RecreationFields": ("#4c7033", 3),
    "Sidewalks": ("#b9b6b0", 4),
    "Buildings_Extruded": ("#6e6e73", 5),
    "Buildings_Campus3D": ("#b5651d", 6),
}

fig, ax = plt.subplots(figsize=(16, 16), dpi=110)
ax.set_facecolor("#101014")

for name, (colour, order) in sorted(STYLE.items(), key=lambda kv: kv[1][1]):
    faces = objects.get(name)
    if not faces:
        continue
    # Top-down: OBJ X east, OBJ -Z north. Only draw upward-facing (roof/ground) polys.
    polys = []
    for f in faces:
        if len(f) != 3:
            continue  # skip wall quads
        polys.append([(verts[i][0], -verts[i][2]) for i in f])
    if polys:
        ax.add_collection(PolyCollection(polys, facecolors=colour, edgecolors="none", linewidths=0))

trees = objects.get("Trees_Canopy")
if trees:
    pts = {}
    for f in trees:
        for i in f:
            pts[i] = (verts[i][0], -verts[i][2])
    tx = [p[0] for p in pts.values()]
    tz = [p[1] for p in pts.values()]
    ax.scatter(tx, tz, s=0.4, c="#2f6b2a", alpha=0.5, linewidths=0)

ax.set_xlim(-1200, 1200)
ax.set_ylim(-1200, 1400)
ax.set_aspect("equal")
ax.set_title("Georgia Tech campus - top-down from gt_campus.obj", color="w")
ax.tick_params(colors="#888")
fig.savefig(PREVIEW, facecolor="#101014", bbox_inches="tight")
print(f"\npreview written : {PREVIEW}")
