using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace DeadEverywhere.FloridaGeneration.Tests
{
    public class FloridaElevationTests
    {
        private const string GridHeader =
            "ncols 2\nnrows 2\nxllcorner 0\nyllcorner 0\ncellsize 1\nNODATA_value -9999\n";

        [Test]
        public void SamplingRequestUsesGeographicCoordinatesAndInvariantCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var points = new List<(int row, int col, double lat, double lon)>
                {
                    (0, 0, 27.95, -82.46), (0, 1, 28.5383, -81.3792)
                };
                string body = FloridaTerrainGenerator.BuildGetSamplesPostBody(points, 0, 2);
                string geometry = Uri.UnescapeDataString(body.Split('&')[0].Substring("geometry=".Length));
                Assert.That(geometry, Does.Contain("[[-82.4600000,27.9500000],[-81.3792000,28.5383000]]"));
                Assert.That(geometry, Does.Contain("\"spatialReference\":{\"wkid\":4326}"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void CompletelyMissingHighResolutionSamplesAreRejected()
        {
            const int res = 257;
            var points = new List<(int row, int col, double lat, double lon)>(res * res);
            for (int row = 0; row < res; row++)
                for (int col = 0; col < res; col++)
                    points.Add((row, col, 27.95, -82.46));
            var error = Assert.Throws<FormatException>(() =>
                FloridaTerrainGenerator.ParseGetSamplesJson("{\"samples\":[]}", points, 0, points.Count, new float[res, res]));
            Assert.That(error.Message, Does.Contain("0 of 66049"));
        }

        [TestCase("{\"error\":{\"code\":400,\"message\":\"Invalid geometry\"}}")]
        [TestCase("{\"samples\":[{\"locationId\":0,\"value\":\"NoData\"}]}")]
        [TestCase("{\"samples\":[{\"locationId\":0,\"value\":\"NaN\"}]}")]
        [TestCase("{\"samples\":[{\"locationId\":0,\"value\":\"Infinity\"}]}")]
        [TestCase("{\"samples\":[{\"locationId\":0,\"value\":\"-999999\"}]}")]
        [TestCase("{\"samples\":[{\"locationId\":0,\"value\":\"12.5\"}]}")]
        [TestCase("")]
        public void MissingOrPartialSampleResponsesCannotBecomeSuccessfulGrids(string json)
        {
            var points = new List<(int row, int col, double lat, double lon)>
            {
                (0, 0, 27.95, -82.46), (0, 1, 27.95, -82.45)
            };
            Assert.Throws<FormatException>(() =>
                FloridaTerrainGenerator.ParseGetSamplesJson(json, points, 0, points.Count, new float[2, 2]));
        }

        [Test]
        public void OverlappingRasterSamplesKeepBatchLocationsAndRealNegativeHeights()
        {
            var points = new List<(int row, int col, double lat, double lon)>
            {
                (0, 0, 27.95, -82.46), (0, 1, 27.95, -82.45),
                (1, 0, 27.96, -82.46), (1, 1, 27.96, -82.45)
            };
            var result = new float[2, 2];
            int received = FloridaTerrainGenerator.ParseGetSamplesJson(
                "{\"samples\":[{\"locationId\":1,\"value\":\"NoData\"}," +
                "{\"locationId\":0,\"value\":\"3.25\"},{\"locationId\":1,\"value\":\"-2.5\"}," +
                "{\"locationId\":0,\"value\":\"999\"}]}", points, 2, 4, result);
            Assert.That(received, Is.EqualTo(2));
            Assert.That(result[1, 0], Is.EqualTo(3.25f));
            Assert.That(result[1, 1], Is.EqualTo(-2.5f));
        }

        [Test]
        public void ExplicitZeroSampleValuesAreStillValidSeaLevel()
        {
            var points = new List<(int row, int col, double lat, double lon)>
            {
                (0, 0, 27.95, -82.46), (0, 1, 27.95, -82.45)
            };
            var result = new float[2, 2];
            Assert.That(FloridaTerrainGenerator.ParseGetSamplesJson(
                "{\"samples\":[{\"value\":\"0\"},{\"value\":0}]}", points, 0, 2, result), Is.EqualTo(2));
            Assert.That(result[0, 0], Is.Zero);
            Assert.That(result[0, 1], Is.Zero);
        }

        [Test]
        public void AsciiGridPreservesReliefAndFlipsNorthToSouth()
        {
            float[,] grid = FloridaTerrainGenerator.ParseAAIGrid(GridHeader + "12.5 20\n-2.5 0", 3);
            Assert.That(grid[0, 0], Is.EqualTo(-2.5f));
            Assert.That(grid[0, 2], Is.Zero);
            Assert.That(grid[2, 0], Is.EqualTo(12.5f));
            Assert.That(grid[2, 2], Is.EqualTo(20f));
            Assert.That(grid[1, 1], Is.EqualTo(7.5f));
        }

        [TestCase("-9999 20\n-2.5 0")]
        [TestCase("NaN 20\n-2.5 0")]
        [TestCase("Infinity 20\n-2.5 0")]
        [TestCase("12.5 20\n-2.5 invalid")]
        [TestCase("12.5 20\n-2.5")]
        [TestCase("12.5 20")]
        public void AsciiGridRejectsMissingInvalidAndTruncatedData(string values)
        {
            Assert.Throws<FormatException>(() => FloridaTerrainGenerator.ParseAAIGrid(GridHeader + values, 3));
        }

        [TestCase("{\"value\":\"NoData\"}")]
        [TestCase("{\"value\":-1000000}")]
        [TestCase("{\"value\":\"NaN\"}")]
        [TestCase("{\"value\":\"Infinity\"}")]
        [TestCase("{\"error\":{\"message\":\"Unavailable\"}}")]
        [TestCase("")]
        public void PointQueryRejectsMissingElevationsInsteadOfReturningZero(string json)
        {
            Assert.Throws<FormatException>(() => FloridaTerrainGenerator.ParseEPQSValue(json));
        }

        [Test]
        public void PointQueryPreservesNumericStringAndBelowSeaLevelValues()
        {
            Assert.That(FloridaTerrainGenerator.ParseEPQSValue("{\"value\":12.75}"), Is.EqualTo(12.75f));
            Assert.That(FloridaTerrainGenerator.ParseEPQSValue("{\"value\":\"-2.5\"}"), Is.EqualTo(-2.5f));
            Assert.That(FloridaTerrainGenerator.ParseEPQSValue("{\"value\":\"0\"}"), Is.Zero);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ElevationSourcesApplyRealReliefAndFallBackWhenNeeded(int validSource)
        {
            var go = CreateGenerator(out var data, out var gen);
            try
            {
                gen.samples = new float[33, 33];
                gen.samples[0, 0] = float.NaN;
                var elevations = new float[33, 33];
                for (int row = 0; row < 33; row++)
                    for (int col = 0; col < 33; col++)
                        elevations[row, col] = row;
                if (validSource == 1) gen.samples = elevations;
                else if (validSource == 2) gen.wcs = elevations;
                else gen.epqs = elevations;
                gen.GenerateChunk();
                Assert.That(gen.sampleCalls, Is.EqualTo(1));
                Assert.That(gen.wcsCalls, Is.EqualTo(validSource > 1 ? 1 : 0));
                Assert.That(gen.epqsCalls, Is.EqualTo(validSource == 3 ? 1 : 0));
                Assert.That(gen.statusMessage, Does.Contain("\u2713"));
                float[,] heights = data.GetHeights(0, 0, 33, 33);
                Assert.That(heights[32, 0] - heights[0, 0], Is.EqualTo(32f / 500f).Within(0.0001f));
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(data); }
        }

        [Test]
        public void EverySourceFailingLeavesTheExistingTerrainAndPositionUntouched()
        {
            var go = CreateGenerator(out var data, out var gen);
            try
            {
                var original = new float[33, 33];
                for (int row = 0; row < 33; row++)
                    for (int col = 0; col < 33; col++)
                        original[row, col] = 0.5f + row * 0.001f;
                data.SetHeights(0, 0, original);
                original = data.GetHeights(0, 0, 33, 33);
                go.transform.position = new Vector3(123f, -250f, 456f);
                Vector3 size = data.size;
                LogAssert.Expect(LogType.Error, new Regex("^\\[FloridaTerrainGenerator\\] All elevation sources failed\\."));
                gen.GenerateChunk();
                Assert.That(gen.sampleCalls, Is.EqualTo(1));
                Assert.That(gen.wcsCalls, Is.EqualTo(1));
                Assert.That(gen.epqsCalls, Is.EqualTo(1));
                Assert.That(gen.statusMessage, Does.Contain("Download failed"));
                Assert.That(gen.statusMessage, Does.Contain("No heightmap was applied"));
                Assert.That(data.heightmapResolution, Is.EqualTo(33));
                Assert.That(data.size, Is.EqualTo(size));
                Assert.That(go.transform.position, Is.EqualTo(new Vector3(123f, -250f, 456f)));
                float[,] current = data.GetHeights(0, 0, 33, 33);
                for (int row = 0; row < 33; row++)
                    for (int col = 0; col < 33; col++)
                        Assert.That(current[row, col], Is.EqualTo(original[row, col]));
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(data); }
        }

        private static GameObject CreateGenerator(out TerrainData data, out TestElevationGenerator gen)
        {
            data = new TerrainData { heightmapResolution = 33, size = new Vector3(128f, 500f, 128f) };
            var go = Terrain.CreateTerrainGameObject(data);
            gen = go.AddComponent<TestElevationGenerator>();
            gen.heightmapResolution = FloridaTerrainGenerator.HeightmapResolution.R33;
            gen.autoSaveDataFiles = false;
            return go;
        }
    }

    public class TestElevationGenerator : FloridaTerrainGenerator
    {
        [NonSerialized] public float[,] samples, wcs, epqs;
        public int sampleCalls, wcsCalls, epqsCalls;

        protected override float[,] FetchViaGetSamples(GeoBounds bounds, int res) { sampleCalls++; return samples; }
        protected override float[,] FetchViaWCS(GeoBounds bounds, int res) { wcsCalls++; return wcs; }
        protected override float[,] FetchViaEPQS(GeoBounds bounds, int res) { epqsCalls++; return epqs; }
    }
}
