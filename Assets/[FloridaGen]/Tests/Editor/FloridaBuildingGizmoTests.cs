using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeadEverywhere.FloridaGeneration.Tests
{
    public class FloridaBuildingGizmoTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private TerrainData data;
        private GameObject terrainObject;
        private FloridaTerrainGenerator gen;

        [SetUp]
        public void SetUp()
        {
            data = new TerrainData { heightmapResolution = 33, size = new Vector3(128f, 500f, 128f) };
            terrainObject = Terrain.CreateTerrainGameObject(data);
            terrainObject.transform.position = new Vector3(400f, -250f, 800f);
            gen = terrainObject.AddComponent<FloridaTerrainGenerator>();
            gen.osmBuildings = new[]
            {
                new FloridaTerrainGenerator.OsmBuilding
                {
                    height = 9.9f,
                    footprint = new[]
                    {
                        new Vector2(416f, 816f), new Vector2(432f, 816f),
                        new Vector2(432f, 832f), new Vector2(416f, 832f)
                    }
                }
            };
            SetGround(7f);
        }

        [TearDown]
        public void TearDown()
        {
            FloridaTerrainGeneratorEditor.InvalidateCache(gen);
            FloridaRoadGizmos.Invalidate(gen);
            Object.DestroyImmediate(terrainObject);
            Object.DestroyImmediate(data);
        }

        [Test]
        public void TaggedHeightIsAboveGroundNotAboveSeaLevelAndIdentifiesItsSource()
        {
            var group = HeightGroup();
            var points = HeightPoints(group);
            float ground = gen.TerrainSurfaceY(points[0].x, points[0].z);
            Assert.That(ground, Is.EqualTo(7f).Within(0.02f));
            Assert.That(points[0].y, Is.EqualTo(ground).Within(0.001f));
            foreach (var point in points.Skip(1))
                Assert.That(point.y, Is.EqualTo(ground + 9.9f).Within(0.001f));
            object label = ((IEnumerable)Field(group, "labels")).Cast<object>().Single();
            Assert.That(Field(label, "text"),
                Does.Contain(9.9f.ToString("F1", CultureInfo.CurrentCulture) + " m (OSM)"));
            Assert.That(gen.osmBuildings[0].height, Is.EqualTo(9.9f));
        }

        [TestCase(3f, 0, 3f)]
        [TestCase(0f, 1, 3.2f)]
        [TestCase(12f, 0, 0f)]
        public void RaisedBuildingPartsKeepTheirIntentionalGroundClearance(float minHeight, int minLevel, float bottom)
        {
            gen.osmBuildings[0].minHeight = minHeight;
            gen.osmBuildings[0].minLevel = minLevel;
            var points = HeightPoints(HeightGroup());
            float ground = gen.TerrainSurfaceY(points[0].x, points[0].z);
            Assert.That(points[0].y, Is.EqualTo(ground + bottom).Within(0.001f));
            Assert.That(points[1].y - ground, Is.EqualTo(9.9f).Within(0.001f));
        }

        [Test]
        public void LevelOnlyBuildingsRetainEstimatedHeights()
        {
            gen.osmBuildings[0].height = 0f;
            gen.osmBuildings[0].levels = 2;
            var group = HeightGroup();
            var points = HeightPoints(group);
            Assert.That(points[1].y - points[0].y, Is.EqualTo(6.4f).Within(0.001f));
            object label = ((IEnumerable)Field(group, "labels")).Cast<object>().Single();
            Assert.That(Field(label, "text"), Does.Contain("(2 levels)"));
        }

        [Test]
        public void FootprintsFollowTheTerrainAtEachVertex()
        {
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                    heights[z, x] = 0.5f + x * 0.001f + z * 0.002f;
            data.SetHeights(0, 0, heights);
            var points = FootprintPoints(LineCache());
            Assert.That(points.Length, Is.EqualTo(8));
            Assert.That(points.Max(p => p.y) - points.Min(p => p.y), Is.GreaterThan(5f));
            foreach (var point in points)
                Assert.That(point.y, Is.EqualTo(gen.TerrainSurfaceY(point.x, point.z)).Within(0.001f));
        }

        [Test]
        public void HeightmapEditsRebuildBothCachedBuildingOverlays()
        {
            object heights = HeightGroup(), lines = LineCache();
            Assert.That(HeightGroup(), Is.SameAs(heights));
            Assert.That(LineCache(), Is.SameAs(lines));

            SetGround(-2f);

            object rebuiltHeights = HeightGroup(), rebuiltLines = LineCache();
            Assert.That(rebuiltHeights, Is.Not.SameAs(heights));
            Assert.That(rebuiltLines, Is.Not.SameAs(lines));
            Assert.That(HeightPoints(rebuiltHeights)[0].y, Is.EqualTo(-2f).Within(0.02f));
            foreach (var point in FootprintPoints(rebuiltLines))
                Assert.That(point.y, Is.EqualTo(-2f).Within(0.02f));
        }

        [TestCase("position")]
        [TestCase("size")]
        [TestCase("data")]
        public void TerrainChangesInvalidateBothCachesWithoutMovingWorldSpaceFootprints(string change)
        {
            object heights = HeightGroup(), lines = LineCache();
            TerrainData replacement = null;
            try
            {
                if (change == "position")
                    terrainObject.transform.position += new Vector3(4f, 0f, 4f);
                else if (change == "size")
                    data.size = new Vector3(256f, 400f, 256f);
                else
                {
                    replacement = new TerrainData { heightmapResolution = 33, size = data.size };
                    terrainObject.GetComponent<Terrain>().terrainData = replacement;
                }

                object rebuiltHeights = HeightGroup(), rebuiltLines = LineCache();
                Assert.That(rebuiltHeights, Is.Not.SameAs(heights));
                Assert.That(rebuiltLines, Is.Not.SameAs(lines));
                var marker = HeightPoints(rebuiltHeights)[0];
                Assert.That(marker.x, Is.EqualTo(424f));
                Assert.That(marker.z, Is.EqualTo(824f));
                Assert.That(marker.y, Is.EqualTo(gen.TerrainSurfaceY(marker.x, marker.z)).Within(0.001f));
                foreach (var point in FootprintPoints(rebuiltLines))
                    Assert.That(point.y, Is.EqualTo(gen.TerrainSurfaceY(point.x, point.z)).Within(0.001f));
            }
            finally
            {
                terrainObject.GetComponent<Terrain>().terrainData = data;
                if (replacement != null) Object.DestroyImmediate(replacement);
            }
        }

        [Test]
        public void ReplacingBuildingsWithTheSameCountRebuildsBothCaches()
        {
            object heights = HeightGroup(), lines = LineCache();
            var footprint = gen.osmBuildings[0].footprint.Select(p => p + new Vector2(8f, 0f)).ToArray();
            gen.osmBuildings = new[]
            {
                new FloridaTerrainGenerator.OsmBuilding { footprint = footprint, height = 4f }
            };
            object rebuiltHeights = HeightGroup(), rebuiltLines = LineCache();
            Assert.That(rebuiltHeights, Is.Not.SameAs(heights));
            Assert.That(rebuiltLines, Is.Not.SameAs(lines));
            var points = HeightPoints(rebuiltHeights);
            Assert.That(points[1].y - points[0].y, Is.EqualTo(4f).Within(0.001f));
            Assert.That(FootprintPoints(rebuiltLines)[0].x, Is.EqualTo(footprint[0].x));
        }

        private void SetGround(float elevation)
        {
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                    heights[z, x] = (elevation - terrainObject.transform.position.y) / data.size.y;
            data.SetHeights(0, 0, heights);
        }

        private object HeightGroup()
        {
            Type type = typeof(FloridaRoadGizmos);
            object entries = type.GetField("s_Entries", PrivateStatic).GetValue(null);
            object entry = entries.GetType().GetMethod("GetOrCreateValue").Invoke(entries, new object[] { gen });
            FieldInfo field = entry.GetType().GetField("buildingHeights");
            MethodInfo build = type.GetMethod("BuildBuildingHeights", PrivateStatic);
            var factory = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(build.ReturnType), gen, build);
            object[] args = { field.GetValue(entry), gen.osmBuildings, gen, factory, null };
            object group = type.GetMethod("Fetch", PrivateStatic).Invoke(null, args);
            field.SetValue(entry, group);
            return group;
        }

        private object LineCache() => typeof(FloridaTerrainGeneratorEditor)
            .GetMethod("GetOrBuildCache", PrivateStatic)
            .Invoke(null, new object[] { gen, 0.5f });

        private static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
        private static Vector3[] FootprintPoints(object cache) => (Vector3[])Field(cache, "buildingLines");
        private static Vector3[] HeightPoints(object group) => ((IEnumerable)Field(group, "lines")).Cast<object>()
            .SelectMany(line => (Vector3[])Field(line, "points")).ToArray();
    }
}
