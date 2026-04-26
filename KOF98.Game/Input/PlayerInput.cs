using System;

namespace KOF98.Game
{
    /// <summary>
    /// Raw button flags for a single player on a single frame.
    /// Matches classic 2D fighting game 4-direction + 4-button layout.
    /// </summary>
    [Flags]
    public enum InputButton : ushort
    {
        None  = 0,
        Up    = 1 << 0,
        Down  = 1 << 1,
        Left  = 1 << 2,
        Right = 1 << 3,
        LP    = 1 << 4,   // Light Punch
        HP    = 1 << 5,   // Heavy Punch
        LK    = 1 << 6,   // Light Kick
        HK    = 1 << 7,   // Heavy Kick
        Start = 1 << 8,

        // Derived masks
        AnyPunch  = LP | HP,
        AnyKick   = LK | HK,
        AnyAttack = AnyPunch | AnyKick,
        AnyDir    = Up | Down | Left | Right,
    }

    /// <summary>
    /// Per-player input state for a single frame.
    /// Stores both current and "just pressed" (edge-triggered) buttons.
    /// </summary>
    public struct PlayerInput
    {
        /// <summary>Buttons held this frame.</summary>
        public InputButton Held;

        /// <summary>Buttons newly pressed this frame (not held last frame).</summary>
        public InputButton Pressed;

        public bool IsHeld(InputButton btn) => (Held & btn) == btn;
        public bool IsPressed(InputButton btn) => (Pressed & btn) == btn;
        public bool HasAny(InputButton mask) => (Held & mask) != 0;

        /// <summary>
        /// Get the directional input relative to the character's facing direction.
        /// Returns: +1 = forward, -1 = backward, 0 = neutral.
        /// </summary>
        public int GetForwardDir(Direction facing)
        {
            bool right = IsHeld(InputButton.Right);
            bool left = IsHeld(InputButton.Left);
            if (right == left) return 0;
            if (facing == Direction.Right)
                return right ? 1 : -1;
            else
                return left ? 1 : -1;
        }

        /// <summary>Update Pressed from previous and current Held.</summary>
        public static PlayerInput ComputeEdge(InputButton prevHeld, InputButton currentHeld)
        {
            return new PlayerInput
            {
                Held = currentHeld,
                Pressed = currentHeld & ~prevHeld,
            };
        }

        public static readonly PlayerInput Empty = default;
    }
}
