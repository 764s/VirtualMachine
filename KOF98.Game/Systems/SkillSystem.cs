namespace KOF98.Game
{
    /// <summary>
    /// Skill state-machine driver, ECS edition.
    ///
    /// Replaces the per-character SkillManager with a stateless system that
    /// operates on entity slots in a <see cref="GameWorld"/> using the
    /// <see cref="SkillComponent"/> for storage.
    ///
    /// Responsibilities are split into ordered steps that mirror the original
    /// 12-step frame loop: select &amp; activate → enter-frame collision boxes
    /// → tick behaviors and advance frame counters → status countdown.
    /// </summary>
    public static class SkillSystem
    {
        // ── Step 3: skill selection + idle fallback ──────────────

        public static void SelectAndActivate(GameWorld world)
        {
            var w = world;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;
                if (!w.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsEntityFrozen(w, e)) continue;

                TryActivate(w, e, w.Input[e].Current);

                if (!w.Skill[e].IsSkillActive)
                {
                    var loadout = GameCatalog.GetCharacterSkillLoadout(w.Identity[e].CharacterId);
                    if (loadout.IdleSkillIndex >= 0
                        && loadout.IdleSkillIndex < (loadout.SkillIds?.Length ?? 0))
                    {
                        Activate(w, e, loadout.SkillIds[loadout.IdleSkillIndex]);
                    }
                }
            }
        }

        public static void TryActivate(GameWorld w, int entity, PlayerInput input)
        {
            // Pending (force-activation bypass)
            if (w.Skill[entity].PendingSkillId >= 0)
            {
                int pendingId = w.Skill[entity].PendingSkillId;
                w.Skill[entity].PendingSkillId = GameCatalog.InvalidId;
                Activate(w, entity, pendingId);
                return;
            }

            var loadout = GameCatalog.GetCharacterSkillLoadout(w.Identity[entity].CharacterId);
            if (loadout.SkillIds == null || loadout.SkillIds.Length == 0) return;

            ref var skill = ref w.Skill[entity];
            var stance = w.GetStance(entity);
            var activeDef = GameCatalog.GetSkill(skill.ActiveSkillId);
            int currentActPriority = activeDef?.ActivationPriority ?? int.MaxValue;
            bool hasActiveSkill = skill.IsSkillActive && activeDef != null;

            SkillDef best = null;
            int bestSkillId = GameCatalog.InvalidId;
            int bestActivationPriority = int.MaxValue;

            var actCtx = new SkillActivationContext(w, entity, input);

            var skillIds = loadout.SkillIds;
            for (int i = 0; i < skillIds.Length; i++)
            {
                int sid = skillIds[i];
                var def = GameCatalog.GetSkill(sid);
                if (def == null) continue;
                if (activeDef != null && sid == skill.ActiveSkillId) continue;

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
                if (hasActiveSkill && def.InterruptPriority > currentActPriority)
                {
                    if (def.Priority <= (activeDef?.Priority ?? -1))
                        continue;
                }

                // Layer 3: condition check.
                if (def.CanActivate != null && !def.CanActivate(actCtx))
                    continue;

                // Layer 4: priority sorting.
                if (def.ActivationPriority < bestActivationPriority
                    || (def.ActivationPriority == bestActivationPriority
                        && (best == null || def.Priority > best.Priority)))
                {
                    best = def;
                    bestSkillId = sid;
                    bestActivationPriority = def.ActivationPriority;
                }
            }

            if (bestSkillId >= 0)
                Activate(w, entity, bestSkillId);
        }

        public static void Activate(GameWorld w, int entity, int skillId)
        {
            Deactivate(w, entity);

            var def = GameCatalog.GetSkill(skillId);
            if (def == null || def.BehaviorFactory == null) return;

            var behavior = def.BehaviorFactory();
            ref var skill = ref w.Skill[entity];
            skill.ActiveSkillId = skillId;
            skill.SkillFrame = 0;
            skill.IsSkillActive = true;
            skill.SkillMutexFlags = 0;
            w.ActiveBehaviors[entity] = behavior;

            w.ClearAllTags(entity);
            w.Tags[entity].ActiveTags = def.Tags;

            var ctx = new SkillContext(w, entity);
            behavior.Spawn(ctx);
        }

        public static void Deactivate(GameWorld w, int entity)
        {
            ref var skill = ref w.Skill[entity];
            if (!skill.IsSkillActive) return;

            var behavior = w.ActiveBehaviors[entity];
            if (behavior != null)
            {
                var ctx = new SkillContext(w, entity);
                behavior.Kill(ctx);
            }

            ClearActiveSkill(ref skill);
            w.ActiveBehaviors[entity] = null;
            w.ClearAllTags(entity);
            w.ClearHitBoxes(entity);
            w.ClearHurtBoxes(entity);
        }

        private static void ClearActiveSkill(ref SkillComponent skill)
        {
            skill.ActiveSkillId = GameCatalog.InvalidId;
            skill.SkillFrame = 0;
            skill.IsSkillActive = false;
            skill.SkillMutexFlags = 0;
        }

        public static void ClearForRound(GameWorld world, int entity)
        {
            // Round reset path: we deliberately do NOT run Kill here so a
            // skill behavior cannot push side-effects into a round that has
            // already ended. The caller is expected to also reset tags/boxes.
            world.Skill[entity] = default;
            world.Skill[entity].ActiveSkillId = GameCatalog.InvalidId;
            world.Skill[entity].PendingSkillId = GameCatalog.InvalidId;
            world.ActiveBehaviors[entity] = null;
        }

        // ── Step 4: enter-frame collision box update ─────────────

        public static void UpdateCollisionBoxes(GameWorld world)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Character) continue;
                if (!world.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsEntityFrozen(world, e)) continue;

                ref var skill = ref world.Skill[e];
                if (!skill.IsSkillActive) continue;
                var def = GameCatalog.GetSkill(skill.ActiveSkillId);
                if (def == null) continue;

                var frames = def.CollisionFrames;
                if (frames == null || frames.Length == 0) continue;

                world.HitBoxCounts[e] = 0;
                bool hasSkillHurtbox = false;

                for (int i = 0; i < frames.Length; i++)
                {
                    ref var cf = ref frames[i];
                    if (skill.SkillFrame < cf.StartFrame || skill.SkillFrame >= cf.EndFrame) continue;

                    switch (cf.BoxType)
                    {
                        case CollisionBoxType.Hitbox:
                            if (world.HitBoxCounts[e] < GameWorld.MaxHitBoxesPerEntity)
                            {
                                world.HitBox(e, world.HitBoxCounts[e]++)
                                    = new HitBoxEntry(cf.GroupId, cf.Box);
                            }
                            break;
                        case CollisionBoxType.Hurtbox:
                            if (!hasSkillHurtbox)
                            {
                                world.HurtBoxCounts[e] = 0;
                                hasSkillHurtbox = true;
                            }
                            if (world.HurtBoxCounts[e] < GameWorld.MaxHurtBoxesPerEntity)
                            {
                                world.HurtBox(e, world.HurtBoxCounts[e]++) = cf.Box;
                            }
                            break;
                        case CollisionBoxType.Blockbox:
                            world.BlockBoxes[e] = cf.Box;
                            break;
                        case CollisionBoxType.Pushbox:
                            world.PushBoxes[e] = cf.Box;
                            break;
                    }
                }
            }
        }

        // ── Step 7: tick behaviors, advance frame, status countdown ─

        public static void TickAndAdvance(GameWorld w)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;
                if (!w.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsEntityFrozen(w, e)) continue;

                ref var skill = ref w.Skill[e];
                if (skill.IsSkillActive)
                {
                    var def = GameCatalog.GetSkill(skill.ActiveSkillId);
                    var behavior = w.ActiveBehaviors[e];
                    if (def != null && behavior != null)
                    {
                        var ctx = new SkillContext(w, e);
                        var result = behavior.Tick(ctx);

                        if (result == SkillTickResult.Completed)
                        {
                            Deactivate(w, e);
                        }
                        else if (!def.IsLooping
                            && def.TotalFrames > 0
                            && skill.SkillFrame >= def.TotalFrames)
                        {
                            Deactivate(w, e);
                        }
                    }
                }

                // Frame counter advance for collision-frame tracking.
                if (w.Skill[e].IsSkillActive) w.Skill[e].SkillFrame++;

                // Status countdown is gated by the character frame line so
                // a paused character does not bleed hitstun / blockstun.
                if (w.Status[e].HitstunFrames > 0) w.Status[e].HitstunFrames--;
                if (w.Status[e].BlockstunFrames > 0) w.Status[e].BlockstunFrames--;
            }
        }
    }
}
