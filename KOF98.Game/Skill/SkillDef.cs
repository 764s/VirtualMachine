using System;

namespace KOF98.Game
{
    /// <summary>
    /// Static definition of a skill.
    ///
    /// In this layer (game framework) a skill is identified by metadata +
    /// a factory that creates an <see cref="ISkillBehavior"/> instance.
    /// The framework does not know whether the behavior is implemented
    /// by a CS simulation object or by a VM-instance wrapper —
    /// both implement the same <see cref="ISkillBehavior"/>.
    /// </summary>
    public class SkillDef
    {
        public int Id;
        public string Name;

        /// <summary>Total frames of the skill action (-1 = infinite/looping like idle).</summary>
        public int TotalFrames;

        /// <summary>Skill priority (higher can interrupt lower). Used by legacy flat scan.</summary>
        public int Priority;

        /// <summary>Tag bitflags applied when this skill is active.</summary>
        public int Tags;

        /// <summary>Whether this skill loops (e.g., idle, walk).</summary>
        public bool IsLooping;

        /// <summary>
        /// Factory producing a fresh <see cref="ISkillBehavior"/> instance each
        /// time the skill is activated. Required — null means the skill cannot
        /// be activated.
        /// </summary>
        public Func<ISkillBehavior> BehaviorFactory;

        // ── Layered Candidate Pool Fields ────────────────────────

        /// <summary>
        /// Stances in which this skill is a valid candidate.
        /// Null or empty = no stance restriction.
        /// </summary>
        public Stance[] AllowedStances;

        /// <summary>
        /// Activation priority within the candidate pool.
        /// Numerically lower = stronger (tried first). Default = 100.
        /// Different from legacy <see cref="Priority"/> which is used for
        /// the fallback interrupt comparison.
        /// </summary>
        public int ActivationPriority = 100;

        /// <summary>
        /// Interrupt priority — numerically lower = stronger.
        /// A candidate may interrupt the currently active skill only if its
        /// InterruptPriority is &lt;= the active skill's ActivationPriority.
        /// Default = 100.
        /// </summary>
        public int InterruptPriority = 100;

        // ── Activation Conditions ────────────────────────────────

        /// <summary>
        /// Host-side activation guard: returns true if this skill can activate
        /// given the character/input state. Null = always activatable (subject
        /// to stance + interrupt filters).
        ///
        /// This is the C# analogue of the FFS "first-frame return" pattern:
        /// when the future VM layer is added, a wrapper can call
        /// VMWorld probing to mirror this check. Keeping it as a separate
        /// host hook lets the CS simulation layer skip the probe overhead.
        /// </summary>
        public Func<Character, PlayerInput, bool> CanActivate;

        // ── Collision Box Frames (static action data) ────────────
        /// <summary>
        /// Collision box timeline for this skill's action.
        /// Defines which boxes are active on which frames.
        /// </summary>
        public CollisionBoxFrame[] CollisionFrames = Array.Empty<CollisionBoxFrame>();

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
