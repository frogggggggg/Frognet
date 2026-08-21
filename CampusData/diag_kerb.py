"""Why did the ground partition emit zero kerb edges?

Rebuilds only the partition step and histograms the source-ring edge counts. If the
outer perimeter of the paving really is unshared there must be thousands of count==1
edges; anything else means the key or the ring walk is wrong.
"""
import collections

import numpy as np

import build_campus_obj as B

layers, claimed = B.partition_ground(B.GROUND_LAYERS)
print(f"{len(layers)} layers, {claimed.area / 10000.0:.1f} ha")

counts = collections.Counter()
per_layer = {}
for obj_name, material, max_edge, geom in layers:
    n_ring_edges = 0
    n_tri_edges = 0
    tri_keys = set()
    for flat, ends in B._rings_of(geom):
        start = 0
        for e in ends:
            ring = flat[start:int(e)]
            start = int(e)
            for i in range(len(ring)):
                counts[B._edge_key(ring[i], ring[(i + 1) % len(ring)])] += 1
                n_ring_edges += 1
        tris = B.triangulate(flat, ends)
        parents = flat[tris]
        for t in range(len(tris)):
            for ci in range(3):
                tri_keys.add(B._edge_key(parents[t, ci], parents[t, (ci + 1) % 3]))
                n_tri_edges += 1
    per_layer[obj_name] = (n_ring_edges, n_tri_edges, tri_keys)
    print(f"  {obj_name:18s} ring edges {n_ring_edges:8,d}  tri edges {n_tri_edges:8,d}")

hist = collections.Counter(counts.values())
print("\nsource-ring edge multiplicity:")
for mult in sorted(hist):
    print(f"  used {mult}x : {hist[mult]:,d} edges")

print("\ntriangle edges that resolve to each multiplicity:")
for obj_name, (_, _, tri_keys) in per_layer.items():
    h = collections.Counter(counts.get(k, 0) for k in tri_keys)
    print(f"  {obj_name:18s} " + "  ".join(f"{m}x:{n:,d}" for m, n in sorted(h.items())))
