namespace KOF98
{
    /// <summary>
    /// A runtime projectile entity (fireball, beam, etc.).
    /// Projectiles have their own physics, hitbox, and optionally a VM script.
    /// Extension point: complex projectile behavior via VM instances.
    /// </summary>
    public struct ProjectileData
    {
        public int Id;
        public int OwnerId;           // Spawning character
        public int Team;
        public FVec2 Position;
        public FVec2 Velocity;
        public FRect HitBox;          // Relative to position
        public float Damage;
        public int DamageType;
        public int RemainingFrames;
        public bool IsActive;

        /// <summary>VM instance ID for scripted projectile behavior (-1 = simple linear).</summary>
        public int VMInstanceId;
    }

    /// <summary>
    /// Manages all active projectiles in the scene.
    /// </summary>
    public class ProjectileManager
    {
        public ProjectileData[] Projectiles = new ProjectileData[GameConstants.MaxProjectiles];
        public int Count;
        private int _nextId;

        /// <summary>Spawn a new projectile.</summary>
        public int Spawn(int ownerId, int team, FVec2 position, FVec2 velocity,
            FRect hitBox, float damage, int damageType, int durationFrames)
        {
            int slot = FindFreeSlot();
            if (slot < 0) return -1;

            Projectiles[slot] = new ProjectileData
            {
                Id = _nextId++,
                OwnerId = ownerId,
                Team = team,
                Position = position,
                Velocity = velocity,
                HitBox = hitBox,
                Damage = damage,
                DamageType = damageType,
                RemainingFrames = durationFrames,
                IsActive = true,
                VMInstanceId = -1,
            };
            Count++;
            return Projectiles[slot].Id;
        }

        /// <summary>
        /// Update all projectiles: move, check lifetime.
        /// Hit detection is performed by CollisionSystem separately.
        /// </summary>
        public void Update()
        {
            for (int i = 0; i < Projectiles.Length; i++)
            {
                if (!Projectiles[i].IsActive) continue;

                // Move
                Projectiles[i].Position = Projectiles[i].Position + Projectiles[i].Velocity;

                // Lifetime
                Projectiles[i].RemainingFrames--;
                if (Projectiles[i].RemainingFrames <= 0)
                {
                    Projectiles[i].IsActive = false;
                    Count--;
                    continue;
                }

                // Stage bounds check
                if (Projectiles[i].Position.X < GameConstants.StageLeftBound - 2f
                    || Projectiles[i].Position.X > GameConstants.StageRightBound + 2f)
                {
                    Projectiles[i].IsActive = false;
                    Count--;
                }
            }
        }

        /// <summary>Destroy a projectile by ID.</summary>
        public void Destroy(int projectileId)
        {
            for (int i = 0; i < Projectiles.Length; i++)
            {
                if (Projectiles[i].IsActive && Projectiles[i].Id == projectileId)
                {
                    Projectiles[i].IsActive = false;
                    Count--;
                    return;
                }
            }
        }

        /// <summary>Clear all projectiles.</summary>
        public void Clear()
        {
            for (int i = 0; i < Projectiles.Length; i++)
                Projectiles[i].IsActive = false;
            Count = 0;
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < Projectiles.Length; i++)
            {
                if (!Projectiles[i].IsActive) return i;
            }
            return -1;
        }
    }
}
