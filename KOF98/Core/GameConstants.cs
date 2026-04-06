namespace KOF98
{
    /// <summary>
    /// Game-wide constants for the KOF98 simulation.
    /// </summary>
    public static class GameConstants
    {
        // ── Physics ──────────────────────────────────────────────
        public const float Gravity = -0.55f;
        public const float GroundY = 0f;
        public const float StageLeftBound = -7f;
        public const float StageRightBound = 7f;

        // ── Character defaults ───────────────────────────────────
        public const float DefaultMaxHP = 1000f;
        public const float DefaultMaxPower = 3f;
        public const float DefaultWalkSpeed = 0.06f;
        public const float DefaultBackWalkSpeed = 0.04f;
        public const float DefaultRunSpeed = 0.12f;
        public const float DefaultJumpSpeedY = 0.35f;
        public const float DefaultPushboxHalfWidth = 0.25f;
        public const float DefaultPushboxHalfHeight = 0.55f;

        // ── Frame / Round ────────────────────────────────────────
        public const int FPS = 60;
        public const int RoundTimeSeconds = 60;
        public const int MaxRoundFrames = FPS * RoundTimeSeconds;
        public const int MaxRounds = 3;

        // ── Capacity limits ──────────────────────────────────────
        public const int MaxCharacters = 4;
        public const int MaxProjectiles = 32;
        public const int MaxEffects = 64;
        public const int MaxActiveSkillsPerCharacter = 4;

        // ── Skill Tags (bitfield positions) ──────────────────────
        public const int TAG_IDLE = 0;
        public const int TAG_WALK = 1;
        public const int TAG_RUN = 2;
        public const int TAG_CROUCH = 3;
        public const int TAG_JUMP = 4;
        public const int TAG_ATTACK = 5;
        public const int TAG_HIT = 6;
        public const int TAG_BLOCK = 7;
        public const int TAG_THROW = 8;
        public const int TAG_KNOCKDOWN = 9;
        public const int TAG_DEATH = 10;
        public const int TAG_AIR_STATE = 11;
        public const int TAG_SKILL = 12;
        public const int TAG_PERFORMANCE = 13;
        public const int TAG_CUTSCENE = 14;

        // ── Skill Priorities ─────────────────────────────────────
        public const int PRIORITY_IDLE = 0;
        public const int PRIORITY_MOVEMENT = 1;
        public const int PRIORITY_HIT = 2;
        public const int PRIORITY_ATTACK = 3;
        public const int PRIORITY_SPECIAL = 4;
        public const int PRIORITY_SUPER = 5;
        public const int PRIORITY_DODGE = 5;
        public const int PRIORITY_THROW = 6;
        public const int PRIORITY_SYSTEM = 10;

        // ── Damage Types ─────────────────────────────────────────
        public const int DMG_NONE = 0;
        public const int DMG_NORMAL_LOWER = 101;
        public const int DMG_NORMAL_UPPER = 102;
        public const int DMG_KNOCKDOWN = 201;
        public const int DMG_THROW = 301;
        public const int DMG_SUPER = 401;

        // ── Syscall Slot Allocation ──────────────────────────────
        // 0-19: Action management
        public const int SYS_BEGIN_ACTION = 0;
        public const int SYS_END_ACTION = 1;
        public const int SYS_GET_FRAME = 2;

        // 20-39: Collision detection
        public const int SYS_CHECK_ATTACK_HIT = 20;
        public const int SYS_CHECK_ATTACK_BLOCKED = 21;
        public const int SYS_HAS_TARGET_TAG = 22;

        // 40-59: Damage and effects
        public const int SYS_APPLY_DAMAGE = 40;
        public const int SYS_SET_ENERGY_COEFF = 41;
        public const int SYS_APPLY_HITSTUN = 42;
        public const int SYS_APPLY_HORIZ_KB_DIST = 43;
        public const int SYS_APPLY_HORIZ_KB_SPEED = 44;
        public const int SYS_APPLY_VERT_KB = 45;
        public const int SYS_APPLY_CORNER_KB_SELF = 46;
        public const int SYS_APPLY_SELF_HITSTUN = 47;
        public const int SYS_APPLY_SELF_HORIZ_KB = 48;
        public const int SYS_APPLY_SELF_VERT_KB = 49;

        // 60-79: Visual effects
        public const int SYS_SPAWN_EFFECT_HIT = 60;
        public const int SYS_SPAWN_EFFECT_SELF = 61;

        // 80-99: Character queries
        public const int SYS_GET_SELF_ID = 80;
        public const int SYS_GET_POS_X = 81;
        public const int SYS_GET_POS_Y = 82;
        public const int SYS_GET_FACING = 83;
        public const int SYS_GET_HP = 84;
        public const int SYS_GET_POWER = 85;
        public const int SYS_IS_GROUNDED = 86;
        public const int SYS_GET_OPPONENT_ID = 87;
        public const int SYS_GET_DISTANCE = 88;

        // 100-119: Character control
        public const int SYS_SET_VELOCITY = 100;
        public const int SYS_SET_FACING = 101;
        public const int SYS_ADD_POWER = 102;

        // 120-139: Input queries
        public const int SYS_GET_INPUT = 120;
        public const int SYS_GET_INPUT_DIR = 121;
        public const int SYS_IS_INPUT_PRESSED = 122;
        public const int SYS_IS_INPUT_HELD = 123;

        // 140-159: AI (reserved)
        public const int SYS_AI_FIND_NEAREST_ENEMY = 140;
        public const int SYS_AI_GET_DISTANCE = 141;
        public const int SYS_AI_MOVE_TOWARD = 142;

        // 160-179: VM instance management (MI-2, MI-3)
        public const int SYS_SPAWN_SCRIPT = 160;
        public const int SYS_KILL_INSTANCE = 161;

        // 180-199: Blackboard
        public const int SYS_SET_BLACKBOARD = 180;
        public const int SYS_GET_BLACKBOARD = 181;

        // 200-219: Utility
        public const int SYS_PRINT = 200;
        public const int SYS_RANDOM = 201;
        public const int SYS_ABS = 202;
        public const int SYS_MIN = 203;
        public const int SYS_MAX = 204;

        /// <summary>Facing direction to multiplier: Right=+1, Left=-1.</summary>
        public static int FacingSign(Direction dir) => dir == Direction.Right ? 1 : -1;
    }
}
