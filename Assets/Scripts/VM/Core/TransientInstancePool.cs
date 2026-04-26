namespace FFVM
{
    /// <summary>
    /// VOM3 Phase1: a small pool of <see cref="VMInstanceState"/> slots reserved for
    /// synchronous host calls (<c>VMEngine.Call</c> / <c>ReadOnlyCall</c> / <c>StaticReadOnlyCall</c>).
    ///
    /// Slots in this pool are NOT registered in <see cref="InstancePool.ActiveList"/>,
    /// NOT visible to scripts (XCALL never sees these IDs), NOT included in snapshots,
    /// and NOT advanced by <see cref="VMWorld.Tick"/>. They exist only for the duration
    /// of a single <c>VMEngine.*Call</c> invocation.
    ///
    /// Single-threaded by contract; mirrors the VM-wide single-thread assumption.
    /// </summary>
    internal sealed unsafe class TransientInstancePool
    {
        private VMInstanceState[] _slots;
        private int[] _free;
        private int _top;

        public int Capacity => _slots.Length;

        public TransientInstancePool(int initialCapacity = 4)
        {
            if (initialCapacity < 1) initialCapacity = 1;
            _slots = new VMInstanceState[initialCapacity];
            _free = new int[initialCapacity];
            for (int i = 0; i < initialCapacity; i++)
                _free[i] = initialCapacity - 1 - i; // pop slot 0 first
            _top = initialCapacity;
        }

        /// <summary>Take an unused slot index. Grows on demand.</summary>
        public int Rent()
        {
            if (_top == 0) Grow();
            _top--;
            int id = _free[_top];
            // VOM11 A.1: lazy reset. Wholesale `_slots[id] = default;` (~1 KB memzero
            // per Rent) is removed. Safety relies on three layers:
            //   (1) VMEngine.InvokeOnTransient / Batch explicitly rewrite all
            //       hot-path control fields (IP / IsAlive / StateFlags /
            //       CallStackDepth=1 with sentinel CallFrame / CleanupDepth=0 /
            //       ErrorFlag / etc.) before ExecuteInstance runs.
            //   (2) Compiler enforces init-before-use for register reads.
            //   (3) DEBUG_VM_POISON build poisons Registers.Raw[*] so debug
            //       sessions and crash dumps surface the magic sentinel
            //       0xDEADBEEFDEADBEEF for any unintended residual read.
            // Newly-grown slots are zero by .NET array-alloc contract, so
            // first-time Rent of any slot still observes default state.
            PoisonRegistersIfDebug(id);
            return id;
        }

        /// <summary>
        /// VOM11 A.4 (simplified): under <c>DEBUG_VM_POISON</c> build, fill the
        /// register file with <c>0xDEADBEEFDEADBEEF</c> on every Rent. No read-side
        /// LOAD_REG assertion (would tax the hot path); the sentinel value alone
        /// makes residual reads visually obvious in debugger / panic logs.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG_VM_POISON")]
        private void PoisonRegistersIfDebug(int id)
        {
            const long Poison = unchecked((long)0xDEADBEEFDEADBEEFUL);
            fixed (long* p = _slots[id].Cpu.Registers.Raw)
            {
                for (int i = 0; i < VMConstants.MaxRegisters; i++)
                    p[i] = Poison;
            }
        }

        /// <summary>Return a slot to the pool. Caller must not retain refs after this.</summary>
        public void Return(int id)
        {
            _free[_top] = id;
            _top++;
        }

        public ref VMInstanceState Get(int id) => ref _slots[id];

        private void Grow()
        {
            int oldCap = _slots.Length;
            int newCap = oldCap * 2;
            System.Array.Resize(ref _slots, newCap);
            System.Array.Resize(ref _free, newCap);
            // Push newly created slots (oldCap..newCap-1) onto the free stack
            // in descending order so the lowest index is allocated first.
            for (int i = 0; i < oldCap; i++)
                _free[_top + i] = newCap - 1 - i;
            _top += oldCap;
        }
    }
}
