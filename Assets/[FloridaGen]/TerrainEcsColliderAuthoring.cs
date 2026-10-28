using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace DeadEverywhere.FloridaGeneration
{
    /// <summary>
    /// Attach to an active GameObject outside an ECS SubScene to add a Terrain's static physics collider to each ECS game world.
    /// Assign a Terrain from another GameObject to keep the rendering Terrain separate from this bridge.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TerrainEcsColliderAuthoring : MonoBehaviour
    {
        private readonly EcsColliderWorlds worlds = new EcsColliderWorlds();

        [SerializeField, Tooltip("Optional. If empty, the Terrain on this GameObject is used.")]
        private Terrain terrain;
        private Terrain sourceTerrain;
        private TerrainData sourceTerrainData;
        private BlobAssetReference<Unity.Physics.Collider> collider;
        private bool offsetZeroHeight;
        private Vector3 lastLossyScale;

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                RebuildCollider();
            }
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            ResolveTerrain();

            if (terrain == null)
            {
                ClearCollider();
                return;
            }

            if (terrain.terrainData == null)
            {
                ClearCollider();
                return;
            }

            if (!collider.IsCreated ||
                sourceTerrain != terrain ||
                sourceTerrainData != terrain.terrainData ||
                terrain.transform.lossyScale != lastLossyScale)
            {
                RebuildCollider();
                return;
            }

            SynchronizeWorlds(false);
        }

        private void OnDisable()
        {
            ClearCollider();
        }

        /// <summary>
        /// Rebuilds the ECS collider after changing the terrain heightmap at runtime.
        /// </summary>
        public void RebuildCollider()
        {
            if (!Application.isPlaying || !isActiveAndEnabled)
            {
                return;
            }

            ResolveTerrain();
            if (terrain == null || terrain.terrainData == null)
            {
                ClearCollider();
                return;
            }

            TerrainData data = terrain.terrainData;
            if (!TryCreateCollider(
                    data,
                    terrain.transform.lossyScale,
                    out BlobAssetReference<Unity.Physics.Collider> rebuiltCollider,
                    out bool rebuiltWithOffset))
            {
                ClearCollider();
                return;
            }

            BlobAssetReference<Unity.Physics.Collider> previousCollider = collider;
            collider = rebuiltCollider;
            sourceTerrain = terrain;
            sourceTerrainData = data;
            offsetZeroHeight = rebuiltWithOffset;
            lastLossyScale = terrain.transform.lossyScale;
            SynchronizeWorlds(true);

            if (previousCollider.IsCreated)
            {
                previousCollider.Dispose();
            }
        }

        private bool TryCreateCollider(
            TerrainData data,
            Vector3 terrainScale,
            out BlobAssetReference<Unity.Physics.Collider> result,
            out bool addHeightOffset)
        {
            result = default;
            addHeightOffset = false;

            int resolution = data.heightmapResolution;
            if (resolution < 2 ||
                data.size.x <= 0f || data.size.y <= 0f || data.size.z <= 0f ||
                terrainScale.x <= 0f || terrainScale.y <= 0f || terrainScale.z <= 0f)
            {
                Debug.LogError($"Cannot create an ECS collider for terrain '{terrain.name}' with invalid terrain dimensions or scale.", this);
                return false;
            }

            float[,] terrainHeights = data.GetHeights(0, 0, resolution, resolution);
            var heights = new NativeArray<float>(resolution * resolution, Allocator.Temp);
            try
            {
                bool allHeightsAreZero = true;
                for (int z = 0; z < resolution; z++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        float height = terrainHeights[z, x];
                        heights[z * resolution + x] = height;
                        allHeightsAreZero &= height == 0f;
                    }
                }

                if (allHeightsAreZero)
                {
                    for (int i = 0; i < heights.Length; i++)
                    {
                        heights[i] = 1f;
                    }
                }

                float3 colliderScale = new float3(
                    data.size.x * terrainScale.x / (resolution - 1),
                    data.size.y * terrainScale.y,
                    data.size.z * terrainScale.z / (resolution - 1));
                result = Unity.Physics.TerrainCollider.Create(
                    heights,
                    new int2(resolution, resolution),
                    colliderScale,
                    Unity.Physics.TerrainCollider.CollisionMethod.Triangles);
                addHeightOffset = allHeightsAreZero;
            }
            finally
            {
                heights.Dispose();
            }

            return result.IsCreated;
        }

        private void SynchronizeWorlds(bool colliderChanged)
        {
            if (!collider.IsCreated || terrain == null)
            {
                return;
            }

            Transform terrainTransform = terrain.transform;
            Vector3 position = terrainTransform.position;
            Quaternion rotation = terrainTransform.rotation;
            Vector3 lossyScale = terrainTransform.lossyScale;
            LocalTransform localTransform = CreateLocalTransform(position, rotation, lossyScale);
            worlds.Synchronize(collider, localTransform, colliderChanged, "Terrain Collider");
        }

        private LocalTransform CreateLocalTransform(Vector3 position, Quaternion rotation, Vector3 lossyScale)
        {
            if (offsetZeroHeight)
            {
                position -= rotation * Vector3.up * (sourceTerrainData.size.y * lossyScale.y);
            }

            return new LocalTransform
            {
                Position = new float3(position.x, position.y, position.z),
                Rotation = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                Scale = 1f
            };
        }

        private void ResolveTerrain()
        {
            if (terrain == null)
            {
                terrain = GetComponent<Terrain>();
            }
        }

        private void ClearCollider()
        {
            worlds.Clear();

            if (collider.IsCreated)
            {
                collider.Dispose();
            }

            collider = default;
            sourceTerrain = null;
            sourceTerrainData = null;
        }
    }
}
