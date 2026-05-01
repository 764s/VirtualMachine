namespace KOF98.Game
{
    // ── Character composition ────────────────────────────────────

    /// <summary>
    /// Static identity (team, authoring data) of a character entity.
    /// <see cref="CharacterId"/> indexes into <see cref="GameCatalog"/>.
    /// Use <c>GameCatalog.InvalidId</c> for "no data" — never raw 0.
    /// </summary>
    public struct IdentityComponent
    {
        public int Team;            // 0 = P1, 1 = P2
        public int CharacterId;     // -1 = none
    }

    /// <summary>Mutable life-state of a character entity.</summary>
    public struct LifeComponent
    {
        public float HP;
        public float Power;
        public bool IsAlive;
    }

    /// <summary>Position + facing of any entity that exists in the stage.</summary>
    public struct TransformComponent
    {
        public FVec2 Position;
        public Direction Facing;

        public int FacingSign => Facing == Direction.Right ? 1 : -1;
    }

    /// <summary>Physics integration state for a character entity.</summary>
    public struct PhysicsComponent
    {
        public FVec2 Velocity;
        public FVec2 Acceleration;
        public bool IsGrounded;
        public bool GravityEnabled;
    }

    /// <summary>Per-frame input applied to a character entity.</summary>
    public struct InputComponent
    {
        public PlayerInput Current;
    }

    /// <summary>
    /// Status timers and flags for a character. Hitstun / blockstun timers
    /// are decremented by the character's own <see cref="FrameLineComponent"/>,
    /// so a paused character (hit-pause / global time-stop) does not bleed
    /// stun frames.
    /// </summary>
    public struct StatusComponent
    {
        public int HitstunFrames;
        public int BlockstunFrames;
        public bool IsKnockedDown;
    }

    /// <summary>
    /// Per-character frame line. Only character entity slots use this component;
    /// non-character slots keep the default value and are cleared on destroy.
    /// Each character owns its own time clock so hit-pause and time-stop can be
    /// expressed by freezing a single entity without touching the global frame
    /// counter.
    ///
    ///   <see cref="LocalFrame"/>  monotonic frame count for this entity (only
    ///                 advances on frames where the character is not paused).
    ///   <see cref="PauseFrames"/> remaining pause frames (&gt; 0 means this
    ///                 character is frozen this frame). Decremented by
    ///                 <c>FrameLineSystem.AdvanceCharacters</c>.
    /// </summary>
    public struct FrameLineComponent
    {
        public int LocalFrame;
        public int PauseFrames;
        public int RequestedPauseFrames;
    }

    /// <summary>Single scene-wide frame line owned by the ECS world.</summary>
    public struct SceneFrameLineComponent
    {
        public int GlobalFrame;
        public int PauseFrames;
        public int RequestedPauseFrames;
    }

    /// <summary>Bitfield of active gameplay tags from the current skill.</summary>
    public struct TagComponent
    {
        public int ActiveTags; // bit N = TAG_xxx where N = GameConstants.TAG_xxx
    }

    /// <summary>
    /// Skill runtime state owned by a character entity. All fields are value
    /// types so the component is fully snapshot-friendly. The live behavior
    /// instance is held in the world's transient
    /// <see cref="GameWorld.ActiveBehaviors"/> cache and reconstructed from
    /// <see cref="ActiveSkillId"/> after a snapshot restore.
    /// </summary>
    public struct SkillComponent
    {
        public int ActiveSkillId;       // -1 = none
        public int PendingSkillId;      // -1 = none
        public int SkillFrame;
        public bool IsSkillActive;
        public int SkillMutexFlags;
    }

    // ── Projectile / Effect composition ─────────────────────────

    public struct ProjectileComponent
    {
        public int OwnerEntity;
        public int Team;
        public FVec2 Velocity;
        public FRect HitBox;     // Relative to position
        public float Damage;
        public int DamageType;
    }

    public struct EffectComponent
    {
        public int EffectTypeId;
        public int OwnerEntity;
    }

    /// <summary>Frame-counted lifetime; entity is destroyed when it hits zero.</summary>
    public struct LifetimeComponent
    {
        public int RemainingFrames;
    }

    // ── Round / AI singletons ────────────────────────────────────

    /// <summary>
    /// Scene-wide round state. Lives on <see cref="GameWorld"/> as a singleton
    /// component so it participates in snapshot capture/restore. <see cref="WinnerSlot"/>
    /// is initialized to -1 by <c>GameWorld</c>'s constructor and reset paths.
    /// </summary>
    public struct RoundStateComponent
    {
        public int RoundNumber;
        public bool IsPaused;       // External / menu pause (host-driven)
        public bool IsRoundOver;
        public int WinnerSlot;      // -1 = no winner / draw
    }

    /// <summary>
    /// Per-character AI controller kind. <see cref="AIKind.None"/> means the
    /// slot is human-driven (input comes from <c>SceneInput</c>).
    /// </summary>
    public enum AIKind : byte
    {
        None = 0,
        Null = 1,
        Simple = 2,
    }

    /// <summary>
    /// Per-character AI runtime state (only valid for character slots).
    /// All fields are POD and snapshot-friendly.
    /// </summary>
    public struct AIStateComponent
    {
        public int Cooldown;
    }

    // ── Collision box entries ────────────────────────────────────

    /// <summary>A single hitbox entry associated with an attack group.</summary>
    public struct HitBoxEntry
    {
        public int GroupId;
        public FRect Box;

        public HitBoxEntry(int groupId, FRect box)
        {
            GroupId = groupId;
            Box = box;
        }
    }
}
