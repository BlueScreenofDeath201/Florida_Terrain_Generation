using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace DeadEverywhere.FloridaGeneration.Editor
{
    public class FloridaGenerationTests
    {
        [Test]
        public void ResidentialDefaultsMatchTenMetreReferenceAndMappedWidthsWin()
        {
            Assert.That(WorldChunk.RoadTileSize, Is.EqualTo(10f));
            Assert.That(FloridaRoadStandards.HalfWidth(Road("residential")), Is.EqualTo(5f));
            Assert.That(FloridaRoadStandards.HalfWidth(Road("residential", lanes: 2)), Is.EqualTo(5f));
            Assert.That(FloridaRoadStandards.HalfWidth(Road("residential", width: 7.2f, lanes: 2)),
                        Is.EqualTo(3.6f).Within(0.00001f));
            Assert.That(FloridaRoadStandards.HalfWidth(Road("service")), Is.EqualTo(3f));
            Assert.That(2f * FloridaRoadStandards.LaneWidth + 2f * FloridaRoadStandards.ResidentialShoulder,
                        Is.EqualTo(WorldChunk.RoadTileSize));
        }

        [TestCase("motorway", 0, 15.24f)]
        [TestCase("motorway", 1, 7.9248f)]
        [TestCase("motorway", 2, 11.5824f)]
        [TestCase("motorway", 4, 18.8976f)]
        [TestCase("motorway_link", 0, 7.3152f)]
        [TestCase("motorway_link", 2, 10.9728f)]
        [TestCase("trunk", 2, 13.4112f)]
        public void HighwayWidthsIncludeTwelveFootLanesAndRealShoulders(string highway, int lanes, float expected)
        {
            var road = Road(highway, lanes: lanes);
            Assert.That(FloridaRoadStandards.HalfWidth(road) * 2f, Is.EqualTo(expected).Within(0.00002f));
            var plan = FloridaRoadTranslator.Translate(new[] { road }, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(128f, 128f),
                minimumMotorwayLanes = 0, groundHeight = (x, z) => 0f
            });
            var p = plan.roads.Single();
            FloridaRoadStandards.LaneEdges(p, p.halfWidth, out float left, out float right);
            Assert.That(right - left, Is.EqualTo(p.lanes * 3.6576f).Within(0.00002f));
            if (p.oneway != 0)
            {
                Assert.That(left + p.halfWidth, Is.EqualTo(1.2192f).Within(0.00002f));
                Assert.That(p.halfWidth - right, Is.EqualTo(highway.EndsWith("_link") ? 2.4384f : 3.048f).Within(0.00002f));
            }
            var mesh = BuildRoads(plan).surface[0];
            Assert.That(mesh.verts.Max(v => v.z) - mesh.verts.Min(v => v.z), Is.EqualTo(expected).Within(0.00003f));
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void HighwayPaintMatchesAsymmetricLaneEnvelopeAndDirection(int direction)
        {
            var road = Road("motorway", lanes: 3);
            road.oneway = direction;
            var plan = PlanRoads(road);
            var p = plan.roads[0];
            FloridaRoadStandards.LaneEdges(p, p.halfWidth, out float left, out float right);
            float yellow = direction > 0 ? left : right;
            var meshes = BuildRoads(plan);
            Assert.That(meshes.yellowPaint.verts, Is.Not.Empty);
            foreach (var vertex in meshes.yellowPaint.verts)
                Assert.That(Mathf.Abs((16f - vertex.z) - yellow), Is.LessThanOrEqualTo(0.10002f));
            Assert.That(meshes.yellowPaint.verts.Max(v => v.z) - meshes.yellowPaint.verts.Min(v => v.z),
                        Is.EqualTo(0.2f).Within(0.00002f));
            for (int divider = 1; divider < p.lanes; divider++)
            {
                float lateral = Mathf.Lerp(left, right, (float)divider / p.lanes);
                Assert.That(TryMeshHeight(meshes.whitePaint, new Vector2(11f, 16f - lateral), out _), Is.True);
                Assert.That(TryMeshHeight(meshes.whitePaint, new Vector2(16f, 16f - lateral), out _), Is.False);
            }
            foreach (var uv in meshes.whitePaint.uvs.Concat(meshes.yellowPaint.uvs))
                Assert.That(uv.x, Is.InRange(0f, 1f));
        }

        [TestCase("motorway_link", 1, 0f)]
        [TestCase("motorway_link", -1, 0f)]
        [TestCase("trunk_link", 1, 0f)]
        [TestCase("trunk_link", -1, 0f)]
        [TestCase("primary_link", 1, 0f)]
        [TestCase("primary_link", -1, 0f)]
        [TestCase("secondary_link", 1, 0f)]
        [TestCase("tertiary_link", -1, 0f)]
        [TestCase("motorway_link", 1, 9f)]
        [TestCase("motorway_link", -1, 9f)]
        public void SingleLaneRampPaintFollowsPavementSidesInsteadOfTheMiddle(string highway, int direction, float width)
        {
            var road = Road(highway, lanes: 1, width: width);
            road.oneway = direction;
            var plan = PlanRoads(road);
            var p = plan.roads.Single();
            var meshes = BuildRoads(plan);
            float edge = p.halfWidth - FloridaRoadStandards.EdgeLineInset;
            float stripeWidth = FloridaRoadStandards.IsMotorwayClass(highway)
                ? FloridaRoadStandards.HighwayEdgePaintWidth : FloridaRoadStandards.PaintWidth;
            foreach (var vertex in meshes.yellowPaint.verts)
                Assert.That(Mathf.Abs((16f - vertex.z) + direction * edge),
                    Is.LessThanOrEqualTo(stripeWidth * 0.5f + 0.00002f));
            foreach (var vertex in meshes.whitePaint.verts)
                Assert.That(Mathf.Abs((16f - vertex.z) - direction * edge),
                    Is.LessThanOrEqualTo(stripeWidth * 0.5f + 0.00002f));
            Assert.That(meshes.yellowPaint.verts, Is.Not.Empty);
            Assert.That(meshes.whitePaint.verts, Is.Not.Empty);
            foreach (var paint in new[] { meshes.yellowPaint, meshes.whitePaint })
                Assert.That(TryMeshHeight(paint, new Vector2(30f, 16f), out _), Is.False);
        }

        [Test]
        public void MultiLaneAndExplicitlyTwoWayRampsRetainTheirRealDividers()
        {
            var road = Road("motorway_link", lanes: 2);
            var plan = PlanRoads(road);
            var p = plan.roads.Single();
            FloridaRoadStandards.LaneEdges(p, p.halfWidth, out float left, out float right);
            var meshes = BuildRoads(plan);
            float divider = (left + right) * 0.5f;
            Assert.That(TryMeshHeight(meshes.whitePaint, new Vector2(11f, 16f - divider), out _), Is.True);
            Assert.That(TryMeshHeight(meshes.whitePaint, new Vector2(16f, 16f - divider), out _), Is.False);
            road.onewaySpecified = true;
            meshes = BuildRoads(PlanRoads(road));
            Assert.That(meshes.yellowPaint.verts, Is.Not.Empty);
            foreach (var vertex in meshes.yellowPaint.verts)
                Assert.That(Mathf.Abs(vertex.z - 16f), Is.InRange(0.07499f, 0.22501f));
        }

        [Test]
        public void ExplicitWidthsAndExplicitTwoWayMotorwaysRemainAuthoritative()
        {
            var road = Road("motorway", width: 12f, lanes: 2);
            road.onewaySpecified = true;
            var p = PlanRoads(road).roads.Single();
            Assert.That(p.halfWidth, Is.EqualTo(6f));
            Assert.That(p.oneway, Is.Zero);
            var loaded = FloridaChunkFile.FromJson(FloridaChunkFile.ToJson(new FloridaChunkData { roads = new[] { road } }));
            Assert.That(loaded.roads[0].onewaySpecified, Is.True);
            Assert.That(FloridaRoadStandards.TravelDirection(loaded.roads[0]), Is.Zero);
            Assert.That(FloridaRoadStandards.HalfWidth(Road("motorway", lanes: 8)),
                        Is.GreaterThan(14f), "Wide mapped carriageways must not silently lose lanes to the old cap.");
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void MainlineLaneMinimumWidensPavementAndPaintWithoutChangingSourceData(int direction)
        {
            var road = Road("motorway", lanes: 1);
            road.oneway = direction;
            var plan = PlanRoads(road);
            var main = plan.roads.Single();
            Assert.That(road.lanes, Is.EqualTo(1));
            Assert.That(main.lanes, Is.EqualTo(3));
            Assert.That(main.halfWidth * 2f, Is.EqualTo(15.24f).Within(0.00002f));
            var mesh = BuildRoads(plan);
            FloridaRoadStandards.LaneEdges(main, main.halfWidth, out float left, out float right);
            for (int divider = 1; divider < 3; divider++)
                Assert.That(TryMeshHeight(mesh.whitePaint,
                    new Vector2(11f, 16f - Mathf.Lerp(left, right, divider / 3f)), out _), Is.True);
        }

        [Test]
        public void LaneMinimumLeavesRampsExplicitWidthsAndTwoWayRoadsAloneAndCanBeDisabled()
        {
            var ramp = Road("motorway_link", lanes: 1);
            Assert.That(PlanRoads(ramp).roads.Single().lanes, Is.EqualTo(1));
            var main = Road("motorway", lanes: 1, width: 8f);
            Assert.That(PlanRoads(main).roads.Single().halfWidth, Is.EqualTo(4f));
            Assert.That(PlanRoads(main).roads.Single().lanes, Is.EqualTo(1));
            main.width = 0f;
            main.onewaySpecified = true;
            Assert.That(PlanRoads(main).roads.Single().lanes, Is.EqualTo(1));
            main.onewaySpecified = false;
            var mapped = FloridaRoadTranslator.Translate(new[] { main }, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(128f, 128f),
                minimumMotorwayLanes = 0, groundHeight = (x, z) => 0f
            }).roads.Single();
            Assert.That(mapped.lanes, Is.EqualTo(1));
            Assert.That(mapped.halfWidth * 2f, Is.EqualTo(7.9248f).Within(0.00002f));
        }

        [Test]
        public void LaneMinimumRoundTripsAndOldChunkFilesReceiveTheNewDefault()
        {
            var data = new FloridaChunkData { minimumMotorwayLanes = 0 };
            Assert.That(FloridaChunkFile.FromJson(FloridaChunkFile.ToJson(data)).minimumMotorwayLanes, Is.Zero);
            Assert.That(FloridaChunkFile.FromJson("{\"version\":3}").minimumMotorwayLanes, Is.EqualTo(3));
        }

        [Test]
        public void WiderMainlinesStillRespectParallelDeckClearanceIncludingTheirEnds()
        {
            var plan = PlanRoads(Road("motorway", lanes: 1, z: 56f, bridge: true),
                                 Road("motorway", lanes: 1, z: 64f, bridge: true));
            Assert.That(plan.narrowedStations, Is.GreaterThan(0));
            foreach (var road in plan.roads)
            {
                Assert.That(road.lanes, Is.EqualTo(3));
                for (int i = 0; i < road.pos.Length; i++)
                    Assert.That(road.HalfWidthAt(i), Is.LessThanOrEqualTo(3.45002f),
                        "A requested lane minimum must never force overlapping decks.");
            }
        }

        [Test]
        public void GradedMainlineContinuationsAreNotMistakenForAdjacentCarriageways()
        {
            var before = Road("motorway", lanes: 3, z: 64f, bridge: true);
            var after = Road("motorway", lanes: 3, z: 64f, bridge: true);
            before.points = new[] { new Vector2(10f, 64f), new Vector2(60f, 64f) };
            after.points = new[] { new Vector2(60f, 64f), new Vector2(110f, 64f) };
            var plan = FloridaRoadTranslator.Translate(new[] { before, after }, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(128f, 128f),
                smoothingRadius = 0f, deckSmoothingRadius = 0f, deckClosingRadius = 0f,
                groundHeight = (x, z) => x * 0.05f
            });
            foreach (var road in plan.roads)
                for (int i = 0; i < road.pos.Length; i++)
                    Assert.That(road.HalfWidthAt(i), Is.EqualTo(road.halfWidth).Within(0.00002f));
        }

        [Test]
        public void RaisedMotorwayUsesRetainingEdgesRatherThanWideEarthMounds()
        {
            var plan = PlanRoads(Road("motorway", lanes: 3, z: 64f));
            var road = plan.roads.Single();
            for (int i = 0; i < road.height.Length; i++) road.height[i] = 8f;
            var mesh = BuildRoads(plan);
            Assert.That(mesh.embankment.verts, Is.Empty);
            Assert.That(mesh.structure.verts, Is.Not.Empty);
            foreach (var vertex in mesh.structure.verts)
                Assert.That(Mathf.Abs(vertex.z - 64f),
                    Is.LessThanOrEqualTo(road.halfWidth + FloridaRoadMeshBuilder.BarrierThickness + 0.00002f));
        }

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(90f)]
        public void EarthworksDoNotBuryOtherRoadsAtAnyAngleIncludingTheirEndpoints(float degrees)
        {
            var approach = Road("primary", z: 64f);
            var other = Road("residential");
            Vector2 direction = new Vector2(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad));
            other.points = new[] { new Vector2(64f, 49f) - direction * 20f, new Vector2(64f, 49f) };
            var plan = FloridaRoadTranslator.Translate(new[] { approach, other }, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(128f, 128f),
                sampleSpacing = 25f, groundHeight = (x, z) => 0f
            });
            var upper = plan.roads[0];
            for (int i = 0; i < upper.height.Length; i++) upper.height[i] = 6f;
            var alone = new RoadPlan();
            alone.roads.Add(upper);
            Assert.That(TryMeshHeight(BuildRoads(alone).embankment, new Vector2(64f, 49f), out _), Is.True,
                "The fixture must put the original mound across the lower road.");
            var meshes = BuildRoads(plan, new Vector3(15f, 2f, -23f));
            for (int i = 0; i <= 20; i++)
            {
                Vector2 point = other.points[1] - direction * i - new Vector2(15f, -23f);
                if (TryMeshHeight(meshes.embankment, point, out float height))
                    Assert.That(height + 2f, Is.LessThanOrEqualTo(0.061f));
            }
        }

        [Test]
        public void HighwayRampJoinKeepsMainDividersAndUsesShortDashedEdgeOpening()
        {
            var main = Road("motorway", lanes: 3, z: 64f);
            main.points = new[] { new Vector2(10f, 64f), new Vector2(40f, 64f), new Vector2(125f, 64f) };
            var link = Road("motorway_link");
            link.points = new[] { new Vector2(40f, 64f), new Vector2(80f, 54f), new Vector2(125f, 25f) };
            var plan = PlanRoads(main, link);
            var p = plan.roads[0];
            Assert.That(p.markingGaps, Is.Empty, "A motorway merge must not blank every mainline stripe.");
            Assert.That(p.mergeEdges[0], Is.Empty);
            Assert.That(p.mergeEdges[1], Is.Not.Empty);
            Assert.That(plan.roads[1].markingGaps, Is.Not.Empty);
            var mainOnly = new RoadPlan();
            mainOnly.roads.Add(p);
            var paint = BuildRoads(mainOnly);
            FloridaRoadStandards.LaneEdges(p, p.halfWidth, out float left, out float right);
            float dividerZ = 64f - Mathf.Lerp(left, right, 1f / 3f);
            Assert.That(TryMeshHeight(paint.whitePaint, new Vector2(47f, dividerZ), out _), Is.True);
            float edgeZ = 64f - right;
            Assert.That(TryMeshHeight(paint.whitePaint, new Vector2(46.8f, edgeZ), out _), Is.True);
            Assert.That(TryMeshHeight(paint.whitePaint, new Vector2(48.5f, edgeZ), out _), Is.False);
            float yellowZ = 64f - left;
            Assert.That(TryMeshHeight(paint.yellowPaint, new Vector2(48.5f, yellowZ), out _), Is.True);
        }

        [Test]
        public void CurvingRampDepartsSoonerThanItsInitialNearlyParallelTangentSuggests()
        {
            var main = Road("motorway", lanes: 3, z: 64f);
            main.points = new[] { new Vector2(10f, 64f), new Vector2(40f, 64f), new Vector2(125f, 64f) };
            var link = Road("motorway_link");
            link.points = new[]
            {
                new Vector2(40f, 64f), new Vector2(50f, 63.99f),
                new Vector2(65f, 48f), new Vector2(100f, 10f)
            };
            var plan = PlanRoads(main, link);
            var ramp = plan.roads[1];
            Assert.That(ramp.markingGaps, Is.Not.Empty);
            Assert.That(ramp.markingGaps.Max(g => g.y), Is.LessThan(ramp.Length - 10f));
            Assert.That(plan.roads[0].markingGaps, Is.Empty);
            var rampOnly = new RoadPlan();
            rampOnly.roads.Add(ramp);
            var meshes = BuildRoads(rampOnly);
            Assert.That(meshes.whitePaint.verts, Is.Not.Empty);
            Assert.That(meshes.yellowPaint.verts, Is.Not.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MainlineWaySplitAtAnExitDoesNotClearThroughLaneMarkings(bool bridgeBoundary)
        {
            var before = Road("motorway", lanes: 3, z: 64f);
            before.points = new[] { new Vector2(10f, 64f), new Vector2(40f, 64f) };
            var after = Road("motorway", lanes: 3, z: 64f, bridge: bridgeBoundary);
            after.points = new[] { new Vector2(40f, 64f), new Vector2(125f, 68f) };
            var exit = Road("motorway_link");
            exit.points = new[] { new Vector2(40f, 64f), new Vector2(80f, 54f), new Vector2(125f, 25f) };
            var plan = PlanRoads(before, after, exit);
            Assert.That(plan.roads.Take(2).All(p => p.markingGaps.Count == 0), Is.True);
            Assert.That(plan.roads[1].mergeEdges[1], Is.Not.Empty);
        }

        [Test]
        public void InitiallyCollinearRampUsesItsActualLeftDepartureForEdgeGuidance()
        {
            var main = Road("motorway", lanes: 3, z: 64f);
            main.points = new[] { new Vector2(10f, 64f), new Vector2(40f, 64f), new Vector2(125f, 64f) };
            var ramp = Road("motorway_link");
            ramp.points = new[]
            {
                new Vector2(40f, 64f), new Vector2(50f, 64f),
                new Vector2(70f, 85f), new Vector2(105f, 110f)
            };
            var p = PlanRoads(main, ramp).roads[0];
            Assert.That(p.markingGaps, Is.Empty);
            Assert.That(p.mergeEdges[0], Is.Not.Empty);
            Assert.That(p.mergeEdges[1], Is.Empty);
        }

        [Test]
        public void OpenBridgeBarrierRetainsFasciaBelowTheDrivingSurface()
        {
            var plan = PlanRoads(Road("motorway", lanes: 3, bridge: true));
            var p = plan.roads.Single();
            for (int i = 0; i < p.height.Length; i++) p.height[i] = 8f;
            p.barrierOpen = Enumerable.Repeat(true, p.pos.Length * 2).ToArray();
            var structure = BuildRoads(plan).structure;
            float edge = 16f + p.halfWidth + FloridaRoadMeshBuilder.BarrierThickness;
            bool fascia = false;
            for (int i = 0; i < structure.tris.Count; i += 3)
            {
                var a = structure.verts[structure.tris[i]];
                var b = structure.verts[structure.tris[i + 1]];
                var c = structure.verts[structure.tris[i + 2]];
                if (Mathf.Abs(a.z - edge) > 0.0001f || Mathf.Abs(b.z - edge) > 0.0001f ||
                    Mathf.Abs(c.z - edge) > 0.0001f) continue;
                if (Mathf.Max(a.y, b.y, c.y) <= 8.0001f &&
                    Mathf.Min(a.y, b.y, c.y) < 8f - p.deckDepth + 0.001f &&
                    Vector3.Cross(b - a, c - a).z > 0f) fascia = true;
            }
            Assert.That(fascia, Is.True);
            Assert.That(structure.verts.Max(v => v.y), Is.LessThanOrEqualTo(8.051f),
                        "A merge opening must not reintroduce a crash barrier above the roadway.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ConnectedMotorwayWayEndsShareAnExactCrossSectionIncludingBentWidthChanges(bool reverse, bool bridgeBoundary)
        {
            var a = Road("motorway", lanes: 2, z: 64f);
            a.points = new[] { new Vector2(10f, 64f), new Vector2(60f, 64f) };
            var b = Road("motorway", lanes: 3, bridge: bridgeBoundary);
            b.points = new[] { new Vector2(60f, 64f), new Vector2(110f, 85f) };
            if (reverse) { b.points = b.points.Reverse().ToArray(); b.oneway = -1; }
            var plan = PlanRoads(a, b);
            Assert.That(plan.roads[0].HalfWidthAt(plan.roads[0].pos.Length - 1),
                        Is.EqualTo(plan.roads[1].HalfWidthAt(reverse ? plan.roads[1].pos.Length - 1 : 0)));
            var mesh = BuildRoads(plan).surface[0];
            int seamA = (plan.roads[0].pos.Length - 1) * 2;
            int seamB = plan.roads[0].pos.Length * 2 + (reverse ? (plan.roads[1].pos.Length - 1) * 2 : 0);
            Assert.That(Vector3.Distance(mesh.verts[seamA], mesh.verts[seamB + (reverse ? 1 : 0)]), Is.LessThan(0.00002f));
            Assert.That(Vector3.Distance(mesh.verts[seamA + 1], mesh.verts[seamB + (reverse ? 0 : 1)]), Is.LessThan(0.00002f));
        }

        [Test]
        public void OneNarrowStationDoesNotEraseAllHighwayMarkings()
        {
            var plan = PlanRoads(Road("motorway", lanes: 3));
            var p = plan.roads[0];
            p.halfWidthAt = Enumerable.Repeat(p.halfWidth, p.pos.Length).ToArray();
            p.halfWidthAt[p.pos.Length / 2] = 3.5f;
            var mesh = BuildRoads(plan);
            Assert.That(mesh.whitePaint.verts, Is.Not.Empty);
            Assert.That(mesh.yellowPaint.verts, Is.Not.Empty);
        }

        [Test]
        public void AggregateAsphaltHasVisibleNeutralGrainAndReadableStripeTextures()
        {
            var asphalt = FloridaRoadTexture.CreateAsphalt();
            var paint = FloridaRoadTexture.CreatePaint();
            try
            {
                var pixels = asphalt.GetPixels32();
                Assert.That(pixels.Max(p => p.r) - pixels.Min(p => p.r), Is.InRange(18, 32));
                Assert.That(pixels.Min(p => p.r), Is.GreaterThanOrEqualTo(224));
                Assert.That(pixels.All(p => p.r == p.g && p.g == p.b), Is.True);
                Assert.That(paint.wrapModeU, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(paint.wrapModeV, Is.EqualTo(TextureWrapMode.Repeat));
                Assert.That(paint.mipmapCount, Is.GreaterThan(1));
                Assert.That(paint.GetPixels32().Min(p => p.a), Is.EqualTo(255));
                Assert.That(paint.GetPixel(0, 50).r, Is.LessThan(paint.GetPixel(paint.width / 2, 50).r));
            }
            finally { Object.DestroyImmediate(asphalt); Object.DestroyImmediate(paint); }
        }

        [Test]
        public void ResidentialPaintHasMetricDoubleYellowAndWhiteShoulderLines()
        {
            var plan = PlanRoads(Road("residential"));
            var meshes = BuildRoads(plan);
            Assert.That(meshes.yellowPaint.verts, Is.Not.Empty);
            Assert.That(meshes.whitePaint.verts, Is.Not.Empty);
            foreach (var v in meshes.yellowPaint.verts)
            {
                Assert.That(Mathf.Abs(v.z - 16f), Is.InRange(0.07499f, 0.22501f));
                Assert.That(v.y, Is.EqualTo(plan.roads[0].height[0] + 0.015f).Within(0.00001f));
            }
            for (int side = -1; side <= 1; side += 2)
            {
                var line = meshes.yellowPaint.verts.Where(v => Mathf.Sign(v.z - 16f) == side).ToArray();
                Assert.That(line.Max(v => v.z) - line.Min(v => v.z), Is.EqualTo(0.15f).Within(0.00001f));
            }
            foreach (var v in meshes.whitePaint.verts)
                Assert.That(Mathf.Abs(v.z - 16f), Is.InRange(3.42499f, 3.57501f));
            foreach (var paint in new[] { meshes.whitePaint, meshes.yellowPaint })
                for (int i = 0; i < paint.tris.Count; i += 3)
                {
                    Vector3 a = paint.verts[paint.tris[i]], b = paint.verts[paint.tris[i + 1]];
                    Vector3 c = paint.verts[paint.tris[i + 2]];
                    Assert.That(Vector3.Cross(b - a, c - a).y, Is.GreaterThan(0f));
                }
        }

        [Test, Timeout(10000)]
        public void LongRoadDashEnumerationAlwaysAdvancesAtLargeSourceDistances()
        {
            var road = Road("residential", lanes: 2);
            road.oneway = 1;
            road.points = new[] { new Vector2(-50000f, 16f), new Vector2(3000f, 16f) };
            var plan = FloridaRoadTranslator.Translate(new[] { road }, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(3000f, 128f), groundHeight = (x, z) => 0f
            });
            var paint = BuildRoads(plan).whitePaint;
            double cycle = (double)FloridaRoadStandards.DashLength + FloridaRoadStandards.DashGap;
            for (float x = 2000.25f; x < 2100f; x += 1f)
                Assert.That(PaintCoversCentre(paint, x),
                            Is.EqualTo((50000.0 + x) % cycle < FloridaRoadStandards.DashLength), $"Dash at {x}");
        }

        [Test]
        public void PaintTriangleInteriorsFollowSlopedBendsWithoutCuttingThroughAsphalt()
        {
            var road = Road("residential");
            road.points = new[]
            {
                new Vector2(10f, 30f), new Vector2(50f, 30f),
                new Vector2(80f, 65f), new Vector2(110f, 65f)
            };
            float Height(float x, float z) => 0.03f * x + 0.02f * z;
            var plan = FloridaRoadTranslator.Translate(new[] { road }, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(128f, 128f), groundHeight = Height
            });
            var meshes = new RoadMeshSet();
            FloridaRoadMeshBuilder.Build(plan, meshes, Vector3.zero, Height);
            foreach (var paint in new[] { meshes.whitePaint, meshes.yellowPaint })
                for (int i = 0; i < paint.tris.Count; i += 3)
                {
                    Vector3 centre = (paint.verts[paint.tris[i]] + paint.verts[paint.tris[i + 1]] +
                                      paint.verts[paint.tris[i + 2]]) / 3f;
                    Assert.That(TryMeshHeight(meshes.surface[3], new Vector2(centre.x, centre.z), out float y), Is.True);
                    Assert.That(centre.y - y, Is.EqualTo(0.015f).Within(0.001f));
                }
        }

        [TestCase(1, -1)]
        [TestCase(-1, 1)]
        public void OneWayRoadsHaveYellowOnTrafficLeftAndDashedWhiteDividers(int direction, int yellowSide)
        {
            var road = Road("residential", lanes: 2);
            road.oneway = direction;
            var meshes = BuildRoads(PlanRoads(road));
            Assert.That(meshes.yellowPaint.verts, Is.Not.Empty);
            // This road travels along +X: its frame's right side is -Z.
            foreach (var v in meshes.yellowPaint.verts)
                Assert.That(Mathf.Sign(16f - v.z), Is.EqualTo(yellowSide));
            Assert.That(PaintCoversCentre(meshes.whitePaint, 11f), Is.True);
            Assert.That(PaintCoversCentre(meshes.whitePaint, 16f), Is.False);
            Assert.That(PaintCoversCentre(meshes.whitePaint, 23f), Is.True);
        }

        [Test]
        public void DashPhaseAndAsphaltScaleSurviveChunkClippingAndNonzeroOrigins()
        {
            var road = Road("residential", lanes: 2);
            road.oneway = 1;
            var full = BuildRoads(PlanRoads(road));
            var clipped = FloridaRoadTranslator.Translate(new[] { road }, null, new RoadTranslationSettings
            {
                clipMin = new Vector2(63f, 0f), clipMax = new Vector2(128f, 128f),
                groundHeight = (x, z) => 0f
            });
            Assert.That(clipped.roads[0].sourceDistance, Is.EqualTo(53f).Within(0.00001f));
            var origin = new Vector3(63f, -250f, -20f);
            var meshes = BuildRoads(clipped, origin);
            for (float x = 63.25f; x < 110f; x += 0.5f)
                Assert.That(PaintCoversCentre(meshes.whitePaint, x, origin),
                            Is.EqualTo(PaintCoversCentre(full.whitePaint, x)), $"Dash at X={x}");
            var surface = meshes.surface[3];
            for (int i = 0; i < surface.verts.Count; i++)
            {
                Vector3 world = surface.verts[i] + origin;
                Assert.That(surface.uvs[i].x, Is.EqualTo(world.x / 4f).Within(0.00001f));
                Assert.That(surface.uvs[i].y, Is.EqualTo(world.z / 4f).Within(0.00001f));
            }
        }

        [Test]
        public void ConnectedJunctionsClearPaintButCollinearSplitsAndOverpassesDoNot()
        {
            var main = Road("residential");
            main.points = new[] { new Vector2(10f, 16f), new Vector2(60f, 16f), new Vector2(110f, 16f) };
            var side = Road("residential");
            side.points = new[] { new Vector2(60f, 16f), new Vector2(60f, 100f) };
            var plan = PlanRoads(main, side);
            Assert.That(plan.roads.All(p => p.markingGaps.Count > 0), Is.True);
            var mainOnly = new RoadPlan();
            mainOnly.roads.Add(plan.roads[0]);
            var paint = BuildRoads(mainOnly);
            foreach (var v in paint.whitePaint.verts.Concat(paint.yellowPaint.verts))
                Assert.That(v.x <= 54.001f || v.x >= 65.999f, Is.True, $"Paint in junction at {v}");

            var continuation = Road("residential");
            continuation.points = new[] { new Vector2(110f, 16f), new Vector2(125f, 16f) };
            Assert.That(PlanRoads(main, continuation).roads.All(p => p.markingGaps.Count == 0), Is.True);
            side.isBridge = true;
            side.points = new[] { new Vector2(60f, 0f), new Vector2(60f, 100f) };
            Assert.That(PlanRoads(main, side).roads.All(p => p.markingGaps.Count == 0), Is.True);
        }

        [Test]
        public void ShallowMergesAlsoClearOverlappingPaint()
        {
            var main = Road("residential");
            main.points = new[] { new Vector2(10f, 16f), new Vector2(60f, 16f), new Vector2(125f, 16f) };
            var side = Road("residential");
            side.points = new[]
            {
                new Vector2(60f, 16f),
                new Vector2(60f, 16f) + new Vector2(Mathf.Cos(10f * Mathf.Deg2Rad), Mathf.Sin(10f * Mathf.Deg2Rad)) * 60f
            };
            Assert.That(PlanRoads(main, side).roads.All(p => p.markingGaps.Count > 0), Is.True);
        }

        [TestCase("service", "")]
        [TestCase("living_street", "")]
        [TestCase("residential", "gravel")]
        [TestCase("residential", "unpaved")]
        public void UnmarkedAndUnpavedRoadsDoNotReceiveHighwayPaint(string highway, string surface)
        {
            var road = Road(highway);
            road.surface = surface;
            var meshes = BuildRoads(PlanRoads(road));
            Assert.That(meshes.whitePaint.verts, Is.Empty);
            Assert.That(meshes.yellowPaint.verts, Is.Empty);
            Assert.That(meshes.surface[3].verts, Is.Not.Empty);
        }

        [Test]
        public void AsphaltTexturesAreRepeatableMipmappedAndDoNotConsumeGameplayRandomness()
        {
            UnityEngine.Random.State before = UnityEngine.Random.state;
            Texture2D first = null, second = null, normal = null;
            try
            {
                first = FloridaRoadTexture.CreateAsphalt();
                second = FloridaRoadTexture.CreateAsphalt();
                normal = FloridaRoadTexture.CreateAsphalt(true);
                Assert.That(UnityEngine.Random.state, Is.EqualTo(before));
                Assert.That(first.wrapMode, Is.EqualTo(TextureWrapMode.Repeat));
                Assert.That(first.mipmapCount, Is.GreaterThan(1));
                CollectionAssert.AreEqual(first.GetPixels32(), second.GetPixels32());
                var pixels = first.GetPixels32();
                Assert.That(pixels.Min(p => p.r), Is.LessThan(pixels.Max(p => p.r)));
                var normals = normal.GetPixels();
                Assert.That(normals.Max(p => Mathf.Abs(p.r - 0.5f)), Is.GreaterThan(0.05f));
                for (int i = 0; i < normals.Length; i += 97)
                {
                    var p = normals[i];
                    Vector3 n = new Vector3(p.r * 2f - 1f, p.g * 2f - 1f, p.b * 2f - 1f);
                    Assert.That(n.magnitude, Is.EqualTo(1f).Within(0.015f));
                    Assert.That(p.a, Is.EqualTo(1f));
                }
            }
            finally
            {
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
                if (normal != null) Object.DestroyImmediate(normal);
                UnityEngine.Random.state = before;
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void AsphaltDefaultsUpgradeExistingSubtleMapsIndependentlyAndPreserveCustomTextures(bool customAlbedo, bool customNormal)
        {
            const string directory = "Assets/[DeadEverywhere]/FloridaGen/Materials";
            string[] outputs = { $"{directory}/Florida_Asphalt_Aggregate_Albedo.asset",
                                 $"{directory}/Florida_Asphalt_Aggregate_Normal.asset" };
            bool[] existed = outputs.Select(path => UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null).ToArray();
            var material = new UnityEngine.Material(Shader.Find(FloridaTerrainGenerator.RoadShaderName));
            var artistAlbedo = new Texture2D(2, 2);
            var artistNormal = new Texture2D(2, 2);
            try
            {
                var albedo = customAlbedo ? artistAlbedo :
                    UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>($"{directory}/Florida_Asphalt_Subtle_Albedo.asset");
                var normal = customNormal ? artistNormal :
                    UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>($"{directory}/Florida_Asphalt_Subtle_Normal.asset");
                Assert.That(albedo, Is.Not.Null);
                Assert.That(normal, Is.Not.Null);
                material.SetTexture("_MainTex", albedo);
                material.SetTexture("_NormalMap", normal);
                material.SetFloat("_NormalScale", customNormal ? 0.4f : 0.15f);
                var scale = new Vector2(2f, 3f);
                var offset = new Vector2(0.2f, 0.3f);
                material.SetTextureScale("_MainTex", scale);
                material.SetTextureOffset("_MainTex", offset);
                material.SetTextureScale("_NormalMap", scale);
                material.SetTextureOffset("_NormalMap", offset);
                for (int pass = 0; pass < 2; pass++)
                {
                    FloridaTerrainGenerator.AddDefaultAsphaltTextures(material);
                    if (customAlbedo) Assert.That(material.GetTexture("_MainTex"), Is.SameAs(artistAlbedo));
                    else Assert.That(UnityEditor.AssetDatabase.GetAssetPath(material.GetTexture("_MainTex")), Is.EqualTo(outputs[0]));
                    if (customNormal) Assert.That(material.GetTexture("_NormalMap"), Is.SameAs(artistNormal));
                    else Assert.That(UnityEditor.AssetDatabase.GetAssetPath(material.GetTexture("_NormalMap")), Is.EqualTo(outputs[1]));
                    Assert.That(material.GetFloat("_NormalScale"), Is.EqualTo(customNormal ? 0.4f : 0.65f));
                    Assert.That(material.GetTextureScale("_MainTex"), Is.EqualTo(scale));
                    Assert.That(material.GetTextureOffset("_MainTex"), Is.EqualTo(offset));
                    Assert.That(material.GetTextureScale("_NormalMap"), Is.EqualTo(scale));
                    Assert.That(material.GetTextureOffset("_NormalMap"), Is.EqualTo(offset));
                    if (!customNormal) Assert.That(material.IsKeywordEnabled("N_F_NM_ON"), Is.True);
                }
                material.SetFloat("_NormalScale", 0.4f);
                FloridaTerrainGenerator.AddDefaultAsphaltTextures(material);
                Assert.That(material.GetFloat("_NormalScale"), Is.EqualTo(0.4f));
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(artistAlbedo);
                Object.DestroyImmediate(artistNormal);
                for (int i = 0; i < outputs.Length; i++)
                    if (!existed[i]) UnityEditor.AssetDatabase.DeleteAsset(outputs[i]);
            }
        }

        [Test]
        public void RealToonMigrationPreservesArtistMapsTintAndTilingAndEnablesItsNormalKeyword()
        {
            Shader source = Shader.Find("HDRP/Lit");
            Assert.That(source, Is.Not.Null);
            var material = new UnityEngine.Material(source);
            var albedo = new Texture2D(2, 2);
            var normal = new Texture2D(2, 2);
            try
            {
                var tint = new Color(0.23f, 0.25f, 0.27f);
                var tiling = new Vector2(2f, 3f);
                var offset = new Vector2(0.2f, 0.3f);
                material.SetColor("_BaseColor", tint);
                material.SetTexture("_BaseColorMap", albedo);
                material.SetTextureScale("_BaseColorMap", tiling);
                material.SetTextureOffset("_BaseColorMap", offset);
                material.SetTexture("_NormalMap", normal);
                material.SetTextureScale("_NormalMap", Vector2.one);
                material.SetTextureOffset("_NormalMap", Vector2.zero);
                material.SetFloat("_NormalScale", 0.4f);
                for (int pass = 0; pass < 2; pass++)
                {
                    FloridaTerrainGenerator.ConfigureRoadMaterial(material);
                    Assert.That(material.shader.name, Is.EqualTo(FloridaTerrainGenerator.RoadShaderName));
                    Assert.That(material.GetColor("_MainColor"), Is.EqualTo(tint));
                    Assert.That(material.GetTexture("_MainTex"), Is.SameAs(albedo));
                    Assert.That(material.GetTextureScale("_MainTex"), Is.EqualTo(tiling));
                    Assert.That(material.GetTextureOffset("_MainTex"), Is.EqualTo(offset));
                    Assert.That(material.GetTexture("_NormalMap"), Is.SameAs(normal));
                    Assert.That(material.GetTextureScale("_NormalMap"), Is.EqualTo(tiling));
                    Assert.That(material.GetTextureOffset("_NormalMap"), Is.EqualTo(offset));
                    Assert.That(material.GetFloat("_NormalScale"), Is.EqualTo(0.4f));
                    Assert.That(material.GetFloat("_N_F_NM"), Is.EqualTo(1f));
                    Assert.That(material.IsKeywordEnabled("N_F_NM_ON"), Is.True);
                    Assert.That(material.GetFloat("_N_F_O"), Is.Zero);
                    Assert.That(material.IsKeywordEnabled("N_F_O_ON"), Is.False);
                    Assert.That(material.GetShaderPassEnabled("Outline"), Is.False);
                    Assert.That(material.GetFloat("_MaiColPo"), Is.EqualTo(1f));
                    Assert.That(material.GetFloat("_N_F_LLI"), Is.EqualTo(1f));
                    Assert.That(material.IsKeywordEnabled("N_F_LLI_ON"), Is.True);
                    Assert.That(material.GetFloat("_LLI_Min"), Is.Zero, "Unlit roads must not be forced to glow.");
                    Assert.That(material.GetFloat("_LLI_Max"), Is.EqualTo(1f));
                }
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(albedo);
                Object.DestroyImmediate(normal);
            }
        }

        [TestCase("Motorway")]
        [TestCase("Structure")]
        [TestCase("WhitePaint")]
        [TestCase("YellowPaint")]
        public void ExistingGeneratedMaterialsReceiveMatteDaylightSafeSettingsWithoutLosingTint(string category)
        {
            string path = $"Assets/[DeadEverywhere]/FloridaGen/Materials/HighwayMat_{category}.mat";
            var saved = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path);
            Assert.That(saved, Is.Not.Null);
            var material = new UnityEngine.Material(saved);
            try
            {
                var tint = material.GetColor("_MainColor");
                var albedo = material.GetTexture("_MainTex");
                material.SetFloat("_N_F_LLI", 0f);
                material.DisableKeyword("N_F_LLI_ON");
                material.SetFloat("_GlossIntensity", 1f);
                material.SetFloat("_N_F_GLO", 1f);
                material.SetFloat("_EnvironmentalLightingIntensity", 1f);
                FloridaTerrainGenerator.ConfigureRoadMaterial(material);
                Assert.That(material.GetColor("_MainColor"), Is.EqualTo(tint));
                Assert.That(material.GetTexture("_MainTex"), Is.SameAs(albedo));
                Assert.That(material.GetFloat("_N_F_LLI"), Is.EqualTo(1f));
                Assert.That(material.IsKeywordEnabled("N_F_LLI_ON"), Is.True);
                Assert.That(material.GetFloat("_LLI_Max"), Is.EqualTo(1f));
                Assert.That(material.GetFloat("_MCIALO"), Is.Zero);
                Assert.That(material.GetFloat("_GlossIntensity"), Is.Zero);
                Assert.That(material.IsKeywordEnabled("N_F_GLO_ON"), Is.False);
                Assert.That(material.IsKeywordEnabled("N_F_R_ON"), Is.False);
                Assert.That(material.IsKeywordEnabled("N_F_RL_ON"), Is.False);
                Assert.That(material.IsKeywordEnabled("N_F_SL_ON"), Is.False);
                Assert.That(material.GetFloat("_EnvironmentalLightingIntensity"), Is.EqualTo(0.25f));
                if (category == "Motorway")
                    Assert.That(tint.maxColorComponent, Is.LessThan(0.5f));
                if (category == "YellowPaint")
                    Assert.That(tint.r, Is.GreaterThan(tint.b * 5f));
            }
            finally { Object.DestroyImmediate(material); }
        }

        [TestCase(0f)]
        [TestCase(1f)]
        [TestCase(100f)]
        public void WidthAdjustmentChangesPlanAndMeshTogether(float adjustment)
        {
            foreach (RoadKind kind in new[] { RoadKind.Ground, RoadKind.Bridge, RoadKind.Tunnel })
            foreach (var road in new[]
            {
                Road("residential"), Road("primary", width: 12f), Road("motorway", lanes: 3)
            })
            {
                road.isBridge = kind == RoadKind.Bridge;
                road.isTunnel = kind == RoadKind.Tunnel;
                var plan = FloridaRoadTranslator.Translate(new[] { road }, null, new RoadTranslationSettings
                {
                    clipMin = Vector2.zero,
                    clipMax = new Vector2(128f, 128f),
                    widthAdjustment = adjustment,
                    groundHeight = (x, z) => 0f
                });
                float expected = FloridaRoadStandards.HalfWidth(road) * (1f + adjustment * 0.0001f);
                Assert.That(plan.roads.Count, Is.EqualTo(1));
                Assert.That(plan.roads[0].halfWidth, Is.EqualTo(expected).Within(0.000001f));

                var meshes = new RoadMeshSet();
                FloridaRoadMeshBuilder.Build(plan, meshes, Vector3.zero, (x, z) => 0f);
                var vertices = meshes.surface[plan.roads[0].category].verts;
                float width = vertices.Max(v => v.z) - vertices.Min(v => v.z);
                Assert.That(width, Is.EqualTo(2f * expected).Within(0.00002f));
            }
        }

        [Test]
        public void ChunkJsonRetainsWidthAndAllBuildingHeightMetadata()
        {
            var data = new FloridaChunkData
            {
                roadWidthAdjustment = 12.5f,
                buildings = new[]
                {
                    new FloridaTerrainGenerator.OsmBuilding
                    {
                        height = 18.25f, levels = 5, minHeight = 3f, minLevel = 1,
                        roofHeight = 2.25f, roofLevels = 1
                    }
                }
            };
            var loaded = FloridaChunkFile.FromJson(FloridaChunkFile.ToJson(data));
            Assert.That(loaded.roadWidthAdjustment, Is.EqualTo(12.5f));
            Assert.That(loaded.buildings[0].height, Is.EqualTo(18.25f));
            Assert.That(loaded.buildings[0].levels, Is.EqualTo(5));
            Assert.That(loaded.buildings[0].minHeight, Is.EqualTo(3f));
            Assert.That(loaded.buildings[0].minLevel, Is.EqualTo(1));
            Assert.That(loaded.buildings[0].roofHeight, Is.EqualTo(2.25f));
            Assert.That(loaded.buildings[0].roofLevels, Is.EqualTo(1));
            Assert.That(FloridaChunkFile.FromJson("{\"version\":1}").roadWidthAdjustment, Is.Zero);
        }

        [Test]
        public void BuildingTextAppendsHeightAndDirectionUsingInvariantCulture()
        {
            var go = new GameObject("Building export test");
            string path = Path.GetTempFileName();
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                gen.osmBuildings = new[]
                {
                    new FloridaTerrainGenerator.OsmBuilding
                    {
                        footprint = new[] { Vector2.zero, new Vector2(4f, 0f), new Vector2(4f, 4f) },
                        type = "house", levels = 4, height = 12.345f
                    },
                    new FloridaTerrainGenerator.OsmBuilding
                    {
                        footprint = new[] { Vector2.zero, new Vector2(2f, 0f), new Vector2(2f, 2f) },
                        levels = 2
                    }
                };
                gen.osmRoads = new[]
                {
                    new FloridaTerrainGenerator.OsmRoad
                    {
                        points = new[] { new Vector2(10f, -100f), new Vector2(10f, 100f) }
                    }
                };
                Assert.That(gen.SaveBuildingData(path), Is.EqualTo(2));
                string[] lines = File.ReadAllLines(path);
                Assert.That(lines[0], Does.Contain("|height|nearestRoadDirection|nearestRoadBearing"));
                string[] first = lines[1].Split('|');
                Assert.That(first.Length, Is.EqualTo(11));
                Assert.That(first[1], Is.EqualTo("house"));
                Assert.That(first[2], Is.EqualTo("4"));
                Assert.That(first[7], Is.EqualTo("0.00,0.00;4.00,0.00;4.00,4.00"));
                Assert.That(first[8], Is.EqualTo("12.345"));
                Assert.That(first[9], Is.EqualTo("E"));
                Assert.That(first[10], Is.EqualTo("90.00"));
                Assert.That(lines[2].Split('|')[8], Is.EqualTo("0"));

                gen.osmRoads = null;
                Assert.That(gen.SaveBuildingData(path), Is.EqualTo(2));
                Assert.That(File.ReadAllLines(path)[1], Does.EndWith("|12.345|unknown|-1.00"));
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                File.Delete(path);
                Object.DestroyImmediate(go);
            }
        }

        [TestCase(0f, 10f, "N", 0f)]
        [TestCase(10f, 10f, "NE", 45f)]
        [TestCase(10f, 0f, "E", 90f)]
        [TestCase(10f, -10f, "SE", 135f)]
        [TestCase(0f, -10f, "S", 180f)]
        [TestCase(-10f, -10f, "SW", 225f)]
        [TestCase(-10f, 0f, "W", 270f)]
        [TestCase(-10f, 10f, "NW", 315f)]
        public void DirectionUsesWorldSpaceBearingClockwiseFromNorth(float dx, float dz, string direction, float bearing)
        {
            var go = new GameObject("Road direction test");
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                gen.osmBuildings = new[] { BuildingAt(5f, 5f) };
                var target = new Vector2(5f + dx, 5f + dz);
                gen.osmRoads = new[]
                {
                    new FloridaTerrainGenerator.OsmRoad
                    {
                        points = new[] { target + new Vector2(-dz, dx), target + new Vector2(dz, -dx) }
                    }
                };
                gen.UpdateBuildingRoadDirections();
                Assert.That(gen.osmBuildings[0].nearestRoadDirection, Is.EqualTo(direction));
                Assert.That(gen.osmBuildings[0].nearestRoadBearing, Is.EqualTo(bearing).Within(0.001f));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ClosestSegmentInteriorWinsAndEqualDistancesKeepTheFirstRoad()
        {
            var go = new GameObject("Road segment test");
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                gen.osmBuildings = new[] { BuildingAt(5f, 5f), BuildingAt(50f, 40f) };
                gen.osmRoads = new[]
                {
                    new FloridaTerrainGenerator.OsmRoad { points = new[] { new Vector2(5f, 5f), new Vector2(5f, 5f) } },
                    new FloridaTerrainGenerator.OsmRoad { points = new[] { new Vector2(20f, -100f), new Vector2(20f, 100f) } },
                    new FloridaTerrainGenerator.OsmRoad { points = new[] { new Vector2(-100f, 20f), new Vector2(100f, 20f) } }
                };
                gen.UpdateBuildingRoadDirections();
                Assert.That(gen.osmBuildings[0].nearestRoadDirection, Is.EqualTo("E"));
                Assert.That(gen.osmBuildings[0].nearestRoadBearing, Is.EqualTo(90f));
                Assert.That(gen.osmBuildings[1].nearestRoadDirection, Is.EqualTo("S"));
                Assert.That(gen.osmBuildings[1].nearestRoadBearing, Is.EqualTo(180f));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MissingOrCoincidentRoadGeometryProducesUnknownDirection()
        {
            var go = new GameObject("Unknown road direction test");
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                var building = BuildingAt(5f, 5f);
                gen.osmBuildings = new[] { building };
                gen.osmRoads = new[] { new FloridaTerrainGenerator.OsmRoad { points = new[] { new Vector2(5f, 5f), new Vector2(5f, 5f) } } };
                gen.UpdateBuildingRoadDirections();
                Assert.That(building.nearestRoadDirection, Is.EqualTo("unknown"));
                Assert.That(building.nearestRoadBearing, Is.EqualTo(-1f));

                gen.osmRoads[0].points = new[] { new Vector2(5.0005f, 0f), new Vector2(5.0005f, 10f) };
                gen.UpdateBuildingRoadDirections();
                Assert.That(building.nearestRoadDirection, Is.EqualTo("unknown"));
                Assert.That(building.nearestRoadBearing, Is.EqualTo(-1f));

                gen.osmRoads[0].points = new[] { new Vector2(10f, 0f), new Vector2(10f, 10f) };
                gen.UpdateBuildingRoadDirections();
                Assert.That(building.nearestRoadDirection, Is.EqualTo("E"));
                gen.osmRoads = null;
                gen.UpdateBuildingRoadDirections();
                Assert.That(building.nearestRoadDirection, Is.EqualTo("unknown"));
                Assert.That(building.nearestRoadBearing, Is.EqualTo(-1f));

                building.footprint = null;
                gen.osmRoads = new[] { new FloridaTerrainGenerator.OsmRoad { points = new[] { Vector2.zero, Vector2.one } } };
                gen.UpdateBuildingRoadDirections();
                Assert.That(building.nearestRoadDirection, Is.EqualTo("unknown"));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TextAndDirectionUseTheSameVertexAverageForClosedFootprints()
        {
            var go = new GameObject("Closed footprint test");
            string path = Path.GetTempFileName();
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                gen.osmBuildings = new[]
                {
                    new FloridaTerrainGenerator.OsmBuilding
                    {
                        footprint = new[] { Vector2.zero, new Vector2(10f, 0f), new Vector2(10f, 10f),
                            new Vector2(0f, 10f), Vector2.zero }
                    }
                };
                gen.osmRoads = new[] { new FloridaTerrainGenerator.OsmRoad
                {
                    points = new[] { new Vector2(20f, 10f), new Vector2(20f, 20f) }
                } };
                Assert.That(gen.SaveBuildingData(path), Is.EqualTo(1));
                var row = File.ReadAllLines(path)[1].Split('|');
                Assert.That(row[3], Is.EqualTo("4.00"));
                Assert.That(row[5], Is.EqualTo("4.00"));
                Assert.That(gen.osmBuildings[0].nearestRoadBearing,
                    Is.EqualTo(Mathf.Atan2(16f, 6f) * Mathf.Rad2Deg).Within(0.001f));
                Assert.That(row[10], Is.EqualTo(gen.osmBuildings[0].nearestRoadBearing.ToString("F2", CultureInfo.InvariantCulture)));
            }
            finally
            {
                File.Delete(path);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ParsedBuildingsHaveDirectionsEvenWhenRoadsFollowThemInOsmResponse()
        {
            var go = new GameObject("OSM direction test");
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                double lat = FloridaTerrainGenerator.ORIGIN_LAT, lon = FloridaTerrainGenerator.ORIGIN_LON;
                string json = "{\"elements\":[" +
                    "{\"type\":\"way\",\"tags\":{\"building\":\"house\"},\"geometry\":[" +
                    OsmPoint(lat, lon) + "," + OsmPoint(lat, lon + 0.00002) + "," +
                    OsmPoint(lat + 0.00002, lon + 0.00002) + "," + OsmPoint(lat + 0.00002, lon) + "]}," +
                    "{\"type\":\"way\",\"tags\":{\"highway\":\"residential\"},\"geometry\":[" +
                    OsmPoint(lat - 0.00001, lon + 0.0001) + "," + OsmPoint(lat + 0.00003, lon + 0.0001) + "]}]}";
                gen.ParseOsmJson(json);
                Assert.That(gen.osmRoads.Length, Is.EqualTo(1));
                Assert.That(gen.osmBuildings.Length, Is.EqualTo(1));
                Assert.That(gen.osmBuildings[0].nearestRoadDirection, Is.EqualTo("E"));
                Assert.That(gen.osmBuildings[0].nearestRoadBearing, Is.EqualTo(90f).Within(0.1f));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SavedChunkIncludesCalculatedRoadDirectionAndExistingBuildingHeight()
        {
            var go = new GameObject("Chunk direction save test");
            string path = Path.GetTempFileName();
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                var building = BuildingAt(5f, 5f);
                building.height = 12.345f;
                gen.osmBuildings = new[] { building };
                gen.osmRoads = new[] { new FloridaTerrainGenerator.OsmRoad
                {
                    points = new[] { new Vector2(20f, -100f), new Vector2(20f, 100f) }
                } };
                gen.SaveChunkFile(path);
                var data = FloridaChunkFile.FromJson(File.ReadAllText(path));
                Assert.That(data.version, Is.EqualTo(FloridaChunkData.CurrentVersion));
                Assert.That(data.buildings[0].height, Is.EqualTo(12.345f));
                Assert.That(data.buildings[0].nearestRoadDirection, Is.EqualTo("E"));
                Assert.That(data.buildings[0].nearestRoadBearing, Is.EqualTo(90f));
            }
            finally
            {
                File.Delete(path);
                Object.DestroyImmediate(go);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void LoadingChunksRecalculatesDirectionsFromSavedGeometry(int version)
        {
            var go = new GameObject("Chunk direction load test");
            string path = Path.GetTempFileName();
            try
            {
                var gen = go.AddComponent<FloridaTerrainGenerator>();
                string json = "{\"version\":" + version +
                    ",\"buildings\":[{\"height\":12.345,\"footprint\":[{\"x\":4,\"y\":4},{\"x\":6,\"y\":4},{\"x\":6,\"y\":6},{\"x\":4,\"y\":6}]" +
                    (version == 3 ? ",\"nearestRoadDirection\":\"W\",\"nearestRoadBearing\":270" : "") + "}]" +
                    ",\"roads\":[{\"points\":[{\"x\":20,\"y\":-100},{\"x\":20,\"y\":100}]}]}";
                File.WriteAllText(path, json);
                gen.LoadChunkFile(path, regenerateRoads: false);
                Assert.That(gen.osmBuildings[0].height, Is.EqualTo(12.345f));
                Assert.That(gen.osmBuildings[0].nearestRoadDirection, Is.EqualTo("E"));
                Assert.That(gen.osmBuildings[0].nearestRoadBearing, Is.EqualTo(90f));
            }
            finally
            {
                File.Delete(path);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void WorldRoadGenerationAttachesBridgesToEveryBatchAndSavesWidth()
        {
            var worldObject = new GameObject("Florida generation test");
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(128f, 500f, 128f) };
            var terrain = Terrain.CreateTerrainGameObject(data);
            try
            {
                terrain.transform.SetParent(worldObject.transform, false);
                var gen = terrain.AddComponent<FloridaTerrainGenerator>();
                var world = worldObject.AddComponent<FloridaWorldGenerator>();
                world.roadWidthAdjustment = 100f;
                gen.osmRoads = new[]
                {
                    Road("motorway", z: 10f), Road("primary", z: 30f),
                    Road("secondary", z: 50f), Road("residential", z: 70f),
                    Road("primary", z: 90f, bridge: true), Road("primary", z: 110f, tunnel: true)
                };
                Assert.That(terrain.GetComponent<TerrainEcsColliderAuthoring>(), Is.Not.Null);
                Assert.That(world.GenerateRoads(), Is.True);
                var colliders = terrain.GetComponentsInChildren<UnityEngine.MeshCollider>();
                Assert.That(colliders.Length, Is.EqualTo(6));
                foreach (var collider in colliders)
                    Assert.That(collider.GetComponent<RoadEcsColliderAuthoring>(), Is.Not.Null);
                var paint = terrain.GetComponentsInChildren<MeshRenderer>()
                    .Where(r => r.name.StartsWith("HWY_WhitePaint") || r.name.StartsWith("HWY_YellowPaint")).ToArray();
                Assert.That(paint.Length, Is.EqualTo(2));
                foreach (var renderer in paint)
                {
                    Assert.That(renderer.GetComponent<UnityEngine.Collider>(), Is.Null);
                    Assert.That(renderer.GetComponent<RoadEcsColliderAuthoring>(), Is.Null);
                    Assert.That(renderer.sharedMaterial, Is.Not.Null);
                    Assert.That(renderer.sharedMaterial.GetTexture("_MainTex"), Is.Not.Null);
                    Assert.That(renderer.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
                }
                foreach (var renderer in terrain.GetComponentsInChildren<MeshRenderer>())
                    Assert.That(renderer.sharedMaterial.shader.name, Is.EqualTo(FloridaTerrainGenerator.RoadShaderName));
                var asphalt = terrain.GetComponentsInChildren<MeshRenderer>()
                    .Single(r => r.name.StartsWith("HWY_Residential")).sharedMaterial;
                Assert.That(asphalt.GetTexture("_MainTex"), Is.Not.Null);
                Assert.That(asphalt.GetTexture("_NormalMap"), Is.Not.Null);
                Assert.That(asphalt.IsKeywordEnabled("N_F_NM_ON"), Is.True);
                var structure = terrain.GetComponentsInChildren<MeshRenderer>()
                    .Single(r => r.name.StartsWith("HWY_Structure")).sharedMaterial;
                Assert.That(structure.GetTexture("_MainTex"), Is.Not.Null);
                Assert.That(structure.GetTexture("_NormalMap"), Is.Not.Null);
                Assert.That(gen.roadWidthAdjustment, Is.EqualTo(100f));
                Assert.That(gen.CaptureChunkData().roadWidthAdjustment, Is.EqualTo(100f));

                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, FloridaChunkFile.ToJson(gen.CaptureChunkData()));
                    gen.roadWidthAdjustment = 0f;
                    gen.LoadChunkFile(path, regenerateRoads: false);
                    Assert.That(gen.roadWidthAdjustment, Is.EqualTo(100f));
                }
                finally { File.Delete(path); }
            }
            finally
            {
                foreach (var filter in terrain.GetComponentsInChildren<MeshFilter>())
                    Object.DestroyImmediate(filter.sharedMesh);
                Object.DestroyImmediate(worldObject);
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void SharedCollidersTrackWorldsTransformsReplacementAndCleanup()
        {
            var worlds = new EcsColliderWorlds();
            var first = Unity.Physics.SphereCollider.Create(new SphereGeometry { Radius = 1f });
            var replacement = Unity.Physics.SphereCollider.Create(new SphereGeometry { Radius = 2f });
            var client = new World("Florida client test", WorldFlags.GameClient);
            var editor = new World("Florida editor test", WorldFlags.Editor);
            World server = null;
            try
            {
                var transform = LocalTransform.Identity;
                worlds.Synchronize(first, transform, true, "Florida test collider");
                using var clientQuery = client.EntityManager.CreateEntityQuery(typeof(PhysicsCollider));
                using var editorQuery = editor.EntityManager.CreateEntityQuery(typeof(PhysicsCollider));
                Assert.That(clientQuery.CalculateEntityCount(), Is.EqualTo(1));
                Assert.That(editorQuery.CalculateEntityCount(), Is.Zero);

                server = new World("Florida late server test", WorldFlags.GameServer);
                worlds.Synchronize(first, transform, false, "Florida test collider");
                using (var query = server.EntityManager.CreateEntityQuery(typeof(PhysicsCollider)))
                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));

                Entity entity = clientQuery.GetSingletonEntity();
                transform.Position = new float3(10f, 2f, -5f);
                worlds.Synchronize(replacement, transform, true, "Florida test collider");
                Assert.That(client.EntityManager.GetComponentData<PhysicsCollider>(entity).Value, Is.EqualTo(replacement));
                Assert.That(client.EntityManager.GetComponentData<LocalTransform>(entity).Position, Is.EqualTo(transform.Position));
                Assert.That(client.EntityManager.GetSharedComponent<PhysicsWorldIndex>(entity).Value, Is.Zero);
                Assert.That(clientQuery.CalculateEntityCount(), Is.EqualTo(1));

                client.EntityManager.DestroyEntity(entity);
                worlds.Synchronize(replacement, transform, false, "Florida test collider");
                Assert.That(clientQuery.CalculateEntityCount(), Is.EqualTo(1));
                server.Dispose();
                worlds.Synchronize(replacement, transform, false, "Florida test collider");
                worlds.Clear();
                worlds.Clear();
                Assert.That(clientQuery.CalculateEntityCount(), Is.Zero);
            }
            finally
            {
                worlds.Clear();
                if (server != null && server.IsCreated) server.Dispose();
                client.Dispose();
                editor.Dispose();
                first.Dispose();
                replacement.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RuntimeBridgesRefreshGeometryAndRemoveDisabledColliders()
        {
            yield return new EnterPlayMode();
            var world = new World("Florida collider runtime test", WorldFlags.GameClient);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(128f, 500f, 128f) };
            var terrain = Terrain.CreateTerrainGameObject(data);
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, new Vector3(0f, 0f, 4f), new Vector3(4f, 0f, 0f) },
                triangles = new[] { 0, 1, 2 }
            };
            World lateWorld = null;
            try
            {
                terrain.transform.position = new Vector3(0f, -250f, 0f);
                var roads = new GameObject("Highway Meshes");
                roads.transform.SetParent(terrain.transform, false);
                var road = new GameObject("Legacy road without an ECS bridge");
                road.transform.SetParent(roads.transform, false);
                road.transform.position = new Vector3(10f, 3f, 10f);
                road.transform.localScale = new Vector3(2f, 1f, 3f);
                var meshCollider = road.AddComponent<UnityEngine.MeshCollider>();
                meshCollider.sharedMesh = mesh;
                var gen = terrain.AddComponent<FloridaTerrainGenerator>();
                gen.heightmapResolution = FloridaTerrainGenerator.HeightmapResolution.R33;
                var bridge = road.GetComponent<RoadEcsColliderAuthoring>();
                Assert.That(bridge, Is.Not.Null);
                using var query = world.EntityManager.CreateEntityQuery(typeof(PhysicsCollider));
                Assert.That(query.CalculateEntityCount(), Is.EqualTo(2));
                Entity terrainEntity = Entity.Null, roadEntity = Entity.Null;
                using (var entities = query.ToEntityArray(Allocator.Temp))
                {
                    foreach (var entity in entities)
                    {
                        var collider = world.EntityManager.GetComponentData<PhysicsCollider>(entity).Value;
                        if (collider.Value.Type == ColliderType.Terrain) terrainEntity = entity;
                        else roadEntity = entity;
                    }
                }
                Assert.That(terrainEntity, Is.Not.EqualTo(Entity.Null));
                Assert.That(roadEntity, Is.Not.EqualTo(Entity.Null));
                AssertRayHeight(world, roadEntity, 12f, 13f, 20f, -10f, 3f);
                gen.ClearTerrain();
                AssertRayHeight(world, terrainEntity, 64f, 64f, 100f, -10f, 0f);

                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++)
                    for (int x = 0; x < 33; x++)
                        heights[z, x] = 0.6f;
                data.SetHeights(0, 0, heights);
                terrain.GetComponent<TerrainEcsColliderAuthoring>().RebuildCollider();
                AssertRayHeight(world, terrainEntity, 64f, 64f, 100f, -10f, 50f);
                gen.ClearTerrain();
                AssertRayHeight(world, terrainEntity, 64f, 64f, 100f, -10f, 0f);

                lateWorld = new World("Florida late runtime world", WorldFlags.GameServer);
                yield return null;
                using var lateQuery = lateWorld.EntityManager.CreateEntityQuery(typeof(PhysicsCollider));
                Assert.That(lateQuery.CalculateEntityCount(), Is.EqualTo(2));
                road.SetActive(false);
                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
                Assert.That(lateQuery.CalculateEntityCount(), Is.EqualTo(1));
                road.SetActive(true);
                Assert.That(query.CalculateEntityCount(), Is.EqualTo(2));
                meshCollider.sharedMesh = null;
                yield return null;
                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
                Assert.That(lateQuery.CalculateEntityCount(), Is.EqualTo(1));
                terrain.SetActive(false);
                Assert.That(query.CalculateEntityCount(), Is.Zero);
                Assert.That(lateQuery.CalculateEntityCount(), Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(terrain);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(mesh);
                if (lateWorld != null && lateWorld.IsCreated) lateWorld.Dispose();
                world.Dispose();
            }
            yield return new ExitPlayMode();
        }

        private static void AssertRayHeight(World world, Entity entity, float x, float z, float top, float bottom, float expected)
        {
            var collider = world.EntityManager.GetComponentData<PhysicsCollider>(entity).Value;
            float3 position = world.EntityManager.GetComponentData<LocalTransform>(entity).Position;
            var ray = new RaycastInput
            {
                Start = new float3(x, top, z) - position,
                End = new float3(x, bottom, z) - position,
                Filter = CollisionFilter.Default
            };
            Assert.That(collider.Value.CastRay(ray, out var hit), Is.True);
            Assert.That(hit.Position.y + position.y, Is.EqualTo(expected).Within(0.01f));
        }

        private static RoadPlan PlanRoads(params FloridaTerrainGenerator.OsmRoad[] roads) =>
            FloridaRoadTranslator.Translate(roads, null, new RoadTranslationSettings
            {
                clipMin = Vector2.zero, clipMax = new Vector2(128f, 128f), groundHeight = (x, z) => 0f
            });

        private static RoadMeshSet BuildRoads(RoadPlan plan, Vector3 origin = default)
        {
            var meshes = new RoadMeshSet();
            FloridaRoadMeshBuilder.Build(plan, meshes, origin, (x, z) => 0f);
            return meshes;
        }

        private static bool PaintCoversCentre(MeshBatch paint, float x, Vector3 origin = default)
            => TryMeshHeight(paint, new Vector2(x - origin.x, 16f - origin.z), out _);

        private static bool TryMeshHeight(MeshBatch mesh, Vector2 point, out float height)
        {
            for (int i = 0; i < mesh.tris.Count; i += 3)
            {
                Vector3 a = mesh.verts[mesh.tris[i]], b = mesh.verts[mesh.tris[i + 1]], c = mesh.verts[mesh.tris[i + 2]];
                Vector2 ab = new Vector2(b.x - a.x, b.z - a.z), ac = new Vector2(c.x - a.x, c.z - a.z);
                Vector2 ap = point - new Vector2(a.x, a.z);
                float area = ab.x * ac.y - ab.y * ac.x;
                if (Mathf.Abs(area) < 1e-10f) continue;
                float u = (ap.x * ac.y - ap.y * ac.x) / area;
                float v = (ab.x * ap.y - ab.y * ap.x) / area;
                if (u < -0.00001f || v < -0.00001f || u + v > 1.00001f) continue;
                height = a.y + u * (b.y - a.y) + v * (c.y - a.y);
                return true;
            }
            height = 0f;
            return false;
        }

        private static FloridaTerrainGenerator.OsmRoad Road(
            string highway, float width = 0f, int lanes = 0, float z = 16f, bool bridge = false, bool tunnel = false)
        {
            return new FloridaTerrainGenerator.OsmRoad
            {
                highway = highway, width = width, lanes = lanes, isBridge = bridge, isTunnel = tunnel,
                points = new[] { new Vector2(10f, z), new Vector2(110f, z) }
            };
        }

        private static FloridaTerrainGenerator.OsmBuilding BuildingAt(float x, float z)
        {
            return new FloridaTerrainGenerator.OsmBuilding
            {
                footprint = new[]
                {
                    new Vector2(x - 1f, z - 1f), new Vector2(x + 1f, z - 1f),
                    new Vector2(x + 1f, z + 1f), new Vector2(x - 1f, z + 1f)
                }
            };
        }

        private static string OsmPoint(double lat, double lon) =>
            string.Format(CultureInfo.InvariantCulture, "{{\"lat\":{0:R},\"lon\":{1:R}}}", lat, lon);
    }
}
