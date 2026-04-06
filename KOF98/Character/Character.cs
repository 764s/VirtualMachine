namespace KOF98
{
    /// <summary>
    /// Runtime state for a single character in the fight.
    /// All fields are value-type for snapshot/rollback friendliness.
    /// </summary>
    public class Character
    {
        // ── Identity ─────────────────────────────────────────────
        public int Id;
        public int Team;       // 0 = P1, 1 = P2
        public CharacterData Data;

        // ── Core State ───────────────────────────────────────────
        public float HP;
        public float Power;    // Super meter (0..MaxPower)
        public Direction Facing;
        public bool IsAlive;

        // ── Physics ──────────────────────────────────────────────
        public PhysicsBody Body;

        // ── Input ────────────────────────────────────────────────
        public PlayerInput CurrentInput;

        // ── Active Collision Boxes ───────────────────────────────
        /// <summary>Current pushbox (set by skill/action).</summary>
        public FRect PushBox;
        /// <summary>Current hurtboxes (up to 4, empty = no hurtbox).</summary>
        public FRect[] HurtBoxes = new FRect[4];
        public int HurtBoxCount;
        /// <summary>Active hitboxes per attack group (groupId → box).</summary>
        public HitBoxEntry[] HitBoxes = new HitBoxEntry[4];
        public int HitBoxCount;
        /// <summary>Block detection box.</summary>
        public FRect BlockBox;

        // ── Skill State ──────────────────────────────────────────
        /// <summary>Active skills managed by the skill manager.</summary>
        public SkillManager SkillMgr;

        // ── Status Effects ───────────────────────────────────────
        public int HitstunFrames;
        public int BlockstunFrames;
        public bool IsKnockedDown;

        // ── Tags (bitflags) ──────────────────────────────────────
        /// <summary>
        /// Active gameplay tags from the current skill.
        /// Bit N = TAG_xxx where N matches GameConstants.TAG_xxx.
        /// </summary>
        public int ActiveTags;

        // ── Blackboard (key→value for VM communication) ──────────
        /// <summary>
        /// Simple blackboard for VM ↔ host data exchange.
        /// Key = integer ID, Value = float.
        /// </summary>
        public System.Collections.Generic.Dictionary<int, float> Blackboard = new();

        // ── VM Instance Tracking ─────────────────────────────────
        /// <summary>VM instance ID for AI script (-1 = no AI script).</summary>
        public int AIInstanceId = -1;

        public Character(int id, int team, CharacterData data)
        {
            Id = id;
            Team = team;
            Data = data;
            HP = data.MaxHP;
            Power = 0;
            Facing = team == 0 ? Direction.Right : Direction.Left;
            IsAlive = true;
            Body = new PhysicsBody();
            PushBox = data.StandPushBox;
            HurtBoxCount = 1;
            HurtBoxes[0] = data.StandHurtBox;
            SkillMgr = new SkillManager(this);
        }

        // ── Helpers ──────────────────────────────────────────────
        public int FacingSign => GameConstants.FacingSign(Facing);
        public bool IsGrounded => Body.IsGrounded;

        /// <summary>
        /// Compute the current stance from runtime state.
        /// Used by the layered candidate pool (SK2) for stance-based skill grouping.
        /// Note: TAG_CROUCH is only set by the Crouch skill which requires IsGrounded,
        /// so the Airborne check before TAG_CROUCH is safe — an airborne character
        /// will never have TAG_CROUCH active.
        /// </summary>
        public Stance GetStance()
        {
            if (!IsAlive) return Stance.Dead;
            if (IsKnockedDown) return Stance.Knockdown;
            if (HitstunFrames > 0) return Stance.Hitstun;
            if (!Body.IsGrounded) return Stance.Airborne;
            if (HasTag(GameConstants.TAG_CROUCH)) return Stance.Crouching;
            return Stance.Grounded;
        }

        public bool HasTag(int tagBit) => (ActiveTags & (1 << tagBit)) != 0;
        public void SetTag(int tagBit) => ActiveTags |= (1 << tagBit);
        public void ClearTag(int tagBit) => ActiveTags &= ~(1 << tagBit);
        public void ClearAllTags() => ActiveTags = 0;

        /// <summary>Clear all active hitboxes.</summary>
        public void ClearHitBoxes() => HitBoxCount = 0;

        /// <summary>Clear all hurtboxes.</summary>
        public void ClearHurtBoxes() => HurtBoxCount = 0;

        public void SetBlackboard(int key, float value) => Blackboard[key] = value;
        public float GetBlackboard(int key) => Blackboard.TryGetValue(key, out var v) ? v : 0f;
    }

    /// <summary>
    /// A single hitbox entry associated with an attack group.
    /// </summary>
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
