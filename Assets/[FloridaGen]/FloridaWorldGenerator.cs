using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DeadEverywhere.FloridaGen
{
    /// <summary>
    /// One object to generate Florida with.  Put it on an empty GameObject (leave the transform at the
    /// origin), type a chunk (x, y) and press a button - the terrain chunk appears as a child, already
    /// named, positioned, grouped and connected to its neighbours:
    ///
    /// <code>
    /// Florida                    (this component)
    ///   Row : -1
    ///     Row_-1_Col_0           (Terrain + TerrainCollider + FloridaTerrainGenerator)
    ///   Row : 0
    ///     Row_0_Col_0
    ///     Row_0_Col_1
    ///       Highway Meshes
    /// </code>
    ///
    /// The same hierarchy convention as the existing Florida scene: one "Row : X" group per X, chunks
    /// sorted by Y inside it, rows sorted by X.  The buttons run the pipeline of the chunk at (x, y):
    /// generate terrain, fetch OSM data, generate roads, save building data, Save All, Load All.
    /// Gizmo toggles stay on each chunk's <see cref="FloridaTerrainGenerator"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Florida/Florida World Generator")]
    public class FloridaWorldGenerator : MonoBehaviour
    {
        /// <summary>Where new chunks put their TerrainData asset (and so their Save All / building files).</summary>
        public const string DefaultTerrainFolder = "Assets/[DeadEverywhere]/DeadEverywhere/TheWorld/TheTerrain";

        [Header("Chunk  (1 unit = 1 mile)")]
        [Tooltip("East-West chunk index.  0 = origin longitude.  Positive = East.")]
        public int chunkX = 0;

        [Tooltip("North-South chunk index.  0 = origin latitude.  Positive = North.")]
        public int chunkY = 0;

        [Header("New Chunk Defaults  (applied when a chunk is created)")]
        [Tooltip("Project folder for the new chunk's TerrainData asset, named Row_<x>_Col_<y>.asset.  " +
                 "The building and Save All files are written next to it.  An asset that already exists " +
                 "there is reused, so a chunk that was deleted from the scene comes back with its data.")]
        public string terrainFolder = DefaultTerrainFolder;

        [Tooltip("Total terrain height range in metres (the chunk sits at Y = -range / 2).")]
        public float maxTerrainHeight = 500f;

        [Tooltip("Heightmap resolution of new chunks.  129 is a good balance for a 1-mile tile.")]
        public FloridaTerrainGenerator.HeightmapResolution heightmapResolution =
            FloridaTerrainGenerator.HeightmapResolution.R129;

        [Tooltip("New chunks write Buildings_<x>_<y>.txt and ChunkData_<x>_<y>.json after generating terrain " +
                 "and after fetching OSM data.")]
        public bool autoSaveDataFiles = true;

        [HideInInspector] public string statusMessage = "Ready";

        // ── Naming ────────────────────────────────────────────────────────────────

        private const string RowPrefix = "Row : ";

        /// <summary>Name of the group that holds every chunk with this X ("Row : 0").</summary>
        public static string RowName(int x) => RowPrefix + x.ToString(CultureInfo.InvariantCulture);

        /// <summary>Name of a chunk object ("Row_0_Col_3") - also the name of its TerrainData asset.</summary>
        public static string ChunkName(int x, int y) =>
            "Row_" + x.ToString(CultureInfo.InvariantCulture) + "_Col_" + y.ToString(CultureInfo.InvariantCulture);

        /// <summary>True when <paramref name="name"/> is a row group name; <paramref name="x"/> is its X.</summary>
        public static bool TryParseRowName(string name, out int x)
        {
            x = 0;
            return name != null &&
                   name.StartsWith(RowPrefix, StringComparison.Ordinal) &&
                   int.TryParse(name.Substring(RowPrefix.Length), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out x);
        }

        // ── Lookup ────────────────────────────────────────────────────────────────

        /// <summary>The chunk with these coordinates anywhere below this object, or null.</summary>
        public FloridaTerrainGenerator FindChunk(int x, int y)
        {
            foreach (var gen in GetComponentsInChildren<FloridaTerrainGenerator>(true))
                if (gen != null && gen.chunkX == x && gen.chunkY == y) return gen;
            return null;
        }

        /// <summary>The chunk at the (chunkX, chunkY) typed into this component, or null.</summary>
        public FloridaTerrainGenerator CurrentChunk => FindChunk(chunkX, chunkY);

        /// <summary>Every chunk below this object, sorted by X then Y.</summary>
        public List<FloridaTerrainGenerator> GetChunks()
        {
            var list = new List<FloridaTerrainGenerator>(GetComponentsInChildren<FloridaTerrainGenerator>(true));
            list.RemoveAll(g => g == null);
            list.Sort((a, b) => a.chunkX != b.chunkX ? a.chunkX.CompareTo(b.chunkX) : a.chunkY.CompareTo(b.chunkY));
            return list;
        }

        /// <summary>The direct child group "Row : x", or null.</summary>
        public Transform FindRow(int x)
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (TryParseRowName(child.name, out int rowX) && rowX == x) return child;
            }
            return null;
        }

        /// <summary>Names of coordinates that more than one chunk object claims.</summary>
        public List<string> FindDuplicateChunks()
        {
            var seen = new Dictionary<(int, int), string>();
            var result = new List<string>();
            foreach (var gen in GetChunks())
            {
                var key = (gen.chunkX, gen.chunkY);
                if (seen.TryGetValue(key, out string first))
                    result.Add($"({gen.chunkX}, {gen.chunkY}): {first} and {gen.name}");
                else
                    seen[key] = gen.name;
            }
            return result;
        }

        /// <summary>
        /// Chunk positions come from <c>transform.position</c> and the OSM data is in world space, so a
        /// moved, rotated or scaled root would put roads off the terrain.
        /// </summary>
        public bool HasIdentityTransform() =>
            transform.position == Vector3.zero &&
            transform.rotation == Quaternion.identity &&
            transform.lossyScale == Vector3.one;

        // ── Hierarchy ordering (editor and tests) ────────────────────────────────

        /// <summary>
        /// Orders the children of <paramref name="parent"/>: the ones without a key stay first in their
        /// current order, the keyed ones follow sorted by key (stable).  Returns the number moved.
        /// </summary>
        public static int SortChildren(Transform parent, Func<Transform, int?> keyOf)
        {
            var plain = new List<Transform>();
            var keyed = new List<KeyValuePair<int, Transform>>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                int? key = keyOf(child);
                if (key.HasValue) keyed.Add(new KeyValuePair<int, Transform>(key.Value, child));
                else plain.Add(child);
            }

            var ordered = new List<Transform>(plain);
            ordered.AddRange(keyed.OrderBy(k => k.Key).Select(k => k.Value));   // stable: equal keys keep their order

            int moved = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].GetSiblingIndex() == i) continue;
                ordered[i].SetSiblingIndex(i);
                moved++;
            }
            return moved;
        }

        /// <summary>Sorts the rows by X and the chunks inside every row by Y.</summary>
        public void SortHierarchy()
        {
            SortChildren(transform, t => TryParseRowName(t.name, out int x) ? x : (int?)null);
            for (int i = 0; i < transform.childCount; i++)
            {
                var row = transform.GetChild(i);
                if (!TryParseRowName(row.name, out _)) continue;
                SortChildren(row, t =>
                {
                    var gen = t.GetComponent<FloridaTerrainGenerator>();
                    return gen != null ? gen.chunkY : (int?)null;
                });
            }
        }

        /// <summary>
        /// Calls <see cref="Terrain.SetNeighbors"/> on the chunk at (x, y) and its four neighbours, so
        /// the seams between tiles are stitched.  Missing neighbours are left unconnected.
        /// </summary>
        public void ConnectNeighbours(int x, int y)
        {
            var map = new Dictionary<(int, int), Terrain>();
            foreach (var gen in GetComponentsInChildren<FloridaTerrainGenerator>(true))
            {
                if (gen == null) continue;
                var terrain = gen.GetComponent<Terrain>();
                if (terrain != null && !map.ContainsKey((gen.chunkX, gen.chunkY)))
                    map[(gen.chunkX, gen.chunkY)] = terrain;
            }

            Terrain At(int cx, int cy) => map.TryGetValue((cx, cy), out var t) ? t : null;

            foreach (var (dx, dy) in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                var t = At(x + dx, y + dy);
                if (t == null) continue;
                int cx = x + dx, cy = y + dy;
                t.SetNeighbors(At(cx - 1, cy), At(cx, cy + 1), At(cx + 1, cy), At(cx, cy - 1));
            }
        }

        /// <summary>
        /// Turns every gizmo of one chunk, or of all chunks, on or off.  Returns how many chunks were
        /// changed.  The toggles themselves stay on each chunk's <see cref="FloridaTerrainGenerator"/>.
        /// </summary>
        public int SetGizmos(bool on, bool allChunks)
        {
            int changed = 0;
            if (allChunks)
            {
                foreach (var gen in GetChunks()) { gen.SetAllGizmos(on); changed++; }
            }
            else
            {
                var gen = CurrentChunk;
                if (gen != null) { gen.SetAllGizmos(on); changed++; }
            }
            return changed;
        }

#if UNITY_EDITOR

        // ── Chunk creation ────────────────────────────────────────────────────────

        /// <summary>
        /// The chunk at (x, y), created when it does not exist: a Terrain with its own TerrainData asset
        /// under <see cref="terrainFolder"/>, a TerrainCollider, and a <see cref="FloridaTerrainGenerator"/>
        /// set to these coordinates, placed in its "Row : x" group at the right world position and
        /// connected to its neighbours.  A new chunk starts flat at sea level.
        /// </summary>
        public FloridaTerrainGenerator GetOrCreateChunk(int x, int y, out bool created)
        {
            created = false;
            var existing = FindChunk(x, y);
            if (existing != null) return existing;

            string folder = (terrainFolder ?? "").Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Terrain Folder '{terrainFolder}' must be a project folder inside Assets.");
            EnsureFolder(folder);

            string assetPath = folder + "/" + ChunkName(x, y) + ".asset";
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath);
            bool newAsset = data == null;
            if (newAsset)
            {
                data = new TerrainData
                {
                    heightmapResolution = (int)heightmapResolution,
                    size = new Vector3((float)FloridaTerrainGenerator.MILES_TO_METERS, maxTerrainHeight,
                                       (float)FloridaTerrainGenerator.MILES_TO_METERS)
                };
                AssetDatabase.CreateAsset(data, assetPath);
            }

            var template = NearestChunk(x, y);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = ChunkName(x, y);
            Undo.RegisterCreatedObjectUndo(go, "Create Florida Chunk");
            go.transform.SetParent(GetOrCreateRow(x), worldPositionStays: false);

            var gen = Undo.AddComponent<FloridaTerrainGenerator>(go);
            gen.chunkX = x;
            gen.chunkY = y;
            gen.maxTerrainHeight = newAsset || data.size.y <= 0f ? maxTerrainHeight : data.size.y;
            gen.autoSaveDataFiles = autoSaveDataFiles;
            gen.heightmapResolution = newAsset
                ? heightmapResolution
                : (FloridaTerrainGenerator.HeightmapResolution)
                    FloridaTerrainGenerator.ClosestHeightmapResolution(data.heightmapResolution);

            var terrain = go.GetComponent<Terrain>();
            if (template != null) CopyTerrainSettings(template.GetComponent<Terrain>(), terrain, template.gameObject, go);

            go.transform.position = gen.ChunkWorldPosition(x, y);
            if (newAsset) gen.ClearTerrain();   // flat at sea level instead of 0 = -range / 2

            SortHierarchy();
            ConnectNeighbours(x, y);
            created = true;
            EditorUtility.SetDirty(this);
            return gen;
        }

        private FloridaTerrainGenerator NearestChunk(int x, int y)
        {
            FloridaTerrainGenerator best = null;
            long bestDistance = long.MaxValue;
            foreach (var gen in GetChunks())
            {
                long dx = gen.chunkX - x, dy = gen.chunkY - y;
                long d = dx * dx + dy * dy;
                if (d < bestDistance && gen.GetComponent<Terrain>() != null) { best = gen; bestDistance = d; }
            }
            return best;
        }

        /// <summary>Copies the rendering settings (material, LOD error, shadows, …) of a neighbouring chunk.</summary>
        private static void CopyTerrainSettings(Terrain from, Terrain to, GameObject fromObject, GameObject toObject)
        {
            if (from == null || to == null) return;
            to.materialTemplate    = from.materialTemplate;
            to.heightmapPixelError = from.heightmapPixelError;
            to.basemapDistance     = from.basemapDistance;
            to.shadowCastingMode   = from.shadowCastingMode;
            to.drawInstanced       = from.drawInstanced;
            to.groupingID          = from.groupingID;
            to.allowAutoConnect    = from.allowAutoConnect;
            toObject.layer         = fromObject.layer;
        }

        /// <summary>The direct child group "Row : x", created when missing.</summary>
        public Transform GetOrCreateRow(int x)
        {
            var row = FindRow(x);
            if (row != null) return row;

            var go = new GameObject(RowName(x));
            Undo.RegisterCreatedObjectUndo(go, "Create Florida Row");
            go.transform.SetParent(transform, worldPositionStays: false);
            return go.transform;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            if (slash <= 0) throw new InvalidOperationException($"'{path}' is not a folder inside Assets.");
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }

        /// <summary>
        /// Puts every chunk below this object - including ones created by hand - into the "Row : x" group
        /// of its X, sorts rows by X and chunks by Y, removes empty rows and connects the neighbours.
        /// Chunks keep their names.  Returns a one-line report.
        /// </summary>
        public string OrganizeHierarchy()
        {
            int moved = 0;
            var chunks = GetChunks();
            foreach (var gen in chunks)
            {
                var row = GetOrCreateRow(gen.chunkX);
                if (gen.transform.parent == row) continue;
                Undo.SetTransformParent(gen.transform, row, "Organize Florida Chunks");
                moved++;
            }

            int removed = 0;
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (!TryParseRowName(child.name, out _) || child.childCount > 0) continue;
                Undo.DestroyObjectImmediate(child.gameObject);
                removed++;
            }

            SortHierarchy();
            foreach (var gen in chunks) ConnectNeighbours(gen.chunkX, gen.chunkY);

            var duplicates = FindDuplicateChunks();
            string report = $"Organized {chunks.Count} chunk(s): {moved} moved, {removed} empty row(s) removed.";
            if (duplicates.Count > 0) report += $"  \u26a0 duplicate coordinates: {string.Join("; ", duplicates)}";
            statusMessage = report;
            EditorUtility.SetDirty(this);
            return report;
        }

        // ── Pipeline: every step works on the chunk at (chunkX, chunkY) ──────────

        private FloridaTerrainGenerator RequireChunk(string action)
        {
            var gen = CurrentChunk;
            if (gen == null)
                statusMessage = $"Cannot {action}: there is no chunk ({chunkX}, {chunkY}) yet - " +
                                "click \"Generate Terrain\" first.";
            return gen;
        }

        private bool Fail(string step, Exception ex)
        {
            statusMessage = $"Error during {step}: {ex.Message}";
            Debug.LogError($"[FloridaWorldGenerator] {step} failed: {ex}");
            return false;
        }

        /// <summary>Creates the chunk if needed, then downloads its elevation into the heightmap.</summary>
        public bool GenerateTerrain()
        {
            try
            {
                var gen = GetOrCreateChunk(chunkX, chunkY, out bool created);
                gen.GenerateChunk();
                ConnectNeighbours(chunkX, chunkY);
                bool ok = gen.statusMessage != null && gen.statusMessage.Contains("\u2713");
                statusMessage = (created ? "Created " + gen.name + ".  " : "") + gen.statusMessage;
                return ok;
            }
            catch (Exception ex) { return Fail("Generate Terrain", ex); }
        }

        /// <summary>Downloads the OpenStreetMap data (buildings, roads, water, rail, nodes) of the chunk.</summary>
        public bool FetchOsm()
        {
            try
            {
                var gen = RequireChunk("fetch OSM data");
                if (gen == null) return false;
                gen.FetchOsmData();
                bool ok = !gen.osmFetching && !string.IsNullOrEmpty(gen.osmStatusMessage) &&
                          !gen.osmStatusMessage.StartsWith("OSM Error", StringComparison.Ordinal);
                statusMessage = gen.osmStatusMessage;
                return ok;
            }
            catch (Exception ex) { return Fail("Fetch OSM", ex); }
        }

        /// <summary>Builds the road, bridge and tunnel meshes (with colliders) from the fetched OSM data.</summary>
        public bool GenerateRoads()
        {
            try
            {
                var gen = RequireChunk("generate roads");
                if (gen == null) return false;
                gen.GenerateHighwayMeshes();
                bool ok = gen.highwayMeshStatusMessage != null && gen.highwayMeshStatusMessage.Contains("\u2713");
                statusMessage = string.IsNullOrEmpty(gen.highwayMeshStatusMessage) ? "No roads generated." : gen.highwayMeshStatusMessage;
                return ok;
            }
            catch (Exception ex) { return Fail("Generate Roads", ex); }
        }

        /// <summary>Writes Buildings_x_y.txt next to the chunk's terrain asset.</summary>
        public bool SaveBuildingData()
        {
            try
            {
                var gen = RequireChunk("save building data");
                if (gen == null) return false;
                int count = gen.SaveBuildingDataFile();
                statusMessage = count > 0
                    ? $"Saved {count} building(s) to {gen.GetBuildingFilePath()}  \u2713"
                    : "No building data to save - fetch OSM data first.";
                return count > 0;
            }
            catch (Exception ex) { return Fail("Save Building Data", ex); }
        }

        /// <summary>Writes ChunkData_x_y.json (heightmap + all OSM data) next to the chunk's terrain asset.</summary>
        public bool SaveAll()
        {
            try
            {
                var gen = RequireChunk("save the chunk");
                if (gen == null) return false;
                string path = gen.GetChunkFilePath();
                gen.SaveChunkFile(path);
                statusMessage = $"Saved the whole chunk to {path}  \u2713";
                return true;
            }
            catch (Exception ex) { return Fail("Save All", ex); }
        }

        /// <summary>Full path of the Save All file for (x, y): beside the chunk's terrain asset, or in <see cref="terrainFolder"/>.</summary>
        public string GetChunkFilePath(int x, int y)
        {
            var gen = FindChunk(x, y);
            if (gen != null) return gen.GetChunkFilePath();
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(terrainFolder ?? "Assets", FloridaChunkFile.ChunkFileName(x, y)));
        }

        /// <summary>
        /// Rebuilds the chunk from ChunkData_x_y.json - heightmap, OSM data and road meshes - without the
        /// network.  Creates the chunk if it is not in the scene yet.
        /// </summary>
        public bool LoadAll()
        {
            try
            {
                string path = GetChunkFilePath(chunkX, chunkY);
                if (!System.IO.File.Exists(path))
                {
                    statusMessage = $"Cannot load: {path} does not exist.  Run Save All for this chunk first.";
                    return false;
                }

                var gen = GetOrCreateChunk(chunkX, chunkY, out _);
                gen.LoadChunkFile(path, regenerateRoads: true);
                statusMessage = gen.statusMessage;
                if (gen.chunkX != chunkX || gen.chunkY != chunkY)
                {
                    // The file belongs to another chunk: file the object under its real coordinates.
                    statusMessage += $"  \u26a0 the file is for chunk ({gen.chunkX}, {gen.chunkY}); {gen.name} was moved there.";
                    OrganizeHierarchy();
                }
                return true;
            }
            catch (Exception ex) { return Fail("Load All", ex); }
        }

        /// <summary>Generate Terrain, Fetch OSM, Generate Roads, then Save All - stops at the first failure.</summary>
        public bool RunFullPipeline()
        {
            if (!GenerateTerrain()) return false;
            if (!FetchOsm()) return false;
            if (!GenerateRoads()) return false;
            SaveBuildingData();   // a chunk without buildings is fine
            if (!SaveAll()) return false;
            statusMessage = $"Chunk ({chunkX}, {chunkY}) done: terrain, OSM data, roads and data files  \u2713";
            return true;
        }

#endif // UNITY_EDITOR
    }
}
