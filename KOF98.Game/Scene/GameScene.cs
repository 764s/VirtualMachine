namespace KOF98.Game
{
    /// <summary>
    /// Top-level orchestrator. Holds the <see cref="GameWorld"/> and runs the
    /// fixed frame loop. The step order is the gameplay contract: changing
    /// it breaks all skill timing assumptions.
    ///
    /// Two-tier frame line:
    ///   Scene line     — <see cref="GameWorld.SceneFrameLine"/>. Round timer /
    ///                    external commands / input collection ride on the scene line.
    ///   Character line — <see cref="FrameLineComponent"/> per character.
    ///                    A character with <c>PauseFrames &gt; 0</c> is frozen
    ///                    this frame: physics / skill ticks / status decrement
    ///                    / auto-face all skip via per-system gating.
    ///
    /// Step order:
    ///   1.  Apply external commands (spawn / SetAI / etc.).
    ///   2.  Apply per-frame inputs (player + AI).
    ///   --  if (!FrameLineSystem.IsScenePaused(World)) {
    ///   3.    Skill selection &amp; activation (with idle fallback).
    ///   4.    Update collision boxes for the new frame.
    ///   5.    Auto-face nearest opponent.
    ///   6.    Physics integration.
    ///   7.    Tick skill behaviors, advance skill Frame, decrement status timers.
    ///   8.    Hit detection &amp; combat resolution.
    ///   9.    Pushbox resolution.
    ///   10.   Projectile update.
    ///   11.   Effect update.
    ///   12.   Round-over check.
    ///   13.   FrameLineSystem.AdvanceCharacters(world).
    ///   --  }
    ///   14. FrameLineSystem.AdvanceScene.
    /// </summary>
    public class GameScene
    {
        public readonly GameWorld World = new GameWorld();

        /// <summary>Compatibility alias for the scene-line frame counter.</summary>
        public int FrameNumber => World.SceneFrameLine.GlobalFrame;

        // Round-state delegating accessors (state lives on World.RoundState).
        public int RoundNumber
        {
            get => World.RoundState.RoundNumber;
            set => World.RoundState.RoundNumber = value;
        }

        /// <summary>Menu / external pause — distinct from in-game time-stop.</summary>
        public bool IsPaused
        {
            get => World.RoundState.IsPaused;
            set => World.RoundState.IsPaused = value;
        }

        public bool IsRoundOver
        {
            get => World.RoundState.IsRoundOver;
            set => World.RoundState.IsRoundOver = value;
        }

        /// <summary>Entity slot index of the round winner, or -1 if no winner / draw.</summary>
        public int WinnerId
        {
            get => World.RoundState.WinnerSlot;
            set => World.RoundState.WinnerSlot = value;
        }

        public GameScene() { }

        // ── Frame loop ───────────────────────────────────────────

        public void Step(SceneInput input)
        {
            // Step 1: external commands always run.
            if (input != null)
            {
                for (int i = 0; i < input.CommandCount; i++)
                    input.Commands[i].Apply(this);
                input.ClearCommands();
            }

            if (IsPaused || IsRoundOver) return;

            // Step 2: input collection always runs (preserves pre-input during time-stop).
            InputSystem.Apply(World, input ?? new SceneInput());

            if (!FrameLineSystem.IsScenePaused(World))
            {
                // Step 3-6.
                SkillSystem.SelectAndActivate(World);
                SkillSystem.UpdateCollisionBoxes(World);
                AutoFaceSystem.Run(World);
                PhysicsSystem.Step(World);

                // Step 7: tick skills + advance skill Frame + decrement status.
                SkillSystem.TickAndAdvance(World);

                // Step 8: combat (hits enqueued by behaviors during Tick).
                CombatSystem.ProcessHits(World);

                // Step 9-11: pushbox / projectiles / effects.
                CollisionSystem.ResolvePushBoxes(World);
                ProjectileSystem.Update(World);
                EffectSystem.Update(World);

                // Step 12: round check.
                CheckRoundOver();

                // Step 13: per-character frame line.
                FrameLineSystem.AdvanceCharacters(World);
            }

            // Step 14: scene line always advances.
            FrameLineSystem.AdvanceScene(World);
        }

        // ── Round management ─────────────────────────────────────

        public void ResetRound()
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!World.IsAliveSlot(e) || World.Kinds[e] != EntityKind.Character) continue;

                World.Life[e].HP = GameCatalog.GetCharacterStats(World.Identity[e].CharacterId).MaxHP;
                World.Life[e].Power = 0f;
                World.Life[e].IsAlive = true;
                World.Status[e] = default;
                World.Physics[e].Velocity = FVec2.Zero;
                World.Physics[e].Acceleration = FVec2.Zero;
                World.Physics[e].IsGrounded = true;
                World.ClearAllTags(e);
                World.ClearHitBoxes(e);
                World.FrameLine[e] = default;
                SkillSystem.ClearForRound(World, e);

                // Restore default position based on team.
                int team = World.Identity[e].Team;
                float x = team == 0 ? -2f : 2f;
                World.Transform[e].Position = new FVec2(x, 0f);
                World.Transform[e].Facing = team == 0 ? Direction.Right : Direction.Left;
            }

            // Clear projectiles + effects.
            for (int e = World.NonCharacterSlotStart; e < GameWorld.MaxEntities; e++)
            {
                if (World.IsAliveSlot(e)) World.DestroyAt(e);
            }

            CombatSystem.Clear(World);
            World.SceneFrameLine = default;
            RoundNumber++;
            IsRoundOver = false;
            WinnerId = -1;
        }

        public void SetAI(int charEntity, AIKind kind)
        {
            if (charEntity < 0 || charEntity >= GameConstants.MaxCharacters) return;
            World.AIKinds[charEntity] = kind;
            World.AIState[charEntity] = default;
        }

        public bool IsAIControlled(int charEntity)
            => charEntity >= 0 && charEntity < GameConstants.MaxCharacters
                && World.AIKinds[charEntity] != AIKind.None;

        private void CheckRoundOver()
        {
            int aliveTeam = -1;
            int aliveTeams = 0;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!World.IsAliveSlot(e) || World.Kinds[e] != EntityKind.Character) continue;
                if (!World.Life[e].IsAlive) continue;

                int team = World.Identity[e].Team;
                if (aliveTeam < 0) { aliveTeam = team; aliveTeams = 1; }
                else if (aliveTeam != team) { aliveTeams = 2; break; }
            }

            if (aliveTeams <= 1)
            {
                IsRoundOver = true;
                WinnerId = -1;
                if (aliveTeams == 1)
                {
                    for (int e = 0; e < GameConstants.MaxCharacters; e++)
                    {
                        if (World.IsAliveSlot(e)
                            && World.Kinds[e] == EntityKind.Character
                            && World.Life[e].IsAlive
                            && World.Identity[e].Team == aliveTeam)
                        {
                            WinnerId = e;
                            break;
                        }
                    }
                }
            }
        }
    }
}
