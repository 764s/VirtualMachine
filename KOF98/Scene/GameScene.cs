using System.Collections.Generic;

namespace KOF98
{
    /// <summary>
    /// Main game scene. Orchestrates all game systems for a single fight.
    ///
    /// Frame execution order:
    ///   1. Process scene commands (create characters, set AI, etc.)
    ///   2. Collect/apply character inputs (player + AI)
    ///   3. Character update pass 1: skill state transitions (deactivate → activate)
    ///   4. Character update pass 2: enter frame (physics pre-step, collision box update)
    ///   5. Physics: integrate velocities, resolve stage bounds
    ///   6. Physics: resolve pushbox overlaps
    ///   7. Character update pass 3: process skills (run VM instances)
    ///   8. Combat: resolve pending hit events
    ///   9. Effects: update timers, remove expired
    ///  10. Projectiles: move, lifetime, stage bounds
    ///  11. Cleanup: remove dead entities
    ///  12. Advance frame number
    /// </summary>
    public class GameScene
    {
        // ── Subsystems ───────────────────────────────────────────
        public CharacterManager Characters { get; } = new CharacterManager();
        public CollisionSystem Collision { get; } = new CollisionSystem();
        public CombatSystem Combat { get; } = new CombatSystem();
        public EffectManager Effects { get; } = new EffectManager();
        public ProjectileManager Projectiles { get; } = new ProjectileManager();

        // ── VM Bridge (optional — set by Program to enable FFVM) ─
        public GameVMBridge VMBridge { get; set; }

        // ── AI Controllers ───────────────────────────────────────
        private readonly Dictionary<int, IAIController> _aiControllers = new();

        // ── Frame State ──────────────────────────────────────────
        public int FrameNumber { get; private set; }
        public int RoundNumber { get; private set; } = 1;
        public bool IsPaused { get; set; }
        public bool IsRoundOver { get; private set; }
        public int WinnerId { get; private set; } = -1;

        // ── Main Frame Step ──────────────────────────────────────

        /// <summary>
        /// Execute one simulation frame.
        /// This is the core game loop tick — deterministic, no side effects beyond state mutation.
        /// </summary>
        public void Step(SceneInput input)
        {
            if (IsPaused) return;

            // 1. Process scene commands
            for (int i = 0; i < input.Commands.Count; i++)
                input.Commands[i].Execute(this);

            // 2. Apply character inputs (player + AI)
            ApplyInputs(input);

            // 3. Skill state transitions
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null || !ch.IsAlive) continue;

                ch.SkillMgr.TryDeactivateSkill();
                ch.SkillMgr.TryActivateSkill(ch.CurrentInput);

                // If no skill is active, activate idle
                if (ch.SkillMgr.ActiveSkill == null && ch.Data.IdleSkillIndex >= 0)
                {
                    ch.SkillMgr.ActivateSkill(ch.Data.Skills[ch.Data.IdleSkillIndex]);
                }
            }

            // 4. Enter frame: update collision boxes from skill data
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null || !ch.IsAlive) continue;
                ch.SkillMgr.EnterFrame();
            }

            // 5. Physics: integrate
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null) continue;
                ch.Body.Step();
                ch.Body.ClampToStage();
            }

            // 6. Physics: pushbox resolution
            Collision.ResolvePushBoxes(Characters);

            // 7. Process skills (run VM instances)
            VMBridge?.TickVMWorld();
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null || !ch.IsAlive) continue;

                // Advance host-driven skill frames
                if (ch.SkillMgr.ActiveSkill != null && ch.SkillMgr.ActiveSkill.VMInstanceId < 0)
                {
                    ch.SkillMgr.ActiveSkill.AdvanceFrame();
                }

                // Decrement stun timers
                if (ch.HitstunFrames > 0) ch.HitstunFrames--;
                if (ch.BlockstunFrames > 0) ch.BlockstunFrames--;
            }

            // 8. Combat: resolve hits
            Combat.ProcessHits(Characters);

            // 9. Effects update
            Effects.Update();

            // 10. Projectiles update
            Projectiles.Update();

            // 11. Auto-face opponent
            AutoFaceOpponents();

            // 12. Check round over
            CheckRoundOver();

            FrameNumber++;
        }

        // ── Input Handling ───────────────────────────────────────

        private void ApplyInputs(SceneInput input)
        {
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null) continue;

                if (input.CharacterInputs.TryGetValue(ch.Id, out var playerInput))
                {
                    ch.CurrentInput = playerInput;
                }
                else if (_aiControllers.TryGetValue(ch.Id, out var ai))
                {
                    ch.CurrentInput = ai.GetInput(this, ch.Id);
                }
                else
                {
                    ch.CurrentInput = PlayerInput.Empty;
                }
            }
        }

        // ── AI ───────────────────────────────────────────────────

        public void SetAI(int charId, IAIController ai)
        {
            _aiControllers[charId] = ai;
        }

        // ── Auto-face ────────────────────────────────────────────

        private void AutoFaceOpponents()
        {
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null || !ch.IsAlive) continue;

                // Don't auto-face during attacks or hitstun
                if (ch.HasTag(GameConstants.TAG_ATTACK) || ch.HitstunFrames > 0) continue;

                var opp = Characters.FindNearestOpponent(ch.Id);
                if (opp != null)
                {
                    ch.Facing = opp.Body.Position.X > ch.Body.Position.X
                        ? Direction.Right : Direction.Left;
                }
            }
        }

        // ── Round Logic ──────────────────────────────────────────

        private void CheckRoundOver()
        {
            if (IsRoundOver) return;

            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch != null && !ch.IsAlive)
                {
                    IsRoundOver = true;
                    // Find winner (first alive opponent)
                    var winner = Characters.FindNearestOpponent(ch.Id);
                    WinnerId = winner?.Id ?? -1;
                    return;
                }
            }

            // Time over
            if (FrameNumber >= GameConstants.MaxRoundFrames)
            {
                IsRoundOver = true;
                // Winner = character with more HP
                float bestHP = -1;
                for (int i = 0; i < Characters.Count; i++)
                {
                    var ch = Characters.Characters[i];
                    if (ch != null && ch.HP > bestHP)
                    {
                        bestHP = ch.HP;
                        WinnerId = ch.Id;
                    }
                }
            }
        }

        /// <summary>Reset for a new round (keeps characters but resets state).</summary>
        public void ResetRound(FVec2 p1Start, FVec2 p2Start)
        {
            Characters.ResetForRound();
            Effects.Clear();
            Projectiles.Clear();
            Combat.PendingHits.Clear();

            if (Characters.Count >= 1)
                Characters.Characters[0].Body.Position = p1Start;
            if (Characters.Count >= 2)
                Characters.Characters[1].Body.Position = p2Start;

            FrameNumber = 0;
            IsRoundOver = false;
            WinnerId = -1;
            RoundNumber++;
        }
    }
}
