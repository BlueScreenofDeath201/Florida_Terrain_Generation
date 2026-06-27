# Florida Terrain Generator

Generates a **1 mile × 1 mile** Unity Terrain tile from real USGS 3DEP topographic elevation data for any location in Florida.

---

## Quick Start

1. Create a **Terrain** GameObject in your scene.
2. Attach the **FloridaTerrainGenerator** component to it.
3. Set **Chunk X** and **Chunk Y** in the Inspector (see coordinate system below).
4. Click **▶ Generate Terrain Chunk**.

The terrain will be automatically resized to exactly **1609.344 m × 1609.344 m** (1 international mile) and its heightmap filled with real elevation data.

---

## Coordinate System

| Property | Value |
|---|---|
| World Origin (chunk 0, 0 SW corner) | **27°53′02.27″N  82°30′42.40″W** *(Tampa Bay area)* |
| 1 Unity unit | 1 metre |
| Positive chunk X | East |
| Positive chunk Y | North |
| Unity +X axis | East |
| Unity +Z axis | North |
| Unity +Y axis | Elevation |

### Finding a Chunk

```
chunkX = floor( (target_longitude_W - origin_longitude_W) / miles_per_degree_lon )
chunkY = floor( (target_latitude_N  - origin_latitude_N)  / miles_per_degree_lat )
```

At latitude 28°N:
- 1° latitude  ≈ 69.1 miles  → 1 mile ≈ 0.01447°
- 1° longitude ≈ 61.1 miles  → 1 mile ≈ 0.01637°

**Examples (relative to the Tampa Bay origin):**

| Place | approx. chunkX | approx. chunkY |
|---|---|---|
| Home (origin) | 0 | 0 |
| Downtown Tampa | +4 | +4 |
| St. Pete Beach | -5 | -5 |
| Clearwater | -3 | +7 |
| Orlando | +130 | +70 |

---

## Inspector Settings

| Field | Description |
|---|---|
| Chunk X | East-West tile index (integer miles from origin) |
| Chunk Y | North-South tile index (integer miles from origin) |
| Max Terrain Height | Total vertical range in metres.  The terrain is placed at Y = −(maxTerrainHeight / 2) so that sea level sits at world Y 0 and you can sculpt the terrain both upward **and** downward.  Default **500 m** gives ±250 m of headroom. |
| Heightmap Resolution | Heightmap pixel count per side (33–513). **129** is recommended for a good quality/speed balance. |
| Auto Position | When enabled, moves the Terrain GameObject to the correct world-space position so tiles stitch seamlessly. |

---

## Elevation Data

Data is fetched from the **USGS 3D Elevation Program (3DEP)** at **1/3 arc-second (~10 m) horizontal resolution**. No API key or account is required.

The tool tries three sources in order:

| Priority | Source | Method | Notes |
|---|---|---|---|
| 1 | USGS 3DEP WCS | Single HTTP request, Arc/Info ASCII Grid | Fastest; ~10 m native resolution |
| 2 | USGS 3DEP ImageServer getSamples | Batched JSON REST | Falls back if WCS is unavailable |
| 3 | USGS EPQS point query | Per-point HTTP, 17×17 sample grid | Slowest; produces ~95 m effective resolution |

All sources use the same underlying USGS dataset. Results are bilinearly resampled to the selected heightmap resolution.

---

## Tiling Multiple Chunks

With **Auto Position** enabled each chunk automatically snaps to the right world-space location. To tile an area:

1. Create one Terrain + FloridaTerrainGenerator per tile.
2. Give each a unique chunk coordinate pair.
3. Generate them one by one.

Adjacent Terrain components will share edges without gaps because each tile is sized to exactly `1609.344 m` on a side and positioned at `(chunkX × 1609.344, -maxTerrainHeight / 2, chunkY × 1609.344)`.

---

## Troubleshooting

**Generation fails / "All elevation sources failed"**
- Check internet connectivity.  
- The USGS services occasionally have maintenance windows; try again shortly.

**Terrain looks flat**
- Verify `Max Terrain Height` is at least 500 m — if set too low, small elevation differences are compressed to near-zero.  
- Florida is genuinely very flat. Most of the state is 0–50 m above sea level.

**Chunk boundary mismatches**
- Ensure **Auto Position** is enabled on all tiles.  
- All tiles must use the same `Max Terrain Height` value.

---

## Building Footprint Hook

When OSM data is fetched, `FloridaTerrainGenerator` calls `OnBuildingFootprintGenerated` once per building footprint.  The method is `protected virtual` so you can subclass the generator and override it to spawn actual building geometry:

```csharp
using kimbleCode.DeadEverywhere.FloridaTerrainGen;
using UnityEngine;

public class MyTerrainGenerator : FloridaTerrainGenerator
{
    protected override void OnBuildingFootprintGenerated(Vector2[] footprintPoints)
    {
        // footprintPoints are the same world-space XZ vertices drawn as the
        // building gizmo.  Spawn your building prefab here.
    }
}
```

**Notes:**
- Called **once at generation time** (when "Fetch Buildings & Roads" completes), not every frame.
- `footprintPoints` is in world-space XZ (metres from world origin), matching the gizmo.
- The polygon is closed — the last vertex connects back to the first.

---

## Namespace / Pattern

Follows the project-wide editor tool convention:

- **Runtime component:** `Assets/[Tools]/FloridaGen/FloridaTerrainGenerator.cs`  
  Namespace: `kimbleCode.DeadEverywhere.FloridaTerrainGen`  
  Attributes: `[ExecuteInEditMode]`, `[RequireComponent(typeof(Terrain))]`

- **Custom editor:** `Assets/[Tools]/FloridaGen/Editor/FloridaTerrainGeneratorEditor.cs`  
  Namespace: `kimbleCode.DeadEverywhere.FloridaTerrainGen.Editor`

---

## Changelog

### 2026-06-23 – Raise terrain baseline; add building footprint hook

#### Terrain height & Y-offset (±250 m range at 500 m total height)

`maxTerrainHeight` default raised from 200 m to **500 m** and the terrain
position Y is now set to `−maxTerrainHeight / 2` (−250 m at default).

This means:
- Sea level (USGS elevation 0) maps to **world Y 0** — the terrain sits
  half-way up its total height range.
- The Unity Terrain sculpting tools can now push vertices **both upward
  and downward** from the sea-level baseline.  Previously the terrain base
  was at Y 0, so it could only be raised above that level.
- `ApplyToTerrain` normalisation updated to
  `(elevation + maxTerrainHeight / 2) / maxTerrainHeight`.
- `ClearTerrain` now resets to a flat surface at sea level (normalised 0.5)
  instead of the terrain floor.
- `ChunkWorldPosition` Y component updated accordingly.

> All tiles in a multi-chunk scene **must** share the same `maxTerrainHeight`
> value to stitch correctly — this was already true and hasn't changed.

#### `OnBuildingFootprintGenerated` hook

Added `protected virtual void OnBuildingFootprintGenerated(Vector2[] footprintPoints)`
to `FloridaTerrainGenerator`.  It is called **once per building** at the moment
OSM data is parsed, providing the same world-space XZ polygon used to draw the
building gizmo.  Subclass the generator and override this method to spawn
actual building geometry.  The base implementation is intentionally empty.

### 2026-03-30 – Fix Unity 6 deprecation warnings (CS0618)

Resolved all `CS0618` compiler warnings in `FloridaTerrainGeneratorEditor.cs` to stay
ahead of upcoming Unity API removals. **No functional changes.**

| Line(s) | Before | After | Reason |
|---|---|---|---|
| 98, 227, 310 | `GetInstanceID()` | `GetEntityId()` | `Object.GetInstanceID()` is deprecated; Unity will replace InstanceID with EntityId in a future version. |
| 48–49 | `Dictionary<int, OsmLineCache>` | `Dictionary<EntityId, OsmLineCache>` | The implicit `EntityId → int` cast is deprecated; using `EntityId` as the dictionary key avoids the cast entirely. |
| 227 | `int id = gen.GetEntityId()` | `var id = gen.GetEntityId()` | Stores the `EntityId` directly instead of implicitly casting to `int`. |
| 110–111 | `FindObjectsByType<T>(FindObjectsInactive, FindObjectsSortMode)` | `FindObjectsByType<T>(FindObjectsInactive)` | The overload taking `FindObjectsSortMode` is deprecated; the sort-mode-free overload is the recommended replacement. |

### 2026-03-30 – Subdivide long road-mesh segments (MeshCollider stability)

Added `SubdivideLongSegments()` to `FloridaTerrainGenerator.cs`.  Any road
centre-line segment longer than 450 m is evenly subdivided so that the
resulting `MeshCollider` triangles stay within Unity's 500-unit vertex-distance
stability limit.  This eliminates the physics-engine warning:

> *"the distance between any 2 vertices is greater than 500 units — the
> resulting triangle mesh can impact simulation and query stability"*

The change is purely additive (new vertices are interpolated on existing
straight edges) and does not alter the visual appearance of road meshes.

### 2026-03-30 – Fix FindObjectsSortMode deprecation in SampleOscillateSliders

Removed the deprecated `FindObjectsSortMode.None` parameter from
`FindObjectsByType` in `Assets/[Synty]/InterfaceApocalypseHUD/Samples/Scripts/SampleOscillateSliders.cs`
(line 36).  Same deprecation pattern as the FloridaTerrainGeneratorEditor fix above.
