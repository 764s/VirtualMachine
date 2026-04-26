namespace KOF98.Game
{
    /// <summary>
    /// Runtime state for a single active skill.
    /// Holds the skill definition, frame counter and the live behavior instance
    /// (which encapsulates VM-instance-equivalent state — for the CS sim,
    /// the behavior object itself; for the future VM layer, it would wrap
    /// a VMWorld instance ID).
    /// </summary>
    public class SkillInstance
    {
        /// <summary>Reference to the skill definition.</summary>
        public SkillDef Def;

        /// <summary>Current frame within the skill timeline (0-based).</summary>
        public int Frame;

        /// <summary>Whether this skill instance is active.</summary>
        public bool IsActive;

        /// <summary>The live behavior driving this skill (CS sim object or VM wrapper).</summary>
        public ISkillBehavior Behavior;

        /// <summary>Owning character ID.</summary>
        public int OwnerId;

        /// <summary>
        /// Simple mutex flags for subskill effect deduplication.
        /// Managed by the skill behavior via local state.
        /// </summary>
        public int MutexFlags;

        public SkillInstance() { }

        public SkillInstance(SkillDef def, int ownerId, ISkillBehavior behavior)
        {
            Def = def;
            Frame = 0;
            IsActive = true;
            OwnerId = ownerId;
            Behavior = behavior;
            MutexFlags = 0;
        }

        /// <summary>Advance frame counter. Returns true if skill has ended via frame budget.</summary>
        public bool AdvanceFrame()
        {
            if (!IsActive) return true;
            Frame++;
            if (!Def.IsLooping && Def.TotalFrames > 0 && Frame >= Def.TotalFrames)
                return true;
            return false;
        }
    }
}
