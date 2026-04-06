using System;
using System.Collections.Generic;
using FFVM;

namespace KOF98
{
    /// <summary>
    /// Registers all KOF98-specific syscalls on a FFVM SyscallTable.
    /// Bridges between VM scripts and the game's C# systems.
    ///
    /// Syscall slot allocation follows GameConstants.SYS_* constants.
    /// Each syscall reads/writes via SyscallArgs (absolute register convention).
    /// </summary>
    public static class GameSyscalls
    {
        /// <summary>Current scene context — set before each VMWorld.Tick().</summary>
        private static GameScene _scene;
        private static CharacterManager _chars;
        private static CombatSystem _combat;
        private static EffectManager _effects;
        private static CollisionSystem _collision;

        /// <summary>
        /// Per-tick context: the VM bridge for resolving instance → owner mapping.
        /// Set by GameVMBridge before Tick().
        /// </summary>
        public static GameVMBridge VMBridge;

        /// <summary>Pending energy coefficient for the next ApplyDamage call.</summary>
        private static float _energyCoeff = 1f;

        /// <summary>Resolve the owner character ID for the given VM instance state.</summary>
        private static int ResolveOwnerId(ref VMInstanceState s)
        {
            return VMBridge?.GetOwnerForInstance(s.InstanceId) ?? -1;
        }

        /// <summary>
        /// Build the syscall name → slot mapping for the FFS compiler.
        /// </summary>
        public static Dictionary<string, int> GetSyscallMap()
        {
            return new Dictionary<string, int>
            {
                // Action management
                { "BeginAction", GameConstants.SYS_BEGIN_ACTION },
                { "EndAction", GameConstants.SYS_END_ACTION },
                { "GetFrame", GameConstants.SYS_GET_FRAME },

                // Collision detection
                { "CheckAttackHit", GameConstants.SYS_CHECK_ATTACK_HIT },
                { "CheckAttackBlocked", GameConstants.SYS_CHECK_ATTACK_BLOCKED },
                { "HasTargetTag", GameConstants.SYS_HAS_TARGET_TAG },

                // Damage and effects
                { "ApplyDamage", GameConstants.SYS_APPLY_DAMAGE },
                { "SetEnergyCoeff", GameConstants.SYS_SET_ENERGY_COEFF },
                { "ApplyHitstun", GameConstants.SYS_APPLY_HITSTUN },
                { "ApplyHorizKB_Dist", GameConstants.SYS_APPLY_HORIZ_KB_DIST },
                { "ApplyHorizKB_Speed", GameConstants.SYS_APPLY_HORIZ_KB_SPEED },
                { "ApplyVertKB", GameConstants.SYS_APPLY_VERT_KB },
                { "ApplyCornerKBSelf", GameConstants.SYS_APPLY_CORNER_KB_SELF },
                { "ApplySelfHitstun", GameConstants.SYS_APPLY_SELF_HITSTUN },
                { "ApplySelfHorizKB", GameConstants.SYS_APPLY_SELF_HORIZ_KB },
                { "ApplySelfVertKB", GameConstants.SYS_APPLY_SELF_VERT_KB },

                // Visual effects
                { "SpawnEffectHit", GameConstants.SYS_SPAWN_EFFECT_HIT },
                { "SpawnEffectSelf", GameConstants.SYS_SPAWN_EFFECT_SELF },

                // Character queries
                { "GetSelfId", GameConstants.SYS_GET_SELF_ID },
                { "GetPosX", GameConstants.SYS_GET_POS_X },
                { "GetPosY", GameConstants.SYS_GET_POS_Y },
                { "GetFacing", GameConstants.SYS_GET_FACING },
                { "GetHP", GameConstants.SYS_GET_HP },
                { "GetPower", GameConstants.SYS_GET_POWER },
                { "IsGrounded", GameConstants.SYS_IS_GROUNDED },
                { "GetOpponentId", GameConstants.SYS_GET_OPPONENT_ID },
                { "GetDistance", GameConstants.SYS_GET_DISTANCE },

                // Character control
                { "SetVelocity", GameConstants.SYS_SET_VELOCITY },
                { "SetFacing", GameConstants.SYS_SET_FACING },
                { "AddPower", GameConstants.SYS_ADD_POWER },

                // Input queries
                { "GetInput", GameConstants.SYS_GET_INPUT },
                { "GetInputDir", GameConstants.SYS_GET_INPUT_DIR },

                // AI
                { "FindNearestEnemy", GameConstants.SYS_AI_FIND_NEAREST_ENEMY },
                { "GetDistanceTo", GameConstants.SYS_AI_GET_DISTANCE },
                { "MoveToward", GameConstants.SYS_AI_MOVE_TOWARD },

                // VM instance management
                { "SpawnScript", GameConstants.SYS_SPAWN_SCRIPT },
                { "KillInstance", GameConstants.SYS_KILL_INSTANCE },

                // Blackboard
                { "SetBlackboard", GameConstants.SYS_SET_BLACKBOARD },
                { "GetBlackboard", GameConstants.SYS_GET_BLACKBOARD },

                // Utility
                { "print", GameConstants.SYS_PRINT },
                { "random", GameConstants.SYS_RANDOM },
                { "abs", GameConstants.SYS_ABS },
                { "min", GameConstants.SYS_MIN },
                { "max", GameConstants.SYS_MAX },
            };
        }

        /// <summary>
        /// Set scene context. Call once per frame before VMWorld.Tick().
        /// </summary>
        public static void SetContext(GameScene scene)
        {
            _scene = scene;
            _chars = scene.Characters;
            _combat = scene.Combat;
            _effects = scene.Effects;
            _collision = scene.Collision;
        }

        /// <summary>
        /// Register all game syscall handlers on the FFVM SyscallTable.
        /// </summary>
        public static void RegisterAll(SyscallTable table)
        {
            // ── Action Management ────────────────────────────────
            table.Register(GameConstants.SYS_BEGIN_ACTION, "BeginAction", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int actionId = args.GetInt(0);
                int totalFrames = args.GetInt(1);
                var owner = GetOwner(ref s);
                if (owner != null && owner.SkillMgr.ActiveSkill != null)
                {
                    owner.SkillMgr.ActiveSkill.Def.TotalFrames = totalFrames;
                }
                // Action data (collision boxes) would be loaded from action assets here.
                // For now, collision boxes are defined in SkillDef.CollisionFrames.
            });

            table.Register(GameConstants.SYS_END_ACTION, "EndAction", (ref VMInstanceState s) =>
            {
                var owner = GetOwner(ref s);
                if (owner != null)
                {
                    owner.ClearHitBoxes();
                    owner.ClearHurtBoxes();
                }
            });

            table.Register(GameConstants.SYS_GET_FRAME, "GetFrame", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                int frame = owner?.SkillMgr.ActiveSkill?.Frame ?? 0;
                args.SetReturnInt(frame);
            });

            // ── Collision Detection ──────────────────────────────
            table.Register(GameConstants.SYS_CHECK_ATTACK_HIT, "CheckAttackHit", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int groupId = args.GetInt(0);
                int targetId = _collision.CheckAttackHit(_chars, ResolveOwnerId(ref s), groupId);
                args.SetReturnInt(targetId >= 0 ? targetId + 1 : 0); // 0 = no hit (script convention)
            });

            table.Register(GameConstants.SYS_CHECK_ATTACK_BLOCKED, "CheckAttackBlocked", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int groupId = args.GetInt(0);
                int targetId = _collision.CheckAttackBlocked(_chars, ResolveOwnerId(ref s), groupId);
                args.SetReturnInt(targetId >= 0 ? targetId + 1 : 0);
            });

            table.Register(GameConstants.SYS_HAS_TARGET_TAG, "HasTargetTag", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int targetId = args.GetInt(0) - 1; // Script uses 1-based IDs
                int tagBit = args.GetInt(1);
                var target = _chars.Get(targetId);
                args.SetReturnInt(target != null && target.HasTag(tagBit) ? 1 : 0);
            });

            // ── Damage and Effects ───────────────────────────────
            table.Register(GameConstants.SYS_APPLY_DAMAGE, "ApplyDamage", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int targetId = args.GetInt(0) - 1;
                float coeff = args.GetFloat(1);
                int dmgType = args.GetInt(2);
                _combat.EnqueueHit(new HitEvent
                {
                    AttackerId = ResolveOwnerId(ref s),
                    TargetId = targetId,
                    DamageCoeff = coeff,
                    DamageType = dmgType,
                    EnergyCoeff = _energyCoeff,
                });
                _energyCoeff = 1f; // Reset after use
            });

            table.Register(GameConstants.SYS_SET_ENERGY_COEFF, "SetEnergyCoeff", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                _energyCoeff = args.GetFloat(0);
            });

            table.Register(GameConstants.SYS_APPLY_HITSTUN, "ApplyHitstun", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int targetId = args.GetInt(0) - 1;
                var target = _chars.Get(targetId);
                if (target != null)
                {
                    target.HitstunFrames = args.GetInt(2); // durF
                }
            });

            table.Register(GameConstants.SYS_APPLY_HORIZ_KB_DIST, "ApplyHorizKB_Dist", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                // targetId, dist, durF — applied via CombatSystem
            });

            table.Register(GameConstants.SYS_APPLY_HORIZ_KB_SPEED, "ApplyHorizKB_Speed", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                // targetId, speed — applied via CombatSystem
            });

            table.Register(GameConstants.SYS_APPLY_VERT_KB, "ApplyVertKB", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                // targetId, speed, durF — applied via CombatSystem
            });

            table.Register(GameConstants.SYS_APPLY_CORNER_KB_SELF, "ApplyCornerKBSelf", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                // dist, durF — applied to self via CombatSystem
            });

            table.Register(GameConstants.SYS_APPLY_SELF_HITSTUN, "ApplySelfHitstun", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                if (owner != null)
                {
                    owner.HitstunFrames = args.GetInt(1); // durF
                }
            });

            table.Register(GameConstants.SYS_APPLY_SELF_HORIZ_KB, "ApplySelfHorizKB", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                float dist = args.GetFloat(0);
                int durF = args.GetInt(1);
                var owner = GetOwner(ref s);
                if (owner != null && durF > 0)
                {
                    float speed = dist / durF;
                    owner.Body.Velocity = new FVec2(speed * owner.FacingSign, owner.Body.Velocity.Y);
                }
            });

            table.Register(GameConstants.SYS_APPLY_SELF_VERT_KB, "ApplySelfVertKB", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                float speed = args.GetFloat(0);
                float accel = args.GetFloat(1);
                var owner = GetOwner(ref s);
                if (owner != null)
                {
                    owner.Body.Velocity = new FVec2(owner.Body.Velocity.X, speed);
                    owner.Body.Acceleration = new FVec2(0, accel);
                    owner.Body.IsGrounded = false;
                }
            });

            // ── Visual Effects ───────────────────────────────────
            table.Register(GameConstants.SYS_SPAWN_EFFECT_HIT, "SpawnEffectHit", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int effectId = args.GetInt(0);
                int durF = args.GetInt(1);
                var owner = GetOwner(ref s);
                if (owner != null)
                    _effects.Spawn(effectId, ResolveOwnerId(ref s), owner.Body.Position, durF);
            });

            table.Register(GameConstants.SYS_SPAWN_EFFECT_SELF, "SpawnEffectSelf", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int effectId = args.GetInt(0);
                int durF = args.GetInt(1);
                var owner = GetOwner(ref s);
                if (owner != null)
                    _effects.Spawn(effectId, ResolveOwnerId(ref s), owner.Body.Position, durF);
            });

            // ── Character Queries ────────────────────────────────
            table.Register(GameConstants.SYS_GET_SELF_ID, "GetSelfId", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                args.SetReturnInt(ResolveOwnerId(ref s) + 1); // 1-based for scripts
            });

            table.Register(GameConstants.SYS_GET_POS_X, "GetPosX", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnFloat(owner?.Body.Position.X ?? 0f);
            });

            table.Register(GameConstants.SYS_GET_POS_Y, "GetPosY", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnFloat(owner?.Body.Position.Y ?? 0f);
            });

            table.Register(GameConstants.SYS_GET_FACING, "GetFacing", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnInt(owner != null ? GameConstants.FacingSign(owner.Facing) : 1);
            });

            table.Register(GameConstants.SYS_GET_HP, "GetHP", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnFloat(owner?.HP ?? 0f);
            });

            table.Register(GameConstants.SYS_GET_POWER, "GetPower", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnFloat(owner?.Power ?? 0f);
            });

            table.Register(GameConstants.SYS_IS_GROUNDED, "IsGrounded", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnInt(owner != null && owner.IsGrounded ? 1 : 0);
            });

            table.Register(GameConstants.SYS_GET_OPPONENT_ID, "GetOpponentId", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var opp = _chars.FindNearestOpponent(ResolveOwnerId(ref s));
                args.SetReturnInt(opp != null ? opp.Id + 1 : 0);
            });

            table.Register(GameConstants.SYS_GET_DISTANCE, "GetDistance", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                var opp = _chars.FindNearestOpponent(ResolveOwnerId(ref s));
                float dist = (owner != null && opp != null)
                    ? owner.Body.Position.HDistanceTo(opp.Body.Position) : 999f;
                args.SetReturnFloat(dist);
            });

            // ── Character Control ────────────────────────────────
            table.Register(GameConstants.SYS_SET_VELOCITY, "SetVelocity", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                float vx = args.GetFloat(0);
                float vy = args.GetFloat(1);
                var owner = GetOwner(ref s);
                if (owner != null)
                    owner.Body.Velocity = new FVec2(vx, vy);
            });

            table.Register(GameConstants.SYS_SET_FACING, "SetFacing", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int dir = args.GetInt(0);
                var owner = GetOwner(ref s);
                if (owner != null)
                    owner.Facing = dir >= 0 ? Direction.Right : Direction.Left;
            });

            table.Register(GameConstants.SYS_ADD_POWER, "AddPower", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                float amount = args.GetFloat(0);
                var owner = GetOwner(ref s);
                if (owner != null)
                    owner.Power = Math.Min(owner.Power + amount, owner.Data.MaxPower);
            });

            // ── Input Queries ────────────────────────────────────
            table.Register(GameConstants.SYS_GET_INPUT, "GetInput", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                args.SetReturnInt(owner != null ? (int)owner.CurrentInput.Held : 0);
            });

            table.Register(GameConstants.SYS_GET_INPUT_DIR, "GetInputDir", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var owner = GetOwner(ref s);
                int dir = owner?.CurrentInput.GetForwardDir(owner.Facing) ?? 0;
                args.SetReturnInt(dir);
            });

            // ── Blackboard ───────────────────────────────────────
            table.Register(GameConstants.SYS_SET_BLACKBOARD, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int entityId = args.GetInt(0) - 1;
                int key = args.GetInt(1);
                float value = args.GetFloat(2);
                var target = _chars.Get(entityId);
                target?.SetBlackboard(key, value);
            });

            table.Register(GameConstants.SYS_GET_BLACKBOARD, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int entityId = args.GetInt(0) - 1;
                int key = args.GetInt(1);
                var target = _chars.Get(entityId);
                args.SetReturnFloat(target?.GetBlackboard(key) ?? 0f);
            });

            // ── Utility ──────────────────────────────────────────
            table.Register(GameConstants.SYS_PRINT, "print", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                Console.WriteLine($"[KOF98] {args.GetNumber(0)}");
            });

            table.Register(GameConstants.SYS_RANDOM, "random", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int upper = args.GetInt(0);
                args.SetReturnInt(upper > 0 ? Random.Shared.Next(upper) : 0);
            });

            table.Register(GameConstants.SYS_ABS, "abs", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var val = args.GetNumber(0);
                if (val < Number.Zero) args.SetReturn(-val);
            });

            table.Register(GameConstants.SYS_MIN, "min", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var a = args.GetNumber(0);
                var b = args.GetNumber(1);
                args.SetReturn(a < b ? a : b);
            });

            table.Register(GameConstants.SYS_MAX, "max", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var a = args.GetNumber(0);
                var b = args.GetNumber(1);
                args.SetReturn(a > b ? a : b);
            });

            // ── VM Instance Management (MI-2, MI-3) ──────────────
            // SpawnScript and KillInstance are registered by GameVMBridge
            // since they need direct access to VMWorld.
        }

        private static Character GetOwner(ref VMInstanceState s)
        {
            int ownerId = ResolveOwnerId(ref s);
            return ownerId >= 0 ? _chars?.Get(ownerId) : null;
        }
    }
}
