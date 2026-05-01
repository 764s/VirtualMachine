using System;

namespace KOF98.Game
{
    /// <summary>
    /// Pure-POD snapshot of <see cref="GameWorld"/> mutable state. Enables
    /// bit-stable save/load and round-trip determinism checks.
    ///
    /// Excluded by design:
    ///   - <see cref="GameWorld.ActiveBehaviors"/> — transient cache, rebuilt on
    ///     restore from <c>Skill[e].ActiveSkillId</c> via <see cref="GameCatalog"/>.
    ///   - Any external host state (<c>SceneInput</c>, view, settings).
    ///
    /// <see cref="GameCatalog"/> is treated as an immutable definition registry;
    /// restoring on a process whose catalog content differs is undefined.
    /// </summary>
    public struct GameSnapshot
    {
        // Slot bookkeeping
        public int[] Generations;
        public EntityKind[] Kinds;

        // Component arrays
        public IdentityComponent[] Identity;
        public LifeComponent[] Life;
        public TransformComponent[] Transform;
        public PhysicsComponent[] Physics;
        public InputComponent[] Input;
        public StatusComponent[] Status;
        public TagComponent[] Tags;

        public FRect[] PushBoxes;
        public FRect[] BlockBoxes;
        public FRect[] HurtBoxesFlat;
        public int[] HurtBoxCounts;
        public HitBoxEntry[] HitBoxesFlat;
        public int[] HitBoxCounts;

        public SkillComponent[] Skill;
        public FrameLineComponent[] FrameLine;
        public SceneFrameLineComponent SceneFrameLine;

        public ProjectileComponent[] Projectile;
        public EffectComponent[] Effect;
        public LifetimeComponent[] Lifetime;

        public float[] BlackboardValues;
        public bool[] BlackboardHasValue;

        public HitEvent[] PendingHits;
        public int PendingHitCount;

        public RoundStateComponent RoundState;

        public AIKind[] AIKinds;
        public AIStateComponent[] AIState;

        public int CharacterCount;
        public int ProjectileCount;
        public int EffectCount;

        /// <summary>
        /// Allocate buffers sized for a default <see cref="GameWorld"/>. Capture
        /// reuses these without reallocation.
        /// </summary>
        public static GameSnapshot CreateBuffer()
        {
            int N = GameWorld.MaxEntities;
            int H = GameWorld.MaxHurtBoxesPerEntity;
            int K = GameWorld.MaxHitBoxesPerEntity;
            int B = GameConstants.MaxBlackboardKeys;
            int C = GameConstants.MaxCharacters;
            return new GameSnapshot
            {
                Generations = new int[N],
                Kinds = new EntityKind[N],
                Identity = new IdentityComponent[N],
                Life = new LifeComponent[N],
                Transform = new TransformComponent[N],
                Physics = new PhysicsComponent[N],
                Input = new InputComponent[N],
                Status = new StatusComponent[N],
                Tags = new TagComponent[N],
                PushBoxes = new FRect[N],
                BlockBoxes = new FRect[N],
                HurtBoxesFlat = new FRect[N * H],
                HurtBoxCounts = new int[N],
                HitBoxesFlat = new HitBoxEntry[N * K],
                HitBoxCounts = new int[N],
                Skill = new SkillComponent[N],
                FrameLine = new FrameLineComponent[N],
                Projectile = new ProjectileComponent[N],
                Effect = new EffectComponent[N],
                Lifetime = new LifetimeComponent[N],
                BlackboardValues = new float[N * B],
                BlackboardHasValue = new bool[N * B],
                PendingHits = new HitEvent[GameConstants.MaxPendingHits],
                AIKinds = new AIKind[C],
                AIState = new AIStateComponent[C],
            };
        }

        public void CaptureFrom(GameWorld w)
        {
            Array.Copy(w.Generations, Generations, Generations.Length);
            Array.Copy(w.Kinds, Kinds, Kinds.Length);
            Array.Copy(w.Identity, Identity, Identity.Length);
            Array.Copy(w.Life, Life, Life.Length);
            Array.Copy(w.Transform, Transform, Transform.Length);
            Array.Copy(w.Physics, Physics, Physics.Length);
            Array.Copy(w.Input, Input, Input.Length);
            Array.Copy(w.Status, Status, Status.Length);
            Array.Copy(w.Tags, Tags, Tags.Length);
            Array.Copy(w.PushBoxes, PushBoxes, PushBoxes.Length);
            Array.Copy(w.BlockBoxes, BlockBoxes, BlockBoxes.Length);
            Array.Copy(w.HurtBoxesFlat, HurtBoxesFlat, HurtBoxesFlat.Length);
            Array.Copy(w.HurtBoxCounts, HurtBoxCounts, HurtBoxCounts.Length);
            Array.Copy(w.HitBoxesFlat, HitBoxesFlat, HitBoxesFlat.Length);
            Array.Copy(w.HitBoxCounts, HitBoxCounts, HitBoxCounts.Length);
            Array.Copy(w.Skill, Skill, Skill.Length);
            Array.Copy(w.FrameLine, FrameLine, FrameLine.Length);
            SceneFrameLine = w.SceneFrameLine;
            Array.Copy(w.Projectile, Projectile, Projectile.Length);
            Array.Copy(w.Effect, Effect, Effect.Length);
            Array.Copy(w.Lifetime, Lifetime, Lifetime.Length);
            Array.Copy(w.BlackboardValues, BlackboardValues, BlackboardValues.Length);
            Array.Copy(w.BlackboardHasValue, BlackboardHasValue, BlackboardHasValue.Length);
            Array.Copy(w.PendingHits, PendingHits, PendingHits.Length);
            PendingHitCount = w.PendingHitCount;
            RoundState = w.RoundState;
            Array.Copy(w.AIKinds, AIKinds, AIKinds.Length);
            Array.Copy(w.AIState, AIState, AIState.Length);
            CharacterCount = w.CharacterCount;
            ProjectileCount = w.ProjectileCount;
            EffectCount = w.EffectCount;
        }

        public void RestoreTo(GameWorld w)
        {
            Array.Copy(Generations, w.Generations, Generations.Length);
            Array.Copy(Kinds, w.Kinds, Kinds.Length);
            Array.Copy(Identity, w.Identity, Identity.Length);
            Array.Copy(Life, w.Life, Life.Length);
            Array.Copy(Transform, w.Transform, Transform.Length);
            Array.Copy(Physics, w.Physics, Physics.Length);
            Array.Copy(Input, w.Input, Input.Length);
            Array.Copy(Status, w.Status, Status.Length);
            Array.Copy(Tags, w.Tags, Tags.Length);
            Array.Copy(PushBoxes, w.PushBoxes, PushBoxes.Length);
            Array.Copy(BlockBoxes, w.BlockBoxes, BlockBoxes.Length);
            Array.Copy(HurtBoxesFlat, w.HurtBoxesFlat, HurtBoxesFlat.Length);
            Array.Copy(HurtBoxCounts, w.HurtBoxCounts, HurtBoxCounts.Length);
            Array.Copy(HitBoxesFlat, w.HitBoxesFlat, HitBoxesFlat.Length);
            Array.Copy(HitBoxCounts, w.HitBoxCounts, HitBoxCounts.Length);
            Array.Copy(Skill, w.Skill, Skill.Length);
            Array.Copy(FrameLine, w.FrameLine, FrameLine.Length);
            w.SceneFrameLine = SceneFrameLine;
            Array.Copy(Projectile, w.Projectile, Projectile.Length);
            Array.Copy(Effect, w.Effect, Effect.Length);
            Array.Copy(Lifetime, w.Lifetime, Lifetime.Length);
            Array.Copy(BlackboardValues, w.BlackboardValues, BlackboardValues.Length);
            Array.Copy(BlackboardHasValue, w.BlackboardHasValue, BlackboardHasValue.Length);
            Array.Copy(PendingHits, w.PendingHits, PendingHits.Length);
            w.PendingHitCount = PendingHitCount;
            w.RoundState = RoundState;
            Array.Copy(AIKinds, w.AIKinds, AIKinds.Length);
            Array.Copy(AIState, w.AIState, AIState.Length);
            w.CharacterCount = CharacterCount;
            w.ProjectileCount = ProjectileCount;
            w.EffectCount = EffectCount;

            // Rebuild transient ActiveBehaviors[] from skill ids.
            for (int e = 0; e < GameWorld.MaxEntities; e++)
            {
                if (w.IsAliveSlot(e) && w.Skill[e].IsSkillActive)
                {
                    int sid = w.Skill[e].ActiveSkillId;
                    var def = GameCatalog.GetSkill(sid);
                    w.ActiveBehaviors[e] = def?.BehaviorFactory?.Invoke();
                }
                else
                {
                    w.ActiveBehaviors[e] = null;
                }
            }
        }

        /// <summary>
        /// Field-by-field equality used by the round-trip determinism self-test.
        /// Returns the name of the first differing field (or null on full match).
        /// </summary>
        public static string FirstDiff(in GameSnapshot a, in GameSnapshot b)
        {
            if (!ArrEq(a.Generations, b.Generations)) return nameof(Generations);
            if (!ArrEq(a.Kinds, b.Kinds)) return nameof(Kinds);
            if (!ArrEq(a.Identity, b.Identity)) return nameof(Identity);
            if (!ArrEq(a.Life, b.Life)) return nameof(Life);
            if (!ArrEq(a.Transform, b.Transform)) return nameof(Transform);
            if (!ArrEq(a.Physics, b.Physics)) return nameof(Physics);
            if (!ArrEq(a.Input, b.Input)) return nameof(Input);
            if (!ArrEq(a.Status, b.Status)) return nameof(Status);
            if (!ArrEq(a.Tags, b.Tags)) return nameof(Tags);
            if (!ArrEq(a.PushBoxes, b.PushBoxes)) return nameof(PushBoxes);
            if (!ArrEq(a.BlockBoxes, b.BlockBoxes)) return nameof(BlockBoxes);
            if (!ArrEq(a.HurtBoxesFlat, b.HurtBoxesFlat)) return nameof(HurtBoxesFlat);
            if (!ArrEq(a.HurtBoxCounts, b.HurtBoxCounts)) return nameof(HurtBoxCounts);
            if (!ArrEq(a.HitBoxesFlat, b.HitBoxesFlat)) return nameof(HitBoxesFlat);
            if (!ArrEq(a.HitBoxCounts, b.HitBoxCounts)) return nameof(HitBoxCounts);
            if (!ArrEq(a.Skill, b.Skill)) return nameof(Skill);
            if (!ArrEq(a.FrameLine, b.FrameLine)) return nameof(FrameLine);
            if (!a.SceneFrameLine.Equals(b.SceneFrameLine)) return nameof(SceneFrameLine);
            if (!ArrEq(a.Projectile, b.Projectile)) return nameof(Projectile);
            if (!ArrEq(a.Effect, b.Effect)) return nameof(Effect);
            if (!ArrEq(a.Lifetime, b.Lifetime)) return nameof(Lifetime);
            if (!ArrEqFloat(a.BlackboardValues, b.BlackboardValues)) return nameof(BlackboardValues);
            if (!ArrEqBool(a.BlackboardHasValue, b.BlackboardHasValue)) return nameof(BlackboardHasValue);
            if (!ArrEq(a.PendingHits, b.PendingHits)) return nameof(PendingHits);
            if (a.PendingHitCount != b.PendingHitCount) return nameof(PendingHitCount);
            if (!a.RoundState.Equals(b.RoundState)) return nameof(RoundState);
            if (!ArrEqAIKind(a.AIKinds, b.AIKinds)) return nameof(AIKinds);
            if (!ArrEq(a.AIState, b.AIState)) return nameof(AIState);
            if (a.CharacterCount != b.CharacterCount) return nameof(CharacterCount);
            if (a.ProjectileCount != b.ProjectileCount) return nameof(ProjectileCount);
            if (a.EffectCount != b.EffectCount) return nameof(EffectCount);
            return null;
        }

        private static bool ArrEq<T>(T[] a, T[] b) where T : struct
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            var cmp = System.Collections.Generic.EqualityComparer<T>.Default;
            for (int i = 0; i < a.Length; i++) if (!cmp.Equals(a[i], b[i])) return false;
            return true;
        }

        private static bool ArrEqFloat(float[] a, float[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        private static bool ArrEqBool(bool[] a, bool[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool ArrEqAIKind(AIKind[] a, AIKind[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
