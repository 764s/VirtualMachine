using System.Collections.Generic;

namespace KOF98
{
    /// <summary>
    /// Describes a pending hit that needs to be resolved.
    /// Produced by skill scripts (via syscall), consumed by CombatSystem.
    /// </summary>
    public struct HitEvent
    {
        public int AttackerId;
        public int TargetId;
        public float DamageCoeff;
        public int DamageType;
        public float EnergyCoeff;

        // ── Knockback ────────────────────────────────────────────
        public int HitstunStartFrame;
        public int HitstunDuration;
        public int HitstunLevel;
        public int HitstunShake;

        public float HorizKBDist;
        public int HorizKBDuration;
        public float HorizKBSpeed;   // Speed mode (-1 = use dist mode)
        public float VertKBSpeed;
        public int VertKBDuration;

        // ── Self effects ─────────────────────────────────────────
        public int SelfHitstunStart;
        public int SelfHitstunDuration;
        public float SelfHorizKBDist;
        public int SelfHorizKBDuration;
        public float SelfVertKBSpeed;
        public float SelfVertKBAccel;
        public float CornerKBSelfDist;
        public int CornerKBSelfDuration;
    }

    /// <summary>
    /// Combat resolution system.
    /// Collects hit events from skill scripts and applies damage, hitstun, and knockback.
    /// </summary>
    public class CombatSystem
    {
        /// <summary>Pending hit events to process this frame.</summary>
        public List<HitEvent> PendingHits = new();

        /// <summary>
        /// Process all pending hit events. Called once per frame after skills run.
        /// </summary>
        public void ProcessHits(CharacterManager chars)
        {
            for (int i = 0; i < PendingHits.Count; i++)
            {
                ProcessSingleHit(chars, PendingHits[i]);
            }
            PendingHits.Clear();
        }

        private void ProcessSingleHit(CharacterManager chars, HitEvent hit)
        {
            var target = chars.Get(hit.TargetId);
            var attacker = chars.Get(hit.AttackerId);
            if (target == null || attacker == null || !target.IsAlive) return;

            // Apply damage
            float damage = hit.DamageCoeff * 10f; // Base damage formula (placeholder)
            target.HP -= damage;
            if (target.HP <= 0)
            {
                target.HP = 0;
                target.IsAlive = false;
                target.SetTag(GameConstants.TAG_DEATH);
            }

            // Apply power gain
            float powerGain = damage * 0.01f * hit.EnergyCoeff;
            attacker.Power = System.Math.Min(attacker.Power + powerGain, attacker.Data.MaxPower);

            // Apply hitstun to target
            if (hit.HitstunDuration > 0)
            {
                target.HitstunFrames = hit.HitstunDuration;
                target.SetTag(GameConstants.TAG_HIT);
            }

            // Apply knockback to target
            ApplyKnockback(target, attacker, hit);

            // Apply self effects to attacker
            ApplySelfEffects(attacker, hit);
        }

        private void ApplyKnockback(Character target, Character attacker, HitEvent hit)
        {
            int kbDir = attacker.Body.Position.X < target.Body.Position.X ? 1 : -1;

            // Horizontal knockback
            if (hit.HorizKBSpeed > 0)
            {
                // Speed mode (until landing)
                target.Body.Velocity = new FVec2(hit.HorizKBSpeed * kbDir, target.Body.Velocity.Y);
            }
            else if (hit.HorizKBDist > 0 && hit.HorizKBDuration > 0)
            {
                // Distance mode
                float speed = hit.HorizKBDist / hit.HorizKBDuration;
                target.Body.Velocity = new FVec2(speed * kbDir, target.Body.Velocity.Y);
            }

            // Vertical knockback
            if (hit.VertKBSpeed > 0)
            {
                target.Body.Velocity = new FVec2(target.Body.Velocity.X, hit.VertKBSpeed);
                target.Body.IsGrounded = false;
                target.SetTag(GameConstants.TAG_AIR_STATE);
            }
        }

        private void ApplySelfEffects(Character attacker, HitEvent hit)
        {
            if (hit.SelfHitstunDuration > 0)
            {
                attacker.HitstunFrames = hit.SelfHitstunDuration;
            }

            if (hit.SelfHorizKBDist > 0 && hit.SelfHorizKBDuration > 0)
            {
                float speed = hit.SelfHorizKBDist / hit.SelfHorizKBDuration;
                // Self knockback is in the facing direction
                attacker.Body.Velocity = new FVec2(
                    speed * attacker.FacingSign, attacker.Body.Velocity.Y);
            }

            if (hit.SelfVertKBSpeed != 0)
            {
                attacker.Body.Velocity = new FVec2(
                    attacker.Body.Velocity.X, hit.SelfVertKBSpeed);
                attacker.Body.Acceleration = new FVec2(0, hit.SelfVertKBAccel);
                attacker.Body.IsGrounded = false;
            }
        }

        /// <summary>Queue a hit event for processing.</summary>
        public void EnqueueHit(HitEvent hit)
        {
            PendingHits.Add(hit);
        }
    }
}
