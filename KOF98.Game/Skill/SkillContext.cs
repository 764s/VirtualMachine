namespace KOF98.Game
{
    /// <summary>
    /// Per-call context passed to <see cref="ISkillBehavior"/> methods.
    /// Wraps a (world, entity) pair and exposes the host
    /// capabilities a skill behavior needs — modeled after the GameSyscalls
    /// surface that a future VM instance would have access to.
    /// </summary>
    public struct SkillContext
    {
        public GameWorld World;
        public int Entity;

        public SkillContext(GameWorld world, int entity)
        {
            World = world;
            Entity = entity;
        }

        // ── Static character data ────────────────────────────────

        public CharacterData Data => GameCatalog.Characters[World.Identity[Entity].CharacterId];
        public CharacterMovementDef Movement => GameCatalog.Movements[GameCatalog.Characters[World.Identity[Entity].CharacterId].MovementId];
        public CharacterStatsDef Stats => GameCatalog.Stats[GameCatalog.Characters[World.Identity[Entity].CharacterId].StatsId];
        public int Team => World.Identity[Entity].Team;
        public SkillDef CurrentSkillDef => GameCatalog.GetSkill(World.Skill[Entity].ActiveSkillId);
        public int CurrentSkillFrame => World.Skill[Entity].SkillFrame;

        // ── Input queries ────────────────────────────────────────

        public bool IsInputHeld(InputButton btn) => World.Input[Entity].Current.IsHeld(btn);
        public bool IsInputPressed(InputButton btn) => World.Input[Entity].Current.IsPressed(btn);

        /// <summary>+1 = forward (relative to facing), -1 = backward, 0 = neutral.</summary>
        public int GetInputDir() => World.Input[Entity].Current.GetForwardDir(World.Transform[Entity].Facing);

        // ── Character state ──────────────────────────────────────

        public bool IsGrounded() => World.Physics[Entity].IsGrounded;
        public float GetPosX() => World.Transform[Entity].Position.X;
        public float GetPosY() => World.Transform[Entity].Position.Y;
        public Direction GetFacing() => World.Transform[Entity].Facing;
        public int GetFacingSign() => World.Transform[Entity].FacingSign;

        // ── Character control ────────────────────────────────────

        public void SetVelocity(float vx, float vy)
        {
            World.Physics[Entity].Velocity = new FVec2(vx, vy);
        }

        public void SetFacing(Direction facing)
        {
            World.Transform[Entity].Facing = facing;
        }

        /// <summary>
        /// Raw move direction in world space derived from input + facing.
        /// Returns +1, -1 or 0. Same semantics as common/input.ffs getMoveDirX().
        /// </summary>
        public float GetMoveDirX()
        {
            bool right = IsInputHeld(InputButton.Right);
            bool left = IsInputHeld(InputButton.Left);
            if (right == left) return 0f;
            return right ? 1f : -1f;
        }
    }

    /// <summary>
    /// Read-only context passed to <see cref="SkillDef.CanActivate"/>.
    /// Exposes just what an activation guard needs without leaking
    /// mutable internals.
    /// </summary>
    public struct SkillActivationContext
    {
        public GameWorld World;
        public int Entity;
        public PlayerInput Input;

        public SkillActivationContext(GameWorld world, int entity, PlayerInput input)
        {
            World = world;
            Entity = entity;
            Input = input;
        }

        public CharacterData Data => GameCatalog.Characters[World.Identity[Entity].CharacterId];
        public CharacterMovementDef Movement => GameCatalog.Movements[GameCatalog.Characters[World.Identity[Entity].CharacterId].MovementId];
        public CharacterStatsDef Stats => GameCatalog.Stats[GameCatalog.Characters[World.Identity[Entity].CharacterId].StatsId];
        public Direction Facing => World.Transform[Entity].Facing;
        public int FacingSign => World.Transform[Entity].FacingSign;
        public bool IsGrounded => World.Physics[Entity].IsGrounded;
        public bool IsAlive => World.Life[Entity].IsAlive;
        public Stance Stance => World.GetStance(Entity);
        public bool HasTag(int tagBit) => World.HasTag(Entity, tagBit);
    }
}
