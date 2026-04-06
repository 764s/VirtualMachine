namespace KOF98
{
    /// <summary>
    /// Static definition of a skill.
    /// Corresponds to a single .ffs script or a host-side skill behavior.
    /// </summary>
    public class SkillDef
    {
        public int Id;
        public string Name;

        /// <summary>Total frames of the skill action (-1 = infinite/looping like idle).</summary>
        public int TotalFrames;

        /// <summary>Skill priority (higher can interrupt lower).</summary>
        public int Priority;

        /// <summary>Tag bitflags applied when this skill is active.</summary>
        public int Tags;

        /// <summary>Whether this skill loops (e.g., idle, walk).</summary>
        public bool IsLooping;

        /// <summary>
        /// VM module slot for the .ffs script (-1 = host-side implementation).
        /// When >= 0, a VM instance is spawned for this skill.
        /// </summary>
        public int VMModuleSlot = -1;

        // ── Activation Conditions ────────────────────────────────

        /// <summary>
        /// Condition callback: returns true if this skill can activate.
        /// Parameters: (character, input). Null = always activatable.
        /// Host-side skill transitions use this.
        /// </summary>
        public System.Func<Character, PlayerInput, bool> CanActivate;

        /// <summary>
        /// Condition callback: returns true if the skill should remain active.
        /// Called each frame during TryDeactivateSkill for looping/continuous skills.
        /// Null = skill uses default deactivation (frame count or VM completion).
        /// Example: Walk skill returns false when directional input is released.
        /// </summary>
        public System.Func<Character, PlayerInput, bool> CanContinue;

        /// <summary>
        /// Per-frame callback invoked during EnterFrame for host-driven skills.
        /// Used to apply per-frame effects like setting walk velocity or jump physics.
        /// Null = no per-frame callback.
        /// </summary>
        public System.Action<Character, PlayerInput> OnFrame;

        // ── Collision Box Frames (static action data) ────────────
        /// <summary>
        /// Collision box timeline for this skill's action.
        /// Defines which boxes are active on which frames.
        /// </summary>
        public CollisionBoxFrame[] CollisionFrames = System.Array.Empty<CollisionBoxFrame>();

        public SkillDef() { }

        public SkillDef(int id, string name, int totalFrames, int priority, int tags, bool looping = false)
        {
            Id = id;
            Name = name;
            TotalFrames = totalFrames;
            Priority = priority;
            Tags = tags;
            IsLooping = looping;
        }
    }

    /// <summary>
    /// Defines collision boxes active during a frame range of a skill action.
    /// </summary>
    public struct CollisionBoxFrame
    {
        public int StartFrame;
        public int EndFrame;    // Exclusive

        public CollisionBoxType BoxType;
        public int GroupId;     // Attack group ID (for hitboxes)
        public FRect Box;

        public CollisionBoxFrame(int start, int end, CollisionBoxType type, int groupId, FRect box)
        {
            StartFrame = start;
            EndFrame = end;
            BoxType = type;
            GroupId = groupId;
            Box = box;
        }
    }

    /// <summary>
    /// Type of collision box.
    /// </summary>
    public enum CollisionBoxType : byte
    {
        Hurtbox = 0,   // Can be hit
        Hitbox = 1,    // Deals damage
        Blockbox = 2,  // Block detection
        Pushbox = 3,   // Push resolution
    }
}
