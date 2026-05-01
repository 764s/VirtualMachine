namespace KOF98.Game
{
    /// <summary>
    /// Per-frame projectile update: integrate position, decrement lifetime,
    /// destroy on stage bounds or expiration.
    /// </summary>
    public static class ProjectileSystem
    {
        public static void Update(GameWorld world)
        {
            for (int e = world.NonCharacterSlotStart; e < GameWorld.MaxEntities; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Projectile) continue;

                ref var tr = ref world.Transform[e];
                ref var proj = ref world.Projectile[e];

                tr.Position = tr.Position + proj.Velocity;

                ref var life = ref world.Lifetime[e];
                life.RemainingFrames--;
                if (life.RemainingFrames <= 0)
                {
                    world.DestroyAt(e);
                    continue;
                }

                if (tr.Position.X < GameConstants.StageLeftBound - 2f
                    || tr.Position.X > GameConstants.StageRightBound + 2f)
                {
                    world.DestroyAt(e);
                }
            }
        }

        /// <summary>Spawn a new projectile entity. Returns the entity slot index, or -1 on failure.</summary>
        public static int Spawn(GameWorld world, int ownerEntity, int team,
            FVec2 position, FVec2 velocity, FRect hitBox, float damage, int damageType, int durationFrames)
        {
            var id = world.SpawnProjectile();
            if (!id.IsValid) return -1;

            int e = id.Index;
            world.Transform[e] = new TransformComponent { Position = position, Facing = Direction.Right };
            world.Projectile[e] = new ProjectileComponent
            {
                OwnerEntity = ownerEntity,
                Team = team,
                Velocity = velocity,
                HitBox = hitBox,
                Damage = damage,
                DamageType = damageType,
            };
            world.Lifetime[e] = new LifetimeComponent { RemainingFrames = durationFrames };
            return e;
        }
    }
}
