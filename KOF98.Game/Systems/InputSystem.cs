namespace KOF98.Game
{
    /// <summary>
    /// Apply per-frame inputs (player + AI) to character entities.
    /// </summary>
    public static class InputSystem
    {
        public static void Apply(GameWorld world, SceneInput input)
        {
            var w = world;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;

                if (input.HasCharacterInput[e])
                {
                    w.Input[e].Current = input.CharacterInputs[e];
                }
                else if (w.AIKinds[e] != AIKind.None)
                {
                    w.Input[e].Current = AISystem.GetInput(world, e);
                }
                else
                {
                    w.Input[e].Current = PlayerInput.Empty;
                }
            }
        }
    }
}
