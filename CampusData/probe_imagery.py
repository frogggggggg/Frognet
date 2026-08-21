"""Probe public-domain aerial imagery services for a campus ortho.

Everything outside the GT vector layers is currently painted a single flat turf green,
which is what makes the aerial view read as a plastic model. USDA NAIP and the USGS
National Map imagery are US federal public domain, so unlike the GT layers they are
actually usable. This just finds which endpoint answers.
"""
import json
import urllib.parse
import urllib.request

HEADERS = {"User-Agent": "gt-campus-model/1.0 (research)"}

CANDIDATES = [
    ("USGS NAIPPlus",
     "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer"),
    ("USGS NAIPImagery",
     "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPImagery/ImageServer"),
    ("APFO NAIP GA 2021",
     "https://gis.apfo.usda.gov/arcgis/rest/services/NAIP/Georgia_2021_60cm/ImageServer"),
    ("APFO NAIP GA 2019",
     "https://gis.apfo.usda.gov/arcgis/rest/services/NAIP/Georgia_2019_60cm/ImageServer"),
    ("Esri World Imagery",
     "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer"),
    ("GA GIS Clearinghouse 2019",
     "https://gisserver.gio.georgia.gov/arcgis/rest/services/Imagery/Georgia2019/ImageServer"),
]


def probe(url):
    try:
        req = urllib.request.Request(url + "?f=json", headers=HEADERS)
        with urllib.request.urlopen(req, timeout=25) as fh:
            data = json.loads(fh.read().decode("utf-8", "replace"))
    except Exception as exc:  # noqa: BLE001
        return f"FAIL {type(exc).__name__}: {exc}"
    if "error" in data:
        return f"ERROR {data['error'].get('message')}"
    bits = []
    for key in ("name", "serviceDescription", "pixelSizeX", "bandCount",
                "maxImageHeight", "maxImageWidth", "spatialReference"):
        if key in data:
            v = data[key]
            if isinstance(v, dict):
                v = v.get("latestWkid") or v.get("wkid")
            bits.append(f"{key}={str(v)[:70]}")
    ext = data.get("extent") or data.get("fullExtent")
    if ext:
        bits.append(f"extent x {ext.get('xmin'):.0f}..{ext.get('xmax'):.0f}")
    return "OK  " + "  ".join(bits)


for label, url in CANDIDATES:
    print(f"{label:28s} {probe(url)}")
