"""One-off probe: look for public art (the Koan sculpture) and site furniture layers."""

import json
import urllib.parse
import urllib.request

ROOT = "https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"

CANDIDATES = [
    "Arboretum_Tours",
    "Arboretum_Tour_Zones",
    "Campus_Stories_(approved)",
    "Trash_and_Recycling_Bins_View_Only",
    "Recycle_Bins_View_Only",
    "Call_Box_Location_View_Layer",
    "ADA_Entrances",
    "Campus_Plant_Data_View_Only",
    "Parking_Spaces_Public_View",
    "EcoCommons_Data_Points",
    "Basemap_Layers_2024",
    "Basemap2024_Layers",
    "GT_BaseMap",
    "GT_Campus_Map_2022",
]


def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-probe/1.0"})
    with urllib.request.urlopen(req, timeout=90) as resp:
        return json.load(resp)


for service in CANDIDATES:
    try:
        meta = get(f"{ROOT}/{service}/FeatureServer?f=json")
    except Exception as exc:  # noqa: BLE001
        print(f"{service}: unreachable ({exc})")
        continue
    for layer in meta.get("layers", []) + meta.get("tables", []):
        lid = layer["id"]
        try:
            info = get(f"{ROOT}/{service}/FeatureServer/{lid}?f=json")
            count = get(f"{ROOT}/{service}/FeatureServer/{lid}/query?"
                        + urllib.parse.urlencode({"where": "1=1", "returnCountOnly": "true",
                                                  "f": "json"}))
        except Exception as exc:  # noqa: BLE001
            print(f"{service}/{lid}: {exc}")
            continue
        fields = [f["name"] for f in info.get("fields", [])]
        print(f"{service}/{lid} '{layer['name']}' {info.get('geometryType')} "
              f"n={count.get('count')}")
        print(f"    {', '.join(fields[:24])}")
