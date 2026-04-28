namespace KOF98.Game
{
    /// <summary>
    /// Per-frame effect lifetime management.
    /// </summary>
    public static class EffectSystem
    {
        public static void Update(GameWorld world)
        {
            for (int e = world.NonCharacterSlotStart; e < GameWorld.MaxEntities; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Effect) continue;

                ref var life = ref world.Lifetime[e];
                life.RemainingFrames--;
                if (life.RemainingFrames <= 0)
                {
                    world.DestroyAt(e);
                }
            }
        }

        /// <summary>Spawn a new effect entity. Returns the entity slot index, or -1 on failure.</summary>
        public static int Spawn(GameWorld world, int effectTypeId, int ownerEntity,
            FVec2 position, int durationFrames)
        {
            var id = world.SpawnEffect();
            if (!id.IsValid) return -1;

            int e = id.Index;
            world.Transform[e] = new TransformComponent { Position = position, Facing = Direction.Right };
            world.Effect[e] = new EffectComponent
            {
                EffectTypeId = effectTypeId,
                OwnerEntity = ownerEntity,
            };
            world.Lifetime[e] = new LifetimeComponent { RemainingFrames = durationFrames };
            return e;
        }
    }
}
