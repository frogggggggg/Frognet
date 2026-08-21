"""Download a USGS 3DEP elevation raster covering the campus model extent.

Requested in EPSG:4326 so the pixel grid is regular in lon/lat, which makes sampling
from the local ENU frame a simple linear lookup. Saves terrain.npz (float32 metres).
"""

import io
import os
import urllib.parse
import urllib.request

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "terrain.npz")

USGS = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer"

# Covers the full model extent (buildings, off-campus context, trees) with margin.
LON_MIN, LON_MAX = -84.4200, -84.3720
LAT_MIN, LAT_MAX = 33.7600, 33.7960

# ~1 m ground sample distance. Georgia is covered by QL2 lidar, so 3DEP has genuine 1 m
# data here; asking for 2 m threw away detail the source actually holds and left ridges
# and cut slopes looking rounded off.
#
# WIDTH:HEIGHT must equal the bbox aspect ratio in DEGREES (0.048 / 0.036 = 4:3). It did
# not, and the ImageServer quietly re-fitted the extent to the requested pixel aspect, so
# the northern and southern tile rows came back covering different latitudes. Stitched,
# that produced an 18 m cliff straight across the middle of the campus - which is what the
# stair generator then dutifully carved a staircase into.
WIDTH = 4440
HEIGHT = 3330
TILES = 2               # the ImageServer rejects a single request this large


def fetch_tile(lon0, lat0, lon1, lat1, w, h):
    params = urllib.parse.urlencode({
        "bbox": f"{lon0},{lat0},{lon1},{lat1}",
        "bboxSR": 4326,
        "imageSR": 4326,
        "size": f"{w},{h}",
        "format": "tiff",
        "pixelType": "F32",
        "noData": "-9999",
        "interpolation": "RSP_BilinearInterpolation",
        "f": "image",
    })
    req = urllib.request.Request(f"{USGS}/exportImage?{params}",
                                headers={"User-Agent": "campus-terrain/1.0"})
    with urllib.request.urlopen(req, timeout=300) as resp:
        raw = resp.read()

    from PIL import Image
    Image.MAX_IMAGE_PIXELS = None
    return np.array(Image.open(io.BytesIO(raw)), dtype=np.float32), len(raw)


def main():
    # Georgia is covered by QL2 lidar so 3DEP genuinely holds ~1 m data here, but the
    # ImageServer caps a single export well below that for this footprint. Requesting a
    # grid of tiles and stitching them keeps the native detail.
    tw, th = WIDTH // TILES, HEIGHT // TILES
    rows = []
    total_bytes = 0
    print(f"requesting 3DEP raster as {TILES}x{TILES} tiles of {tw}x{th} ...")
    for j in range(TILES):
        lat0 = LAT_MIN + (LAT_MAX - LAT_MIN) * j / TILES
        lat1 = LAT_MIN + (LAT_MAX - LAT_MIN) * (j + 1) / TILES
        row = []
        for i in range(TILES):
            lon0 = LON_MIN + (LON_MAX - LON_MIN) * i / TILES
            lon1 = LON_MIN + (LON_MAX - LON_MIN) * (i + 1) / TILES
            tile, nbytes = fetch_tile(lon0, lat0, lon1, lat1, tw, th)
            total_bytes += nbytes
            row.append(tile)
            print(f"  tile {i},{j}: {tile.shape} {nbytes / 1e6:.1f} MB")
        rows.append(np.hstack(row))
    # Tiles arrive north-up, so the northern row must come first when stacking.
    grid = np.vstack(rows[::-1])
    print(f"  {total_bytes / 1e6:.1f} MB tiff total")
    print(f"  array {grid.shape}")

    valid = grid[(grid > -1000) & np.isfinite(grid)]
    print(f"  elevation {valid.min():.1f} .. {valid.max():.1f} m   mean {valid.mean():.1f} m")
    print(f"  nodata pixels: {int((grid <= -1000).sum())}")

    # Row 0 of the image is the NORTH edge; store so that index 0 == LAT_MIN.
    grid = np.flipud(grid)
    grid = np.where(np.isfinite(grid) & (grid > -1000), grid, np.nan).astype(np.float32)

    np.savez_compressed(
        OUT,
        grid=grid,
        lon_min=LON_MIN, lon_max=LON_MAX,
        lat_min=LAT_MIN, lat_max=LAT_MAX,
    )
    print(f"saved {OUT} ({os.path.getsize(OUT) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
