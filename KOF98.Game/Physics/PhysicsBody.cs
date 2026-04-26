namespace KOF98.Game
{
    /// <summary>
    /// Physics body for a character or projectile.
    /// Handles position, velocity, gravity, and ground detection.
    /// All values are in game units (not pixels).
    /// </summary>
    public struct PhysicsBody
    {
        public FVec2 Position;
        public FVec2 Velocity;
        public FVec2 Acceleration;

        /// <summary>Whether the body is on the ground.</summary>
        public bool IsGrounded;

        /// <summary>Whether gravity applies to this body.</summary>
        public bool GravityEnabled;

        public PhysicsBody(FVec2 position)
        {
            Position = position;
            Velocity = FVec2.Zero;
            Acceleration = FVec2.Zero;
            IsGrounded = true;
            GravityEnabled = true;
        }

        /// <summary>
        /// Integrate physics for one frame.
        /// Call once per frame before collision resolution.
        /// </summary>
        public void Step()
        {
            // Apply acceleration to velocity
            Velocity = Velocity + Acceleration;

            // Apply gravity
            if (GravityEnabled && !IsGrounded)
            {
                Velocity = new FVec2(Velocity.X, Velocity.Y + GameConstants.Gravity);
            }

            // Integrate position
            Position = Position + Velocity;

            // Ground check
            if (Position.Y <= GameConstants.GroundY)
            {
                Position = new FVec2(Position.X, GameConstants.GroundY);
                if (Velocity.Y < 0)
                    Velocity = new FVec2(Velocity.X, 0);
                IsGrounded = true;
            }
            else
            {
                IsGrounded = false;
            }
        }

        /// <summary>Clamp position to stage bounds.</summary>
        public void ClampToStage()
        {
            if (Position.X < GameConstants.StageLeftBound)
                Position = new FVec2(GameConstants.StageLeftBound, Position.Y);
            if (Position.X > GameConstants.StageRightBound)
                Position = new FVec2(GameConstants.StageRightBound, Position.Y);
        }
    }
}
