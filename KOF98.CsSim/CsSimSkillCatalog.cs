using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// Builders for <see cref="SkillDef"/> entries backed by the CS-simulation
    /// behaviors in this library. The metadata (priority / tags / stances /
    /// activation guard) mirrors the corresponding FFS skill scripts so that
    /// when the VM layer is introduced, swapping <see cref="SkillDef.BehaviorFactory"/>
    /// for a VM-instance wrapper yields equivalent gameplay.
    ///
    /// Three skills supported in this initial CS-sim layer:
    ///   - Idle           ↔ skill_idle.ffs
    ///   - WalkForward    ↔ skill_walk_forward.ffs (forward branch)
    ///   - WalkBackward   ↔ skill_walk_forward.ffs (backward branch)
    /// </summary>
    public static class CsSimSkillCatalog
    {
        public const int IdleId = 0;
        public const int WalkForwardId = 1;
        public const int WalkBackwardId = 2;

        /// <summary>Idle: looping fallback skill, lowest activation priority.</summary>
        public static SkillDef BuildIdle() => new SkillDef
        {
            Id = IdleId,
            Name = "Idle",
            TotalFrames = -1,
            Priority = GameConstants.PRIORITY_IDLE,
            Tags = 1 << GameConstants.TAG_IDLE,
            IsLooping = true,
            ActivationPriority = 900,
            InterruptPriority = 900,
            AllowedStances = new[] { Stance.Grounded },
            BehaviorFactory = () => new IdleBehavior(),
            // No CanActivate — idle is always a valid candidate when grounded;
            // the candidate-pool layer in SkillManager picks it as fallback.
        };

        /// <summary>WalkForward: activates when forward direction is held while grounded.</summary>
        public static SkillDef BuildWalkForward() => new SkillDef
        {
            Id = WalkForwardId,
            Name = "WalkForward",
            TotalFrames = -1,
            Priority = GameConstants.PRIORITY_MOVEMENT,
            Tags = 1 << GameConstants.TAG_WALK,
            IsLooping = true,
            ActivationPriority = 500,
            InterruptPriority = 500,
            AllowedStances = new[] { Stance.Grounded },
            CanActivate = (ch, input) =>
                ch.IsGrounded
                && !input.IsHeld(InputButton.Up)
                && input.GetForwardDir(ch.Facing) > 0,
            BehaviorFactory = () => new WalkForwardBehavior(),
        };

        /// <summary>WalkBackward: activates when backward direction is held while grounded.</summary>
        public static SkillDef BuildWalkBackward() => new SkillDef
        {
            Id = WalkBackwardId,
            Name = "WalkBackward",
            TotalFrames = -1,
            Priority = GameConstants.PRIORITY_MOVEMENT,
            Tags = 1 << GameConstants.TAG_WALK,
            IsLooping = true,
            ActivationPriority = 500,
            InterruptPriority = 500,
            AllowedStances = new[] { Stance.Grounded },
            CanActivate = (ch, input) =>
                ch.IsGrounded
                && !input.IsHeld(InputButton.Up)
                && input.GetForwardDir(ch.Facing) < 0,
            BehaviorFactory = () => new WalkBackwardBehavior(),
        };

        /// <summary>
        /// Build the default CS-sim character data: a character with Idle,
        /// WalkForward and WalkBackward skills wired up.
        /// </summary>
        public static CharacterData BuildDefaultCharacterData(int id, string name)
        {
            var idle = BuildIdle();
            var walkF = BuildWalkForward();
            var walkB = BuildWalkBackward();

            return new CharacterData
            {
                Id = id,
                Name = name,
                Skills = new[] { idle, walkF, walkB },
                IdleSkillIndex = 0,
            };
        }
    }
}
