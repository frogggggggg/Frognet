# Data sources and licensing

This model is assembled from four sources with **four different and incompatible**
licensing positions. Read this before the model goes anywhere near a shipped product.

| Source | What it provides | Licence status |
|---|---|---|
| USGS 3DEP DEM | bare-earth terrain | US federal work, public domain |
| USGS 3DEP lidar (`GA_Statewide_B2_2018`) | DSM, canopy, roof surfaces | US federal work, public domain |
| USGS NAIP imagery | `ortho.jpg`, terrain and roof texture | US federal work, public domain |
| OpenStreetMap | steps, artwork, benches, tables, bike racks, fountains | **ODbL 1.0** |
| Georgia Tech Facilities GIS / NBBJ I3S | footprints, roads, sidewalks, trees, building massing | **"No License Provided - request permission"** |

## The two problems

**1. Georgia Tech data has no licence at all.** Not permissive, not restrictive -
absent. The ArcGIS metadata literally reads *"No License Provided. Request permission to
use."* Publicly reachable is not the same as licensed. This project sits inside a Unity
tree with a `steam_appid.txt` in it, which means the intent is commercial, which means
this needs an actual written answer from GT Capital Planning & Space Management before
release. Nothing about the rest of the pipeline fixes this.

**2. OpenStreetMap is ODbL, which is share-alike on databases.** Attribution is
mandatory and easy. The share-alike clause is the awkward part: if the shipped artifact
is judged a *Derivative Database* it must be released under ODbL too. The usual reading
is that a rendered scene is a *Produced Work* - attribution required, share-alike not -
but `gt_campus.blend` contains geometry generated one-to-one from OSM nodes and ways,
which is closer to a database than a picture is. Treat this as unresolved.

If ODbL is unacceptable, the OSM-derived content is separable: it is exactly the
`osm_*.geojson` layers and the `Steps`, `Amenity_*`, `Water`, and public-art parts.
Dropping them costs 546 stair flights, 55 artworks including the Koan, and 394
amenities - a real loss, but a bounded one.

## Required attribution text

If the OSM content stays in, this must appear somewhere a user can find it:

> Contains information from OpenStreetMap and OpenStreetMap Foundation, which is made
> available under the Open Database License (ODbL). https://www.openstreetmap.org/copyright
>
> Elevation, lidar and imagery courtesy of the U.S. Geological Survey.

## What is *not* in here, and why

There is no facade imagery. Georgia Tech publishes exactly one SceneServer
(`NBBJ_Buildings3D_WebM`) and it reports zero `textureSetDefinitions` - the buildings are
untextured massing. Every wall in this model is procedural, and no request to any GT
endpoint will change that. Roofs are the exception: those are sampled from NAIP, because
nadir imagery is the one view that measures a roof honestly.
