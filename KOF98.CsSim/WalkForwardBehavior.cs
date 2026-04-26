using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// CS-simulation Walk-Forward behavior.
    ///
    /// Mirrors the forward branch of skill_walk_forward.ffs:
    ///   - Activates when the forward direction (relative to facing) is held
    ///     while grounded and Up is not held.
    ///   - Continues until the directional input is released or the character
    ///     leaves the ground — at which point Tick returns Completed
    ///     (equivalent of the FFS <c>return</c>).
    ///   - On Kill, velocity is zeroed, mirroring the FFS <c>defer { SetVelocity(0,0) }</c>.
    /// </summary>
    public sealed class WalkForwardBehavior : ISkillBehavior
    {
        public void Spawn(SkillContext ctx) { /* no entry side-effects */ }

        public SkillTickResult Tick(SkillContext ctx)
        {
            // Continuation guards (mirror FFS first-thing-in-loop checks)
            if (!ctx.IsGrounded()) return SkillTickResult.Completed;

            int inputDir = ctx.GetInputDir();
            if (inputDir <= 0) return SkillTickResult.Completed;

            // Forward velocity = +facing * WALK_SPEED
            float speed = ctx.Self.Data.WalkSpeed;
            float vx = speed * ctx.GetFacingSign();
            ctx.SetVelocity(vx, 0f);

            return SkillTickResult.Running;
        }

        public void Kill(SkillContext ctx)
        {
            // FFS defer { SetVelocity(0.0, 0.0) }
            ctx.SetVelocity(0f, 0f);
        }
    }
}
