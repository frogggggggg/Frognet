"""Probe the two upstream services that could give real terrain and real building geometry."""

import json
import urllib.parse
import urllib.request


def get(url, raw=False):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-probe/1.0"})
    with urllib.request.urlopen(req, timeout=90) as resp:
        data = resp.read()
        return data if raw else json.loads(data)


print("=" * 70)
print("USGS 3DEP ElevationImageServer")
print("=" * 70)
USGS = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer"
try:
    info = get(f"{USGS}?f=json")
    print(f"  name          : {info.get('name')}")
    print(f"  pixel type    : {info.get('pixelType')}")
    print(f"  cellsize      : {info.get('pixelSizeX')} x {info.get('pixelSizeY')} (sr {info['spatialReference'].get('wkid')})")
    print(f"  capabilities  : {info.get('capabilities')}")
    print(f"  export formats: {info.get('supportedExportImageFormats')}")
    print(f"  max size      : {info.get('maxImageWidth')} x {info.get('maxImageHeight')}")
except Exception as exc:  # noqa: BLE001
    print(f"  FAILED: {exc}")

print()
print("  identify a point on campus (Tech Green):")
try:
    params = urllib.parse.urlencode(
        {"geometry": json.dumps({"x": -84.3963, "y": 33.7756, "spatialReference": {"wkid": 4326}}),
         "geometryType": "esriGeometryPoint", "returnGeometry": "false", "f": "json"}
    )
    r = get(f"{USGS}/identify?{params}")
    print(f"    elevation = {r.get('value')} m")
except Exception as exc:  # noqa: BLE001
    print(f"    FAILED: {exc}")

print()
print("=" * 70)
print("GT NBBJ_Buildings3D_WebM  (I3S SceneServer)")
print("=" * 70)
SCENE = ("https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"
         "/NBBJ_Buildings3D_WebM/SceneServer/layers/0")
try:
    lyr = get(f"{SCENE}?f=json")
    store = lyr.get("store", {})
    print(f"  i3s version   : {store.get('version')}")
    print(f"  index scheme  : {(store.get('indexCRS') or '')}  node page size {(lyr.get('nodePages') or {}).get('nodesPerPage')}")
    print(f"  geometry enc  : {[b.get('encoding') for b in (lyr.get('geometryDefinitions') or [{}])[0].get('geometryBuffers', [])]}")
    gs = store.get("defaultGeometrySchema", {})
    print(f"  topology      : {gs.get('topology')}  header {[h.get('property') for h in gs.get('header', [])]}")
    print(f"  vtx attrs     : {list((gs.get('vertexAttributes') or {}).keys())}")
    print(f"  ordering      : {gs.get('ordering')}")
    print(f"  lod type      : {store.get('lodType')}  model {store.get('lodModel')}")
except Exception as exc:  # noqa: BLE001
    print(f"  FAILED: {exc}")

print()
print("  node page 0:")
try:
    page = get(f"{SCENE}/nodepages/0?f=json")
    nodes = page.get("nodes", [])
    print(f"    nodes on page : {len(nodes)}")
    leaves = [n for n in nodes if not n.get("childCount")]
    print(f"    leaf nodes    : {len(leaves)}")
    sample = nodes[0]
    print(f"    sample keys   : {list(sample.keys())}")
    print(f"    sample mesh   : {sample.get('mesh')}")
except Exception as exc:  # noqa: BLE001
    print(f"    FAILED: {exc}")
