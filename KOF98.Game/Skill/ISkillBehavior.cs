namespace KOF98.Game
{
    /// <summary>
    /// Result of a single behavior tick.
    /// </summary>
    public enum SkillTickResult : byte
    {
        /// <summary>Behavior is still running. Continue ticking next frame.</summary>
        Running = 0,
        /// <summary>Behavior has finished. Host should deactivate the skill.</summary>
        Completed = 1,
    }

    /// <summary>
    /// Behavior implemented by every skill at runtime — corresponds to what a
    /// VM instance does when driving a skill. Mirrors VMWorld's per-instance
    /// lifecycle (Spawn / Tick / Kill) so that:
    ///
    /// - The CS simulation layer can implement this with plain C# objects,
    ///   serving as a parallel implementation and a performance baseline.
    /// - The VM layer can implement this by wrapping a VMWorld instance —
    ///   Spawn ↔ VMWorld.SpawnInstance, Tick ↔ VMWorld.TickInstance,
    ///   Kill ↔ VMWorld.KillInstance.
    ///
    /// This interface intentionally does NOT contain the skill running
    /// framework (selection, transitions, candidate pools, etc.).
    /// That framework lives in <see cref="SkillManager"/> on the game layer.
    /// </summary>
    public interface ISkillBehavior
    {
        /// <summary>
        /// Called once when the skill is activated, before the first <see cref="Tick"/>.
        /// Mirrors VM "instance spawned" semantics — equivalent of entering
        /// a script's <c>func main()</c> body.
        /// </summary>
        void Spawn(SkillContext ctx);

        /// <summary>
        /// Called once per simulation frame while the skill is active.
        /// Returning <see cref="SkillTickResult.Completed"/> tells the host
        /// to deactivate the skill — equivalent of the script returning from
        /// <c>main</c> (or the FFS pattern <c>if (cond) { return }</c>).
        /// Returning <see cref="SkillTickResult.Running"/> is equivalent of
        /// reaching a <c>yield</c> in the script.
        /// </summary>
        SkillTickResult Tick(SkillContext ctx);

        /// <summary>
        /// Called once when the skill is deactivated — either after a
        /// <see cref="SkillTickResult.Completed"/> tick, or when forcibly
        /// interrupted by a higher-priority skill. Mirrors the FFS
        /// <c>defer { ... }</c> block executed on instance kill.
        /// </summary>
        void Kill(SkillContext ctx);
    }
}
