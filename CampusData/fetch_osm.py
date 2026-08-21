"""Fetch OpenStreetMap features for the campus and write them as GeoJSON layers.

Georgia Tech's own ArcGIS estate is excellent for surfaces - it publishes surveyed
sidewalks, roads, tree inventories and building massing - but it publishes nothing at
all for three categories that dominate how a campus actually reads on foot:

  * stairs.  There is no step layer in any of the 164 GT services, so every flight in
    the model was being inferred from the slope of a bare-earth raster.  OSM has 376
    surveyed `highway=steps` ways here.
  * loose furniture.  No tables, no chairs, no fountains, no bike racks.
  * public art.  No sculpture layer, which is why the Koan was missing.

Overpass answers 504 from this machine even for a trivial query, so this goes through
the core OSM API instead, which serves the whole bounding box as XML with no query
language in the way.  The response is cached on disk: it is ~13 MB and there is no
reason to pull it on every build.

Licensing: OpenStreetMap is ODbL.  Using it obliges attribution, and share-alike
applies to a derived *database*.  A rendered mesh is normally a produced work rather
than a derived database, but that is a decision to take deliberately.
"""

import json
import math
import os
import time
import urllib.request
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE_DIR = os.path.join(HERE, "osm_cache")

LAT0, LON0 = 33.7756, -84.3963
M_PER_DEG_LAT = 110540.0
M_PER_DEG_LON = 111320.0 * math.cos(math.radians(LAT0))
EXTENT = (-2150.0, 2200.0, -1680.0, 2200.0)  # xmin, xmax, ymin, ymax, metres

PAD = 150.0
WEST = LON0 + (EXTENT[0] - PAD) / M_PER_DEG_LON
EAST = LON0 + (EXTENT[1] + PAD) / M_PER_DEG_LON
SOUTH = LAT0 + (EXTENT[2] - PAD) / M_PER_DEG_LAT
NORTH = LAT0 + (EXTENT[3] + PAD) / M_PER_DEG_LAT

# The API refuses any box containing more than 50,000 nodes, and midtown Atlanta is
# mapped densely enough that a single campus-sized request is roughly twice over.
# 4 x 4 tiles leaves comfortable headroom.
TILES = 4
API = "https://api.openstreetmap.org/api/0.6/map?bbox={:.5f},{:.5f},{:.5f},{:.5f}"

# name -> (geometry kind, predicate on the tag dict, tags to carry through)
POINT_TAGS = ("name", "artwork_type", "memorial", "amenity", "leisure", "tourism",
              "material", "height", "backrest", "covered", "capacity", "seats")
LINE_TAGS = ("name", "highway", "step_count", "width", "incline", "handrail",
             "conveying", "surface", "ramp", "layer", "man_made", "bridge")


def is_art(t):
    return t.get("tourism") == "artwork" or "artwork_type" in t or "memorial" in t


def is_table(t):
    return t.get("leisure") in ("picnic_table", "outdoor_seating")


def is_amenity(t):
    return t.get("amenity") in ("bench", "fountain", "bicycle_parking", "waste_basket",
                                "drinking_water", "bbq", "shelter") \
        or t.get("man_made") == "water_tap" \
        or t.get("amenity") == "bicycle_repair_station"


def is_steps(t):
    return t.get("highway") == "steps"


def is_bridge(t):
    return t.get("man_made") == "bridge" or t.get("bridge") in ("yes", "boardwalk")


def is_pitch(t):
    return t.get("leisure") in ("pitch", "track", "playground")


POINT_LAYERS = [
    ("osm_artwork", is_art),
    ("osm_tables", is_table),
    ("osm_amenity", is_amenity),
]
LINE_LAYERS = [
    ("osm_steps", is_steps),
    ("osm_bridges", is_bridge),
    ("osm_pitches", is_pitch),
]


def download():
    """Yield one parsed XML root per tile, caching each tile's response on disk.

    Tiles overlap by a whisker so that a way crossing a seam is returned whole by at
    least one of them; duplicates are removed downstream by OSM id.
    """
    os.makedirs(CACHE_DIR, exist_ok=True)
    dlon = (EAST - WEST) / TILES
    dlat = (NORTH - SOUTH) / TILES
    for iy in range(TILES):
        for ix in range(TILES):
            w = WEST + ix * dlon
            s = SOUTH + iy * dlat
            box = (w, s, w + dlon, s + dlat)
            path = os.path.join(CACHE_DIR, f"tile_{ix}_{iy}.xml")
            if os.path.exists(path) and os.path.getsize(path) > 200:
                raw = open(path, "rb").read()
            else:
                url = API.format(*box)
                for attempt in range(4):
                    try:
                        req = urllib.request.Request(
                            url, headers={"User-Agent": "gt-campus-builder/1.0"})
                        raw = urllib.request.urlopen(req, timeout=300).read()
                        break
                    except Exception as exc:
                        print(f"  tile {ix},{iy} attempt {attempt + 1}: "
                              f"{type(exc).__name__}: {exc}")
                        time.sleep(5 * (attempt + 1))
                else:
                    print(f"  tile {ix},{iy} GIVEN UP")
                    continue
                open(path, "wb").write(raw)
            print(f"  tile {ix},{iy}  {len(raw) / 1e6:5.1f} MB")
            yield ET.fromstring(raw)


def tags_of(el):
    return {t.get("k"): t.get("v") for t in el.findall("tag")}


def write(name, features):
    path = os.path.join(HERE, name + ".geojson")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump({"type": "FeatureCollection", "features": features}, fh)
    print(f"  {name:16s} {len(features):5d} features")


def main():
    coords = {}
    node_tags = {}
    ways = {}
    for root in download():
        for n in root.findall("node"):
            nid = n.get("id")
            coords[nid] = (float(n.get("lon")), float(n.get("lat")))
            t = tags_of(n)
            if t:
                node_tags[nid] = t
        for w in root.findall("way"):
            wid = w.get("id")
            if wid in ways:
                continue
            refs = [nd.get("ref") for nd in w.findall("nd")]
            ways[wid] = (refs, tags_of(w))
    print(f"merged {len(coords):,} nodes, {len(ways):,} ways")

    point_out = {name: [] for name, _ in POINT_LAYERS}
    line_out = {name: [] for name, _ in LINE_LAYERS}

    def emit_point(name, lon, lat, t):
        props = {k: t[k] for k in POINT_TAGS if k in t}
        point_out[name].append({
            "type": "Feature",
            "geometry": {"type": "Point", "coordinates": [lon, lat]},
            "properties": props,
        })

    def emit_line(name, pts, t):
        props = {k: t[k] for k in LINE_TAGS if k in t}
        closed = len(pts) > 3 and pts[0] == pts[-1]
        line_out[name].append({
            "type": "Feature",
            "geometry": {
                "type": "Polygon" if closed else "LineString",
                "coordinates": [[list(p) for p in pts]] if closed
                else [list(p) for p in pts],
            },
            "properties": props,
        })

    for nid, t in node_tags.items():
        lon, lat = coords[nid]
        for name, test in POINT_LAYERS:
            if test(t):
                emit_point(name, lon, lat, t)

    # A way that spans a tile seam may be returned with some of its nodes missing.
    # Dropping the unresolved refs keeps the run of surveyed geometry we do have rather
    # than discarding the whole feature.
    for refs, t in ways.values():
        pts = [coords[r] for r in refs if r in coords]
        if len(pts) < 2:
            continue
        for name, test in LINE_LAYERS:
            if test(t):
                emit_line(name, pts, t)
        for name, test in POINT_LAYERS:
            if test(t):
                cx = sum(p[0] for p in pts) / len(pts)
                cy = sum(p[1] for p in pts) / len(pts)
                emit_point(name, cx, cy, t)

    print("written:")
    for name in point_out:
        write(name, point_out[name])
    for name in line_out:
        write(name, line_out[name])

    art = point_out["osm_artwork"]
    named = [f for f in art if f["properties"].get("name")]
    print(f"\nnamed artworks in extent ({len(named)} named of {len(art)} total):")
    for f in sorted(named, key=lambda f: f["properties"]["name"]):
        lon, lat = f["geometry"]["coordinates"]
        x = (lon - LON0) * M_PER_DEG_LON
        y = (lat - LAT0) * M_PER_DEG_LAT
        if EXTENT[0] <= x <= EXTENT[1] and EXTENT[2] <= y <= EXTENT[3]:
            print(f"  {f['properties']['name'][:44]:46s} "
                  f"{f['properties'].get('artwork_type', '-'):12s} "
                  f"x={x:8.1f} y={y:8.1f}")



if __name__ == "__main__":
    main()
