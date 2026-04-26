namespace KOF98.Game
{
    /// <summary>
    /// Per-character skill management.
    /// Handles skill activation, deactivation, and per-frame ticking of the
    /// active <see cref="ISkillBehavior"/>.
    ///
    /// Design: All character states (idle, walk, hit, block, etc.) are implemented
    /// as skills. The skill manager acts as a state machine driven by input and
    /// game events, delegating per-frame logic to <see cref="ISkillBehavior.Tick"/>.
    ///
    /// This class is the "skill running framework" — it intentionally lives on
    /// the game layer and is shared by both the CS simulation layer and the
    /// future VM-backed layer.
    /// </summary>
    public class SkillManager
    {
        private readonly Character _owner;

        /// <summary>Currently active main skill (determines character state).</summary>
        public SkillInstance ActiveSkill;

        /// <summary>
        /// Active sub-skills (e.g., ongoing effects spawned by the main skill).
        /// Fixed capacity to avoid allocation.
        /// </summary>
        public SkillInstance[] SubSkills = new SkillInstance[GameConstants.MaxActiveSkillsPerCharacter];
        public int SubSkillCount;

        /// <summary>Pending skill to activate next frame (set by force-activation paths).</summary>
        public SkillDef PendingSkillDef;

        /// <summary>Scene reference, set by GameScene before each tick (used to build SkillContext).</summary>
        internal GameScene Scene;

        public SkillManager(Character owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Tick the active skill behavior for this frame.
        /// If the behavior reports <see cref="SkillTickResult.Completed"/>,
        /// the skill is deactivated (mirrors VM "instance returned" lifecycle).
        /// </summary>
        public void TickActiveBehavior()
        {
            if (ActiveSkill == null || !ActiveSkill.IsActive || ActiveSkill.Behavior == null) return;

            var ctx = new SkillContext(_owner, Scene, ActiveSkill);
            var result = ActiveSkill.Behavior.Tick(ctx);
            if (result == SkillTickResult.Completed)
            {
                DeactivateCurrentSkill();
                return;
            }

            // Non-looping host skills can also end via frame budget.
            if (!ActiveSkill.Def.IsLooping && ActiveSkill.Def.TotalFrames > 0
                && ActiveSkill.Frame >= ActiveSkill.Def.TotalFrames)
            {
                DeactivateCurrentSkill();
            }
        }

        /// <summary>
        /// Try to activate a new skill based on character input and state.
        /// Uses a 4-layer candidate pool:
        ///   Layer 1: Stance grouping
        ///   Layer 2: Interrupt check
        ///   Layer 3: Condition check (CanActivate host guard)
        ///   Layer 4: Priority sorting (lowest ActivationPriority wins)
        /// </summary>
        public void TryActivateSkill(PlayerInput input)
        {
            // Pending (force-activation bypass)
            if (PendingSkillDef != null)
            {
                ActivateSkill(PendingSkillDef);
                PendingSkillDef = null;
                return;
            }

            var skills = _owner.Data.Skills;
            if (skills.Length == 0) return;

            var stance = _owner.GetStance();
            int currentActPriority = ActiveSkill?.Def?.ActivationPriority ?? int.MaxValue;
            bool hasActiveSkill = ActiveSkill != null && ActiveSkill.IsActive;

            SkillDef best = null;
            int bestActivationPriority = int.MaxValue;

            for (int i = 0; i < skills.Length; i++)
            {
                var def = skills[i];
                if (def == null) continue;

                // Skip re-activation of the currently active skill.
                if (ActiveSkill != null && def == ActiveSkill.Def) continue;

                // Layer 1: stance grouping.
                if (def.AllowedStances != null && def.AllowedStances.Length > 0)
                {
                    bool stanceMatch = false;
                    for (int s = 0; s < def.AllowedStances.Length; s++)
                    {
                        if (def.AllowedStances[s] == stance) { stanceMatch = true; break; }
                    }
                    if (!stanceMatch) continue;
                }

                // Layer 2: interrupt check (numerically lower = stronger).
                // A candidate may interrupt the current skill only if its
                // InterruptPriority is <= the current skill's ActivationPriority.
                if (hasActiveSkill && def.InterruptPriority > currentActPriority)
                {
                    // Not directly allowed — fall back to legacy Priority field.
                    if (def.Priority <= (ActiveSkill.Def?.Priority ?? -1))
                        continue;
                }

                // Layer 3: condition check.
                if (def.CanActivate != null && !def.CanActivate(_owner, input))
                    continue;

                // Layer 4: priority sorting.
                if (def.ActivationPriority < bestActivationPriority
                    || (def.ActivationPriority == bestActivationPriority
                        && (best == null || def.Priority > best.Priority)))
                {
                    best = def;
                    bestActivationPriority = def.ActivationPriority;
                }
            }

            if (best != null)
                ActivateSkill(best);
        }

        /// <summary>Activate a specific skill, deactivating the current one.</summary>
        public void ActivateSkill(SkillDef def)
        {
            DeactivateCurrentSkill();

            if (def.BehaviorFactory == null)
            {
                // Misconfigured skill — refuse to activate rather than crash silently.
                return;
            }

            var behavior = def.BehaviorFactory();
            ActiveSkill = new SkillInstance(def, _owner.Id, behavior);

            _owner.ClearAllTags();
            _owner.ActiveTags = def.Tags;

            var ctx = new SkillContext(_owner, Scene, ActiveSkill);
            behavior.Spawn(ctx);
        }

        /// <summary>Deactivate the current main skill.</summary>
        public void DeactivateCurrentSkill()
        {
            if (ActiveSkill == null) return;

            var behavior = ActiveSkill.Behavior;
            if (behavior != null)
            {
                var ctx = new SkillContext(_owner, Scene, ActiveSkill);
                behavior.Kill(ctx);
            }

            ActiveSkill.IsActive = false;
            ActiveSkill = null;

            _owner.ClearAllTags();
            _owner.ClearHitBoxes();
            _owner.ClearHurtBoxes();
        }

        /// <summary>
        /// Update collision boxes from the active skill's static action data.
        /// Called once per frame after the behavior tick.
        /// </summary>
        public void EnterFrame()
        {
            if (ActiveSkill == null || !ActiveSkill.IsActive) return;
            UpdateCollisionBoxes(ActiveSkill);
        }

        private void UpdateCollisionBoxes(SkillInstance skill)
        {
            var frames = skill.Def.CollisionFrames;
            if (frames == null || frames.Length == 0) return;

            _owner.HitBoxCount = 0;
            bool hasSkillHurtbox = false;

            for (int i = 0; i < frames.Length; i++)
            {
                ref var cf = ref frames[i];
                if (skill.Frame < cf.StartFrame || skill.Frame >= cf.EndFrame)
                    continue;

                switch (cf.BoxType)
                {
                    case CollisionBoxType.Hitbox:
                        if (_owner.HitBoxCount < _owner.HitBoxes.Length)
                        {
                            _owner.HitBoxes[_owner.HitBoxCount++] = new HitBoxEntry(cf.GroupId, cf.Box);
                        }
                        break;
                    case CollisionBoxType.Hurtbox:
                        if (!hasSkillHurtbox)
                        {
                            _owner.HurtBoxCount = 0;
                            hasSkillHurtbox = true;
                        }
                        if (_owner.HurtBoxCount < _owner.HurtBoxes.Length)
                        {
                            _owner.HurtBoxes[_owner.HurtBoxCount++] = cf.Box;
                        }
                        break;
                    case CollisionBoxType.Blockbox:
                        _owner.BlockBox = cf.Box;
                        break;
                    case CollisionBoxType.Pushbox:
                        _owner.PushBox = cf.Box;
                        break;
                }
            }
        }

        /// <summary>Clear all active skills (for round reset).</summary>
        public void ClearAll()
        {
            // Don't run Kill on round-reset to keep semantics simple — caller is
            // expected to also reset character tags / boxes.
            ActiveSkill = null;
            SubSkillCount = 0;
            PendingSkillDef = null;
        }
    }
}
