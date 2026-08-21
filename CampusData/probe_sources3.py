"""Probe the two data sources that were previously written off.

1. OpenStreetMap. Overpass returned 406 from this machine earlier, which is the classic
   symptom of a missing User-Agent rather than a real block. OSM would supply, in one go,
   the three things GT's own GIS does not publish: highway=steps, picnic tables and
   loose seating, and tourism=artwork (the Koan).

2. USGS 3DEP lidar point clouds, published as Entwine Point Tiles on a public S3 bucket
   with no auth. A real point cloud carries roof shape, rooftop plant, tree crowns and
   actual stair treads - all currently guessed from a bare-earth raster.
"""

import json
import urllib.error
import urllib.request

UA = {"User-Agent": "gt-campus-builder/1.0 (offline model build)"}
BBOX = (33.7680, -84.4080, 33.7830, -84.3830)  # s, w, n, e


def get(url, data=None, timeout=90):
    req = urllib.request.Request(url, data=data, headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def probe_overpass():
    query = """
[out:json][timeout:60];
(
  way["highway"="steps"](%f,%f,%f,%f);
  node["tourism"="artwork"](%f,%f,%f,%f);
  node["leisure"="picnic_table"](%f,%f,%f,%f);
  node["amenity"~"bench|drinking_water|fountain|bicycle_parking"](%f,%f,%f,%f);
  way["man_made"="bridge"](%f,%f,%f,%f);
);
out tags center;
""" % (BBOX * 5)
    for host in ("https://overpass-api.de/api/interpreter",
                 "https://overpass.kumi.systems/api/interpreter",
                 "https://overpass.osm.jp/api/interpreter"):
        try:
            raw = get(host, data=query.encode("utf-8"))
        except urllib.error.HTTPError as exc:
            print(f"  {host}  HTTP {exc.code}")
            continue
        except Exception as exc:
            print(f"  {host}  {type(exc).__name__}: {exc}")
            continue
        js = json.loads(raw)
        els = js.get("elements", [])
        print(f"  {host}  OK, {len(els)} elements")
        tally = {}
        art = []
        for e in els:
            t = e.get("tags", {})
            key = (t.get("highway") or t.get("tourism") or t.get("leisure")
                   or t.get("amenity") or t.get("man_made") or "?")
            tally[key] = tally.get(key, 0) + 1
            if t.get("tourism") == "artwork":
                art.append((t.get("name", "<unnamed>"), e.get("lat"), e.get("lon"),
                            t.get("artwork_type", "")))
        for k, v in sorted(tally.items(), key=lambda kv: -kv[1]):
            print(f"      {k:20s} {v}")
        if art:
            print("    artwork found:")
            for name, lat, lon, kind in art:
                print(f"      {name!r:34s} {lat}, {lon}  {kind}")
        return True
    return False


def probe_lidar():
    idx = "https://raw.githubusercontent.com/hobuinc/usgs-lidar/master/boundaries/resources.geojson"
    try:
        js = json.loads(get(idx))
    except Exception as exc:
        print(f"  index unreachable: {type(exc).__name__}: {exc}")
        return
    lat, lon = 33.7756, -84.3963
    hits = []
    for feat in js["features"]:
        name = feat["properties"].get("name", "")
        geom = feat.get("geometry") or {}
        rings = []
        if geom.get("type") == "Polygon":
            rings = geom["coordinates"]
        elif geom.get("type") == "MultiPolygon":
            rings = [r for poly in geom["coordinates"] for r in poly]
        for ring in rings:
            inside = False
            n = len(ring)
            for i in range(n):
                x0, y0 = ring[i][0], ring[i][1]
                x1, y1 = ring[(i + 1) % n][0], ring[(i + 1) % n][1]
                if (y0 > lat) != (y1 > lat):
                    xi = x0 + (lat - y0) * (x1 - x0) / (y1 - y0)
                    if lon < xi:
                        inside = not inside
            if inside:
                hits.append((name, feat["properties"].get("count", 0)))
                break
    if not hits:
        print("  no 3DEP project covers the campus origin")
        return
    for name, count in hits:
        print(f"  covers campus: {name}  ({count:,} points in project)")
        url = f"https://s3-us-west-2.amazonaws.com/usgs-lidar-public/{name}/ept.json"
        try:
            ept = json.loads(get(url, timeout=30))
            print(f"      ept.json OK  span={ept.get('span')}  srs="
                  f"{ept.get('srs', {}).get('horizontal')}  bounds={ept.get('bounds')}")
        except Exception as exc:
            print(f"      ept.json failed: {type(exc).__name__}: {exc}")


print("OpenStreetMap / Overpass")
if not probe_overpass():
    print("  every mirror refused")
print()
print("USGS 3DEP lidar (Entwine Point Tiles on public S3)")
probe_lidar()
