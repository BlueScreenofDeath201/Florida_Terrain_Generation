using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeadEverywhere.FloridaGeneration
{
    /// <summary>
    /// Accumulates geometry for many roads into one mesh (32-bit indices) so a chunk
    /// produces a handful of renderers/colliders instead of thousands.
    /// </summary>
    public sealed class MeshBatch
    {
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<Vector2> uvs   = new List<Vector2>();
        public readonly List<int>     tris  = new List<int>();
        /// <summary>Number of roads (or road pieces) that contributed geometry.</summary>
        public int roadCount;

        public int TriangleCount => tris.Count / 3;

        public Mesh ToMesh(string name)
        {
            if (verts.Count == 0) return null;
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>The meshes produced for one chunk of roads.</summary>
    public sealed class RoadMeshSet
    {
        /// <summary>Road surfaces by <see cref="FloridaRoadStandards.Category"/> (asphalt).</summary>
        public readonly MeshBatch[] surface =
            { new MeshBatch(), new MeshBatch(), new MeshBatch(), new MeshBatch() };
        /// <summary>Bridge decks, barriers, retaining walls, piers and tunnel shells (concrete).</summary>
        public readonly MeshBatch structure = new MeshBatch();
        /// <summary>Embankment slopes and skirts beside the road (earth).</summary>
        public readonly MeshBatch embankment = new MeshBatch();
        /// <summary>Paint is visual only, never included in either physics world's colliders.</summary>
        public readonly MeshBatch whitePaint = new MeshBatch();
        public readonly MeshBatch yellowPaint = new MeshBatch();

        public int piers, barrierRuns, portals;

        public IEnumerable<MeshBatch> All()
        {
            foreach (var s in surface) yield return s;
            yield return structure;
            yield return embankment;
            yield return whitePaint;
            yield return yellowPaint;
        }
    }

    /// <summary>
    /// Builds closed, outward-facing road geometry from a <see cref="RoadPlan"/>.
    ///
    /// <b>Ground</b> roads get a surface plus side skirts, which become sloped embankments
    /// (down to the terrain beside the road) wherever the solved profile lifts the road above
    /// it, and crash barriers where the lift is large. Highways use retaining walls;
    /// earthworks that would intrude into another road's driving clearance are omitted.
    ///
    /// <b>Bridges</b> are solid decks: surface, barriers on both sides, a fascia and an
    /// underside at the structure depth below the surface, end walls, and piers wherever
    /// there is room under the deck.  Everything is in the standard <see cref="MeshCollider"/>,
    /// so a car can drive on the deck and under it.
    ///
    /// <b>Tunnels</b> are a closed tube around the road: floor, inner walls, ceiling, a roof
    /// slab and outer walls, with a portal frame at each real end.
    ///
    /// Winding is chosen per face from an explicit "which way should this face point" hint,
    /// so every face is outward-facing whatever the road direction.
    /// </summary>
    public static class FloridaRoadMeshBuilder
    {
        // Cross-section dimensions (metres).
        public const float BarrierHeight    = 0.9f;
        public const float BarrierThickness = 0.45f;
        private const float kLiftForBarrier = 1.5f;
        private const float kSlopeRatio     = 2f;     // embankment run per metre of rise
        private const float kMaxSlopeRun    = 30f;
        private const float kMaxMiter       = 2.0f;
        private const float kPierSpacing    = 30f;
        private const float kPierEndMargin  = 12f;
        private const float kPierMinGap     = 1.0f;
        private const float kTunnelWall     = 0.5f;
        private const float kTunnelRoof     = 0.6f;
        private const float kTunnelSide     = 0.4f;   // curb space beside the paved width

        private enum Uv { Surface, Wall, Plan }

        // ═════════════════════════════════════════════════════════════════════
        //  Entry point
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Appends the geometry of every road in <paramref name="plan"/> to <paramref name="set"/>.
        /// Vertex positions are relative to <paramref name="origin"/> (the transform the
        /// resulting objects will sit at).
        /// </summary>
        /// <param name="groundHeight">World Y of the bare terrain at world (x, z).</param>
        public static void Build(RoadPlan plan, RoadMeshSet set, Vector3 origin, Func<float, float, float> groundHeight)
        {
            var frames = new Frame[plan.roads.Count];
            for (int i = 0; i < frames.Length; i++) frames[i] = new Frame(plan.roads[i], origin);
            var clearance = new PavementClearance(frames);
            for (int i = 0; i < plan.roads.Count; i++)
            {
                var p = plan.roads[i];
                var f = frames[i];
                MeshBatch surface = set.surface[Mathf.Clamp(p.category, 0, set.surface.Length - 1)];
                bool added;
                switch (p.kind)
                {
                    case RoadKind.Bridge: added = BuildBridge(f, p, surface, set, groundHeight); break;
                    case RoadKind.Tunnel: added = BuildTunnel(f, p, surface, set, groundHeight); break;
                    default:              added = BuildGround(f, p, surface, set, groundHeight, clearance, i); break;
                }
                if (added)
                {
                    surface.roadCount++;
                    AddMarkings(f, p, set);
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Station frame
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Per-station right vector and miter scale for one piece.</summary>
        private sealed class Frame
        {
            public readonly int n;
            public readonly Vector2[] pos;
            public readonly Vector2[] right;
            public readonly Vector2[] fwd;
            public readonly float[] miter;
            public readonly float[] h;
            public readonly float[] dist;
            /// <summary>Paved half-width (m) at every station.</summary>
            public readonly float[] hw;
            private readonly Vector3 _origin;

            public Frame(PlannedRoad p, Vector3 origin)
            {
                n = p.pos.Length; pos = p.pos; h = p.height; dist = p.dist; _origin = origin;
                right = new Vector2[n]; fwd = new Vector2[n]; miter = new float[n];
                hw = new float[n];
                for (int i = 0; i < n; i++) hw[i] = p.HalfWidthAt(i);

                var segD = new Vector2[n - 1];
                var segN = new Vector2[n - 1];
                for (int i = 0; i < n - 1; i++)
                {
                    Vector2 d = pos[i + 1] - pos[i];
                    segD[i] = d.sqrMagnitude > 1e-10f ? d.normalized : (i > 0 ? segD[i - 1] : Vector2.right);
                    segN[i] = new Vector2(segD[i].y, -segD[i].x);
                }
                for (int i = 0; i < n; i++)
                {
                    float scale = 1f;
                    Vector2 nrm;
                    if (i == 0)          { nrm = segN[0];     fwd[i] = segD[0]; }
                    else if (i == n - 1) { nrm = segN[n - 2]; fwd[i] = segD[n - 2]; }
                    else
                    {
                        nrm = segN[i - 1] + segN[i];
                        if (nrm.sqrMagnitude < 1e-4f) nrm = segN[i];
                        nrm.Normalize();
                        float dot = Vector2.Dot(nrm, segN[i]);
                        scale = dot > 0.01f ? Mathf.Min(1f / dot, kMaxMiter) : kMaxMiter;
                        Vector2 fw = segD[i - 1] + segD[i];
                        fwd[i] = fw.sqrMagnitude > 1e-6f ? fw.normalized : segD[i];
                    }
                    right[i] = nrm; miter[i] = scale;
                }
                if (p.startRight.sqrMagnitude > 0f)
                {
                    right[0] = p.startRight.normalized;
                    miter[0] = p.startRight.magnitude;
                }
                if (p.endRight.sqrMagnitude > 0f)
                {
                    right[n - 1] = p.endRight.normalized;
                    miter[n - 1] = p.endRight.magnitude;
                }
            }

            /// <summary>World XZ at lateral offset <paramref name="w"/> (positive = right) of station i.</summary>
            public Vector2 Xz(int i, float w) => pos[i] + right[i] * (w * miter[i]);

            /// <summary>Mesh-space vertex at lateral offset w and world height y.</summary>
            public Vector3 At(int i, float w, float y)
            {
                Vector2 q = Xz(i, w);
                return new Vector3(q.x - _origin.x, y - _origin.y, q.y - _origin.z);
            }

            /// <summary>Mesh-space vertex at world XZ <paramref name="xz"/> and world height y.</summary>
            public Vector3 World(Vector2 xz, float y) => new Vector3(xz.x - _origin.x, y - _origin.y, xz.y - _origin.z);

            public Vector3 Right3(int i) => new Vector3(right[i].x, 0f, right[i].y);
            public Vector3 Fwd3(int i)   => new Vector3(fwd[i].x, 0f, fwd[i].y);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Ground
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Actual mitered pavement triangles extruded through the vehicle clearance envelope.</summary>
        private sealed class PavementClearance
        {
            private const float CellSize = 48f;
            private sealed class Prism
            {
                public int owner, stamp;
                public Bounds bounds;
                public Plane[] planes;
            }

            private readonly Dictionary<Vector2Int, List<Prism>> cells = new Dictionary<Vector2Int, List<Prism>>();
            private List<Vector3> polygon = new List<Vector3>(12), clipped = new List<Vector3>(12);
            private int query;

            public PavementClearance(Frame[] frames)
            {
                for (int owner = 0; owner < frames.Length; owner++)
                {
                    var f = frames[owner];
                    for (int i = 0; i < f.n - 1; i++)
                    {
                        Vector3 a = f.At(i, -f.hw[i] - 0.3f, f.h[i]);
                        Vector3 b = f.At(i, f.hw[i] + 0.3f, f.h[i]);
                        Vector3 c = f.At(i + 1, -f.hw[i + 1] - 0.3f, f.h[i + 1]);
                        Vector3 d = f.At(i + 1, f.hw[i + 1] + 0.3f, f.h[i + 1]);
                        Add(owner, a, b, c);
                        Add(owner, b, d, c);
                    }
                }
            }

            private void Add(int owner, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                if (Mathf.Abs(normal.y) < 0.0001f) return;
                if (normal.y < 0f) normal = -normal;
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b); bounds.Encapsulate(c);
                bounds.SetMinMax(bounds.min - Vector3.up * 0.05f,
                                 bounds.max + Vector3.up * FloridaRoadStandards.UnderRoadClearance);
                var prism = new Prism
                {
                    owner = owner, bounds = bounds,
                    planes = new[]
                    {
                        Edge(a, b, c), Edge(b, c, a), Edge(c, a, b),
                        new Plane(normal, a - Vector3.up * 0.05f),
                        new Plane(-normal, a + Vector3.up * FloridaRoadStandards.UnderRoadClearance)
                    }
                };
                for (int x = Cell(bounds.min.x); x <= Cell(bounds.max.x); x++)
                    for (int z = Cell(bounds.min.z); z <= Cell(bounds.max.z); z++)
                    {
                        var key = new Vector2Int(x, z);
                        if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Prism>();
                        list.Add(prism);
                    }
            }

            private static int Cell(float value) => Mathf.FloorToInt(value / CellSize);

            private static Plane Edge(Vector3 a, Vector3 b, Vector3 inside)
            {
                var plane = new Plane(new Vector3(b.z - a.z, 0f, a.x - b.x), a);
                return plane.GetDistanceToPoint(inside) >= 0f ? plane : plane.flipped;
            }

            public bool Intersects(int owner, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                query++;
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b); bounds.Encapsulate(c); bounds.Encapsulate(d);
                for (int x = Cell(bounds.min.x); x <= Cell(bounds.max.x); x++)
                    for (int z = Cell(bounds.min.z); z <= Cell(bounds.max.z); z++)
                    {
                        if (!cells.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                        foreach (var prism in list)
                        {
                            if (prism.owner == owner || prism.stamp == query) continue;
                            prism.stamp = query;
                            if (!prism.bounds.Intersects(bounds)) continue;
                            if (Intersects(prism, a, b, c) || Intersects(prism, a, c, d)) return true;
                        }
                    }
                return false;
            }

            private bool Intersects(Prism prism, Vector3 a, Vector3 b, Vector3 c)
            {
                polygon.Clear();
                polygon.Add(a); polygon.Add(b); polygon.Add(c);
                foreach (var plane in prism.planes)
                {
                    clipped.Clear();
                    Vector3 previous = polygon[polygon.Count - 1];
                    float previousDistance = plane.GetDistanceToPoint(previous);
                    foreach (var point in polygon)
                    {
                        float distance = plane.GetDistanceToPoint(point);
                        if ((previousDistance >= 0f) != (distance >= 0f))
                            clipped.Add(Vector3.Lerp(previous, point, previousDistance / (previousDistance - distance)));
                        if (distance >= 0f) clipped.Add(point);
                        previous = point; previousDistance = distance;
                    }
                    var swap = polygon; polygon = clipped; clipped = swap;
                    if (polygon.Count < 3) return false;
                }
                return true;
            }
        }

        private static bool BuildGround(Frame f, PlannedRoad p, MeshBatch surface, RoadMeshSet set,
                                        Func<float, float, float> terrain, PavementClearance clearance, int owner)
        {
            int n = f.n;
            AddSurface(f, surface);
            bool highway = FloridaRoadStandards.IsMotorwayClass(p.highway);

            for (int side = -1; side <= 1; side += 2)
            {
                // Barrier flag per station: the road is lifted well above the terrain on this side.
                var barrier = new bool[n];
                var edgeW   = new float[n];
                var topA    = new Vector3[n]; var botA = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    float tEdge = EdgeTerrain(f, terrain, i, side * (f.hw[i] + BarrierThickness));
                    barrier[i] = f.h[i] - tEdge >= kLiftForBarrier && !p.BarrierOpen(i, side);
                }
                for (int i = 0; i < n; i++)
                {
                    edgeW[i] = f.hw[i] + (barrier[i] ? BarrierThickness : 0f);
                    float w  = side * edgeW[i];
                    float tEdge = EdgeTerrain(f, terrain, i, w);
                    float lift  = f.h[i] - tEdge;

                    float slope = highway ? 0f : kSlopeRatio * Mathf.Clamp01((lift - 0.4f) / 0.8f);
                    float bottom = Mathf.Min(f.h[i] - 0.5f, tEdge - 0.25f);
                    float toe = w;
                    if (slope > 0f)
                    {
                        // Walk the toe outwards until it meets the terrain beside the road, but
                        // stop short of a lower road beside it.
                        float maxRun = Mathf.Min(kMaxSlopeRun, p.SkirtRun(i, side));
                        for (int it = 0; it < 2; it++)
                        {
                            float run = Mathf.Min(maxRun, slope * (f.h[i] - bottom));
                            toe = w + side * run;
                            float tToe = terrain(f.Xz(i, toe).x, f.Xz(i, toe).y);
                            bottom = Mathf.Min(f.h[i] - 0.5f, Mathf.Min(tEdge, tToe) - 0.25f);
                        }
                        toe = w + side * Mathf.Min(maxRun, slope * (f.h[i] - bottom));
                    }
                    topA[i] = f.At(i, w, f.h[i]);
                    botA[i] = f.At(i, toe, bottom);
                }

                for (int i = 0; i < n - 1; i++)
                {
                    Vector3 a = topA[i], b = topA[i + 1], c = botA[i + 1], d = botA[i];
                    bool wall = highway;
                    if (clearance.Intersects(owner, a, b, c, d))
                    {
                        // Retain the approach instead of spilling fill into a crossing or ramp.
                        c = new Vector3(b.x, c.y, b.z);
                        d = new Vector3(a.x, d.y, a.z);
                        wall = true;
                        if (clearance.Intersects(owner, a, b, c, d)) continue;
                    }
                    Vector3 r = (f.Right3(i) + f.Right3(i + 1)) * side;
                    Quad(wall ? set.structure : set.embankment, a, b, c, d,
                         new Vector2(f.dist[i] * 0.25f, 0f), new Vector2(f.dist[i + 1] * 0.25f, 0f),
                         new Vector2(f.dist[i + 1] * 0.25f, Vector3.Distance(b, c) * 0.25f),
                         new Vector2(f.dist[i] * 0.25f, Vector3.Distance(a, d) * 0.25f),
                         new Vector3(r.x, 0.5f, r.z));
                }

                // Crash barriers on the stretches where the road stands proud of the terrain.
                int i0 = 0;
                while (i0 < n)
                {
                    if (!barrier[i0]) { i0++; continue; }
                    int i1 = i0;
                    while (i1 + 1 < n && barrier[i1 + 1]) i1++;
                    if (i1 > i0)
                    {
                        AddBarrier(f, set.structure, side, i0, i1, i => f.h[i]);
                        set.barrierRuns++;
                    }
                    i0 = i1 + 1;
                }
            }
            if (highway) set.structure.roadCount++;
            else set.embankment.roadCount++;
            return true;
        }

        private static float EdgeTerrain(Frame f, Func<float, float, float> terrain, int i, float w)
        {
            Vector2 q = f.Xz(i, w);
            return terrain(q.x, q.y);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Bridge
        // ═════════════════════════════════════════════════════════════════════

        private static bool BuildBridge(Frame f, PlannedRoad p, MeshBatch surface, RoadMeshSet set,
                                        Func<float, float, float> terrain)
        {
            int n = f.n;
            float depth = p.deckDepth;

            AddSurface(f, surface);

            // Barriers on both sides, their outer face running down to the soffit as the fascia.
            for (int side = -1; side <= 1; side += 2)
            {
                // Left open where another road's pavement runs through the barrier line.
                int i0 = 0;
                while (i0 < n)
                {
                    if (p.BarrierOpen(i0, side)) { i0++; continue; }
                    int i1 = i0;
                    while (i1 + 1 < n && !p.BarrierOpen(i1 + 1, side)) i1++;
                    if (i1 > i0)
                    {
                        AddBarrier(f, set.structure, side, i0, i1, i => f.h[i] - depth);
                        set.barrierRuns++;
                    }
                    i0 = i1 + 1;
                }
                // Opening a crash barrier must not open the side of the structural deck below it.
                for (int i = 0; i < n - 1; i++)
                {
                    if (!p.BarrierOpen(i, side) && !p.BarrierOpen(i + 1, side)) continue;
                    float w0 = side * (f.hw[i] + BarrierThickness);
                    float w1 = side * (f.hw[i + 1] + BarrierThickness);
                    Vector3 a = f.At(i, w0, f.h[i]), b = f.At(i + 1, w1, f.h[i + 1]);
                    Vector3 c = b - Vector3.up * depth, d = a - Vector3.up * depth;
                    Quad(set.structure, a, b, c, d, new Vector2(0f, 0f), new Vector2(1f, 0f),
                         new Vector2(1f, 1f), new Vector2(0f, 1f), f.Right3(i) * side);
                    Vector3 inner0 = f.At(i, side * f.hw[i], f.h[i]);
                    Vector3 inner1 = f.At(i + 1, side * f.hw[i + 1], f.h[i + 1]);
                    float tile = FloridaRoadStandards.AsphaltTileSize;
                    Quad(surface, inner0, a, b, inner1,
                         f.Xz(i, side * f.hw[i]) / tile, f.Xz(i, w0) / tile,
                         f.Xz(i + 1, w1) / tile, f.Xz(i + 1, side * f.hw[i + 1]) / tile, Vector3.up);
                }
            }

            // Underside.
            var ua = new Vector3[n]; var ub = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float ow = f.hw[i] + BarrierThickness;
                ua[i] = f.At(i, -ow, f.h[i] - depth);
                ub[i] = f.At(i,  ow, f.h[i] - depth);
            }
            Strip(set.structure, ua, ub, Uv.Plan, f.dist, 0.25f, Vector3.down);

            // End walls close the deck so it is a solid slab, not an open box.
            EndWall(f, set.structure, 0, -1, f.hw[0] + BarrierThickness, f.h[0] - depth, f.h[0]);
            EndWall(f, set.structure, n - 1, +1, f.hw[n - 1] + BarrierThickness, f.h[n - 1] - depth, f.h[n - 1]);

            AddPiers(f, p, set, terrain);
            set.structure.roadCount++;
            return true;
        }

        private static void AddPiers(Frame f, PlannedRoad p, RoadMeshSet set, Func<float, float, float> terrain)
        {
            float length = p.Length;
            int spans = Mathf.RoundToInt(length / kPierSpacing);
            if (spans < 2) return;

            for (int k = 1; k < spans; k++)
            {
                float s = length * k / spans;
                float hw = p.HalfWidthAtDist(s);
                bool twin = hw > 5f;
                if (s < kPierEndMargin || s > length - kPierEndMargin) continue;
                bool blocked = false;
                foreach (var z in p.crossZones)
                    if (s >= z.x - 1.5f && s <= z.y + 1.5f) { blocked = true; break; }
                if (blocked) continue;

                Vector2 c = p.PointAt(s);
                float deck = p.HeightAt(s);
                float soffit = deck - p.deckDepth;
                int seg = SegmentIndex(f, s);
                Vector2 d = f.fwd[seg];
                Vector2 r = new Vector2(d.y, -d.x);

                for (int col = 0; col < (twin ? 2 : 1); col++)
                {
                    float lateral = twin ? (col == 0 ? -0.5f : 0.5f) * hw : 0f;
                    Vector2 q = c + r * lateral;
                    float ground = terrain(q.x, q.y);
                    if (soffit - ground < kPierMinGap) continue;
                    AddColumn(f, set.structure, q, d, r, ground - 1.0f, soffit + 0.05f,
                              0.8f, twin ? 0.7f : Mathf.Min(1.2f, hw * 0.3f));
                    set.piers++;
                }
            }
        }

        private static int SegmentIndex(Frame f, float s)
        {
            int lo = 0, hi = f.n - 1;
            if (s <= f.dist[0]) return 0;
            if (s >= f.dist[hi]) return hi - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (f.dist[mid] <= s) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>Rectangular column: halfAlong along the road, halfAcross across it.</summary>
        private static void AddColumn(Frame f, MeshBatch m, Vector2 c, Vector2 d, Vector2 r,
                                      float yBottom, float yTop, float halfAlong, float halfAcross)
        {
            Vector2[] corner =
            {
                c - d * halfAlong - r * halfAcross,
                c + d * halfAlong - r * halfAcross,
                c + d * halfAlong + r * halfAcross,
                c - d * halfAlong + r * halfAcross
            };
            for (int k = 0; k < 4; k++)
            {
                Vector2 a = corner[k], b = corner[(k + 1) & 3];
                Vector2 mid = (a + b) * 0.5f - c;
                Vector3 hint = new Vector3(mid.x, 0f, mid.y);
                Vector3 p0 = f.World(a, yBottom), p1 = f.World(b, yBottom);
                Vector3 p2 = f.World(b, yTop),    p3 = f.World(a, yTop);
                Quad(m, p0, p1, p2, p3,
                     new Vector2(0f, yBottom * 0.25f), new Vector2(1f, yBottom * 0.25f),
                     new Vector2(1f, yTop * 0.25f),    new Vector2(0f, yTop * 0.25f), hint);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Tunnel
        // ═════════════════════════════════════════════════════════════════════

        private static bool BuildTunnel(Frame f, PlannedRoad p, MeshBatch surface, RoadMeshSet set,
                                        Func<float, float, float> terrain)
        {
            int n = f.n;
            var iw = new float[n];                        // interior half-width
            var ow = new float[n];                        // exterior half-width
            for (int i = 0; i < n; i++) { iw[i] = f.hw[i] + kTunnelSide; ow[i] = iw[i] + kTunnelWall; }
            float ih = p.tunnelHeight;                    // interior height above the road
            var m = set.structure;

            AddSurface(f, surface);

            // Floor strips beside the paved width, inside the tube.
            for (int side = -1; side <= 1; side += 2)
            {
                var a = new Vector3[n]; var b = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    a[i] = f.At(i, side * f.hw[i], f.h[i]);
                    b[i] = f.At(i, side * iw[i], f.h[i]);
                }
                Strip(m, a, b, Uv.Plan, f.dist, 0.25f, Vector3.up);
            }

            var bottom = new float[n];
            for (int i = 0; i < n; i++)
            {
                float tl = EdgeTerrain(f, terrain, i, -ow[i]), tr = EdgeTerrain(f, terrain, i, ow[i]);
                bottom[i] = Mathf.Min(f.h[i] - 0.5f, Mathf.Min(tl, tr) - 0.3f);
            }

            var ceilL = new Vector3[n]; var ceilR = new Vector3[n];
            var roofL = new Vector3[n]; var roofR = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                ceilL[i] = f.At(i, -iw[i], f.h[i] + ih);
                ceilR[i] = f.At(i,  iw[i], f.h[i] + ih);
                roofL[i] = f.At(i, -ow[i], f.h[i] + ih + kTunnelRoof);
                roofR[i] = f.At(i,  ow[i], f.h[i] + ih + kTunnelRoof);
            }
            Strip(m, ceilL, ceilR, Uv.Plan, f.dist, 0.25f, Vector3.down);
            Strip(m, roofL, roofR, Uv.Plan, f.dist, 0.25f, Vector3.up);

            for (int side = -1; side <= 1; side += 2)
            {
                var innerLow = new Vector3[n]; var innerHigh = new Vector3[n];
                var outerHigh = new Vector3[n]; var outerLow = new Vector3[n];
                var inward = new Vector3[n]; var outward = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    innerLow[i]  = f.At(i, side * iw[i], f.h[i]);
                    innerHigh[i] = f.At(i, side * iw[i], f.h[i] + ih);
                    outerHigh[i] = f.At(i, side * ow[i], f.h[i] + ih + kTunnelRoof);
                    outerLow[i]  = f.At(i, side * ow[i], bottom[i]);
                    outward[i]   = f.Right3(i) * side;
                    inward[i]    = -outward[i];
                }
                Strip(m, innerLow, innerHigh, Uv.Wall, f.dist, 0.25f, inward);
                Strip(m, outerHigh, outerLow, Uv.Wall, f.dist, 0.25f, outward);
            }

            // Portal frames: the solid wall and roof section seen from outside at a real tunnel mouth.
            if (p.capStart) { PortalFrame(f, m, 0, -1, iw[0], ow[0], ih, bottom[0]); set.portals++; }
            if (p.capEnd)   { PortalFrame(f, m, n - 1, +1, iw[n - 1], ow[n - 1], ih, bottom[n - 1]); set.portals++; }
            m.roadCount++;
            return true;
        }

        private static void PortalFrame(Frame f, MeshBatch m, int i, int dir, float iw, float ow, float ih, float bottom)
        {
            float y = f.h[i];
            Vector3 hint = f.Fwd3(i) * dir;
            float roofTop = y + ih + kTunnelRoof;
            // Roof slab over the opening.
            Quad(m, f.At(i, -ow, y + ih), f.At(i, ow, y + ih), f.At(i, ow, roofTop), f.At(i, -ow, roofTop),
                 new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), hint);
            // Wall faces either side of the opening.
            for (int side = -1; side <= 1; side += 2)
                Quad(m, f.At(i, side * iw, bottom), f.At(i, side * ow, bottom),
                        f.At(i, side * ow, roofTop), f.At(i, side * iw, roofTop),
                     new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), hint);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Shared pieces
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>World-projected asphalt keeps aggregate at the same scale on every road and chunk.</summary>
        private static void AddSurface(Frame f, MeshBatch m)
        {
            int n = f.n;
            var a = new Vector3[n]; var b = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = f.At(i, -f.hw[i], f.h[i]);
                b[i] = f.At(i,  f.hw[i], f.h[i]);
            }
            int first = m.verts.Count;
            Strip(m, a, b, Uv.Surface, f.dist, 1f, Vector3.up);
            for (int i = 0; i < n; i++)
            {
                m.uvs[first + i * 2] = f.Xz(i, -f.hw[i]) / FloridaRoadStandards.AsphaltTileSize;
                m.uvs[first + i * 2 + 1] = f.Xz(i, f.hw[i]) / FloridaRoadStandards.AsphaltTileSize;
            }
        }

        private static void AddMarkings(Frame f, PlannedRoad p, RoadMeshSet set)
        {
            if (p.highway == "service" || p.highway == "living_street" ||
                p.surface == "unpaved" || p.surface == "gravel" || p.surface == "fine_gravel" ||
                p.surface == "dirt" || p.surface == "ground" || p.surface == "sand" || p.surface == "grass")
                return;

            int lanes = Mathf.Max(1, p.lanes);
            bool oneWay = p.oneway != 0;
            bool singleLaneRamp = oneWay && lanes == 1 && FloridaRoadStandards.IsRamp(p.highway);
            var leftEdge = new float[f.n];
            var rightEdge = new float[f.n];
            for (int i = 0; i < f.n; i++)
            {
                FloridaRoadStandards.LaneEdges(p, f.hw[i], out leftEdge[i], out rightEdge[i]);
            }

            int whiteBefore = set.whitePaint.verts.Count, yellowBefore = set.yellowPaint.verts.Count;
            float centre = (FloridaRoadStandards.PaintWidth + FloridaRoadStandards.CentreLineGap) * 0.5f;
            if (!oneWay && lanes >= 2)
            {
                PaintLine(f, p, set.yellowPaint, i => -centre, false);
                PaintLine(f, p, set.yellowPaint, i => centre, false);
            }

            for (int side = -1; side <= 1; side += 2)
            {
                int s = side;
                bool yellowEdge = oneWay && side == (p.oneway < 0 ? 1 : -1);
                PaintLine(f, p, yellowEdge ? set.yellowPaint : set.whitePaint,
                          i => singleLaneRamp ? s * (f.hw[i] - FloridaRoadStandards.EdgeLineInset)
                                              : s < 0 ? leftEdge[i] : rightEdge[i], false,
                          FloridaRoadStandards.IsMotorwayClass(p.highway)
                              ? FloridaRoadStandards.HighwayEdgePaintWidth : FloridaRoadStandards.PaintWidth,
                          p.mergeEdges[side < 0 ? 0 : 1]);
            }

            // Odd two-way lane totals do not tell us whether the middle lane is a turn lane.
            if (oneWay || lanes % 2 == 0)
                for (int divider = 1; divider < lanes; divider++)
                {
                    if (!oneWay && divider == lanes / 2) continue;
                    float fraction = (float)divider / lanes;
                    PaintLine(f, p, set.whitePaint, i => Mathf.Lerp(leftEdge[i], rightEdge[i], fraction), true);
                }
            if (set.whitePaint.verts.Count > whiteBefore) set.whitePaint.roadCount++;
            if (set.yellowPaint.verts.Count > yellowBefore) set.yellowPaint.roadCount++;
        }

        private static void PaintLine(Frame f, PlannedRoad p, MeshBatch paint, Func<int, float> lateral, bool dashed,
                                      float width = FloridaRoadStandards.PaintWidth, List<Vector2> merges = null)
        {
            var ribbon = new Vector2[4];
            var clipped = new Vector2[6];
            for (int i = 0; i < f.n - 1; i++)
            {
                double begin = f.dist[i], end = f.dist[i + 1];
                if (end - begin < 0.001) continue;
                FloridaRoadStandards.LaneEdges(p, f.hw[i], out float left0, out float right0);
                FloridaRoadStandards.LaneEdges(p, f.hw[i + 1], out float left1, out float right1);
                if (Mathf.Min(right0 - left0, right1 - left1) < Mathf.Max(1, p.lanes) * 2.5f) continue;
                double cursor = begin;
                if (merges != null)
                    foreach (var zone in merges)
                    {
                        if (zone.y <= cursor) continue;
                        if (zone.x >= end) break;
                        EmitPattern(cursor, Math.Min(end, zone.x), dashed, false);
                        double stop = Math.Min(end, zone.y);
                        EmitPattern(Math.Max(cursor, zone.x), stop, true, true);
                        cursor = stop;
                        if (cursor >= end) break;
                    }
                EmitPattern(cursor, end, dashed, false);

                void EmitPattern(double start, double stop, bool broken, bool merge)
                {
                    if (stop <= start) return;
                    if (!broken)
                    {
                        PaintInterval(f, p, paint, i, start, stop, lateral, width, ribbon, clipped);
                        return;
                    }
                    double length = merge ? FloridaRoadStandards.MergeDashLength : FloridaRoadStandards.DashLength;
                    double cycle = length + (merge ? FloridaRoadStandards.MergeDashGap : FloridaRoadStandards.DashGap);
                    // Finite interval enumeration avoids stalled float increments on long ways.
                    double phase = ((double)p.sourceDistance + start) % cycle;
                    double first = start - phase;
                    int count = (int)Math.Ceiling((stop - first) / cycle);
                    for (int dash = 0; dash < count; dash++)
                    {
                        double dashStart = first + dash * cycle;
                        PaintInterval(f, p, paint, i, Math.Max(start, dashStart),
                                      Math.Min(stop, dashStart + length), lateral, width, ribbon, clipped);
                    }
                }
            }
        }

        private static void PaintInterval(Frame f, PlannedRoad p, MeshBatch paint, int i, double begin, double end,
                                          Func<int, float> lateral, float width, Vector2[] ribbon, Vector2[] clipped)
        {
            foreach (var gap in p.markingGaps)
            {
                if (gap.y <= begin) continue;
                if (gap.x >= end) break;
                if (gap.x > begin)
                    PaintRibbon(f, paint, i, begin, Math.Min(end, gap.x), lateral, width, p.sourceDistance, ribbon, clipped);
                begin = Math.Max(begin, gap.y);
                if (begin >= end) return;
            }
            if (end > begin) PaintRibbon(f, paint, i, begin, end, lateral, width, p.sourceDistance, ribbon, clipped);
        }

        private static void PaintRibbon(Frame f, MeshBatch paint, int i, double begin, double end,
                                        Func<int, float> lateral, float width, float sourceDistance,
                                        Vector2[] ribbon, Vector2[] clipped)
        {
            float t0 = (float)((begin - f.dist[i]) / (f.dist[i + 1] - f.dist[i]));
            float t1 = (float)((end - f.dist[i]) / (f.dist[i + 1] - f.dist[i]));
            if (t1 - t0 < 0.000001f) return;
            float half = width * 0.5f;
            float U(float t, float edge) =>
                Mathf.Lerp((lateral(i) + edge) / f.hw[i], (lateral(i + 1) + edge) / f.hw[i + 1], t) * 0.5f + 0.5f;
            ribbon[0] = new Vector2(U(t0, -half), t0);
            ribbon[1] = new Vector2(U(t0, half), t0);
            ribbon[2] = new Vector2(U(t1, half), t1);
            ribbon[3] = new Vector2(U(t1, -half), t1);

            // Split at the asphalt diagonal in (lateral, longitudinal) parameter space.
            // Every emitted triangle then lies on exactly one underlying pavement triangle.
            for (int side = -1; side <= 1; side += 2)
            {
                int count = 0;
                Vector2 previous = ribbon[3];
                float prevDist = side * (previous.x + previous.y - 1f);
                foreach (var current in ribbon)
                {
                    float distance = side * (current.x + current.y - 1f);
                    if ((distance <= 0f) != (prevDist <= 0f))
                        clipped[count++] = Vector2.Lerp(previous, current, prevDist / (prevDist - distance));
                    if (distance <= 0f) clipped[count++] = current;
                    previous = current;
                    prevDist = distance;
                }
                if (count < 3) continue;
                int first = paint.verts.Count;
                for (int v = 0; v < count; v++)
                {
                    Vector3 point = PaintPoint(f, i, clipped[v]);
                    paint.verts.Add(point);
                    float t = clipped[v].y;
                    float across = Mathf.InverseLerp(U(t, -half), U(t, half), clipped[v].x);
                    float along = sourceDistance + Mathf.Lerp(f.dist[i], f.dist[i + 1], t);
                    paint.uvs.Add(new Vector2(across, along * 0.5f));
                }
                for (int v = 1; v < count - 1; v++)
                {
                    float up = Vector3.Cross(paint.verts[first + v] - paint.verts[first],
                                             paint.verts[first + v + 1] - paint.verts[first]).y;
                    if (Mathf.Abs(up) < 1e-10f) continue;
                    paint.tris.Add(first);
                    paint.tris.Add(first + (up > 0f ? v : v + 1));
                    paint.tris.Add(first + (up > 0f ? v + 1 : v));
                }
            }
        }

        private static Vector3 PaintPoint(Frame f, int i, Vector2 parameter)
        {
            float u = parameter.x, t = parameter.y;
            Vector3 a = f.At(i, -f.hw[i], f.h[i]), b = f.At(i, f.hw[i], f.h[i]);
            Vector3 c = f.At(i + 1, -f.hw[i + 1], f.h[i + 1]);
            Vector3 d = f.At(i + 1, f.hw[i + 1], f.h[i + 1]);
            // Match the surface strip's diagonal instead of floating above a smoothed centre-line.
            Vector3 point = u + t <= 1f ? a * (1f - u - t) + b * u + c * t
                                       : b * (1f - t) + c * (1f - u) + d * (u + t - 1f);
            return point + Vector3.up * 0.015f;
        }

        /// <summary>
        /// Crash barrier along stations i0..i1 on one side: inner face, top and outer face
        /// (down to <paramref name="footY"/>), with end caps.
        /// </summary>
        private static void AddBarrier(Frame f, MeshBatch m, int side, int i0, int i1, Func<int, float> footY)
        {
            int cnt = i1 - i0 + 1;
            var innerFoot = new Vector3[cnt]; var innerTop = new Vector3[cnt];
            var outerTop  = new Vector3[cnt]; var outerFoot = new Vector3[cnt];
            var inward = new Vector3[cnt]; var outward = new Vector3[cnt];
            var dist = new float[cnt];
            for (int k = 0; k < cnt; k++)
            {
                int i = i0 + k;
                float hw = f.hw[i], ow = hw + BarrierThickness;
                float y = f.h[i];
                innerFoot[k] = f.At(i, side * hw, y);
                innerTop[k]  = f.At(i, side * hw, y + BarrierHeight);
                outerTop[k]  = f.At(i, side * ow, y + BarrierHeight);
                outerFoot[k] = f.At(i, side * ow, footY(i));
                outward[k]   = f.Right3(i) * side;
                inward[k]    = -outward[k];
                dist[k]      = f.dist[i];
            }
            Strip(m, innerFoot, innerTop, Uv.Wall, dist, 0.25f, inward);
            Strip(m, innerTop, outerTop, Uv.Plan, dist, 0.25f, Vector3.up);
            Strip(m, outerTop, outerFoot, Uv.Wall, dist, 0.25f, outward);

            // End caps.
            for (int e = 0; e < 2; e++)
            {
                int k = e == 0 ? 0 : cnt - 1;
                int i = i0 + k;
                Vector3 hint = f.Fwd3(i) * (e == 0 ? -1f : 1f);
                Quad(m, innerFoot[k], innerTop[k], outerTop[k], outerFoot[k],
                     new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), hint);
            }
        }

        /// <summary>Full-width wall across the deck at one end, from the soffit up to the road surface.</summary>
        private static void EndWall(Frame f, MeshBatch m, int i, int dir, float ow, float yBottom, float yTop)
        {
            Vector3 hint = f.Fwd3(i) * dir;
            Quad(m, f.At(i, -ow, yBottom), f.At(i, ow, yBottom), f.At(i, ow, yTop), f.At(i, -ow, yTop),
                 new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), hint);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Primitive emitters
        // ═════════════════════════════════════════════════════════════════════

        private static void Strip(MeshBatch m, Vector3[] a, Vector3[] b, Uv mode, float[] dist, float vScale,
                                  Vector3 hint)
        {
            var h = new Vector3[a.Length];
            for (int i = 0; i < h.Length; i++) h[i] = hint;
            Strip(m, a, b, mode, dist, vScale, h);
        }

        /// <summary>
        /// Emits a ribbon between edge lines <paramref name="a"/> and <paramref name="b"/>
        /// (one point per station, <paramref name="dist"/> gives the distance along the road).
        /// Each segment is wound so its face normal points the same way as <paramref name="hint"/>.
        /// </summary>
        private static void Strip(MeshBatch m, Vector3[] a, Vector3[] b, Uv mode, float[] dist, float vScale,
                                  Vector3[] hint)
        {
            int count = a.Length;
            if (count < 2) return;
            int baseIdx = m.verts.Count;

            for (int i = 0; i < count; i++)
            {
                Vector3 pa = a[i], pb = b[i];
                float v = dist[i] * vScale;
                Vector2 uvA, uvB;
                switch (mode)
                {
                    case Uv.Wall:
                        uvA = new Vector2(pa.y * 0.25f, v); uvB = new Vector2(pb.y * 0.25f, v); break;
                    case Uv.Plan:
                        uvA = new Vector2(0f, v); uvB = new Vector2((pb - pa).magnitude * 0.25f, v); break;
                    default:
                        uvA = new Vector2(0f, v); uvB = new Vector2(1f, v); break;
                }
                m.verts.Add(pa); m.verts.Add(pb);
                m.uvs.Add(uvA);  m.uvs.Add(uvB);
            }

            for (int i = 0; i < count - 1; i++)
            {
                int ia = baseIdx + i * 2, ib = ia + 1, ja = ia + 2, jb = ia + 3;
                Vector3 pa = a[i], pb = b[i], qa = a[i + 1], qb = b[i + 1];
                Vector3 n = Vector3.Cross(qa - pa, pb - pa) + Vector3.Cross(qb - qa, pb - qa);
                if (n.sqrMagnitude < 1e-12f) continue;
                if (Vector3.Dot(n, hint[i] + hint[i + 1]) >= 0f)
                {
                    m.tris.Add(ia); m.tris.Add(ja); m.tris.Add(ib);
                    m.tris.Add(ja); m.tris.Add(jb); m.tris.Add(ib);
                }
                else
                {
                    m.tris.Add(ia); m.tris.Add(ib); m.tris.Add(ja);
                    m.tris.Add(ja); m.tris.Add(ib); m.tris.Add(jb);
                }
            }
        }

        /// <summary>Single quad p0→p1→p2→p3 (in order around the quad), wound to face <paramref name="hint"/>.</summary>
        private static void Quad(MeshBatch m, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                 Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3, Vector3 hint)
        {
            Vector3 n = Vector3.Cross(p2 - p0, p3 - p1);
            if (n.sqrMagnitude < 1e-12f) return;
            int b = m.verts.Count;
            m.verts.Add(p0); m.verts.Add(p1); m.verts.Add(p2); m.verts.Add(p3);
            m.uvs.Add(u0); m.uvs.Add(u1); m.uvs.Add(u2); m.uvs.Add(u3);
            if (Vector3.Dot(n, hint) >= 0f)
            {
                m.tris.Add(b); m.tris.Add(b + 1); m.tris.Add(b + 2);
                m.tris.Add(b); m.tris.Add(b + 2); m.tris.Add(b + 3);
            }
            else
            {
                m.tris.Add(b); m.tris.Add(b + 2); m.tris.Add(b + 1);
                m.tris.Add(b); m.tris.Add(b + 3); m.tris.Add(b + 2);
            }
        }
    }
}
