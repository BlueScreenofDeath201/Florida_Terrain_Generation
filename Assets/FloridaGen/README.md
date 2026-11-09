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
without network access. New chunk JSON files use version 4, including the
mainline motorway lane-minimum setting.

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

## Road scale and appearance

The `WorldChunk` residential reference is **10 × 10 metres at scale 1**.
One Unity unit remains one metre throughout the generator: a chunk is still
1609.344 metres across, and building footprints/heights, bridge clearances,
barriers, tunnel walls and embankments are not multiplied by a tile scale.
The reference prefab is a dimensional guide, not a tile stamped along curves.

Residential and unclassified defaults now have a 10-metre paved width:
two 3.5-metre lanes with 1.5 metres of shoulder on each side. An explicit OSM
`width` still takes precedence; local-road `lanes` uses the same metric lane width.
Primary, secondary and tertiary defaults are 14, 12 and 10 metres respectively;
service roads and living streets retain their narrower defaults. The existing
**Road Width Adjustment** scales the pavement, collision and structural footprint
together; paint width and texture grain stay in metres. Lane counts and one-way
directions come from OSM when supplied. Missing counts default to three lanes on
motorways, four on primary roads, one on links/service/living streets and two
elsewhere. These are visual fallbacks, not a lane-level survey.

Use **Generate Roads** (or the full pipeline) on existing chunks to rebuild:

- Seamlessly repeating, deterministic asphalt albedo and fine aggregate normal
  textures, mipmapped and anisotropically filtered, with one repeat per 4 metres
  in world XZ. The base is neutral gray with up to 12% aggregate variation and
  stronger fine normal relief (default strength 0.65), rather than a smooth,
  featureless surface. Larger aggregate variation sits beneath the fine grain
  without adding colored blotches or gloss. Grain does not
  stretch on wider roads or restart at chunk edges.
- Separate batched white/yellow paint meshes: 150 mm double yellow centre lines
  for opposing traffic, white shoulder lines, and 10-foot dashes / 30-foot gaps
  between same-direction lanes. One-way left edges are yellow, including reversed
  OSM ways. Paint follows the solved ground, bridge and tunnel surfaces without
  adding physics colliders or casting detached shadows. White/yellow paint has its
  own opaque, mildly worn thermoplastic texture: its UVs follow the stripe rather
  than the world axes, with slightly softened edges and sparse wear flecks.
- Unpainted openings at connected, same-height T/cross junctions, including skew
  junctions. Ordinary two-way way splits and grade-separated crossings do not
  interrupt the lines. Dash phase is measured from the original OSM way, so it
  survives clipping into neighbouring chunks.

### Highways and interchange ramps

Motorway/trunk lanes use **3.6576 m (12 ft)**, separately from the residential
3.5-metre standard. A one-way motorway defaults to three lanes with **1.2192 m
inside / 3.048 m outside shoulders**: **15.24 m** paved width per carriageway,
not the entire divided highway. A one-lane motorway ramp has a 1.2192 m inside
and 2.4384 m outside shoulder: **7.3152 m** total. Two-lane ramps are **10.9728 m**.
Two-way trunk roads have outside shoulders on both edges. **Minimum Motorway
Lanes**, exposed on both generators, now defaults to **3** per one-way mainline
carriageway. This deliberately widens the saved one-lane motorways into full-size
highways, with matching dividers, without modifying OSM data. Set it to **0** for
strict mapped lane counts. Ramps, explicit widths and explicit two-way roads are
never overridden. The setting is saved with each chunk; older saves receive the
new default. Geometric clearance still takes priority where mapped carriageways
are too close to accommodate the requested width; lane paint is withheld where
the lanes no longer fit. Reversed ways swap the asymmetric shoulders. Explicit `oneway=no`
is retained on newly fetched roads (old saves can be refreshed by fetching OSM).

Lane placement, pavement width and road structures now use this shared
cross-section. Highway edges are 200 mm solid yellow on traffic-left and white
on traffic-right, with 150 mm long-dashed white lane dividers. At connected
on/off-ramps, the intersecting mainline edge becomes a **3 ft / 9 ft dotted
merge line** while the other edge and through-lane dividers continue. Ramp paint
is withheld while it lies within the shared pavement, then resumes when the
actual curved ramp departs; a shallow first tangent no longer erases all the
mainline's markings. These openings are inferred from connected geometry, not a
surveyed acceleration-lane design. Gore hatching, lane arrows and exit-only lane
assignments are not invented from missing lane-level data.

On single-lane one-way ramps, the solid boundary guides follow the paved sides,
300 mm in from the pavement edges, instead of appearing down the middle of the
wide ramp-and-shoulder surface. Yellow remains on traffic-left and white on
traffic-right, including reversed ways. This is a visual guide adjustment only:
the ramp's pavement width and collision geometry are unchanged. Real dividers on
multi-lane ramps and center lines on explicitly two-way links remain intact.

Same-class, same-height two-piece continuations share an exact mitered endpoint
cross-section, including reversed point order; width changes taper into the join
instead of leaving triangular cracks, including at ground/bridge boundaries.
Connected coplanar motorway/ramp pavement
is not pinched merely for extending past an arbitrary 90-metre join radius.
Opening a bridge barrier retains the deck fascia below it. A narrow conflict at
one station no longer removes the paint from the entire road.

Motorway/trunk approaches now use **vertical retaining edges**, shaded with the
structural material, instead of spreading large earth mounds beside the lanes.
For other roads, each earthwork face is tested against the actual mitered
pavement of every nearby road, including angled crossings, short roads and
endpoints. A conflicting slope becomes a retaining edge; if that edge would
still intrude into another road's driving clearance, the face is omitted. This
affects the rendered mesh and both collider paths, not just its appearance.
Source terrain itself is not excavated.

Shared asphalt textures are saved in the generator's Materials folder on first
generation. Existing untextured road materials receive them automatically;
artist-assigned albedo maps, normal maps and tiling are preserved. Paint has its
own editable materials. Service/living streets, known unpaved roads, and roads
too narrow for the indicated lanes do not receive highway paint. Odd two-way lane
totals do not invent a centre turn lane. Junction priority, stop bars, crosswalks
and turn arrows are not inferred without lane/control data.

Regeneration upgrades both the original and subtle generator-owned asphalt maps
to the aggregate versions, including materials already saved in the project.
The old 0.15 normal-strength default becomes 0.65; custom strengths and texture
transforms are preserved. Albedo and normal upgrades are independent, so a custom
albedo does not prevent a default normal from updating, or vice versa.
Unchanged default tints are updated; artist-assigned textures and custom tints
are left alone. Decks, barriers, tunnel shells and support columns share a
darker gray structural material with the same understated surface detail, instead
of bright white concrete. Paved shoulders share their road's asphalt material.

### Automatic RealToon setup

Generation automatically uses the installed **HDRP/RealToon/Version 5/Default**
shader for pavement, paint, structures and embankments. No Inspector shader swap
is needed. Existing generated materials are migrated in place when roads are
regenerated, preserving their tint, albedo/normal textures, tiling, offsets and
normal strength. The code maps those values to RealToon's `_MainColor`,
`_MainTex`, `_NormalMap` and `_NormalScale`, enables its `N_F_NM_ON` feature when
a normal map is present, and applies its material property drawers. Outlines are
disabled on road batches so thin paint and mesh seams do not acquire black rims.
The stock 0.15 Main Color Power is raised to 1 on generated materials so paint
does not disappear into the asphalt; other artist-adjusted power values remain.
The installed shader directly multiplies albedo by exposed HDRP light values,
unlike VehicleDemo's HDRP/Lit materials. Generated roads now enable RealToon's
**Limit Light Intensity** feature (0–1) so bright sunlight cannot bleach gray
asphalt and yellow paint into white. Gloss, reflection, rim-light and self-lit
features are disabled for a matte finish; the default environmental-light
multiplier is reduced to 0.25, retaining custom multiplier values. This preset
is also applied to previously generated materials, without changing scene
lighting or discarding custom textures/tints. Like VehicleDemo, markings are
separate colored geometry over asphalt, not lines baked into the road texture.
Click **Generate Roads** again to replace existing meshes and update materials.
If the installed shader is missing, generation reports an error before clearing
the existing roads rather than silently switching to a different shader.

The existing FloridaGeneration EditMode test assembly covers metric widths,
paint geometry/direction, highway/ramp shoulders, merge openings, bent way-piece
seams, chunk dash/UV continuity, texture restraint/determinism, RealToon migration,
matte daylight settings, retaining edges, angled earthwork clearance, lane-minimum persistence
and collider separation. Run it in the project's Unity Editor via
**Window → General → Test Runner → EditMode**.
