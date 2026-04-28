namespace KOF98.Game
{
    /// <summary>
    /// Auto-face the nearest opponent for any character that is not currently
    /// attacking or in hitstun. Mirrors the original GameScene.AutoFaceOpponents.
    /// </summary>
    public static class AutoFaceSystem
    {
        public static void Run(GameWorld world)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Character) continue;
                if (!world.Life[e].IsAlive) continue;
                if (FrameLineSystem.IsCharacterPaused(world, e)) continue;

                if (world.HasTag(e, GameConstants.TAG_ATTACK)) continue;
                if (world.Status[e].HitstunFrames > 0) continue;

                int opp = world.FindNearestOpponent(e);
                if (opp < 0) continue;

                world.Transform[e].Facing =
                    world.Transform[opp].Position.X > world.Transform[e].Position.X
                        ? Direction.Right : Direction.Left;
            }
        }
    }
}
