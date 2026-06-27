using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace kimbleCode.DeadEverywhere.FloridaTerrainGen
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

        // ── Runtime state (read by editor) ───────────────────────────────────────
        [HideInInspector] public bool   isGenerating;
        [HideInInspector] public string statusMessage = "Ready";

        [HideInInspector] public bool          osmFetching;
        [HideInInspector] public string        osmStatusMessage = "";
        [HideInInspector] public OsmBuilding[] osmBuildings     = new OsmBuilding[0];
        [HideInInspector] public OsmRoad[]     osmRoads         = new OsmRoad[0];
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
            /// The mesh deck will be elevated above the terrain with the minimum vertical
            /// clearance required for the road class.</summary>
            public bool isBridge;
            /// <summary>True when the OSM way carries a <c>tunnel=yes</c> (or similar) tag.
            /// Tunnel segments are skipped during mesh generation because they are
            /// underground and invisible from the surface.</summary>
            public bool isTunnel;
            /// <summary>
            /// OSM <c>layer</c> tag value — the number of levels above grade this way
            /// sits relative to the surrounding ground-level roads.
            /// 0 = at grade (default for non-bridge ways).
            /// 1 = first level above grade (typical single-span overpass).
            /// 2 = second level (e.g. a flyover above another bridge at an interchange).
            /// 3 = third level (rare high ramps at complex multi-level interchanges).
            /// Bridge deck height is scaled proportionally: <c>clearance × max(1, layer)</c>.
            /// </summary>
            public int layer;
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

                statusMessage = $"Chunk ({chunkX}, {chunkY}) generated  \u2713";
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

        /// <summary>
        /// Downloads building footprints and road centre-lines from the
        /// OpenStreetMap Overpass API for chunk (chunkX, chunkY) and stores
        /// the results for scene-view gizmo display.  No API key required.
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
                string q =
                    "[out:json][timeout:60];" +
                    "(" +
                    $"way[\"building\"]({bounds.south:F7},{bounds.west:F7},{bounds.north:F7},{bounds.east:F7});" +
                    $"way[\"highway\"]({bounds.south:F7},{bounds.west:F7},{bounds.north:F7},{bounds.east:F7});" +
                    ");out geom qt;";

                const string OsmUrl = "https://overpass-api.de/api/interpreter";
                string body = "data=" + Uri.EscapeDataString(q);

                EditorUtility.DisplayProgressBar("OSM Data", "Downloading…", 0.35f);
                string json = SyncHttpPost(OsmUrl, body, OsmRequestTimeoutSeconds);

                // Log a brief excerpt so failures are diagnosable in the Console.
                const int OsmResponsePreviewLength = 300;
                Debug.Log($"[FloridaTerrainGenerator] OSM response ({json.Length} chars): " +
                          (json.Length > 0 ? json.Substring(0, Mathf.Min(OsmResponsePreviewLength, json.Length)) : "(empty)"));

                EditorUtility.DisplayProgressBar("OSM Data", "Parsing response…", 0.80f);
                ParseOsmJson(json);

                osmStatusMessage =
                    osmBuildings.Length == 0 && osmRoads.Length == 0
                        ? "OSM: no data found for this chunk"
                        : $"OSM: {osmBuildings.Length} buildings, {osmRoads.Length} roads  \u2713";
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
                ParseGetSamplesJson(json, points, start, end, result);
            }

            return result;
        }

        private static void ParseGetSamplesJson(
            string json,
            List<(int row, int col, double lat, double lon)> points,
            int start, int end,
            float[,] result)
        {
            // Parse: {"samples":[{"location":{...},"value":"35.2"},…]}
            // Simple index-based parse avoids a JSON library dependency.
            int cursor = 0;
            for (int i = start; i < end; i++)
            {
                int vi = json.IndexOf("\"value\"", cursor, StringComparison.Ordinal);
                if (vi < 0) break;

                int colon = json.IndexOf(':', vi + 7);
                if (colon < 0) break;

                // Advance past whitespace and optional opening quote
                int s = colon + 1;
                while (s < json.Length && (json[s] == ' ' || json[s] == '"')) s++;

                int e = s;
                while (e < json.Length &&
                       json[e] != '"' && json[e] != ',' &&
                       json[e] != '}' && json[e] != ']')
                    e++;

                cursor = e;

                string valStr = json.Substring(s, e - s).Trim();
                if (float.TryParse(valStr,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float val))
                {
                    result[points[i].row, points[i].col] = Mathf.Max(0f, val);
                }
            }
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
                out float val) ? Mathf.Max(0f, val) : 0f;
        }

        // ── OSM Overpass JSON parsing ─────────────────────────────────────────────

        /// <summary>
        /// Parses an Overpass API JSON response (using <c>out geom qt;</c> format)
        /// and populates <see cref="osmBuildings"/> and <see cref="osmRoads"/>
        /// with world-space XZ coordinates.  Each way element contains an embedded
        /// <c>"geometry"</c> array of <c>{"lat":…,"lon":…}</c> objects, so no
        /// separate node-ID resolution pass is required.
        /// </summary>
        private void ParseOsmJson(string json)
        {
            osmBuildings = new OsmBuilding[0];
            osmRoads     = new OsmRoad[0];

            var buildings = new List<OsmBuilding>();
            var roads     = new List<OsmRoad>();

            // Find the "elements" array boundaries
            int elemKey  = json.IndexOf("\"elements\"", StringComparison.Ordinal);
            if (elemKey < 0)
            {
                Debug.LogWarning($"[FloridaTerrainGenerator] OSM response ({json.Length} chars) has no 'elements' key.");
                return;
            }
            int arrOpen  = json.IndexOf('[', elemKey + 10);
            if (arrOpen < 0) return;
            int arrClose = OsmFindMatchingBracket(json, arrOpen, '[', ']');
            if (arrClose < 0) return;

            // Collect all top-level JSON objects from the elements array
            var elements = OsmExtractTopLevelObjects(json, arrOpen + 1, arrClose);
            Debug.Log($"[FloridaTerrainGenerator] OSM elements extracted: {elements.Count}");

            foreach (string el in elements)
            {
                // Only process way elements
                if (!OsmReadString(el, "type", out string t) || t != "way") continue;

                OsmReadTagValue(el, "building", out string bldgTag);
                OsmReadTagValue(el, "highway",  out string hw);
                bool isBuilding = !string.IsNullOrEmpty(bldgTag);
                bool isRoad     = !string.IsNullOrEmpty(hw);
                if (!isBuilding && !isRoad) continue;

                // Read geometry embedded directly in the way (out geom qt format)
                Vector2[] pts = OsmReadGeometry(el);
                if (pts.Length < 2) continue;

                if (isBuilding)
                {
                    var bldg = new OsmBuilding { footprint = pts };
                    buildings.Add(bldg);
                    OnBuildingFootprintGenerated(bldg.footprint);
                }
                else
                {
                    OsmReadTagValue(el, "bridge", out string bridgeTag);
                    OsmReadTagValue(el, "tunnel", out string tunnelTag);
                    OsmReadTagValue(el, "layer",  out string layerTag);
                    int layerVal = 0;
                    if (!string.IsNullOrEmpty(layerTag))
                        int.TryParse(layerTag,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out layerVal);
                    roads.Add(new OsmRoad
                    {
                        points   = pts,
                        highway  = hw ?? "",
                        isBridge = !string.IsNullOrEmpty(bridgeTag) && bridgeTag != "no",
                        isTunnel = !string.IsNullOrEmpty(tunnelTag) && tunnelTag != "no",
                        layer    = layerVal
                    });
                }
            }

            osmBuildings = buildings.ToArray();
            osmRoads     = roads.ToArray();
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

        /// <summary>Reads a string value from within the "tags" object of a way element.</summary>
        private static void OsmReadTagValue(string obj, string key, out string value)
        {
            value = "";
            int tagsIdx = obj.IndexOf("\"tags\"", StringComparison.Ordinal);
            if (tagsIdx < 0) return;
            int tagsOpen  = obj.IndexOf('{', tagsIdx + 6);
            if (tagsOpen < 0) return;
            int tagsClose = OsmFindMatchingBracket(obj, tagsOpen, '{', '}');
            if (tagsClose < 0) return;
            OsmReadString(obj.Substring(tagsOpen, tagsClose - tagsOpen + 1), key, out value);
        }

        // ── Highway mesh generation ───────────────────────────────────────────────

        private const string HwyMatDir =
            "Assets/[Tools]/FloridaGen/Materials";

        /// <summary>
        /// Returns the minimum vertical clearance (metres) the bridge deck must
        /// maintain above the terrain it crosses.
        ///
        /// Values are set to real-world US highway design standards (AASHTO /
        /// FDOT) so the generated mesh is visually plausible in a Tampa Bay
        /// context.  Tampa's elevated motorways (Howard Frankland, Gandy, etc.)
        /// sit 18–20 m above water; the 12 m floor ensures they are clearly
        /// visible as elevated even over flat Florida terrain.  A future
        /// improvement could source actual bridge-deck elevations from the USGS
        /// National Bridge Inventory (NBI), but that requires an API key.
        /// </summary>
        private static float HighwayBridgeClearance(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk":    case "trunk_link":
                    return 12.0f;  // well above AASHTO 14 ft (4.27 m) min; gives visibility over flat FL terrain
                case "primary":  case "primary_link":
                    return 8.0f;
                case "secondary": case "secondary_link":
                case "tertiary":  case "tertiary_link":
                    return 6.0f;
                default:
                    return 5.0f;   // residential / service
            }
        }
        private static float HighwayHalfWidth(string highway)
        {
            switch (highway)
            {
                case "motorway":        return 7.0f;
                case "motorway_link":   return 4.0f;
                case "trunk":           return 5.5f;
                case "trunk_link":      return 4.0f;
                case "primary":         return 4.5f;
                case "primary_link":    return 3.5f;
                case "secondary":       return 4.0f;
                case "secondary_link":  return 3.0f;
                case "tertiary":        return 3.5f;
                case "tertiary_link":   return 2.5f;
                case "residential":
                case "living_street":
                case "unclassified":
                case "service":         return 3.0f;
                default:                return 2.0f;
            }
        }

        /// <summary>
        /// Returns <c>true</c> for OSM highway values that should produce a 3-D
        /// road surface mesh.  Paths, footways and cycleways are excluded because
        /// they are too narrow and numerous to mesh efficiently.
        /// </summary>
        private static bool IsDrivableHighway(string highway)
        {
            switch (highway)
            {
                case "motorway":   case "motorway_link":
                case "trunk":      case "trunk_link":
                case "primary":    case "primary_link":
                case "secondary":  case "secondary_link":
                case "tertiary":   case "tertiary_link":
                case "residential": case "living_street":
                case "unclassified": case "service":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Generates 3-D road-surface meshes for all drivable highway segments in
        /// <see cref="osmRoads"/>.  Each road polyline is extruded into a quad-strip
        /// that conforms to the terrain height.  Results are parented under a
        /// "Highway Meshes" child GameObject; the four road-category materials are
        /// saved as project assets so textures can be swapped without re-running.
        /// OSM data must be fetched before calling this method.
        ///
        /// Height data sources:
        /// <list type="bullet">
        ///   <item><b>Ground-level roads</b> — deck vertices are snapped to the USGS
        ///   3DEP bare-earth DEM (the same data baked into the Terrain heightmap by
        ///   <see cref="GenerateChunk"/>).  This is real measured elevation.</item>
        ///   <item><b>Bridges / elevated ways</b> — the USGS 3DEP DEM is a bare-earth
        ///   model that intentionally removes elevated structures, so the actual bridge-deck
        ///   elevation is not directly available from a free per-point REST API.
        ///   Instead, deck height is estimated as:
        ///   <c>terrain + HighwayBridgeClearance × max(1, layer)</c>, where
        ///   <c>layer</c> is the OSM-mapped stacking level (1 = simple overpass,
        ///   2 = flyover above another bridge, etc.) and the clearance floors are
        ///   set to real US AASHTO/FDOT design minimums (12 m for motorways).
        ///   Elevation is triggered by <c>bridge=yes</c> OR <c>layer &gt; 0</c> so
        ///   that ways tagged only with a layer value (common in older OSM edits)
        ///   are still elevated correctly.
        ///   </item>
        /// </list>
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

            var terrain    = GetComponent<Terrain>();
            var terrainPos = transform.position;

            // Ensure the Materials folder exists before creating assets.
            if (!AssetDatabase.IsValidFolder(HwyMatDir))
                AssetDatabase.CreateFolder(
                    "Assets/[Tools]/FloridaGen", "Materials");

            // Pre-load / create the four road-surface material variants.
            var matHighway     = GetOrCreateHighwayMaterial(
                "Motorway",    new Color(0.22f, 0.22f, 0.25f));
            var matPrimary     = GetOrCreateHighwayMaterial(
                "Primary",     new Color(0.25f, 0.24f, 0.24f));
            var matSecondary   = GetOrCreateHighwayMaterial(
                "Secondary",   new Color(0.28f, 0.27f, 0.26f));
            var matResidential = GetOrCreateHighwayMaterial(
                "Residential", new Color(0.30f, 0.29f, 0.28f));

            // Parent object holds all road meshes for this chunk.
            var parent = new GameObject("Highway Meshes");
            parent.transform.SetParent(transform, worldPositionStays: false);
            Undo.RegisterCreatedObjectUndo(parent, "Generate Highway Meshes");

            int count   = 0;
            int tunnels = 0;
            int bridges = 0; // isBridge=true
            int elevated = 0; // layer>0 (elevated even without explicit bridge tag)
            for (int i = 0; i < osmRoads.Length; i++)
            {
                var road = osmRoads[i];
                if (road?.points == null || road.points.Length < 2) continue;
                if (!IsDrivableHighway(road.highway)) continue;

                // Tunnels are underground – skip visible mesh generation.
                if (road.isTunnel) { tunnels++; continue; }

                if (road.isBridge)           bridges++;
                else if (road.layer > 0)     elevated++;

                // An elevated way is indicated by EITHER the OSM bridge=yes tag OR
                // a positive layer value.  Using both ensures elevation is applied
                // even if a mapper tagged the layer but omitted bridge=yes (common
                // for US motorway viaducts in older edits).  Clearance is scaled
                // proportionally to the stacking level so layer=2 interchange
                // flyovers sit twice as high as a simple layer=1 overpass.
                // A bridge tagged bridge=yes without a layer tag (layer=0) is
                // treated as a single-level overpass (multiplier=1), matching a
                // way with layer=1 and no explicit bridge tag — both are one level
                // above grade and get the same deck height.
                bool isElevated = road.isBridge || road.layer > 0;
                float clearance = isElevated
                    ? HighwayBridgeClearance(road.highway) * Mathf.Max(1, road.layer)
                    : 0f;

                Mesh mesh = BuildRoadStripMesh(
                    road.points, HighwayHalfWidth(road.highway),
                    clearance, terrain, terrainPos);
                if (mesh == null) continue;

                Material mat;
                switch (road.highway)
                {
                    case "motorway": case "motorway_link":
                    case "trunk":    case "trunk_link":
                        mat = matHighway; break;
                    case "primary": case "primary_link":
                        mat = matPrimary; break;
                    case "secondary": case "secondary_link":
                    case "tertiary":  case "tertiary_link":
                        mat = matSecondary; break;
                    default:
                        mat = matResidential; break;
                }

                var go = new GameObject(
                    isElevated
                        ? $"HWY_{road.highway}_{i}_elevated_L{(road.layer > 0 ? road.layer : 1)}"
                        : $"HWY_{road.highway}_{i}");
                go.transform.SetParent(parent.transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh   = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                Undo.RegisterCreatedObjectUndo(go, "Generate Highway Meshes");
                count++;
            }

            string tunnelNote = tunnels > 0 ? $", {tunnels} tunnels skipped" : "";
            highwayMeshStatusMessage = $"Highway meshes: {count} generated{tunnelNote} \u2713";
            EditorUtility.SetDirty(gameObject);
            // Diagnostic: helps confirm bridge/layer detection is working correctly.
            Debug.Log($"[FloridaTerrainGenerator] Highway mesh generation complete: " +
                      $"{count} segments ({bridges} bridge-tagged, {elevated} layer>0 elevated, " +
                      $"{tunnels} tunnel ways skipped). " +
                      $"If bridges=0 and elevated=0 try re-fetching OSM data.");
        }

        /// <summary>
        /// Destroys the "Highway Meshes" child object (and its children) that was
        /// created by <see cref="GenerateHighwayMeshes"/>.
        /// </summary>
        public void ClearHighwayMeshes()
        {
            var existing = transform.Find("Highway Meshes");
            if (existing != null)
                Undo.DestroyObjectImmediate(existing.gameObject);

            highwayMeshStatusMessage = "";
            EditorUtility.SetDirty(gameObject);
        }

        /// <summary>
        /// Maximum allowed distance (in metres) between consecutive road-mesh
        /// vertices.  Unity's physics engine warns when any two vertices of a
        /// <see cref="MeshCollider"/> triangle are farther apart than 500 units,
        /// which degrades simulation stability.  We subdivide to stay well below
        /// that threshold.
        /// </summary>
        private const float kMaxSegmentLength = 450f;

        /// <summary>
        /// Subdivides a polyline so that no segment is longer than
        /// <see cref="kMaxSegmentLength"/>.  Points that are already close
        /// enough are kept unchanged; only overlong gaps receive new
        /// intermediate vertices spaced evenly along the original segment.
        /// </summary>
        private static Vector2[] SubdivideLongSegments(Vector2[] pts)
        {
            // Fast path — measure whether any segment exceeds the limit.
            bool needsSplit = false;
            for (int i = 1; i < pts.Length; i++)
            {
                if ((pts[i] - pts[i - 1]).sqrMagnitude > kMaxSegmentLength * kMaxSegmentLength)
                {
                    needsSplit = true;
                    break;
                }
            }
            if (!needsSplit) return pts;

            var result = new System.Collections.Generic.List<Vector2>(pts.Length * 2);
            result.Add(pts[0]);
            for (int i = 1; i < pts.Length; i++)
            {
                float dist = (pts[i] - pts[i - 1]).magnitude;
                if (dist > kMaxSegmentLength)
                {
                    int   divisions = Mathf.CeilToInt(dist / kMaxSegmentLength);
                    for (int d = 1; d < divisions; d++)
                    {
                        float t = (float)d / divisions;
                        result.Add(Vector2.Lerp(pts[i - 1], pts[i], t));
                    }
                }
                result.Add(pts[i]);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Builds a quad-strip (ribbon) mesh from a road centre-line polyline.
        /// </summary>
        /// <param name="pts">Centre-line vertices in world-space XZ (metres).</param>
        /// <param name="halfW">Half the road surface width in metres.</param>
        /// <param name="bridgeClearance">
        /// When &gt; 0 the way is a bridge: the deck is elevated above the terrain
        /// so that every point on it is at least <paramref name="bridgeClearance"/>
        /// metres above the terrain surface beneath it.  The deck follows the
        /// straightest possible grade that satisfies this constraint, giving a
        /// realistic ramp/arch profile.  Pass 0 for ordinary ground-level roads.
        /// </param>
        /// <param name="terrain">Terrain component used for height sampling.</param>
        /// <param name="terrainPos">World position of the terrain transform.</param>
        /// <remarks>
        /// Vertex layout: <c>vertices[0..n-1]</c> = left edge,
        /// <c>vertices[n..2n-1]</c> = right edge, where n = pts.Length.
        /// UVs tile the road-surface texture squarely: U 0→1 across width,
        /// V increments with arc-length so one texture repeat ≈ road width.
        /// </remarks>
        private static Mesh BuildRoadStripMesh(
            Vector2[] pts, float halfW, float bridgeClearance,
            Terrain terrain, Vector3 terrainPos)
        {
            // Subdivide any segments longer than kMaxSegmentLength so the
            // resulting MeshCollider triangles stay within Unity's 500-unit
            // stability limit.
            pts = SubdivideLongSegments(pts);

            int n = pts.Length;
            if (n < 2) return null;

            // ── Per-vertex right-perpendicular normals (in the XZ plane) ──────────
            // At interior vertices the two adjacent segment directions are averaged
            // (bisector approach).  The miter length is clamped so sharp bends do
            // not produce excessively wide joints.
            var normals = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 dir;
                if (i == 0)
                {
                    dir = (pts[1] - pts[0]).normalized;
                }
                else if (i == n - 1)
                {
                    dir = (pts[n - 1] - pts[n - 2]).normalized;
                }
                else
                {
                    Vector2 d0 = (pts[i]     - pts[i - 1]).normalized;
                    Vector2 d1 = (pts[i + 1] - pts[i]).normalized;
                    dir = d0 + d1;
                    if (dir.sqrMagnitude < 0.0001f) dir = d1;
                    dir.Normalize();
                }
                // Right-perpendicular: rotate 90° CW in XZ → (dir.y, -dir.x)
                normals[i] = new Vector2(dir.y, -dir.x);
            }

            // ── Cumulative arc-length for UV tiling ───────────────────────────────
            float totalLen = 0f;
            var   segLen   = new float[n];
            segLen[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                totalLen += (pts[i] - pts[i - 1]).magnitude;
                segLen[i] = totalLen;
            }
            float uvScale = halfW > 0f ? 1f / (halfW * 2f) : 1f;

            // ── Bridge deck elevation pre-pass ────────────────────────────────────
            // For bridge ways the deck must stay at least `bridgeClearance` metres
            // above the terrain at every centre-line point while following the
            // straightest possible grade between the two abutment ends.
            // Algorithm: start with a linear grade between the two endpoint terrain
            // heights, then raise any vertex that would violate the clearance
            // constraint.  A second forward pass smooths out any remaining steps
            // introduced by the constraint lift.
            float[] bridgeDeckH = null;
            if (bridgeClearance > 0f && n >= 2)
            {
                float startH = SampleTerrainHeight(pts[0].x, pts[0].y, terrain, terrainPos);
                float endH   = SampleTerrainHeight(pts[n - 1].x, pts[n - 1].y, terrain, terrainPos);

                bridgeDeckH = new float[n];

                // Pass 1: straight grade clamped to minimum clearance
                for (int i = 0; i < n; i++)
                {
                    float t        = n > 1 ? (float)i / (n - 1) : 0f;
                    float terrainH = SampleTerrainHeight(pts[i].x, pts[i].y, terrain, terrainPos);
                    float straight = Mathf.Lerp(startH, endH, t);
                    bridgeDeckH[i] = Mathf.Max(straight, terrainH + bridgeClearance);
                }

                // Pass 2: forward smoothing – ensure no deck vertex is lower than
                // the previous one by more than a plausible descent (50 m / 1000 m = 5 %)
                // so the approach ramp stays driveable even when terrain dips abruptly.
                for (int i = 1; i < n; i++)
                {
                    float segmentLength = (pts[i] - pts[i - 1]).magnitude;
                    float maxDrop = segmentLength * 0.05f; // ≈ 5 % max downgrade
                    if (bridgeDeckH[i] < bridgeDeckH[i - 1] - maxDrop)
                        bridgeDeckH[i] = bridgeDeckH[i - 1] - maxDrop;
                }
                // Reverse pass for symmetric smoothing
                for (int i = n - 2; i >= 0; i--)
                {
                    float segmentLength = (pts[i + 1] - pts[i]).magnitude;
                    float maxDrop = segmentLength * 0.05f;
                    if (bridgeDeckH[i] < bridgeDeckH[i + 1] - maxDrop)
                        bridgeDeckH[i] = bridgeDeckH[i + 1] - maxDrop;
                }
            }

            // ── Vertices and UVs ──────────────────────────────────────────────────
            const float kMaxMiter = 2.5f; // guard against extreme miter at tight bends
            var vertices = new Vector3[n * 2];
            var uvs      = new Vector2[n * 2];

            for (int i = 0; i < n; i++)
            {
                // Miter scale: at interior vertices, stretch the offset so the
                // edge stays on the true boundary of the road through a bend.
                float miterLen = 1f;
                if (i > 0 && i < n - 1)
                {
                    Vector2 d0        = (pts[i] - pts[i - 1]).normalized;
                    Vector2 segNormal = new Vector2(d0.y, -d0.x);
                    float   dot       = Vector2.Dot(segNormal, normals[i]);
                    if (Mathf.Abs(dot) > 0.001f)
                        miterLen = Mathf.Clamp(1f / dot, -kMaxMiter, kMaxMiter);
                }

                Vector2 left  = pts[i] - normals[i] * (halfW * miterLen);
                Vector2 right = pts[i] + normals[i] * (halfW * miterLen);

                // For bridges the deck is level across its width; for ground roads
                // each edge is independently snapped to the terrain cross-slope.
                float yl, yr;
                if (bridgeDeckH != null)
                {
                    yl = yr = bridgeDeckH[i];
                }
                else
                {
                    yl = SampleTerrainHeight(left.x,  left.y,  terrain, terrainPos);
                    yr = SampleTerrainHeight(right.x, right.y, terrain, terrainPos);
                }

                float v = segLen[i] * uvScale;
                vertices[i]     = new Vector3(left.x  - terrainPos.x, yl - terrainPos.y, left.y  - terrainPos.z);
                vertices[n + i] = new Vector3(right.x - terrainPos.x, yr - terrainPos.y, right.y - terrainPos.z);
                uvs[i]          = new Vector2(0f, v);
                uvs[n + i]      = new Vector2(1f, v);
            }

            // ── Triangle indices ──────────────────────────────────────────────────
            // Winding: CCW when viewed from above (+Y) so RecalculateNormals
            // produces face normals that point upward (+Y = road surface faces sky).
            //
            // Quad layout per segment i (XZ plane, looking down):
            //   l0 ─── l1   (left / "North" edge at vertices[i], vertices[i+1])
            //    │       │
            //   r0 ─── r1   (right / "South" edge at vertices[n+i], vertices[n+i+1])
            //
            // CCW upper triangle : l0 → l1 → r0
            // CCW lower triangle : l1 → r1 → r0
            var tris = new int[(n - 1) * 6];
            for (int i = 0; i < n - 1; i++)
            {
                int t  = i * 6;
                int l0 = i,     r0 = n + i;
                int l1 = i + 1, r1 = n + i + 1;
                tris[t + 0] = l0; tris[t + 1] = l1; tris[t + 2] = r0;
                tris[t + 3] = l1; tris[t + 4] = r1; tris[t + 5] = r0;
            }

            var mesh = new Mesh { name = "HighwayRoadMesh" };
            mesh.vertices  = vertices;
            mesh.triangles = tris;
            mesh.uv        = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Returns the world-space Y of the terrain surface at the given world XZ
        /// position, plus a small offset to prevent z-fighting.
        ///
        /// Height accuracy: <see cref="Terrain.SampleHeight"/> reads the terrain's
        /// heightmap, which was populated from real USGS 3DEP 1/3 arc-second
        /// (~10 m resolution) elevation data by <see cref="GenerateChunk"/>.
        /// Road vertices therefore use the same accurate ground elevation that the
        /// terrain mesh itself uses.
        ///
        /// <see cref="Terrain.SampleHeight"/> returns height in the terrain's local
        /// coordinate space (above <c>terrain.transform.position.y</c>); adding
        /// <paramref name="terrainPos"/>.y converts to world-space Y.
        /// </summary>
        private static float SampleTerrainHeight(
            float worldX, float worldZ, Terrain terrain, Vector3 terrainPos)
        {
            const float kSurfaceOffset = 0.04f;
            if (terrain == null || terrain.terrainData == null)
                return terrainPos.y + kSurfaceOffset;
            return terrain.SampleHeight(new Vector3(worldX, 0f, worldZ))
                   + terrainPos.y + kSurfaceOffset;
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

#endif // UNITY_EDITOR

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
                        raw[r, c] = (v <= nodata || float.IsNaN(v)) ? 0f : Mathf.Max(0f, v);
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
            for (int r = 0; r < res; r++)
                for (int c = 0; c < res; c++)
                    heights[r, c] = Mathf.Clamp01((elevations[r, c] + halfHeight) / maxTerrainHeight);

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
