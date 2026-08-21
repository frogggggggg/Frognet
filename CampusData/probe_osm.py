"""Fetch raw OSM for the campus through the main API instead of Overpass.

Overpass answers 504 from here on even a trivial query, but the core OSM API is a
different service and serves the whole bbox as XML with no query language involved.
0.025 x 0.015 degrees is far inside the 0.25 sq deg limit.
"""

import collections
import urllib.request
import xml.etree.ElementTree as ET

URL = ("https://api.openstreetmap.org/api/0.6/map"
       "?bbox=-84.408,33.768,-84.383,33.783")

req = urllib.request.Request(URL, headers={"User-Agent": "gt-campus-builder/1.0"})
raw = urllib.request.urlopen(req, timeout=300).read()
open("osm_campus.xml", "wb").write(raw)
print(f"{len(raw):,} bytes")

root = ET.fromstring(raw)
nodes = root.findall("node")
ways = root.findall("way")
print(f"{len(nodes):,} nodes, {len(ways):,} ways")

INTEREST = ("tourism", "artwork_type", "amenity", "leisure", "highway",
            "man_made", "historic", "memorial")
tally = collections.Counter()
named_art = []
steps = 0
for el in list(nodes) + list(ways):
    tags = {t.get("k"): t.get("v") for t in el.findall("tag")}
    for key in ("tourism", "amenity", "leisure", "historic", "man_made"):
        if key in tags:
            tally[f"{key}={tags[key]}"] += 1
    if tags.get("highway") == "steps":
        steps += 1
    if tags.get("tourism") == "artwork" or "artwork_type" in tags or "memorial" in tags:
        named_art.append((tags.get("name", "<unnamed>"), el.tag, el.get("id"),
                          tags.get("artwork_type") or tags.get("memorial", ""),
                          el.get("lat"), el.get("lon")))

print(f"\nhighway=steps ways: {steps}")
print("\ntop tagged features:")
for k, v in tally.most_common(30):
    print(f"  {k:34s} {v}")
print("\nart / memorials:")
for row in named_art:
    print("  ", row)
