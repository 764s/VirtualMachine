namespace KOF98.Game
{
    /// <summary>
    /// Per-call context passed to <see cref="ISkillBehavior"/> methods.
    /// Exposes the host capabilities a skill behavior needs — modeled after
    /// the GameSyscalls surface that a VM instance would have access to.
    ///
    /// Keep this surface minimal and close to VMWorld semantics so that:
    ///   - CS simulation behaviors call methods that mirror FFS syscalls.
    ///   - The future VM-layer wrapper (VMSkillBehavior) gets the same
    ///     context shape, simplifying side-by-side comparison.
    /// </summary>
    public struct SkillContext
    {
        /// <summary>The character owning the skill instance.</summary>
        public Character Self;

        /// <summary>The scene this skill is running in (for cross-character queries).</summary>
        public GameScene Scene;

        /// <summary>The skill instance currently running.</summary>
        public SkillInstance Instance;

        public SkillContext(Character self, GameScene scene, SkillInstance instance)
        {
            Self = self;
            Scene = scene;
            Instance = instance;
        }

        // ── Input queries (mirror IsInputHeld / IsInputPressed / GetInputDir syscalls) ──

        public bool IsInputHeld(InputButton btn) => Self.CurrentInput.IsHeld(btn);
        public bool IsInputPressed(InputButton btn) => Self.CurrentInput.IsPressed(btn);
        /// <summary>+1 = forward (relative to facing), -1 = backward, 0 = neutral.</summary>
        public int GetInputDir() => Self.CurrentInput.GetForwardDir(Self.Facing);

        // ── Character state (mirror IsGrounded / GetPosX / etc. syscalls) ──

        public bool IsGrounded() => Self.IsGrounded;
        public float GetPosX() => Self.Body.Position.X;
        public float GetPosY() => Self.Body.Position.Y;
        public Direction GetFacing() => Self.Facing;
        public int GetFacingSign() => Self.FacingSign;

        // ── Character control (mirror SetVelocity / SetFacing syscalls) ──

        public void SetVelocity(float vx, float vy) =>
            Self.Body.Velocity = new FVec2(vx, vy);

        /// <summary>
        /// Compute the raw move direction along X in world space, derived from input
        /// and facing. Same semantics as common/input.ffs getMoveDirX():
        /// returns +1, -1 or 0 in world coordinates.
        /// </summary>
        public float GetMoveDirX()
        {
            bool right = IsInputHeld(InputButton.Right);
            bool left = IsInputHeld(InputButton.Left);
            if (right == left) return 0f;
            return right ? 1f : -1f;
        }
    }
}
