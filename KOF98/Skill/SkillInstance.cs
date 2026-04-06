namespace KOF98
{
    /// <summary>
    /// Runtime state for a single active skill.
    /// A character may have one main skill and potentially sub-skills.
    /// </summary>
    public class SkillInstance
    {
        /// <summary>Reference to the skill definition.</summary>
        public SkillDef Def;

        /// <summary>Current frame within the skill timeline (0-based).</summary>
        public int Frame;

        /// <summary>Whether this skill instance is active.</summary>
        public bool IsActive;

        /// <summary>
        /// FFVM instance ID driving this skill (-1 = host-driven or no script).
        /// </summary>
        public int VMInstanceId = -1;

        /// <summary>Owning character ID.</summary>
        public int OwnerId;

        // ── Mutex Tracking (for effect deduplication) ────────────
        /// <summary>
        /// Simple mutex flags for subskill effect deduplication.
        /// Managed by the skill script via local variables.
        /// </summary>
        public int MutexFlags;

        public SkillInstance() { }

        public SkillInstance(SkillDef def, int ownerId)
        {
            Def = def;
            Frame = 0;
            IsActive = true;
            OwnerId = ownerId;
            MutexFlags = 0;
        }

        /// <summary>Advance frame counter. Returns true if skill has ended.</summary>
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
