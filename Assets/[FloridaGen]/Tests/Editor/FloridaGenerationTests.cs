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

namespace DeadEverywhere.FloridaGeneration.Tests
{
    public class FloridaGenerationTests
    {
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
                Assert.That(data.version, Is.EqualTo(3));
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
