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

        public static void SelectAndActivate(GameScene scene)
        {
            var w = scene.World;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;
                if (!w.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsCharacterPaused(w, e)) continue;

                TryActivate(scene, e, w.Input[e].Current);

                if (w.Skill[e].ActiveSkill == null)
                {
                    var data = w.Identity[e].Data;
                    if (data != null && data.IdleSkillIndex >= 0
                        && data.IdleSkillIndex < (data.Skills?.Length ?? 0))
                    {
                        Activate(scene, e, data.Skills[data.IdleSkillIndex]);
                    }
                }
            }
        }

        public static void TryActivate(GameScene scene, int entity, PlayerInput input)
        {
            var w = scene.World;

            // Pending (force-activation bypass)
            if (w.Skill[entity].PendingSkillDef != null)
            {
                var pending = w.Skill[entity].PendingSkillDef;
                w.Skill[entity].PendingSkillDef = null;
                Activate(scene, entity, pending);
                return;
            }

            var data = w.Identity[entity].Data;
            if (data == null || data.Skills == null || data.Skills.Length == 0) return;

            var stance = w.GetStance(entity);
            var active = w.Skill[entity].ActiveSkill;
            int currentActPriority = active?.Def?.ActivationPriority ?? int.MaxValue;
            bool hasActiveSkill = active != null && active.IsActive;

            SkillDef best = null;
            int bestActivationPriority = int.MaxValue;

            var actCtx = new SkillActivationContext(w, entity, input);

            var skills = data.Skills;
            for (int i = 0; i < skills.Length; i++)
            {
                var def = skills[i];
                if (def == null) continue;
                if (active != null && def == active.Def) continue;

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
                    if (def.Priority <= (active.Def?.Priority ?? -1))
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
                    bestActivationPriority = def.ActivationPriority;
                }
            }

            if (best != null)
                Activate(scene, entity, best);
        }

        public static void Activate(GameScene scene, int entity, SkillDef def)
        {
            Deactivate(scene, entity);

            if (def == null || def.BehaviorFactory == null) return;

            var w = scene.World;
            var behavior = def.BehaviorFactory();
            var inst = new SkillInstance(def, entity, behavior);

            w.Skill[entity].ActiveSkill = inst;
            w.ClearAllTags(entity);
            w.Tags[entity].ActiveTags = def.Tags;

            var ctx = new SkillContext(w, scene, entity, inst);
            behavior.Spawn(ctx);
        }

        public static void Deactivate(GameScene scene, int entity)
        {
            var w = scene.World;
            var inst = w.Skill[entity].ActiveSkill;
            if (inst == null) return;

            if (inst.Behavior != null)
            {
                var ctx = new SkillContext(w, scene, entity, inst);
                inst.Behavior.Kill(ctx);
            }

            inst.IsActive = false;
            w.Skill[entity].ActiveSkill = null;
            w.ClearAllTags(entity);
            w.ClearHitBoxes(entity);
            w.ClearHurtBoxes(entity);
        }

        public static void ClearForRound(GameWorld world, int entity)
        {
            // Round reset path: we deliberately do NOT run Kill here so a
            // skill behavior cannot push side-effects into a round that has
            // already ended. The caller is expected to also reset tags/boxes.
            world.Skill[entity] = default;
        }

        // ── Step 4: enter-frame collision box update ─────────────

        public static void UpdateCollisionBoxes(GameWorld world)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Character) continue;
                if (!world.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsCharacterPaused(world, e)) continue;

                var inst = world.Skill[e].ActiveSkill;
                if (inst == null || !inst.IsActive) continue;

                var frames = inst.Def.CollisionFrames;
                if (frames == null || frames.Length == 0) continue;

                world.HitBoxCounts[e] = 0;
                bool hasSkillHurtbox = false;

                for (int i = 0; i < frames.Length; i++)
                {
                    ref var cf = ref frames[i];
                    if (inst.Frame < cf.StartFrame || inst.Frame >= cf.EndFrame) continue;

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

        public static void TickAndAdvance(GameScene scene)
        {
            var w = scene.World;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;
                if (!w.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsCharacterPaused(w, e)) continue;

                var inst = w.Skill[e].ActiveSkill;
                if (inst != null && inst.IsActive && inst.Behavior != null)
                {
                    var ctx = new SkillContext(w, scene, e, inst);
                    var result = inst.Behavior.Tick(ctx);

                    if (result == SkillTickResult.Completed)
                    {
                        Deactivate(scene, e);
                    }
                    else if (!inst.Def.IsLooping
                        && inst.Def.TotalFrames > 0
                        && inst.Frame >= inst.Def.TotalFrames)
                    {
                        Deactivate(scene, e);
                    }
                }

                // Frame counter advance for collision-frame tracking.
                var advInst = w.Skill[e].ActiveSkill;
                if (advInst != null) advInst.Frame++;

                // Status countdown is gated by the character frame line so
                // a paused character does not bleed hitstun / blockstun.
                if (w.Status[e].HitstunFrames > 0) w.Status[e].HitstunFrames--;
                if (w.Status[e].BlockstunFrames > 0) w.Status[e].BlockstunFrames--;
            }
        }
    }
}
