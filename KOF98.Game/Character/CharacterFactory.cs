namespace KOF98.Game
{
    /// <summary>
    /// Builds a fully-initialized character entity in a <see cref="GameWorld"/>.
    /// </summary>
    public static class CharacterFactory
    {
        public static int Spawn(GameWorld world, int team, CharacterData data, FVec2 position)
        {
            var id = world.SpawnCharacter();
            if (!id.IsValid) return -1;

            int e = id.Index;
            world.Identity[e] = new IdentityComponent { Team = team, Data = data };
            world.Life[e] = new LifeComponent
            {
                HP = data?.MaxHP ?? GameConstants.DefaultMaxHP,
                Power = 0f,
                IsAlive = true,
            };
            world.Transform[e] = new TransformComponent
            {
                Position = position,
                Facing = team == 0 ? Direction.Right : Direction.Left,
            };
            world.Physics[e] = new PhysicsComponent
            {
                Velocity = FVec2.Zero,
                Acceleration = FVec2.Zero,
                IsGrounded = true,
                GravityEnabled = true,
            };
            world.Input[e] = default;
            world.Status[e] = default;
            world.ClearAllTags(e);

            world.PushBoxes[e] = data?.StandPushBox ?? new FRect(
                0, 0.55f,
                GameConstants.DefaultPushboxHalfWidth,
                GameConstants.DefaultPushboxHalfHeight);
            world.BlockBoxes[e] = FRect.Empty;

            world.HurtBoxCounts[e] = 1;
            world.HurtBox(e, 0) = data?.StandHurtBox ?? new FRect(0, 0.55f, 0.2f, 0.5f);
            world.HitBoxCounts[e] = 0;

            world.Skill[e] = default;
            world.FrameLine[e] = default;
            return e;
        }
    }
}
