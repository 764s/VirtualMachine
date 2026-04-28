using System.Collections.Generic;

namespace KOF98.Game
{
    /// <summary>
    /// Apply per-frame inputs (player + AI) to character entities.
    /// </summary>
    public static class InputSystem
    {
        public static void Apply(GameScene scene, SceneInput input,
            Dictionary<int, IAIController> aiControllers)
        {
            var w = scene.World;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;

                if (input.CharacterInputs.TryGetValue(e, out var p))
                {
                    w.Input[e].Current = p;
                }
                else if (aiControllers != null && aiControllers.TryGetValue(e, out var ai))
                {
                    w.Input[e].Current = ai.GetInput(scene, e);
                }
                else
                {
                    w.Input[e].Current = PlayerInput.Empty;
                }
            }
        }
    }
}
