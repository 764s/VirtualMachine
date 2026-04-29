using System.Diagnostics;

namespace KOF98.Game
{
    /// <summary>
    /// Describes a pending hit produced by a skill behavior, consumed by
    /// <see cref="CombatSystem"/>. Attacker/Target are entity slot indices.
    /// </summary>
    public struct HitEvent
    {
        public int AttackerId;
        public int TargetId;
        public float DamageCoeff;
        public int DamageType;
        public float EnergyCoeff;

        public int HitstunStartFrame;
        public int HitstunDuration;
        public int HitstunLevel;
        public int HitstunShake;

        public float HorizKBDist;
        public int HorizKBDuration;
        public float HorizKBSpeed;
        public float VertKBSpeed;
        public int VertKBDuration;

        public int SelfHitstunStart;
        public int SelfHitstunDuration;
        public float SelfHorizKBDist;
        public int SelfHorizKBDuration;
        public float SelfVertKBSpeed;
        public float SelfVertKBAccel;
        public float CornerKBSelfDist;
        public int CornerKBSelfDuration;

        /// <summary>
        /// Hit-pause: number of frames both attacker and target freeze on impact.
        /// 0 = no hit-pause (legacy behavior). Stacking is max(old, new).
        /// </summary>
        public int HitPauseFrames;
    }

    /// <summary>
    /// Combat resolution system. Stateless: pending hit queue lives on
    /// <see cref="GameWorld.PendingHits"/> / <see cref="GameWorld.PendingHitCount"/>
    /// so it participates in snapshot capture/restore.
    /// </summary>
    public static class CombatSystem
    {
        public static void EnqueueHit(GameWorld world, in HitEvent hit)
        {
            if (world.PendingHitCount >= world.PendingHits.Length)
            {
                Debug.Assert(false, "Pending hit queue overflow");
                return;
            }

            world.PendingHits[world.PendingHitCount++] = hit;
        }

        public static void Clear(GameWorld world) => world.PendingHitCount = 0;

        public static void ProcessHits(GameWorld world)
        {
            for (int i = 0; i < world.PendingHitCount; i++)
                ApplyHit(world, world.PendingHits[i]);
            world.PendingHitCount = 0;
        }

        private static void ApplyHit(GameWorld world, HitEvent hit)
        {
            int target = hit.TargetId;
            int attacker = hit.AttackerId;

            if (!world.IsAliveSlot(target) || !world.IsAliveSlot(attacker)) return;
            if (!world.Life[target].IsAlive) return;

            // Damage
            float damage = hit.DamageCoeff * 10f;
            world.Life[target].HP -= damage;
            if (world.Life[target].HP <= 0)
            {
                world.Life[target].HP = 0;
                world.Life[target].IsAlive = false;
                world.SetTag(target, GameConstants.TAG_DEATH);
            }

            // Power gain on attacker
            float powerGain = damage * 0.01f * hit.EnergyCoeff;
            float newPower = world.Life[attacker].Power + powerGain;
            var stats = GameCatalog.Stats[GameCatalog.Characters[world.Identity[attacker].CharacterId].StatsId];
            float maxPower = stats.MaxPower;
            if (newPower > maxPower) newPower = maxPower;
            world.Life[attacker].Power = newPower;

            // Hitstun on target
            if (hit.HitstunDuration > 0)
            {
                world.Status[target].HitstunFrames = hit.HitstunDuration;
                world.SetTag(target, GameConstants.TAG_HIT);
            }

            ApplyKnockback(world, target, attacker, hit);
            ApplySelfEffects(world, attacker, hit);

            if (hit.HitPauseFrames > 0)
            {
                FrameLineSystem.RequestCharacterPause(world, attacker, hit.HitPauseFrames);
                FrameLineSystem.RequestCharacterPause(world, target, hit.HitPauseFrames);
            }
        }

        private static void ApplyKnockback(GameWorld world, int target, int attacker, HitEvent hit)
        {
            int kbDir = world.Transform[attacker].Position.X < world.Transform[target].Position.X ? 1 : -1;
            ref var phys = ref world.Physics[target];

            if (hit.HorizKBSpeed > 0)
            {
                phys.Velocity = new FVec2(hit.HorizKBSpeed * kbDir, phys.Velocity.Y);
            }
            else if (hit.HorizKBDist > 0 && hit.HorizKBDuration > 0)
            {
                float speed = hit.HorizKBDist / hit.HorizKBDuration;
                phys.Velocity = new FVec2(speed * kbDir, phys.Velocity.Y);
            }

            if (hit.VertKBSpeed > 0)
            {
                phys.Velocity = new FVec2(phys.Velocity.X, hit.VertKBSpeed);
                phys.IsGrounded = false;
                world.SetTag(target, GameConstants.TAG_AIR_STATE);
            }
        }

        private static void ApplySelfEffects(GameWorld world, int attacker, HitEvent hit)
        {
            if (hit.SelfHitstunDuration > 0)
            {
                world.Status[attacker].HitstunFrames = hit.SelfHitstunDuration;
            }

            ref var phys = ref world.Physics[attacker];
            int facingSign = world.Transform[attacker].FacingSign;

            if (hit.SelfHorizKBDist > 0 && hit.SelfHorizKBDuration > 0)
            {
                float speed = hit.SelfHorizKBDist / hit.SelfHorizKBDuration;
                phys.Velocity = new FVec2(speed * facingSign, phys.Velocity.Y);
            }

            if (hit.SelfVertKBSpeed != 0)
            {
                phys.Velocity = new FVec2(phys.Velocity.X, hit.SelfVertKBSpeed);
                phys.Acceleration = new FVec2(0, hit.SelfVertKBAccel);
                phys.IsGrounded = false;
            }
        }
    }
}
