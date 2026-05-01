namespace KOF98.Game
{
    /// <summary>
    /// Fixed-capacity ECS world for the KOF98 simulation.
    ///
    /// Layout decisions:
    ///   - Single dense pool of <see cref="MaxEntities"/> slots.
    ///   - Slots [0..MaxCharacters) are reserved for characters so external
    ///     code that talks about "character id 0/1" maps directly to the
    ///     entity index. Slots [MaxCharacters..MaxEntities) hold projectiles
    ///     and effects.
    ///   - Components live in parallel arrays indexed by entity index. We do
    ///     not maintain per-component bitmasks because each entity kind has
    ///     a fixed component shape — system code can branch on
    ///     <see cref="EntityKind"/> when needed.
    ///   - Generation counter detects stale <see cref="EntityId"/> handles.
    /// </summary>
    public class GameWorld
    {
        public const int MaxEntities = GameConstants.MaxCharacters
            + GameConstants.MaxProjectiles + GameConstants.MaxEffects;

        public const int MaxHurtBoxesPerEntity = 4;
        public const int MaxHitBoxesPerEntity = 4;

        // ── Slot bookkeeping ─────────────────────────────────────

        /// <summary>
        /// Generation counter per slot.
        ///   = 0 : never used.
        ///   &gt; 0 : currently alive with that generation.
        ///   &lt; 0 : freed; absolute value is the last alive generation.
        /// </summary>
        public readonly int[] Generations = new int[MaxEntities];
        public readonly EntityKind[] Kinds = new EntityKind[MaxEntities];

        // ── Component arrays ─────────────────────────────────────

        public readonly IdentityComponent[] Identity = new IdentityComponent[MaxEntities];
        public readonly LifeComponent[] Life = new LifeComponent[MaxEntities];
        public readonly TransformComponent[] Transform = new TransformComponent[MaxEntities];
        public readonly PhysicsComponent[] Physics = new PhysicsComponent[MaxEntities];
        public readonly InputComponent[] Input = new InputComponent[MaxEntities];
        public readonly StatusComponent[] Status = new StatusComponent[MaxEntities];
        public readonly TagComponent[] Tags = new TagComponent[MaxEntities];

        public readonly FRect[] PushBoxes = new FRect[MaxEntities];
        public readonly FRect[] BlockBoxes = new FRect[MaxEntities];
        public readonly FRect[] HurtBoxesFlat = new FRect[MaxEntities * MaxHurtBoxesPerEntity];
        public readonly int[] HurtBoxCounts = new int[MaxEntities];
        public readonly HitBoxEntry[] HitBoxesFlat = new HitBoxEntry[MaxEntities * MaxHitBoxesPerEntity];
        public readonly int[] HitBoxCounts = new int[MaxEntities];

        public readonly SkillComponent[] Skill = new SkillComponent[MaxEntities];
        public readonly FrameLineComponent[] FrameLine = new FrameLineComponent[MaxEntities];
        public SceneFrameLineComponent SceneFrameLine;

        /// <summary>
        /// Transient per-entity behavior cache. NOT part of the snapshot —
        /// rebuilt on snapshot restore from <see cref="SkillComponent.ActiveSkillId"/>.
        /// </summary>
        public readonly ISkillBehavior[] ActiveBehaviors = new ISkillBehavior[MaxEntities];

        public readonly ProjectileComponent[] Projectile = new ProjectileComponent[MaxEntities];
        public readonly EffectComponent[] Effect = new EffectComponent[MaxEntities];
        public readonly LifetimeComponent[] Lifetime = new LifetimeComponent[MaxEntities];

        public readonly float[] BlackboardValues = new float[MaxEntities * GameConstants.MaxBlackboardKeys];
        public readonly bool[] BlackboardHasValue = new bool[MaxEntities * GameConstants.MaxBlackboardKeys];

        // ── Combat pending hit queue (was CombatSystem instance state) ──

        public readonly HitEvent[] PendingHits = new HitEvent[GameConstants.MaxPendingHits];
        public int PendingHitCount;

        // ── Round state singleton (was GameScene fields) ───────────────────

        public RoundStateComponent RoundState;

        // ── AI per-character slot (was GameScene._aiControllers + SimpleAI._cooldown) ──

        public readonly AIKind[] AIKinds = new AIKind[GameConstants.MaxCharacters];
        public readonly AIStateComponent[] AIState = new AIStateComponent[GameConstants.MaxCharacters];

        // ── Counts ──────────────────────────────────────────────────────────────

        public int CharacterCount;
        public int ProjectileCount;
        public int EffectCount;

        public GameWorld()
        {
            RoundState.WinnerSlot = -1;
        }

        // ── Slot allocation ──────────────────────────────────────

        /// <summary>Reserve a character slot in the [0..MaxCharacters) range.</summary>
        public EntityId SpawnCharacter()
        {
            for (int i = 0; i < GameConstants.MaxCharacters; i++)
            {
                if (Generations[i] <= 0)
                {
                    var id = AllocAt(i, EntityKind.Character);
                    CharacterCount++;
                    return id;
                }
            }
            return EntityId.Null;
        }

        public EntityId SpawnProjectile()
        {
            int id = AllocNonCharacter(EntityKind.Projectile);
            if (id >= 0) { ProjectileCount++; return new EntityId(id, Generations[id]); }
            return EntityId.Null;
        }

        public EntityId SpawnEffect()
        {
            int id = AllocNonCharacter(EntityKind.Effect);
            if (id >= 0) { EffectCount++; return new EntityId(id, Generations[id]); }
            return EntityId.Null;
        }

        private int AllocNonCharacter(EntityKind kind)
        {
            for (int i = GameConstants.MaxCharacters; i < MaxEntities; i++)
            {
                if (Generations[i] <= 0)
                {
                    AllocAt(i, kind);
                    return i;
                }
            }
            return -1;
        }

        private EntityId AllocAt(int i, EntityKind kind)
        {
            int gen = Generations[i] == 0 ? 1 : (-Generations[i]) + 1;
            Generations[i] = gen;
            Kinds[i] = kind;
            return new EntityId(i, gen);
        }

        public void Destroy(EntityId id)
        {
            if (!IsAlive(id)) return;
            DestroyAt(id.Index);
        }

        public void DestroyAt(int index)
        {
            if (index < 0 || index >= MaxEntities) return;
            if (Generations[index] <= 0) return;

            switch (Kinds[index])
            {
                case EntityKind.Character: CharacterCount--; break;
                case EntityKind.Projectile: ProjectileCount--; break;
                case EntityKind.Effect: EffectCount--; break;
            }

            Generations[index] = -Generations[index];
            Kinds[index] = EntityKind.None;

            // Clear heap-referencing component fields so we do not leak
            // CharacterData or behavior handles into the next reuse.
            Skill[index] = default;
            Skill[index].ActiveSkillId = GameCatalog.InvalidId;
            Skill[index].PendingSkillId = GameCatalog.InvalidId;
            ActiveBehaviors[index] = null;
            Identity[index] = default;
            Identity[index].CharacterId = GameCatalog.InvalidId;
            Tags[index] = default;
            FrameLine[index] = default;
            HitBoxCounts[index] = 0;
            HurtBoxCounts[index] = 0;
            ClearBlackboard(index);

            // Character-only slot-scoped state.
            if (index < GameConstants.MaxCharacters)
            {
                AIKinds[index] = AIKind.None;
                AIState[index] = default;
            }
        }

        // ── Aliveness queries ────────────────────────────────────

        public bool IsAlive(EntityId id)
        {
            if (id.Index < 0 || id.Index >= MaxEntities) return false;
            return Generations[id.Index] > 0 && Generations[id.Index] == id.Generation;
        }

        public bool IsAliveSlot(int index)
        {
            if (index < 0 || index >= MaxEntities) return false;
            return Generations[index] > 0;
        }

        public EntityId IdAt(int index)
        {
            int gen = Generations[index];
            return gen > 0 ? new EntityId(index, gen) : EntityId.Null;
        }

        // ── Ranged accessors ─────────────────────────────────────

        public int CharacterSlotEnd => GameConstants.MaxCharacters;
        public int NonCharacterSlotStart => GameConstants.MaxCharacters;

        // ── Tag helpers ──────────────────────────────────────────

        public bool HasTag(int entity, int tagBit) => (Tags[entity].ActiveTags & (1 << tagBit)) != 0;
        public void SetTag(int entity, int tagBit) => Tags[entity].ActiveTags |= (1 << tagBit);
        public void ClearTag(int entity, int tagBit) => Tags[entity].ActiveTags &= ~(1 << tagBit);
        public void ClearAllTags(int entity) => Tags[entity].ActiveTags = 0;

        // ── Stance derivation ────────────────────────────────────

        public Stance GetStance(int entity)
        {
            if (!Life[entity].IsAlive) return Stance.Dead;
            if (Status[entity].IsKnockedDown) return Stance.Knockdown;
            if (Status[entity].HitstunFrames > 0) return Stance.Hitstun;
            if (!Physics[entity].IsGrounded) return Stance.Airborne;
            if (HasTag(entity, GameConstants.TAG_CROUCH)) return Stance.Crouching;
            return Stance.Grounded;
        }

        // ── Collision-box accessors ──────────────────────────────

        public ref FRect HurtBox(int entity, int slot)
            => ref HurtBoxesFlat[entity * MaxHurtBoxesPerEntity + slot];

        public ref HitBoxEntry HitBox(int entity, int slot)
            => ref HitBoxesFlat[entity * MaxHitBoxesPerEntity + slot];

        public void ClearHitBoxes(int entity) => HitBoxCounts[entity] = 0;
        public void ClearHurtBoxes(int entity) => HurtBoxCounts[entity] = 0;

        // ── Blackboard ───────────────────────────────────────────

        public void SetBlackboard(int entity, int key, float value)
        {
            if (!IsValidBlackboardSlot(entity, key)) return;
            int index = BlackboardIndex(entity, key);
            BlackboardValues[index] = value;
            BlackboardHasValue[index] = true;
        }

        public float GetBlackboard(int entity, int key)
        {
            if (!IsValidBlackboardSlot(entity, key)) return 0f;
            int index = BlackboardIndex(entity, key);
            return BlackboardHasValue[index] ? BlackboardValues[index] : 0f;
        }

        public void ClearBlackboard(int entity)
        {
            if (entity < 0 || entity >= MaxEntities) return;
            int start = entity * GameConstants.MaxBlackboardKeys;
            for (int i = 0; i < GameConstants.MaxBlackboardKeys; i++)
            {
                BlackboardValues[start + i] = 0f;
                BlackboardHasValue[start + i] = false;
            }
        }

        private static bool IsValidBlackboardSlot(int entity, int key)
            => entity >= 0 && entity < MaxEntities
                && key >= 0 && key < GameConstants.MaxBlackboardKeys;

        private static int BlackboardIndex(int entity, int key)
            => entity * GameConstants.MaxBlackboardKeys + key;

        // ── Find helpers ─────────────────────────────────────────

        public int FindNearestOpponent(int charEntity)
        {
            if (!IsAliveSlot(charEntity)) return -1;
            int selfTeam = Identity[charEntity].Team;
            float selfX = Transform[charEntity].Position.X;

            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < GameConstants.MaxCharacters; i++)
            {
                if (i == charEntity) continue;
                if (!IsAliveSlot(i)) continue;
                if (Kinds[i] != EntityKind.Character) continue;
                if (Identity[i].Team == selfTeam) continue;
                if (!Life[i].IsAlive) continue;
                float dist = System.Math.Abs(selfX - Transform[i].Position.X);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }
    }
}
