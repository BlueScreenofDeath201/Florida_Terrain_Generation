using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DeadEverywhere.FloridaGen
{
    /// <summary>
    /// Generates a Unity Terrain heightmap from real USGS 3DEP elevation data for
    /// 1-mile × 1-mile chunks of Florida.
    ///
    /// Coordinate system:
    ///   • Chunk (0, 0) maps to 27°53′02.27″N  82°30′42.40″W (Tampa Bay area).
    ///   • Positive X  = East,  positive Y (chunk) = North.
    ///   • 1 Unity unit = 1 metre.
    ///
    /// Place this component on a Terrain GameObject, set chunkX/chunkY, then click
    /// "Generate Terrain Chunk" in the inspector.  The terrain will be automatically
    /// resized to exactly 1 mile × 1 mile and its heightmap populated from live
    /// USGS elevation data.
    ///
    /// Data sources (tried in order):
    ///   1. USGS 3DEP WCS – single request, Arc/Info ASCII Grid text format.
    ///   2. USGS 3DEP ImageServer getSamples REST API – batched JSON.
    ///   3. USGS EPQS point query – individual requests at reduced resolution.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(Terrain))]
    public class FloridaTerrainGenerator : MonoBehaviour
    {
        // ── Origin  27°53′02.27″N  82°30′42.40″W ────────────────────────────────
        /// <summary>Latitude of world origin (chunk 0,0 SW corner).</summary>
        public const double ORIGIN_LAT = 27.0 + 53.0 / 60.0 + 2.27 / 3600.0;   // ≈ 27.883964°N
        /// <summary>Longitude of world origin (chunk 0,0 SW corner), decimal-degrees West negative.</summary>
        public const double ORIGIN_LON = -(82.0 + 30.0 / 60.0 + 42.40 / 3600.0); // ≈ −82.511778°

        public  const double MILES_TO_METERS     = 1609.344;
        private const double METERS_PER_DEG_LAT  = 111111.0;

        // ── Inspector Fields ─────────────────────────────────────────────────────
        [Header("Chunk Coordinates  (1 unit = 1 mile)")]
        [Tooltip("East-West chunk index.  0 = origin longitude.  Positive = East.")]
        public int chunkX = 0;

        [Tooltip("North-South chunk index.  0 = origin latitude.  Positive = North.")]
        public int chunkY = 0;

        [Header("Terrain Settings")]
        [Tooltip("Total terrain height range in metres.  The terrain is positioned at " +
                 "Y = -(maxTerrainHeight / 2) so it can be sculpted equally above and below " +
                 "sea level (world Y 0).  500 m gives ±250 m of range, which is more than " +
                 "enough for Florida while still providing good heightmap resolution.")]
        public float maxTerrainHeight = 500f;

        [Tooltip("Unity Terrain heightmap resolution.  Higher = more detail but slower to generate.  " +
                 "129 is a good balance for a 1-mile tile.")]
        public HeightmapResolution heightmapResolution = HeightmapResolution.R129;

        [Tooltip("Automatically move this Terrain GameObject so its SW corner aligns with the " +
                 "world-space position that corresponds to the given chunk coordinates.")]
        public bool autoPosition = true;

        [Header("Gizmo Visibility")]
        [Tooltip("Master switch – show all gizmos for this terrain in the Scene view at all times.  " +
                 "Use Tools → Florida Terrain in the main menu for a project-wide override.")]
        public bool showGizmos = true;

        [Header("OSM Buildings & Roads")]
        [Tooltip("Show building footprint outlines as gizmos in the scene view.  " +
                 "Requires OSM data to be fetched first (see editor button below).")]
        public bool showBuildingGizmos = true;

        [Tooltip("Show road centre-lines as gizmos in the scene view.  " +
                 "Requires OSM data to be fetched first (see editor button below).")]
        public bool showRoadGizmos = true;

        [Tooltip("Show the solved 3-D road profile: ground, embankment, bridge and tunnel centre-lines, " +
                 "bridge piers, crossing clearances, tunnel portals, junctions and chunk-edge cuts.")]
        public bool showRoadProfileGizmos = true;

        [Tooltip("Show road names, route numbers, lane counts and speed limits as labels.")]
        public bool showRoadLabelGizmos = true;

        [Tooltip("Show rivers, streams, canals, ditches and water bodies.")]
        public bool showWaterGizmos = true;

        [Tooltip("Show railway lines.")]
        public bool showRailwayGizmos = true;

        [Tooltip("Show traffic signals, stop signs, crossings, street lights, ramps and other road nodes.")]
        public bool showTrafficNodeGizmos = true;

        [Tooltip("Show building heights (height / levels / roof tags) as vertical lines.")]
        public bool showBuildingHeightGizmos = true;

        [Header("Road Generation")]
        [Tooltip("Maximum distance (m) between road mesh stations. Smaller follows terrain more closely.")]
        [Range(1f, 25f)] public float roadSampleSpacing = 5f;

        [Tooltip("Radius (m) of the moving average applied to the road height profile.")]
        [Range(0f, 40f)] public float roadSmoothingRadius = 8f;

        [Tooltip("Radius (m) of the moving average applied to bridge decks.  Larger gives gentler vertical curves.")]
        [Range(0f, 80f)] public float roadDeckSmoothingRadius = 20f;

        [Tooltip("Bridge decks stay level across valleys narrower than twice this distance (m), so a " +
                 "viaduct does not dip to the ground between the roads it crosses.")]
        [Range(0f, 1000f)] public float roadDeckClosingRadius = 400f;

        [Tooltip("Height (m) of the road surface above the terrain to avoid z-fighting.")]
        [Range(0f, 1f)] public float roadSurfaceOffset = 0.06f;

        [Header("Data Files")]
        [Tooltip("After terrain generation and after fetching OSM data, automatically write " +
                 "Buildings_<x>_<y>.txt and ChunkData_<x>_<y>.json next to the terrain asset.")]
        public bool autoSaveDataFiles = true;

        // ── Runtime state (read by editor) ───────────────────────────────────────
        [HideInInspector] public bool   isGenerating;
        [HideInInspector] public string statusMessage = "Ready";

        [HideInInspector] public bool          osmFetching;
        [HideInInspector] public string        osmStatusMessage = "";
        [HideInInspector] public OsmBuilding[] osmBuildings     = new OsmBuilding[0];
        [HideInInspector] public OsmRoad[]     osmRoads         = new OsmRoad[0];
        [HideInInspector] public OsmWaterway[]   osmWaterways   = new OsmWaterway[0];
        [HideInInspector] public OsmRailway[]    osmRailways    = new OsmRailway[0];
        [HideInInspector] public OsmWaterBody[]  osmWaterBodies = new OsmWaterBody[0];
        [HideInInspector] public OsmNode[]       osmNodes       = new OsmNode[0];
        [HideInInspector] public string        highwayMeshStatusMessage = "";

        // ── Enums / structs ───────────────────────────────────────────────────────
        public enum HeightmapResolution
        {
            R33  = 33,
            R65  = 65,
            R129 = 129,
            R257 = 257,
            R513 = 513
        }

        /// <summary>Geographic bounding box in decimal degrees.</summary>
        public struct GeoBounds
        {
            public double south, north, west, east;
            public override string ToString() =>
                $"S{south:F6}° N{north:F6}° W{-west:F6}° E{-east:F6}°";
        }

        /// <summary>A single building footprint as world-space XZ points.</summary>
        [System.Serializable]
        public class OsmBuilding
        {
            /// <summary>Polygon vertices in world-space XZ (metres from world origin).</summary>
            public Vector2[] footprint;
            /// <summary>OSM building tag value ("yes", "house", "apartments", …).</summary>
            public string type;
            /// <summary>OSM building:levels tag (0 if absent).</summary>
            public int levels;
            /// <summary>OpenStreetMap way id.</summary>
            public long osmId;
            public string name = "";
            /// <summary>OSM <c>height</c> tag in metres (0 if absent).</summary>
            public float height;
            /// <summary>OSM <c>min_height</c> tag in metres (0 if absent) – the bottom of a building part.</summary>
            public float minHeight;
            /// <summary>OSM <c>building:min_level</c> tag (0 if absent).</summary>
            public int minLevel;
            /// <summary>OSM <c>roof:shape</c> tag ("flat", "gabled", "hipped", …).</summary>
            public string roofShape = "";
            /// <summary>OSM <c>roof:levels</c> tag (0 if absent).</summary>
            public int roofLevels;
            /// <summary>OSM <c>roof:height</c> tag in metres (0 if absent).</summary>
            public float roofHeight;
            /// <summary>OSM <c>building:material</c> tag.</summary>
            public string material = "";
            /// <summary>OSM <c>building:colour</c> (or <c>colour</c>) tag.</summary>
            public string colour = "";
            /// <summary>First of the amenity / shop / tourism / office / leisure tags (what the building is used for).</summary>
            public string usage = "";
        }

        /// <summary>A road centre-line as world-space XZ points.</summary>
        [System.Serializable]
        public class OsmRoad
        {
            /// <summary>Polyline vertices in world-space XZ (metres from world origin).</summary>
            public Vector2[] points;
            /// <summary>OpenStreetMap highway tag value (e.g. "residential", "primary").</summary>
            public string highway;
            /// <summary>True when the OSM way carries a <c>bridge=yes</c> (or similar) tag.
            /// The deck is lifted over what it crosses by <see cref="FloridaRoadTranslator"/>.</summary>
            public bool isBridge;
            /// <summary>True when the OSM way carries a <c>tunnel=yes</c> (or similar) tag.
            /// Tunnels are meshed as a closed tube that follows the terrain surface.</summary>
            public bool isTunnel;
            /// <summary>
            /// OSM <c>layer</c> tag value — the number of levels above (+) or below (−) grade this
            /// way sits relative to the surrounding ground-level roads.
            /// 0 = at grade, 1 = first level above grade (typical overpass), 2 = flyover above
            /// another bridge, −1 = first level underground.
            /// </summary>
            public int layer;
            /// <summary>OpenStreetMap way id.</summary>
            public long osmId;
            public string name = "";
            /// <summary>OSM <c>ref</c> tag (route number, e.g. "I 275").</summary>
            public string refTag = "";
            /// <summary>OSM <c>lanes</c> tag (0 if absent).</summary>
            public int lanes;
            /// <summary>OSM <c>width</c> tag in metres (0 if absent).</summary>
            public float width;
            /// <summary>OSM <c>maxspeed</c> tag as written (e.g. "45 mph").</summary>
            public string maxSpeed = "";
            /// <summary>OSM <c>surface</c> tag.</summary>
            public string surface = "";
            /// <summary>1 = one-way along the points, −1 = one-way against them, 0 = two-way.</summary>
            public int oneway;
            /// <summary>OSM <c>junction</c> tag ("roundabout", …).</summary>
            public string junction = "";
            /// <summary><c>bridge:structure</c> (or the <c>bridge</c> value when it is not just "yes").</summary>
            public string bridgeType = "";
            /// <summary>The <c>tunnel</c> tag value ("yes", "building_passage", …).</summary>
            public string tunnelType = "";
            public bool lit;
            /// <summary>OSM <c>incline</c> tag as written.</summary>
            public string incline = "";
            /// <summary><c>embankment=yes</c>.</summary>
            public bool hasEmbankment;
            /// <summary><c>cutting=yes</c>.</summary>
            public bool hasCutting;
        }

        /// <summary>A river, stream, canal, ditch or drain centre-line.</summary>
        [System.Serializable]
        public class OsmWaterway
        {
            public Vector2[] points;
            /// <summary>OSM <c>waterway</c> tag value.</summary>
            public string type = "";
            public string name = "";
            /// <summary>OSM <c>width</c> tag in metres (0 if absent).</summary>
            public float width;
            /// <summary>Culvert or tunnel: the water runs underground and nothing needs to span it.</summary>
            public bool underground;
        }

        /// <summary>A railway line.</summary>
        [System.Serializable]
        public class OsmRailway
        {
            public Vector2[] points;
            /// <summary>OSM <c>railway</c> tag value ("rail", "light_rail", "tram", …).</summary>
            public string type = "";
            public string name = "";
            public bool isBridge;
            public bool isTunnel;
            public int layer;
        }

        /// <summary>A lake, pond, reservoir or coastline way.</summary>
        [System.Serializable]
        public class OsmWaterBody
        {
            public Vector2[] outline;
            /// <summary>OSM <c>water</c> tag ("lake", "pond", …) or "water" / "coastline".</summary>
            public string type = "";
            public string name = "";
            /// <summary>An open coastline way (land on its left) rather than a closed water polygon.</summary>
            public bool isCoastline;
        }

        /// <summary>A point feature on or beside the road network.</summary>
        [System.Serializable]
        public class OsmNode
        {
            /// <summary>World-space XZ.</summary>
            public Vector2 position;
            /// <summary>"traffic_signals", "stop", "give_way", "crossing", "street_lamp", "motorway_junction",
            /// "mini_roundabout", "turning_circle", "speed_camera", "bus_stop" or "level_crossing".</summary>
            public string kind = "";
            public string name = "";
            /// <summary>OSM <c>ref</c> tag (e.g. a motorway exit number).</summary>
            public string refTag = "";
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the geographic bounding box (WGS-84 decimal degrees) for the
        /// given chunk coordinate pair.
        /// </summary>
        public GeoBounds CalculateChunkBounds(int cx, int cy)
        {
            double south = ORIGIN_LAT + cy * MILES_TO_METERS / METERS_PER_DEG_LAT;
            double west  = ORIGIN_LON + cx * MILES_TO_METERS / MetersPerDegreeLon(south);
            double north = south + MILES_TO_METERS / METERS_PER_DEG_LAT;
            double east  = west  + MILES_TO_METERS / MetersPerDegreeLon(south);
            return new GeoBounds { south = south, north = north, west = west, east = east };
        }

        /// <summary>
        /// Returns the Unity world-space position (metres) of the SW corner of the
        /// given chunk.  X = East, Z = North.  Y is set to <c>-maxTerrainHeight / 2</c>
        /// so that sea level (elevation 0) maps to world Y 0 and the terrain can be
        /// sculpted both above and below that baseline.
        /// </summary>
        public Vector3 ChunkWorldPosition(int cx, int cy) =>
            new Vector3((float)(cx * MILES_TO_METERS), -maxTerrainHeight * 0.5f, (float)(cy * MILES_TO_METERS));

        /// <summary>
        /// Turns every gizmo group of this chunk on or off: the master switch and each overlay
        /// (buildings, roads, road profile, labels, water, railways, nodes, building heights).
        /// </summary>
        public void SetAllGizmos(bool on)
        {
            showGizmos = showBuildingGizmos = showRoadGizmos = showRoadProfileGizmos =
                showRoadLabelGizmos = showWaterGizmos = showRailwayGizmos =
                showTrafficNodeGizmos = showBuildingHeightGizmos = on;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>
        /// Called once for every building footprint when OSM data is fetched.
        /// Override this method (or hook into it from a subclass) to spawn actual
        /// building geometry at generation time.  It is never called during playback
        /// or scene repaints — only at the moment the OSM data arrives.
        /// </summary>
        /// <param name="footprintPoints">
        /// World-space XZ polygon vertices — the same points used to draw the
        /// building gizmo in the Scene view.  The polygon is closed (last vertex
        /// connects back to the first).
        /// </param>
        protected virtual void OnBuildingFootprintGenerated(Vector2[] footprintPoints) { }

        // ── Editor-only generation methods ────────────────────────────────────────
#if UNITY_EDITOR

        /// <summary>
        /// Downloads real USGS elevation data for chunk (chunkX, chunkY) and applies
        /// it to the attached Terrain component.  Call from the custom inspector.
        /// </summary>
        public void GenerateChunk()
        {
            if (isGenerating) return;

            isGenerating  = true;
            statusMessage = $"Requesting elevation data for chunk ({chunkX}, {chunkY})…";

            var bounds = CalculateChunkBounds(chunkX, chunkY);
            int res    = (int)heightmapResolution;

            try
            {
                EditorUtility.DisplayProgressBar(
                    "Florida Terrain Generator", statusMessage, 0.05f);

                float[,] elevations = DownloadElevationGrid(bounds, res);

                if (elevations == null)
                {
                    statusMessage = "Download failed – check the Console for details.";
                    return;
                }

                EditorUtility.DisplayProgressBar(
                    "Florida Terrain Generator", "Applying heightmap to terrain…", 0.92f);

                ApplyToTerrain(elevations);

                if (autoPosition)
                    transform.position = ChunkWorldPosition(chunkX, chunkY);

                InvalidateRoadPlan();
                statusMessage = $"Chunk ({chunkX}, {chunkY}) generated  \u2713";
                if (autoSaveDataFiles)
                {
                    string saved = AutoSaveDataFiles();
                    statusMessage += "  " + saved;
                }
            }
            catch (Exception ex)
            {
                statusMessage = $"Error: {ex.Message}";
                Debug.LogError($"[FloridaTerrainGenerator] {ex}");
                EditorUtility.DisplayDialog(
                    "Florida Terrain Generator – Error", ex.Message, "OK");
            }
            finally
            {
                isGenerating = false;
                EditorUtility.ClearProgressBar();
                EditorUtility.SetDirty(this);
            }
        }

        // ── OSM data fetching ─────────────────────────────────────────────────────

        private const int OsmRequestTimeoutSeconds = 90;
        private const string OsmUrl = "https://overpass-api.de/api/interpreter";

        /// <summary>Overpass bounding box "(south,west,north,east)" with an invariant decimal separator.</summary>
        private static string OsmBBox(GeoBounds b) =>
            "(" + b.south.ToString("F7", Inv) + "," + b.west.ToString("F7", Inv) + "," +
            b.north.ToString("F7", Inv) + "," + b.east.ToString("F7", Inv) + ")";

        /// <summary>
        /// Downloads building footprints, road centre-lines (with their lane, speed, bridge and tunnel
        /// tags) and the features that shape them - waterways, water bodies, railways and traffic
        /// nodes - from the OpenStreetMap Overpass API for chunk (chunkX, chunkY) and stores the
        /// results for scene-view gizmo display and road generation.  No API key required.
        /// </summary>
        public void FetchOsmData()
        {
            if (osmFetching || isGenerating) return;

            osmFetching      = true;
            osmStatusMessage = $"Fetching OSM data for chunk ({chunkX}, {chunkY})…";

            var bounds = CalculateChunkBounds(chunkX, chunkY);

            try
            {
                EditorUtility.DisplayProgressBar(
                    "OSM Data", "Querying OpenStreetMap Overpass API…", 0.1f);

                // Build Overpass QL query – request all ways tagged as building
                // or highway with inline geometry (out geom qt).  Embedding geometry
                // directly in each way element avoids the fragile two-pass node-ID
                // resolution approach and returns a smaller, simpler response.
                string box = OsmBBox(bounds);
                string q =
                    "[out:json][timeout:60];" +
                    "(" +
                    $"way[\"building\"]{box};" +
                    $"way[\"highway\"]{box};" +
                    ");out geom qt;";

                string body = "data=" + Uri.EscapeDataString(q);

                EditorUtility.DisplayProgressBar("OSM Data", "Downloading…", 0.35f);
                string json = SyncHttpPost(OsmUrl, body, OsmRequestTimeoutSeconds);

                // Log a brief excerpt so failures are diagnosable in the Console.
                const int OsmResponsePreviewLength = 300;
                Debug.Log($"[FloridaTerrainGenerator] OSM response ({json.Length} chars): " +
                          (json.Length > 0 ? json.Substring(0, Mathf.Min(OsmResponsePreviewLength, json.Length)) : "(empty)"));

                EditorUtility.DisplayProgressBar("OSM Data", "Parsing response…", 0.65f);
                ParseOsmJson(json);

                EditorUtility.DisplayProgressBar("OSM Data", "Downloading waterways, railways and road nodes…", 0.75f);
                bool extrasOk = FetchOsmExtras(box);
                InvalidateRoadPlan();

                string status =
                    osmBuildings.Length == 0 && osmRoads.Length == 0
                        ? "OSM: no data found for this chunk"
                        : $"OSM: {osmBuildings.Length} buildings, {osmRoads.Length} roads, " +
                          $"{osmWaterways.Length} waterways, {osmRailways.Length} railways, " +
                          $"{osmNodes.Length} nodes" + (extrasOk ? "" : " (extras unavailable)") + "  \u2713";
                osmStatusMessage = status;

                if (autoSaveDataFiles)
                {
                    string saved = AutoSaveDataFiles();
                    osmStatusMessage = status + "  " + saved;
                }
            }
            catch (Exception ex)
            {
                osmStatusMessage = $"OSM Error: {ex.Message}";
                Debug.LogError($"[FloridaTerrainGenerator] OSM fetch failed: {ex}");
            }
            finally
            {
                osmFetching = false;
                EditorUtility.ClearProgressBar();
                EditorUtility.SetDirty(this);
            }
        }

        /// <summary>
        /// Second Overpass request: rivers, streams and canals (bridges must clear them), lakes and the
        /// coastline, railways, and point features along the roads.  Failure is not fatal - the roads
        /// and buildings are already stored - so this returns false instead of throwing.
        /// </summary>
        private bool FetchOsmExtras(string box)
        {
            osmWaterways   = new OsmWaterway[0];
            osmWaterBodies = new OsmWaterBody[0];
            osmRailways    = new OsmRailway[0];
            osmNodes       = new OsmNode[0];
            try
            {
                string q =
                    "[out:json][timeout:60];(" +
                    "way[\"waterway\"~\"^(river|stream|canal|ditch|drain|tidal_channel)$\"]" + box + ";" +
                    "way[\"natural\"~\"^(water|coastline)$\"]" + box + ";" +
                    "way[\"railway\"~\"^(rail|light_rail|subway|tram|narrow_gauge|monorail|preserved)$\"]" + box + ";" +
                    "node[\"highway\"~\"^(traffic_signals|stop|give_way|crossing|street_lamp|motorway_junction|" +
                    "mini_roundabout|turning_circle|speed_camera|bus_stop)$\"]" + box + ";" +
                    "node[\"railway\"~\"^(level_crossing|crossing)$\"]" + box + ";" +
                    ");out geom qt;";
                string json = SyncHttpPost(OsmUrl, "data=" + Uri.EscapeDataString(q), OsmRequestTimeoutSeconds);
                ParseOsmExtras(json);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FloridaTerrainGenerator] OSM extras (waterways, railways, nodes) failed: {ex.Message}");
                return false;
            }
        }

        // ── Download orchestration ────────────────────────────────────────────────

        private float[,] DownloadElevationGrid(GeoBounds b, int res)
        {
            // Source 1 ── ArcGIS ImageServer getSamples (batched JSON REST)
            // Most reliable source: standard ArcGIS REST endpoint, no OGC quirks.
            EditorUtility.DisplayProgressBar(
                "Florida Terrain Generator", "Requesting elevation via ArcGIS getSamples…", 0.10f);
            try
            {
                return FetchViaGetSamples(b, res);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FloridaTerrainGenerator] getSamples failed: {ex.Message}");
            }

            // Source 2 ── USGS 3DEP WCS (Arc/Info ASCII Grid, single HTTP request)
            // Coverage name is discovered dynamically from GetCapabilities to avoid
            // hardcoding a name that may vary across service deployments.
            EditorUtility.DisplayProgressBar(
                "Florida Terrain Generator", "Trying USGS 3DEP WCS…", 0.30f);
            try
            {
                return FetchViaWCS(b, res);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FloridaTerrainGenerator] WCS failed: {ex.Message}");
            }

            // Source 3 ── USGS EPQS individual point queries at reduced resolution
            EditorUtility.DisplayProgressBar(
                "Florida Terrain Generator",
                "Falling back to USGS EPQS point queries (slow)…", 0.45f);
            try
            {
                return FetchViaEPQS(b, res);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[FloridaTerrainGenerator] All elevation sources failed. Last error: {ex.Message}");
                return null;
            }
        }

        // ── Source 2: USGS 3DEP WCS ───────────────────────────────────────────────

        private const string WCS_BASE_URL =
            "https://elevation.nationalmap.gov/arcgis/services/" +
            "3DEPElevation/ImageServer/WCSServer";

        private float[,] FetchViaWCS(GeoBounds b, int res)
        {
            // Discover the real coverage name from GetCapabilities so we are not
            // coupled to a hardcoded name that may differ across service deployments.
            string coverageName = GetWCSCoverageName();

            string url =
                WCS_BASE_URL +
                "?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
                $"&COVERAGE={Uri.EscapeDataString(coverageName)}" +
                "&CRS=EPSG:4326" +
                $"&BBOX={b.west:F7},{b.south:F7},{b.east:F7},{b.north:F7}" +
                $"&WIDTH={res}&HEIGHT={res}" +
                "&FORMAT=image/x-aaigrid";

            string body = SyncHttpGet(url, timeoutSeconds: 90);

            // Guard against XML/HTML error responses
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("<") || trimmed.StartsWith("<?"))
                throw new Exception(
                    "WCS returned XML (service error). Response: " +
                    body.Substring(0, Math.Min(200, body.Length)));

            return ParseAAIGrid(body, res);
        }

        /// <summary>
        /// Queries the WCS GetCapabilities document and returns the identifier of
        /// the first available coverage.  Falls back to <c>"DEMSmoothed"</c> if the
        /// capabilities document cannot be parsed.
        /// </summary>
        private static string GetWCSCoverageName()
        {
            const string OFFERING_TAG = "CoverageOfferingBrief";
            const string NAME_OPEN    = "<name>";
            const string NAME_CLOSE   = "</name>";

            try
            {
                string caps = SyncHttpGet(
                    WCS_BASE_URL + "?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCapabilities",
                    timeoutSeconds: 30);

                // WCS 1.0.0 capabilities: look for <name> inside <CoverageOfferingBrief>
                int briefIdx = caps.IndexOf(OFFERING_TAG, StringComparison.OrdinalIgnoreCase);
                if (briefIdx < 0) return "DEMSmoothed";

                int nameOpen = caps.IndexOf(NAME_OPEN, briefIdx, StringComparison.OrdinalIgnoreCase);
                if (nameOpen < 0) return "DEMSmoothed";

                int start     = nameOpen + NAME_OPEN.Length;
                int nameClose = caps.IndexOf(NAME_CLOSE, start, StringComparison.OrdinalIgnoreCase);
                if (nameClose < 0) return "DEMSmoothed";

                string name = caps.Substring(start, nameClose - start).Trim();
                return string.IsNullOrEmpty(name) ? "DEMSmoothed" : name;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[FloridaTerrainGenerator] WCS GetCapabilities failed, " +
                    $"using 'DEMSmoothed' as coverage name: {ex.Message}");
                return "DEMSmoothed";
            }
        }

        // ── Source 1: ArcGIS ImageServer getSamples ───────────────────────────────

        private float[,] FetchViaGetSamples(GeoBounds b, int res)
        {
            const string ENDPOINT =
                "https://elevation.nationalmap.gov/arcgis/rest/services/" +
                "3DEPElevation/ImageServer/getSamples";
            const int BATCH_SIZE = 500;

            // Build ordered list of (row, col, lat, lon) grid points
            var points = new List<(int row, int col, double lat, double lon)>(res * res);
            for (int row = 0; row < res; row++)
                for (int col = 0; col < res; col++)
                {
                    double lat = res > 1
                        ? b.south + row * (b.north - b.south) / (res - 1)
                        : b.south;
                    double lon = res > 1
                        ? b.west  + col * (b.east  - b.west)  / (res - 1)
                        : b.west;
                    points.Add((row, col, lat, lon));
                }

            float[,] result = new float[res, res];
            int missing = 0;

            for (int start = 0; start < points.Count; start += BATCH_SIZE)
            {
                int end = Math.Min(start + BATCH_SIZE, points.Count);
                float pct = 0.10f + 0.55f * (float)start / points.Count;

                EditorUtility.DisplayProgressBar(
                    "Florida Terrain Generator",
                    $"Downloading samples {start + 1}–{end} / {points.Count}…", pct);

                // Build esriGeometryMultipoint JSON
                var sb = new StringBuilder("{\"points\":[");
                for (int i = start; i < end; i++)
                {
                    if (i > start) sb.Append(',');
                    sb.Append($"[{points[i].lon:F7},{points[i].lat:F7}]");
                }
                sb.Append("]}");

                string postBody =
                    "geometry="    + Uri.EscapeDataString(sb.ToString()) +
                    "&geometryType=esriGeometryMultipoint" +
                    "&returnFirstValueOnly=false" +
                    "&interpolation=RSP_BilinearInterpolation" +
                    "&f=json";

                string json = SyncHttpPost(ENDPOINT, postBody, timeoutSeconds: 60);
                missing += (end - start) - ParseGetSamplesJson(json, points, start, end, result);
            }

            if (missing > 0)
                Debug.LogWarning(
                    $"[FloridaTerrainGenerator] getSamples returned no data for {missing} of {points.Count} " +
                    "points; they were left at sea level (0 m).");

            return result;
        }

        /// <summary>
        /// Writes the elevations of one getSamples response into <paramref name="result"/> and returns
        /// how many of the <c>end - start</c> requested points received a real value.
        /// <para>
        /// The request uses <c>returnFirstValueOnly=false</c>, so where rasters overlap the server returns
        /// one sample per raster for the same point.  Counting "value" tokens would then shift every later
        /// height in the batch, so samples are matched to points by their <c>locationId</c> (the index in the
        /// request) and the first usable value per point wins.  Responses without <c>locationId</c> fall
        /// back to one sample per point, in order.  NoData and sentinel values never count as a value.
        /// </para>
        /// </summary>
        internal static int ParseGetSamplesJson(
            string json,
            List<(int row, int col, double lat, double lon)> points,
            int start, int end,
            float[,] result)
        {
            var samples = ReadGetSamples(json);

            bool byId = samples.Count > 0;
            foreach (var s in samples)
                if (s.id == NoLocationId) { byId = false; break; }

            var got = new bool[end - start];
            int received = 0, order = 0;
            foreach (var s in samples)
            {
                int index = byId ? s.id : order++;
                if (index < 0 || index >= got.Length || got[index]) continue;

                if (!float.TryParse(s.value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float val) || float.IsNaN(val) || float.IsInfinity(val) || val <= NoDataElevation)
                    continue;

                var p = points[start + index];
                result[p.row, p.col] = val;
                got[index] = true;
                received++;
            }
            return received;
        }

        private const int NoLocationId = int.MinValue;

        /// <summary>
        /// Reads <c>(locationId, value)</c> from every object of the <c>"samples"</c> array.
        /// <c>id</c> is <see cref="NoLocationId"/> when the sample has no <c>locationId</c>; <c>value</c> is the raw text
        /// (a number, or something like "NoData").  Only top-level keys of a sample are read, so
        /// nested objects cannot be mistaken for it.  No JSON library dependency.
        /// </summary>
        private static List<(int id, string value)> ReadGetSamples(string json)
        {
            var list = new List<(int id, string value)>();
            if (string.IsNullOrEmpty(json)) return list;

            int key = json.IndexOf("\"samples\"", StringComparison.Ordinal);
            int i   = key < 0 ? -1 : json.IndexOf('[', key);
            if (i < 0) return list;

            i++;
            while (i < json.Length)
            {
                while (i < json.Length && json[i] != '{' && json[i] != ']') i++;
                if (i >= json.Length || json[i] == ']') break;

                int end = SkipJsonValue(json, i);
                ReadGetSample(json, i, end, out int id, out string value);
                list.Add((id, value));
                i = end;
            }
            return list;
        }

        private static void ReadGetSample(string json, int start, int end, out int id, out string value)
        {
            id    = NoLocationId;
            value = "";

            int i = start + 1;
            while (i < end)
            {
                while (i < end && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                if (i >= end || json[i] != '"') break;

                int keyEnd = SkipJsonValue(json, i);
                string key = json.Substring(i + 1, Math.Max(0, keyEnd - i - 2));
                i = keyEnd;
                while (i < end && (char.IsWhiteSpace(json[i]) || json[i] == ':')) i++;
                if (i >= end) break;

                int valueEnd = SkipJsonValue(json, i);
                if (key == "locationId" || key == "value")
                {
                    string text = json[i] == '"'
                        ? json.Substring(i + 1, Math.Max(0, valueEnd - i - 2))
                        : json.Substring(i, valueEnd - i).Trim();
                    if (key == "value") value = text;
                    else if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                        id = parsed;
                }
                i = valueEnd;
            }
        }

        /// <summary>Returns the index just past the JSON string, object, array or scalar that starts at <paramref name="i"/>.</summary>
        private static int SkipJsonValue(string json, int i)
        {
            if (i >= json.Length) return json.Length;

            if (json[i] == '"')
            {
                for (i++; i < json.Length; i++)
                {
                    if (json[i] == '\\') i++;
                    else if (json[i] == '"') return i + 1;
                }
                return json.Length;
            }

            if (json[i] == '{' || json[i] == '[')
            {
                int depth = 0;
                while (i < json.Length)
                {
                    char c = json[i];
                    if (c == '"') { i = SkipJsonValue(json, i); continue; }
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') { depth--; if (depth == 0) return i + 1; }
                    i++;
                }
                return json.Length;
            }

            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && !char.IsWhiteSpace(json[i])) i++;
            return i;
        }

        // ── Source 3: USGS EPQS individual point queries ──────────────────────────

        private float[,] FetchViaEPQS(GeoBounds b, int res)
        {
            // Sample at reduced resolution and bilinearly upsample to avoid
            // hundreds of HTTP requests while still capturing the terrain shape.
            const int SAMPLE_RES = 17;
            float[,] sparse = new float[SAMPLE_RES, SAMPLE_RES];
            int total = SAMPLE_RES * SAMPLE_RES;
            int done  = 0;

            using var wc = new System.Net.WebClient();
            wc.Headers["User-Agent"] = "UnityTerrainGenerator/1.0";

            for (int row = 0; row < SAMPLE_RES; row++)
            {
                for (int col = 0; col < SAMPLE_RES; col++)
                {
                    double lat = b.south + row * (b.north - b.south) / (SAMPLE_RES - 1);
                    double lon = b.west  + col * (b.east  - b.west)  / (SAMPLE_RES - 1);

                    done++;
                    EditorUtility.DisplayProgressBar(
                        "Florida Terrain Generator",
                        $"EPQS fallback: point {done}/{total}…",
                        0.45f + 0.45f * (float)done / total);

                    try
                    {
                        string url =
                            "https://epqs.nationalmap.gov/v1/json" +
                            $"?x={lon:F7}&y={lat:F7}&wkid=4326&units=Meters&includeDate=false";
                        string response = wc.DownloadString(url);
                        sparse[row, col] = ParseEPQSValue(response);
                    }
                    catch (Exception epqsEx)
                    {
                        // Log individual point failures at warning level and keep
                        // the default 0 (sea level) for that sample.
                        Debug.LogWarning(
                            $"[FloridaTerrainGenerator] EPQS point ({lat:F5},{lon:F5}) failed: " +
                            epqsEx.Message);
                    }
                }
            }

            return BilinearUpsample(sparse, SAMPLE_RES, SAMPLE_RES, res);
        }

        private static float ParseEPQSValue(string json)
        {
            // Response: {"value":"35.19"} or {"value":35.19}
            int vi = json.IndexOf("\"value\"", StringComparison.Ordinal);
            if (vi < 0) return 0f;

            int colon = json.IndexOf(':', vi + 7);
            if (colon < 0) return 0f;

            int s = colon + 1;
            while (s < json.Length && (json[s] == ' ' || json[s] == '"')) s++;

            int e = s;
            while (e < json.Length &&
                   json[e] != '"' && json[e] != ',' &&
                   json[e] != '}' && json[e] != ']')
                e++;

            string v = json.Substring(s, e - s).Trim();
            return float.TryParse(v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float val) ? SanitizeElevation(val) : 0f;
        }

        // ── OSM Overpass JSON parsing ─────────────────────────────────────────────

        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// Splits an Overpass API JSON response into its <c>elements</c> (one JSON object
        /// each).  Returns an empty list when the response has none.
        /// </summary>
        private static List<string> OsmReadElements(string json)
        {
            var none = new List<string>();
            int elemKey = json.IndexOf("\"elements\"", StringComparison.Ordinal);
            if (elemKey < 0)
            {
                Debug.LogWarning($"[FloridaTerrainGenerator] OSM response ({json.Length} chars) has no 'elements' key.");
                return none;
            }
            int arrOpen = json.IndexOf('[', elemKey + 10);
            if (arrOpen < 0) return none;
            int arrClose = OsmFindMatchingBracket(json, arrOpen, '[', ']');
            if (arrClose < 0) return none;
            return OsmExtractTopLevelObjects(json, arrOpen + 1, arrClose);
        }

        /// <summary>
        /// Parses an Overpass API JSON response (using <c>out geom qt;</c> format)
        /// and populates <see cref="osmBuildings"/> and <see cref="osmRoads"/>
        /// with world-space XZ coordinates.  Each way element contains an embedded
        /// <c>"geometry"</c> array of <c>{"lat":…,"lon":…}</c> objects, so no
        /// separate node-ID resolution pass is required.
        /// </summary>
        internal void ParseOsmJson(string json)
        {
            osmBuildings = new OsmBuilding[0];
            osmRoads     = new OsmRoad[0];

            var buildings = new List<OsmBuilding>();
            var roads     = new List<OsmRoad>();

            var elements = OsmReadElements(json);
            Debug.Log($"[FloridaTerrainGenerator] OSM elements extracted: {elements.Count}");

            foreach (string el in elements)
            {
                // Only process way elements
                if (!OsmReadString(el, "type", out string t) || t != "way") continue;

                var tags = OsmReadTags(el);
                tags.TryGetValue("building", out string bldgTag);
                tags.TryGetValue("highway",  out string hw);
                bool isBuilding = !string.IsNullOrEmpty(bldgTag) && bldgTag != "no";
                bool isRoad     = !string.IsNullOrEmpty(hw);
                if (!isBuilding && !isRoad) continue;

                // Read geometry embedded directly in the way (out geom qt format)
                Vector2[] pts = OsmReadGeometry(el);
                if (pts.Length < 2) continue;

                OsmReadDouble(el, "id", out double id);

                if (isBuilding)
                {
                    var bldg = new OsmBuilding
                    {
                        footprint  = pts,
                        type       = bldgTag,
                        levels     = OsmTagInt(tags, "building:levels"),
                        osmId      = (long)id,
                        name       = OsmTag(tags, "name"),
                        height     = OsmTagLength(tags, "height"),
                        minHeight  = OsmTagLength(tags, "min_height"),
                        minLevel   = OsmTagInt(tags, "building:min_level"),
                        roofShape  = OsmTag(tags, "roof:shape"),
                        roofLevels = OsmTagInt(tags, "roof:levels"),
                        roofHeight = OsmTagLength(tags, "roof:height"),
                        material   = OsmTag(tags, "building:material"),
                        colour     = OsmFirstTag(tags, "building:colour", "colour"),
                        usage      = OsmFirstTag(tags, "amenity", "shop", "tourism", "office", "leisure", "building:use")
                    };
                    buildings.Add(bldg);
                    OnBuildingFootprintGenerated(bldg.footprint);
                }
                else
                {
                    tags.TryGetValue("bridge", out string bridgeTag);
                    tags.TryGetValue("tunnel", out string tunnelTag);
                    string junction = OsmTag(tags, "junction");
                    string onewayTag = OsmTag(tags, "oneway");
                    int oneway = onewayTag == "-1" || onewayTag == "reverse" ? -1
                               : onewayTag == "yes" || onewayTag == "true" || onewayTag == "1" ? 1 : 0;
                    if (oneway == 0 && junction == "roundabout") oneway = 1;

                    string bridgeStructure = OsmTag(tags, "bridge:structure");
                    roads.Add(new OsmRoad
                    {
                        points   = pts,
                        highway  = hw ?? "",
                        isBridge = !string.IsNullOrEmpty(bridgeTag) && bridgeTag != "no",
                        // tunnel=culvert on a way marks water running under it, not a road tunnel.
                        isTunnel = !string.IsNullOrEmpty(tunnelTag) && tunnelTag != "no" && tunnelTag != "culvert",
                        layer    = OsmTagInt(tags, "layer"),
                        osmId    = (long)id,
                        name     = OsmTag(tags, "name"),
                        refTag   = OsmTag(tags, "ref"),
                        lanes    = OsmTagInt(tags, "lanes"),
                        width    = OsmTagLength(tags, "width"),
                        maxSpeed = OsmTag(tags, "maxspeed"),
                        surface  = OsmTag(tags, "surface"),
                        oneway   = oneway,
                        junction = junction,
                        bridgeType = bridgeStructure.Length > 0 ? bridgeStructure
                                   : bridgeTag != null && bridgeTag != "yes" && bridgeTag != "no" ? bridgeTag : "",
                        tunnelType = tunnelTag != null && tunnelTag != "no" ? tunnelTag : "",
                        lit      = OsmTag(tags, "lit") == "yes",
                        incline  = OsmTag(tags, "incline"),
                        hasEmbankment = OsmTag(tags, "embankment") == "yes",
                        hasCutting    = OsmTag(tags, "cutting") == "yes"
                    });
                }
            }

            osmBuildings = buildings.ToArray();
            osmRoads     = roads.ToArray();
        }

        /// <summary>
        /// Parses the response of the second (extras) Overpass query: waterways, water bodies,
        /// railways and point features.  Replaces <see cref="osmWaterways"/>,
        /// <see cref="osmWaterBodies"/>, <see cref="osmRailways"/> and <see cref="osmNodes"/>.
        /// </summary>
        internal void ParseOsmExtras(string json)
        {
            var waterways = new List<OsmWaterway>();
            var bodies    = new List<OsmWaterBody>();
            var railways  = new List<OsmRailway>();
            var nodes     = new List<OsmNode>();

            foreach (string el in OsmReadElements(json))
            {
                if (!OsmReadString(el, "type", out string t)) continue;
                var tags = OsmReadTags(el);

                if (t == "node")
                {
                    string kind = OsmFirstTag(tags, "highway", "railway");
                    if (kind.Length == 0) continue;
                    if (kind == "crossing" && tags.ContainsKey("railway")) kind = "level_crossing";
                    if (!OsmReadDouble(el, "lat", out double lat) || !OsmReadDouble(el, "lon", out double lon)) continue;
                    nodes.Add(new OsmNode
                    {
                        position = GeoToWorldXZ(lat, lon),
                        kind     = kind,
                        name     = OsmTag(tags, "name"),
                        refTag   = OsmTag(tags, "ref")
                    });
                    continue;
                }
                if (t != "way") continue;

                Vector2[] pts = OsmReadGeometry(el);
                if (pts.Length < 2) continue;

                string natural = OsmTag(tags, "natural");
                string waterway = OsmTag(tags, "waterway");
                string railway = OsmTag(tags, "railway");
                if (natural == "water" || natural == "coastline")
                {
                    bodies.Add(new OsmWaterBody
                    {
                        outline     = pts,
                        type        = natural == "water" ? (OsmTag(tags, "water").Length > 0 ? OsmTag(tags, "water") : "water") : "coastline",
                        name        = OsmTag(tags, "name"),
                        isCoastline = natural == "coastline"
                    });
                }
                else if (waterway.Length > 0)
                {
                    string tunnel = OsmTag(tags, "tunnel");
                    waterways.Add(new OsmWaterway
                    {
                        points      = pts,
                        type        = waterway,
                        name        = OsmTag(tags, "name"),
                        width       = OsmTagLength(tags, "width"),
                        underground = (tunnel.Length > 0 && tunnel != "no") || OsmTag(tags, "covered") == "yes"
                    });
                }
                else if (railway.Length > 0)
                {
                    string bridge = OsmTag(tags, "bridge");
                    string tunnel = OsmTag(tags, "tunnel");
                    railways.Add(new OsmRailway
                    {
                        points   = pts,
                        type     = railway,
                        name     = OsmTag(tags, "name"),
                        isBridge = bridge.Length > 0 && bridge != "no",
                        isTunnel = tunnel.Length > 0 && tunnel != "no",
                        layer    = OsmTagInt(tags, "layer")
                    });
                }
            }

            osmWaterways   = waterways.ToArray();
            osmWaterBodies = bodies.ToArray();
            osmRailways    = railways.ToArray();
            osmNodes       = nodes.ToArray();
        }

        // ── Tag helpers ───────────────────────────────────────────────────────────

        private static string OsmTag(Dictionary<string, string> tags, string key) =>
            tags.TryGetValue(key, out string v) && v != null ? v : "";

        private static string OsmFirstTag(Dictionary<string, string> tags, params string[] keys)
        {
            foreach (string k in keys)
                if (tags.TryGetValue(k, out string v) && !string.IsNullOrEmpty(v) && v != "no") return v;
            return "";
        }

        /// <summary>Leading integer of a tag ("2", "-1", "2;3" → 2), 0 when absent or not a number.</summary>
        private static int OsmTagInt(Dictionary<string, string> tags, string key)
        {
            if (!tags.TryGetValue(key, out string v) || string.IsNullOrEmpty(v)) return 0;
            int i = 0;
            while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
            int start = i;
            if (i < v.Length && (v[i] == '-' || v[i] == '+')) i++;
            while (i < v.Length && v[i] >= '0' && v[i] <= '9') i++;
            return int.TryParse(v.Substring(start, i - start), System.Globalization.NumberStyles.Integer, Inv, out int r) ? r : 0;
        }

        /// <summary>
        /// A length tag in metres: "12", "12.5", "12 m", "40 ft" and 12'6" are understood.
        /// 0 when absent or not a number.
        /// </summary>
        private static float OsmTagLength(Dictionary<string, string> tags, string key)
        {
            if (!tags.TryGetValue(key, out string v) || string.IsNullOrEmpty(v)) return 0f;
            v = v.Trim();
            int i = 0;
            while (i < v.Length && ((v[i] >= '0' && v[i] <= '9') || v[i] == '.' || (i == 0 && v[i] == '-'))) i++;
            if (i == 0 || !float.TryParse(v.Substring(0, i), System.Globalization.NumberStyles.Float, Inv, out float num))
                return 0f;
            string rest = v.Substring(i).Trim().ToLowerInvariant();
            if (rest.StartsWith("'"))
            {
                // feet and inches: 12'6"
                float inches = 0f;
                string inchPart = rest.Substring(1).Trim().TrimEnd('"');
                if (inchPart.Length > 0) float.TryParse(inchPart, System.Globalization.NumberStyles.Float, Inv, out inches);
                return (num * 12f + inches) * 0.0254f;
            }
            if (rest.StartsWith("ft") || rest.StartsWith("feet")) return num * 0.3048f;
            if (rest.StartsWith("mi")) return num * 1609.344f;
            return num;
        }

        /// <summary>Reads every key/value pair of the <c>"tags"</c> object of an element.</summary>
        private static Dictionary<string, string> OsmReadTags(string obj)
        {
            var tags = new Dictionary<string, string>();
            int tagsIdx = obj.IndexOf("\"tags\"", StringComparison.Ordinal);
            if (tagsIdx < 0) return tags;
            int open = obj.IndexOf('{', tagsIdx + 6);
            if (open < 0) return tags;
            int close = OsmFindMatchingBracket(obj, open, '{', '}');
            if (close < 0) return tags;

            int i = open + 1;
            while (i < close)
            {
                while (i < close && obj[i] != '"') i++;
                if (i >= close) break;
                string key = OsmReadJsonString(obj, ref i);
                while (i < close && obj[i] != ':') i++;
                i++;
                while (i < close && char.IsWhiteSpace(obj[i])) i++;
                if (i >= close) break;
                if (obj[i] == '"') tags[key] = OsmReadJsonString(obj, ref i);
                else
                {
                    int e = i;
                    while (e < close && obj[e] != ',' && obj[e] != '}') e++;
                    tags[key] = obj.Substring(i, e - i).Trim();
                    i = e;
                }
            }
            return tags;
        }

        /// <summary>Reads the JSON string starting at the opening quote at <paramref name="i"/>; leaves i after the closing quote.</summary>
        private static string OsmReadJsonString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char esc = s[i + 1];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 5 < s.Length &&
                                int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, Inv, out int code))
                            {
                                sb.Append((char)code);
                                i += 6;
                                continue;
                            }
                            sb.Append(esc);
                            break;
                        default: sb.Append(esc); break;
                    }
                    i += 2;
                    continue;
                }
                if (c == '"') { i++; break; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reads the embedded <c>"geometry":[{"lat":…,"lon":…},…]</c> array from a
        /// way element returned by Overpass's <c>out geom</c> output format and
        /// converts each coordinate to a Unity world-space XZ point.
        /// </summary>
        private static Vector2[] OsmReadGeometry(string obj)
        {
            const string key = "\"geometry\"";
            int ki = obj.IndexOf(key, StringComparison.Ordinal);
            if (ki < 0) return new Vector2[0];

            int arrOpen  = obj.IndexOf('[', ki + key.Length);
            if (arrOpen < 0) return new Vector2[0];
            int arrClose = OsmFindMatchingBracket(obj, arrOpen, '[', ']');
            if (arrClose < 0) return new Vector2[0];

            var pts  = new List<Vector2>();
            int i    = arrOpen + 1;
            while (i < arrClose)
            {
                // Advance to next {
                while (i < arrClose && obj[i] != '{') i++;
                if (i >= arrClose) break;

                int end = OsmFindMatchingBracket(obj, i, '{', '}');
                if (end < 0 || end >= arrClose) break;

                string coord = obj.Substring(i, end - i + 1);
                if (OsmReadDouble(coord, "lat", out double lat) &&
                    OsmReadDouble(coord, "lon", out double lon))
                {
                    pts.Add(GeoToWorldXZ(lat, lon));
                }
                i = end + 1;
            }
            return pts.ToArray();
        }

        /// <summary>
        /// Returns the index of the bracket that matches the opening bracket at
        /// <paramref name="open"/>.  Correctly skips over quoted strings,
        /// including backslash-escaped characters inside strings.
        /// </summary>
        private static int OsmFindMatchingBracket(string s, int open, char openCh, char closeCh)
        {
            int  depth    = 0;
            bool inString = false;
            bool escaped  = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (escaped)  { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if      (c == openCh)  depth++;
                else if (c == closeCh) { if (--depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>
        /// Extracts all top-level JSON objects <c>{…}</c> found in
        /// <paramref name="s"/> between indices <paramref name="from"/> and
        /// <paramref name="to"/> (exclusive).
        /// </summary>
        private static List<string> OsmExtractTopLevelObjects(string s, int from, int to)
        {
            var list = new List<string>();
            int i    = from;
            while (i < to)
            {
                while (i < to && s[i] != '{') i++;
                if (i >= to) break;
                int end = OsmFindMatchingBracket(s, i, '{', '}');
                if (end < 0 || end > to) break;
                list.Add(s.Substring(i, end - i + 1));
                i = end + 1;
            }
            return list;
        }

        /// <summary>Reads a JSON string value for the given key.</summary>
        private static bool OsmReadString(string obj, string key, out string value)
        {
            value = "";
            string searchKey = "\"" + key + "\"";
            int ki = obj.IndexOf(searchKey, StringComparison.Ordinal);
            if (ki < 0) return false;
            int ci = obj.IndexOf(':', ki + searchKey.Length);
            if (ci < 0) return false;
            int s = ci + 1;
            while (s < obj.Length && obj[s] == ' ') s++;
            if (s >= obj.Length || obj[s] != '"') return false;
            s++;
            var sb = new System.Text.StringBuilder();
            while (s < obj.Length)
            {
                char c = obj[s];
                if (c == '\\' && s + 1 < obj.Length)
                {
                    char esc = obj[s + 1];
                    switch (esc)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        default:   sb.Append(esc);  break;
                    }
                    s += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(obj[s++]);
            }
            value = sb.ToString();
            return true;
        }

        /// <summary>Reads a JSON floating-point value for the given key.</summary>
        private static bool OsmReadDouble(string obj, string key, out double value)
        {
            value = 0;
            string searchKey = "\"" + key + "\"";
            int ki = obj.IndexOf(searchKey, StringComparison.Ordinal);
            if (ki < 0) return false;
            int ci = obj.IndexOf(':', ki + searchKey.Length);
            if (ci < 0) return false;
            int s = ci + 1;
            while (s < obj.Length && obj[s] == ' ') s++;
            int e = s;
            while (e < obj.Length && obj[e] != ',' && obj[e] != '}' && obj[e] != '"' && obj[e] != ']') e++;
            return double.TryParse(obj.Substring(s, e - s).Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // ── Road planning & mesh generation ───────────────────────────────────────

        private const string HwyMatDir =
            "Assets/[Tools]/FloridaGen/Materials";

        [NonSerialized] private RoadPlan _roadPlan;

        /// <summary>
        /// World-space XZ footprint of the terrain.  Roads are clipped to it, because Overpass
        /// returns the whole geometry of every way that touches the chunk.
        /// </summary>
        private void GetTerrainFootprint(out Vector2 min, out Vector2 max)
        {
            var terrain = GetComponent<Terrain>();
            var pos     = transform.position;
            min = new Vector2(pos.x, pos.z);
            max = min + (terrain != null && terrain.terrainData != null
                ? new Vector2(terrain.terrainData.size.x, terrain.terrainData.size.z)
                : new Vector2((float)MILES_TO_METERS, (float)MILES_TO_METERS));
        }

        /// <summary>
        /// World Y of the bare terrain surface at world XZ, clamped to the terrain footprint so
        /// points just outside it (road edges, embankment toes) read the edge height.
        /// </summary>
        public float TerrainSurfaceY(float worldX, float worldZ)
        {
            var terrain = GetComponent<Terrain>();
            var pos     = transform.position;
            if (terrain == null || terrain.terrainData == null) return pos.y;
            GetTerrainFootprint(out Vector2 min, out Vector2 max);
            worldX = Mathf.Clamp(worldX, min.x, max.x);
            worldZ = Mathf.Clamp(worldZ, min.y, max.y);
            return terrain.SampleHeight(new Vector3(worldX, 0f, worldZ)) + pos.y;
        }

        /// <summary>
        /// The solved 3-D road plan (heights, bridges, tunnels, grade separations).  Cached until
        /// <see cref="InvalidateRoadPlan"/>; the Scene-view gizmos and the mesh generator share it.
        /// </summary>
        public RoadPlan GetRoadPlan()
        {
            if (_roadPlan == null) _roadPlan = BuildRoadPlan();
            return _roadPlan;
        }

        /// <summary>Forgets the cached plan; call after the terrain, OSM data or road settings change.</summary>
        public void InvalidateRoadPlan() => _roadPlan = null;

        /// <summary>True when a solved road plan is cached (reading <see cref="GetRoadPlan"/> would not recompute it).</summary>
        public bool HasRoadPlan => _roadPlan != null;

        /// <summary>
        /// Runs <see cref="FloridaRoadTranslator"/> over <see cref="osmRoads"/> against the current
        /// terrain, with rivers, canals and railways as things bridges must clear.
        /// </summary>
        public RoadPlan BuildRoadPlan()
        {
            GetTerrainFootprint(out Vector2 min, out Vector2 max);
            var cfg = new RoadTranslationSettings
            {
                clipMin             = min,
                clipMax             = max,
                sampleSpacing       = Mathf.Max(1f, roadSampleSpacing),
                smoothingRadius     = roadSmoothingRadius,
                deckSmoothingRadius = roadDeckSmoothingRadius,
                deckClosingRadius   = roadDeckClosingRadius,
                surfaceOffset       = roadSurfaceOffset,
                groundHeight        = TerrainSurfaceY
            };
            return FloridaRoadTranslator.Translate(osmRoads, BuildRoadObstacles(), cfg);
        }

        /// <summary>Metres of free air a bridge deck must leave over a waterway of this type.</summary>
        private static float WaterwayClearance(string type)
        {
            switch (type)
            {
                case "river":          return 4.0f;
                case "tidal_channel":
                case "canal":          return 2.5f;
                case "stream":         return 1.0f;
                default:               return 0.6f;   // ditch, drain
            }
        }

        private List<RoadObstacle> BuildRoadObstacles()
        {
            var list = new List<RoadObstacle>();
            if (osmWaterways != null)
                foreach (var w in osmWaterways)
                {
                    if (w?.points == null || w.points.Length < 2 || w.underground) continue;
                    list.Add(new RoadObstacle
                    {
                        points = w.points, clearance = WaterwayClearance(w.type),
                        label = string.IsNullOrEmpty(w.name) ? w.type : $"{w.type} {w.name}"
                    });
                }
            if (osmRailways != null)
                foreach (var r in osmRailways)
                {
                    if (r?.points == null || r.points.Length < 2 || r.isTunnel || r.isBridge) continue;
                    list.Add(new RoadObstacle
                    {
                        points = r.points, clearance = FloridaRoadStandards.UnderRailClearance,
                        label = string.IsNullOrEmpty(r.name) ? r.type : $"{r.type} {r.name}"
                    });
                }
            return list;
        }

        /// <summary>
        /// Generates road meshes for all drivable highways in <see cref="osmRoads"/> under a
        /// "Highway Meshes" child object.  <see cref="FloridaRoadTranslator"/> first clips each way to
        /// the terrain, finds every grade separation (a deck over a road, river or railway) and solves a
        /// height profile that clears them within each road class's grade limit, lifting the ground roads
        /// that lead onto a bridge onto an approach embankment.  <see cref="FloridaRoadMeshBuilder"/> then
        /// builds the geometry: ground roads with embankments and barriers, bridges as solid decks with
        /// barriers and piers, and tunnels as closed tubes.  Everything is batched into a handful of
        /// meshes, each with a standard <see cref="MeshCollider"/>; Unity Physics bakes it like any other
        /// static collider when the object is placed in an ECS SubScene.
        /// </summary>
        public void GenerateHighwayMeshes()
        {
            if (osmRoads == null || osmRoads.Length == 0)
            {
                highwayMeshStatusMessage =
                    "No OSM road data \u2013 click \"\u2b07 Fetch Buildings & Roads\" first.";
                EditorUtility.SetDirty(this);
                return;
            }

            ClearHighwayMeshes();

            var terrainPos = transform.position;

            if (!AssetDatabase.IsValidFolder(HwyMatDir))
                AssetDatabase.CreateFolder("Assets/[Tools]/FloridaGen", "Materials");

            InvalidateRoadPlan();
            var plan = GetRoadPlan();
            var set  = new RoadMeshSet();
            FloridaRoadMeshBuilder.Build(plan, set, terrainPos, TerrainSurfaceY);

            string[] cats   = { "Motorway", "Primary", "Secondary", "Residential" };
            var      colors = new[]
            {
                new Color(0.22f, 0.22f, 0.25f), new Color(0.25f, 0.24f, 0.24f),
                new Color(0.28f, 0.27f, 0.26f), new Color(0.30f, 0.29f, 0.28f)
            };

            var parent = new GameObject("Highway Meshes");
            parent.transform.SetParent(transform, worldPositionStays: false);
            Undo.RegisterCreatedObjectUndo(parent, "Generate Highway Meshes");

            int objects = 0;
            void Emit(string label, MeshBatch batch, string matName, Color color)
            {
                var mesh = batch.ToMesh($"Highway_{label}");
                if (mesh == null) return;
                var go = new GameObject($"HWY_{label} ({batch.roadCount})");
                go.transform.SetParent(parent.transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = GetOrCreateHighwayMaterial(matName, color);
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                Undo.RegisterCreatedObjectUndo(go, "Generate Highway Meshes");
                objects++;
            }
            for (int c = 0; c < cats.Length; c++) Emit(cats[c], set.surface[c], cats[c], colors[c]);
            Emit("Structure",   set.structure,   "Structure",   new Color(0.58f, 0.58f, 0.56f));
            Emit("Embankment",  set.embankment,  "Embankment",  new Color(0.24f, 0.21f, 0.15f));

            highwayMeshStatusMessage =
                $"Highway meshes: {plan.groundPieces} ground, {plan.bridgePieces} bridge, " +
                $"{plan.tunnelPieces} tunnel pieces, {plan.crossings.Count} grade separations" +
                (plan.warnings.Count > 0 ? $", {plan.warnings.Count} warnings" : "") + " \u2713";
            EditorUtility.SetDirty(gameObject);

            Debug.Log($"[FloridaTerrainGenerator] Highway mesh generation complete: {plan.sourceRoads} roads " +
                      $"({plan.roads.Count} pieces: {plan.groundPieces} ground, {plan.bridgePieces} bridge, " +
                      $"{plan.tunnelPieces} tunnel), {plan.outsideRoads} outside the chunk, " +
                      $"{plan.degeneratePieces} degenerate, {plan.crossings.Count} grade separations " +
                      $"({plan.unmetCrossings} short of clearance), {set.piers} piers, {set.portals} tunnel portals, " +
                      $"max lift {plan.maxLift:F1} m, steepest grade {plan.maxGrade * 100f:F1}%, {objects} mesh objects.");
            for (int i = 0; i < plan.warnings.Count && i < 10; i++)
                Debug.LogWarning($"[FloridaTerrainGenerator] {plan.warnings[i]}");
        }

        /// <summary>Destroys the "Highway Meshes" child created by <see cref="GenerateHighwayMeshes"/>.</summary>
        public void ClearHighwayMeshes()
        {
            var existing = transform.Find("Highway Meshes");
            if (existing != null)
                Undo.DestroyObjectImmediate(existing.gameObject);

            highwayMeshStatusMessage = "";
            EditorUtility.SetDirty(gameObject);
        }

        /// <summary>
        /// Loads the material asset for <paramref name="category"/> from the
        /// Materials folder, or creates a new one with the supplied base colour.
        /// Saved as a project asset so the user can replace the default
        /// asphalt-grey tint with any texture in the Inspector.
        /// </summary>
        private static Material GetOrCreateHighwayMaterial(string category, Color color)
        {
            string path     = $"{HwyMatDir}/HighwayMat_{category}.mat";
            var    existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            // Prefer the project's active render-pipeline Lit shader.
            Shader shader =
                Shader.Find("HDRP/Lit") ??
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("Standard");

            var mat = new Material(shader) { name = $"HighwayMat_{category}" };

            // _BaseColor is used by HDRP and URP; _Color by Built-in RP.
            if      (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     color);

            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.08f);
            if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic",   0f);

            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // ── Building data export ──────────────────────────────────────────────────

        /// <summary>
        /// Writes every fetched building to a text file, one building per line:
        /// <c>id|type|levels|centerX|groundY|centerZ|area|x,z;x,z;…</c>
        /// (world-space metres, invariant culture). A single leading '#' line
        /// documents the format; lines starting with '#' should be ignored by readers.
        /// </summary>
        public int SaveBuildingData(string path)
        {
            if (osmBuildings == null || osmBuildings.Length == 0) return 0;
            var terrain = GetComponent<Terrain>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append($"# chunk={chunkX},{chunkY} format=id|type|levels|centerX|groundY|centerZ|area|x,z;x,z;...\n");
            int count = 0;
            foreach (var b in osmBuildings)
            {
                if (b?.footprint == null || b.footprint.Length < 3) continue;
                Vector2 c = Vector2.zero; float area = 0f;
                int m = b.footprint.Length;
                foreach (var v in b.footprint) c += v;
                c /= m;
                for (int i = 0; i < m; i++)
                {
                    Vector2 a = b.footprint[i], d = b.footprint[(i + 1) % m];
                    area += a.x * d.y - d.x * a.y;
                }
                area = Mathf.Abs(area) * 0.5f;
                float y = terrain != null && terrain.terrainData != null
                    ? terrain.SampleHeight(new Vector3(c.x, 0f, c.y)) + transform.position.y
                    : transform.position.y;
                sb.Append(count.ToString(inv)).Append('|')
                  .Append(string.IsNullOrEmpty(b.type) ? "yes" : b.type).Append('|')
                  .Append(b.levels.ToString(inv)).Append('|')
                  .Append(c.x.ToString("F2", inv)).Append('|')
                  .Append(y.ToString("F2", inv)).Append('|')
                  .Append(c.y.ToString("F2", inv)).Append('|')
                  .Append(area.ToString("F1", inv)).Append('|');
                for (int i = 0; i < m; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(b.footprint[i].x.ToString("F2", inv)).Append(',')
                      .Append(b.footprint[i].y.ToString("F2", inv));
                }
                sb.Append('\n');
                count++;
            }
            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            osmStatusMessage = $"Saved {count} buildings to {path}";
            return count;
        }

        // ── Save All / Load All ───────────────────────────────────────────────────

        /// <summary>
        /// Project-relative folder of the Terrain's TerrainData asset - where the building and chunk
        /// data files are written.  Falls back to "Assets" for a TerrainData that is not an asset.
        /// </summary>
        public string GetDataDirectory()
        {
            var terrain = GetComponent<Terrain>();
            string assetPath = terrain != null && terrain.terrainData != null
                ? AssetDatabase.GetAssetPath(terrain.terrainData) : "";
            string dir = string.IsNullOrEmpty(assetPath) ? "" : System.IO.Path.GetDirectoryName(assetPath);
            return string.IsNullOrEmpty(dir) ? "Assets" : dir.Replace('\\', '/');
        }

        /// <summary>Full path of the building export (<c>Buildings_x_y.txt</c>) beside the terrain asset.</summary>
        public string GetBuildingFilePath() =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(GetDataDirectory(), FloridaChunkFile.BuildingFileName(chunkX, chunkY)));

        /// <summary>Full path of the Save All file (<c>ChunkData_x_y.json</c>) beside the terrain asset.</summary>
        public string GetChunkFilePath() =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(GetDataDirectory(), FloridaChunkFile.ChunkFileName(chunkX, chunkY)));

        /// <summary>
        /// Writes the building export and the Save All file next to the terrain asset.
        /// Returns a one-line summary for the status label; failures are logged, not thrown.
        /// </summary>
        public string AutoSaveDataFiles()
        {
            try
            {
                SaveBuildingDataFile();
                SaveChunkFile(GetChunkFilePath());
                return $"Saved data files to {GetDataDirectory()}";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FloridaTerrainGenerator] Could not save the data files: {ex.Message}");
                return "(data files not saved)";
            }
        }

        /// <summary>
        /// Writes the building export (<see cref="SaveBuildingData"/>, format unchanged) to
        /// <see cref="GetBuildingFilePath"/> and imports it.  Returns the number of buildings written.
        /// </summary>
        public int SaveBuildingDataFile()
        {
            string path = GetBuildingFilePath();
            int count = SaveBuildingData(path);
            if (count > 0) ImportDataFile(path);
            return count;
        }

        /// <summary>Makes the Asset Database notice a file written under Assets.</summary>
        private static void ImportDataFile(string fullPath)
        {
            string project = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."))
                                 .Replace('\\', '/').TrimEnd('/') + "/";
            string full = System.IO.Path.GetFullPath(fullPath).Replace('\\', '/');
            if (full.StartsWith(project, StringComparison.OrdinalIgnoreCase))
                AssetDatabase.ImportAsset(full.Substring(project.Length));
        }

        /// <summary>Snapshot of the terrain heights and every fetched OSM feature.</summary>
        public FloridaChunkData CaptureChunkData()
        {
            var data = new FloridaChunkData
            {
                savedUtc                = DateTime.UtcNow.ToString("o", Inv),
                chunkX                  = chunkX,
                chunkY                  = chunkY,
                maxTerrainHeight        = maxTerrainHeight,
                roadSampleSpacing       = roadSampleSpacing,
                roadSmoothingRadius     = roadSmoothingRadius,
                roadDeckSmoothingRadius = roadDeckSmoothingRadius,
                roadDeckClosingRadius   = roadDeckClosingRadius,
                roadSurfaceOffset       = roadSurfaceOffset,
                buildings               = osmBuildings   ?? new OsmBuilding[0],
                roads                   = osmRoads       ?? new OsmRoad[0],
                waterways               = osmWaterways   ?? new OsmWaterway[0],
                railways                = osmRailways    ?? new OsmRailway[0],
                waterBodies             = osmWaterBodies ?? new OsmWaterBody[0],
                nodes                   = osmNodes       ?? new OsmNode[0]
            };

            var terrain = GetComponent<Terrain>();
            if (terrain != null && terrain.terrainData != null)
            {
                var td  = terrain.terrainData;
                int res = td.heightmapResolution;
                float[,] h = td.GetHeights(0, 0, res, res);
                float half = maxTerrainHeight * 0.5f;
                var grid = new float[res, res];
                for (int r = 0; r < res; r++)
                    for (int c = 0; c < res; c++)
                        grid[r, c] = h[r, c] * maxTerrainHeight - half;   // inverse of ApplyToTerrain
                data.heightmapResolution = res;
                data.elevations = FloridaChunkFile.Flatten(grid);
            }
            return data;
        }

        /// <summary>Writes the Save All file (heightmap plus all OSM data) to <paramref name="path"/>.</summary>
        public void SaveChunkFile(string path)
        {
            var data = CaptureChunkData();
            System.IO.File.WriteAllText(path, FloridaChunkFile.ToJson(data), new UTF8Encoding(false));
            ImportDataFile(path);
        }

        /// <summary>
        /// Rebuilds the chunk from a Save All file: chunk coordinates and road settings, the heightmap,
        /// every OSM feature (buildings go through <see cref="OnBuildingFootprintGenerated"/> again), and
        /// - when <paramref name="regenerateRoads"/> is set - the road meshes.  No network access.
        /// </summary>
        public void LoadChunkFile(string path, bool regenerateRoads)
        {
            var data = FloridaChunkFile.FromJson(System.IO.File.ReadAllText(path));

            chunkX                  = data.chunkX;
            chunkY                  = data.chunkY;
            maxTerrainHeight        = data.maxTerrainHeight > 0f ? data.maxTerrainHeight : maxTerrainHeight;
            roadSampleSpacing       = data.roadSampleSpacing;
            roadSmoothingRadius     = data.roadSmoothingRadius;
            roadDeckSmoothingRadius = data.roadDeckSmoothingRadius;
            roadDeckClosingRadius   = data.roadDeckClosingRadius;
            roadSurfaceOffset       = data.roadSurfaceOffset;

            if (data.elevations.Length > 0)
            {
                int res = ClosestHeightmapResolution(data.heightmapResolution);
                heightmapResolution = (HeightmapResolution)res;
                float[,] grid = FloridaChunkFile.ToGrid(data.elevations, data.heightmapResolution);
                if (res != data.heightmapResolution)
                    grid = BilinearUpsample(grid, data.heightmapResolution, data.heightmapResolution, res);
                ApplyToTerrain(grid);
                if (autoPosition)
                    transform.position = ChunkWorldPosition(chunkX, chunkY);
            }

            osmBuildings   = data.buildings;
            osmRoads       = data.roads;
            osmWaterways   = data.waterways;
            osmRailways    = data.railways;
            osmWaterBodies = data.waterBodies;
            osmNodes       = data.nodes;
            foreach (var b in osmBuildings)
                if (b?.footprint != null) OnBuildingFootprintGenerated(b.footprint);

            InvalidateRoadPlan();
            osmStatusMessage =
                $"Loaded chunk ({chunkX}, {chunkY}): {osmBuildings.Length} buildings, {osmRoads.Length} roads, " +
                $"{osmWaterways.Length} waterways, {osmRailways.Length} railways, {osmNodes.Length} nodes  \u2713";
            statusMessage = $"Chunk ({chunkX}, {chunkY}) loaded from {System.IO.Path.GetFileName(path)}  \u2713";
            EditorUtility.SetDirty(this);

            if (regenerateRoads && osmRoads.Length > 0) GenerateHighwayMeshes();
        }

        internal static int ClosestHeightmapResolution(int res)
        {
            int best = (int)HeightmapResolution.R129, bestDiff = int.MaxValue;
            foreach (HeightmapResolution r in Enum.GetValues(typeof(HeightmapResolution)))
            {
                int d = Mathf.Abs((int)r - res);
                if (d < bestDiff) { bestDiff = d; best = (int)r; }
            }
            return best;
        }

#endif // UNITY_EDITOR

        /// <summary>Anything at or below this (m) is a NoData sentinel (EPQS uses -1000000, 3DEP -999999).</summary>
        private const float NoDataElevation = -1000f;

        /// <summary>
        /// Cleans a downloaded elevation (m).  NoData sentinels and non-finite values become
        /// 0 (sea level); real below-sea-level values are kept, because the terrain is offset
        /// by -maxTerrainHeight/2 precisely so it can go below sea level as well as above.
        /// </summary>
        private static float SanitizeElevation(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) || v <= NoDataElevation ? 0f : v;

        // ── Parsing ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses an Arc/Info ASCII Grid (AAIGrid) string into a 2-D float array
        /// with row 0 = south (matching Unity's terrain heightmap layout) and
        /// resamples it to <paramref name="targetRes"/> × <paramref name="targetRes"/>.
        /// </summary>
        internal static float[,] ParseAAIGrid(string text, int targetRes)
        {
            string[] lines = text.Split(
                new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            ParseAAIGridHeader(lines,
                out int ncols, out int nrows, out float nodata, out int dataLine);

            if (ncols == 0 || nrows == 0)
                throw new Exception(
                    $"AAIGrid parse error: ncols={ncols} nrows={nrows}");

            // Read raw elevation values.
            // AAIGrid: row 0 = northernmost row, row nrows-1 = southernmost.
            float[,] raw = new float[nrows, ncols];
            for (int r = 0; r < nrows && dataLine + r < lines.Length; r++)
            {
                string[] vals = lines[dataLine + r].Trim().Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                for (int c = 0; c < ncols && c < vals.Length; c++)
                {
                    if (float.TryParse(vals[c],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out float v))
                    {
                        raw[r, c] = v <= nodata ? 0f : SanitizeElevation(v);
                    }
                }
            }

            // Flip north-south so that row 0 = south (Unity terrain convention).
            float[,] flipped = new float[nrows, ncols];
            for (int r = 0; r < nrows; r++)
                for (int c = 0; c < ncols; c++)
                    flipped[r, c] = raw[nrows - 1 - r, c];

            return BilinearUpsample(flipped, nrows, ncols, targetRes);
        }

        private static void ParseAAIGridHeader(
            string[] lines,
            out int ncols, out int nrows, out float nodata, out int dataLine)
        {
            ncols    = 0;
            nrows    = 0;
            nodata   = -9999f;
            dataLine = 0;

            for (int i = 0; i < lines.Length && i < 12; i++)
            {
                string[] p = lines[i].Trim().Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                if (p.Length < 2)
                {
                    dataLine = i;
                    return;
                }

                string k = p[0].ToUpperInvariant();
                try
                {
                    switch (k)
                    {
                        case "NCOLS":
                            ncols    = int.Parse(p[1]);
                            dataLine = i + 1;
                            break;
                        case "NROWS":
                            nrows    = int.Parse(p[1]);
                            dataLine = i + 1;
                            break;
                        case "NODATA_VALUE":
                        case "NODATA":
                            nodata   = float.Parse(p[1],
                                System.Globalization.CultureInfo.InvariantCulture);
                            dataLine = i + 1;
                            break;
                        case "XLLCORNER": case "XLLCENTER":
                        case "YLLCORNER": case "YLLCENTER":
                        case "CELLSIZE":  case "DX":  case "DY":
                            dataLine = i + 1;
                            break;
                        default:
                            // First unrecognised token = start of data rows
                            if (ncols > 0 && nrows > 0)
                                return;
                            dataLine = i + 1;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"AAIGrid header parse error on keyword '{k}' (value='{p[1]}'): {ex.Message}", ex);
                }
            }
        }

        // ── Resampling ────────────────────────────────────────────────────────────

        internal static float[,] BilinearUpsample(
            float[,] src, int srcRows, int srcCols, int dstRes)
        {
            if (srcRows == dstRes && srcCols == dstRes) return src;

            // Degenerate source: replicate single value across entire output.
            if (srcRows <= 1 || srcCols <= 1)
            {
                float fill = (src.Length > 0) ? src[0, 0] : 0f;
                float[,] flat = new float[dstRes, dstRes];
                for (int r = 0; r < dstRes; r++)
                    for (int c = 0; c < dstRes; c++)
                        flat[r, c] = fill;
                return flat;
            }

            float[,] dst = new float[dstRes, dstRes];
            for (int r = 0; r < dstRes; r++)
            {
                for (int c = 0; c < dstRes; c++)
                {
                    float sr = (dstRes > 1)
                        ? (float)r / (dstRes - 1) * (srcRows - 1) : 0f;
                    float sc = (dstRes > 1)
                        ? (float)c / (dstRes - 1) * (srcCols - 1) : 0f;

                    int r0 = Mathf.Clamp((int)sr, 0, srcRows - 2);
                    int c0 = Mathf.Clamp((int)sc, 0, srcCols - 2);
                    int r1 = r0 + 1;
                    int c1 = c0 + 1;

                    float tr = sr - r0;
                    float tc = sc - c0;

                    dst[r, c] = Mathf.Lerp(
                        Mathf.Lerp(src[r0, c0], src[r0, c1], tc),
                        Mathf.Lerp(src[r1, c0], src[r1, c1], tc),
                        tr);
                }
            }
            return dst;
        }

        // ── Terrain application ───────────────────────────────────────────────────

        private void ApplyToTerrain(float[,] elevations)
        {
            var terrain = GetComponent<Terrain>();
            if (!terrain || !terrain.terrainData) return;

            var  td      = terrain.terrainData;
            int  res     = (int)heightmapResolution;
            float tileM  = (float)MILES_TO_METERS;   // 1609.344 m

#if UNITY_EDITOR
            Undo.RecordObject(td, "Generate Florida Terrain Chunk");
#endif
            // Resize terrain to exactly 1 mile × 1 mile.
            td.heightmapResolution = res;
            td.size = new Vector3(tileM, maxTerrainHeight, tileM);

            // Normalise elevation to [0, 1] for Unity's heightmap.
            // The terrain is positioned at Y = -maxTerrainHeight/2 so that elevation 0
            // (sea level) sits at world Y 0.  The offset is applied here so that real
            // USGS values map to the correct normalized position within that range.
            float halfHeight = maxTerrainHeight * 0.5f;
            float[,] heights = new float[res, res];
            float minElev = float.MaxValue, maxElev = float.MinValue;
            for (int r = 0; r < res; r++)
                for (int c = 0; c < res; c++)
                {
                    float elev = elevations[r, c];
                    minElev = Mathf.Min(minElev, elev);
                    maxElev = Mathf.Max(maxElev, elev);
                    heights[r, c] = Mathf.Clamp01((elev + halfHeight) / maxTerrainHeight);
                }

            if (minElev < -halfHeight || maxElev > halfHeight)
                Debug.LogWarning(
                    $"[FloridaTerrainGenerator] Elevations {minElev:F1} m to {maxElev:F1} m exceed the " +
                    $"terrain range of \u00b1{halfHeight:F0} m and were clamped. Increase maxTerrainHeight.");

            td.SetHeights(0, 0, heights);

#if UNITY_EDITOR
            EditorUtility.SetDirty(td);
#endif
        }

        /// <summary>
        /// Resets the terrain to a flat surface at sea level (world Y 0).
        /// With the ±half-height Y-offset scheme, this corresponds to a normalised
        /// height of 0.5, allowing the terrain to be sculpted both above and below
        /// sea level after clearing.
        /// </summary>
        public void ClearTerrain()
        {
            var terrain = GetComponent<Terrain>();
            if (!terrain || !terrain.terrainData) return;

            var td  = terrain.terrainData;
            int res = (int)heightmapResolution;

#if UNITY_EDITOR
            Undo.RecordObject(td, "Clear Florida Terrain");
#endif
            td.heightmapResolution = res;
            td.size = new Vector3((float)MILES_TO_METERS, maxTerrainHeight, (float)MILES_TO_METERS);

            // 0.5 normalised = sea level (world Y 0) with the terrain base at Y = -maxTerrainHeight/2.
            float seaLevel = 0.5f;
            var clearHeights = new float[res, res];
            for (int r = 0; r < res; r++)
                for (int c = 0; c < res; c++)
                    clearHeights[r, c] = seaLevel;
            td.SetHeights(0, 0, clearHeights);

            statusMessage = "Terrain cleared.";

#if UNITY_EDITOR
            InvalidateRoadPlan();
            EditorUtility.SetDirty(td);
#endif
        }

        // ── HTTP helpers (editor-only) ────────────────────────────────────────────
#if UNITY_EDITOR

        // Shared client avoids socket exhaustion from repeated instantiation.
        // Instantiated lazily on first use; the editor process lifetime is sufficient scope.
        private static HttpClient _httpClient;

        private static HttpClient GetHttpClient(int timeoutSeconds)
        {
            if (_httpClient == null)
            {
                _httpClient = new HttpClient();
                _httpClient.DefaultRequestHeaders.Add(
                    "User-Agent", "UnityFloridaTerrainGenerator/1.0");
            }
            // Timeout is updated per-call so large downloads aren't capped by the default.
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return _httpClient;
        }

        /// <summary>
        /// Synchronous HTTP GET.  Runs on the thread pool inside Task.Run to escape
        /// Unity's synchronisation context, preventing deadlocks when the editor
        /// main thread blocks on GetAwaiter().GetResult().
        /// </summary>
        private static string SyncHttpGet(string url, int timeoutSeconds)
        {
            return Task.Run(async () =>
            {
                var client = GetHttpClient(timeoutSeconds);
                return await client.GetStringAsync(url).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous HTTP POST with application/x-www-form-urlencoded body.
        /// Uses the same Task.Run pattern as <see cref="SyncHttpGet"/> to avoid
        /// synchronisation-context deadlocks.
        /// </summary>
        private static string SyncHttpPost(string url, string body, int timeoutSeconds)
        {
            return Task.Run(async () =>
            {
                var client  = GetHttpClient(timeoutSeconds);
                var content = new StringContent(
                    body, Encoding.UTF8, "application/x-www-form-urlencoded");
                var response = await client.PostAsync(url, content).ConfigureAwait(false);
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }

#endif // UNITY_EDITOR

        // ── Math utilities ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the number of metres per degree of longitude at the given latitude.
        /// </summary>
        private static double MetersPerDegreeLon(double latDeg) =>
            METERS_PER_DEG_LAT * Math.Cos(latDeg * Math.PI / 180.0);

        /// <summary>
        /// Converts WGS-84 geographic coordinates to Unity world-space XZ
        /// (metres from the world origin at
        /// <see cref="ORIGIN_LAT"/>/<see cref="ORIGIN_LON"/>).
        /// +X = East, +Z = North, 1 unit = 1 metre.
        /// </summary>
        public static Vector2 GeoToWorldXZ(double lat, double lon)
        {
            double x = (lon - ORIGIN_LON) * MetersPerDegreeLon(lat);
            double z = (lat - ORIGIN_LAT) * METERS_PER_DEG_LAT;
            return new Vector2((float)x, (float)z);
        }
    }
}