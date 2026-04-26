using System.Collections.Generic;

namespace KOF98.Game
{
    /// <summary>
    /// Main game scene. Orchestrates all game systems for a single fight.
    ///
    /// Frame execution order (matches the historical KOF98 host order, with
    /// the VM-tick step replaced by per-character behavior ticking):
    ///   1. Process scene commands
    ///   2. Apply character inputs (player + AI)
    ///   3. Skill state transitions (deactivate → activate, fall back to idle)
    ///   4. Enter frame: update collision boxes from skill data
    ///   5. Physics integrate + stage clamp
    ///   6. Pushbox resolution
    ///   7. Tick active skill behaviors (mirrors VMWorld.Tick across all instances)
    ///   8. Combat: resolve pending hit events
    ///   9. Effects update
    ///  10. Projectiles update
    ///  11. Auto-face opponents
    ///  12. Round check + advance frame
    /// </summary>
    public class GameScene
    {
        // ── Subsystems ───────────────────────────────────────────
        public CharacterManager Characters { get; } = new CharacterManager();
        public CollisionSystem Collision { get; } = new CollisionSystem();
        public CombatSystem Combat { get; } = new CombatSystem();
        public EffectManager Effects { get; } = new EffectManager();
        public ProjectileManager Projectiles { get; } = new ProjectileManager();

        // ── AI Controllers ───────────────────────────────────────
        private readonly Dictionary<int, IAIController> _aiControllers = new();

        // ── Frame State ──────────────────────────────────────────
        public int FrameNumber { get; private set; }
        public int RoundNumber { get; private set; } = 1;
        public bool IsPaused { get; set; }
        public bool IsRoundOver { get; private set; }
        public int WinnerId { get; private set; } = -1;

        // ── Main Frame Step ──────────────────────────────────────

        public void Step(SceneInput input)
        {
            if (IsPaused) return;

            // 1. Process scene commands
            for (int i = 0; i < input.Commands.Count; i++)
                input.Commands[i].Execute(this);

            // 2. Apply character inputs
            ApplyInputs(input);

            // 3. Skill state transitions
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null || !ch.IsAlive) continue;

                ch.SkillMgr.Scene = this;
                ch.SkillMgr.TryActivateSkill(ch.CurrentInput);

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

            // 5. Physics integrate
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null) continue;
                ch.Body.Step();
                ch.Body.ClampToStage();
            }

            // 6. Pushbox resolution
            Collision.ResolvePushBoxes(Characters);

            // 7. Tick all active skill behaviors
            for (int i = 0; i < Characters.Count; i++)
            {
                var ch = Characters.Characters[i];
                if (ch == null || !ch.IsAlive) continue;

                ch.SkillMgr.Scene = this;
                ch.SkillMgr.TickActiveBehavior();

                // Advance frame counter for collision-frame tracking.
                if (ch.SkillMgr.ActiveSkill != null)
                {
                    ch.SkillMgr.ActiveSkill.Frame++;
                }

                if (ch.HitstunFrames > 0) ch.HitstunFrames--;
                if (ch.BlockstunFrames > 0) ch.BlockstunFrames--;
            }

            // 8. Combat
            Combat.ProcessHits(Characters);

            // 9. Effects
            Effects.Update();

            // 10. Projectiles
            Projectiles.Update();

            // 11. Auto-face
            AutoFaceOpponents();

            // 12. Round check
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
                    var winner = Characters.FindNearestOpponent(ch.Id);
                    WinnerId = winner?.Id ?? -1;
                    return;
                }
            }

            if (FrameNumber >= GameConstants.MaxRoundFrames)
            {
                IsRoundOver = true;
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
