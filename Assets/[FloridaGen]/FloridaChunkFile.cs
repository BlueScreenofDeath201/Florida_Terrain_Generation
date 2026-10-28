using System;
using UnityEngine;

namespace DeadEverywhere.FloridaGeneration
{
    /// <summary>
    /// Everything needed to rebuild one terrain chunk without the network: the heightmap
    /// (as elevations in metres) and every OpenStreetMap feature the generator fetched.
    /// Written by "Save All" and read by "Load All" in the Florida Terrain Generator inspector.
    /// Plain <see cref="JsonUtility"/> data, so the file is ordinary JSON.
    /// </summary>
    [Serializable]
    public class FloridaChunkData
    {
        public const int CurrentVersion = 3;

        public int    version = CurrentVersion;
        public string savedUtc = "";

        public int   chunkX, chunkY;
        public float maxTerrainHeight = 500f;
        public int   heightmapResolution = 129;

        public float roadSampleSpacing = 5f;
        public float roadSmoothingRadius = 8f;
        public float roadDeckSmoothingRadius = 20f;
        public float roadDeckClosingRadius = 400f;
        public float roadSurfaceOffset = 0.06f;
        public float roadWidthAdjustment = 0f;

        /// <summary>
        /// Terrain elevation in metres (world Y, sea level = 0), row-major
        /// (<c>elevations[row * heightmapResolution + column]</c>, row 0 = south, column 0 = west).
        /// </summary>
        public float[] elevations = new float[0];

        public FloridaTerrainGenerator.OsmBuilding[]  buildings   = new FloridaTerrainGenerator.OsmBuilding[0];
        public FloridaTerrainGenerator.OsmRoad[]      roads       = new FloridaTerrainGenerator.OsmRoad[0];
        public FloridaTerrainGenerator.OsmWaterway[]  waterways   = new FloridaTerrainGenerator.OsmWaterway[0];
        public FloridaTerrainGenerator.OsmRailway[]   railways    = new FloridaTerrainGenerator.OsmRailway[0];
        public FloridaTerrainGenerator.OsmWaterBody[] waterBodies = new FloridaTerrainGenerator.OsmWaterBody[0];
        public FloridaTerrainGenerator.OsmNode[]      nodes       = new FloridaTerrainGenerator.OsmNode[0];
    }

    /// <summary>File names and (de)serialisation for <see cref="FloridaChunkData"/>.</summary>
    public static class FloridaChunkFile
    {
        /// <summary>Name of the Save All file for a chunk, e.g. <c>ChunkData_0_3.json</c>.</summary>
        public static string ChunkFileName(int chunkX, int chunkY) => $"ChunkData_{chunkX}_{chunkY}.json";

        /// <summary>Name of the building export for a chunk, e.g. <c>Buildings_0_3.txt</c>.</summary>
        public static string BuildingFileName(int chunkX, int chunkY) => $"Buildings_{chunkX}_{chunkY}.txt";

        public static string ToJson(FloridaChunkData data) => JsonUtility.ToJson(data, false);

        /// <summary>Parses and validates a Save All file; throws <see cref="FormatException"/> if it is unusable.</summary>
        public static FloridaChunkData FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("The chunk file is empty.");

            FloridaChunkData data;
            try { data = JsonUtility.FromJson<FloridaChunkData>(json); }
            catch (ArgumentException ex) { throw new FormatException("The chunk file is not valid JSON: " + ex.Message, ex); }
            if (data == null)
                throw new FormatException("The chunk file could not be read.");
            if (data.version > FloridaChunkData.CurrentVersion)
                throw new FormatException(
                    $"The chunk file is version {data.version}; this tool reads up to version {FloridaChunkData.CurrentVersion}.");

            if (data.elevations == null) data.elevations = new float[0];
            if (data.elevations.Length > 0 &&
                (data.heightmapResolution < 2 || data.elevations.Length != data.heightmapResolution * data.heightmapResolution))
                throw new FormatException(
                    $"The chunk file has {data.elevations.Length} elevations, which does not fit a " +
                    $"{data.heightmapResolution} x {data.heightmapResolution} heightmap.");

            if (data.buildings   == null) data.buildings   = new FloridaTerrainGenerator.OsmBuilding[0];
            if (data.roads       == null) data.roads       = new FloridaTerrainGenerator.OsmRoad[0];
            if (data.waterways   == null) data.waterways   = new FloridaTerrainGenerator.OsmWaterway[0];
            if (data.railways    == null) data.railways    = new FloridaTerrainGenerator.OsmRailway[0];
            if (data.waterBodies == null) data.waterBodies = new FloridaTerrainGenerator.OsmWaterBody[0];
            if (data.nodes       == null) data.nodes       = new FloridaTerrainGenerator.OsmNode[0];
            return data;
        }

        /// <summary>Flattens a [row, column] grid into the row-major layout of <see cref="FloridaChunkData.elevations"/>.</summary>
        public static float[] Flatten(float[,] grid)
        {
            int rows = grid.GetLength(0), cols = grid.GetLength(1);
            var flat = new float[rows * cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    flat[r * cols + c] = Mathf.Round(grid[r, c] * 1000f) / 1000f;   // millimetres
            return flat;
        }

        /// <summary>Inverse of <see cref="Flatten"/>.</summary>
        public static float[,] ToGrid(float[] flat, int res)
        {
            var grid = new float[res, res];
            for (int r = 0; r < res; r++)
                for (int c = 0; c < res; c++)
                    grid[r, c] = flat[r * res + c];
            return grid;
        }
    }
}
