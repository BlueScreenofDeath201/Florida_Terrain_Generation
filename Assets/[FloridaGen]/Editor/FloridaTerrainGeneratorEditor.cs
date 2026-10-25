using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


namespace DeadEverywhere.FloridaGen.FloridaGenEditor
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
    public class FloridaTerrainGeneratorEditor : Editor
    {
        // ── Session-state keys for menu master toggles ────────────────────────────
        private const string KeyMasterAll       = "FloridaTerrainGen.ShowGizmos";
        private const string KeyMasterBuildings = "FloridaTerrainGen.ShowBuildingGizmos";
        private const string KeyMasterRoads     = "FloridaTerrainGen.ShowRoadGizmos";
        private const string KeyMasterRoadDetail = "FloridaTerrainGen.ShowRoadDetailGizmos";
        private const string KeyMasterWaterRail  = "FloridaTerrainGen.ShowWaterRailGizmos";

        private const string MenuRoot      = "Tools/Florida Terrain/";
        private const string MenuAll       = MenuRoot + "Show All Terrain Gizmos";
        private const string MenuBuildings = MenuRoot + "Show All Building Gizmos";
        private const string MenuRoads     = MenuRoot + "Show All Road Gizmos";
        private const string MenuRoadDetail = MenuRoot + "Show All Road Profile, Label and Node Gizmos";
        private const string MenuWaterRail  = MenuRoot + "Show All Water and Railway Gizmos";

        // ── Scene-wide generator cache ────────────────────────────────────────────
        // Rebuilt lazily on hierarchyChanged, not on every SceneView repaint.
        // Used only when Show All is ON.
        private static FloridaTerrainGenerator[] s_AllGenerators = Array.Empty<FloridaTerrainGenerator>();
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
            s_AllGenerators = UnityEngine.Object.FindObjectsByType<FloridaTerrainGenerator>(
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

            bool masterBuildings  = SessionState.GetBool(KeyMasterBuildings, true);
            bool masterRoads      = SessionState.GetBool(KeyMasterRoads, true);
            bool masterRoadDetail = SessionState.GetBool(KeyMasterRoadDetail, true);
            bool masterWaterRail  = SessionState.GetBool(KeyMasterWaterRail, true);

            foreach (var gen in s_AllGenerators)
            {
                if (gen == null) continue;
                DrawGizmos(gen, masterBuildings, masterRoads, masterRoadDetail, masterWaterRail);
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
                    masterBuildings:  SessionState.GetBool(KeyMasterBuildings,  true),
                    masterRoads:      SessionState.GetBool(KeyMasterRoads,      true),
                    masterRoadDetail: SessionState.GetBool(KeyMasterRoadDetail, true),
                    masterWaterRail:  SessionState.GetBool(KeyMasterWaterRail,  true));
            }
        }

        // ── Gizmo drawing (called once per terrain per repaint) ───────────────────

        private static void DrawGizmos(FloridaTerrainGenerator gen,
                                       bool masterBuildings, bool masterRoads,
                                       bool masterRoadDetail, bool masterWaterRail)
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

            // ── Solved road profile, water, railways, nodes, building heights ─────
            FloridaRoadGizmos.Draw(gen, masterRoadDetail, masterWaterRail, masterBuildings);

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

        [MenuItem(MenuRoadDetail, false, 103)]
        private static void MenuToggleRoadDetail()
        {
            SessionState.SetBool(KeyMasterRoadDetail, !SessionState.GetBool(KeyMasterRoadDetail, true));
            SceneView.RepaintAll();
        }

        [MenuItem(MenuRoadDetail, true)]
        private static bool MenuToggleRoadDetailValidate()
        {
            Menu.SetChecked(MenuRoadDetail, SessionState.GetBool(KeyMasterRoadDetail, true));
            return SessionState.GetBool(KeyMasterAll, true);
        }

        [MenuItem(MenuWaterRail, false, 104)]
        private static void MenuToggleWaterRail()
        {
            SessionState.SetBool(KeyMasterWaterRail, !SessionState.GetBool(KeyMasterWaterRail, true));
            SceneView.RepaintAll();
        }

        [MenuItem(MenuWaterRail, true)]
        private static bool MenuToggleWaterRailValidate()
        {
            Menu.SetChecked(MenuWaterRail, SessionState.GetBool(KeyMasterWaterRail, true));
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
                "Downloads real data from OpenStreetMap for this chunk:\n" +
                "\u2022 buildings \u2013 footprints plus height, levels, roof, material, colour and use;\n" +
                "\u2022 roads \u2013 centre-lines plus name, ref, lanes, width, speed limit, one-way, " +
                "bridge / tunnel type and layer;\n" +
                "\u2022 waterways, lakes and the coastline, railways, and traffic nodes " +
                "(signals, stop signs, crossings, street lights, motorway exits).\n" +
                "Rivers, canals and railways also set how much clearance a bridge must leave.\n" +
                "No API key required.  Toggle each gizmo group with the checkboxes in the inspector above, " +
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

            EditorGUILayout.Space(4);
            DrawDataFiles(gen);

            EditorGUILayout.Space(4);

            // Repaint continuously while generating so the status message updates
            if (gen.isGenerating || gen.osmFetching)
                Repaint();

            EditorGUILayout.Space(8);

            // ── Highway Meshes ────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Highway Meshes", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Builds 3-D road meshes for all drivable roads in the fetched OSM data " +
                "(motorway through residential).  A translation step first clips every road to this chunk, " +
                "finds each place one road passes over another (or over a river or railway), and solves " +
                "a height profile that respects each road class's maximum grade.\n\n" +
                "\u2022 Ground roads conform to the terrain heightmap and are never buried below it.\n" +
                "\u2022 Bridges are solid decks with crash barriers, a fascia, end walls and piers. " +
                "They clear what they cross by the legal minimum for a road (5.2 m), a railway (7 m) or a " +
                "river (4 m), plus the deck depth, and stay level across valleys instead of dipping.\n" +
                "\u2022 The ground roads that lead onto a bridge are lifted into approach embankments, so " +
                "the surface is continuous from ground to deck.\n" +
                "\u2022 Tunnels (tunnel=yes) are closed tubes with portal frames. The terrain is not " +
                "carved, so a tube follows the surface instead of boring under a hill.\n" +
                "\u2022 Roads are clipped to this chunk; where a road continues into the next chunk " +
                "it ends at the shared edge and the neighbour carries it on.\n" +
                "\u2022 Everything gets a standard MeshCollider (surface, structure and embankment " +
                "batches). Keep \"Highway Meshes\" inside a SubScene so it is baked into the ECS " +
                "physics worlds (only the Terrain needs a dedicated ECS collider component).\n\n" +
                "Material assets are created in Assets/[Tools]/FloridaGen/Materials/ \u2013 " +
                "assign any road-surface texture there without re-generating.  " +
                "After sculpting the terrain, click \"Rebuild Road Plan\" to refresh the gizmos, " +
                "then regenerate the meshes.",
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

            using (new EditorGUI.DisabledScope(!osmReady || gen.osmFetching || gen.isGenerating))
            {
                if (GUILayout.Button(new GUIContent("\u21bb  Rebuild Road Plan (refresh gizmos)",
                        "Re-solves the road heights against the current terrain and OSM data. " +
                        "Use after sculpting the terrain or changing the road settings."),
                        GUILayout.Height(24)))
                {
                    gen.InvalidateRoadPlan();
                    FloridaRoadGizmos.Invalidate(gen);
                    InvalidateCache(gen);
                }
            }

            DrawRoadPlanSummary(gen);

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

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Turn Off All Gizmos (this chunk)"))
                {
                    Undo.RecordObject(gen, "Florida Gizmos");
                    gen.SetAllGizmos(false);
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Turn On All Gizmos"))
                {
                    Undo.RecordObject(gen, "Florida Gizmos");
                    gen.SetAllGizmos(true);
                    SceneView.RepaintAll();
                }
            }
            DrawGizmoLegend();
        }

        // ── Data files (Save Building Data / Save All / Load All) ────────────────

        private void DrawDataFiles(FloridaTerrainGenerator gen)
        {
            EditorGUILayout.LabelField("Data Files", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Both files live next to this terrain's TerrainData asset (" + gen.GetDataDirectory() + "):\n" +
                "\u2022 " + FloridaChunkFile.BuildingFileName(gen.chunkX, gen.chunkY) +
                " \u2013 building footprints only, for a building generator.\n" +
                "\u2022 " + FloridaChunkFile.ChunkFileName(gen.chunkX, gen.chunkY) +
                " \u2013 Save All: the heightmap, buildings (with all their tags), roads, waterways, " +
                "railways and nodes. Load All rebuilds the whole chunk from it without the network.\n" +
                "Both are rewritten automatically after generating terrain or fetching OSM data " +
                "(\"Auto Save Data Files\").",
                MessageType.Info);

            bool busy = gen.osmFetching || gen.isGenerating;

            using (new EditorGUI.DisabledScope(busy || gen.osmBuildings == null || gen.osmBuildings.Length == 0))
            {
                if (GUILayout.Button("\U0001F4BE  Save Building Data (.txt)", GUILayout.Height(26)))
                {
                    try { gen.SaveBuildingDataFile(); }
                    catch (Exception ex)
                    {
                        gen.osmStatusMessage = "OSM Error: save failed - " + ex.Message;
                    }
                    GUIUtility.ExitGUI();
                }
            }

            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("\U0001F4BE  Save All (terrain + roads + OSM data)", GUILayout.Height(26)))
                {
                    try
                    {
                        string path = gen.GetChunkFilePath();
                        gen.SaveChunkFile(path);
                        gen.osmStatusMessage = "Saved the whole chunk to " + path + "  \u2713";
                    }
                    catch (Exception ex)
                    {
                        gen.osmStatusMessage = "OSM Error: save all failed - " + ex.Message;
                    }
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button("\U0001F4C2  Load All (rebuild chunk from file)", GUILayout.Height(26)))
                {
                    string dir = System.IO.Path.GetFullPath(gen.GetDataDirectory());
                    string path = EditorUtility.OpenFilePanel("Load chunk data", dir, "json");
                    if (!string.IsNullOrEmpty(path))
                    {
                        try
                        {
                            gen.LoadChunkFile(path, regenerateRoads: true);
                            InvalidateCache(gen);
                            FloridaRoadGizmos.Invalidate(gen);
                        }
                        catch (Exception ex)
                        {
                            gen.osmStatusMessage = "OSM Error: load failed - " + ex.Message;
                        }
                    }
                    GUIUtility.ExitGUI();
                }
            }
        }

        // ── Road plan summary and gizmo legend ───────────────────────────────────

        private static void DrawRoadPlanSummary(FloridaTerrainGenerator gen)
        {
            // Only read a plan that already exists: solving one is not free.
            if (!gen.HasRoadPlan) return;
            var plan = gen.GetRoadPlan();
            string text =
                $"Road plan: {plan.sourceRoads} roads \u2192 {plan.roads.Count} pieces " +
                $"({plan.groundPieces} ground, {plan.bridgePieces} bridge, {plan.tunnelPieces} tunnel)\n" +
                $"{plan.crossings.Count} grade separations, {plan.unmetCrossings} short of clearance\n" +
                $"Steepest grade {plan.maxGrade * 100f:F1}%, highest lift above terrain {plan.maxLift:F1} m";
            if (plan.warnings.Count > 0)
                text += "\n\u26a0 " + plan.warnings.Count + " warning(s):\n  " +
                        string.Join("\n  ", plan.warnings.GetRange(0, Mathf.Min(5, plan.warnings.Count)));
            EditorGUILayout.HelpBox(text,
                plan.unmetCrossings > 0 || plan.warnings.Count > 0 ? MessageType.Warning : MessageType.None);
        }

        private static bool _legendOpen;

        private static void DrawGizmoLegend()
        {
            _legendOpen = EditorGUILayout.Foldout(_legendOpen, "Gizmo Legend", true);
            if (!_legendOpen) return;
            foreach (var (color, text) in FloridaRoadGizmos.Legend)
            {
                var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                EditorGUI.DrawRect(new Rect(rect.x + 4f, rect.y + 4f, 22f, rect.height - 8f), color);
                EditorGUI.LabelField(new Rect(rect.x + 32f, rect.y, rect.width - 32f, rect.height), text);
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
                "correct world-space position so adjacent chunks tile seamlessly.\n\n" +
                "Tip: \"Save All\" writes the heightmap, buildings and roads to one file next to the " +
                "terrain asset. \"Load All\" rebuilds the chunk from it without any download.",
                MessageType.None);
        }
    }
}