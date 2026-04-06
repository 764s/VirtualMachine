namespace KOF98
{
    /// <summary>
    /// Interface for AI controllers.
    /// Each AI implementation produces PlayerInput for a character each frame.
    ///
    /// Extension point: AI can be implemented as:
    /// - Host-side C# (this interface)
    /// - VM script instance (via GameVMBridge, one ffs per character)
    /// - Hybrid (C# for decision tree, ffs for sub-behaviors)
    /// </summary>
    public interface IAIController
    {
        /// <summary>
        /// Produce input for the controlled character this frame.
        /// </summary>
        /// <param name="scene">Current game scene for reading state.</param>
        /// <param name="charId">ID of the controlled character.</param>
        /// <returns>Input to apply to the character.</returns>
        PlayerInput GetInput(GameScene scene, int charId);
    }

    /// <summary>
    /// Placeholder AI that does nothing. For P2 slot when no AI is assigned.
    /// </summary>
    public class NullAI : IAIController
    {
        public static readonly NullAI Instance = new NullAI();
        public PlayerInput GetInput(GameScene scene, int charId) => PlayerInput.Empty;
    }

    /// <summary>
    /// Simple dummy AI that walks toward the opponent and occasionally attacks.
    /// Placeholder for testing — will be replaced by VM-scripted AI.
    /// </summary>
    public class SimpleAI : IAIController
    {
        private int _actionTimer;
        private int _seed;

        public SimpleAI(int seed = 42)
        {
            _seed = seed;
        }

        public PlayerInput GetInput(GameScene scene, int charId)
        {
            var ch = scene.Characters.Get(charId);
            if (ch == null || !ch.IsAlive) return PlayerInput.Empty;

            var opponent = scene.Characters.FindNearestOpponent(charId);
            if (opponent == null) return PlayerInput.Empty;

            InputButton held = InputButton.None;
            float dist = ch.Body.Position.HDistanceTo(opponent.Body.Position);

            // Walk toward opponent
            if (dist > 1.5f)
            {
                held |= opponent.Body.Position.X > ch.Body.Position.X
                    ? InputButton.Right : InputButton.Left;
            }
            else
            {
                // In range: occasionally attack
                _actionTimer++;
                _seed = (_seed * 1103515245 + 12345) & 0x7FFFFFFF;
                if (_actionTimer > 30 && (_seed % 4) == 0)
                {
                    held |= InputButton.LP;
                    _actionTimer = 0;
                }
            }

            return new PlayerInput { Held = held, Pressed = held };
        }
    }
}
