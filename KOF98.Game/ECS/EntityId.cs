using System;

namespace KOF98.Game
{
    /// <summary>
    /// Stable handle to an entity slot in <see cref="GameWorld"/>.
    /// Index = slot in the world arrays. Generation is incremented on each
    /// reuse so stale handles can be detected.
    /// </summary>
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public readonly int Index;
        public readonly int Generation;

        public EntityId(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public static EntityId Null => default;

        public bool IsValid => Generation > 0;

        public bool Equals(EntityId other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is EntityId e && Equals(e);
        public override int GetHashCode() => unchecked((Index * 397) ^ Generation);

        public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);
        public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);

        public override string ToString() => $"E#{Index}.{Generation}";
    }

    /// <summary>Kind of entity in a <see cref="GameWorld"/> slot.</summary>
    public enum EntityKind : byte
    {
        None = 0,
        Character = 1,
        Projectile = 2,
        Effect = 3,
    }
}
