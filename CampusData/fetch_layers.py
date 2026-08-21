"""Download Georgia Tech campus GIS layers from the public GT Facilities ArcGIS services."""

import json
import os
import urllib.parse
import urllib.request

ROOT = "https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
PAGE = 1000

# (output name, service, layer id)
#
# GT publishes no public-art layer and no tables/chairs layer, so the loose seating and
# the named sculptures have to come from somewhere else. Everything below is real
# surveyed data.
LAYERS = [
    ("buildings_gt", "GT_Campus_Map_Tiles", 0),
    ("recreation_fields", "GT_Campus_Map_Tiles", 1),
    ("buildings_noncampus", "GT_Campus_Map_Tiles", 2),
    ("parking", "GT_Campus_Map_Tiles", 3),
    ("sidewalks", "GT_Campus_Map_Tiles", 4),
    ("roads", "GT_Campus_Map_Tiles", 5),
    ("buildings_offcampus", "GT_Campus_Map_Tiles", 6),
    ("site_boundary", "GT_Campus_Map_Tiles", 9),
    ("trees", "Tree_Inventory_View", 1),
    ("landscape_areas", "NBBJ_Landscape_Areas", 13),
    # Site furniture and monuments.
    ("benches", "Commemorative_Benches_Public_View", 0),
    ("benches_potential", "Potential_Commemorative_Benches", 0),
    ("lights", "Outside_Lights_Public_View", 2),
    ("bollards", "Bollard_Review", 0),
    ("monument_signs", "MonumentSigns_ViewOnly", 0),
    ("bins", "Trash_and_Recycling_Bins_View_Only", 0),
    ("call_boxes", "Call_Box_Location_View_Layer", 0),
    ("entrances", "ADA_Entrances", 0),
    ("shrubs", "Campus_Plant_Data_View_Only", 0),
    ("parking_spaces", "Parking_Spaces_Public_View", 6),
    ("street_lines", "GT_BaseMap", 6),
]


def get_json(url):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-fetch/1.0"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.load(resp)


def fetch_layer(service, layer_id):
    """Page through a feature layer and return a single GeoJSON FeatureCollection."""
    base = f"{ROOT}/{service}/FeatureServer/{layer_id}/query"
    features = []
    offset = 0
    while True:
        params = urllib.parse.urlencode(
            {
                "where": "1=1",
                "outFields": "*",
                "outSR": "4326",
                "f": "geojson",
                "resultOffset": offset,
                "resultRecordCount": PAGE,
            }
        )
        data = get_json(f"{base}?{params}")
        batch = data.get("features", [])
        features.extend(batch)
        if len(batch) < PAGE:
            break
        offset += PAGE
    return {"type": "FeatureCollection", "features": features}


def main():
    for name, service, layer_id in LAYERS:
        path = os.path.join(OUT_DIR, f"{name}.geojson")
        try:
            fc = fetch_layer(service, layer_id)
        except Exception as exc:  # noqa: BLE001 - report and continue with other layers
            print(f"{name:24s} FAILED  ({exc})")
            continue
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(fc, fh)
        kinds = sorted({f["geometry"]["type"] for f in fc["features"] if f.get("geometry")})
        size_kb = os.path.getsize(path) // 1024
        print(f"{name:24s} {len(fc['features']):6d} features  {size_kb:6d} KB  {','.join(kinds)}")


if __name__ == "__main__":
    main()
