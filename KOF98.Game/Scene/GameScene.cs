using System.Collections.Generic;

namespace KOF98.Game
{
    /// <summary>
    /// Top-level orchestrator. Holds the <see cref="GameWorld"/> and runs the
    /// fixed frame loop. The step order is the gameplay contract: changing
    /// it breaks all skill timing assumptions.
    ///
    /// Two-tier frame line:
    ///   Scene line     — <see cref="FrameNumber"/>/<see cref="GlobalFrame"/>;
    ///                    always advances unless the menu pause holds. Round
    ///                    timer / external commands / input collection ride on
    ///                    the scene line.
    ///   Character line — <see cref="FrameLineComponent"/> per character.
    ///                    A character with <c>PauseFrames &gt; 0</c> is frozen
    ///                    this frame: physics / skill ticks / status decrement
    ///                    / auto-face all skip via per-system gating.
    ///
    /// Step order:
    ///   1.  Apply external commands (spawn / SetAI / etc.).
    ///   2.  Apply per-frame inputs (player + AI).
    ///   --  if (!IsSceneFrozen) {
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
    ///   14. FrameLineSystem.AdvanceScene + FrameNumber++.
    /// </summary>
    public class GameScene
    {
        public readonly GameWorld World = new GameWorld();
        public readonly CombatSystem Combat = new CombatSystem();

        /// <summary>Scene-line frame counter. Alias of <see cref="GlobalFrame"/>.</summary>
        public int FrameNumber;

        /// <summary>Scene-line frame counter (frame line terminology).</summary>
        public int GlobalFrame;

        /// <summary>Remaining global pause frames (time-stop on the scene line).</summary>
        public int GlobalPauseFrames;

        public int RoundNumber;

        /// <summary>Menu / external pause — distinct from in-game time-stop.</summary>
        public bool IsPaused;
        public bool IsRoundOver;

        /// <summary>Entity slot index of the round winner, or -1 if no winner / draw.</summary>
        public int WinnerId = -1;

        private readonly Dictionary<int, IAIController> _aiControllers = new Dictionary<int, IAIController>();

        public GameScene() { }

        // ── Frame line API ───────────────────────────────────────

        /// <summary>True while the scene line is frozen by an in-game time-stop.</summary>
        public bool IsSceneFrozen => GlobalPauseFrames > 0;

        /// <summary>True if the given character entity is frozen this frame.</summary>
        public bool IsCharacterFrozen(int entity)
            => IsSceneFrozen || FrameLineSystem.IsCharacterPaused(World, entity);

        /// <summary>Pause a single character for <paramref name="frames"/> frames (max-stacked).</summary>
        public void PauseCharacter(int entity, int frames)
            => FrameLineSystem.PauseCharacter(World, entity, frames);

        /// <summary>Pause the entire scene line for <paramref name="frames"/> frames (max-stacked).</summary>
        public void PauseScene(int frames)
        {
            if (frames <= 0) return;
            if (frames > GlobalPauseFrames) GlobalPauseFrames = frames;
        }

        // ── Frame loop ───────────────────────────────────────────

        public void Step(SceneInput input)
        {
            // Step 1: external commands always run.
            if (input != null)
            {
                for (int i = 0; i < input.Commands.Count; i++)
                    input.Commands[i].Apply(this);
                input.Commands.Clear();
            }

            if (IsPaused || IsRoundOver) return;

            // Step 2: input collection always runs (preserves pre-input during time-stop).
            InputSystem.Apply(this, input ?? new SceneInput(), _aiControllers);

            if (!IsSceneFrozen)
            {
                // Step 3-6.
                SkillSystem.SelectAndActivate(this);
                SkillSystem.UpdateCollisionBoxes(World);
                AutoFaceSystem.Run(World);
                PhysicsSystem.Step(World);

                // Step 7: tick skills + advance skill Frame + decrement status.
                SkillSystem.TickAndAdvance(this);

                // Step 8: combat (hits enqueued by behaviors during Tick).
                Combat.ProcessHits(this);

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
            FrameLineSystem.AdvanceScene(this);
            FrameNumber = GlobalFrame;
        }

        // ── Round management ─────────────────────────────────────

        public void ResetRound()
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!World.IsAliveSlot(e) || World.Kinds[e] != EntityKind.Character) continue;

                World.Life[e].HP = World.Identity[e].Data?.MaxHP ?? GameConstants.DefaultMaxHP;
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

            Combat.PendingHits.Clear();
            FrameNumber = 0;
            GlobalFrame = 0;
            GlobalPauseFrames = 0;
            RoundNumber++;
            IsRoundOver = false;
            WinnerId = -1;
        }

        public void SetAI(int charEntity, IAIController ai)
        {
            if (ai == null) _aiControllers.Remove(charEntity);
            else _aiControllers[charEntity] = ai;
        }

        public bool IsAIControlled(int charEntity) => _aiControllers.ContainsKey(charEntity);

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
