using System.Collections.Generic;
using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;

namespace DeadEverywhere.FloridaGeneration
{
    /// <summary>Shares one static collider with every live ECS game world.</summary>
    internal sealed class EcsColliderWorlds
    {
        private static readonly WorldFlags GameWorldFlags =
            WorldFlags.Game | WorldFlags.GameClient | WorldFlags.GameServer | WorldFlags.GameThinClient;

        private readonly Dictionary<World, Entity> entities = new Dictionary<World, Entity>();
        private readonly HashSet<World> activeWorlds = new HashSet<World>();
        private readonly List<World> staleWorlds = new List<World>();
        private LocalTransform lastTransform;
        private bool hasLastTransform;

        public void Synchronize(
            BlobAssetReference<Collider> collider, LocalTransform transform, bool colliderChanged, string name)
        {
            bool transformChanged = colliderChanged || !hasLastTransform ||
                                    !transform.Position.Equals(lastTransform.Position) ||
                                    !transform.Rotation.Equals(lastTransform.Rotation) ||
                                    transform.Scale != lastTransform.Scale;

            activeWorlds.Clear();
            for (int i = 0; i < World.All.Count; i++)
            {
                World world = World.All[i];
                if (world == null || !world.IsCreated || (world.Flags & GameWorldFlags) == 0)
                    continue;

                activeWorlds.Add(world);
                EntityManager manager = world.EntityManager;
                if (entities.TryGetValue(world, out Entity entity) && manager.Exists(entity))
                {
                    if (colliderChanged)
                    {
                        // The caller disposes the previous blob after all worlds have switched to this one.
                        manager.CompleteAllTrackedJobs();
                        manager.SetComponentData(entity, new PhysicsCollider { Value = collider });
                    }
                    if (transformChanged)
                        manager.SetComponentData(entity, transform);
                    continue;
                }

                entity = manager.CreateEntity(
                    ComponentType.ReadWrite<LocalTransform>(),
                    ComponentType.ReadWrite<PhysicsCollider>(),
                    ComponentType.ReadWrite<PhysicsWorldIndex>());
                manager.SetName(entity, name);
                manager.SetComponentData(entity, transform);
                manager.SetComponentData(entity, new PhysicsCollider { Value = collider });
                manager.SetSharedComponent(entity, new PhysicsWorldIndex { Value = 0 });
                entities[world] = entity;
            }

            staleWorlds.Clear();
            foreach (var pair in entities)
                if (!activeWorlds.Contains(pair.Key))
                    staleWorlds.Add(pair.Key);
            foreach (World world in staleWorlds)
            {
                DestroyEntity(world, entities[world]);
                entities.Remove(world);
            }

            lastTransform = transform;
            hasLastTransform = true;
        }

        public void Clear()
        {
            foreach (var pair in entities)
                DestroyEntity(pair.Key, pair.Value);
            entities.Clear();
            activeWorlds.Clear();
            staleWorlds.Clear();
            hasLastTransform = false;
        }

        private static void DestroyEntity(World world, Entity entity)
        {
            if (world == null || !world.IsCreated) return;
            EntityManager manager = world.EntityManager;
            manager.CompleteAllTrackedJobs();
            if (manager.Exists(entity))
                manager.DestroyEntity(entity);
        }
    }
}
