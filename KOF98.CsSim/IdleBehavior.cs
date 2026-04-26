using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// CS-simulation Idle behavior — pure C# mirror of skill_idle.ffs.
    ///
    /// FFS reference:
    ///   func main() {
    ///       while 1 { yield }
    ///   }
    ///
    /// The behavior never completes; it is interrupted by a higher-priority
    /// skill activating. Mirrors the looping <c>while 1 { yield }</c>.
    /// </summary>
    public sealed class IdleBehavior : ISkillBehavior
    {
        public void Spawn(SkillContext ctx) { /* nothing to set up */ }

        public SkillTickResult Tick(SkillContext ctx)
        {
            // Equivalent of `yield` — keep running.
            return SkillTickResult.Running;
        }

        public void Kill(SkillContext ctx) { /* no defer-equivalent */ }
    }
}
