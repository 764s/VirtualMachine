namespace KOF98.Game
{
    /// <summary>
    /// Physics integration system. Replaces the per-character PhysicsBody.Step()
    /// with an entity-scoped pass over <see cref="PhysicsComponent"/> and
    /// <see cref="TransformComponent"/>.
    ///
    /// Step:
    ///   velocity += acceleration
    ///   if (gravity &amp;&amp; !grounded) velocity.y += Gravity
    ///   position += velocity
    ///   if (position.y &lt;= GroundY) snap to ground, set grounded = true
    ///   else grounded = false
    ///   clamp to stage bounds
    /// </summary>
    public static class PhysicsSystem
    {
        public static void Step(GameWorld world)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Character) continue;
                if (FrameLineSystem.IsEntityFrozen(world, e)) continue;

                ref var phys = ref world.Physics[e];
                ref var tr = ref world.Transform[e];

                phys.Velocity = phys.Velocity + phys.Acceleration;

                if (phys.GravityEnabled && !phys.IsGrounded)
                {
                    phys.Velocity = new FVec2(phys.Velocity.X, phys.Velocity.Y + GameConstants.Gravity);
                }

                tr.Position = tr.Position + phys.Velocity;

                if (tr.Position.Y <= GameConstants.GroundY)
                {
                    tr.Position = new FVec2(tr.Position.X, GameConstants.GroundY);
                    if (phys.Velocity.Y < 0)
                        phys.Velocity = new FVec2(phys.Velocity.X, 0);
                    phys.IsGrounded = true;
                }
                else
                {
                    phys.IsGrounded = false;
                }

                ClampToStage(ref tr.Position);
            }
        }

        public static void ClampToStage(ref FVec2 pos)
        {
            if (pos.X < GameConstants.StageLeftBound)
                pos = new FVec2(GameConstants.StageLeftBound, pos.Y);
            if (pos.X > GameConstants.StageRightBound)
                pos = new FVec2(GameConstants.StageRightBound, pos.Y);
        }
    }
}
