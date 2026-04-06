namespace KOF98
{
    /// <summary>
    /// A runtime visual/gameplay effect instance.
    /// Lightweight: spawned by skill scripts, ticks down duration, then auto-removes.
    /// Extension point: in the future, complex effects can be VM instances.
    /// </summary>
    public struct EffectInstance
    {
        public int Id;
        public int EffectTypeId;     // Visual effect type identifier
        public int OwnerId;          // Character that spawned this effect
        public FVec2 Position;       // World position
        public int RemainingFrames;  // Frames until auto-removal
        public bool IsActive;

        /// <summary>VM instance ID for scripted effects (-1 = simple timer).</summary>
        public int VMInstanceId;
    }

    /// <summary>
    /// Manages all active effects in the scene.
    /// </summary>
    public class EffectManager
    {
        public EffectInstance[] Effects = new EffectInstance[GameConstants.MaxEffects];
        public int Count;
        private int _nextId;

        /// <summary>Spawn a new effect at the given position.</summary>
        public int Spawn(int effectTypeId, int ownerId, FVec2 position, int durationFrames)
        {
            if (Count >= GameConstants.MaxEffects) return -1;

            int slot = FindFreeSlot();
            if (slot < 0) return -1;

            Effects[slot] = new EffectInstance
            {
                Id = _nextId++,
                EffectTypeId = effectTypeId,
                OwnerId = ownerId,
                Position = position,
                RemainingFrames = durationFrames,
                IsActive = true,
                VMInstanceId = -1,
            };
            Count++;
            return Effects[slot].Id;
        }

        /// <summary>Update all effects: decrement timers, remove expired.</summary>
        public void Update()
        {
            for (int i = 0; i < Effects.Length; i++)
            {
                if (!Effects[i].IsActive) continue;

                Effects[i].RemainingFrames--;
                if (Effects[i].RemainingFrames <= 0)
                {
                    Effects[i].IsActive = false;
                    Count--;
                }
            }
        }

        /// <summary>Clear all effects.</summary>
        public void Clear()
        {
            for (int i = 0; i < Effects.Length; i++)
                Effects[i].IsActive = false;
            Count = 0;
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < Effects.Length; i++)
            {
                if (!Effects[i].IsActive) return i;
            }
            return -1;
        }
    }
}
