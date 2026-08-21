"""What building attributes does the cached OSM download actually carry?

The GT facilities layer has no construction year and no material, so `is_brick` is
currently a hash of the building id - literally random. OSM is the only remaining source
that could make it real. This counts what is there before any code is written against it.
"""
import collections
import glob
import os
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))

WANT = ("building", "building:material", "building:colour", "building:levels",
        "roof:material", "roof:colour", "roof:shape", "roof:levels", "start_date",
        "height", "name", "wall", "building:part")

counts = collections.Counter()
values = collections.defaultdict(collections.Counter)
seen = set()
buildings = 0

for path in sorted(glob.glob(os.path.join(HERE, "osm_cache", "*.xml"))):
    root = ET.parse(path).getroot()
    for el in root:
        if el.tag not in ("way", "relation"):
            continue
        key = (el.tag, el.get("id"))
        if key in seen:
            continue
        seen.add(key)
        tags = {t.get("k"): t.get("v") for t in el.findall("tag")}
        if "building" not in tags and "building:part" not in tags:
            continue
        buildings += 1
        for k in WANT:
            if k in tags:
                counts[k] += 1
                values[k][tags[k]] += 1

print(f"{buildings:,} OSM building ways/relations in the cached tiles\n")
for k in WANT:
    if not counts[k]:
        print(f"  {k:22s} 0")
        continue
    top = "  ".join(f"{v}({n})" for v, n in values[k].most_common(6))
    print(f"  {k:22s} {counts[k]:5,d}   {top}")
