using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeadEverywhere.FloridaGen
{
    // ═════════════════════════════════════════════════════════════════════════
    //  Road design standards
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Design tables for the road classes the Florida generator meshes: widths, grade
    /// limits, structure depths and clearances.  Values follow AASHTO / FDOT practice
    /// closely enough that decks, ramps and tunnels read correctly at street level.
    /// </summary>
    public static class FloridaRoadStandards
    {
        /// <summary>Vertical gap (m) between the top of a road below and the underside of a deck.</summary>
        public const float UnderRoadClearance = 5.2f;
        /// <summary>Vertical gap (m) between the top of a railway below and the underside of a deck.</summary>
        public const float UnderRailClearance = 7.0f;

        /// <summary>True for OSM highway values that get a 3-D road surface.</summary>
        public static bool IsDrivable(string highway)
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

        /// <summary>0 = motorway/trunk, 1 = primary, 2 = secondary/tertiary, 3 = other.</summary>
        public static int Category(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk":    case "trunk_link":     return 0;
                case "primary":  case "primary_link":   return 1;
                case "secondary": case "secondary_link":
                case "tertiary":  case "tertiary_link": return 2;
                default:                                return 3;
            }
        }

        public static bool IsMotorwayClass(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk":    case "trunk_link": return true;
                default: return false;
            }
        }

        /// <summary>Default half-width (m) of the paved surface for a highway class.</summary>
        public static float DefaultHalfWidth(string highway)
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
        /// Half-width (m) of a mapped road: the <c>width</c> tag when present, otherwise the
        /// <c>lanes</c> tag, otherwise the class default.
        /// </summary>
        public static float HalfWidth(FloridaTerrainGenerator.OsmRoad road)
        {
            float half = DefaultHalfWidth(road.highway);
            if (road.width > 0.5f)
                half = road.width * 0.5f;
            else if (road.lanes > 0)
            {
                float w = IsMotorwayClass(road.highway) ? road.lanes * 3.7f + 3.0f
                                                         : road.lanes * 3.2f + 0.8f;
                half = w * 0.5f;
            }
            return Mathf.Clamp(half, 1.5f, 14f);
        }

        /// <summary>Steepest allowed grade (rise / run) of the road profile.</summary>
        public static float MaxGrade(string highway)
        {
            switch (highway)
            {
                case "motorway": case "trunk":                       return 0.05f;
                case "motorway_link": case "trunk_link":             return 0.07f;
                case "primary":                                      return 0.06f;
                case "secondary": case "tertiary":                   return 0.07f;
                case "primary_link": case "secondary_link":
                case "tertiary_link":                                return 0.08f;
                default:                                             return 0.09f;
            }
        }

        /// <summary>Structure depth (m) from the road surface to the underside of a bridge deck.</summary>
        public static float DeckDepth(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk":    case "trunk_link":    return 1.8f;
                case "primary":  case "primary_link":
                case "secondary": case "secondary_link": return 1.4f;
                default:                                return 1.0f;
            }
        }

        /// <summary>Free interior height (m) of a tunnel tube above the road surface.</summary>
        public static float TunnelHeight(string highway)
        {
            switch (highway)
            {
                case "motorway": case "motorway_link":
                case "trunk":    case "trunk_link":    return 5.0f;
                case "primary":  case "primary_link":
                case "secondary": case "secondary_link":
                case "tertiary": case "tertiary_link": return 4.6f;
                default:                                return 3.8f;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Plan data
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>How a road piece is carried relative to the ground.</summary>
    public enum RoadKind : byte
    {
        /// <summary>On the terrain (may still be lifted on an embankment).</summary>
        Ground,
        /// <summary>Elevated deck: bridge, flyover or ramp (bridge=yes or layer &gt; 0).</summary>
        Bridge,
        /// <summary>Covered tube (tunnel=yes).</summary>
        Tunnel
    }

    /// <summary>Linear feature a deck must clear (waterway, railway).</summary>
    public sealed class RoadObstacle
    {
        public Vector2[] points;
        /// <summary>Metres of free air required between the feature and the deck underside.</summary>
        public float clearance;
        public string label;
    }

    /// <summary>Inputs that tune <see cref="FloridaRoadTranslator.Translate"/>.</summary>
    public sealed class RoadTranslationSettings
    {
        /// <summary>World-XZ footprint the roads are clipped to (the terrain footprint).</summary>
        public Vector2 clipMin, clipMax;
        /// <summary>Maximum distance between profile stations.</summary>
        public float sampleSpacing = 5f;
        /// <summary>Moving-average radius (m) applied to ground and tunnel profiles.</summary>
        public float smoothingRadius = 8f;
        /// <summary>Moving-average radius (m) applied to bridge decks (gentler vertical curves).</summary>
        public float deckSmoothingRadius = 20f;
        /// <summary>
        /// Valleys in a deck profile narrower than twice this radius are filled in, so a
        /// viaduct stays level between the roads it crosses instead of dipping to the ground.
        /// </summary>
        public float deckClosingRadius = 400f;
        /// <summary>Height (m) of the road surface above the terrain surface.</summary>
        public float surfaceOffset = 0.06f;
        /// <summary>World Y of the terrain surface at world (x, z).  Must clamp outside the terrain.</summary>
        public Func<float, float, float> groundHeight;
    }

    /// <summary>
    /// One road (or the part of one road inside the chunk) with its solved height profile.
    /// </summary>
    public sealed class PlannedRoad
    {
        /// <summary>Index of the OSM way in the source array.</summary>
        public int source;
        public string highway = "";
        public string name = "";
        public string refTag = "";
        public RoadKind kind;
        /// <summary>Effective OSM layer (bridge without a layer tag counts as 1, tunnel as -1).</summary>
        public int layer;
        public int category;
        public float halfWidth;
        /// <summary>
        /// Paved half-width (m) of every station, or null when it is <see cref="halfWidth"/>
        /// throughout.  Narrower where a parallel road's footprint would otherwise overlap this one.
        /// </summary>
        public float[] halfWidthAt;
        /// <summary>
        /// Barrier suppression flags, two per station (left, right): true where the barrier would
        /// stand inside another road's paved width.  Null when every barrier is closed.
        /// </summary>
        public bool[] barrierOpen;
        /// <summary>
        /// Longest embankment slope run (m) at every station, two per station (left, right), so a
        /// slope stops short of a lower road beside it.  Null when the slopes are unconstrained.
        /// </summary>
        public float[] skirtRun;
        public float maxGrade;
        public float deckDepth;
        public float tunnelHeight;
        /// <summary>The footprint edge cut this end; the road continues in the neighbouring chunk.</summary>
        public bool cutStart, cutEnd;
        /// <summary>Tunnel only: close this end with a portal wall.</summary>
        public bool capStart, capEnd;
        /// <summary>Tunnel only: highest terrain above the portal-to-portal line (m); a hill the tube does not bore.</summary>
        public float tunnelHillRise;

        /// <summary>Station positions in world XZ.</summary>
        public Vector2[] pos;
        /// <summary>Distance (m) along the piece of every station.</summary>
        public float[] dist;
        /// <summary>Terrain surface plus the surface offset at every station.</summary>
        public float[] ground;
        /// <summary>Solved road surface height (world Y) at every station.</summary>
        public float[] height;
        /// <summary>Junction graph node of every station (shared by roads meeting at an OSM node).</summary>
        public int[] node;
        /// <summary>Lower bound used by the solver: ground, clearance over crossed roads, edge anchors.</summary>
        internal float[] lower;
        internal bool[] isVertex;
        /// <summary>Extra deck lift (m above terrain) demanded where the footprint edge cut a long bridge.</summary>
        internal float edgeLiftStart, edgeLiftEnd;
        /// <summary>Stretches [x = start, y = end] (metres along the piece) where a road passes underneath.</summary>
        public readonly List<Vector2> crossZones = new List<Vector2>();

        public float Length => dist[dist.Length - 1];

        /// <summary>Paved half-width (m) at station <paramref name="i"/>.</summary>
        public float HalfWidthAt(int i) => halfWidthAt != null ? halfWidthAt[i] : halfWidth;

        /// <summary>Paved half-width (m) <paramref name="s"/> metres along the piece.</summary>
        public float HalfWidthAtDist(float s) => halfWidthAt != null ? Interpolate(halfWidthAt, s) : halfWidth;

        /// <summary>True when the barrier on <paramref name="side"/> (-1 left, +1 right) is left open at station i.</summary>
        public bool BarrierOpen(int i, int side) => barrierOpen != null && barrierOpen[i * 2 + (side < 0 ? 0 : 1)];

        /// <summary>Longest embankment slope run (m) on <paramref name="side"/> at station i.</summary>
        public float SkirtRun(int i, int side) => skirtRun != null ? skirtRun[i * 2 + (side < 0 ? 0 : 1)] : float.MaxValue;

        /// <summary>Deck/road height interpolated at <paramref name="s"/> metres along the piece.</summary>
        public float HeightAt(float s) => Interpolate(height, s);

        /// <summary>Point on the centre-line <paramref name="s"/> metres along the piece.</summary>
        public Vector2 PointAt(float s)
        {
            int i = SegmentAt(s, out float t);
            return Vector2.Lerp(pos[i], pos[i + 1], t);
        }

        /// <summary>Largest unsigned grade between consecutive stations.</summary>
        public float MaxObservedGrade()
        {
            float g = 0f;
            for (int i = 0; i < pos.Length - 1; i++)
            {
                float d = dist[i + 1] - dist[i];
                if (d > 1e-3f) g = Mathf.Max(g, Mathf.Abs(height[i + 1] - height[i]) / d);
            }
            return g;
        }

        private float Interpolate(float[] values, float s)
        {
            int i = SegmentAt(s, out float t);
            return Mathf.Lerp(values[i], values[i + 1], t);
        }

        private int SegmentAt(float s, out float t)
        {
            int n = dist.Length;
            if (s <= dist[0]) { t = 0f; return 0; }
            if (s >= dist[n - 1]) { t = 1f; return n - 2; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (dist[mid] <= s) lo = mid; else hi = mid;
            }
            float d = dist[lo + 1] - dist[lo];
            t = d > 1e-5f ? (s - dist[lo]) / d : 0f;
            return lo;
        }
    }

    /// <summary>A grade separation: a deck passing over a road, waterway or railway.</summary>
    public struct RoadCrossing
    {
        /// <summary>Index into <see cref="RoadPlan.roads"/> of the deck.</summary>
        public int upper;
        /// <summary>Index into <see cref="RoadPlan.roads"/> of the road below, or -1 for an obstacle.</summary>
        public int lower;
        public Vector2 point;
        public float upperDist;
        /// <summary>Distance along the lower road (unused for obstacles).</summary>
        public float lowerDist;
        /// <summary>Free air (m) required between what is crossed and the deck underside.</summary>
        public float clearance;
        /// <summary>Surface height (world Y) of what the deck crosses, after solving.</summary>
        public float lowerHeight;
        /// <summary>Lowest allowed deck surface height at this crossing, after solving.</summary>
        public float requiredHeight;
        public string label;
    }

    /// <summary>Everything <see cref="FloridaRoadTranslator.Translate"/> produces.</summary>
    public sealed class RoadPlan
    {
        public readonly List<PlannedRoad> roads = new List<PlannedRoad>();
        public readonly List<RoadCrossing> crossings = new List<RoadCrossing>();
        /// <summary>Positions where three or more road ends meet.</summary>
        public readonly List<Vector2> junctions = new List<Vector2>();
        public readonly List<string> warnings = new List<string>();

        public int sourceRoads, outsideRoads, degeneratePieces;
        public int groundPieces, bridgePieces, tunnelPieces;
        public int embankmentStations;
        /// <summary>Stations whose paved width was reduced to clear a parallel road.</summary>
        public int narrowedStations;
        /// <summary>Barrier stations left open because another road's pavement is there.</summary>
        public int openBarrierStations;
        /// <summary>Embankment slopes steepened so they do not bury a lower road beside them.</summary>
        public int limitedSkirtStations;
        public float maxLift;
        public float maxGrade;
        public float worstGradeExcess;
        public int unmetCrossings;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Translator
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Turns raw OpenStreetMap road ways into a physically consistent 3-D road plan.
    /// This is the "retranslation" step between the OSM data and the mesh builder; it
    /// replaces the old per-road clearance heuristic.
    ///
    /// <b>Network.</b>  Every road is clipped to the chunk, resampled into stations, and
    /// stations at shared OSM vertices are merged into graph nodes, so ramps, bridges and
    /// the ground roads they meet are solved together.
    ///
    /// <b>Grade separations.</b>  Where a bridge's polyline crosses a road (or waterway or
    /// railway) with a lower layer, the deck is required to sit
    /// <c>lower surface + clearance + deck depth</c> above it across the whole width of
    /// what it crosses.  Layer stacks resolve bottom-up over several passes.
    ///
    /// <b>Profile.</b>  Heights are the lowest surface that is above every lower bound and
    /// never steeper than the road class grade (a max-propagation over the network graph).
    /// Because propagation crosses node boundaries, the ground roads leading to a bridge are
    /// lifted onto an approach embankment instead of the deck being squashed into a
    /// speed-bump tent.  Decks are then smoothed into vertical curves and valleys shorter
    /// than twice <see cref="RoadTranslationSettings.deckClosingRadius"/> are filled so a
    /// viaduct stays level between the roads it crosses.
    ///
    /// <b>Tunnels.</b>  Tunnel pieces follow the surface like ground roads (the terrain is
    /// never carved, so a tube cannot dive below it) and are wrapped in a tube by the mesh
    /// builder.
    /// </summary>
    public static class FloridaRoadTranslator
    {
        private const float kMinPtDist       = 0.05f;
        private const float kCrossCell       = 48f;
        private const float kMinCrossSin     = 0.30f;
        private const float kMaxCrossReach   = 70f;
        private const float kEdgeBridgeMinLen = 120f;
        private const float kTunnelHillWarn  = 3f;

        /// <summary>Builds the road plan for <paramref name="source"/>.</summary>
        public static RoadPlan Translate(
            FloridaTerrainGenerator.OsmRoad[] source,
            IList<RoadObstacle> obstacles,
            RoadTranslationSettings cfg)
        {
            var plan = new RoadPlan();
            if (source == null || cfg == null || cfg.groundHeight == null) return plan;

            float spacing = Mathf.Max(1f, cfg.sampleSpacing);

            // ── 1. Clip, classify and resample every road ─────────────────────
            for (int i = 0; i < source.Length; i++)
            {
                var r = source[i];
                if (r?.points == null || r.points.Length < 2) continue;
                if (!FloridaRoadStandards.IsDrivable(r.highway)) continue;
                plan.sourceRoads++;

                bool tunnel = r.isTunnel;
                bool bridge = !tunnel && (r.isBridge || r.layer > 0);
                int  layer  = r.layer;
                if (tunnel) layer = Mathf.Min(layer, -1);
                else if (bridge) layer = Mathf.Max(layer, 1);

                var pieces = ClipToRect(r.points, cfg.clipMin, cfg.clipMax);
                if (pieces.Count == 0) { plan.outsideRoads++; continue; }

                float fullLength = PolylineLength(r.points);
                foreach (var pc in pieces)
                {
                    var p = BuildPiece(r, i, pc, tunnel ? RoadKind.Tunnel : bridge ? RoadKind.Bridge : RoadKind.Ground,
                                       layer, fullLength, spacing, cfg);
                    if (p == null) { plan.degeneratePieces++; continue; }
                    plan.roads.Add(p);
                    switch (p.kind)
                    {
                        case RoadKind.Bridge: plan.bridgePieces++; break;
                        case RoadKind.Tunnel: plan.tunnelPieces++; break;
                        default:              plan.groundPieces++; break;
                    }
                }
            }
            if (plan.roads.Count == 0) return plan;

            // ── 2. Junction graph ─────────────────────────────────────────────
            int nodeCount = BuildNodes(plan, out int[] edgeHead, out int[] edgeNext, out int[] edgeTo,
                                       out float[] edgeGrade, out float[] edgeLen);
            FindJunctions(plan, nodeCount);
            MarkTunnelCaps(plan);

            // ── 3. Grade separations ──────────────────────────────────────────
            FindCrossings(plan, obstacles, cfg);

            // ── 4. Solve the height profile ───────────────────────────────────
            int maxLayer = 0;
            foreach (var p in plan.roads) maxLayer = Mathf.Max(maxLayer, p.layer);
            int passes = Mathf.Clamp(3 + maxLayer, 3, 6);
            Solve(plan, cfg, nodeCount, edgeHead, edgeNext, edgeTo, edgeGrade, edgeLen, passes);

            // ── 5. Footprints ─────────────────────────────────────────────────
            ResolveFootprints(plan);

            // ── 6. Diagnostics ────────────────────────────────────────────────
            Diagnose(plan);
            return plan;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Pieces
        // ─────────────────────────────────────────────────────────────────────

        private static PlannedRoad BuildPiece(
            FloridaTerrainGenerator.OsmRoad r, int sourceIndex, RoadPiece pc, RoadKind kind,
            int layer, float fullLength, float spacing, RoadTranslationSettings cfg)
        {
            var pos    = new List<Vector2>(pc.pts.Length * 2) { pc.pts[0] };
            var vertex = new List<bool>(pc.pts.Length * 2) { !pc.cutStart };
            for (int k = 1; k < pc.pts.Length; k++)
            {
                Vector2 a = pos[pos.Count - 1], b = pc.pts[k];
                float d = (b - a).magnitude;
                if (d < kMinPtDist) continue;
                int div = Mathf.Max(1, Mathf.CeilToInt(d / spacing));
                for (int m = 1; m <= div; m++)
                {
                    pos.Add(Vector2.Lerp(a, b, (float)m / div));
                    vertex.Add(m == div);
                }
            }
            int n = pos.Count;
            if (n < 2) return null;
            if (pc.cutEnd) vertex[n - 1] = false;

            var p = new PlannedRoad
            {
                source       = sourceIndex,
                highway      = r.highway ?? "",
                name         = r.name ?? "",
                refTag       = r.refTag ?? "",
                kind         = kind,
                layer        = layer,
                category     = FloridaRoadStandards.Category(r.highway),
                halfWidth    = FloridaRoadStandards.HalfWidth(r),
                maxGrade     = FloridaRoadStandards.MaxGrade(r.highway),
                deckDepth    = FloridaRoadStandards.DeckDepth(r.highway),
                tunnelHeight = FloridaRoadStandards.TunnelHeight(r.highway),
                cutStart     = pc.cutStart,
                cutEnd       = pc.cutEnd,
                pos          = pos.ToArray(),
                dist         = new float[n],
                ground       = new float[n],
                height       = new float[n],
                lower        = new float[n],
                node         = new int[n],
                isVertex     = vertex.ToArray()
            };

            for (int i = 1; i < n; i++)
                p.dist[i] = p.dist[i - 1] + (p.pos[i] - p.pos[i - 1]).magnitude;
            for (int i = 0; i < n; i++)
            {
                p.ground[i] = HighestTerrain(p, i, cfg) + cfg.surfaceOffset;
                p.height[i] = p.ground[i];
            }

            // A long elevated structure cut by the chunk edge continues into the next chunk
            // at deck height.  Both chunks anchor the cut at the same height above the shared
            // edge terrain, so the two halves meet.
            if (kind == RoadKind.Bridge && (layer >= 1 || fullLength >= kEdgeBridgeMinLen))
            {
                float lift = FloridaRoadStandards.UnderRoadClearance + p.deckDepth;
                p.edgeLiftStart = pc.cutStart ? lift : 0f;
                p.edgeLiftEnd   = pc.cutEnd   ? lift : 0f;
            }
            return p;
        }

        /// <summary>
        /// Highest bare terrain a road station must clear: the centre-line, both road edges and
        /// the midpoints to the neighbouring stations.  A road cut into a side slope, or over a
        /// bump between two stations, is then never buried by the terrain beside or under it.
        /// </summary>
        private static float HighestTerrain(PlannedRoad p, int i, RoadTranslationSettings cfg)
        {
            int n = p.pos.Length;
            Vector2 d = i == 0 ? p.pos[1] - p.pos[0]
                      : i == n - 1 ? p.pos[n - 1] - p.pos[n - 2]
                      : p.pos[i + 1] - p.pos[i - 1];
            d = d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
            Vector2 side = new Vector2(d.y, -d.x) * p.halfWidth;
            Vector2 c = p.pos[i];

            float g = Mathf.Max(cfg.groundHeight(c.x, c.y),
                      Mathf.Max(cfg.groundHeight(c.x + side.x, c.y + side.y),
                                cfg.groundHeight(c.x - side.x, c.y - side.y)));
            if (i > 0)
            {
                Vector2 m = (c + p.pos[i - 1]) * 0.5f;
                g = Mathf.Max(g, cfg.groundHeight(m.x, m.y));
            }
            if (i < n - 1)
            {
                Vector2 m = (c + p.pos[i + 1]) * 0.5f;
                g = Mathf.Max(g, cfg.groundHeight(m.x, m.y));
            }
            return g;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Junction graph
        // ─────────────────────────────────────────────────────────────────────

        private static long NodeKey(Vector2 p)
        {
            long x = Mathf.RoundToInt(p.x * 20f);
            long y = Mathf.RoundToInt(p.y * 20f);
            return (x << 32) ^ (y & 0xFFFFFFFFL);
        }

        /// <summary>
        /// Assigns every station a graph node (stations on a shared OSM vertex share a node)
        /// and builds the bidirectional edge lists used by the height propagation.
        /// </summary>
        private static int BuildNodes(
            RoadPlan plan, out int[] head, out int[] next, out int[] to, out float[] grade, out float[] len)
        {
            var keyToNode = new Dictionary<long, int>();
            int nodes = 0, edges = 0;
            foreach (var p in plan.roads)
            {
                for (int i = 0; i < p.pos.Length; i++)
                {
                    if (p.isVertex[i])
                    {
                        long key = NodeKey(p.pos[i]);
                        if (!keyToNode.TryGetValue(key, out int id)) { id = nodes++; keyToNode[key] = id; }
                        p.node[i] = id;
                    }
                    else p.node[i] = nodes++;
                }
                edges += (p.pos.Length - 1) * 2;
            }

            head  = new int[nodes];
            for (int i = 0; i < nodes; i++) head[i] = -1;
            next  = new int[edges];
            to    = new int[edges];
            grade = new float[edges];
            len   = new float[edges];

            int e = 0;
            foreach (var p in plan.roads)
            {
                for (int i = 0; i < p.pos.Length - 1; i++)
                {
                    int a = p.node[i], b = p.node[i + 1];
                    float l = p.dist[i + 1] - p.dist[i];
                    to[e] = b; grade[e] = p.maxGrade; len[e] = l; next[e] = head[a]; head[a] = e++;
                    to[e] = a; grade[e] = p.maxGrade; len[e] = l; next[e] = head[b]; head[b] = e++;
                }
            }
            return nodes;
        }

        private static void FindJunctions(RoadPlan plan, int nodeCount)
        {
            // Not stored per node; recount degree from the station arrays.
            var degree = new int[nodeCount];
            var seen   = new bool[nodeCount];
            foreach (var p in plan.roads)
            {
                int n = p.pos.Length;
                for (int i = 0; i < n; i++)
                {
                    int links = (i > 0 ? 1 : 0) + (i < n - 1 ? 1 : 0);
                    degree[p.node[i]] += links;
                }
            }
            foreach (var p in plan.roads)
                for (int i = 0; i < p.pos.Length; i++)
                {
                    int id = p.node[i];
                    if (degree[id] >= 3 && !seen[id]) { seen[id] = true; plan.junctions.Add(p.pos[i]); }
                }
        }

        /// <summary>Tunnel ends get a portal wall unless another tunnel piece continues from them.</summary>
        private static void MarkTunnelCaps(RoadPlan plan)
        {
            var tunnelAt = new Dictionary<int, int>();
            foreach (var p in plan.roads)
            {
                if (p.kind != RoadKind.Tunnel) continue;
                Count(tunnelAt, p.node[0]);
                Count(tunnelAt, p.node[p.node.Length - 1]);
            }
            foreach (var p in plan.roads)
            {
                if (p.kind != RoadKind.Tunnel) continue;
                p.capStart = !p.cutStart && tunnelAt[p.node[0]] < 2;
                p.capEnd   = !p.cutEnd   && tunnelAt[p.node[p.node.Length - 1]] < 2;
            }
        }

        private static void Count(Dictionary<int, int> d, int key)
        {
            d.TryGetValue(key, out int c);
            d[key] = c + 1;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Crossings
        // ─────────────────────────────────────────────────────────────────────

        private struct SegRef { public int piece, seg; }

        private static long CellKey(int cx, int cy) => ((long)cx << 32) ^ (cy & 0xFFFFFFFFL);

        private static void FindCrossings(RoadPlan plan, IList<RoadObstacle> obstacles, RoadTranslationSettings cfg)
        {
            // Hash every segment of every bridge piece; roads and obstacles below query it.
            var cells = new Dictionary<long, List<SegRef>>();
            bool anyBridge = false;
            for (int pi = 0; pi < plan.roads.Count; pi++)
            {
                var p = plan.roads[pi];
                if (p.kind != RoadKind.Bridge) continue;
                anyBridge = true;
                for (int s = 0; s < p.pos.Length - 1; s++)
                    ForCells(p.pos[s], p.pos[s + 1], (cx, cy) =>
                    {
                        long key = CellKey(cx, cy);
                        if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<SegRef>();
                        list.Add(new SegRef { piece = pi, seg = s });
                    });
            }
            if (!anyBridge) return;

            var found = new List<RoadCrossing>();

            // Roads passing underneath another road's deck.
            for (int qi = 0; qi < plan.roads.Count; qi++)
            {
                var q = plan.roads[qi];
                for (int qs = 0; qs < q.pos.Length - 1; qs++)
                {
                    Vector2 a0 = q.pos[qs], a1 = q.pos[qs + 1];
                    ForCells(a0, a1, (cx, cy) =>
                    {
                        if (!cells.TryGetValue(CellKey(cx, cy), out var list)) return;
                        foreach (var sr in list)
                        {
                            if (sr.piece == qi) continue;
                            var p = plan.roads[sr.piece];
                            if (p.layer <= q.layer) continue;
                            if (p.node[sr.seg] == q.node[qs] || p.node[sr.seg] == q.node[qs + 1] ||
                                p.node[sr.seg + 1] == q.node[qs] || p.node[sr.seg + 1] == q.node[qs + 1])
                                continue;       // connected, not a crossing

                            if (!SegmentIntersect(p.pos[sr.seg], p.pos[sr.seg + 1], a0, a1,
                                                  out float t, out float u, out float sin))
                                continue;

                            float sP = p.dist[sr.seg] + t * (p.dist[sr.seg + 1] - p.dist[sr.seg]);
                            float sQ = q.dist[qs]     + u * (q.dist[qs + 1]     - q.dist[qs]);
                            AddCrossing(found, new RoadCrossing
                            {
                                upper = sr.piece, lower = qi,
                                point = Vector2.Lerp(p.pos[sr.seg], p.pos[sr.seg + 1], t),
                                upperDist = sP, lowerDist = sQ,
                                // A tunnel tube follows the surface, so a deck over it must also clear the tube.
                                clearance = FloridaRoadStandards.UnderRoadClearance +
                                            (q.kind == RoadKind.Tunnel ? q.tunnelHeight + 1.1f : 0f),
                                label = q.highway
                            });
                        }
                    });
                }
            }

            // Waterways and railways passing underneath a deck.
            if (obstacles != null)
            {
                foreach (var ob in obstacles)
                {
                    if (ob?.points == null || ob.points.Length < 2) continue;
                    for (int os = 0; os < ob.points.Length - 1; os++)
                    {
                        Vector2 a0 = ob.points[os], a1 = ob.points[os + 1];
                        ForCells(a0, a1, (cx, cy) =>
                        {
                            if (!cells.TryGetValue(CellKey(cx, cy), out var list)) return;
                            foreach (var sr in list)
                            {
                                var p = plan.roads[sr.piece];
                                if (!SegmentIntersect(p.pos[sr.seg], p.pos[sr.seg + 1], a0, a1,
                                                      out float t, out float u, out float sin))
                                    continue;
                                float sP = p.dist[sr.seg] + t * (p.dist[sr.seg + 1] - p.dist[sr.seg]);
                                Vector2 hit = Vector2.Lerp(p.pos[sr.seg], p.pos[sr.seg + 1], t);
                                AddCrossing(found, new RoadCrossing
                                {
                                    upper = sr.piece, lower = -1,
                                    point = hit, upperDist = sP,
                                    clearance = ob.clearance,
                                    lowerHeight = cfg.groundHeight(hit.x, hit.y) + cfg.surfaceOffset,
                                    label = ob.label
                                });
                            }
                        });
                    }
                }
            }

            plan.crossings.AddRange(found);
        }

        /// <summary>
        /// Stores a crossing unless the same deck already has one with the same lower road
        /// within a few metres (a crossing on a segment boundary is found twice).
        /// </summary>
        private static void AddCrossing(List<RoadCrossing> list, RoadCrossing c)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.upper == c.upper && e.lower == c.lower && e.label == c.label &&
                    (e.point - c.point).sqrMagnitude < 9f)
                    return;
            }
            list.Add(c);
        }

        private static void ForCells(Vector2 a, Vector2 b, Action<int, int> visit)
        {
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / kCrossCell);
            int x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / kCrossCell);
            int y0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / kCrossCell);
            int y1 = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / kCrossCell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    visit(x, y);
        }

        /// <summary>Proper intersection of segments p0-p1 and q0-q1; <paramref name="sin"/> is the sine of the crossing angle.</summary>
        private static bool SegmentIntersect(
            Vector2 p0, Vector2 p1, Vector2 q0, Vector2 q1, out float t, out float u, out float sin)
        {
            t = u = sin = 0f;
            Vector2 r = p1 - p0, s = q1 - q0;
            float den = r.x * s.y - r.y * s.x;
            float rl = r.magnitude, sl = s.magnitude;
            if (rl < 1e-4f || sl < 1e-4f) return false;
            sin = Mathf.Abs(den) / (rl * sl);
            if (sin < 1e-3f) return false;
            t = ((q0.x - p0.x) * s.y - (q0.y - p0.y) * s.x) / den;
            u = ((q0.x - p0.x) * r.y - (q0.y - p0.y) * r.x) / den;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Height solver
        // ─────────────────────────────────────────────────────────────────────

        private static void Solve(
            RoadPlan plan, RoadTranslationSettings cfg, int nodeCount,
            int[] head, int[] next, int[] to, float[] grade, float[] len, int passes)
        {
            var val = new float[nodeCount];
            float negInf = float.NegativeInfinity;

            for (int pass = 0; pass < passes; pass++)
            {
                // Lower bounds: never below the terrain, plus clearance over crossed features.
                foreach (var p in plan.roads)
                {
                    Array.Copy(p.ground, p.lower, p.ground.Length);
                    if (p.edgeLiftStart > 0f) p.lower[0] = Mathf.Max(p.lower[0], p.ground[0] + p.edgeLiftStart);
                    int last = p.lower.Length - 1;
                    if (p.edgeLiftEnd > 0f) p.lower[last] = Mathf.Max(p.lower[last], p.ground[last] + p.edgeLiftEnd);
                    p.crossZones.Clear();
                }
                for (int ci = 0; ci < plan.crossings.Count; ci++)
                {
                    var c = plan.crossings[ci];
                    var up = plan.roads[c.upper];
                    UpdateCrossing(plan, ref c);
                    plan.crossings[ci] = c;

                    float lowerHalf = 2f, sin = 1f;
                    if (c.lower >= 0)
                    {
                        var lo = plan.roads[c.lower];
                        lowerHalf = lo.halfWidth;
                        sin       = CrossSin(up, c.upperDist, lo, c.lowerDist);
                    }
                    float s    = Mathf.Max(sin, kMinCrossSin);
                    float cosA = Mathf.Sqrt(Mathf.Max(0f, 1f - sin * sin));
                    float reach = Mathf.Min(kMaxCrossReach, (lowerHalf + 2f + up.halfWidth * cosA) / s);
                    ApplyBound(up, c.upperDist - reach, c.upperDist + reach, c.requiredHeight);
                    up.crossZones.Add(new Vector2(c.upperDist - reach, c.upperDist + reach));
                }

                // Lowest surface above every bound with no slope steeper than the class grade.
                for (int i = 0; i < nodeCount; i++) val[i] = negInf;
                foreach (var p in plan.roads)
                    for (int i = 0; i < p.node.Length; i++)
                        val[p.node[i]] = Mathf.Max(val[p.node[i]], p.lower[i]);
                Propagate(val, head, next, to, grade, len);
                foreach (var p in plan.roads)
                    for (int i = 0; i < p.node.Length; i++)
                        p.height[i] = val[p.node[i]];

                // Keep bridge decks level between the roads they cross, then round off the
                // profile into vertical curves.  The lower bounds are re-applied afterwards.
                foreach (var p in plan.roads)
                {
                    float radius = p.kind == RoadKind.Bridge ? cfg.deckSmoothingRadius : cfg.smoothingRadius;
                    var h = p.height;
                    if (p.kind == RoadKind.Bridge && cfg.deckClosingRadius > 0f)
                        h = Close(p.dist, h, cfg.deckClosingRadius, Mathf.Max(1f, cfg.sampleSpacing));
                    h = Smooth(p.dist, h, radius);
                    for (int i = 0; i < h.Length; i++)
                        p.height[i] = Mathf.Max(h[i], p.lower[i]);
                }

                for (int i = 0; i < nodeCount; i++) val[i] = negInf;
                foreach (var p in plan.roads)
                    for (int i = 0; i < p.node.Length; i++)
                        val[p.node[i]] = Mathf.Max(val[p.node[i]], p.height[i]);
                Propagate(val, head, next, to, grade, len);
                foreach (var p in plan.roads)
                    for (int i = 0; i < p.node.Length; i++)
                        p.height[i] = val[p.node[i]];
            }
        }

        /// <summary>Refreshes what the deck crosses (its current surface height) and the deck height this demands.</summary>
        private static void UpdateCrossing(RoadPlan plan, ref RoadCrossing c)
        {
            if (c.lower >= 0)
                c.lowerHeight = plan.roads[c.lower].HeightAt(c.lowerDist);
            c.requiredHeight = c.lowerHeight + c.clearance + plan.roads[c.upper].deckDepth;
        }

        private static float CrossSin(PlannedRoad a, float sa, PlannedRoad b, float sb)
        {
            Vector2 da = Direction(a, sa), db = Direction(b, sb);
            return Mathf.Abs(da.x * db.y - da.y * db.x);
        }

        private static Vector2 Direction(PlannedRoad p, float s)
        {
            int n = p.dist.Length;
            int i = 0;
            while (i < n - 2 && p.dist[i + 1] < s) i++;
            Vector2 d = p.pos[i + 1] - p.pos[i];
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
        }

        private static void ApplyBound(PlannedRoad p, float s0, float s1, float required)
        {
            for (int i = 0; i < p.dist.Length; i++)
                if (p.dist[i] >= s0 && p.dist[i] <= s1)
                    p.lower[i] = Mathf.Max(p.lower[i], required);
        }

        /// <summary>
        /// Raises every node to the lowest surface that satisfies all lower bounds without
        /// any edge being steeper than its grade (best-first max-propagation).
        /// </summary>
        private static void Propagate(float[] val, int[] head, int[] next, int[] to, float[] grade, float[] len)
        {
            var heap = new MaxHeap(val.Length);
            for (int i = 0; i < val.Length; i++)
                if (val[i] > float.NegativeInfinity) heap.Push(val[i], i);

            while (heap.Count > 0)
            {
                heap.Pop(out float v, out int n);
                if (v < val[n]) continue;                         // stale entry
                for (int e = head[n]; e >= 0; e = next[e])
                {
                    float cand = v - grade[e] * len[e];
                    int   m    = to[e];
                    if (cand > val[m] + 1e-4f) { val[m] = cand; heap.Push(cand, m); }
                }
            }
        }

        /// <summary>Moving average over distance; the window shrinks to zero at both ends so end heights are untouched.</summary>
        private static float[] Smooth(float[] dist, float[] h, float radius)
        {
            int n = h.Length;
            var o = new float[n];
            float total = dist[n - 1];
            radius = Mathf.Max(0f, radius);
            for (int i = 0; i < n; i++)
            {
                float r = Mathf.Min(radius, Mathf.Min(dist[i], total - dist[i]));
                if (r <= 0.01f) { o[i] = h[i]; continue; }
                float sum = 0f; int cnt = 0;
                for (int j = i; j >= 0 && dist[i] - dist[j] <= r; j--) { sum += h[j]; cnt++; }
                for (int j = i + 1; j < n && dist[j] - dist[i] <= r; j++) { sum += h[j]; cnt++; }
                o[i] = sum / cnt;
            }
            return o;
        }

        /// <summary>
        /// Morphological closing (dilate then erode over ±<paramref name="window"/> metres) with the
        /// end values extended outwards, which fills valleys narrower than twice the window and
        /// leaves monotone ramps and the end heights untouched.
        /// </summary>
        private static float[] Close(float[] dist, float[] h, float window, float step)
        {
            int n = h.Length;
            int pad = Mathf.CeilToInt(2f * window / step) + 2;
            int m = n + 2 * pad;
            var d = new float[m];
            var v = new float[m];
            for (int k = 0; k < pad; k++) { d[k] = dist[0] - (pad - k) * step; v[k] = h[0]; }
            for (int i = 0; i < n; i++)   { d[pad + i] = dist[i]; v[pad + i] = h[i]; }
            for (int k = 0; k < pad; k++) { d[pad + n + k] = dist[n - 1] + (k + 1) * step; v[pad + n + k] = h[n - 1]; }

            var dil = new float[m];
            for (int i = 0; i < m; i++)
            {
                float mx = v[i];
                for (int j = i - 1; j >= 0 && d[i] - d[j] <= window; j--) mx = Mathf.Max(mx, v[j]);
                for (int j = i + 1; j < m && d[j] - d[i] <= window; j++)   mx = Mathf.Max(mx, v[j]);
                dil[i] = mx;
            }
            var o = new float[n];
            for (int i = pad; i < pad + n; i++)
            {
                float mn = dil[i];
                for (int j = i - 1; j >= 0 && d[i] - d[j] <= window; j--) mn = Mathf.Min(mn, dil[j]);
                for (int j = i + 1; j < m && d[j] - d[i] <= window; j++)   mn = Mathf.Min(mn, dil[j]);
                o[i - pad] = mn;
            }
            return o;
        }

        private sealed class MaxHeap
        {
            private float[] _key;
            private int[]   _val;
            public int Count { get; private set; }

            public MaxHeap(int capacity)
            {
                _key = new float[Mathf.Max(16, capacity)];
                _val = new int[_key.Length];
            }

            public void Push(float key, int val)
            {
                if (Count == _key.Length)
                {
                    Array.Resize(ref _key, Count * 2);
                    Array.Resize(ref _val, Count * 2);
                }
                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_key[parent] >= key) break;
                    _key[i] = _key[parent]; _val[i] = _val[parent];
                    i = parent;
                }
                _key[i] = key; _val[i] = val;
            }

            public void Pop(out float key, out int val)
            {
                key = _key[0]; val = _val[0];
                Count--;
                if (Count == 0) return;
                float k = _key[Count]; int v = _val[Count];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= Count) break;
                    if (c + 1 < Count && _key[c + 1] > _key[c]) c++;
                    if (_key[c] <= k) break;
                    _key[i] = _key[c]; _val[i] = _val[c];
                    i = c;
                }
                _key[i] = k; _val[i] = v;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Footprints
        // ─────────────────────────────────────────────────────────────────────

        private const float kFootMinLift   = 0.6f;    // ground road this far above terrain has barriers/skirts
        private const float kFootMinHalf   = 2.0f;    // never narrower than a 4 m carriageway
        private const float kFootTaper     = 0.4f;    // metres of width regained per metre along the road
        private const float kFootMergeReach = 90f;    // roads that meet at a node overlap by design this far out
        private const float kFootVehicle   = 3.6f;    // free height needed beside/over a lane
        private const float kFootCoplanar  = 0.35f;   // height difference (m) below which joined roads share a pavement
        private const float kFootKeep      = 0.5f;    // share of its width a road always keeps (a deck is never squeezed further)
        private const float kFootEdgeSolid = 0.45f;   // barrier thickness
        private const float kFootEdgeTunnel = 0.9f;   // tunnel wall + curb
        private const float kFootSkirtReach = 40f;    // an embankment slope is never longer than this
        private const float kFootSkirtMinRun = 0.5f;  // steepest slope: a retaining wall

        private sealed class FootJunctions
        {
            public float[] arc;
            public int[] node;
        }

        /// <summary>A neighbouring road segment found near a station.</summary>
        private struct FootHit
        {
            public int piece, seg;
            public float t, dist, height, lift, arc;
            public Vector2 point;
        }

        /// <summary>Spatial hash of every road segment, queried for the roads beside a station.</summary>
        private sealed class FootIndex
        {
            private readonly RoadPlan _plan;
            private readonly Dictionary<long, List<SegRef>> _cells = new Dictionary<long, List<SegRef>>();
            private readonly int[] _segBase;
            private readonly int[] _stamp;
            private int _query;

            public FootIndex(RoadPlan plan)
            {
                _plan = plan;
                int count = plan.roads.Count;
                _segBase = new int[count + 1];
                for (int pi = 0; pi < count; pi++)
                {
                    var p = plan.roads[pi];
                    _segBase[pi + 1] = _segBase[pi] + p.pos.Length - 1;
                    for (int s = 0; s < p.pos.Length - 1; s++)
                    {
                        int piece = pi, seg = s;
                        ForCells(p.pos[s], p.pos[s + 1], (cx, cy) =>
                        {
                            long key = CellKey(cx, cy);
                            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<SegRef>();
                            list.Add(new SegRef { piece = piece, seg = seg });
                        });
                    }
                }
                _stamp = new int[_segBase[count]];
            }

            /// <summary>
            /// Nearest point of every other road segment within <paramref name="radius"/> of
            /// <paramref name="pt"/>.  Road ends that the nearest point snaps to are skipped:
            /// those are a road that continues, not one running alongside.
            /// </summary>
            public void Query(Vector2 pt, int self, float radius, List<FootHit> hits)
            {
                hits.Clear();
                _query++;
                int cx0 = Mathf.FloorToInt(pt.x / kCrossCell), cy0 = Mathf.FloorToInt(pt.y / kCrossCell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (!_cells.TryGetValue(CellKey(cx0 + dx, cy0 + dy), out var list)) continue;
                        foreach (var sr in list)
                        {
                            if (sr.piece == self) continue;
                            int sid = _segBase[sr.piece] + sr.seg;
                            if (_stamp[sid] == _query) continue;
                            _stamp[sid] = _query;

                            var b = _plan.roads[sr.piece];
                            Vector2 b0 = b.pos[sr.seg], b1 = b.pos[sr.seg + 1];
                            Vector2 ab = b1 - b0;
                            float len2 = ab.sqrMagnitude;
                            if (len2 < 1e-8f) continue;
                            float t = Mathf.Clamp01(Vector2.Dot(pt - b0, ab) / len2);
                            if ((t <= 0f && sr.seg == 0) || (t >= 1f && sr.seg == b.pos.Length - 2)) continue;
                            float d = (pt - (b0 + ab * t)).magnitude;
                            if (d > radius) continue;
                            hits.Add(new FootHit
                            {
                                piece = sr.piece, seg = sr.seg, t = t, dist = d, point = b0 + ab * t,
                                height = Mathf.Lerp(b.height[sr.seg], b.height[sr.seg + 1], t),
                                lift = Mathf.Lerp(b.height[sr.seg] - b.ground[sr.seg], b.height[sr.seg + 1] - b.ground[sr.seg + 1], t),
                                arc = Mathf.Lerp(b.dist[sr.seg], b.dist[sr.seg + 1], t)
                            });
                        }
                    }
            }
        }

        private static bool FootSolid(PlannedRoad p, float lift) => p.kind != RoadKind.Ground || lift >= kFootMinLift;

        /// <summary>Free height a vehicle needs between two overlapping roads, including the upper deck's depth.</summary>
        private static float FootSeparation(PlannedRoad a, float ha, PlannedRoad b, float hb)
        {
            PlannedRoad upper = ha >= hb ? a : b;
            return kFootVehicle + (upper.kind == RoadKind.Bridge ? upper.deckDepth : 0f);
        }

        /// <summary>
        /// Resolves how roads share the space beside each other.
        ///
        /// <b>Width.</b>  Narrows the paved width of a road wherever the footprint of a nearly
        /// parallel road at a similar height would overlap it.  Two carriageways drawn 8 m apart
        /// would otherwise both be 14 m wide: barriers and walls would stand inside the other
        /// lane and two decks a few decimetres apart would overlap.  The room between the two
        /// centre-lines is shared in proportion to the nominal widths.  Roads that meet at a node
        /// (ramp gores, merges) overlap by design and keep their width, as do roads that cross at
        /// a real angle (grade separations) and roads whose heights differ enough for a vehicle
        /// to pass between them.
        ///
        /// <b>Barriers.</b>  Wherever a barrier line would end up inside another road's paved
        /// width (the gore of a ramp, a merge) the barrier is left open, so it never walls off a
        /// lane that continues past it.
        /// </summary>
        private static void ResolveFootprints(RoadPlan plan)
        {
            int count = plan.roads.Count;
            var structured = false;
            foreach (var p in plan.roads)
            {
                if (p.kind != RoadKind.Ground) { structured = true; break; }
                for (int i = 0; i < p.height.Length; i++)
                    if (p.height[i] - p.ground[i] >= kFootMinLift) { structured = true; break; }
                if (structured) break;
            }
            if (!structured) return;

            // Junction vertices of every piece (nodes shared with another piece).
            var firstPiece = new Dictionary<int, int>();
            for (int pi = 0; pi < count; pi++)
            {
                var p = plan.roads[pi];
                for (int i = 0; i < p.pos.Length; i++)
                {
                    if (!p.isVertex[i]) continue;
                    if (!firstPiece.TryGetValue(p.node[i], out int first)) firstPiece[p.node[i]] = pi;
                    else if (first != pi) firstPiece[p.node[i]] = -2;
                }
            }
            var junctions = new FootJunctions[count];
            for (int pi = 0; pi < count; pi++)
            {
                var p = plan.roads[pi];
                var arcs = new List<float>(); var nodes = new List<int>();
                for (int i = 0; i < p.pos.Length; i++)
                    if (p.isVertex[i] && firstPiece.TryGetValue(p.node[i], out int f) && f == -2)
                    { arcs.Add(p.dist[i]); nodes.Add(p.node[i]); }
                junctions[pi] = new FootJunctions { arc = arcs.ToArray(), node = nodes.ToArray() };
            }

            var index = new FootIndex(plan);
            var hits = new List<FootHit>();

            // ── Widths ────────────────────────────────────────────────────────
            for (int pi = 0; pi < count; pi++)
            {
                var a = plan.roads[pi];
                int n = a.pos.Length;
                var cap = new float[n];
                bool narrowed = false;

                for (int i = 0; i < n; i++)
                {
                    cap[i] = a.halfWidth;
                    float floor = Mathf.Max(kFootMinHalf, kFootKeep * a.halfWidth);
                    Vector2 ta = StationTangent(a, i);
                    bool solidA = FootSolid(a, a.height[i] - a.ground[i]);
                    index.Query(a.pos[i], pi, a.halfWidth + 12f, hits);
                    foreach (var h in hits)
                    {
                        var b = plan.roads[h.piece];
                        if (h.dist > a.halfWidth + b.halfWidth + 2f) continue;
                        if (!solidA && !FootSolid(b, h.lift)) continue;
                        if (Mathf.Abs(a.height[i] - h.height) >= FootSeparation(a, a.height[i], b, h.height)) continue;

                        Vector2 tb = (b.pos[h.seg + 1] - b.pos[h.seg]).normalized;
                        if (Mathf.Abs(ta.x * tb.y - ta.y * tb.x) >= kMinCrossSin) continue;

                        // Roads that meet at a node share one pavement at first (a gore); they only
                        // need separating once they are no longer level with each other.
                        if (FootConnected(junctions[pi], a.dist[i], junctions[h.piece], h.arc) &&
                            Mathf.Abs(a.height[i] - h.height) < kFootCoplanar) continue;

                        float gap = FootEdge(a) + FootEdge(b) + 0.2f;
                        float room = a.halfWidth * (h.dist - gap) / (a.halfWidth + b.halfWidth);
                        cap[i] = Mathf.Min(cap[i], Mathf.Max(floor, room));
                    }
                    if (cap[i] < a.halfWidth - 0.05f) narrowed = true;
                }
                if (!narrowed) continue;

                // Widen gradually away from the conflict instead of stepping.
                for (int i = 1; i < n; i++)
                    cap[i] = Mathf.Min(cap[i], cap[i - 1] + kFootTaper * (a.dist[i] - a.dist[i - 1]));
                for (int i = n - 2; i >= 0; i--)
                    cap[i] = Mathf.Min(cap[i], cap[i + 1] + kFootTaper * (a.dist[i + 1] - a.dist[i]));
                for (int i = 0; i < n; i++)
                    if (cap[i] < a.halfWidth - 0.05f) plan.narrowedStations++;
                a.halfWidthAt = cap;
            }

            // ── Barriers ──────────────────────────────────────────────────────
            for (int pi = 0; pi < count; pi++)
            {
                var a = plan.roads[pi];
                if (a.kind == RoadKind.Tunnel) continue;
                int n = a.pos.Length;
                bool[] open = null;
                for (int i = 0; i < n; i++)
                {
                    if (!FootSolid(a, a.height[i] - a.ground[i])) continue;
                    Vector2 t = StationTangent(a, i);
                    Vector2 right = new Vector2(t.y, -t.x);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector2 edge = a.pos[i] + right * (side * (a.HalfWidthAt(i) + kFootEdgeSolid * 0.5f));
                        index.Query(edge, pi, 16f, hits);
                        foreach (var h in hits)
                        {
                            var b = plan.roads[h.piece];
                            if (h.dist > Mathf.Lerp(b.HalfWidthAt(h.seg), b.HalfWidthAt(h.seg + 1), h.t) + 0.1f) continue;
                            if (Mathf.Abs(a.height[i] - h.height) >= FootSeparation(a, a.height[i], b, h.height)) continue;
                            if (open == null) open = new bool[n * 2];
                            open[i * 2 + (side < 0 ? 0 : 1)] = true;
                            plan.openBarrierStations++;
                            break;
                        }
                    }
                }
                a.barrierOpen = open;
            }

            // ── Embankment slopes ─────────────────────────────────────────────
            for (int pi = 0; pi < count; pi++)
            {
                var a = plan.roads[pi];
                if (a.kind != RoadKind.Ground) continue;
                int n = a.pos.Length;
                float[] run = null;
                for (int i = 0; i < n; i++)
                {
                    if (a.height[i] - a.ground[i] < kFootMinLift) continue;
                    Vector2 t = StationTangent(a, i);
                    Vector2 right = new Vector2(t.y, -t.x);
                    index.Query(a.pos[i], pi, kFootSkirtReach, hits);
                    foreach (var h in hits)
                    {
                        var b = plan.roads[h.piece];
                        if (h.height > a.height[i] - 0.3f) continue;
                        Vector2 tb = (b.pos[h.seg + 1] - b.pos[h.seg]).normalized;
                        if (Mathf.Abs(t.x * tb.y - t.y * tb.x) >= kMinCrossSin) continue;
                        float lateral = Vector2.Dot(h.point - a.pos[i], right);
                        int side = lateral < 0f ? -1 : 1;
                        float room = Mathf.Abs(lateral) - (a.HalfWidthAt(i) + kFootEdgeSolid)
                                     - Mathf.Lerp(b.HalfWidthAt(h.seg), b.HalfWidthAt(h.seg + 1), h.t) - 0.4f;
                        float limit = Mathf.Max(kFootSkirtMinRun, room);
                        if (limit >= kFootSkirtReach) continue;
                        if (run == null)
                        {
                            run = new float[n * 2];
                            for (int k = 0; k < run.Length; k++) run[k] = float.MaxValue;
                        }
                        int slot = i * 2 + (side < 0 ? 0 : 1);
                        if (limit < run[slot]) run[slot] = limit;
                    }
                }
                if (run == null) continue;
                for (int k = 0; k < run.Length; k++) if (run[k] < float.MaxValue) plan.limitedSkirtStations++;
                a.skirtRun = run;
            }
        }

        private static float FootEdge(PlannedRoad p) => p.kind == RoadKind.Tunnel ? kFootEdgeTunnel : kFootEdgeSolid;

        private static Vector2 StationTangent(PlannedRoad p, int i)
        {
            int n = p.pos.Length;
            Vector2 d = p.pos[Mathf.Min(i + 1, n - 1)] - p.pos[Mathf.Max(i - 1, 0)];
            return d.sqrMagnitude > 1e-10f ? d.normalized : Vector2.right;
        }

        /// <summary>True when the two roads meet at a node, both stations being within merge reach of it.</summary>
        private static bool FootConnected(FootJunctions a, float sa, FootJunctions b, float sb)
        {
            for (int i = 0; i < a.node.Length; i++)
            {
                if (Mathf.Abs(a.arc[i] - sa) > kFootMergeReach) continue;
                for (int j = 0; j < b.node.Length; j++)
                    if (b.node[j] == a.node[i] && Mathf.Abs(b.arc[j] - sb) <= kFootMergeReach) return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Diagnostics
        // ─────────────────────────────────────────────────────────────────────

        private static void Diagnose(RoadPlan plan)
        {
            foreach (var p in plan.roads)
            {
                for (int i = 0; i < p.height.Length; i++)
                {
                    float lift = p.height[i] - p.ground[i];
                    if (lift > plan.maxLift) plan.maxLift = lift;
                    if (p.kind == RoadKind.Ground && lift > 0.35f) plan.embankmentStations++;
                }
                float g = p.MaxObservedGrade();
                plan.maxGrade = Mathf.Max(plan.maxGrade, g);
                plan.worstGradeExcess = Mathf.Max(plan.worstGradeExcess, g - p.maxGrade);

                if (p.kind == RoadKind.Tunnel)
                {
                    int n = p.height.Length;
                    float h0 = p.height[0], h1 = p.height[n - 1], rise = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        float t = p.Length > 0f ? p.dist[i] / p.Length : 0f;
                        rise = Mathf.Max(rise, p.ground[i] - Mathf.Lerp(h0, h1, t));
                    }
                    p.tunnelHillRise = rise;
                    if (rise > kTunnelHillWarn)
                        plan.warnings.Add($"Tunnel '{p.highway}' (way {p.source}) runs under terrain {rise:F1} m above its " +
                                          "portals. The terrain is not carved, so the tube follows the surface.");
                }
            }
            for (int i = 0; i < plan.crossings.Count; i++)
            {
                var c = plan.crossings[i];
                UpdateCrossing(plan, ref c);
                plan.crossings[i] = c;
                if (plan.roads[c.upper].HeightAt(c.upperDist) < c.requiredHeight - 0.05f) plan.unmetCrossings++;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Clipping
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Part of a road that lies inside the terrain footprint.  A cut end was made by
        /// the footprint edge rather than being a real end of the OSM way, so the road
        /// continues there (into the neighbouring chunk).
        /// </summary>
        public readonly struct RoadPiece
        {
            public readonly Vector2[] pts;
            public readonly bool cutStart, cutEnd;

            public RoadPiece(Vector2[] pts, bool cutStart, bool cutEnd)
            {
                this.pts = pts; this.cutStart = cutStart; this.cutEnd = cutEnd;
            }
        }

        public static float PolylineLength(Vector2[] pts)
        {
            float l = 0f;
            for (int i = 0; i < pts.Length - 1; i++) l += (pts[i + 1] - pts[i]).magnitude;
            return l;
        }

        /// <summary>
        /// Clips a world-XZ centre-line to the rectangle [<paramref name="min"/>,
        /// <paramref name="max"/>].  Overpass returns the complete geometry of every way
        /// that touches the chunk, so roads routinely run far past the terrain edge, where
        /// the terrain height is only the clamped edge height.  A way that leaves and
        /// re-enters yields several pieces.
        /// </summary>
        public static List<RoadPiece> ClipToRect(Vector2[] pts, Vector2 min, Vector2 max)
        {
            var  pieces   = new List<RoadPiece>();
            var  cur      = new List<Vector2>();
            bool cutStart = false;

            void Flush(bool cutEnd)
            {
                if (cur.Count >= 2) pieces.Add(new RoadPiece(cur.ToArray(), cutStart, cutEnd));
                cur.Clear();
            }

            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = pts[i], d = pts[i + 1] - a;
                if (!ClipSegment(a, d, min, max, out float t0, out float t1)) { Flush(true); continue; }

                if (cur.Count == 0)
                {
                    cutStart = i > 0 || t0 > 0f;
                    cur.Add(a + d * t0);
                }

                if (t1 < 1f) { cur.Add(a + d * t1); Flush(true); }
                else           cur.Add(pts[i + 1]);
            }
            Flush(false);
            return pieces;
        }

        /// <summary>Liang-Barsky: the part [t0, t1] of segment a + t·d inside the rectangle.</summary>
        private static bool ClipSegment(
            Vector2 a, Vector2 d, Vector2 min, Vector2 max, out float t0, out float t1)
        {
            t0 = 0f; t1 = 1f;
            return ClipEdge(-d.x, a.x - min.x, ref t0, ref t1) &&
                   ClipEdge( d.x, max.x - a.x, ref t0, ref t1) &&
                   ClipEdge(-d.y, a.y - min.y, ref t0, ref t1) &&
                   ClipEdge( d.y, max.y - a.y, ref t0, ref t1);
        }

        private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
        {
            if (p == 0f) return q >= 0f;
            float r = q / p;
            if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
            else        { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
    }
}
