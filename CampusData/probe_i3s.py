"""Inspect the I3S node tree and verify the coordinate frame of decoded vertices."""

import gzip
import json
import math
import struct
import urllib.request

SCENE = ("https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"
         "/NBBJ_Buildings3D_WebM/SceneServer/layers/0")


def get_bytes(url):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-probe/1.0", "Accept-Encoding": "gzip"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        raw = resp.read()
        if resp.headers.get("Content-Encoding") == "gzip" or raw[:2] == b"\x1f\x8b":
            raw = gzip.decompress(raw)
        return raw


def merc_to_lonlat(x, y):
    lon = x / 20037508.34 * 180.0
    lat = math.degrees(2 * math.atan(math.exp(y / 20037508.34 * math.pi)) - math.pi / 2)
    return lon, lat


nodes = []
i = 0
while True:
    try:
        raw = get_bytes(f"{SCENE}/nodepages/{i}?f=json")
    except Exception:
        break
    nodes.extend(json.loads(raw).get("nodes", []))
    i += 1
    if i > 80:
        break

print(f"nodes: {len(nodes)}\n")
print(f"{'idx':>4} {'parent':>7} {'children':>9} {'lodThresh':>13} {'verts':>8} {'featCnt':>8}  obb_center(x,y,z)")
for n in nodes:
    mesh = n.get("mesh") or {}
    geom = mesh.get("geometry") or {}
    attr = mesh.get("attribute") or {}
    kids = n.get("childCount", 0) or len(n.get("children", []) or [])
    c = (n.get("obb") or {}).get("center", [0, 0, 0])
    print(f"{n.get('index',-1):>4} {n.get('parentIndex',-1):>7} {kids:>9} "
          f"{n.get('lodThreshold',0):>13.1f} {geom.get('vertexCount',0):>8} "
          f"{attr.get('resource', 0):>8}  {c[0]:.1f}, {c[1]:.1f}, {c[2]:.1f}")

# Decode one node and confirm where it lands on earth.
target = next(n for n in nodes if (n.get("mesh") or {}).get("geometry", {}).get("vertexCount", 0) > 0)
res = target["mesh"]["geometry"]["resource"]
raw = get_bytes(f"{SCENE}/nodes/{res}/geometries/0")
vcount, fcount = struct.unpack_from("<II", raw, 0)
cx, cy, cz = target["obb"]["center"]

print(f"\ndecoding node index={target['index']} resource={res} verts={vcount} feats={fcount}")
xs, ys, zs = [], [], []
for v in range(0, vcount, max(1, vcount // 2000)):
    px, py, pz = struct.unpack_from("<fff", raw, 8 + v * 12)
    xs.append(cx + px)
    ys.append(cy + py)
    zs.append(cz + pz)

lon0, lat0 = merc_to_lonlat(min(xs), min(ys))
lon1, lat1 = merc_to_lonlat(max(xs), max(ys))
print(f"  mercator X {min(xs):.1f}..{max(xs):.1f}   Y {min(ys):.1f}..{max(ys):.1f}")
print(f"  lon/lat    {lon0:.5f},{lat0:.5f}  ..  {lon1:.5f},{lat1:.5f}")
print(f"  Z (m)      {min(zs):.1f} .. {max(zs):.1f}   span {max(zs)-min(zs):.1f}")
print("  -> campus is near -84.396, 33.776; ground ~283 m")
