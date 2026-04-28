using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// Builds character data populated with the CS-simulation skill catalog
    /// (Idle, WalkForward, WalkBackward). Mirrors the layered candidate-pool
    /// activation guards used by the FFVM skill scripts.
    /// </summary>
    public static class CsSimSkillCatalog
    {
        // Stable local skill IDs.
        public const int SKILL_IDLE = 0;
        public const int SKILL_WALK_FWD = 1;
        public const int SKILL_WALK_BACK = 2;

        public static CharacterData BuildDefaultCharacterData(int id, string name)
        {
            var data = new CharacterData { Id = id, Name = name };

            var idle = new SkillDef(SKILL_IDLE, "idle",
                totalFrames: -1, priority: GameConstants.PRIORITY_IDLE,
                tags: 1 << GameConstants.TAG_IDLE, looping: true)
            {
                BehaviorFactory = () => new IdleBehavior(),
                AllowedStances = new[] { Stance.Grounded, Stance.Crouching },
                ActivationPriority = 1000,   // weakest — only chosen by idle fallback
                InterruptPriority = 1000,
                CanActivate = ctx => ctx.IsAlive && ctx.IsGrounded,
            };

            var walkFwd = new SkillDef(SKILL_WALK_FWD, "walk_fwd",
                totalFrames: -1, priority: GameConstants.PRIORITY_MOVEMENT,
                tags: 1 << GameConstants.TAG_WALK, looping: true)
            {
                BehaviorFactory = () => new WalkForwardBehavior(),
                AllowedStances = new[] { Stance.Grounded },
                ActivationPriority = 200,
                InterruptPriority = 200,
                CanActivate = ctx => ctx.IsAlive
                                     && ctx.IsGrounded
                                     && !ctx.Input.IsHeld(InputButton.Up)
                                     && !ctx.Input.IsHeld(InputButton.Down)
                                     && ctx.Input.GetForwardDir(ctx.Facing) > 0,
            };

            var walkBack = new SkillDef(SKILL_WALK_BACK, "walk_back",
                totalFrames: -1, priority: GameConstants.PRIORITY_MOVEMENT,
                tags: 1 << GameConstants.TAG_WALK, looping: true)
            {
                BehaviorFactory = () => new WalkBackwardBehavior(),
                AllowedStances = new[] { Stance.Grounded },
                ActivationPriority = 200,
                InterruptPriority = 200,
                CanActivate = ctx => ctx.IsAlive
                                     && ctx.IsGrounded
                                     && !ctx.Input.IsHeld(InputButton.Up)
                                     && !ctx.Input.IsHeld(InputButton.Down)
                                     && ctx.Input.GetForwardDir(ctx.Facing) < 0,
            };

            data.Skills = new[] { idle, walkFwd, walkBack };
            data.IdleSkillIndex = SKILL_IDLE;
            return data;
        }
    }
}
