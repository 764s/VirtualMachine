namespace KOF98.Game
{
    /// <summary>
    /// Static data defining a character (shared across instances).
    /// Future: load from asset files.
    /// </summary>
    public class CharacterData
    {
        public int Id;
        public string Name;

        // ── Stats ────────────────────────────────────────────────
        public float MaxHP = GameConstants.DefaultMaxHP;
        public float MaxPower = GameConstants.DefaultMaxPower;

        // ── Movement ─────────────────────────────────────────────
        public float WalkSpeed = GameConstants.DefaultWalkSpeed;
        public float BackWalkSpeed = GameConstants.DefaultBackWalkSpeed;
        public float RunSpeed = GameConstants.DefaultRunSpeed;
        public float JumpSpeedY = GameConstants.DefaultJumpSpeedY;

        // ── Collision ────────────────────────────────────────────
        public FRect StandPushBox = new FRect(0, 0.55f, GameConstants.DefaultPushboxHalfWidth, GameConstants.DefaultPushboxHalfHeight);
        public FRect CrouchPushBox = new FRect(0, 0.35f, GameConstants.DefaultPushboxHalfWidth, 0.35f);
        public FRect StandHurtBox = new FRect(0, 0.55f, 0.2f, 0.5f);

        // ── Skill Catalog ────────────────────────────────────────
        /// <summary>
        /// All skill definitions available to this character.
        /// Index = local skill ID.
        /// </summary>
        public SkillDef[] Skills = System.Array.Empty<SkillDef>();

        /// <summary>Index into Skills[] for the idle skill (-1 = none).</summary>
        public int IdleSkillIndex = -1;

        // Extension: walkSkillIndex, hitSkillIndex, etc.
    }
}
