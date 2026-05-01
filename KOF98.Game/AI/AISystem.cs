namespace KOF98.Game
{
    /// <summary>
    /// Stateless AI dispatch for character slots.
    ///
    /// All AI state lives on <see cref="GameWorld"/>:
    ///   - <c>AIKinds[slot]</c>  selects the behavior variant.
    ///   - <c>AIState[slot]</c>  carries per-slot runtime state (e.g. cooldown).
    ///
    /// To add a new AI variant: add an enum value to <see cref="AIKind"/>,
    /// add a branch in <see cref="GetInput"/>, and put any per-slot state in
    /// <see cref="AIStateComponent"/>. There is intentionally no polymorphism;
    /// the snapshot only carries POD bytes.
    /// </summary>
    public static class AISystem
    {
        // SimpleAI tunables (formerly instance fields on SimpleAI class).
        private const float SimpleAttackRange = 1.0f;
        private const int SimpleAttackCooldownFrames = 30;

        public static PlayerInput GetInput(GameWorld w, int charEntity)
        {
            if (charEntity < 0 || charEntity >= GameConstants.MaxCharacters) return PlayerInput.Empty;
            var kind = w.AIKinds[charEntity];
            switch (kind)
            {
                case AIKind.Simple: return GetSimple(w, charEntity);
                case AIKind.Null:
                case AIKind.None:
                default:
                    return PlayerInput.Empty;
            }
        }

        private static PlayerInput GetSimple(GameWorld w, int charEntity)
        {
            if (!w.IsAliveSlot(charEntity)) return PlayerInput.Empty;

            int target = w.FindNearestOpponent(charEntity);
            if (target < 0) return PlayerInput.Empty;

            float selfX = w.Transform[charEntity].Position.X;
            float targetX = w.Transform[target].Position.X;
            float dx = targetX - selfX;
            float dist = System.Math.Abs(dx);

            InputButton held = InputButton.None;
            InputButton pressed = InputButton.None;

            ref var state = ref w.AIState[charEntity];

            if (dist > SimpleAttackRange)
            {
                held |= dx > 0 ? InputButton.Right : InputButton.Left;
            }
            else if (state.Cooldown <= 0)
            {
                held |= InputButton.LP;
                pressed |= InputButton.LP;
                state.Cooldown = SimpleAttackCooldownFrames;
            }

            if (state.Cooldown > 0) state.Cooldown--;

            return new PlayerInput { Held = held, Pressed = pressed };
        }
    }
}
