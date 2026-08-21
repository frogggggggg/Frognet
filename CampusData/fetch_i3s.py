"""Download and decode Georgia Tech's I3S 3D building meshes into local ENU metres.

The layer is I3S 1.9 'MeshPyramid' with legacy PerAttributeArray geometry buffers:

    uint32 vertexCount
    uint32 featureCount
    float32[3] * vertexCount   position   (offset from the node's OBB centre)
    float32[3] * vertexCount   normal
    float32[2] * vertexCount   uv0
    uint8[4]   * vertexCount   colour
    ...featureCount * 16 bytes trailing feature records

Vertex XY are EPSG:3857 metres, Z is real metres above sea level. Only leaf nodes are
taken, since in a mesh pyramid children fully replace their parent.
"""

import gzip
import json
import math
import os
import struct
import urllib.request

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "buildings3d.npz")

SCENE = ("https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"
         "/NBBJ_Buildings3D_WebM/SceneServer/layers/0")

LAT0 = 33.7756
LON0 = -84.3963
M_PER_DEG_LAT = 110540.0
M_PER_DEG_LON = 111320.0 * math.cos(math.radians(LAT0))


def get_bytes(url):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-i3s/1.0", "Accept-Encoding": "gzip"})
    with urllib.request.urlopen(req, timeout=180) as resp:
        raw = resp.read()
        if resp.headers.get("Content-Encoding") == "gzip" or raw[:2] == b"\x1f\x8b":
            raw = gzip.decompress(raw)
        return raw


def load_nodes():
    nodes, page = [], 0
    while True:
        try:
            raw = get_bytes(f"{SCENE}/nodepages/{page}?f=json")
        except Exception:
            break
        batch = json.loads(raw).get("nodes", [])
        if not batch:
            break
        nodes.extend(batch)
        page += 1
        if page > 200:
            break
    return nodes


def mercator_to_local(mx, my):
    lon = mx / 20037508.34 * 180.0
    lat = np.degrees(2.0 * np.arctan(np.exp(my / 20037508.34 * math.pi)) - math.pi / 2.0)
    return (lon - LON0) * M_PER_DEG_LON, (lat - LAT0) * M_PER_DEG_LAT


def main():
    nodes = load_nodes()
    leaves = [
        n for n in nodes
        if not n.get("childCount")
        and (n.get("mesh") or {}).get("geometry", {}).get("vertexCount", 0) > 0
    ]
    print(f"nodes {len(nodes)}, leaf nodes with geometry: {len(leaves)}")

    chunks = []
    for i, node in enumerate(leaves, 1):
        res = node["mesh"]["geometry"]["resource"]
        cx, cy, cz = node["obb"]["center"]
        try:
            raw = get_bytes(f"{SCENE}/nodes/{res}/geometries/0")
        except Exception as exc:  # noqa: BLE001
            print(f"  node {res}: FAILED {exc}")
            continue
        vcount, fcount = struct.unpack_from("<II", raw, 0)
        need = 8 + vcount * 12
        if len(raw) < need:
            print(f"  node {res}: truncated buffer, skipped")
            continue
        pos = np.frombuffer(raw, dtype="<f4", count=vcount * 3, offset=8).reshape(-1, 3).astype(np.float64)
        mx = pos[:, 0] + cx
        my = pos[:, 1] + cy
        mz = pos[:, 2] + cz
        ex, ny = mercator_to_local(mx, my)
        chunks.append(np.column_stack([ex, ny, mz]).astype(np.float32))
        print(f"  [{i:2d}/{len(leaves)}] node {res:>3}  {vcount:>7,} verts  ({fcount} features)")

    verts = np.concatenate(chunks, axis=0)
    # PerAttributeArray topology is a triangle soup: 3 consecutive vertices per triangle.
    verts = verts[: (len(verts) // 3) * 3]
    print()
    print(f"total vertices : {len(verts):,}  ({len(verts)//3:,} triangles)")
    print(f"east   range   : {verts[:,0].min():9.1f} .. {verts[:,0].max():9.1f} m")
    print(f"north  range   : {verts[:,1].min():9.1f} .. {verts[:,1].max():9.1f} m")
    print(f"elev   range   : {verts[:,2].min():9.1f} .. {verts[:,2].max():9.1f} m")

    np.savez_compressed(OUT, vertices=verts)
    print(f"saved {OUT} ({os.path.getsize(OUT) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
