"""Inventory GT's ArcGIS services for site furniture, walls, steps and any textured 3D."""

import json
import re
import urllib.request

ROOT = "https://services5.arcgis.com/7WaXTZEsI88qiQGw/arcgis/rest/services"

KEYWORDS = re.compile(
    r"bench|furnitur|amenit|site|wall|stair|step|ramp|rail|monument|campanile|sculpt|art|"
    r"light|pole|sign|bike|rack|bollard|planter|fountain|water|plaza|hardscape|curb|"
    r"topo|contour|elev|terrain|grade|spot|texture|photo|mesh|point|scene|3d",
    re.I)


def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": "campus-probe/1.0"})
    with urllib.request.urlopen(req, timeout=90) as resp:
        return json.loads(resp.read())


def main():
    root = get(f"{ROOT}?f=json")
    services = root.get("services", [])
    print(f"{len(services)} services\n")

    scene_layers, hits = [], []
    for svc in services:
        name = svc["name"].split("/")[-1]
        if svc.get("type") == "SceneServer":
            scene_layers.append(name)
        if KEYWORDS.search(name):
            hits.append((name, svc.get("type")))

    print("--- services matching keywords ---")
    for name, kind in sorted(hits):
        print(f"  {kind:14s} {name}")

    print("\n--- SceneServers (3D) ---")
    for name in scene_layers:
        print(f"  {name}")

    print("\n--- layers inside matching FeatureServers ---")
    for name, kind in sorted(hits):
        if kind != "FeatureServer":
            continue
        try:
            info = get(f"{ROOT}/{name}/FeatureServer?f=json")
        except Exception as exc:  # noqa: BLE001
            print(f"  {name}: {exc}")
            continue
        for layer in info.get("layers", []) + info.get("tables", []):
            print(f"  {name} [{layer['id']:>2}] {layer['name']}  ({layer.get('geometryType','')})")


if __name__ == "__main__":
    main()
