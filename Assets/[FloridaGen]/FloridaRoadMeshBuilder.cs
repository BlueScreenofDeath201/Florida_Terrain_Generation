using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeadEverywhere.FloridaGen
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
        /// <summary>Bridge decks, barriers, piers and tunnel shells (concrete).</summary>
        public readonly MeshBatch structure = new MeshBatch();
        /// <summary>Embankment slopes and skirts beside the road (earth).</summary>
        public readonly MeshBatch embankment = new MeshBatch();

        public int piers, barrierRuns, portals;

        public IEnumerable<MeshBatch> All()
        {
            foreach (var s in surface) yield return s;
            yield return structure;
            yield return embankment;
        }
    }

    /// <summary>
    /// Builds closed, outward-facing road geometry from a <see cref="RoadPlan"/>.
    ///
    /// <b>Ground</b> roads get a surface plus side skirts, which become sloped embankments
    /// (down to the terrain beside the road) wherever the solved profile lifts the road above
    /// it, and crash barriers where the lift is large.
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
            foreach (var p in plan.roads)
            {
                var f = new Frame(p, origin);
                MeshBatch surface = set.surface[Mathf.Clamp(p.category, 0, set.surface.Length - 1)];
                bool added;
                switch (p.kind)
                {
                    case RoadKind.Bridge: added = BuildBridge(f, p, surface, set, groundHeight); break;
                    case RoadKind.Tunnel: added = BuildTunnel(f, p, surface, set, groundHeight); break;
                    default:              added = BuildGround(f, p, surface, set, groundHeight); break;
                }
                if (added) surface.roadCount++;
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

        private static bool BuildGround(Frame f, PlannedRoad p, MeshBatch surface, RoadMeshSet set,
                                        Func<float, float, float> terrain)
        {
            int n = f.n;
            AddSurface(f, surface, p.halfWidth);

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

                    float slope = kSlopeRatio * Mathf.Clamp01((lift - 0.4f) / 0.8f);
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

                var hints = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    Vector3 r = f.Right3(i) * side;
                    hints[i] = new Vector3(r.x, 0.5f, r.z);
                }
                Strip(set.embankment, topA, botA, Uv.Wall, f.dist, 0.25f, hints);

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
            set.embankment.roadCount++;
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

            AddSurface(f, surface, p.halfWidth);

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

            AddSurface(f, surface, p.halfWidth);

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

        /// <summary>Road surface strip, UVs tiled once per road width.</summary>
        private static void AddSurface(Frame f, MeshBatch m, float nominalHalfWidth)
        {
            int n = f.n;
            var a = new Vector3[n]; var b = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = f.At(i, -f.hw[i], f.h[i]);
                b[i] = f.At(i,  f.hw[i], f.h[i]);
            }
            Strip(m, a, b, Uv.Surface, f.dist, 1f / (2f * nominalHalfWidth), Vector3.up);
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
