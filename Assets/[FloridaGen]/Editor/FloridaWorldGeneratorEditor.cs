using System;
using UnityEditor;
using UnityEngine;

namespace DeadEverywhere.FloridaGen.FloridaGenEditor
{
    /// <summary>
    /// Inspector for <see cref="FloridaWorldGenerator"/>: type a chunk (x, y) and run the whole pipeline
    /// from buttons.  Every button acts on the chunk at (x, y); Generate Terrain creates it first.
    /// </summary>
    [CustomEditor(typeof(FloridaWorldGenerator))]
    public class FloridaWorldGeneratorEditor : Editor
    {
        private const string MenuRoot = "Tools/Florida Terrain/";

        private static bool s_ChunksOpen;

        public override void OnInspectorGUI()
        {
            var world = (FloridaWorldGenerator)target;

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("chunkX"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("chunkY"));
            serializedObject.ApplyModifiedProperties();

            DrawStepButtons(world);

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("terrainFolder"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxTerrainHeight"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("heightmapResolution"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoSaveDataFiles"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            DrawChunkPreview(world);
            DrawGizmoButtons(world);
            DrawHierarchy(world);
        }

        // ── Position + status ─────────────────────────────────────────────────────

        private static void DrawChunkPreview(FloridaWorldGenerator world)
        {
            var gen = world.CurrentChunk;

            if (!world.HasIdentityTransform())
                EditorGUILayout.HelpBox(
                    "This object is moved, rotated or scaled. Chunk positions and OSM data are in world " +
                    "space, so reset its transform (position 0, rotation 0, scale 1).",
                    MessageType.Warning);

            if (gen == null)
            {
                EditorGUILayout.HelpBox(
                    $"No chunk ({world.chunkX}, {world.chunkY}) yet. \"Generate Terrain\" creates it as " +
                    $"{FloridaWorldGenerator.RowName(world.chunkX)} / {FloridaWorldGenerator.ChunkName(world.chunkX, world.chunkY)}.",
                    MessageType.Info);
            }
            else
            {
                var b = gen.CalculateChunkBounds(gen.chunkX, gen.chunkY);
                EditorGUILayout.HelpBox(
                    $"Chunk: {gen.name}\nSW {b.south:F5}\u00b0N {-b.west:F5}\u00b0W    NE {b.north:F5}\u00b0N {-b.east:F5}\u00b0W\n" +
                    $"Data files: {gen.GetDataDirectory()}\n" +
                    $"Buildings {gen.osmBuildings?.Length ?? 0}, roads {gen.osmRoads?.Length ?? 0}, " +
                    $"waterways {gen.osmWaterways?.Length ?? 0}, railways {gen.osmRailways?.Length ?? 0}",
                    MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select Chunk")) Selection.activeGameObject = gen.gameObject;
                    if (GUILayout.Button("Ping")) EditorGUIUtility.PingObject(gen.gameObject);
                }
                ShowStatus("Terrain", gen.statusMessage);
                ShowStatus("OSM", gen.osmStatusMessage);
                ShowStatus("Roads", gen.highwayMeshStatusMessage);
            }

            if (!string.IsNullOrEmpty(world.statusMessage))
            {
                bool err = world.statusMessage.StartsWith("Error") || world.statusMessage.StartsWith("Cannot");
                EditorGUILayout.HelpBox(world.statusMessage, err ? MessageType.Error : MessageType.None);
            }
        }

        private static void ShowStatus(string label, string message)
        {
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.LabelField(label, message, EditorStyles.wordWrappedMiniLabel);
        }

        // ── Pipeline buttons ──────────────────────────────────────────────────────

        private void DrawStepButtons(FloridaWorldGenerator world)
        {
            var gen = world.CurrentChunk;
            bool busy = gen != null && (gen.isGenerating || gen.osmFetching);
            bool hasChunk = gen != null;
            bool hasRoads = hasChunk && gen.osmRoads != null && gen.osmRoads.Length > 0;

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(busy))
            {
                if (Step("\u25b6  Generate Terrain  (creates the chunk)", 34, new Color(0.4f, 0.85f, 0.45f), true)) Run(world, world.GenerateTerrain);
                using (new EditorGUI.DisabledScope(!hasChunk))
                {
                    if (Step("\u2b07  Fetch OSM Data  (buildings, roads, water, rail)", 28, new Color(0.3f, 0.7f, 1f), true)) Run(world, world.FetchOsm);
                }
                using (new EditorGUI.DisabledScope(!hasRoads))
                {
                    if (Step("\u25b6  Generate Roads  (meshes + colliders)", 28, new Color(0.55f, 0.35f, 0.9f), true)) Run(world, world.GenerateRoads);
                }
                using (new EditorGUI.DisabledScope(!hasChunk))
                {
                    if (Step("\U0001F4BE  Save Building Data (.txt)", 24, Color.white, false)) Run(world, world.SaveBuildingData);
                    if (Step("\U0001F4BE  Save All  (terrain + OSM data, .json)", 24, Color.white, false)) Run(world, world.SaveAll);
                }
                if (Step("\U0001F4C2  Load All  (rebuild this chunk from its .json, no network)", 24, Color.white, false)) Run(world, world.LoadAll);

                EditorGUILayout.Space(2);
                if (Step("\u23e9  Run Full Pipeline  (terrain \u2192 OSM \u2192 roads \u2192 save)", 30, new Color(1f, 0.75f, 0.3f), true)) Run(world, world.RunFullPipeline);
            }
        }

        private static bool Step(string label, float height, Color color, bool tint)
        {
            var previous = GUI.backgroundColor;
            if (tint) GUI.backgroundColor = color;
            bool clicked = GUILayout.Button(label, GUILayout.Height(height));
            GUI.backgroundColor = previous;
            return clicked;
        }

        private static void Run(FloridaWorldGenerator world, Func<bool> step)
        {
            step();
            var gen = world.CurrentChunk;
            if (gen != null)
            {
                FloridaTerrainGeneratorEditor.InvalidateCache(gen);
                FloridaRoadGizmos.Invalidate(gen);
            }
            EditorUtility.SetDirty(world);
            GUIUtility.ExitGUI();
        }

        // ── Gizmos ────────────────────────────────────────────────────────────────

        private static void DrawGizmoButtons(FloridaWorldGenerator world)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Gizmos", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The gizmo toggles stay on each chunk's Florida Terrain Generator. These buttons flip all " +
                "of them at once.", MessageType.None);

            using (new EditorGUI.DisabledScope(world.CurrentChunk == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Turn Off All Gizmos (this chunk)")) SetGizmos(world, false, false);
                if (GUILayout.Button("Turn On All (this chunk)")) SetGizmos(world, true, false);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Turn Off All Gizmos (every chunk)")) SetGizmos(world, false, true);
                if (GUILayout.Button("Turn On All (every chunk)")) SetGizmos(world, true, true);
            }
        }

        private static void SetGizmos(FloridaWorldGenerator world, bool on, bool all)
        {
            var targets = all ? world.GetChunks() : new System.Collections.Generic.List<FloridaTerrainGenerator>();
            if (!all && world.CurrentChunk != null) targets.Add(world.CurrentChunk);
            foreach (var gen in targets) Undo.RecordObject(gen, "Florida Gizmos");
            world.SetGizmos(on, all);
            SceneView.RepaintAll();
            GUIUtility.ExitGUI();
        }

        // ── Hierarchy ─────────────────────────────────────────────────────────────

        private static void DrawHierarchy(FloridaWorldGenerator world)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Hierarchy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Chunks live in \"Row : X\" groups (rows sorted by X, chunks by Y) and are named " +
                "Row_X_Col_Y. \"Organize\" files hand-made chunks the same way, removes empty rows and " +
                "connects neighbouring terrains.", MessageType.None);

            if (GUILayout.Button("Organize Hierarchy"))
            {
                world.OrganizeHierarchy();
                GUIUtility.ExitGUI();
            }

            var chunks = world.GetChunks();
            s_ChunksOpen = EditorGUILayout.Foldout(s_ChunksOpen, $"Chunks ({chunks.Count})", true);
            if (!s_ChunksOpen) return;
            foreach (var gen in chunks)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"({gen.chunkX}, {gen.chunkY})  {gen.name}");
                    if (GUILayout.Button("Select", GUILayout.Width(60))) Selection.activeGameObject = gen.gameObject;
                    if (GUILayout.Button("Ping", GUILayout.Width(44))) EditorGUIUtility.PingObject(gen.gameObject);
                }
            }
        }

        // ── Menu: gizmos of the selected chunk ───────────────────────────────────

        [MenuItem(MenuRoot + "Selected Chunk - Turn Off All Gizmos", false, 110)]
        private static void SelectedOff() => SetSelected(false);

        [MenuItem(MenuRoot + "Selected Chunk - Turn On All Gizmos", false, 111)]
        private static void SelectedOn() => SetSelected(true);

        [MenuItem(MenuRoot + "Selected Chunk - Turn Off All Gizmos", true)]
        private static bool SelectedOffValid() => SelectedChunk() != null;

        [MenuItem(MenuRoot + "Selected Chunk - Turn On All Gizmos", true)]
        private static bool SelectedOnValid() => SelectedChunk() != null;

        private static FloridaTerrainGenerator SelectedChunk() =>
            Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<FloridaTerrainGenerator>() : null;

        private static void SetSelected(bool on)
        {
            var gen = SelectedChunk();
            if (gen == null) return;
            Undo.RecordObject(gen, "Florida Gizmos");
            gen.SetAllGizmos(on);
            SceneView.RepaintAll();
        }
    }
}
