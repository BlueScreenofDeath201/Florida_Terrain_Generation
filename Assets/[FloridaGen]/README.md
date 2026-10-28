# Florida Generator building data

Fetching OSM buildings and roads calculates each building's direction towards the
closest point on any fetched road centre-line segment, not just the nearest road
vertex. The start point is the same vertex-average centre already written to the
building text file (including a repeated closing vertex, if present). The search
uses world-space XZ metres and all fetched OSM highway types, independent of road
mesh generation. It does not infer a mapped entrance or a drivable access route,
and roads missing from the fetched data cannot be considered.

Both automatic saves and the **Save Building Data (.txt)** / **Save All (.json)**
buttons refresh the directions before writing. **Load All** recalculates them from
the saved buildings and roads, including older version-1 and version-2 JSON files,
without network access. New chunk JSON files use version 3.

## Fields

Each JSON building now includes:

```json
"nearestRoadDirection": "E",
"nearestRoadBearing": 90.0
```

`nearestRoadDirection` is one of `N`, `NE`, `E`, `SE`, `S`, `SW`, `W`, `NW`
(eight equal 45-degree sectors). `nearestRoadBearing` is the unquantized bearing
in degrees clockwise from north: north = 0, east = 90, south = 180, west = 270.
Unity +Z is north and +X is east, so the bearing can also be used as a Y rotation.

A missing/invalid footprint, no usable road segments, or a centre within 1 mm of
the nearest road produces `nearestRoadDirection = "unknown"` and
`nearestRoadBearing = -1`, rather than falsely reporting north. Zero-length road
segments are ignored. Equal-distance ties keep the first segment in fetched order.

## Generated `Buildings_0_0.txt` format

The existing nine columns (including the OSM `height` in metres), numbering,
coordinate precision and footprint format are unchanged. Two columns are appended
after `height`:
`nearestRoadDirection|nearestRoadBearing`. Text bearings use two decimal places
and wrap a rounded 360.00 back to 0.00. Readers must accept the two new columns
and continue ignoring lines starting with `#`.

For a 10 m square house centred at (5, 5), on flat ground at Y = 0, and a
north-south road at X = 20, the generated file is:

```text
# chunk=0,0 format=id|type|levels|centerX|groundY|centerZ|area|x,z;x,z;...|height|nearestRoadDirection|nearestRoadBearing height=metres(0=unknown) bearing=degrees_clockwise_from_north unknown=unknown,-1.00
0|house|1|5.00|0.00|5.00|100.0|0.00,0.00;10.00,0.00;10.00,10.00;0.00,10.00|0|E|90.00
```

Without a usable road, the same building row ends in `|unknown|-1.00`.
