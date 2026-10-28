using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace DeadEverywhere.FloridaGeneration
{
    /// <summary>Mirrors a generated road MeshCollider into every ECS game world, outside SubScenes.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnityEngine.MeshCollider))]
    public sealed class RoadEcsColliderAuthoring : MonoBehaviour
    {
        private readonly EcsColliderWorlds worlds = new EcsColliderWorlds();
        private UnityEngine.MeshCollider meshCollider;
        private Mesh sourceMesh;
        private Vector3 lastLossyScale;
        private BlobAssetReference<Unity.Physics.Collider> collider;

        private void OnEnable()
        {
            RebuildCollider();
        }

        private void Update()
        {
            if (!Application.isPlaying) return;
            if (meshCollider == null) meshCollider = GetComponent<UnityEngine.MeshCollider>();
            if (meshCollider == null || !meshCollider.enabled || meshCollider.sharedMesh == null)
            {
                ClearCollider();
                return;
            }

            if (!collider.IsCreated || sourceMesh != meshCollider.sharedMesh || lastLossyScale != transform.lossyScale)
                RebuildCollider();
            else
                worlds.Synchronize(collider, ColliderTransform(), false, "Road Collider");
        }

        private void OnDisable()
        {
            ClearCollider();
        }

        /// <summary>Call after editing the source mesh vertices or triangles in place at runtime.</summary>
        public void RebuildCollider()
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return;
            if (meshCollider == null) meshCollider = GetComponent<UnityEngine.MeshCollider>();
            if (meshCollider == null || !meshCollider.enabled || meshCollider.sharedMesh == null)
            {
                ClearCollider();
                return;
            }

            Mesh mesh = meshCollider.sharedMesh;
            Vector3 scale = transform.lossyScale;
            if (!mesh.isReadable || mesh.vertexCount < 3 || scale.x <= 0f || scale.y <= 0f || scale.z <= 0f)
            {
                Debug.LogError($"Cannot create an ECS collider for road '{name}': the mesh must be readable, " +
                               "have at least three vertices, and use a positive scale.", this);
                ClearCollider();
                return;
            }

            Vector3[] meshVertices = mesh.vertices;
            int[] meshTriangles = mesh.triangles;
            if (meshTriangles.Length == 0)
            {
                Debug.LogError($"Cannot create an ECS collider for road '{name}' without triangles.", this);
                ClearCollider();
                return;
            }

            var vertices = new NativeArray<float3>(meshVertices.Length, Allocator.Temp);
            var triangles = new NativeArray<int3>(meshTriangles.Length / 3, Allocator.Temp);
            BlobAssetReference<Unity.Physics.Collider> rebuiltCollider;
            try
            {
                for (int i = 0; i < meshVertices.Length; i++)
                {
                    Vector3 vertex = Vector3.Scale(meshVertices[i], scale);
                    vertices[i] = new float3(vertex.x, vertex.y, vertex.z);
                }
                for (int i = 0; i < triangles.Length; i++)
                    triangles[i] = new int3(meshTriangles[i * 3], meshTriangles[i * 3 + 1], meshTriangles[i * 3 + 2]);
                rebuiltCollider = Unity.Physics.MeshCollider.Create(vertices, triangles);
            }
            finally
            {
                vertices.Dispose();
                triangles.Dispose();
            }

            var previousCollider = collider;
            collider = rebuiltCollider;
            sourceMesh = mesh;
            lastLossyScale = scale;
            worlds.Synchronize(collider, ColliderTransform(), true, "Road Collider");
            if (previousCollider.IsCreated) previousCollider.Dispose();
        }

        private LocalTransform ColliderTransform()
        {
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            return new LocalTransform
            {
                Position = new float3(position.x, position.y, position.z),
                Rotation = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                Scale = 1f
            };
        }

        private void ClearCollider()
        {
            worlds.Clear();
            if (collider.IsCreated) collider.Dispose();
            collider = default;
            sourceMesh = null;
        }
    }
}
