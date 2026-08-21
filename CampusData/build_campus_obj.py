"""Build the Georgia Tech campus OBJ with real terrain, real 3D buildings and draped ground.

Inputs (produced by fetch_layers.py / fetch_terrain.py / fetch_i3s.py):
    *.geojson        GT Facilities vector layers
    terrain.npz      USGS 3DEP elevation raster
    buildings3d.npz  GT's I3S building meshes, already in local ENU metres

Everything is placed in a local east/north/up frame in metres, with z = 0 at Z_REF metres
above sea level. Output is Y-up OBJ so Blender's default import orientation is correct.
"""

import json
import math
import os

import mapbox_earcut as earcut
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
OBJ_PATH = os.path.join(HERE, "gt_campus.obj")
MTL_PATH = os.path.join(HERE, "gt_campus.mtl")

LAT0, LON0 = 33.7756, -84.3963
M_PER_DEG_LAT = 110540.0
M_PER_DEG_LON = 111320.0 * math.cos(math.radians(LAT0))

Z_REF = 283.0          # metres ASL that becomes z = 0 (roughly Tech Green)
FT = 0.3048
FLOOR_HEIGHT = 3.8
MIN_BUILDING_HEIGHT = 4.0
DEFAULT_HEIGHT = 9.0

TERRAIN_STEP = 4.0      # metres between terrain grid points
# Paving must not be subdivided COARSER than the terrain grid. When it was, the terrain
# carried detail the road could not follow, so hillocks pushed through the tarmac and
# long triangles read as crumpled facets. Matching the terrain step is enough; going
# finer than it just multiplies triangles for no visible gain.
MAX_GROUND_EDGE = 4.0
GROUND_OFFSET = 0.05    # the single height at which all paving sits above terrain
EXTENT = (-2150.0, 2200.0, -1680.0, 2200.0)   # x0, x1, y0, y1

# Paving layers, most specific first. Each one has every layer above it subtracted, so
# the whole set becomes a single non-overlapping partition of the ground and they can
# all share one offset. The old scheme gave every layer its own height so the depth
# buffer could separate them, which is what produced the kerb-height steps between lawn
# and footpath and the z-fighting wherever two layers were drawn over each other.
# The last column is the subdivision budget: hard paving has to hug the terrain closely
# or it pokes through, lawn sits on terrain that is already the same colour so it does
# not, and giving it road density cost millions of triangles for nothing.
GROUND_LAYERS = [
    ("roads", "Roads", "road", 4.0),
    ("parking", "Parking", "parking", 5.0),
    ("sidewalks", "Sidewalks", "sidewalk", 4.0),
    ("recreation_fields", "RecreationFields", "field", 7.0),
    ("landscape_areas", "LandscapeAreas", "grass", 9.0),
]

MATERIALS = {
    "terrain": (0.21, 0.25, 0.16),
    "grass": (0.24, 0.34, 0.18),
    "road": (0.16, 0.16, 0.17),
    "parking": (0.22, 0.22, 0.23),
    "field": (0.30, 0.44, 0.20),
    "sidewalk": (0.62, 0.61, 0.58),
    "facade": (0.55, 0.40, 0.32),
    "facade_simple": (0.50, 0.48, 0.46),
    "roof_photo": (0.30, 0.29, 0.28),
    "foundation": (0.42, 0.41, 0.39),
    "monument": (0.64, 0.63, 0.60),
    "stairs": (0.58, 0.57, 0.54),
    "furniture_wood": (0.35, 0.24, 0.15),
    "furniture_metal": (0.30, 0.31, 0.33),
    "tree_trunk": (0.28, 0.20, 0.13),
    "tree_canopy": (0.16, 0.34, 0.14),
    "shrub": (0.19, 0.30, 0.15),
    "marking": (0.80, 0.79, 0.75),
    "marking_yellow": (0.66, 0.50, 0.11),
    "callbox_blue": (0.05, 0.13, 0.45),
    "art_bronze": (0.17, 0.13, 0.09),
    "art_stone": (0.60, 0.58, 0.54),
    "art_painted": (0.52, 0.15, 0.11),
    "water": (0.04, 0.11, 0.14),
}


# --------------------------------------------------------------------------- terrain

class Terrain:
    """Regular ENU grid resampled from the 3DEP raster.

    Ground geometry is draped using this grid (not the raw DEM) so that draped polygons
    sit exactly on the rendered terrain surface regardless of DEM resolution.
    """

    def __init__(self, path, extent, step):
        data = np.load(path)
        dem = data["grid"]
        lon_min, lon_max = float(data["lon_min"]), float(data["lon_max"])
        lat_min, lat_max = float(data["lat_min"]), float(data["lat_max"])
        rows, cols = dem.shape

        self.x0, self.x1, self.y0, self.y1 = extent
        self.step = step
        self.nx = int(math.ceil((self.x1 - self.x0) / step)) + 1
        self.ny = int(math.ceil((self.y1 - self.y0) / step)) + 1
        self.xs = self.x0 + np.arange(self.nx) * step
        self.ys = self.y0 + np.arange(self.ny) * step

        gx, gy = np.meshgrid(self.xs, self.ys)
        lon = LON0 + gx / M_PER_DEG_LON
        lat = LAT0 + gy / M_PER_DEG_LAT
        col = (lon - lon_min) / (lon_max - lon_min) * (cols - 1)
        row = (lat - lat_min) / (lat_max - lat_min) * (rows - 1)
        col = np.clip(col, 0, cols - 1.001)
        row = np.clip(row, 0, rows - 1.001)

        c0, r0 = np.floor(col).astype(int), np.floor(row).astype(int)
        fc, fr = col - c0, row - r0
        dem = np.nan_to_num(dem, nan=float(np.nanmedian(dem)))
        top = dem[r0, c0] * (1 - fc) + dem[r0, c0 + 1] * fc
        bot = dem[r0 + 1, c0] * (1 - fc) + dem[r0 + 1, c0 + 1] * fc
        self.z = (top * (1 - fr) + bot * fr).astype(np.float64) - Z_REF

    def height(self, x, y):
        """Bilinear height of the terrain surface at local ENU coordinates."""
        x = np.clip(np.asarray(x, dtype=np.float64), self.x0, self.x1 - 1e-6)
        y = np.clip(np.asarray(y, dtype=np.float64), self.y0, self.y1 - 1e-6)
        cx = (x - self.x0) / self.step
        cy = (y - self.y0) / self.step
        i0 = np.clip(np.floor(cx).astype(int), 0, self.nx - 2)
        j0 = np.clip(np.floor(cy).astype(int), 0, self.ny - 2)
        fx, fy = cx - i0, cy - j0
        z = self.z
        top = z[j0, i0] * (1 - fx) + z[j0, i0 + 1] * fx
        bot = z[j0 + 1, i0] * (1 - fx) + z[j0 + 1, i0 + 1] * fx
        return top * (1 - fy) + bot * fy

    def mesh(self):
        gx, gy = np.meshgrid(self.xs, self.ys)
        verts = np.column_stack([gx.ravel(), gy.ravel(), self.z.ravel()])
        i, j = np.meshgrid(np.arange(self.nx - 1), np.arange(self.ny - 1))
        i, j = i.ravel(), j.ravel()
        a = j * self.nx + i
        b = a + 1
        c = a + self.nx
        d = c + 1
        tris = np.vstack([np.column_stack([a, c, b]), np.column_stack([b, c, d])])
        return verts, tris


# --------------------------------------------------------------------------- ortho

R_EARTH = 6378137.0


def ortho_uv(x, y, meta):
    """Map local ENU metres onto the NAIP orthophoto.

    Done exactly rather than as a linear fit: Web Mercator's northing is a log-tangent
    of latitude, and over the 3.9 km north-south span the quadratic term is worth about
    6 m of drift at the edges. On a 0.5 m/px image that is a twelve pixel smear, which
    on a road edge is obvious.
    """
    mx0, my0, mx1, my1 = meta["mercator"]
    lon = LON0 + np.asarray(x, dtype=np.float64) / M_PER_DEG_LON
    lat = LAT0 + np.asarray(y, dtype=np.float64) / M_PER_DEG_LAT
    mx = R_EARTH * np.radians(lon)
    my = R_EARTH * np.log(np.tan(np.pi / 4.0 + np.radians(lat) / 2.0))
    return np.column_stack([(mx - mx0) / (mx1 - mx0), (my - my0) / (my1 - my0)])


def load_ortho_meta():
    path = os.path.join(HERE, "ortho.json")
    if not os.path.exists(path):
        print("  no ortho.json - terrain falls back to flat turf; run fetch_imagery.py")
        return None
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


# ------------------------------------------------------------------------- geometry

def project(lon, lat):
    return ((lon - LON0) * M_PER_DEG_LON, (lat - LAT0) * M_PER_DEG_LAT)


def load(name):
    path = os.path.join(HERE, f"{name}.geojson")
    if not os.path.exists(path):
        return []
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh).get("features", [])


def polygons_of(geometry):
    if not geometry:
        return []
    if geometry.get("type") == "Polygon":
        return [geometry["coordinates"]]
    if geometry.get("type") == "MultiPolygon":
        return list(geometry["coordinates"])
    return []


def signed_area(ring):
    total = 0.0
    for i in range(len(ring)):
        x0, y0 = ring[i]
        x1, y1 = ring[(i + 1) % len(ring)]
        total += x0 * y1 - x1 * y0
    return total / 2.0


def prepare(polygon):
    """Project rings to metres, drop repeated points, force outer CCW / holes CW.

    Repeated vertices are not cosmetic: earcut produces missing wedges when a ring
    contains coincident consecutive points, which is what put slices through the roads.
    """
    rings, ends = [], []
    total = 0
    for index, ring in enumerate(polygon):
        pts = np.asarray(ring, dtype=np.float64)
        if pts.ndim != 2 or len(pts) < 4:
            continue
        pts = np.column_stack([(pts[:, 0] - LON0) * M_PER_DEG_LON,
                               (pts[:, 1] - LAT0) * M_PER_DEG_LAT])
        # collapse consecutive duplicates (including the closing point) to 1 mm
        keep = np.r_[True, np.any(np.abs(np.diff(pts, axis=0)) > 1e-3, axis=1)]
        pts = pts[keep]
        if len(pts) > 1 and np.all(np.abs(pts[0] - pts[-1]) < 1e-3):
            pts = pts[:-1]
        if len(pts) < 3:
            continue
        area = float(np.sum(pts[:, 0] * np.roll(pts[:, 1], -1)
                            - np.roll(pts[:, 0], -1) * pts[:, 1]) / 2.0)
        if abs(area) < 1e-6:
            continue
        if (area > 0) != (index == 0):
            pts = pts[::-1]
        rings.append(pts)
        total += len(pts)
        ends.append(total)
    if not rings:
        return None, None
    return np.concatenate(rings), np.array(ends, dtype=np.uint32)


_TRIANGULATE_FAILURES = 0


def triangulate(verts, ends):
    global _TRIANGULATE_FAILURES
    try:
        idx = earcut.triangulate_float64(verts, ends)
    except Exception as exc:  # noqa: BLE001
        _TRIANGULATE_FAILURES += 1
        if _TRIANGULATE_FAILURES <= 3:
            print(f"  !! triangulate failed ({len(verts)} pts, ends={type(ends).__name__}): {exc}")
        return np.zeros((0, 3), dtype=int)
    if len(idx) < 3:
        return np.zeros((0, 3), dtype=int)
    return np.asarray(idx, dtype=int).reshape(-1, 3)


_SUBDIV_TEMPLATES = {}


def _subdiv_template(n):
    """Barycentric weights and face indices for splitting one triangle into n^2."""
    cached = _SUBDIV_TEMPLATES.get(n)
    if cached is not None:
        return cached
    index, order = {}, []
    for i in range(n + 1):
        for j in range(n + 1 - i):
            index[(i, j)] = len(order)
            order.append((i, j, n - i - j))
    weights = np.array(order, dtype=np.float64) / n
    faces = []
    for i in range(n):
        for j in range(n - i):
            faces.append((index[(i, j)], index[(i + 1, j)], index[(i, j + 1)]))
            if i + j < n - 1:
                faces.append((index[(i + 1, j)], index[(i + 1, j + 1)], index[(i, j + 1)]))
    cached = (weights, np.array(faces, dtype=np.int64))
    _SUBDIV_TEMPLATES[n] = cached
    return cached


def subdiv_levels(tri_pts, max_edge, max_n=48):
    """Per-triangle subdivision level.

    Shared with the skirt builder so it can reproduce exactly the vertices subdivide()
    places along a boundary edge.
    """
    a, b, c = tri_pts[:, 0], tri_pts[:, 1], tri_pts[:, 2]
    longest = np.maximum.reduce([np.hypot(*(b - a).T), np.hypot(*(c - b).T),
                                 np.hypot(*(a - c).T)])
    return np.clip(np.ceil(longest / max_edge), 1, max_n).astype(np.int64)


def subdivide(tri_pts, max_edge, max_n=48):
    """Barycentric subdivision so long flat triangles can follow the terrain.

    tri_pts: (T, 3, 2) -> (T2, 3, 2). Triangles are batched by subdivision level and
    split with a cached template, which is roughly two orders of magnitude faster than
    building a dict of points per triangle.
    """
    tri_pts = np.asarray(tri_pts, dtype=np.float64)
    if len(tri_pts) == 0:
        return np.zeros((0, 3, 2))
    levels = subdiv_levels(tri_pts, max_edge, max_n)

    out = []
    for level in np.unique(levels):
        batch = tri_pts[levels == level]
        if level <= 1:
            out.append(batch)
            continue
        weights, faces = _subdiv_template(int(level))
        pts = np.einsum("pk,tkj->tpj", weights, batch)
        out.append(pts[:, faces.reshape(-1), :].reshape(-1, 3, 2))
    return np.concatenate(out, axis=0) if out else np.zeros((0, 3, 2))


# ---------------------------------------------------------------------------- parts

class Part:
    """A named mesh chunk. Optionally carries a per-vertex UV pair used to hand
    per-building facade parameters (base elevation, floor height) to the shader."""

    def __init__(self, name, material, textured=False):
        self.name = name
        self.material = material
        self.textured = textured
        self.verts = []
        self.tris = []
        self.uvs = []
        self._n = 0

    def add(self, verts, tris, uv=None):
        if len(verts) == 0 or len(tris) == 0:
            return
        verts = np.asarray(verts, dtype=np.float64)
        self.verts.append(verts)
        self.tris.append(np.asarray(tris, dtype=np.int64) + self._n)
        if self.textured:
            if uv is None:
                uv = np.zeros((len(verts), 2))
            uv = np.asarray(uv, dtype=np.float64)
            self.uvs.append(np.broadcast_to(uv, (len(verts), 2)) if uv.ndim == 1 else uv)
        self._n += len(verts)

    def finish(self):
        if not self.verts:
            return np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64), None
        uv = np.concatenate(self.uvs) if self.textured else None
        return weld(np.concatenate(self.verts), np.concatenate(self.tris), uv=uv)


def weld(verts, tris, precision=1000.0, uv=None):
    """Merge coincident vertices (to 1 mm), drop degenerates, drop repeated triangles.

    The I3S building mesh ships ~38,000 exactly coincident duplicate faces (some triples
    and one set of six). Two copies of the same wall at the same depth is textbook
    z-fighting, and it is what covered the facades in a shimmering diagonal weave that
    looked like a texture problem but was not.
    """
    key = np.round(verts * precision).astype(np.int64)
    _, first, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    tris = inverse.reshape(-1)[tris.reshape(-1)].reshape(-1, 3)
    keep = (tris[:, 0] != tris[:, 1]) & (tris[:, 1] != tris[:, 2]) & (tris[:, 0] != tris[:, 2])
    tris = tris[keep]
    _, unique_first = np.unique(np.sort(tris, axis=1), axis=0, return_index=True)
    tris = tris[np.sort(unique_first)]
    return verts[first], tris, (uv[first] if uv is not None else None)


def drape(terrain, x, y, radius=TERRAIN_STEP * 0.6):
    """Terrain height dilated over a small neighbourhood.

    Paving vertices sample the terrain at points, but the triangle between two samples is
    a straight line while the terrain mesh bulges between its own grid nodes. Wherever the
    ground was convex, that straight line cut underneath it and the terrain surfaced
    through the road. Taking the maximum of a 3x3 stencil lifts each paving vertex to the
    local high point, so the flat span between vertices stays clear, and it also fills the
    one-cell pits in the raw lidar that made long runs of road look crumpled.

    The same radius is used for every layer on purpose: they all ride the same lifted
    field, so the small offsets that stack grass under road under sidewalk still hold.
    """
    o = (-radius, 0.0, radius)
    z = None
    for dx in o:
        for dy in o:
            s = terrain.height(x + dx, y + dy)
            z = s if z is None else np.maximum(z, s)
    return z


def shapely_polys(features):
    """Project a GeoJSON layer into a list of valid shapely polygons in metres."""
    from shapely.geometry import Polygon
    out = []
    for feature in features:
        for polygon in polygons_of(feature.get("geometry")):
            flat, ends = prepare(polygon)
            if flat is None:
                continue
            rings, start = [], 0
            for e in ends:
                rings.append(flat[start:int(e)])
                start = int(e)
            if len(rings[0]) < 3:
                continue
            poly = Polygon(rings[0], [r for r in rings[1:] if len(r) >= 3])
            if not poly.is_valid:
                poly = poly.buffer(0)
            if poly.is_empty:
                continue
            out.append(poly)
    return out


def partition_ground(layers, exclude=None):
    """Cut the paving layers into one non-overlapping planar partition.

    Every ground artefact this model has had - kerbs that read as retaining walls,
    sawtooth skirts, z-fighting between road and footpath, hillocks poking through
    tarmac - traces back to the same cause: GT publishes its surfaces as independent
    overlapping polygons, and the model stacked them a few centimetres apart so the
    depth buffer could tell them apart. That stack is what you see.

    Here each layer instead has every higher-priority layer subtracted from it, so no
    two faces ever occupy the same ground. They can then all sit at one offset, which
    means there is nothing left to fight and nothing to hide with a skirt except the
    outer edge of the paving as a whole.

    Priority is most-specific-first: a road surface is a road even where the sidewalk
    layer has been drawn across it at a crossing.
    """
    from shapely.ops import unary_union
    claimed = None
    out = []
    for file_name, obj_name, material, max_edge in layers:
        features = load(file_name)
        if exclude and file_name in exclude:
            drop = exclude[file_name]
            features = [f for i, f in enumerate(features) if i not in drop]
        geom = unary_union(shapely_polys(features))
        if geom.is_empty:
            continue
        if claimed is not None:
            geom = geom.difference(claimed)
            if geom.is_empty:
                continue
        claimed = geom if claimed is None else unary_union([claimed, geom])
        out.append((obj_name, material, max_edge, geom))
    return out, claimed


def _rings_of(geom):
    """Yield (flat, ends) for every polygon in a shapely geometry."""
    from shapely.geometry import Polygon
    polys = [geom] if isinstance(geom, Polygon) else list(getattr(geom, "geoms", []))
    for poly in polys:
        if poly.is_empty or poly.area < 1e-6:
            continue
        rings, ends, total = [], [], 0
        for i, ring in enumerate([poly.exterior] + list(poly.interiors)):
            pts = np.asarray(ring.coords, dtype=np.float64)[:-1]
            if len(pts) < 3:
                continue
            area = float(np.sum(pts[:, 0] * np.roll(pts[:, 1], -1)
                                - np.roll(pts[:, 0], -1) * pts[:, 1]) / 2.0)
            if abs(area) < 1e-9:
                continue
            if (area > 0) != (i == 0):
                pts = pts[::-1]
            rings.append(pts)
            total += len(pts)
            ends.append(total)
        if rings:
            # earcut wants a uint32 array here, not a list. Passing a list raises inside
            # the extension, which triangulate() used to swallow - the ground silently
            # vanished while the build still reported hectares of paving.
            yield np.vstack(rings), np.array(ends, dtype=np.uint32)


def _edge_key(a, b):
    """Order-independent 1 mm key for a polygon edge, as a pair of int64 tuples."""
    ka = (int(round(a[0] * 1000)), int(round(a[1] * 1000)))
    kb = (int(round(b[0] * 1000)), int(round(b[1] * 1000)))
    return (ka, kb) if ka <= kb else (kb, ka)


def build_ground_partition(terrain, layers, offset=GROUND_OFFSET, skirt_drop=0.35,
                           ortho=None):
    """Drape the partitioned paving and skirt only its outer edge.

    Because the layers tile exactly, an edge shared by two of them needs no skirt: the
    surfaces meet at the same height and the same vertices. Only edges used once in the
    whole partition are real boundaries against open ground, and those get the kerb.
    Counting is done on the source rings rather than on the subdivided mesh, so it is not
    confused by two layers choosing different subdivision levels.
    """
    prepared = []
    counts = {}
    for obj_name, material, max_edge, geom in layers:
        pieces = []
        for flat, ends in _rings_of(geom):
            tris = triangulate(flat, ends)
            if len(tris) == 0:
                continue
            pieces.append((flat, ends, tris, max_edge))
            start = 0
            for e in ends:
                ring = flat[start:int(e)]
                start = int(e)
                for i in range(len(ring)):
                    k = _edge_key(ring[i], ring[(i + 1) % len(ring)])
                    counts[k] = counts.get(k, 0) + 1
        prepared.append((obj_name, material, pieces))

    parts = []
    skirted = 0
    for obj_name, material, pieces in prepared:
        part = Part(obj_name, material, textured=ortho is not None)
        for flat, ends, tris, max_edge in pieces:
            pts = subdivide(flat[tris], max_edge)
            if len(pts) == 0:
                continue
            v = pts.reshape(-1, 2)
            z = drape(terrain, v[:, 0], v[:, 1]) + offset
            part.add(np.column_stack([v, z]), np.arange(len(v)).reshape(-1, 3),
                     uv=ortho_uv(v[:, 0], v[:, 1], ortho) if ortho else None)

            # Walk each outer ring edge with the same sub-vertices the adjacent parent
            # triangle produced, so the top of the kerb is the paving edge exactly and
            # not a re-sampled approximation of it.
            parents = flat[tris]
            levels = subdiv_levels(parents, max_edge)
            lines = []
            for t in range(len(tris)):
                for ci in range(3):
                    pa, pb = parents[t, ci], parents[t, (ci + 1) % 3]
                    if counts.get(_edge_key(pa, pb), 0) != 1:
                        continue
                    n = int(levels[t])
                    lines.append(pa + (pb - pa) * (np.arange(n + 1)[:, None] / n))
            if not lines:
                continue
            skirted += len(lines)
            a2 = np.vstack([ln[:-1] for ln in lines])
            b2 = np.vstack([ln[1:] for ln in lines])
            za = drape(terrain, a2[:, 0], a2[:, 1]) + offset
            zb = drape(terrain, b2[:, 0], b2[:, 1]) + offset
            quads = np.concatenate([
                np.column_stack([a2, za]),
                np.column_stack([b2, zb]),
                np.column_stack([b2, zb - skirt_drop]),
                np.column_stack([a2, za - skirt_drop]),
            ]).reshape(4, -1, 3).transpose(1, 0, 2).reshape(-1, 3)
            base = np.arange(len(a2)) * 4
            part.add(quads,
                     np.vstack([np.column_stack([base, base + 1, base + 2]),
                                np.column_stack([base, base + 2, base + 3])]),
                     uv=ortho_uv(quads[:, 0], quads[:, 1], ortho) if ortho else None)
        parts.append(part)
    return parts, skirted




def build_extruded(terrain, features, name, material, occupied):
    """Extrude footprints that the real 3D model does not already cover."""
    part = Part(name, material, textured=True)
    kept = skipped = 0
    for feature in features:
        props = feature.get("properties") or {}
        floors = props.get("FLOORCOUNT")
        valid = isinstance(floors, (int, float)) and 0 < floors <= 60
        height = max(float(floors) * FLOOR_HEIGHT, MIN_BUILDING_HEIGHT) if valid else DEFAULT_HEIGHT
        for polygon in polygons_of(feature.get("geometry")):
            flat, ends = prepare(polygon)
            if flat is None:
                continue
            cx, cy = flat[:, 0].mean(), flat[:, 1].mean()
            if occupied is not None and occupied(cx, cy):
                skipped += 1
                continue
            tris = triangulate(flat, ends)
            if len(tris) == 0:
                continue
            base = float(terrain.height(flat[:, 0], flat[:, 1]).min()) - 0.5
            top = base + height
            low = np.column_stack([flat, np.full(len(flat), base)])
            high = np.column_stack([flat, np.full(len(flat), top)])
            verts = np.vstack([low, high])
            n = len(flat)
            faces = [tris + n]  # roof
            start = 0
            for end in ends:
                ring = np.arange(start, int(end))
                nxt = np.roll(ring, -1)
                faces.append(np.column_stack([ring, nxt, nxt + n]))
                faces.append(np.column_stack([ring, nxt + n, ring + n]))
                start = int(end)
            floor_h = min(max(height / float(floors), 2.9), 6.0) if valid else 3.9
            uv = np.array([base, round(height) * 100.0 + floor_h])
            part.add(verts, np.concatenate(faces), uv)
            kept += 1
    return part, kept, skipped


def building_mask(verts, cell=3.0):
    """Return a vectorised test: does this point land on the 3D building model?

    The I3S mesh includes roofs, so hashing every vertex at a few metres covers the whole
    footprint, not just the walls. No dilation: walkways legitimately run right up to a
    building face and must survive.
    """
    if verts is None or len(verts) == 0:
        return None
    keys = np.unique(np.floor(verts[:, :2] / cell).astype(np.int64) @ np.array([1000003, 1]))

    def inside(x, y):
        k = np.floor(np.column_stack([x, y]) / cell).astype(np.int64) @ np.array([1000003, 1])
        return np.isin(k, keys)

    return inside


def build_stairs(terrain, features, inside_building=None, avoid=None,
                 min_slope=0.24, max_slope=1.00,
                 riser=0.17, patch=3.5, min_steep_fraction=0.22):
    """Turn steep stretches of walkway into real flights of steps.

    This is the fallback, not the primary source. Surveyed OSM `highway=steps` lines are
    built as real geometry by `build_osm_steps` and passed in here as `avoid`, so this
    only fires on slopes nobody has mapped. The DEM smooths every flight into a ramp, so
    anything steeper than an ADA ramp (8.3%) that is not already a mapped staircase is in
    reality a terraced walk. The test is applied per patch rather than per polygon: GT's
    walkway polygons are large merged networks, and the interesting places - the
    Campanile bowl, the hillside behind Tech Green - are steep terraces inside an
    otherwise gentle polygon. Steep patches get clipped against terrain contours one
    riser apart; gentle patches stay draped ramps.
    """
    part = Part("Stairs", "stairs")
    steep = set()
    built = 0
    stepped_area = 0.0

    for index, feature in enumerate(features):
        for polygon in polygons_of(feature.get("geometry")):
            flat, ends = prepare(polygon)
            if flat is None or len(flat) < 3:
                continue
            tris = triangulate(flat, ends)
            if len(tris) == 0:
                continue
            pts = subdivide(flat[tris], patch)

            # GT's walkway polygons overlap the buildings they serve. Terracing the
            # overlap produced stacks of slabs punching out through the facade, so those
            # sub-triangles are dropped; they are buried inside the building anyway.
            # Sub-triangles near a surveyed flight are dropped for the opposite reason:
            # real geometry is already there and two staircases in one place z-fight.
            if inside_building is not None or avoid is not None:
                mid = pts.mean(axis=1)
                drop = np.zeros(len(pts), dtype=bool)
                if inside_building is not None:
                    drop |= inside_building(mid[:, 0], mid[:, 1])
                if avoid is not None:
                    drop |= avoid(mid[:, 0], mid[:, 1])
                pts = pts[~drop]
                if len(pts) == 0:
                    continue

            # One batched terrain lookup for the whole polygon. Sampling per sub-triangle
            # made this function 80% of the entire build.
            flatpts = pts.reshape(-1, 2)
            allz = drape(terrain, flatpts[:, 0], flatpts[:, 1]).reshape(-1, 3)
            corner_z = list(allz)
            grads = []
            for tri, fz in zip(pts, corner_z):
                e1 = tri[1] - tri[0]
                e2 = tri[2] - tri[0]
                det = e1[0] * e2[1] - e1[1] * e2[0]
                if abs(det) < 1e-9:
                    grads.append(0.0)
                    continue
                d1, d2 = fz[1] - fz[0], fz[2] - fz[0]
                gx = (d1 * e2[1] - d2 * e1[1]) / det
                gy = (d2 * e1[0] - d1 * e2[0]) / det
                grads.append(math.hypot(gx, gy))
            grads = np.array(grads)
            areas = np.array([abs((t[1, 0] - t[0, 0]) * (t[2, 1] - t[0, 1]) -
                                  (t[2, 0] - t[0, 0]) * (t[1, 1] - t[0, 1])) / 2 for t in pts])
            # The upper bound matters as much as the lower one. The bare-earth DEM has a
            # near vertical step wherever a building occluded the lidar, and a walkway
            # crossing one used to come out as a stack of horizontal plates hanging in the
            # air beside the wall. Nothing walkable exceeds about 1:1, so those are
            # retaining walls or data holes and the polygon stays an ordinary draped path.
            hot = (grads >= min_slope) & (grads <= max_slope)
            total = areas.sum()
            # One cliff sub-triangle is enough to ruin the whole polygon, because every
            # sub-triangle of a terraced walkway gets stepped: the cliff part came out as a
            # stack of slabs poking through the building wall beside it. If more than a
            # token area is unwalkable the polygon is not a staircase at all.
            if total <= 0 or areas[grads > max_slope].sum() > 0.02 * total:
                continue
            # A stray steep patch is DEM noise; a real flight covers a decent share of the
            # walkway it belongs to. Requiring both keeps ramps as ramps.
            if areas[hot].sum() < min_steep_fraction * total:
                continue

            steep.add(index)
            built += 1
            # Snap the reference to a global contour so neighbouring polygons in the same
            # flight land on the same treads instead of each starting its own staircase.
            z_ref = math.floor(float(allz.min()) / riser) * riser

            stepped_area += float(areas[hot].sum())
            for tri, fz in zip(pts, corner_z):
                # Every sub-triangle of a stepped walkway is stepped, including the gentle
                # ones. Mixing draped and stepped triangles inside one polygon left a riser
                # sized gap along every boundary between them; and a genuinely flat patch
                # falls entirely inside one contour band, so it comes out flat anyway.
                poly = [(tri[i, 0], tri[i, 1], fz[i]) for i in range(3)]
                k0 = int(math.floor((fz.min() - z_ref) / riser))
                k1 = int(math.floor((fz.max() - z_ref) / riser))
                if k1 - k0 > 60:
                    continue
                for k in range(k0, k1 + 1):
                    lo = z_ref + k * riser
                    hi = lo + riser
                    band = clip_scalar(poly, lo, hi)
                    m = len(band)
                    if m < 3:
                        continue
                    # Tread sits at the same offset the surrounding paving uses, so a
                    # flight meets the sidewalk it belongs to instead of stepping off it.
                    tread = hi + GROUND_OFFSET
                    ring = np.array([[p[0], p[1], tread] for p in band])
                    fan = np.column_stack([np.zeros(m - 2, dtype=int),
                                           np.arange(1, m - 1),
                                           np.arange(2, m)])
                    part.add(ring, fan)

                    # The riser belongs on the LOWER contour of the band: that is where the
                    # next band down sits a full riser lower. Skirting the whole band
                    # perimeter instead (the previous attempt) walled off every sub-triangle
                    # seam and turned the flights into a grid of 27 cm fins.
                    wall = []
                    for i in range(m):
                        p, q = band[i], band[(i + 1) % m]
                        if abs(p[2] - lo) > 1e-6 or abs(q[2] - lo) > 1e-6:
                            continue
                        wall.append([[p[0], p[1], lo + GROUND_OFFSET],
                                     [q[0], q[1], lo + GROUND_OFFSET],
                                     [q[0], q[1], tread],
                                     [p[0], p[1], tread]])
                    if wall:
                        quads = np.array(wall, dtype=float).reshape(-1, 3)
                        n = len(wall)
                        base = np.arange(n) * 4
                        part.add(quads,
                                 np.vstack([np.column_stack([base, base + 1, base + 2]),
                                            np.column_stack([base, base + 2, base + 3])]))

            # Close the outer boundary so the flight does not float over the terrain.
            start = 0
            for end in ends:
                # Densified to the same spacing as the treads. Sampling the raw ring left
                # the skirt top as a straight line between vertices that could be tens of
                # metres apart, which showed daylight under every step in between.
                ring = densify_ring(flat[start:int(end)], patch)
                rz = drape(terrain, ring[:, 0], ring[:, 1])
                stepped = z_ref + (np.floor((rz - z_ref) / riser) + 1) * riser + GROUND_OFFSET
                nxt = np.roll(np.arange(len(ring)), -1)
                buried = (inside_building(ring[:, 0], ring[:, 1])
                          if inside_building is not None
                          else np.zeros(len(ring), dtype=bool))
                for i, j in zip(range(len(ring)), nxt):
                    if buried[i] or buried[j]:
                        continue
                    quad = np.array([
                        [ring[i, 0], ring[i, 1], rz[i] - 0.30],
                        [ring[j, 0], ring[j, 1], rz[j] - 0.30],
                        [ring[j, 0], ring[j, 1], stepped[j]],
                        [ring[i, 0], ring[i, 1], stepped[i]],
                    ])
                    part.add(quad, np.array([[0, 1, 2], [0, 2, 3]]))
                start = int(end)
    print(f"stairs: {built} walkways terraced, {stepped_area:,.0f} m2 actually stepped "
          f"(slope over {min_slope * 100:.0f}%, {riser * 100:.1f} cm risers)")
    return part, built, steep


def densify_ring(ring, max_edge):
    """Insert points along a closed ring so no edge is longer than max_edge."""
    nxt = np.roll(ring, -1, axis=0)
    steps = np.maximum(1, np.ceil(np.hypot(*(nxt - ring).T) / max_edge).astype(int))
    out = [ring[i] + (nxt[i] - ring[i]) * (np.arange(steps[i])[:, None] / steps[i])
           for i in range(len(ring))]
    return np.vstack(out)


def clip_scalar(poly, lo, hi):
    """Sutherland-Hodgman clip of an (x, y, f) polygon to the band lo <= f <= hi."""
    def half(points, bound, keep_above):
        out = []
        for i in range(len(points)):
            a, b = points[i], points[(i + 1) % len(points)]
            ina = a[2] >= bound if keep_above else a[2] <= bound
            inb = b[2] >= bound if keep_above else b[2] <= bound
            if ina:
                out.append(a)
            if ina != inb and b[2] != a[2]:
                t = (bound - a[2]) / (b[2] - a[2])
                out.append((a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]), bound))
        return out

    clipped = half(poly, lo, True)
    if len(clipped) < 3:
        return []
    clipped = half(clipped, hi, False)
    return clipped if len(clipped) >= 3 else []


def footprint_index(features, cell=60.0):
    """Point-in-polygon test over every building footprint, bucketed by bounding box."""
    polys = []
    for feature in features:
        for polygon in polygons_of(feature.get("geometry")):
            flat, ends = prepare(polygon)
            if flat is None or len(flat) < 3:
                continue
            rings, start = [], 0
            for end in ends:
                rings.append(flat[start:int(end)])
                start = int(end)
            polys.append((flat.min(axis=0), flat.max(axis=0), rings))

    buckets = {}
    for idx, (mn, mx, _) in enumerate(polys):
        lo = np.floor(mn / cell).astype(int)
        hi = np.floor(mx / cell).astype(int)
        for i in range(lo[0], hi[0] + 1):
            for j in range(lo[1], hi[1] + 1):
                buckets.setdefault((i, j), []).append(idx)

    def contains(x, y):
        for idx in buckets.get((int(math.floor(x / cell)), int(math.floor(y / cell))), ()):
            mn, mx, rings = polys[idx]
            if not (mn[0] <= x <= mx[0] and mn[1] <= y <= mx[1]):
                continue
            inside = False
            for ring in rings:                       # even-odd, so holes cancel out
                b = np.roll(ring, -1, axis=0)
                cross = (ring[:, 1] > y) != (b[:, 1] > y)
                if not cross.any():
                    continue
                a1, b1 = ring[cross], b[cross]
                cut = a1[:, 0] + (y - a1[:, 1]) * (b1[:, 0] - a1[:, 0]) / (b1[:, 1] - a1[:, 1])
                inside ^= bool(int((cut > x).sum()) % 2)
            if inside:
                return True
        return False

    return contains


def surface_lookup(terrain, verts, footprints, cell=5.0, min_clear=2.5):
    """Height a point object should stand on: a building deck if it is inside one.

    GT's inventories include trees, bins and lights that sit on podiums, terraces and roof
    gardens. Draping those onto bare terrain buried them inside the building, which is why
    trees appeared to sprout out of ground floors.

    "Inside" has to be decided against the footprint polygons, not against the 3D mesh: a
    roof is a handful of huge triangles, so hashing its vertices leaves the middle of every
    building empty and the test never fires. The deck height then comes from the lowest
    modelled surface within a cell or two that clears terrain by min_clear - for a roof
    garden that is the surrounding parapet, which sits at the terrace level.
    """
    plain = terrain.height
    if verts is None or len(verts) == 0 or not footprints:
        return plain, (lambda x, y: np.zeros(np.size(x), dtype=bool))

    contains = footprint_index(footprints)
    sel = verts[:, 2] - plain(verts[:, 0], verts[:, 1]) >= min_clear
    k = np.floor(verts[sel, :2] / cell).astype(np.int64)
    z = verts[sel, 2]
    order = np.lexsort((k[:, 1], k[:, 0]))
    k, z = k[order], z[order]
    starts = np.flatnonzero(np.r_[True, np.any(k[1:] != k[:-1], axis=1)])
    decks = dict(zip(map(tuple, k[starts].tolist()), np.minimum.reduceat(z, starts).tolist()))

    def height(x, y):
        scalar = np.ndim(x) == 0
        x = np.atleast_1d(np.asarray(x, dtype=float))
        y = np.atleast_1d(np.asarray(y, dtype=float))
        out = np.asarray(plain(x, y), dtype=float).copy()
        ij = np.floor(np.column_stack([x, y]) / cell).astype(np.int64)
        for n, (i, j) in enumerate(ij.tolist()):
            if not contains(x[n], y[n]):
                continue
            near = [decks[(i + di, j + dj)]
                    for di in (-1, 0, 1) for dj in (-1, 0, 1)
                    if (i + di, j + dj) in decks]
            if near and min(near) > out[n] + min_clear:
                out[n] = min(near)
        return float(out[0]) if scalar else out

    def raised(x, y):
        return np.atleast_1d(height(x, y)) > np.asarray(plain(x, y), dtype=float) + min_clear

    return height, raised


def build_trees(surface, features):
    trunks = Part("Trees_Trunks", "tree_trunk")
    canopy = Part("Trees_Canopy", "tree_canopy")
    ts, cs = 4, 6
    # Crown profile: bottom point, four rings, apex - a rounded ovoid rather than a cone.
    RING_T = (0.10, 0.38, 0.66, 0.88)
    RING_R = (0.62, 1.00, 0.88, 0.46)

    pts, hts, crs, trs = [], [], [], []
    for feature in features:
        geom = feature.get("geometry")
        if not geom or geom.get("type") != "Point":
            continue
        props = feature.get("properties") or {}
        x, y = project(geom["coordinates"][0], geom["coordinates"][1])
        h = props.get("TOTHT")
        h = float(h) * FT if isinstance(h, (int, float)) and h > 1 else 7.0
        h = min(max(h, 1.5), 40.0)
        r = props.get("CanopyRadiusFT")
        if isinstance(r, (int, float)) and r > 0:
            cr = float(r) * FT
        else:
            spread = [props.get("CROWNWIDTHNS"), props.get("CROWNWIDTHEW")]
            spread = [float(s) for s in spread if isinstance(s, (int, float)) and s > 0]
            cr = (sum(spread) / len(spread) / 2.0) * FT if spread else h / 3.5
        d = props.get("DBH1")
        tr = (float(d) * 0.0254) / 2.0 if isinstance(d, (int, float)) and d > 0 else 0.12
        pts.append((x, y))
        hts.append(h)
        crs.append(min(max(cr, 0.5), min(9.0, h * 0.55)))
        trs.append(min(max(tr, 0.05), 1.2))

    if not pts:
        return trunks, canopy, 0

    pts = np.array(pts)
    hts = np.array(hts)
    crs = np.array(crs)
    trs = np.array(trs)
    ground = surface(pts[:, 0], pts[:, 1])
    crown_base = ground + hts * 0.32
    top = ground + hts

    idx_t = np.arange(ts)
    nxt_t = np.roll(idx_t, -1)
    trunk_faces = np.vstack([np.column_stack([idx_t, nxt_t, nxt_t + ts]),
                             np.column_stack([idx_t, nxt_t + ts, idx_t + ts])])

    # Canopy vertex order: rings bottom-to-top, then apex, then the bottom point.
    nr = len(RING_T)
    apex, bottom = nr * cs, nr * cs + 1
    i0 = np.arange(cs)
    n0 = np.roll(i0, -1)
    canopy_faces = [np.column_stack([n0, i0, np.full(cs, bottom)])]
    for r in range(nr - 1):
        a, b = i0 + r * cs, n0 + r * cs
        canopy_faces.append(np.column_stack([a, b, b + cs]))
        canopy_faces.append(np.column_stack([a, b + cs, a + cs]))
    top_ring = i0 + (nr - 1) * cs
    canopy_faces.append(np.column_stack([top_ring, np.roll(i0, -1) + (nr - 1) * cs,
                                         np.full(cs, apex)]))
    canopy_faces = np.vstack(canopy_faces)

    rng = np.random.default_rng(20240517)
    spin = rng.uniform(0.0, 2 * math.pi, len(pts))

    for k in range(len(pts)):
        x, y = pts[k]
        a_t = spin[k] + 2 * math.pi * np.arange(ts) / ts
        tx = x + np.cos(a_t) * trs[k]
        ty = y + np.sin(a_t) * trs[k]
        trunks.add(np.vstack([
            np.column_stack([tx, ty, np.full(ts, ground[k])]),
            np.column_stack([tx, ty, np.full(ts, crown_base[k] + 0.3)]),
        ]), trunk_faces)

        a_c = spin[k] + 2 * math.pi * np.arange(cs) / cs
        cosa, sina = np.cos(a_c), np.sin(a_c)
        span = top[k] - crown_base[k]
        rings = [np.column_stack([x + cosa * crs[k] * rr,
                                  y + sina * crs[k] * rr,
                                  np.full(cs, crown_base[k] + span * tt)])
                 for tt, rr in zip(RING_T, RING_R)]
        canopy.add(np.vstack(rings + [np.array([[x, y, top[k]], [x, y, crown_base[k]]])]),
                   canopy_faces)
    return trunks, canopy, len(pts)


def connected_components(n_verts, tris):
    """Union-find over triangle edges: one label per physically separate building."""
    parent = np.arange(n_verts, dtype=np.int64)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for edge in (tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]):
        for a, b in edge:
            ra, rb = find(a), find(b)
            if ra != rb:
                parent[max(ra, rb)] = min(ra, rb)

    labels = np.array([find(i) for i in range(n_verts)], dtype=np.int64)
    _, labels = np.unique(labels, return_inverse=True)
    return labels.reshape(-1)


def footprint_floor_lookup(features):
    """Bounding boxes + storey counts for GT footprints, used to derive floor heights.

    Footprints with no usable FLOORCOUNT still go in the table with floors = 0: knowing a
    piece of mesh stands on a building at all is what separates a facade from a monument.
    """
    boxes = []
    for feature in features:
        floors = (feature.get("properties") or {}).get("FLOORCOUNT")
        if not isinstance(floors, (int, float)) or not 0 < floors <= 60:
            floors = 0
        for polygon in polygons_of(feature.get("geometry")):
            flat, _ = prepare(polygon)
            if flat is None:
                continue
            lo, hi = flat.min(axis=0), flat.max(axis=0)
            boxes.append((lo[0], lo[1], hi[0], hi[1], float(floors)))
    return np.array(boxes) if boxes else np.zeros((0, 5))


def build_i3s(footprints, ortho=None):
    """Real building meshes, tagged per building with base elevation and floor height.

    The source is untextured massing, so the facade detail has to be generated. Baking
    each building's own base elevation and true storey height into the UVs is what lets
    the shader put window bands on that building's actual floor lines instead of on a
    single global grid.
    """
    path = os.path.join(HERE, "buildings3d.npz")
    if not os.path.exists(path):
        return None, None
    raw = np.load(path)["vertices"].astype(np.float64)
    raw[:, 2] -= Z_REF

    verts, tris, _ = weld(raw, np.arange(len(raw)).reshape(-1, 3))
    labels = connected_components(len(verts), tris)
    count = int(labels.max()) + 1

    order = np.argsort(labels, kind="stable")
    starts = np.flatnonzero(np.r_[True, labels[order][1:] != labels[order][:-1]])
    sizes = np.diff(np.r_[starts, len(order)])
    top = np.maximum.reduceat(verts[order, 2], starts)
    cx = np.add.reduceat(verts[order, 0], starts) / sizes
    cy = np.add.reduceat(verts[order, 1], starts) / sizes

    # A building often welds into several disconnected pieces (canopies, parapets, a
    # detached facade panel). Taking each piece's own lowest vertex would start its window
    # bands halfway up the wall, so ground level is read from the lowest geometry anywhere
    # in that piece's footprint instead.
    cell = 8.0
    keys = np.floor(verts[:, :2] / cell).astype(np.int64)
    korder = np.lexsort((keys[:, 1], keys[:, 0]))
    kk, kz = keys[korder], verts[korder, 2]
    kstart = np.flatnonzero(np.r_[True, np.any(kk[1:] != kk[:-1], axis=1)])
    cell_min = dict(zip(map(tuple, kk[kstart].tolist()),
                        np.minimum.reduceat(kz, kstart).tolist()))

    lo_x = np.minimum.reduceat(verts[order, 0], starts)
    hi_x = np.maximum.reduceat(verts[order, 0], starts)
    lo_y = np.minimum.reduceat(verts[order, 1], starts)
    hi_y = np.maximum.reduceat(verts[order, 1], starts)
    own_base = np.minimum.reduceat(verts[order, 2], starts)

    base = np.empty(count)
    for k in range(count):
        i0, i1 = int(lo_x[k] // cell), int(hi_x[k] // cell)
        j0, j1 = int(lo_y[k] // cell), int(hi_y[k] // cell)
        found = [cell_min[(i, j)] for i in range(i0, i1 + 1) for j in range(j0, j1 + 1)
                 if (i, j) in cell_min]
        base[k] = min(found) if found else own_base[k]

    floor_h = np.full(count, 3.9)
    plan = np.maximum(hi_x - lo_x, hi_y - lo_y)
    is_building = np.zeros(count, bool)
    # Which footprint each component stands on. A building welds into a dozen or more
    # disconnected pieces, and giving every piece its own base/height made the shader hash
    # differ from panel to panel: adjacent bits of the same wall drew different cladding
    # and different window pitches, which is the crosshatch that looked like z-fighting.
    owner = np.full(count, -1, dtype=np.int64)
    matched = 0
    if len(footprints):
        for k in range(count):
            # Overlap, not centroid containment: a welded piece is often a single facade
            # panel or a roof canopy whose centroid sits just outside the footprint.
            hit = ((footprints[:, 0] <= hi_x[k] + 3.0) & (lo_x[k] - 3.0 <= footprints[:, 2]) &
                   (footprints[:, 1] <= hi_y[k] + 3.0) & (lo_y[k] - 3.0 <= footprints[:, 3]))
            if not hit.any():
                continue
            is_building[k] = True
            idx = np.flatnonzero(hit)
            # Largest overlap wins, so a piece straddling two footprints picks one.
            ox = (np.minimum(footprints[idx, 2], hi_x[k]) - np.maximum(footprints[idx, 0], lo_x[k]))
            oy = (np.minimum(footprints[idx, 3], hi_y[k]) - np.maximum(footprints[idx, 1], lo_y[k]))
            owner[k] = idx[np.argmax(np.clip(ox, 0, None) * np.clip(oy, 0, None))]

    # Group the pieces of each building and give the whole group one base, one height and
    # one storey height, so the shader sees a single building.
    group = np.where(owner >= 0, owner, len(footprints) + np.arange(count))
    gids, ginv = np.unique(group, return_inverse=True)
    g_base = np.full(len(gids), np.inf)
    g_top = np.full(len(gids), -np.inf)
    np.minimum.at(g_base, ginv, base)
    np.maximum.at(g_top, ginv, top)
    base = g_base[ginv]
    top = g_top[ginv]
    for k in range(count):
        if owner[k] < 0:
            continue
        storeys = footprints[owner[k], 4]
        if storeys > 0:
            floor_h[k] = min(max((top[k] - base[k]) / storeys, 2.9), 6.0)
            matched += 1

    # Anything compact that isn't standing on a footprint is a monument, canopy, retaining
    # wall or bridge - the Kessler Campanile among them. Glazing those would be nonsense,
    # so they get split out and shaded as bare structure.
    structure = (~is_building) & (plan < 30.0)

    # v packs storey height and building height: v = round(height) * 100 + floor_h
    height = np.round(np.clip(top - base, 0, 400))
    uv = np.column_stack([base[labels], height[labels] * 100.0 + floor_h[labels]])

    keep_v = structure[labels]
    struct_tri = keep_v[tris].all(axis=1)

    # Roofs get lifted out and photo-mapped instead of shaded. Nadir imagery is the one
    # thing that sees a roof properly: it measures the real membrane colour, the gravel
    # ballast, the HVAC decks, the skylights and the decades of staining, per building,
    # for free. A single grey membrane shader can do none of that - it just replaced one
    # wrong uniform colour (brick everywhere) with a less wrong uniform colour.
    a, b, c = verts[tris[:, 0]], verts[tris[:, 1]], verts[tris[:, 2]]
    nrm = np.cross(b - a, c - a)
    area = np.linalg.norm(nrm, axis=1)
    up = np.divide(nrm[:, 2], area, out=np.zeros(len(tris)), where=area > 1e-9)
    mid_z = (a[:, 2] + b[:, 2] + c[:, 2]) / 3.0
    tri_base = base[labels[tris[:, 0]]]
    roof_tri = (~struct_tri) & (up > 0.75) & (mid_z > tri_base + 2.5)

    part = Part("Buildings_Campus3D", "facade", textured=True)
    part.add(verts, tris[~struct_tri & ~roof_tri], uv)
    roofs = Part("Buildings_Roofs", "roof_photo", textured=True)
    if ortho is not None and roof_tri.any():
        roofs.add(verts, tris[roof_tri], ortho_uv(verts[:, 0], verts[:, 1], ortho))
    else:
        part.add(verts, tris[roof_tri], uv)
    monuments = Part("Structures_Campus3D", "monument")
    monuments.add(verts, tris[struct_tri])
    print(f"buildings segmented: {count} components, {matched} matched to a storey count "
          f"(floor height {floor_h.min():.1f}-{floor_h.max():.1f} m); "
          f"{int(structure.sum())} free-standing structures shaded without glazing; "
          f"{int(roof_tri.sum()):,} roof faces photo-mapped")
    return part, monuments, raw, roofs


def box(cx, cy, cz, sx, sy, sz, angle):
    """Axis-aligned box rotated about Z, returned as (verts, tris)."""
    hx, hy, hz = sx / 2.0, sy / 2.0, sz / 2.0
    corners = np.array([[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy]])
    ca, sa = math.cos(angle), math.sin(angle)
    rot = np.column_stack([corners[:, 0] * ca - corners[:, 1] * sa,
                           corners[:, 0] * sa + corners[:, 1] * ca])
    low = np.column_stack([rot[:, 0] + cx, rot[:, 1] + cy, np.full(4, cz - hz)])
    high = np.column_stack([rot[:, 0] + cx, rot[:, 1] + cy, np.full(4, cz + hz)])
    tris = np.array([[0, 1, 2], [0, 2, 3],            # bottom (wound down)
                     [4, 6, 5], [4, 7, 6],            # top
                     [0, 4, 5], [0, 5, 1],
                     [1, 5, 6], [1, 6, 2],
                     [2, 6, 7], [2, 7, 3],
                     [3, 7, 4], [3, 4, 0]])
    return np.vstack([low, high]), tris


def prism(cx, cy, z0, z1, radius, sides, angle=0.0, top_radius=None):
    """Vertical prism with an optional taper, capped at the top."""
    top_radius = radius if top_radius is None else top_radius
    a = angle + 2 * math.pi * np.arange(sides) / sides
    low = np.column_stack([cx + np.cos(a) * radius, cy + np.sin(a) * radius, np.full(sides, z0)])
    high = np.column_stack([cx + np.cos(a) * top_radius, cy + np.sin(a) * top_radius,
                            np.full(sides, z1)])
    cap = np.array([[cx, cy, z1]])
    i = np.arange(sides)
    j = np.roll(i, -1)
    tris = np.vstack([
        np.column_stack([i, j, j + sides]),
        np.column_stack([i, j + sides, i + sides]),
        np.column_stack([i + sides, j + sides, np.full(sides, 2 * sides)]),
    ])
    return np.vstack([low, high, cap]), tris


def points_of(features):
    for feature in features:
        geom = feature.get("geometry")
        if geom and geom.get("type") == "Point":
            coords = geom["coordinates"]
            yield project(coords[0], coords[1]), (feature.get("properties") or {})


def edge_index(features, cell=30.0):
    """Bucket every polygon edge of a layer into a coarse grid for nearest-edge queries."""
    segs = []
    for feature in features:
        for polygon in polygons_of(feature.get("geometry")):
            flat, ends = prepare(polygon)
            if flat is None:
                continue
            start = 0
            for end in ends:
                ring = flat[start:int(end)]
                start = int(end)
                if len(ring) >= 2:
                    segs.append(np.stack([ring, np.roll(ring, -1, axis=0)], axis=1))
    if not segs:
        return None
    segs = np.concatenate(segs)
    mid = segs.mean(axis=1)
    buckets = {}
    for i, key in enumerate(map(tuple, np.floor(mid / cell).astype(np.int64).tolist())):
        buckets.setdefault(key, []).append(i)
    return segs, buckets, cell


def nearest_edge(index, x, y, rings=2):
    """Angle and distance of the closest polygon edge, or None if nothing is near."""
    if index is None:
        return None
    segs, buckets, cell = index
    i, j = int(math.floor(x / cell)), int(math.floor(y / cell))
    cand = []
    for di in range(-rings, rings + 1):
        for dj in range(-rings, rings + 1):
            cand.extend(buckets.get((i + di, j + dj), ()))
    if not cand:
        return None
    s = segs[cand]
    a, d = s[:, 0], s[:, 1] - s[:, 0]
    length2 = np.maximum((d * d).sum(axis=1), 1e-9)
    p = np.array([x, y])
    t = np.clip(((p - a) * d).sum(axis=1) / length2, 0.0, 1.0)
    off = a + d * t[:, None] - p
    dist = np.hypot(off[:, 0], off[:, 1])
    k = int(np.argmin(dist))
    return math.atan2(d[k, 1], d[k, 0]), float(dist[k])


def build_furniture(surface, walks=None, roads=None):
    """Benches, light poles, bollards and monument signs from GT's asset inventories."""
    parts = {
        "bench": Part("Benches", "furniture_wood"),
        "metal": Part("Furniture_Metal", "furniture_metal"),
        "stone": Part("Monument_Signs", "foundation"),
    }
    rng = np.random.default_rng(8675309)
    counts = {}

    benches = load("benches") + load("benches_potential")
    for (x, y), _ in points_of(benches):
        z = float(surface(x, y))
        # Benches face the path they sit beside. Spinning them randomly is the single
        # most obvious tell that furniture was scattered rather than placed.
        near = nearest_edge(walks, x, y)
        a = near[0] if near and near[1] < 12.0 else float(rng.uniform(0, math.pi))
        for args in (
            (x, y, z + 0.44, 1.75, 0.52, 0.07, a),                                    # seat
            (x - math.sin(a) * 0.22, y + math.cos(a) * 0.22, z + 0.72, 1.75, 0.06, 0.48, a),  # back
            (x + math.cos(a) * 0.72, y + math.sin(a) * 0.72, z + 0.22, 0.09, 0.48, 0.44, a),  # legs
            (x - math.cos(a) * 0.72, y - math.sin(a) * 0.72, z + 0.22, 0.09, 0.48, 0.44, a),
        ):
            parts["bench"].add(*box(*args))
    counts["benches"] = len(benches)

    # GT's outdoor-lighting inventory mixes freestanding poles with fixtures bolted to
    # walls. Modelling a wallpack as a 6 m pole in the middle of a lawn is what made the
    # first pass look like a forest of lampposts, so the type code drives the shape.
    LIGHT_STYLES = {                    # LIGHTTYPE -> (default height m, head, radius)
        "Halophane":  (4.3, "lantern", 0.085),
        "Decora":     (4.3, "lantern", 0.085),
        "EuroTech":   (4.6, "lantern", 0.085),
        "Pencil":     (4.0, "lantern", 0.070),
        "Shoebox":    (8.2, "box", 0.125),
        "Cobra Head": (9.1, "arm", 0.135),
        "Floodlight": (4.0, "box", 0.100),
        "LED":        (5.0, "box", 0.100),
        "Other":      (5.0, "box", 0.100),
        "Unknown":    (5.0, "box", 0.100),
    }
    poles = 0
    wall_packs = 0
    for (x, y), props in points_of(load("lights")):
        kind = str(props.get("LIGHTTYPE") or "Unknown")
        if kind == "Wallpack":          # bolted to a building, not a pole
            wall_packs += 1
            continue
        z = float(surface(x, y))
        if kind == "Bollard":
            parts["metal"].add(*prism(x, y, z - 0.05, z + 0.95, 0.10, 8))
            poles += 1
            continue
        default_h, head, radius = LIGHT_STYLES.get(kind, (5.0, "box", 0.10))
        d = props.get("DISTTOTOP")
        h = float(d) * FT if isinstance(d, (int, float)) and 6.0 < float(d) < 60.0 else default_h
        # Mast arms reach out over the carriageway; post-tops just need a sane bearing.
        near = nearest_edge(roads if head == "arm" else walks, x, y)
        a = (near[0] + math.pi / 2) if near and near[1] < 25.0 else float(rng.uniform(0, 2 * math.pi))
        parts["metal"].add(*prism(x, y, z - 0.1, z + h, radius, 8, a, top_radius=radius * 0.7))
        if head == "lantern":           # acorn/post-top fitting, not a spike
            parts["metal"].add(*prism(x, y, z + h, z + h + 0.30, 0.20, 8, a, top_radius=0.17))
            parts["metal"].add(*prism(x, y, z + h + 0.30, z + h + 0.40, 0.17, 8, a, top_radius=0.05))
        elif head == "arm":             # mast arm reaching over the carriageway
            arm = 1.55
            parts["metal"].add(*box(x + math.cos(a) * arm / 2, y + math.sin(a) * arm / 2,
                                    z + h + 0.10, arm, 0.09, 0.09, a))
            parts["metal"].add(*box(x + math.cos(a) * arm, y + math.sin(a) * arm,
                                    z + h + 0.02, 0.72, 0.34, 0.16, a))
        else:                           # flat shoebox luminaire
            parts["metal"].add(*box(x + math.cos(a) * 0.30, y + math.sin(a) * 0.30,
                                    z + h + 0.09, 0.58, 0.30, 0.18, a))
        poles += 1
    counts["light poles"] = poles
    counts["wall packs skipped"] = wall_packs

    bollards = list(points_of(load("bollards")))
    for (x, y), _ in bollards:
        z = float(surface(x, y))
        parts["metal"].add(*prism(x, y, z - 0.05, z + 0.92, 0.11, 6))
    counts["bollards"] = len(bollards)

    signs = list(points_of(load("monument_signs")))
    for (x, y), _ in signs:
        z = float(surface(x, y))
        near = nearest_edge(roads, x, y)
        a = near[0] if near and near[1] < 30.0 else float(rng.uniform(0, math.pi))
        parts["stone"].add(*box(x, y, z + 0.30, 2.60, 0.62, 0.60, a))
        parts["stone"].add(*box(x, y, z + 1.05, 2.20, 0.40, 0.92, a))
    counts["monument signs"] = len(signs)

    return list(parts.values()), counts


# Waste containers. GT's inventory records what each one physically is, so a 6 m roll-off
# behind a loading dock is not modelled as the same object as a footpath litter bin.
BIN_STYLES = {
    "satellitestation": ("pair", 0.60, 0.98),
    "futurestation": ("pair", 0.60, 0.98),
    "victorstanley": ("drum", 0.28, 0.92),
    "other": ("drum", 0.26, 0.88),
    "dumpster": ("box", 1.85, 1.25, 1.30),
    "compactor": ("box", 5.60, 2.20, 2.30),
    "rolloff": ("box", 6.10, 2.35, 1.90),
}


def build_site_objects(surface, walks=None):
    """Litter and recycling bins, blue-light phones, and planted shrub beds.

    All three are surveyed point layers. They are the things you notice are missing at
    eye level - GT publishes no tables or loose seating at all, so those stay absent
    rather than being invented.
    """
    metal = Part("Site_Metal", "furniture_metal")
    blue = Part("Call_Box_Lights", "callbox_blue")
    green = Part("Shrubs", "shrub")
    rng = np.random.default_rng(1885)
    counts = {}

    bins = 0
    for (x, y), props in points_of(load("bins")):
        if str(props.get("InOut")) != "Outdoor":
            continue
        style = BIN_STYLES.get(str(props.get("BinType") or "other").lower())
        if style is None:
            continue
        z = float(surface(x, y))
        near = nearest_edge(walks, x, y)
        a = near[0] if near and near[1] < 15.0 else float(rng.uniform(0, math.pi))
        if style[0] == "drum":
            _, radius, height = style
            metal.add(*prism(x, y, z, z + height, radius, 10, a))
            metal.add(*prism(x, y, z + height, z + height + 0.06, radius * 1.12, 10, a,
                             top_radius=radius * 0.95))
        elif style[0] == "pair":
            _, size, height = style
            for side in (-1, 1):
                metal.add(*box(x - math.sin(a) * side * size * 0.55,
                               y + math.cos(a) * side * size * 0.55,
                               z + height / 2, size, size, height, a))
        else:
            _, sx, sy, sz = style
            metal.add(*box(x, y, z + sz / 2, sx, sy, sz, a))
            metal.add(*box(x, y, z + sz + 0.05, sx * 0.98, sy * 0.98, 0.10, a))
        bins += 1
    counts["waste bins"] = bins

    boxes = 0
    for (x, y), _ in points_of(load("call_boxes")):
        z = float(surface(x, y))
        metal.add(*prism(x, y, z - 0.05, z + 2.35, 0.075, 8))
        metal.add(*box(x, y, z + 1.45, 0.26, 0.20, 0.52, 0.0))
        # The strobe on top is the whole point of a blue-light phone.
        blue.add(*prism(x, y, z + 2.35, z + 2.62, 0.115, 8))
        boxes += 1
    counts["blue-light phones"] = boxes

    shrubs = 0
    # Rings of a squashed hemisphere rather than a single taper. A one-piece cone reads
    # as a traffic cone at any distance, which is exactly what 402 of them looked like.
    RING_T = (0.0, 0.30, 0.62, 0.85, 1.0)
    RING_R = (0.72, 1.00, 0.92, 0.62, 0.10)
    for (x, y), props in points_of(load("shrubs")):
        if str(props.get("plantType")) not in ("shrub", "other"):
            continue
        size = props.get("plantedSize")
        spread = float(size) * FT * 0.5 if isinstance(size, (int, float)) and size else 0.75
        spread = min(max(spread, 0.45), 1.6) * float(rng.uniform(0.82, 1.18))
        height = spread * float(rng.uniform(1.05, 1.55))
        z = float(surface(x, y))
        angle = float(rng.uniform(0, math.pi))
        for i in range(len(RING_T) - 1):
            green.add(*prism(x, y, z + height * RING_T[i], z + height * RING_T[i + 1],
                             spread * RING_R[i], 8, angle,
                             top_radius=spread * RING_R[i + 1]))
        shrubs += 1
    counts["shrubs"] = shrubs

    return [metal, blue, green], counts


def build_markings(terrain, roads_index, inside_building=None,
                   road_offset=0.06, stall_offset=0.09):
    """Painted road centrelines and parking bay lines.

    Paint is most of what makes tarmac read as tarmac, and both are surveyed: GT publishes
    street centrelines and one point per parking space. The spaces carry no bearing, so
    each bay takes its orientation from the line of its nearest neighbour, which is the
    row it belongs to.

    Paint drapes on bare terrain, never on the deck lookup the furniture uses: a street
    centreline that crosses a building footprint is a road passing under it, and lifting
    it to the roof drew a yellow line through the sky.
    """
    part_w = Part("Markings_White", "marking")
    part_y = Part("Markings_Yellow", "marking_yellow")
    counts = {}
    surface = terrain.height

    def stripe(part, ax, ay, bx, by, width, lift):
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        if length < 1e-6:
            return
        nx, ny = -dy / length * width / 2, dx / length * width / 2
        quad = np.array([[ax + nx, ay + ny], [bx + nx, by + ny],
                         [bx - nx, by - ny], [ax - nx, ay - ny]])
        z = surface(quad[:, 0], quad[:, 1]) + lift
        part.add(np.column_stack([quad, z]), np.array([[0, 1, 2], [0, 2, 3]]))

    # ---- street centrelines
    painted = 0
    for feature in load("street_lines"):
        geom = feature.get("geometry") or {}
        kind = geom.get("type")
        lines = ([geom.get("coordinates")] if kind == "LineString"
                 else geom.get("coordinates") if kind == "MultiLineString" else [])
        for line in lines or ():
            pts = np.array([project(c[0], c[1]) for c in line])
            if len(pts) < 2:
                continue
            for i in range(len(pts) - 1):
                a, b = pts[i], pts[i + 1]
                mid = (a + b) / 2
                if not (EXTENT[0] < mid[0] < EXTENT[1] and EXTENT[2] < mid[1] < EXTENT[3]):
                    continue
                # A centreline inside a carriageway is within half a road width of its
                # kerb. Streets GT maps but does not pave as polygons fail this and stay
                # unpainted, which is what keeps the paint on actual asphalt.
                near = nearest_edge(roads_index, mid[0], mid[1])
                if near is None or near[1] > 9.0:
                    continue
                if inside_building is not None and bool(inside_building(mid[0], mid[1])):
                    continue
                stripe(part_y, a[0], a[1], b[0], b[1], 0.16, road_offset + 0.004)
                painted += 1
    counts["centreline segments"] = painted

    # ---- parking bays
    spaces = [(p, props) for p, props in points_of(load("parking_spaces"))
              if str(props.get("DeckSpace")) != "Yes"]
    if spaces:
        pts = np.array([p for p, _ in spaces])
        cell = 6.0
        buckets = {}
        for i, key in enumerate(map(tuple, np.floor(pts / cell).astype(np.int64).tolist())):
            buckets.setdefault(key, []).append(i)
        drawn = 0
        for i, (x, y) in enumerate(pts.tolist()):
            gi, gj = int(math.floor(x / cell)), int(math.floor(y / cell))
            cand = []
            for di in (-1, 0, 1):
                for dj in (-1, 0, 1):
                    cand.extend(buckets.get((gi + di, gj + dj), ()))
            cand = [c for c in cand if c != i]
            if not cand:
                continue
            off = pts[cand] - (x, y)
            d = np.hypot(off[:, 0], off[:, 1])
            k = int(np.argmin(d))
            if not 1.8 < d[k] < 4.5:
                continue
            ux, uy = off[k] / d[k]                       # along the row
            hx, hy = -uy * 2.6, ux * 2.6                 # across the bay
            cx, cy = x + ux * d[k] / 2, y + uy * d[k] / 2
            stripe(part_w, cx - hx, cy - hy, cx + hx, cy + hy, 0.10, stall_offset + 0.004)
            drawn += 1
        counts["bay lines"] = drawn

    return [part_w, part_y], counts


def lines_of(geometry):
    """Yield each LineString of a geometry as a projected (n, 2) array of metres."""
    if not geometry:
        return
    kind = geometry.get("type")
    coords = geometry.get("coordinates") or []
    runs = []
    if kind == "LineString":
        runs = [coords]
    elif kind == "MultiLineString":
        runs = coords
    elif kind == "Polygon":
        runs = coords
    elif kind == "MultiPolygon":
        runs = [ring for poly in coords for ring in poly]
    for run in runs:
        if len(run) < 2:
            continue
        arr = np.asarray(run, dtype=np.float64)[:, :2]
        yield np.column_stack([(arr[:, 0] - LON0) * M_PER_DEG_LON,
                               (arr[:, 1] - LAT0) * M_PER_DEG_LAT])


def build_osm_steps(terrain, features, riser=0.165, default_width=1.9, max_width=4.5):
    """Real flights of steps from surveyed OSM `highway=steps` centrelines.

    Everything the model had before this was inferred: no GT service publishes stairs,
    so `build_stairs` guessed a flight wherever the bare-earth DEM happened to be steep.
    That both invented staircases on grassy banks and missed every flight the DEM had
    smoothed into a ramp. These are surveyed centrelines - 572 of them - so the geometry
    is placed where stairs actually are.

    The line gives the run and the terrain gives the rise; the step count comes from the
    `step_count` tag where a surveyor recorded it and from rise/riser where they did not.
    Treads are laid perpendicular to the local direction of the line, so a flight that
    doglegs follows the dogleg instead of shearing.
    """
    part = Part("Steps", "stairs")
    centres = []
    built = 0
    total_steps = 0

    for feature in features:
        props = feature.get("properties") or {}
        try:
            width = float(str(props.get("width", "")).split()[0])
        except (ValueError, IndexError):
            width = default_width
        width = float(np.clip(width, 0.9, max_width))

        for line in lines_of(feature.get("geometry")):
            # Resample to a fixed number of stations so treads are evenly spaced along
            # the run rather than bunching wherever the mapper clicked.
            seg = np.diff(line, axis=0)
            seglen = np.hypot(seg[:, 0], seg[:, 1])
            run = float(seglen.sum())
            if run < 1.2 or run > 120.0:
                continue
            z0 = float(drape(terrain, line[0, 0], line[0, 1]))
            z1 = float(drape(terrain, line[-1, 0], line[-1, 1]))
            rise = abs(z1 - z0)

            count = 0
            try:
                count = int(float(props.get("step_count", 0)))
            except (TypeError, ValueError):
                count = 0
            if count <= 0:
                count = int(round(rise / riser))
            # A tagged flight with no measurable rise is usually a short stair the DEM
            # has flattened. Give it the shallowest believable flight rather than none.
            count = int(np.clip(count, 2, 80))
            if rise < 0.25:
                rise = count * riser * 0.75

            station = np.concatenate([[0.0], np.cumsum(seglen)])
            t = np.linspace(0.0, run, count + 1)
            px = np.interp(t, station, line[:, 0])
            py = np.interp(t, station, line[:, 1])
            centres.append(np.column_stack([px, py]))

            # Direction at each station, and the perpendicular the tread runs along.
            dx = np.gradient(px)
            dy = np.gradient(py)
            norm = np.maximum(np.hypot(dx, dy), 1e-6)
            nx, ny = -dy / norm, dx / norm

            lo, hi = (z0, z1) if z0 <= z1 else (z1, z0)
            if z0 > z1:
                px, py, nx, ny = px[::-1], py[::-1], nx[::-1], ny[::-1]
            half = width * 0.5
            # Landing height of each tread, plus a 12 cm slab so the flight reads as
            # solid from the side instead of as floating plates.
            top = lo + np.arange(count + 1) * (hi - lo) / max(count, 1)
            for i in range(count):
                ax, ay = px[i], py[i]
                bx, by = px[i + 1], py[i + 1]
                z = float(top[i + 1])
                quad = np.array([
                    [ax + nx[i] * half, ay + ny[i] * half],
                    [ax - nx[i] * half, ay - ny[i] * half],
                    [bx - nx[i + 1] * half, by - ny[i + 1] * half],
                    [bx + nx[i + 1] * half, by + ny[i + 1] * half],
                ])
                base = float(top[i]) - 0.12
                verts = np.vstack([
                    np.column_stack([quad, np.full(4, base)]),
                    np.column_stack([quad, np.full(4, z)]),
                ])
                part.add(verts, np.array([[4, 6, 5], [4, 7, 6],
                                          [0, 1, 5], [0, 5, 4],
                                          [1, 2, 6], [1, 6, 5],
                                          [2, 3, 7], [2, 7, 6],
                                          [3, 0, 4], [3, 4, 7]]))
            built += 1
            total_steps += count

    index = np.vstack(centres) if centres else np.zeros((0, 2))
    return part, built, total_steps, index


def near_points_test(points, radius, cell=3.0):
    """Callable (x, y) -> bool array: is the query near any of `points`?

    Cell membership rather than true distance. This is called once per sub-triangle of
    every walkway polygon - millions of queries - so an exact test written as a Python
    loop over candidates costs more than the rest of the build put together. Marking the
    occupied cells and their eight neighbours gives a suppression zone between one and
    three cells wide, which is the right order for "a surveyed staircase is already here".
    """
    if len(points) == 0:
        return lambda x, y: np.zeros(np.shape(x), dtype=bool)
    grow = max(1, int(math.ceil(radius / cell)))
    keys = np.floor(np.asarray(points, dtype=np.float64) / cell).astype(np.int64)
    offs = np.arange(-grow, grow + 1)
    di, dj = np.meshgrid(offs, offs, indexing="ij")
    di = di.ravel()
    dj = dj.ravel()
    occupied = np.unique(((keys[:, None, 0] + di) * 73856093
                          ^ (keys[:, None, 1] + dj) * 19349663).ravel())

    def test(x, y):
        x = np.atleast_1d(np.asarray(x, dtype=np.float64))
        y = np.atleast_1d(np.asarray(y, dtype=np.float64))
        h = (np.floor(x / cell).astype(np.int64) * 73856093
             ^ np.floor(y / cell).astype(np.int64) * 19349663)
        return np.isin(h, occupied)

    return test


# Named works where the OSM tags plus a photograph justify a specific form. OSM gives
# position, type and material but never shape, so anything not listed here gets the
# generic form for its artwork_type rather than an invented one.
LANDMARK_FORMS = {
    "Koan Statue": ("blob", 3.6, 2.4, "art_painted"),
    "Kessler Campanile": ("spire", 24.0, 2.2, "art_stone"),
}


def build_landmarks(surface, features):
    """Public art: statues, sculptures and installations from OSM.

    Murals and graffiti are deliberately skipped - they are paint on a wall, and without
    the actual artwork image there is nothing to model that would not be a lie. What is
    left is the freestanding work, which is what you notice walking past.
    """
    parts = {
        "art_bronze": Part("Art_Bronze", "art_bronze"),
        "art_stone": Part("Art_Stone", "art_stone"),
        "art_painted": Part("Art_Painted", "art_painted"),
    }
    rng = np.random.default_rng(97531)
    counts = {}
    named = []

    MATERIAL_HINT = {
        "bronze": "art_bronze", "brass": "art_bronze", "copper": "art_bronze",
        "steel": "art_painted", "metal": "art_painted", "fiberglass": "art_painted",
        "plastic": "art_painted", "aluminium": "art_painted",
        "stone": "art_stone", "marble": "art_stone", "granite": "art_stone",
        "concrete": "art_stone", "limestone": "art_stone",
    }
    TYPE_DEFAULT = {
        "statue": "art_bronze", "sculpture": "art_stone",
        "installation": "art_painted", "architecture": "art_stone",
    }

    for (x, y), props in points_of(features):
        if not (EXTENT[0] < x < EXTENT[1] and EXTENT[2] < y < EXTENT[3]):
            continue
        kind = (props.get("artwork_type") or "").lower()
        if kind in ("mural", "graffiti", "painting", "notice", "plaque"):
            continue
        name = props.get("name") or ""
        material = MATERIAL_HINT.get((props.get("material") or "").lower())
        key = material or TYPE_DEFAULT.get(kind, "art_stone")

        form, height, span = None, None, None
        if name in LANDMARK_FORMS:
            form, height, span, key = LANDMARK_FORMS[name]
        try:
            height = float(str(props.get("height", "")).split()[0])
        except (ValueError, IndexError):
            pass

        part = parts[key]
        z = float(surface(x, y))
        angle = float(rng.uniform(0, math.pi))

        if form == "spire":
            part.add(*prism(x, y, z, z + height, span * 0.5, 8, angle,
                            top_radius=span * 0.18))
            part.add(*box(x, y, z + 0.35, span * 1.7, span * 1.7, 0.70, angle))
        elif form == "blob":
            # A smooth closed abstract form: stacked tapering rings. Without a scan of
            # the real piece this is a stand-in with the right footprint and height.
            rings = 5
            for i in range(rings):
                f0, f1 = i / rings, (i + 1) / rings
                r0 = span * 0.5 * math.sin(math.pi * (0.18 + 0.82 * f0))
                r1 = span * 0.5 * math.sin(math.pi * (0.18 + 0.82 * f1))
                part.add(*prism(x, y, z + height * f0, z + height * f1,
                                max(r0, 0.12), 12, angle, top_radius=max(r1, 0.12)))
            part.add(*prism(x, y, z, z + 0.25, span * 0.62, 12, angle))
        elif kind == "statue":
            h = height or 2.9
            ped = h * 0.38
            part.add(*box(x, y, z + ped * 0.5, 1.25, 1.25, ped, angle))
            part.add(*box(x, y, z + ped + 0.06, 1.45, 1.45, 0.12, angle))
            # Torso and head: enough mass to read as a figure at walking distance.
            part.add(*prism(x, y, z + ped + 0.12, z + h - 0.34, 0.30, 8, angle,
                            top_radius=0.22))
            part.add(*prism(x, y, z + h - 0.34, z + h, 0.16, 8, angle, top_radius=0.13))
        elif kind == "installation":
            h = height or 4.2
            for k in range(3):
                a = angle + k * 2.094
                ox, oy = math.cos(a) * 0.9, math.sin(a) * 0.9
                part.add(*prism(x + ox, y + oy, z, z + h * (0.6 + 0.2 * k), 0.09, 6, a))
            part.add(*box(x, y, z + h * 0.92, 2.6, 0.22, 0.22, angle))
        else:
            h = height or 3.1
            # Abstract sculpture: three offset slabs on a plinth.
            part.add(*box(x, y, z + 0.30, 2.2, 2.2, 0.60, angle))
            for k in range(3):
                a = angle + k * 0.7
                part.add(*box(x + math.cos(a) * 0.35, y + math.sin(a) * 0.35,
                              z + 0.60 + h * (0.18 + 0.28 * k),
                              1.5 - 0.35 * k, 0.35, h * 0.45, a))

        counts[kind or "artwork"] = counts.get(kind or "artwork", 0) + 1
        if name:
            named.append(name)

    return [p for p in parts.values() if p.tris], counts, named


def build_tables(surface, features):
    """Picnic tables and cafe seating from OSM.

    GT's GIS publishes fixed site furniture only - benches, bins, bollards, signs - so
    every table on campus was missing. These are surveyed positions; the forms are the
    two standard types, a timber picnic table with attached benches and a round cafe
    table with loose chairs.
    """
    wood = Part("Tables_Wood", "furniture_wood")
    metal = Part("Tables_Metal", "furniture_metal")
    rng = np.random.default_rng(60613)
    counts = {}

    for (x, y), props in points_of(features):
        if not (EXTENT[0] < x < EXTENT[1] and EXTENT[2] < y < EXTENT[3]):
            continue
        z = float(surface(x, y))
        angle = float(rng.uniform(0, math.pi))
        kind = props.get("leisure")

        if kind == "picnic_table":
            wood.add(*box(x, y, z + 0.74, 1.80, 0.78, 0.06, angle))
            for side in (-1, 1):
                ox = math.cos(angle + math.pi / 2) * 0.62 * side
                oy = math.sin(angle + math.pi / 2) * 0.62 * side
                wood.add(*box(x + ox, y + oy, z + 0.45, 1.80, 0.28, 0.05, angle))
            for sx in (-0.72, 0.72):
                ox = math.cos(angle) * sx
                oy = math.sin(angle) * sx
                metal.add(*box(x + ox, y + oy, z + 0.37, 0.08, 1.40, 0.74, angle))
            counts["picnic tables"] = counts.get("picnic tables", 0) + 1
        else:
            # Cafe seating: a round table and two or three chairs pulled up to it.
            metal.add(*prism(x, y, z, z + 0.72, 0.05, 6, angle))
            metal.add(*prism(x, y, z + 0.72, z + 0.76, 0.38, 12, angle))
            metal.add(*prism(x, y, z, z + 0.04, 0.22, 8, angle))
            for k in range(int(rng.integers(2, 4))):
                a = angle + k * 2.1 + float(rng.uniform(-0.3, 0.3))
                cx, cy = x + math.cos(a) * 0.72, y + math.sin(a) * 0.72
                metal.add(*box(cx, cy, z + 0.44, 0.42, 0.42, 0.04, a))
                metal.add(*box(cx - math.cos(a) * 0.19, cy - math.sin(a) * 0.19,
                               z + 0.66, 0.06, 0.42, 0.44, a))
                for dx, dy in ((-0.17, -0.17), (0.17, -0.17), (0.17, 0.17), (-0.17, 0.17)):
                    lx = cx + dx * math.cos(a) - dy * math.sin(a)
                    ly = cy + dx * math.sin(a) + dy * math.cos(a)
                    metal.add(*prism(lx, ly, z, z + 0.44, 0.015, 4, a))
            counts["cafe sets"] = counts.get("cafe sets", 0) + 1

    return [p for p in (wood, metal) if p.tris], counts


def build_osm_amenity(surface, features, walks=None):
    """Benches, bike racks, fountains, litter bins and drinking fountains from OSM.

    These overlap GT's own layers in places - GT publishes benches too - but OSM covers
    the parts of the extent that are not GT property, where the GT layers simply stop.
    """
    metal = Part("Amenity_Metal", "furniture_metal")
    wood = Part("Amenity_Wood", "furniture_wood")
    stone = Part("Amenity_Stone", "monument")
    water = Part("Water", "water")
    rng = np.random.default_rng(31337)
    counts = {}

    for (x, y), props in points_of(features):
        if not (EXTENT[0] < x < EXTENT[1] and EXTENT[2] < y < EXTENT[3]):
            continue
        z = float(surface(x, y))
        kind = props.get("amenity") or props.get("man_made")
        near = nearest_edge(walks, x, y) if walks is not None else None
        angle = near[0] if near and near[1] < 12.0 else float(rng.uniform(0, math.pi))

        if kind == "bench":
            wood.add(*box(x, y, z + 0.44, 1.70, 0.46, 0.06, angle))
            wood.add(*box(x - math.sin(angle) * 0.21, y + math.cos(angle) * 0.21,
                          z + 0.70, 1.70, 0.06, 0.44, angle))
            for sx in (-0.72, 0.72):
                metal.add(*box(x + math.cos(angle) * sx, y + math.sin(angle) * sx,
                               z + 0.22, 0.07, 0.42, 0.44, angle))
            counts["benches"] = counts.get("benches", 0) + 1
        elif kind == "bicycle_parking":
            try:
                capacity = int(float(props.get("capacity", 8)))
            except (TypeError, ValueError):
                capacity = 8
            hoops = int(np.clip(capacity // 2, 2, 10))
            for k in range(hoops):
                off = (k - (hoops - 1) / 2.0) * 0.76
                hx = x + math.cos(angle) * off
                hy = y + math.sin(angle) * off
                # Inverted-U hoop: two legs and a crossbar.
                for side in (-0.36, 0.36):
                    lx = hx - math.sin(angle) * side
                    ly = hy + math.cos(angle) * side
                    metal.add(*prism(lx, ly, z, z + 0.78, 0.022, 6, angle))
                metal.add(*box(hx, hy, z + 0.79, 0.044, 0.76, 0.044, angle))
            counts["bike racks"] = counts.get("bike racks", 0) + 1
        elif kind == "fountain":
            stone.add(*prism(x, y, z, z + 0.55, 2.1, 16, angle))
            water.add(*prism(x, y, z + 0.40, z + 0.46, 1.95, 16, angle))
            stone.add(*prism(x, y, z + 0.46, z + 1.35, 0.30, 12, angle,
                             top_radius=0.16))
            counts["fountains"] = counts.get("fountains", 0) + 1
        elif kind == "drinking_water":
            metal.add(*box(x, y, z + 0.52, 0.34, 0.28, 1.04, angle))
            metal.add(*box(x, y, z + 1.06, 0.40, 0.34, 0.08, angle))
            counts["drinking fountains"] = counts.get("drinking fountains", 0) + 1
        elif kind == "waste_basket":
            metal.add(*prism(x, y, z, z + 0.86, 0.26, 10, angle))
            metal.add(*prism(x, y, z + 0.86, z + 0.92, 0.29, 10, angle,
                             top_radius=0.24))
            counts["litter bins"] = counts.get("litter bins", 0) + 1
        elif kind == "bicycle_repair_station":
            metal.add(*prism(x, y, z, z + 1.55, 0.06, 6, angle))
            metal.add(*box(x, y, z + 1.30, 0.50, 0.10, 0.30, angle))
            counts["repair stands"] = counts.get("repair stands", 0) + 1
        elif kind == "shelter":
            for dx, dy in ((-1.9, -1.3), (1.9, -1.3), (1.9, 1.3), (-1.9, 1.3)):
                lx = x + dx * math.cos(angle) - dy * math.sin(angle)
                ly = y + dx * math.sin(angle) + dy * math.cos(angle)
                metal.add(*prism(lx, ly, z, z + 2.45, 0.055, 6, angle))
            metal.add(*box(x, y, z + 2.52, 4.3, 3.0, 0.14, angle))
            counts["shelters"] = counts.get("shelters", 0) + 1

    return [p for p in (metal, wood, stone, water) if p.tris], counts


def occupancy_test(verts, cell=14.0):
    """Return a predicate: is there real 3D building geometry near this point?"""
    if verts is None or len(verts) == 0:
        return None
    keys = set(map(tuple, np.floor(verts[:, :2] / cell).astype(np.int64).tolist()))

    def occupied(x, y):
        i, j = int(math.floor(x / cell)), int(math.floor(y / cell))
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                if (i + di, j + dj) in keys:
                    return True
        return False

    return occupied


def build_foundations(terrain, features, verts, cell=8.0):
    """Plug the gap under buildings whose modelled base sits above the terrain.

    The I3S model and the 3DEP DEM come from different survey bases, so a minority of
    buildings end up hovering. A short skirt from the building's lowest vertex down past
    the terrain fixes that; buildings already buried in the hillside get nothing.
    """
    part = Part("Building_Foundations", "foundation")
    if verts is None or len(verts) == 0:
        return part, 0

    keys = np.floor(verts[:, :2] / cell).astype(np.int64)
    order = np.lexsort((keys[:, 1], keys[:, 0]))
    keys, zs = keys[order], verts[order, 2]
    starts = np.flatnonzero(np.r_[True, np.any(keys[1:] != keys[:-1], axis=1)])
    cell_min = dict(zip(map(tuple, keys[starts].tolist()),
                        np.minimum.reduceat(zs, starts).tolist()))

    built = 0
    for feature in features:
        for polygon in polygons_of(feature.get("geometry")):
            flat, ends = prepare(polygon)
            if flat is None:
                continue
            lo = np.floor(flat.min(axis=0) / cell).astype(int)
            hi = np.floor(flat.max(axis=0) / cell).astype(int)
            found = [cell_min[(i, j)]
                     for i in range(lo[0], hi[0] + 1)
                     for j in range(lo[1], hi[1] + 1)
                     if (i, j) in cell_min]
            if not found:
                continue
            top = min(found) + 0.5
            base = float(terrain.height(flat[:, 0], flat[:, 1]).min()) - 1.5
            # A real gap is a metre or two. Anything taller means the footprint only
            # caught a neighbouring tower's upper floors - skip rather than build a column.
            if not 0.0 < top - base <= 8.0:
                continue
            n = len(flat)
            verts_out = np.vstack([np.column_stack([flat, np.full(n, base)]),
                                   np.column_stack([flat, np.full(n, top)])])
            faces, start = [], 0
            for end in ends:
                ring = np.arange(start, int(end))
                nxt = np.roll(ring, -1)
                faces.append(np.column_stack([ring, nxt, nxt + n]))
                faces.append(np.column_stack([ring, nxt + n, ring + n]))
                start = int(end)
            part.add(verts_out, np.concatenate(faces))
            built += 1
    return part, built


# ---------------------------------------------------------------------------- output

def _write_rows(fh, arr, fmt, chunk=100000):
    """Bulk-format numeric rows. numpy.savetxt spends ~25 s on a scene this size."""
    arr = np.ascontiguousarray(arr)
    for start in range(0, len(arr), chunk):
        block = arr[start:start + chunk]
        fh.write((fmt * len(block)) % tuple(block.ravel().tolist()))


def write_obj(parts):
    with open(MTL_PATH, "w", encoding="utf-8") as fh:
        for name, (r, g, b) in MATERIALS.items():
            fh.write(f"newmtl {name}\nKd {r:.3f} {g:.3f} {b:.3f}\n"
                     f"Ka {r*0.2:.3f} {g*0.2:.3f} {b*0.2:.3f}\nKs 0.05 0.05 0.05\nNs 8\nillum 2\n\n")

    offset = 0
    uv_offset = 0
    with open(OBJ_PATH, "w", encoding="utf-8") as fh:
        fh.write("# Georgia Tech campus\n")
        fh.write("# terrain: USGS 3DEP | buildings: GT NBBJ I3S scene layer | vectors: GT Facilities GIS\n")
        fh.write(f"mtllib {os.path.basename(MTL_PATH)}\n")
        for part in parts:
            verts, tris, uv = part.finish()
            if len(tris) == 0:
                continue
            # local ENU (east, north, up) -> OBJ Y-up (east, up, south)
            out = np.column_stack([verts[:, 0], verts[:, 2], -verts[:, 1]])
            fh.write(f"o {part.name}\nusemtl {part.material}\n")
            _write_rows(fh, out, "v %.3f %.3f %.3f\n")
            if uv is not None:
                _write_rows(fh, uv, "vt %.4f %.4f\n")
                # vt indices are numbered independently of v indices in OBJ.
                vi = tris + offset + 1
                ti = tris + uv_offset + 1
                _write_rows(fh, np.column_stack([vi[:, 0], ti[:, 0], vi[:, 1], ti[:, 1],
                                                 vi[:, 2], ti[:, 2]]),
                            "f %d/%d %d/%d %d/%d\n")
                uv_offset += len(uv)
            else:
                _write_rows(fh, tris + offset + 1, "f %d %d %d\n")
            offset += len(verts)
            print(f"  {part.name:22s} {len(verts):9,d} verts  {len(tris):9,d} tris")
    return offset


def main():
    terrain = Terrain(os.path.join(HERE, "terrain.npz"), EXTENT, TERRAIN_STEP)
    print(f"terrain grid {terrain.nx} x {terrain.ny} @ {TERRAIN_STEP} m "
          f"(relief {terrain.z.min():.1f} .. {terrain.z.max():.1f} m rel. {Z_REF} m ASL)")

    parts = []
    ortho = load_ortho_meta()
    tv, tt = terrain.mesh()
    surface = Part("Terrain", "terrain", textured=ortho is not None)
    surface.add(tv, tt, uv=ortho_uv(tv[:, 0], tv[:, 1], ortho) if ortho else None)
    parts.append(surface)

    gt_footprints = load("buildings_gt")
    # The I3S scene layer reaches well past campus into Midtown, so every footprint layer
    # has to be in the lookup - otherwise a Peachtree Street tower gets classed as a
    # monument and loses its windows.
    all_footprints = (gt_footprints + load("buildings_noncampus")
                      + load("buildings_offcampus"))
    i3s, monuments, raw3d, roofs = build_i3s(footprint_floor_lookup(all_footprints), ortho)
    occupied = occupancy_test(raw3d)

    sidewalks = load("sidewalks")

    # Surveyed staircases first, so the slope heuristic can stand down wherever a real
    # flight has been mapped.
    osm_steps, flights_real, step_count, step_pts = build_osm_steps(terrain,
                                                                   load("osm_steps"))
    print(f"surveyed stairs: {flights_real} OSM flights, {step_count:,} treads")
    parts.append(osm_steps)

    stairs, flights, steep = build_stairs(terrain, sidewalks, building_mask(raw3d),
                                          near_points_test(step_pts, 5.0))

    layers, paved = partition_ground(GROUND_LAYERS, exclude={"sidewalks": steep})
    ground, skirted = build_ground_partition(terrain, layers, ortho=ortho)
    parts.extend(ground)
    print(f"ground partition: {len(layers)} layers, {paved.area / 10000.0:.1f} ha paved, "
          f"{skirted:,} kerb edges (interior seams need none)")
    parts.append(stairs)
    if i3s is not None:
        parts.append(i3s)
        parts.append(monuments)
        parts.append(roofs)

    footprints = load("buildings_offcampus") + load("buildings_noncampus") + gt_footprints
    extruded, kept, skipped = build_extruded(terrain, footprints, "Buildings_Extruded",
                                             "facade_simple", occupied)
    print(f"extruded footprints: {kept} kept, {skipped} skipped (already in the 3D model)")
    parts.append(extruded)

    foundations, built = build_foundations(terrain, footprints, raw3d)
    print(f"foundation skirts: {built} (buildings whose modelled base sat above terrain)")
    parts.append(foundations)

    # Everything placed as a point rides this instead of bare terrain, so anything on a
    # podium or roof garden lands on the deck rather than inside the building.
    surface, raised = surface_lookup(terrain, raw3d, all_footprints)
    walk_index = edge_index(sidewalks)
    road_index = edge_index(load("roads"))

    furniture, counts = build_furniture(surface, walk_index, road_index)
    parts.extend(furniture)
    print("site furniture: " + ", ".join(f"{v} {k}" for k, v in counts.items()))

    site, counts = build_site_objects(surface, walk_index)
    parts.extend(site)
    print("site objects: " + ", ".join(f"{v} {k}" for k, v in counts.items()))

    marks, counts = build_markings(terrain, road_index, footprint_index(all_footprints))
    parts.extend(marks)
    print("paint: " + ", ".join(f"{v} {k}" for k, v in counts.items()))

    art, counts, named = build_landmarks(surface, load("osm_artwork"))
    parts.extend(art)
    print("public art: " + ", ".join(f"{v} {k}" for k, v in counts.items()))
    print("  named: " + ", ".join(sorted(named)[:12])
          + (f" (+{len(named) - 12} more)" if len(named) > 12 else ""))

    tables, counts = build_tables(surface, load("osm_tables"))
    parts.extend(tables)
    print("seating: " + ", ".join(f"{v} {k}" for k, v in counts.items()))

    amenity, counts = build_osm_amenity(surface, load("osm_amenity"), walk_index)
    parts.extend(amenity)
    print("amenities: " + ", ".join(f"{v} {k}" for k, v in counts.items()))

    tree_features = load("trees")
    trunks, canopy, planted = build_trees(surface, tree_features)
    parts.extend([trunks, canopy])
    on_deck = int(np.count_nonzero(raised(
        *np.array([project(f["geometry"]["coordinates"][0], f["geometry"]["coordinates"][1])
                   for f in tree_features
                   if (f.get("geometry") or {}).get("type") == "Point"]).T)))
    print(f"trees planted: {planted} ({on_deck} standing on building decks, not terrain)")

    print("\nwriting objects:")
    total = write_obj(parts)
    print(f"\nvertices : {total:,}")
    print(f"written  : {OBJ_PATH}  ({os.path.getsize(OBJ_PATH) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
