using System;

namespace KOF98.Game
{
    /// <summary>
    /// Static definition of a skill.
    /// Identified by metadata + a factory that creates an <see cref="ISkillBehavior"/>
    /// instance. The framework does not know whether the behavior is implemented
    /// by a CS simulation object or by a VM-instance wrapper — both implement the
    /// same interface.
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

        public Stance[] AllowedStances;

        /// <summary>Numerically lower = stronger. Default = 100.</summary>
        public int ActivationPriority = 100;

        /// <summary>Numerically lower = stronger. Default = 100.</summary>
        public int InterruptPriority = 100;

        // ── Activation guard ─────────────────────────────────────

        /// <summary>
        /// Host-side activation guard: returns true if this skill can activate
        /// given the candidate context. Null = always activatable (subject to
        /// stance + interrupt filters).
        /// </summary>
        public Func<SkillActivationContext, bool> CanActivate;

        // ── Collision Box Frames (static action data) ────────────

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
        public int GroupId;
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

    public enum CollisionBoxType : byte
    {
        Hurtbox = 0,
        Hitbox = 1,
        Blockbox = 2,
        Pushbox = 3,
    }
}
