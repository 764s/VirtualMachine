using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// Builds character data populated with the CS-simulation skill catalog
    /// (Idle, WalkForward, WalkBackward) and registers all defs with
    /// <see cref="GameCatalog"/> so the ECS world can reference them by id.
    /// </summary>
    public static class CsSimSkillCatalog
    {
        // Local indexes into CharacterSkillLoadoutDef.SkillIds.
        public const int LOCAL_IDLE = 0;
        public const int LOCAL_WALK_FWD = 1;
        public const int LOCAL_WALK_BACK = 2;

        public static CharacterData BuildDefaultCharacterData(string name)
        {
            var data = new CharacterData { Name = name };

            var idle = new SkillDef(0, "idle",
                totalFrames: -1, priority: GameConstants.PRIORITY_IDLE,
                tags: 1 << GameConstants.TAG_IDLE, looping: true)
            {
                BehaviorFactory = () => new IdleBehavior(),
                AllowedStances = new[] { Stance.Grounded, Stance.Crouching },
                ActivationPriority = 1000,   // weakest — only chosen by idle fallback
                InterruptPriority = 1000,
                CanActivate = ctx => ctx.IsAlive && ctx.IsGrounded,
            };

            var walkFwd = new SkillDef(0, "walk_fwd",
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

            var walkBack = new SkillDef(0, "walk_back",
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

            int idleId = GameCatalog.RegisterSkill(idle);
            int walkFwdId = GameCatalog.RegisterSkill(walkFwd);
            int walkBackId = GameCatalog.RegisterSkill(walkBack);

            var stats = new CharacterStatsDef
            {
                MaxHP = GameConstants.DefaultMaxHP,
                MaxPower = GameConstants.DefaultMaxPower,
            };

            var movement = new CharacterMovementDef
            {
                WalkSpeed = GameConstants.DefaultWalkSpeed,
                BackWalkSpeed = GameConstants.DefaultBackWalkSpeed,
                RunSpeed = GameConstants.DefaultRunSpeed,
                JumpSpeedY = GameConstants.DefaultJumpSpeedY,
            };

            var loadout = new CharacterSkillLoadoutDef
            {
                SkillIds = new[] { idleId, walkFwdId, walkBackId },
                IdleSkillIndex = LOCAL_IDLE,
            };

            GameCatalog.RegisterCharacter(data, stats, movement, loadout);
            return data;
        }
    }
}
