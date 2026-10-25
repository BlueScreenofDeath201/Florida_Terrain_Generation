using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace DeadEverywhere.FloridaGen.FloridaGenEditor
{
    /// <summary>
    /// Scene-view gizmos for everything the Florida generator knows about a chunk beyond the
    /// flat road centre-lines: the solved 3-D road profile (ground, embankment, bridge, tunnel),
    /// bridge piers, grade-separation clearances, tunnel portals, junctions, chunk-edge cuts,
    /// one-way arrows, road labels, waterways, water bodies, railways, traffic nodes and
    /// building heights.
    ///
    /// Geometry is built once per data set into <c>Vector3[]</c> arrays and drawn with a few
    /// <see cref="Handles.DrawLines(Vector3[])"/> calls; labels are culled by distance from the
    /// Scene-view camera.  Each group is rebuilt only when the object it was built from
    /// (the cached <see cref="RoadPlan"/>, or an OSM data array) is replaced, or after
    /// <see cref="Invalidate"/> (the inspector's "Rebuild Road Plan" button calls it).
    /// </summary>
    internal static class FloridaRoadGizmos
    {
        // ── Colours (also shown in the inspector legend) ──────────────────────────
        internal static readonly Color GroundColor     = new Color(0.35f, 0.90f, 0.40f, 0.95f);
        internal static readonly Color EmbankmentColor = new Color(1.00f, 0.65f, 0.15f, 0.95f);
        internal static readonly Color BridgeColor     = new Color(0.20f, 0.85f, 1.00f, 0.95f);
        internal static readonly Color TunnelColor     = new Color(0.90f, 0.30f, 1.00f, 0.95f);
        internal static readonly Color GradeWarnColor  = new Color(1.00f, 0.15f, 0.15f, 1.00f);
        internal static readonly Color PierColor       = new Color(0.65f, 0.70f, 0.80f, 0.75f);
        internal static readonly Color ClearanceOk     = new Color(0.30f, 1.00f, 0.40f, 1.00f);
        internal static readonly Color ClearanceShort  = new Color(1.00f, 0.15f, 0.15f, 1.00f);
        internal static readonly Color CutColor        = new Color(1.00f, 0.95f, 0.20f, 1.00f);
        internal static readonly Color JunctionColor   = new Color(1.00f, 1.00f, 1.00f, 0.90f);
        internal static readonly Color OnewayColor     = new Color(0.95f, 0.95f, 0.45f, 0.95f);
        internal static readonly Color WaterColor      = new Color(0.20f, 0.50f, 1.00f, 0.95f);
        internal static readonly Color CoastColor      = new Color(0.25f, 0.95f, 0.95f, 0.95f);
        internal static readonly Color RailColor       = new Color(0.85f, 0.85f, 0.90f, 0.95f);
        internal static readonly Color BuildingTagColor   = new Color(0.45f, 1.00f, 0.55f, 0.95f);
        internal static readonly Color BuildingLevelColor = new Color(1.00f, 0.90f, 0.35f, 0.95f);

        private const float LabelRange      = 300f;
        private const int   MaxRoadLabels   = 200;
        private const float MetresPerLevel  = 3.2f;
        private const float MarkerSize      = 1.2f;

        // ── Per-generator cache ───────────────────────────────────────────────────

        private struct Line { public Color color; public Vector3[] points; public bool dotted; }
        private struct Label { public Vector3 position; public string text; public Color color; }
        private struct NodeKind { public string kind; public Color color; public float stake; }

        private sealed class Group
        {
            public object key;
            public float  terrainY;
            public readonly List<Line>  lines  = new List<Line>();
            public readonly List<Label> labels = new List<Label>();
            public readonly List<(Vector3 center, Color color)> discs = new List<(Vector3, Color)>();
        }

        private sealed class Entry
        {
            public Group profile, crossings, water, rail, nodes, buildingHeights;
        }

        private static readonly ConditionalWeakTable<FloridaTerrainGenerator, Entry> s_Entries =
            new ConditionalWeakTable<FloridaTerrainGenerator, Entry>();

        private static GUIStyle s_LabelStyle;
        private static GUIStyle LabelStyle => s_LabelStyle ??= new GUIStyle
        {
            fontSize  = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal    = { textColor = Color.white }
        };

        /// <summary>Drops every cached gizmo group of <paramref name="gen"/>; they are rebuilt on the next repaint.</summary>
        internal static void Invalidate(FloridaTerrainGenerator gen)
        {
            s_Entries.Remove(gen);
            SceneView.RepaintAll();
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Entry point
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws the groups of <paramref name="gen"/> enabled by its per-chunk toggles combined with the
        /// project-wide master toggles.
        /// </summary>
        internal static void Draw(FloridaTerrainGenerator gen, bool masterRoadDetail, bool masterWaterRail,
                                  bool masterBuildings)
        {
            if (gen == null) return;

            bool wantProfile   = masterRoadDetail && gen.showRoadProfileGizmos && HasRoads(gen);
            bool wantLabels    = masterRoadDetail && gen.showRoadLabelGizmos   && HasRoads(gen);
            bool wantNodes     = masterRoadDetail && gen.showTrafficNodeGizmos && gen.osmNodes != null && gen.osmNodes.Length > 0;
            bool wantWater     = masterWaterRail  && gen.showWaterGizmos
                                 && ((gen.osmWaterways != null && gen.osmWaterways.Length > 0) ||
                                     (gen.osmWaterBodies != null && gen.osmWaterBodies.Length > 0));
            bool wantRail      = masterWaterRail  && gen.showRailwayGizmos && gen.osmRailways != null && gen.osmRailways.Length > 0;
            bool wantBuildings = masterBuildings  && gen.showBuildingHeightGizmos && gen.osmBuildings != null && gen.osmBuildings.Length > 0;
            if (!wantProfile && !wantLabels && !wantNodes && !wantWater && !wantRail && !wantBuildings) return;

            var entry = s_Entries.GetOrCreateValue(gen);
            Vector3 cam = CameraPosition();

            if (wantProfile || wantLabels)
            {
                var plan = gen.GetRoadPlan();
                var profile = Fetch(ref entry.profile, plan, gen, () => BuildProfile(gen, plan));
                if (wantProfile)
                {
                    DrawLines(profile);
                    DrawDiscs(profile);
                    var crossings = Fetch(ref entry.crossings, plan, gen, () => BuildCrossings(gen, plan));
                    DrawLines(crossings);
                    DrawLabels(crossings, cam, LabelRange * 0.6f, int.MaxValue);
                }
                if (wantLabels) DrawLabels(profile, cam, LabelRange, MaxRoadLabels);
            }

            if (wantWater)
            {
                var water = Fetch(ref entry.water, gen.osmWaterways, gen, () => BuildWater(gen), gen.osmWaterBodies);
                DrawLines(water);
                DrawLabels(water, cam, LabelRange, 100);
            }
            if (wantRail)
            {
                var rail = Fetch(ref entry.rail, gen.osmRailways, gen, () => BuildRail(gen));
                DrawLines(rail);
                DrawLabels(rail, cam, LabelRange, 100);
            }
            if (wantNodes)
            {
                var nodes = Fetch(ref entry.nodes, gen.osmNodes, gen, () => BuildNodes(gen));
                DrawLines(nodes);
                DrawLabels(nodes, cam, LabelRange * 0.5f, 100);
            }
            if (wantBuildings)
            {
                var heights = Fetch(ref entry.buildingHeights, gen.osmBuildings, gen, () => BuildBuildingHeights(gen));
                DrawLines(heights);
                DrawLabels(heights, cam, LabelRange * 0.5f, 100);
            }
        }

        private static bool HasRoads(FloridaTerrainGenerator gen) => gen.osmRoads != null && gen.osmRoads.Length > 0;

        /// <summary>
        /// Returns the cached group when it was built from the same <paramref name="key"/> (and
        /// optional <paramref name="key2"/>) at the same terrain height; otherwise builds it.
        /// </summary>
        private static Group Fetch(ref Group slot, object key, FloridaTerrainGenerator gen, Func<Group> build,
                                   object key2 = null)
        {
            object composite = key2 == null ? key : (object)Tuple.Create(key, key2);
            float y = gen.transform.position.y;
            if (slot != null && slot.terrainY == y && SameKey(slot.key, composite)) return slot;
            slot = build();
            slot.key = composite;
            slot.terrainY = y;
            return slot;
        }

        private static bool SameKey(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is Tuple<object, object> ta && b is Tuple<object, object> tb)
                return ReferenceEquals(ta.Item1, tb.Item1) && ReferenceEquals(ta.Item2, tb.Item2);
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Drawing
        // ═════════════════════════════════════════════════════════════════════════

        private static void DrawLines(Group g)
        {
            foreach (var l in g.lines)
            {
                if (l.points == null || l.points.Length < 2) continue;
                Handles.color = l.color;
                if (l.dotted) Handles.DrawDottedLines(l.points, 4f);
                else          Handles.DrawLines(l.points);
            }
        }

        private static void DrawDiscs(Group g)
        {
            foreach (var (center, color) in g.discs)
            {
                Handles.color = color;
                Handles.DrawWireDisc(center, Vector3.up, 3f);
            }
        }

        private static void DrawLabels(Group g, Vector3 cam, float range, int max)
        {
            if (g.labels.Count == 0) return;
            float r2 = range * range;
            int drawn = 0;
            var style = LabelStyle;
            foreach (var l in g.labels)
            {
                if ((l.position - cam).sqrMagnitude > r2) continue;
                style.normal.textColor = l.color;
                Handles.Label(l.position, l.text, style);
                if (++drawn >= max) break;
            }
        }

        private static Vector3 CameraPosition()
        {
            var view = SceneView.currentDrawingSceneView;
            if (view != null && view.camera != null) return view.camera.transform.position;
            return Camera.current != null ? Camera.current.transform.position : new Vector3(float.MaxValue, 0f, 0f);
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Road profile
        // ═════════════════════════════════════════════════════════════════════════

        private sealed class Bucket
        {
            public readonly Dictionary<Color, List<Vector3>> byColor = new Dictionary<Color, List<Vector3>>();
            public List<Vector3> Of(Color c)
            {
                if (!byColor.TryGetValue(c, out var l)) byColor[c] = l = new List<Vector3>();
                return l;
            }
            public void Segment(Color c, Vector3 a, Vector3 b) { var l = Of(c); l.Add(a); l.Add(b); }
            public void Flush(Group g, bool dotted = false)
            {
                foreach (var kv in byColor)
                    g.lines.Add(new Line { color = kv.Key, points = kv.Value.ToArray(), dotted = dotted });
            }
        }

        private static Group BuildProfile(FloridaTerrainGenerator gen, RoadPlan plan)
        {
            var g = new Group();
            var profile = new Bucket();
            var support = new Bucket();
            var arrows  = new Bucket();
            var portals = new Bucket();
            var longest = new Dictionary<int, int>();   // source way → index of its longest piece

            for (int i = 0; i < plan.roads.Count; i++)
            {
                var p = plan.roads[i];
                if (!longest.TryGetValue(p.source, out int j) || plan.roads[j].Length < p.Length) longest[p.source] = i;
            }

            for (int i = 0; i < plan.roads.Count; i++)
            {
                var p = plan.roads[i];
                int n = p.pos.Length;

                // ── Centre-line, coloured by what the road is doing there ─────────
                for (int k = 0; k + 1 < n; k++)
                {
                    float seg = Mathf.Max(0.01f, p.dist[k + 1] - p.dist[k]);
                    float grade = Mathf.Abs(p.height[k + 1] - p.height[k]) / seg;
                    Color c;
                    if (grade > p.maxGrade + 0.005f) c = GradeWarnColor;
                    else if (p.kind == RoadKind.Bridge) c = BridgeColor;
                    else if (p.kind == RoadKind.Tunnel) c = TunnelColor;
                    else if (p.height[k] - p.ground[k] > 0.4f) c = EmbankmentColor;
                    else c = GroundColor;
                    profile.Segment(c, Point(p, k, 0.15f), Point(p, k + 1, 0.15f));
                }

                // ── Bridge piers / clearance verticals ────────────────────────────
                if (p.kind == RoadKind.Bridge)
                {
                    float next = 0f;
                    for (int k = 0; k < n; k++)
                    {
                        if (p.dist[k] < next) continue;
                        next = p.dist[k] + 30f;
                        float soffit = p.height[k] - p.deckDepth;
                        if (soffit - p.ground[k] < 0.5f) continue;
                        support.Segment(PierColor, new Vector3(p.pos[k].x, p.ground[k], p.pos[k].y),
                                                   new Vector3(p.pos[k].x, soffit, p.pos[k].y));
                    }
                }

                // ── One-way arrows every ~80 m ────────────────────────────────────
                var src = gen.osmRoads != null && p.source < gen.osmRoads.Length ? gen.osmRoads[p.source] : null;
                if (src != null && src.oneway != 0 && p.Length > 20f)
                {
                    for (float s = 40f; s < p.Length - 10f; s += 80f)
                        AddArrow(arrows, p, s, src.oneway);
                }

                // ── Chunk-edge cuts and tunnel portals ────────────────────────────
                if (p.cutStart) g.discs.Add((Point(p, 0, 0.15f), CutColor));
                if (p.cutEnd)   g.discs.Add((Point(p, n - 1, 0.15f), CutColor));
                if (p.kind == RoadKind.Tunnel)
                {
                    if (p.capStart) AddPortal(portals, g, p, 0, src);
                    if (p.capEnd)   AddPortal(portals, g, p, n - 1, src);
                    if (p.tunnelHillRise > 3f)
                    {
                        int mid = n / 2;
                        g.labels.Add(new Label
                        {
                            position = Point(p, mid, p.tunnelHeight + 3f),
                            text = $"hill {p.tunnelHillRise:F0} m above this tunnel (tube follows the surface)",
                            color = GradeWarnColor
                        });
                    }
                }
            }

            // ── Junctions: white crosses where three or more road ends meet ───────
            var junction = new List<Vector3>();
            foreach (var jn in plan.junctions)
            {
                float y = gen.TerrainSurfaceY(jn.x, jn.y) + 0.3f;
                junction.Add(new Vector3(jn.x - MarkerSize, y, jn.y)); junction.Add(new Vector3(jn.x + MarkerSize, y, jn.y));
                junction.Add(new Vector3(jn.x, y, jn.y - MarkerSize)); junction.Add(new Vector3(jn.x, y, jn.y + MarkerSize));
            }
            if (junction.Count > 0) g.lines.Add(new Line { color = JunctionColor, points = junction.ToArray() });

            profile.Flush(g);
            support.Flush(g);
            arrows.Flush(g);
            portals.Flush(g);

            // ── Labels: one per OSM way, on its longest piece ─────────────────────
            foreach (var kv in longest)
            {
                var p = plan.roads[kv.Value];
                var src = gen.osmRoads != null && p.source < gen.osmRoads.Length ? gen.osmRoads[p.source] : null;
                string text = RoadLabel(p, src);
                if (text.Length == 0) continue;
                int mid = p.pos.Length / 2;
                g.labels.Add(new Label { position = Point(p, mid, 2.0f), text = text, color = KindColor(p.kind) });
            }
            return g;
        }

        private static Color KindColor(RoadKind kind) =>
            kind == RoadKind.Bridge ? BridgeColor : kind == RoadKind.Tunnel ? TunnelColor : GroundColor;

        private static Vector3 Point(PlannedRoad p, int k, float lift) =>
            new Vector3(p.pos[k].x, p.height[k] + lift, p.pos[k].y);

        private static string RoadLabel(PlannedRoad p, FloridaTerrainGenerator.OsmRoad src)
        {
            var parts = new List<string>(6);
            string name = !string.IsNullOrEmpty(p.refTag) ? p.refTag : p.name;
            if (!string.IsNullOrEmpty(p.refTag) && !string.IsNullOrEmpty(p.name)) name = p.refTag + " " + p.name;
            parts.Add(string.IsNullOrEmpty(name) ? p.highway : name);
            if (src != null)
            {
                if (src.lanes > 0) parts.Add(src.lanes + " lanes");
                if (!string.IsNullOrEmpty(src.maxSpeed)) parts.Add(src.maxSpeed);
            }
            float lift = 0f;
            for (int k = 0; k < p.height.Length; k++) lift = Mathf.Max(lift, p.height[k] - p.ground[k]);
            switch (p.kind)
            {
                case RoadKind.Bridge:
                    string type = src != null && !string.IsNullOrEmpty(src.bridgeType) ? " " + src.bridgeType : "";
                    parts.Add($"bridge{type} L{p.layer}, up to {lift:F1} m, {p.Length:F0} m long");
                    break;
                case RoadKind.Tunnel:
                    parts.Add($"tunnel L{p.layer}, {p.Length:F0} m long");
                    break;
                default:
                    if (lift > 0.4f) parts.Add($"embankment up to {lift:F1} m");
                    break;
            }
            return string.Join("  \u00b7  ", parts);
        }

        private static void AddArrow(Bucket b, PlannedRoad p, float s, int direction)
        {
            int k = 1;
            while (k < p.dist.Length - 1 && p.dist[k] < s) k++;
            Vector2 d = (p.pos[k] - p.pos[k - 1]).normalized * direction;
            if (d.sqrMagnitude < 0.1f) return;
            Vector2 r = new Vector2(d.y, -d.x);
            Vector2 tip = p.pos[k];
            float y = p.height[k] + 0.3f;
            Vector2 back = tip - d * 4f;
            b.Segment(OnewayColor, new Vector3(back.x, y, back.y), new Vector3(tip.x, y, tip.y));
            Vector2 w1 = tip - d * 2f + r * 1.5f, w2 = tip - d * 2f - r * 1.5f;
            b.Segment(OnewayColor, new Vector3(tip.x, y, tip.y), new Vector3(w1.x, y, w1.y));
            b.Segment(OnewayColor, new Vector3(tip.x, y, tip.y), new Vector3(w2.x, y, w2.y));
        }

        private static void AddPortal(Bucket b, Group g, PlannedRoad p, int k, FloridaTerrainGenerator.OsmRoad src)
        {
            int a = Mathf.Clamp(k == 0 ? 1 : k, 1, p.pos.Length - 1);
            Vector2 d = (p.pos[a] - p.pos[a - 1]).normalized;
            if (d.sqrMagnitude < 0.1f) return;
            Vector2 r = new Vector2(d.y, -d.x);
            float half = p.HalfWidthAt(k) + 0.9f;
            float y0 = p.height[k], y1 = y0 + p.tunnelHeight + 0.6f;
            Vector2 c = p.pos[k];
            Vector3 bl = new Vector3(c.x - r.x * half, y0, c.y - r.y * half);
            Vector3 br = new Vector3(c.x + r.x * half, y0, c.y + r.y * half);
            Vector3 tl = new Vector3(bl.x, y1, bl.z), tr = new Vector3(br.x, y1, br.z);
            b.Segment(TunnelColor, bl, br); b.Segment(TunnelColor, br, tr);
            b.Segment(TunnelColor, tr, tl); b.Segment(TunnelColor, tl, bl);
            string type = src != null && !string.IsNullOrEmpty(src.tunnelType) ? src.tunnelType : "yes";
            g.labels.Add(new Label { position = tl + Vector3.up * 0.5f, text = $"tunnel portal ({type})", color = TunnelColor });
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Grade separations
        // ═════════════════════════════════════════════════════════════════════════

        private static Group BuildCrossings(FloridaTerrainGenerator gen, RoadPlan plan)
        {
            var g = new Group();
            var ok = new List<Vector3>(); var bad = new List<Vector3>();
            foreach (var c in plan.crossings)
            {
                if (c.upper < 0 || c.upper >= plan.roads.Count) continue;
                var up = plan.roads[c.upper];
                float deck = up.HeightAt(c.upperDist);
                float soffit = deck - up.deckDepth;
                float clear = soffit - c.lowerHeight;
                bool met = clear >= c.clearance - 0.05f;
                var list = met ? ok : bad;
                Vector3 lo = new Vector3(c.point.x, c.lowerHeight, c.point.y);
                Vector3 hi = new Vector3(c.point.x, soffit, c.point.y);
                list.Add(lo); list.Add(hi);
                list.Add(lo + new Vector3(-MarkerSize, 0f, 0f)); list.Add(lo + new Vector3(MarkerSize, 0f, 0f));
                list.Add(hi + new Vector3(-MarkerSize, 0f, 0f)); list.Add(hi + new Vector3(MarkerSize, 0f, 0f));
                string what = string.IsNullOrEmpty(c.label) ? "road" : c.label;
                g.labels.Add(new Label
                {
                    position = (lo + hi) * 0.5f + new Vector3(1.5f, 0f, 0f),
                    text = met ? $"{what}: {clear:F1} m clear (min {c.clearance:F1})"
                               : $"{what}: only {clear:F1} m clear (needs {c.clearance:F1})",
                    color = met ? ClearanceOk : ClearanceShort
                });
            }
            if (ok.Count  > 0) g.lines.Add(new Line { color = ClearanceOk,    points = ok.ToArray() });
            if (bad.Count > 0) g.lines.Add(new Line { color = ClearanceShort, points = bad.ToArray() });
            return g;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Water, railways, nodes, buildings
        // ═════════════════════════════════════════════════════════════════════════

        private static Vector3 OnGround(FloridaTerrainGenerator gen, Vector2 p, float lift) =>
            new Vector3(p.x, gen.TerrainSurfaceY(p.x, p.y) + lift, p.y);

        private static void AddPolyline(FloridaTerrainGenerator gen, List<Vector3> into, Vector2[] pts, float lift, bool closed)
        {
            int n = pts.Length;
            int count = closed ? n : n - 1;
            for (int i = 0; i < count; i++)
            {
                into.Add(OnGround(gen, pts[i], lift));
                into.Add(OnGround(gen, pts[(i + 1) % n], lift));
            }
        }

        private static Group BuildWater(FloridaTerrainGenerator gen)
        {
            var g = new Group();
            var rivers = new List<Vector3>(); var small = new List<Vector3>();
            var buried = new List<Vector3>(); var bodies = new List<Vector3>(); var coast = new List<Vector3>();

            if (gen.osmWaterways != null)
                foreach (var w in gen.osmWaterways)
                {
                    if (w?.points == null || w.points.Length < 2) continue;
                    var target = w.underground ? buried
                               : (w.type == "river" || w.type == "canal" || w.type == "tidal_channel") ? rivers : small;
                    AddPolyline(gen, target, w.points, 0.4f, false);
                    string label = !string.IsNullOrEmpty(w.name) ? $"{w.type} {w.name}" : w.type;
                    if (w.width > 0f) label += $" ({w.width:F0} m wide)";
                    if (w.underground) label += " \u2013 underground";
                    g.labels.Add(new Label { position = OnGround(gen, w.points[w.points.Length / 2], 2f), text = label, color = WaterColor });
                }
            if (gen.osmWaterBodies != null)
                foreach (var b in gen.osmWaterBodies)
                {
                    if (b?.outline == null || b.outline.Length < 2) continue;
                    AddPolyline(gen, b.isCoastline ? coast : bodies, b.outline, 0.4f, !b.isCoastline);
                    if (!string.IsNullOrEmpty(b.name))
                        g.labels.Add(new Label { position = OnGround(gen, b.outline[0], 2f), text = $"{b.type} {b.name}", color = WaterColor });
                }

            if (rivers.Count > 0) g.lines.Add(new Line { color = WaterColor, points = rivers.ToArray() });
            if (small.Count  > 0) g.lines.Add(new Line { color = new Color(0.45f, 0.70f, 1f, 0.9f), points = small.ToArray() });
            if (buried.Count > 0) g.lines.Add(new Line { color = WaterColor, points = buried.ToArray(), dotted = true });
            if (bodies.Count > 0) g.lines.Add(new Line { color = new Color(0.15f, 0.40f, 0.95f, 0.95f), points = bodies.ToArray() });
            if (coast.Count  > 0) g.lines.Add(new Line { color = CoastColor, points = coast.ToArray() });
            return g;
        }

        private static Group BuildRail(FloridaTerrainGenerator gen)
        {
            var g = new Group();
            var surface = new List<Vector3>(); var bridge = new List<Vector3>(); var tunnel = new List<Vector3>();
            foreach (var r in gen.osmRailways)
            {
                if (r?.points == null || r.points.Length < 2) continue;
                AddPolyline(gen, r.isTunnel ? tunnel : r.isBridge ? bridge : surface, r.points, 0.5f, false);
                if (!string.IsNullOrEmpty(r.name) || r.isBridge || r.isTunnel)
                {
                    string label = string.IsNullOrEmpty(r.name) ? r.type : $"{r.type} {r.name}";
                    if (r.isBridge) label += " (bridge)"; else if (r.isTunnel) label += " (tunnel)";
                    g.labels.Add(new Label { position = OnGround(gen, r.points[r.points.Length / 2], 2f), text = label, color = RailColor });
                }
            }
            if (surface.Count > 0) g.lines.Add(new Line { color = RailColor, points = surface.ToArray() });
            if (bridge.Count  > 0) g.lines.Add(new Line { color = BridgeColor, points = bridge.ToArray() });
            if (tunnel.Count  > 0) g.lines.Add(new Line { color = RailColor, points = tunnel.ToArray(), dotted = true });
            return g;
        }

        private static NodeKind Describe(string kind)
        {
            switch (kind)
            {
                case "traffic_signals":    return new NodeKind { kind = kind, color = new Color(1f, 0.25f, 0.25f), stake = 6f };
                case "stop":               return new NodeKind { kind = kind, color = new Color(1f, 0.55f, 0.15f), stake = 3f };
                case "give_way":           return new NodeKind { kind = kind, color = new Color(1f, 0.90f, 0.20f), stake = 3f };
                case "crossing":           return new NodeKind { kind = kind, color = new Color(0.95f, 0.95f, 0.95f), stake = 2f };
                case "street_lamp":        return new NodeKind { kind = kind, color = new Color(1f, 0.95f, 0.60f), stake = 8f };
                case "motorway_junction":  return new NodeKind { kind = kind, color = new Color(1f, 0.35f, 0.90f), stake = 10f };
                case "level_crossing":     return new NodeKind { kind = kind, color = new Color(1f, 0.30f, 0.30f), stake = 5f };
                case "speed_camera":       return new NodeKind { kind = kind, color = new Color(0.60f, 0.40f, 1f), stake = 5f };
                case "bus_stop":           return new NodeKind { kind = kind, color = new Color(0.30f, 0.80f, 1f), stake = 3f };
                case "mini_roundabout":
                case "turning_circle":     return new NodeKind { kind = kind, color = new Color(0.70f, 1f, 0.70f), stake = 2f };
                default:                   return new NodeKind { kind = kind, color = new Color(0.8f, 0.8f, 0.8f), stake = 2f };
            }
        }

        private static Group BuildNodes(FloridaTerrainGenerator gen)
        {
            var g = new Group();
            var byColor = new Bucket();
            foreach (var node in gen.osmNodes)
            {
                if (node == null) continue;
                var d = Describe(node.kind);
                Vector3 b = OnGround(gen, node.position, 0.2f);
                Vector3 t = b + new Vector3(0f, d.stake, 0f);
                byColor.Segment(d.color, b, t);
                byColor.Segment(d.color, t + new Vector3(-0.6f, 0f, 0f), t + new Vector3(0.6f, 0f, 0f));
                byColor.Segment(d.color, t + new Vector3(0f, 0f, -0.6f), t + new Vector3(0f, 0f, 0.6f));
                string text = null;
                if (node.kind == "motorway_junction") text = "exit " + (string.IsNullOrEmpty(node.refTag) ? "?" : node.refTag) + (string.IsNullOrEmpty(node.name) ? "" : " " + node.name);
                else if (node.kind == "level_crossing") text = "level crossing";
                else if (node.kind == "traffic_signals" || node.kind == "speed_camera") text = node.kind.Replace('_', ' ');
                else if (!string.IsNullOrEmpty(node.name)) text = node.name;
                if (text != null) g.labels.Add(new Label { position = t + new Vector3(0.5f, 0.5f, 0f), text = text, color = d.color });
            }
            byColor.Flush(g);
            return g;
        }

        private static Group BuildBuildingHeights(FloridaTerrainGenerator gen)
        {
            var g = new Group();
            var tagged = new List<Vector3>(); var leveled = new List<Vector3>();
            foreach (var b in gen.osmBuildings)
            {
                if (b?.footprint == null || b.footprint.Length < 3) continue;
                bool fromTag = b.height > 0f;
                float top = fromTag ? b.height : b.levels > 0 ? b.levels * MetresPerLevel : 0f;
                if (top <= 0f) continue;
                float bottom = b.minHeight > 0f ? b.minHeight : b.minLevel > 0 ? b.minLevel * MetresPerLevel : 0f;
                if (bottom >= top) bottom = 0f;

                Vector2 c = Vector2.zero;
                foreach (var p in b.footprint) c += p;
                c /= b.footprint.Length;
                float ground = gen.TerrainSurfaceY(c.x, c.y);
                var list = fromTag ? tagged : leveled;
                list.Add(new Vector3(c.x, ground + bottom, c.y));
                list.Add(new Vector3(c.x, ground + top, c.y));

                int n = b.footprint.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector2 p0 = b.footprint[i], p1 = b.footprint[(i + 1) % n];
                    list.Add(new Vector3(p0.x, ground + top, p0.y));
                    list.Add(new Vector3(p1.x, ground + top, p1.y));
                }
                if (g.labels.Count < 400)
                {
                    string roof = string.IsNullOrEmpty(b.roofShape) ? "" : $", {b.roofShape} roof";
                    g.labels.Add(new Label
                    {
                        position = new Vector3(c.x, ground + top + 0.5f, c.y),
                        text = (fromTag ? $"{top:F1} m" : $"~{top:F0} m ({b.levels} levels)") + roof,
                        color = fromTag ? BuildingTagColor : BuildingLevelColor
                    });
                }
            }
            if (tagged.Count  > 0) g.lines.Add(new Line { color = BuildingTagColor,   points = tagged.ToArray() });
            if (leveled.Count > 0) g.lines.Add(new Line { color = BuildingLevelColor, points = leveled.ToArray() });
            return g;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Inspector legend
        // ═════════════════════════════════════════════════════════════════════════

        internal static readonly (Color color, string text)[] Legend =
        {
            (GroundColor,     "Road on the ground"),
            (EmbankmentColor, "Road lifted more than 0.4 m (approach embankment)"),
            (BridgeColor,     "Bridge deck centre-line"),
            (TunnelColor,     "Tunnel centre-line and portal frames"),
            (GradeWarnColor,  "Segment steeper than the road class grade limit"),
            (PierColor,       "Bridge pier / clearance to the ground"),
            (ClearanceOk,     "Grade separation with enough clearance"),
            (ClearanceShort,  "Grade separation short of its clearance"),
            (CutColor,        "Road cut at the chunk edge (continues in the next chunk)"),
            (JunctionColor,   "Junction (three or more road ends)"),
            (OnewayColor,     "One-way direction"),
            (WaterColor,      "Waterways and water bodies (dotted = culvert)"),
            (RailColor,       "Railways (cyan = bridge, dotted = tunnel)"),
            (BuildingTagColor,   "Building height from the OSM height tag"),
            (BuildingLevelColor, "Building height estimated from levels (3.2 m each)"),
        };
    }
}
