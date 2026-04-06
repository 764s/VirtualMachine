using System;

namespace KOF98
{
    /// <summary>
    /// Manages all characters in the scene. Fixed-capacity array.
    /// </summary>
    public class CharacterManager
    {
        public Character[] Characters = new Character[GameConstants.MaxCharacters];
        public int Count;

        /// <summary>Create a new character and return its slot ID.</summary>
        public int CreateCharacter(int team, CharacterData data, FVec2 startPos)
        {
            if (Count >= GameConstants.MaxCharacters)
                throw new InvalidOperationException("Character pool full");

            int id = Count;
            var ch = new Character(id, team, data);
            ch.Body.Position = startPos;
            Characters[id] = ch;
            Count++;
            return id;
        }

        public Character Get(int id)
        {
            if (id < 0 || id >= Count) return null;
            return Characters[id];
        }

        /// <summary>Find the nearest opponent for a character.</summary>
        public Character FindNearestOpponent(int charId)
        {
            var self = Characters[charId];
            if (self == null) return null;

            Character nearest = null;
            float nearestDist = float.MaxValue;

            for (int i = 0; i < Count; i++)
            {
                var other = Characters[i];
                if (other == null || i == charId || other.Team == self.Team || !other.IsAlive)
                    continue;
                float dist = self.Body.Position.HDistanceTo(other.Body.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = other;
                }
            }
            return nearest;
        }

        /// <summary>Reset all characters for a new round.</summary>
        public void ResetForRound()
        {
            for (int i = 0; i < Count; i++)
            {
                var ch = Characters[i];
                if (ch == null) continue;
                ch.HP = ch.Data.MaxHP;
                ch.IsAlive = true;
                ch.HitstunFrames = 0;
                ch.BlockstunFrames = 0;
                ch.IsKnockedDown = false;
                ch.ClearAllTags();
                ch.ClearHitBoxes();
                ch.SkillMgr.ClearAll();
            }
        }
    }
}
