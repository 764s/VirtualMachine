namespace KOF98.Game
{
    /// <summary>Provides per-frame input for an AI-controlled character.</summary>
    public interface IAIController
    {
        PlayerInput GetInput(GameScene scene, int charEntity);
    }

    /// <summary>No-op AI — returns empty input.</summary>
    public class NullAI : IAIController
    {
        public PlayerInput GetInput(GameScene scene, int charEntity) => PlayerInput.Empty;
    }

    /// <summary>
    /// Minimal demo AI: walks toward the nearest opponent, throws an LP at
    /// short range. Intentionally dumb — kept for sanity testing only.
    /// </summary>
    public class SimpleAI : IAIController
    {
        public float AttackRange = 1.0f;
        public int AttackCooldownFrames = 30;

        private int _cooldown;

        public PlayerInput GetInput(GameScene scene, int charEntity)
        {
            var w = scene.World;
            if (!w.IsAliveSlot(charEntity)) return PlayerInput.Empty;

            int target = w.FindNearestOpponent(charEntity);
            if (target < 0) return PlayerInput.Empty;

            float selfX = w.Transform[charEntity].Position.X;
            float targetX = w.Transform[target].Position.X;
            float dx = targetX - selfX;
            float dist = System.Math.Abs(dx);

            InputButton held = InputButton.None;
            InputButton pressed = InputButton.None;

            if (dist > AttackRange)
            {
                held |= dx > 0 ? InputButton.Right : InputButton.Left;
            }
            else if (_cooldown <= 0)
            {
                held |= InputButton.LP;
                pressed |= InputButton.LP;
                _cooldown = AttackCooldownFrames;
            }

            if (_cooldown > 0) _cooldown--;

            return new PlayerInput { Held = held, Pressed = pressed };
        }
    }
}
