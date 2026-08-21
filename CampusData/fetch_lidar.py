"""Fetch USGS 3DEP lidar for the campus and reduce it to raster products.

Source: the `GA_Statewide_B2_2018` project, published as Entwine Point Tiles on the
public `usgs-lidar-public` S3 bucket. No key, no auth, plain HTTPS range-free GETs.
42.7 billion points statewide; we pull the couple of thousand octree nodes that overlap
the campus extent.

What this is actually for. The bare-earth DEM the model already uses is *derived from
this same lidar*, so re-deriving the ground surface would gain very little. The unique
information in the point cloud is everything the bare-earth product throws away:

  * a digital surface model, so roofs stop being flat planes and rooftop plant - the
    chillers, ducts, stair overruns and screens that make an aerial view read as real -
    can be placed where there is actually something standing on the roof;
  * measured tree crown heights, to check the 15,915 inventory trees against reality
    rather than trusting a `TOTHT` attribute;
  * canopy and building masks that are observations rather than inferences.

Output is `lidar.npz`: DTM, DSM and per-class maxima on a metre grid in the same local
ENU frame the rest of the pipeline uses, so nothing downstream needs to know that any
of this came from a point cloud.
"""

import io
import json
import math
import os
import sys
import threading
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

import numpy as np

try:
    import laspy
except ImportError:
    sys.exit("laspy is required:  python -m pip install laspy lazrs")

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE_DIR = os.path.join(HERE, "lidar_cache")
OUT_PATH = os.path.join(HERE, "lidar.npz")

PROJECT = "GA_Statewide_B2_2018"
BASE = f"https://s3-us-west-2.amazonaws.com/usgs-lidar-public/{PROJECT}"

LAT0, LON0 = 33.7756, -84.3963
M_PER_DEG_LAT = 110540.0
M_PER_DEG_LON = 111320.0 * math.cos(math.radians(LAT0))
Z_REF = 283.0
EXTENT = (-2150.0, 2200.0, -1680.0, 2200.0)

# Octree depth. Node spacing is cubeSize / (span * 2^depth); for this project that is
# 203,296 / (128 * 2^d), so depth 11 gives ~0.78 m and depth 12 ~0.39 m. 11 is the point
# of diminishing returns for a 1 m output raster and downloads a quarter as many nodes.
DEPTH = 11
GRID = 1.0            # metres per output cell
WORKERS = 24

# ASPRS classes we care about.
CLASS_GROUND = 2
CLASS_VEG = (3, 4, 5)
CLASS_BUILDING = 6

R_EARTH = 6378137.0


def to_mercator(lon, lat):
    x = R_EARTH * math.radians(lon)
    y = R_EARTH * math.log(math.tan(math.pi / 4.0 + math.radians(lat) / 2.0))
    return x, y


def fetch(url, binary=True, retries=4):
    for attempt in range(retries):
        try:
            req = urllib.request.Request(
                url, headers={"User-Agent": "gt-campus-builder/1.0"})
            with urllib.request.urlopen(req, timeout=120) as r:
                return r.read()
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                return None
            if attempt == retries - 1:
                raise
        except Exception:
            if attempt == retries - 1:
                raise
    return None


def node_bounds(root, key):
    """Axis-aligned bounds of an EPT octree node from its 'D-X-Y-Z' key."""
    d, x, y, z = (int(v) for v in key.split("-"))
    x0, y0, z0, x1, y1, z1 = root
    n = 1 << d
    sx, sy, sz = (x1 - x0) / n, (y1 - y0) / n, (z1 - z0) / n
    return (x0 + x * sx, y0 + y * sy, z0 + z * sz,
            x0 + (x + 1) * sx, y0 + (y + 1) * sy, z0 + (z + 1) * sz)


def overlaps(a, b):
    return not (a[3] <= b[0] or a[0] >= b[3] or a[4] <= b[1] or a[1] >= b[4])


def walk(root, want, key="0-0-0-0", hierarchy=None, out=None, seen=None):
    """Collect every node key at or above DEPTH whose bounds overlap `want`.

    EPT splits its hierarchy across several JSON files: a page whose value is -1 means
    'the subtree rooted here is described in ept-hierarchy/<key>.json'. Those have to be
    followed or the walk stops at an arbitrary depth.
    """
    out = [] if out is None else out
    seen = set() if seen is None else seen
    if hierarchy is None:
        hierarchy = json.loads(fetch(f"{BASE}/ept-hierarchy/0-0-0-0.json"))

    stack = [key]
    while stack:
        k = stack.pop()
        if k in seen:
            continue
        seen.add(k)
        count = hierarchy.get(k)
        if count is None:
            continue
        if count == -1:
            sub = fetch(f"{BASE}/ept-hierarchy/{k}.json")
            if sub is None:
                continue
            page = json.loads(sub)
            walk(root, want, k, page, out, seen - {k})
            continue
        if count == 0:
            continue
        d = int(k.split("-")[0])
        if not overlaps(node_bounds(root, k), want):
            continue
        out.append(k)
        if d < DEPTH:
            dd, x, y, z = (int(v) for v in k.split("-"))
            for ox in (0, 1):
                for oy in (0, 1):
                    for oz in (0, 1):
                        stack.append(f"{dd + 1}-{x * 2 + ox}-{y * 2 + oy}-{z * 2 + oz}")
    return out


_lock = threading.Lock()
_done = [0]


def load_node(key, total):
    path = os.path.join(CACHE_DIR, key + ".laz")
    if os.path.exists(path) and os.path.getsize(path) > 0:
        raw = open(path, "rb").read()
    else:
        raw = fetch(f"{BASE}/ept-data/{key}.laz")
        if raw is None:
            raw = b""
        open(path, "wb").write(raw)
    with _lock:
        _done[0] += 1
        if _done[0] % 100 == 0 or _done[0] == total:
            print(f"  {_done[0]:5d}/{total} nodes", flush=True)
    if not raw:
        return None
    try:
        las = laspy.read(io.BytesIO(raw))
    except Exception as exc:
        print(f"  {key}: {type(exc).__name__}: {exc}")
        return None
    return (np.asarray(las.x, dtype=np.float64),
            np.asarray(las.y, dtype=np.float64),
            np.asarray(las.z, dtype=np.float64),
            np.asarray(las.classification, dtype=np.uint8))


def main():
    os.makedirs(CACHE_DIR, exist_ok=True)
    ept = json.loads(fetch(f"{BASE}/ept.json"))
    root = ept["bounds"]
    print(f"{PROJECT}: span={ept['span']} srs={ept.get('srs', {}).get('horizontal')}")

    pad = 40.0
    west = LON0 + (EXTENT[0] - pad) / M_PER_DEG_LON
    east = LON0 + (EXTENT[1] + pad) / M_PER_DEG_LON
    south = LAT0 + (EXTENT[2] - pad) / M_PER_DEG_LAT
    north = LAT0 + (EXTENT[3] + pad) / M_PER_DEG_LAT
    mx0, my0 = to_mercator(west, south)
    mx1, my1 = to_mercator(east, north)
    want = (mx0, my0, -1e9, mx1, my1, 1e9)
    print(f"campus in EPSG:3857  x {mx0:.0f}..{mx1:.0f}  y {my0:.0f}..{my1:.0f}")

    print(f"walking hierarchy to depth {DEPTH} ...")
    keys = walk(root, want)
    print(f"{len(keys):,} overlapping nodes")

    nx = int(round((EXTENT[1] - EXTENT[0]) / GRID)) + 1
    ny = int(round((EXTENT[3] - EXTENT[2]) / GRID)) + 1
    dtm = np.full((ny, nx), np.inf, dtype=np.float32)
    dsm = np.full((ny, nx), -np.inf, dtype=np.float32)
    bld = np.full((ny, nx), -np.inf, dtype=np.float32)
    veg = np.full((ny, nx), -np.inf, dtype=np.float32)
    hits = np.zeros((ny, nx), dtype=np.int32)

    # Mercator is conformal but not equidistant: at this latitude one metre on the
    # ground is 1/cos(lat) metres of Mercator northing. Undoing that is a per-point
    # inverse, which is cheap enough and keeps everything in true metres.
    def to_local(mx, my):
        lon = np.degrees(mx / R_EARTH)
        lat = np.degrees(2.0 * np.arctan(np.exp(my / R_EARTH)) - math.pi / 2.0)
        return ((lon - LON0) * M_PER_DEG_LON, (lat - LAT0) * M_PER_DEG_LAT)

    def accumulate(chunk):
        if chunk is None:
            return
        mx, my, mz, cls = chunk
        x, y = to_local(mx, my)
        z = (mz - Z_REF).astype(np.float32)
        keep = ((x >= EXTENT[0]) & (x <= EXTENT[1])
                & (y >= EXTENT[2]) & (y <= EXTENT[3]))
        if not keep.any():
            return
        x, y, z, cls = x[keep], y[keep], z[keep], cls[keep]
        col = np.clip(((x - EXTENT[0]) / GRID).astype(np.int32), 0, nx - 1)
        row = np.clip(((y - EXTENT[2]) / GRID).astype(np.int32), 0, ny - 1)
        flat = row.astype(np.int64) * nx + col
        np.maximum.at(dsm.reshape(-1), flat, z)
        np.add.at(hits.reshape(-1), flat, 1)
        g = cls == CLASS_GROUND
        if g.any():
            np.minimum.at(dtm.reshape(-1), flat[g], z[g])
        b = cls == CLASS_BUILDING
        if b.any():
            np.maximum.at(bld.reshape(-1), flat[b], z[b])
        v = np.isin(cls, CLASS_VEG)
        if v.any():
            np.maximum.at(veg.reshape(-1), flat[v], z[v])

    print(f"downloading and binning to a {nx} x {ny} grid at {GRID} m ...")
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for chunk in pool.map(lambda k: load_node(k, len(keys)), keys):
            accumulate(chunk)

    covered = int(np.count_nonzero(hits))
    print(f"cells with returns: {covered:,} of {nx * ny:,} "
          f"({100.0 * covered / (nx * ny):.1f}%)")

    dtm[~np.isfinite(dtm)] = np.nan
    dsm[~np.isfinite(dsm)] = np.nan
    bld[~np.isfinite(bld)] = np.nan
    veg[~np.isfinite(veg)] = np.nan

    np.savez_compressed(OUT_PATH, dtm=dtm, dsm=dsm, building=bld, veg=veg,
                        hits=hits, extent=np.array(EXTENT), grid=GRID, z_ref=Z_REF)
    print(f"written {OUT_PATH} ({os.path.getsize(OUT_PATH) / 1e6:.1f} MB)")
    for name, arr in (("dtm", dtm), ("dsm", dsm), ("building", bld), ("veg", veg)):
        good = np.isfinite(arr)
        if good.any():
            print(f"  {name:9s} {np.count_nonzero(good):9,} cells  "
                  f"{np.nanmin(arr):7.1f} .. {np.nanmax(arr):7.1f} m")


if __name__ == "__main__":
    main()
