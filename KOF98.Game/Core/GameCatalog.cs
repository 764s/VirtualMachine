using System.Collections.Generic;

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
    /// Static-definition registry. Holds immutable character defs and
    /// <see cref="SkillDef"/> instances and assigns each a stable, globally
    /// unique catalog id. ECS runtime components only keep catalog ids so
    /// mutable state remains snapshot-friendly.
    ///
    /// The catalog itself is NOT part of the snapshot. Save and load endpoints
    /// are expected to hold matching registered defs (same registration order).
    /// </summary>
    public static class GameCatalog
    {
        public const int InvalidId = -1;

        private static readonly List<CharacterData> _characters = new List<CharacterData>();
        private static readonly List<CharacterStatsDef> _characterStats = new List<CharacterStatsDef>();
        private static readonly List<CharacterMovementDef> _characterMovement = new List<CharacterMovementDef>();
        private static readonly List<CharacterSkillLoadoutDef> _characterSkillLoadouts = new List<CharacterSkillLoadoutDef>();
        private static readonly List<SkillDef> _skills = new List<SkillDef>();

        private static readonly CharacterStatsDef _defaultStats = new CharacterStatsDef
        {
            MaxHP = GameConstants.DefaultMaxHP,
            MaxPower = GameConstants.DefaultMaxPower,
        };

        private static readonly CharacterMovementDef _defaultMovement = new CharacterMovementDef
        {
            WalkSpeed = GameConstants.DefaultWalkSpeed,
            BackWalkSpeed = GameConstants.DefaultBackWalkSpeed,
            RunSpeed = GameConstants.DefaultRunSpeed,
            JumpSpeedY = GameConstants.DefaultJumpSpeedY,
        };

        private static readonly CharacterSkillLoadoutDef _defaultSkillLoadout = new CharacterSkillLoadoutDef
        {
            SkillIds = System.Array.Empty<int>(),
            IdleSkillIndex = InvalidId,
        };

        public static int CharacterCount => _characters.Count;
        public static int SkillCount => _skills.Count;

        /// <summary>Register a character definition bundle; assigns and returns its catalog id.</summary>
        public static int RegisterCharacter(
            CharacterData data,
            CharacterStatsDef stats,
            CharacterMovementDef movement,
            CharacterSkillLoadoutDef skillLoadout)
        {
            if (data == null) return InvalidId;
            int id = _characters.Count;
            data.CatalogCharacterId = id;
            _characters.Add(data);
            _characterStats.Add(stats);
            _characterMovement.Add(movement);
            _characterSkillLoadouts.Add(skillLoadout);
            return id;
        }

        /// <summary>Register a skill definition; assigns and returns its catalog id.</summary>
        public static int RegisterSkill(SkillDef def)
        {
            if (def == null) return InvalidId;
            int id = _skills.Count;
            def.CatalogSkillId = id;
            _skills.Add(def);
            return id;
        }

        public static CharacterData GetCharacter(int id)
        {
            if (id < 0 || id >= _characters.Count) return null;
            return _characters[id];
        }

        public static CharacterStatsDef GetCharacterStats(int id)
        {
            if (id < 0 || id >= _characterStats.Count) return _defaultStats;
            return _characterStats[id];
        }

        public static CharacterMovementDef GetCharacterMovement(int id)
        {
            if (id < 0 || id >= _characterMovement.Count) return _defaultMovement;
            return _characterMovement[id];
        }

        public static CharacterSkillLoadoutDef GetCharacterSkillLoadout(int id)
        {
            if (id < 0 || id >= _characterSkillLoadouts.Count) return _defaultSkillLoadout;
            return _characterSkillLoadouts[id];
        }

        public static SkillDef GetSkill(int id)
        {
            if (id < 0 || id >= _skills.Count) return null;
            return _skills[id];
        }

        /// <summary>Clear all registered defs. Test/reset only.</summary>
        public static void Clear()
        {
            _characters.Clear();
            _characterStats.Clear();
            _characterMovement.Clear();
            _characterSkillLoadouts.Clear();
            _skills.Clear();
        }
    }
}
