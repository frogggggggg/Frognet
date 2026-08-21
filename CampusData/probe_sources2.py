"""Second probe: can we export a DEM raster, and can we walk the I3S node tree?"""

import gzip
import io
import json
import struct
import urllib.parse
import urllib.request


def get_bytes(url):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-probe/1.0", "Accept-Encoding": "gzip"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        raw = resp.read()
        if resp.headers.get("Content-Encoding") == "gzip" or raw[:2] == b"\x1f\x8b":
            raw = gzip.decompress(raw)
        return raw, resp.headers.get("Content-Type", "")


USGS = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer"

# Campus bbox in Web Mercator (EPSG:3857), roughly matching the model extent.
BBOX = (-9397500, 3996800, -9392300, 4001400)

print("=" * 70)
print("3DEP exportImage")
print("=" * 70)
for fmt in ("tiff", "lerc"):
    params = urllib.parse.urlencode({
        "bbox": ",".join(str(v) for v in BBOX),
        "bboxSR": 3857, "imageSR": 3857,
        "size": "512,452",
        "format": fmt, "pixelType": "F32",
        "interpolation": "RSP_BilinearInterpolation",
        "f": "image",
    })
    try:
        raw, ctype = get_bytes(f"{USGS}/exportImage?{params}")
        print(f"  {fmt:6s} -> {len(raw):8d} bytes  content-type={ctype}  magic={raw[:4]!r}")
    except Exception as exc:  # noqa: BLE001
        print(f"  {fmt:6s} -> FAILED {exc}")

print()
print("=" * 70)
print("I3S node tree")
print("=" * 70)
SCENE = ("https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"
         "/NBBJ_Buildings3D_WebM/SceneServer/layers/0")

pages = []
index = 0
while True:
    try:
        raw, _ = get_bytes(f"{SCENE}/nodepages/{index}?f=json")
    except Exception:
        break
    pages.append(json.loads(raw))
    index += 1
    if index > 60:
        break

nodes = [n for p in pages for n in p.get("nodes", [])]
print(f"  node pages     : {len(pages)}")
print(f"  total nodes    : {len(nodes)}")
leaves = [n for n in nodes if not n.get("childCount")]
with_mesh = [n for n in nodes if (n.get("mesh") or {}).get("geometry", {}).get("resource", 0)]
print(f"  leaf nodes     : {len(leaves)}")
print(f"  nodes w/ mesh  : {len(with_mesh)}")
if nodes:
    print(f"  sample node    : {json.dumps(nodes[1] if len(nodes) > 1 else nodes[0])[:400]}")

print()
print("  fetch one geometry buffer:")
target = next((n for n in with_mesh if not n.get("childCount")), None) or (with_mesh[0] if with_mesh else None)
if target:
    res = target["mesh"]["geometry"]["resource"]
    url = f"{SCENE}/nodes/{res}/geometries/0"
    try:
        raw, ctype = get_bytes(url)
        vcount, fcount = struct.unpack_from("<II", raw, 0)
        expected = 8 + vcount * (12 + 12 + 8 + 4)
        print(f"    node resource {res}: {len(raw)} bytes, vertexCount={vcount} featureCount={fcount}")
        print(f"    expected size for pos+normal+uv+color = {expected}  match={expected == len(raw)}")
        px, py, pz = struct.unpack_from("<fff", raw, 8)
        print(f"    first position : {px:.3f} {py:.3f} {pz:.3f}")
        print(f"    node mbs       : {target.get('obb', {}).get('center') or target.get('mbs')}")
    except Exception as exc:  # noqa: BLE001
        print(f"    FAILED {exc}")
