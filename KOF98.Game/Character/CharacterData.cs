namespace KOF98.Game
{
    /// <summary>
    /// Static data defining a character (shared across instances).
    /// Future: load from asset files.
    /// </summary>
    public class CharacterData
    {
        public int CatalogCharacterId;
        public string Name;

        // Handles into GameCatalog static pools.
        public int StatsId        = GameCatalog.InvalidId;
        public int MovementId     = GameCatalog.InvalidId;
        public int SkillLoadoutId = GameCatalog.InvalidId;

        // ── Collision ────────────────────────────────────────────
        public FRect StandPushBox = new FRect(0, 0.55f, GameConstants.DefaultPushboxHalfWidth, GameConstants.DefaultPushboxHalfHeight);
        public FRect CrouchPushBox = new FRect(0, 0.35f, GameConstants.DefaultPushboxHalfWidth, 0.35f);
        public FRect StandHurtBox = new FRect(0, 0.55f, 0.2f, 0.5f);

        // Extension: walkSkillIndex, hitSkillIndex, etc.
    }
}
