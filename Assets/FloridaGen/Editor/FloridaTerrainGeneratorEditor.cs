using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace kimbleCode.DeadEverywhere.FloridaTerrainGen.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="FloridaTerrainGenerator"/>.
    /// Displays geographic coordinate preview, generation status, action buttons,
    /// and a scene-view visualisation of the 1-mile tile boundary.
    ///
    /// Two gizmo-drawing modes are supported, controlled by the
    /// <c>Tools → Florida Terrain → Show All Terrain Gizmos</c> menu toggle:
    /// <list type="bullet">
    ///   <item><b>Show All ON</b> — gizmos are drawn for every terrain instance in
    ///   the scene on every repaint, regardless of selection.  The full instance list
    ///   is rebuilt lazily (only when the hierarchy changes), so per-frame cost is
    ///   proportional to the number of chunks, not the scene graph size.</item>
    ///   <item><b>Show All OFF</b> — gizmos are drawn only for the currently selected
    ///   terrain object via the standard <c>OnSceneGUI</c> path.</item>
    /// </list>
    ///
    /// In both modes, OSM line geometry is pre-built into <c>Vector3[]</c> pair
    /// arrays and rendered with a single <c>Handles.DrawLines</c> call per colour
    /// category (instead of one call per edge), keeping the draw-call budget small.
    /// </summary>
    [InitializeOnLoad]
    [CustomEditor(typeof(FloridaTerrainGenerator))]
    public class FloridaTerrainGeneratorEditor : UnityEditor.Editor
    {
        // ── Session-state keys for menu master toggles ────────────────────────────
        private const string KeyMasterAll       = "FloridaTerrainGen.ShowGizmos";
        private const string KeyMasterBuildings = "FloridaTerrainGen.ShowBuildingGizmos";
        private const string KeyMasterRoads     = "FloridaTerrainGen.ShowRoadGizmos";

        private const string MenuRoot      = "Tools/Florida Terrain/";
        private const string MenuAll       = MenuRoot + "Show All Terrain Gizmos";
        private const string MenuBuildings = MenuRoot + "Show All Building Gizmos";
        private const string MenuRoads     = MenuRoot + "Show All Road Gizmos";

        // ── Scene-wide generator cache ────────────────────────────────────────────
        // Rebuilt lazily on hierarchyChanged, not on every SceneView repaint.
        // Used only when Show All is ON.
        private static FloridaTerrainGenerator[] s_AllGenerators = System.Array.Empty<FloridaTerrainGenerator>();
        private static bool s_CacheDirty = true;

        // ── Per-generator line-pair cache (rebuilt only when OSM data changes) ────
        private static readonly Dictionary<EntityId, OsmLineCache> s_LineCache
            = new Dictionary<EntityId, OsmLineCache>();

        /// <summary>
        /// Pre-built <c>Handles.DrawLines</c>-ready arrays for one terrain chunk.
        /// Rebuilt only when <see cref="buildingCount"/> or <see cref="roadCount"/>
        /// changes, so the per-repaint cost is a simple array lookup + one or a few
        /// draw calls rather than rebuilding geometry every frame.
        /// </summary>
        private sealed class OsmLineCache
        {
            public int   buildingCount;       // invalidation keys
            public int   roadCount;
            public float baseY;               // terrain Y at build time

            // All building edges as {start0,end0, start1,end1, …}
            public Vector3[] buildingLines;

            // Road edges grouped by colour; each entry = one Handles.DrawLines call
            public List<(Color color, Vector3[] lines)> roadGroups;
        }

        // ── Static initialisation (runs once when the editor domain loads) ─────────

        static FloridaTerrainGeneratorEditor()
        {
            // Repaint-time hook: handles Show All mode
            SceneView.duringSceneGui -= DrawAllTerrainGizmos;
            SceneView.duringSceneGui += DrawAllTerrainGizmos;

            // Mark the scene cache dirty whenever the hierarchy changes so we
            // never call FindObjectsByType inside a repaint callback.
            EditorApplication.hierarchyChanged += () => s_CacheDirty = true;
        }

        // ── Instance lifecycle ────────────────────────────────────────────────────

        private void OnEnable()
        {
            // Mark dirty so the scene-wide list is refreshed on the next repaint.
            // (Also triggered by EditorApplication.hierarchyChanged, but OnEnable
            // covers cases like editor domain reloads where the hierarchy event
            // may not fire.)
            s_CacheDirty = true;
        }

        private void OnDisable()
        {
            if (target is FloridaTerrainGenerator gen)
            {
                s_LineCache.Remove(gen.GetEntityId());
                s_CacheDirty = true;
            }
        }

        // ── Show All path (DrawAllTerrainGizmos → scene-wide cache) ──────────────

        private static void RefreshCacheIfNeeded()
        {
            if (!s_CacheDirty) return;
            s_CacheDirty = false;
#if UNITY_2023_1_OR_NEWER
            s_AllGenerators = Object.FindObjectsByType<FloridaTerrainGenerator>(
                FindObjectsInactive.Include);
#else
            s_AllGenerators = Object.FindObjectsOfType<FloridaTerrainGenerator>(true);
#endif
        }

        private static void DrawAllTerrainGizmos(SceneView _)
        {
            // Show All OFF → gizmos handled per-selection by OnSceneGUI below
            if (!SessionState.GetBool(KeyMasterAll, true)) return;

            RefreshCacheIfNeeded();

            bool masterBuildings = SessionState.GetBool(KeyMasterBuildings, true);
            bool masterRoads     = SessionState.GetBool(KeyMasterRoads, true);

            foreach (var gen in s_AllGenerators)
            {
                if (gen == null) continue;
                DrawGizmos(gen, masterBuildings, masterRoads);
            }
        }

        // ── Per-selection path (OnSceneGUI – only active when Show All is OFF) ───

        private void OnSceneGUI()
        {
            // Show All is ON → already handled by DrawAllTerrainGizmos above
            if (SessionState.GetBool(KeyMasterAll, true)) return;

            if (target is FloridaTerrainGenerator gen)
            {
                DrawGizmos(gen,
                    masterBuildings: SessionState.GetBool(KeyMasterBuildings, true),
                    masterRoads:     SessionState.GetBool(KeyMasterRoads,     true));
            }
        }

        // ── Gizmo drawing (called once per terrain per repaint) ───────────────────

        private static void DrawGizmos(FloridaTerrainGenerator gen,
                                       bool masterBuildings, bool masterRoads)
        {
            if (!gen.showGizmos) return;

            var   pos        = gen.transform.position;
            float sz         = (float)FloridaTerrainGenerator.MILES_TO_METERS;
            float seaLevelY  = pos.y + gen.maxTerrainHeight / 2f;

            // ── Tile boundary (4 lines – always cheap) ────────────────────────────
            Vector3 sw = new Vector3(pos.x,      seaLevelY, pos.z);
            Vector3 se = new Vector3(pos.x + sz, seaLevelY, pos.z);
            Vector3 ne = new Vector3(pos.x + sz, seaLevelY, pos.z + sz);
            Vector3 nw = new Vector3(pos.x,      seaLevelY, pos.z + sz);

            Handles.color = new Color(1f, 0.85f, 0.1f, 0.9f);
            Handles.DrawLine(sw, se, 2f);
            Handles.DrawLine(se, ne, 2f);
            Handles.DrawLine(ne, nw, 2f);
            Handles.DrawLine(nw, sw, 2f);

            // ── Compass labels ────────────────────────────────────────────────────
            var style = new GUIStyle
            {
                normal    = { textColor = new Color(1f, 0.95f, 0.3f) },
                fontSize  = 10,
                fontStyle = FontStyle.Bold
            };

            var b = gen.CalculateChunkBounds(gen.chunkX, gen.chunkY);
            Handles.Label(sw, $"  SW {b.south:F4}\u00b0N {-b.west:F4}\u00b0W", style);
            Handles.Label(ne, $"  NE {b.north:F4}\u00b0N {-b.east:F4}\u00b0W", style);

            // ── Origin marker (chunk 0,0 only) ────────────────────────────────────
            if (gen.chunkX == 0 && gen.chunkY == 0)
            {
                Handles.color = new Color(0.2f, 0.8f, 1f, 0.9f);
                Handles.Label(sw + new Vector3(8f, 0f, 0f),
                    "  Origin (27\u00b053\u203202\u2033N  82\u00b030\u203242\u2033W)", style);
            }

            // ── OSM overlays (batched via cached line arrays) ─────────────────────
            bool wantBuildings = masterBuildings && gen.showBuildingGizmos;
            bool wantRoads     = masterRoads     && gen.showRoadGizmos;

            if ((!wantBuildings && !wantRoads) ||
                (gen.osmBuildings == null && gen.osmRoads == null))
                return;

            var cache = GetOrBuildCache(gen, seaLevelY + 0.5f);

            if (wantBuildings && cache.buildingLines != null && cache.buildingLines.Length >= 2)
            {
                Handles.color = new Color(1f, 0.4f, 0.1f, 0.85f);
                Handles.DrawLines(cache.buildingLines);
            }

            if (wantRoads && cache.roadGroups != null)
            {
                foreach (var (color, lines) in cache.roadGroups)
                {
                    if (lines == null || lines.Length < 2) continue;
                    Handles.color = color;
                    Handles.DrawLines(lines);
                }
            }
        }

        // ── OSM line-pair cache ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the cached <see cref="OsmLineCache"/> for <paramref name="gen"/>,
        /// rebuilding it only when the OSM data has changed.
        /// </summary>
        private static OsmLineCache GetOrBuildCache(FloridaTerrainGenerator gen, float gy)
        {
            var id           = gen.GetEntityId();
            int buildingCount = gen.osmBuildings?.Length ?? 0;
            int roadCount     = gen.osmRoads?.Length    ?? 0;

            if (s_LineCache.TryGetValue(id, out var existing) &&
                existing.buildingCount == buildingCount &&
                existing.roadCount     == roadCount     &&
                Mathf.Approximately(existing.baseY, gy))
            {
                return existing;
            }

            var cache = new OsmLineCache
            {
                buildingCount = buildingCount,
                roadCount     = roadCount,
                baseY         = gy
            };

            // ── Building footprint lines ──────────────────────────────────────────
            if (gen.osmBuildings != null && buildingCount > 0)
            {
                // Count edges first to allocate once
                int edgeCount = 0;
                foreach (var bldg in gen.osmBuildings)
                {
                    if (bldg?.footprint != null && bldg.footprint.Length >= 2)
                        edgeCount += bldg.footprint.Length; // N edges for N-vertex polygon
                }

                var bLines = new Vector3[edgeCount * 2];
                int idx = 0;
                foreach (var bldg in gen.osmBuildings)
                {
                    if (bldg?.footprint == null || bldg.footprint.Length < 2) continue;
                    int n = bldg.footprint.Length;
                    for (int i = 0; i < n; i++)
                    {
                        Vector2 p0 = bldg.footprint[i];
                        Vector2 p1 = bldg.footprint[(i + 1) % n];
                        bLines[idx++] = new Vector3(p0.x, gy, p0.y);
                        bLines[idx++] = new Vector3(p1.x, gy, p1.y);
                    }
                }
                cache.buildingLines = bLines;
            }

            // ── Road centre-line groups (one group per colour) ────────────────────
            if (gen.osmRoads != null && roadCount > 0)
            {
                // Bucket roads by colour so each colour is a single DrawLines call
                var buckets = new Dictionary<Color, List<Vector3>>();
                foreach (var road in gen.osmRoads)
                {
                    if (road?.points == null || road.points.Length < 2) continue;
                    var col = RoadGizmoColor(road.highway);
                    if (!buckets.ContainsKey(col))
                        buckets[col] = new List<Vector3>();
                    var bucket = buckets[col];
                    for (int i = 0; i < road.points.Length - 1; i++)
                    {
                        Vector2 p0 = road.points[i];
                        Vector2 p1 = road.points[i + 1];
                        bucket.Add(new Vector3(p0.x, gy, p0.y));
                        bucket.Add(new Vector3(p1.x, gy, p1.y));
                    }
                }

                cache.roadGroups = new List<(Color, Vector3[])>(buckets.Count);
                foreach (var kv in buckets)
                    cache.roadGroups.Add((kv.Key, kv.Value.ToArray()));
            }

            s_LineCache[id] = cache;
            return cache;
        }

        /// <summary>
        /// Invalidates the line cache for <paramref name="gen"/> so it is rebuilt
        /// on the next repaint.  Call this after new OSM data is fetched.
        /// </summary>
        internal static void InvalidateCache(FloridaTerrainGenerator gen)
        {
            s_LineCache.Remove(gen.GetEntityId());
            SceneView.RepaintAll();
        }

        // ── Tools menu – master controls ─────────────────────────────────────────

        [MenuItem(MenuAll, false, 100)]
        private static void MenuToggleAll()
        {
            SessionState.SetBool(KeyMasterAll, !SessionState.GetBool(KeyMasterAll, true));
            SceneView.RepaintAll();
        }

        [MenuItem(MenuAll, true)]
        private static bool MenuToggleAllValidate()
        {
            Menu.SetChecked(MenuAll, SessionState.GetBool(KeyMasterAll, true));
            return true;
        }

        [MenuItem(MenuBuildings, false, 101)]
        private static void MenuToggleBuildings()
        {
            SessionState.SetBool(KeyMasterBuildings, !SessionState.GetBool(KeyMasterBuildings, true));
            SceneView.RepaintAll();
        }

        [MenuItem(MenuBuildings, true)]
        private static bool MenuToggleBuildingsValidate()
        {
            Menu.SetChecked(MenuBuildings, SessionState.GetBool(KeyMasterBuildings, true));
            // Greyed-out when the master "all" toggle is off
            return SessionState.GetBool(KeyMasterAll, true);
        }

        [MenuItem(MenuRoads, false, 102)]
        private static void MenuToggleRoads()
        {
            SessionState.SetBool(KeyMasterRoads, !SessionState.GetBool(KeyMasterRoads, true));
            SceneView.RepaintAll();
        }

        [MenuItem(MenuRoads, true)]
        private static bool MenuToggleRoadsValidate()
        {
            Menu.SetChecked(MenuRoads, SessionState.GetBool(KeyMasterRoads, true));
            return SessionState.GetBool(KeyMasterAll, true);
        }

        // ── Styles (lazily initialised) ───────────────────────────────────────────
        private GUIStyle _titleStyle;
        private GUIStyle _labelStyle;

        private GUIStyle TitleStyle => _titleStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 14,
            alignment = TextAnchor.MiddleCenter,
            padding   = new RectOffset(0, 0, 4, 4)
        };

        private GUIStyle SmallLabel => _labelStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true
        };

        // ── Inspector GUI ─────────────────────────────────────────────────────────

        public override void OnInspectorGUI()
        {
            var gen = (FloridaTerrainGenerator)target;

            // ── Title ─────────────────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            GUILayout.Label("Florida Terrain Generator", TitleStyle);
            EditorGUILayout.Space(2);

            EditorGUILayout.HelpBox(
                "Generates a 1 mile \u00d7 1 mile terrain tile using real USGS 3DEP elevation data.\n" +
                "Chunk (0,\u20090) \u2192 27\u00b053\u203202.27\u2033N  82\u00b030\u203242.40\u2033W  (Tampa Bay).\n" +
                "1 Unity unit = 1 metre  \u2502  +X = East  \u2502  +Z = North",
                MessageType.Info);

            EditorGUILayout.Space(6);

            // ── Standard inspector fields (chunkX/Y, maxHeight, resolution, etc.) ─
            DrawDefaultInspector();

            EditorGUILayout.Space(6);

            // ── Chunk geographic preview ──────────────────────────────────────────
            EditorGUILayout.LabelField("Chunk Preview", EditorStyles.boldLabel);

            var b  = gen.CalculateChunkBounds(gen.chunkX, gen.chunkY);
            var wp = gen.ChunkWorldPosition(gen.chunkX, gen.chunkY);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    new GUIContent("SW corner",
                        "Geographic coordinates of the terrain's south-west corner."),
                    $"{b.south:F6}\u00b0N   {-b.west:F6}\u00b0W");

                EditorGUILayout.TextField(
                    new GUIContent("NE corner",
                        "Geographic coordinates of the terrain's north-east corner."),
                    $"{b.north:F6}\u00b0N   {-b.east:F6}\u00b0W");

                EditorGUILayout.TextField(
                    new GUIContent("World position (SW)",
                        "Unity world-space XZ of this tile's south-west corner in metres."),
                    $"({wp.x:F1}\u202fm,  0\u202fm,  {wp.z:F1}\u202fm)");
            }

            EditorGUILayout.Space(6);

            // ── Status banner ─────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(gen.statusMessage))
            {
                bool ok  = gen.statusMessage.Contains("\u2713") ||
                           gen.statusMessage.Contains("cleared");
                bool err = gen.statusMessage.StartsWith("Error") ||
                           gen.statusMessage.StartsWith("Failed") ||
                           gen.statusMessage.StartsWith("Download failed");

                EditorGUILayout.HelpBox(gen.statusMessage,
                    err ? MessageType.Error :
                    ok  ? MessageType.None  : MessageType.None);
            }

            if (gen.isGenerating)
            {
                EditorGUILayout.HelpBox(
                    "Downloading elevation data… The editor will respond again shortly.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            // ── Action buttons ────────────────────────────────────────────────────
            using (new EditorGUI.DisabledScope(gen.isGenerating))
            {
                var prevColor = GUI.backgroundColor;
                GUI.backgroundColor = gen.isGenerating
                    ? Color.grey
                    : new Color(0.4f, 0.85f, 0.45f);

                if (GUILayout.Button(
                        gen.isGenerating
                            ? "\u23f3  Generating…"
                            : "\u25b6  Generate Terrain Chunk",
                        GUILayout.Height(36)))
                {
                    gen.GenerateChunk();
                    GUIUtility.ExitGUI();
                }

                GUI.backgroundColor = prevColor;
                EditorGUILayout.Space(2);

                if (GUILayout.Button("\u2715  Clear Terrain (Flat)", GUILayout.Height(26)))
                {
                    if (EditorUtility.DisplayDialog(
                            "Clear Terrain",
                            "Reset this terrain to a flat surface at elevation 0?",
                            "Clear", "Cancel"))
                    {
                        gen.ClearTerrain();
                    }
                }
            }

            EditorGUILayout.Space(8);

            // ── Help foldout ──────────────────────────────────────────────────────
            DrawHelpFoldout();

            EditorGUILayout.Space(8);

            // ── OSM Buildings & Roads ─────────────────────────────────────────────
            EditorGUILayout.LabelField("OSM Buildings & Roads", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Downloads real building footprints and road centre-lines from " +
                "OpenStreetMap for this chunk.\n" +
                "No API key required.  Toggle gizmo visibility with the checkboxes " +
                "in the inspector above (\"Show Building Gizmos\" / \"Show Road Gizmos\"), " +
                "or use Tools \u2192 Florida Terrain for project-wide master toggles.",
                MessageType.Info);

            if (!string.IsNullOrEmpty(gen.osmStatusMessage))
            {
                bool osmErr = gen.osmStatusMessage.StartsWith("OSM Error");
                EditorGUILayout.HelpBox(gen.osmStatusMessage,
                    osmErr ? MessageType.Error : MessageType.None);
            }

            using (new EditorGUI.DisabledScope(gen.osmFetching || gen.isGenerating))
            {
                var prevOsmColor = GUI.backgroundColor;
                GUI.backgroundColor = gen.osmFetching
                    ? Color.grey
                    : new Color(0.3f, 0.7f, 1f);

                if (GUILayout.Button(
                        gen.osmFetching
                            ? "\u23f3  Fetching OSM Data\u2026"
                            : "\u2b07  Fetch Buildings & Roads",
                        GUILayout.Height(30)))
                {
                    gen.FetchOsmData();
                    // Invalidate the line cache so rebuilt geometry is used after fetch
                    InvalidateCache(gen);
                    GUIUtility.ExitGUI();
                }

                GUI.backgroundColor = prevOsmColor;
            }

            EditorGUILayout.Space(4);

            // Repaint continuously while generating so the status message updates
            if (gen.isGenerating || gen.osmFetching)
                Repaint();

            EditorGUILayout.Space(8);

            // ── Highway Meshes ────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Highway Meshes", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Builds 3-D road-surface meshes for all drivable roads in the fetched " +
                "OSM data (motorway through residential).\n\n" +
                "\u2022 Ground roads conform to the terrain heightmap (real USGS elevation).\n" +
                "\u2022 Bridges (bridge=yes) are elevated above the terrain with the minimum " +
                "vertical clearance for their road class and a smooth approach grade.\n" +
                "\u2022 Tunnels (tunnel=yes) are skipped \u2013 they are underground.\n" +
                "\u2022 Road curves come directly from OSM GPS traces, so bends are accurate.\n\n" +
                "Four material assets are created in Assets/[Tools]/FloridaGen/" +
                "Materials/ \u2013 assign any road-surface texture there without re-generating.",
                MessageType.Info);

            if (!string.IsNullOrEmpty(gen.highwayMeshStatusMessage))
            {
                bool hwWarn = gen.highwayMeshStatusMessage.StartsWith("No OSM");
                EditorGUILayout.HelpBox(gen.highwayMeshStatusMessage,
                    hwWarn ? MessageType.Warning : MessageType.None);
            }

            bool osmReady = gen.osmRoads != null && gen.osmRoads.Length > 0;

            using (new EditorGUI.DisabledScope(!osmReady || gen.osmFetching || gen.isGenerating))
            {
                var prevHwyColor = GUI.backgroundColor;
                GUI.backgroundColor = osmReady
                    ? new Color(0.55f, 0.35f, 0.9f)
                    : Color.grey;

                if (GUILayout.Button("\u25b6  Generate Highway Meshes", GUILayout.Height(30)))
                {
                    gen.GenerateHighwayMeshes();
                    GUIUtility.ExitGUI();
                }

                GUI.backgroundColor = prevHwyColor;
            }

            EditorGUILayout.Space(2);

            if (GUILayout.Button("\u2715  Clear Highway Meshes", GUILayout.Height(26)))
            {
                if (EditorUtility.DisplayDialog(
                        "Clear Highway Meshes",
                        "Remove all generated highway mesh objects from this chunk?",
                        "Clear", "Cancel"))
                {
                    gen.ClearHighwayMeshes();
                }
            }
        }

        // ── Road gizmo colour by highway type ────────────────────────────────────

        private static Color RoadGizmoColor(string highway)
        {
            switch (highway)
            {
                case "motorway":
                case "motorway_link":
                case "trunk":
                case "trunk_link":
                    return new Color(1.0f, 0.50f, 0.0f, 0.90f); // orange
                case "primary":
                case "primary_link":
                    return new Color(1.0f, 0.85f, 0.0f, 0.90f); // yellow
                case "secondary":
                case "secondary_link":
                case "tertiary":
                case "tertiary_link":
                    return new Color(0.8f, 0.80f, 0.3f, 0.90f); // pale yellow
                case "residential":
                case "living_street":
                case "unclassified":
                    return new Color(0.9f, 0.90f, 0.9f, 0.80f); // white
                case "path":
                case "footway":
                case "cycleway":
                case "track":
                    return new Color(0.3f, 0.90f, 0.5f, 0.80f); // green
                default:
                    return new Color(0.7f, 0.70f, 0.7f, 0.70f); // gray
            }
        }

        // ── Help foldout ──────────────────────────────────────────────────────────

        private static bool _helpOpen;

        private void DrawHelpFoldout()
        {
            _helpOpen = EditorGUILayout.Foldout(_helpOpen, "Quick-Start Guide", true);
            if (!_helpOpen) return;

            EditorGUILayout.HelpBox(
                "1. Add a Terrain component to a GameObject and attach FloridaTerrainGenerator.\n" +
                "2. Set chunkX / chunkY (integer miles East / North from Tampa Bay origin).\n" +
                "   • Chunk (0,0) = your house at 27\u00b053\u203202.27\u2033N  82\u00b030\u203242.40\u2033W.\n" +
                "   • Chunk (1,0) = one mile East of home.\n" +
                "   • Chunk (0,-1) = one mile South of home.\n" +
                "3. Click \"\u25b6 Generate Terrain Chunk\".\n" +
                "   The terrain will be downloaded and resized to exactly 1\u202fmile \u00d7 1\u202fmile\n" +
                "   (1609.344\u202fm \u00d7 1609.344\u202fm) with real USGS elevation data applied.\n\n" +
                "Data source: USGS 3D Elevation Program (3DEP) \u2014 1/3 arc-second (~10 m) resolution.\n" +
                "No API key or account required.\n\n" +
                "Tip: Enable \"Auto Position\" to have the terrain automatically moved to the\n" +
                "correct world-space position so adjacent chunks tile seamlessly.",
                MessageType.None);
        }
    }
}
