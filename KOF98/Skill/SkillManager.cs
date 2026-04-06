namespace KOF98
{
    /// <summary>
    /// Per-character skill management.
    /// Handles skill activation, deactivation, and per-frame processing.
    ///
    /// Design: All character states (idle, walk, hit, block, etc.) are implemented as skills.
    /// The skill manager acts as a state machine driven by input and game events.
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

        /// <summary>Pending skill to activate next frame (set by TryActivateSkill).</summary>
        public SkillDef PendingSkillDef;

        public SkillManager(Character owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Check if the current main skill should be deactivated.
        /// Called at the start of the character update.
        /// </summary>
        public void TryDeactivateSkill()
        {
            if (ActiveSkill == null || !ActiveSkill.IsActive) return;

            // Check CanContinue callback (e.g., walk stops when input released)
            if (ActiveSkill.Def.CanContinue != null
                && !ActiveSkill.Def.CanContinue(_owner, _owner.CurrentInput))
            {
                DeactivateCurrentSkill();
                return;
            }

            // Non-looping skills end when their frames are exhausted
            // (VM-driven skills handle this via script completion)
            if (ActiveSkill.VMInstanceId < 0)
            {
                // Host-driven skill: check frame count
                if (!ActiveSkill.Def.IsLooping && ActiveSkill.Def.TotalFrames > 0
                    && ActiveSkill.Frame >= ActiveSkill.Def.TotalFrames)
                {
                    DeactivateCurrentSkill();
                }
            }
            // VM-driven skills: deactivation handled by VMWorld.Tick() marking
            // the instance as Completed, checked in ProcessSkills().
        }

        /// <summary>
        /// Try to activate a new skill based on character input and state.
        /// Called after TryDeactivateSkill.
        ///
        /// Uses a 4-layer candidate pool (SK2):
        ///   Layer 1: Stance grouping — filter by AllowedStances matching current stance
        ///   Layer 2: Interrupt check — InterruptPriority vs current skill's ActivationPriority
        ///   Layer 3: Condition check — CanActivate callback (host) or VM first-frame (script)
        ///   Layer 4: Priority sorting — pick best by ActivationPriority (ascending = higher priority)
        ///
        /// Legacy skills without AllowedStances fall back to the old flat scan.
        /// </summary>
        public void TryActivateSkill(PlayerInput input)
        {
            // Check pending skill first (force-activation bypass)
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

            // ── Build sorted candidate list (stack-allocated for small counts) ──
            // We use a simple insertion-sort approach since skill catalogs are small (< 20).
            SkillDef best = null;
            int bestActivationPriority = int.MaxValue;

            for (int i = 0; i < skills.Length; i++)
            {
                var def = skills[i];
                if (def == null) continue;

                // ── Layer 1: Stance grouping ──
                if (def.AllowedStances != null && def.AllowedStances.Length > 0)
                {
                    bool stanceMatch = false;
                    for (int s = 0; s < def.AllowedStances.Length; s++)
                    {
                        if (def.AllowedStances[s] == stance) { stanceMatch = true; break; }
                    }
                    if (!stanceMatch) continue;
                }

                // ── Layer 2: Interrupt check ──
                // A new skill can only interrupt the current skill if its InterruptPriority
                // is >= the current skill's ActivationPriority (lower ActivationPriority = harder to interrupt).
                if (hasActiveSkill && def.InterruptPriority < currentActPriority)
                {
                    // Also check legacy Priority for backward compat: if new skill has
                    // higher legacy priority, allow it through.
                    if (def.Priority <= (ActiveSkill.Def?.Priority ?? -1))
                        continue;
                }

                // ── Layer 3: Condition check (host callback) ──
                if (def.CanActivate != null && !def.CanActivate(_owner, input))
                    continue;

                // ── Layer 4: Priority sorting — pick lowest ActivationPriority ──
                // Among candidates that pass all filters, pick the one with lowest
                // ActivationPriority (= highest priority). Ties broken by legacy Priority (higher wins).
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

            ActiveSkill = new SkillInstance(def, _owner.Id);

            // Apply skill tags
            _owner.ClearAllTags();
            _owner.ActiveTags = def.Tags;

            // VM instance creation is handled by GameVMBridge
        }

        /// <summary>Deactivate the current main skill.</summary>
        public void DeactivateCurrentSkill()
        {
            if (ActiveSkill == null) return;

            // VM instance cleanup is handled by GameVMBridge (Kill → defer)
            ActiveSkill.IsActive = false;
            ActiveSkill = null;

            _owner.ClearAllTags();
            _owner.ClearHitBoxes();
            _owner.ClearHurtBoxes();
        }

        /// <summary>
        /// Process all active skills for this frame.
        /// Advances frame counters and updates collision boxes from action data.
        /// </summary>
        public void EnterFrame()
        {
            if (ActiveSkill == null || !ActiveSkill.IsActive) return;

            // Invoke per-frame callback (e.g., set walk velocity, apply jump physics)
            ActiveSkill.Def.OnFrame?.Invoke(_owner, _owner.CurrentInput);

            // Update collision boxes from skill's action data
            UpdateCollisionBoxes(ActiveSkill);
        }

        /// <summary>
        /// Update character collision boxes based on the active skill's current frame.
        /// </summary>
        private void UpdateCollisionBoxes(SkillInstance skill)
        {
            var frames = skill.Def.CollisionFrames;
            if (frames == null || frames.Length == 0) return;

            _owner.HitBoxCount = 0;
            // Keep existing hurtbox if no skill-specific hurtbox is defined
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
            ActiveSkill = null;
            SubSkillCount = 0;
            PendingSkillDef = null;
        }
    }
}
