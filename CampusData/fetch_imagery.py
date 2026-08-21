"""Download a public-domain NAIP orthophoto covering the campus extent.

Why this exists
---------------
Everything the GT vector layers do not name is currently painted one flat turf green.
From the air that is what makes the model read as a toy: 4.3 x 3.9 km of identical
plastic lawn, with no soil, no scrub, no off-campus roofs, no parking aprons, no shadow
history. No amount of procedural noise fixes that, because the missing information is
not detail - it is *where things are*.

USGS NAIP is 0.3 m/px, four band, and US federal public domain, so unlike the Georgia
Tech layers it can actually ship. Draping it on the terrain gives every unmodelled
square metre a plausible and correctly *located* colour for free.

The service caps a single request at 4000 x 4000 px, so the area is tiled and stitched.
Output is ortho.jpg plus ortho.json holding the exact Web Mercator bounds, which
build_campus_obj.py needs to compute terrain UVs.
"""
import io
import json
import math
import os
import time
import urllib.request

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE_DIR = os.path.join(HERE, "ortho_cache")
OUT_IMAGE = os.path.join(HERE, "ortho.jpg")
OUT_META = os.path.join(HERE, "ortho.json")

SERVICE = ("https://imagery.nationalmap.gov/arcgis/rest/services/"
           "USGSNAIPImagery/ImageServer/exportImage")
HEADERS = {"User-Agent": "gt-campus-model/1.0 (research)"}

LAT0, LON0 = 33.7756, -84.3963
M_PER_DEG_LAT = 110540.0
M_PER_DEG_LON = 111320.0 * math.cos(math.radians(LAT0))
EXTENT = (-2150.0, 2200.0, -1680.0, 2200.0)   # local metres: x0, x1, y0, y1

GROUND_MPP = 0.5      # metres of ground per output pixel
TILE_PX = 3600        # under the service's 4000 px cap, with headroom
R_EARTH = 6378137.0


def to_mercator(lon, lat):
    return (R_EARTH * math.radians(lon),
            R_EARTH * math.log(math.tan(math.pi / 4.0 + math.radians(lat) / 2.0)))


def local_to_mercator(x, y):
    return to_mercator(LON0 + x / M_PER_DEG_LON, LAT0 + y / M_PER_DEG_LAT)


def fetch_tile(bbox, size, path):
    if os.path.exists(path) and os.path.getsize(path) > 4096:
        return Image.open(path).convert("RGB")
    query = (f"?bbox={bbox[0]:.3f},{bbox[1]:.3f},{bbox[2]:.3f},{bbox[3]:.3f}"
             f"&bboxSR=3857&imageSR=3857&size={size[0]},{size[1]}"
             f"&format=jpg&pixelType=U8&noDataInterpretation=esriNoDataMatchAny"
             f"&interpolation=RSP_BilinearInterpolation&f=image")
    last = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(SERVICE + query, headers=HEADERS)
            with urllib.request.urlopen(req, timeout=180) as fh:
                raw = fh.read()
            img = Image.open(io.BytesIO(raw)).convert("RGB")
            if img.size != tuple(size):
                img = img.resize(tuple(size), Image.LANCZOS)
            with open(path, "wb") as out:
                out.write(raw)
            return img
        except Exception as exc:  # noqa: BLE001
            last = exc
            time.sleep(4 * (attempt + 1))
    raise RuntimeError(f"tile {bbox} failed: {last}")


def main():
    os.makedirs(CACHE_DIR, exist_ok=True)
    x0, x1, y0, y1 = EXTENT
    # Mercator is conformal but not equal-scale; at 33.8 deg one Mercator metre is about
    # 0.83 ground metres, so the pixel size has to be scaled or the image comes back
    # ~20% coarser than asked for.
    merc_per_ground = 1.0 / math.cos(math.radians(LAT0))
    mpp = GROUND_MPP * merc_per_ground

    mx0, my0 = local_to_mercator(x0, y0)
    mx1, my1 = local_to_mercator(x1, y1)
    width = int(round((mx1 - mx0) / mpp))
    height = int(round((my1 - my0) / mpp))
    print(f"campus {x1 - x0:.0f} x {y1 - y0:.0f} m -> ortho {width:,} x {height:,} px "
          f"at {GROUND_MPP} m/px")

    nx = math.ceil(width / TILE_PX)
    ny = math.ceil(height / TILE_PX)
    print(f"{nx} x {ny} = {nx * ny} tiles from USGS NAIP (public domain)")

    canvas = Image.new("RGB", (width, height))
    for iy in range(ny):
        for ix in range(nx):
            px0 = ix * TILE_PX
            py0 = iy * TILE_PX
            pw = min(TILE_PX, width - px0)
            ph = min(TILE_PX, height - py0)
            bx0 = mx0 + px0 * mpp
            bx1 = bx0 + pw * mpp
            # image rows run top-down, Mercator y runs bottom-up
            by1 = my1 - py0 * mpp
            by0 = by1 - ph * mpp
            path = os.path.join(CACHE_DIR, f"naip_{ix}_{iy}.jpg")
            tile = fetch_tile((bx0, by0, bx1, by1), (pw, ph), path)
            canvas.paste(tile, (px0, py0))
            print(f"  tile {ix},{iy}  {pw} x {ph} px")

    canvas.save(OUT_IMAGE, quality=90, subsampling=1, optimize=True)
    with open(OUT_META, "w", encoding="utf-8") as fh:
        json.dump({"source": "USGS NAIP (public domain)",
                   "mercator": [mx0, my0, mx1, my1],
                   "size": [width, height],
                   "ground_mpp": GROUND_MPP,
                   "extent_local": list(EXTENT)}, fh, indent=2)
    mb = os.path.getsize(OUT_IMAGE) / 1e6
    print(f"written {OUT_IMAGE} ({mb:.1f} MB) and {os.path.basename(OUT_META)}")


if __name__ == "__main__":
    main()
