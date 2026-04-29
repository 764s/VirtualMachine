using System;

namespace KOF98.Game
{
    public struct CharacterStatsDef
    {
        public float MaxHP;
        public float MaxPower;
    }

    public struct CharacterMovementDef
    {
        public float WalkSpeed;
        public float BackWalkSpeed;
        public float RunSpeed;
        public float JumpSpeedY;
    }

    public struct CharacterSkillLoadoutDef
    {
        public int[] SkillIds;
        public int IdleSkillIndex;
    }

    /// <summary>
    /// Generic append-only pool with stable integer handles. Count is
    /// write-protected; growth is internal. Hot-path readers index
    /// <see cref="Items"/> or use the <c>ref T this[int]</c> indexer.
    /// </summary>
    public sealed class Pool<T>
    {
        private const int InitialCapacity = 16;
        private T[] _items = new T[InitialCapacity];

        public int Count { get; private set; }

        /// <summary>Backing array. Length may exceed Count; only [0, Count) are valid.</summary>
        public T[] Items => _items;

        /// <summary>By-ref slot access. Caller is responsible for id ∈ [0, Count).</summary>
        public ref T this[int id] => ref _items[id];

        /// <summary>Append <paramref name="item"/>; returns its handle id.</summary>
        public int Alloc(in T item)
        {
            int id = Count;
            if (id == _items.Length) Array.Resize(ref _items, id * 2);
            _items[id] = item;
            Count = id + 1;
            return id;
        }

        /// <summary>Test/reset only.</summary>
        public void Clear()
        {
            Array.Clear(_items, 0, Count);
            Count = 0;
        }
    }

    /// <summary>
    /// Static-definition registry: one independent <see cref="Pool{T}"/> per
    /// def kind. Defs are intended to be shared (one Stats may be referenced
    /// by many Characters); runtime ECS components and <see cref="CharacterData"/>
    /// hold pool ids by integer. The catalog is NOT part of the snapshot.
    /// </summary>
    public static class GameCatalog
    {
        public const int InvalidId = -1;

        public static readonly Pool<CharacterData>            Characters    = new Pool<CharacterData>();
        public static readonly Pool<CharacterStatsDef>        Stats         = new Pool<CharacterStatsDef>();
        public static readonly Pool<CharacterMovementDef>     Movements     = new Pool<CharacterMovementDef>();
        public static readonly Pool<CharacterSkillLoadoutDef> SkillLoadouts = new Pool<CharacterSkillLoadoutDef>();
        public static readonly Pool<SkillDef>                 Skills        = new Pool<SkillDef>();

        /// <summary>Append a character; assigns CatalogCharacterId and returns it.</summary>
        public static int AllocCharacter(CharacterData data)
        {
            if (data == null) return InvalidId;
            int id = Characters.Alloc(data);
            data.CatalogCharacterId = id;
            return id;
        }

        /// <summary>Append a skill def; assigns CatalogSkillId and returns it.</summary>
        public static int AllocSkill(SkillDef def)
        {
            if (def == null) return InvalidId;
            int id = Skills.Alloc(def);
            def.CatalogSkillId = id;
            return id;
        }

        /// <summary>Null-tolerant accessor (id may be <see cref="InvalidId"/>).</summary>
        public static CharacterData GetCharacter(int id)
        {
            if ((uint)id >= (uint)Characters.Count) return null;
            return Characters[id];
        }

        /// <summary>Null-tolerant accessor (id may be <see cref="InvalidId"/>).</summary>
        public static SkillDef GetSkill(int id)
        {
            if ((uint)id >= (uint)Skills.Count) return null;
            return Skills[id];
        }

        /// <summary>Test/reset only. Add a line per new pool.</summary>
        public static void Clear()
        {
            Characters.Clear();
            Stats.Clear();
            Movements.Clear();
            SkillLoadouts.Clear();
            Skills.Clear();
        }
    }
}
