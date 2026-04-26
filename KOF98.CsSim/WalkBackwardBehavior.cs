using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// CS-simulation Walk-Backward behavior.
    ///
    /// Mirrors the backward branch of skill_walk_forward.ffs:
    ///   - Activates when the backward direction (relative to facing) is held
    ///     while grounded and Up is not held.
    ///   - Continues until input released or grounded leaves — Tick returns
    ///     Completed (equivalent of the FFS <c>return</c>).
    ///   - On Kill, velocity is zeroed (mirrors FFS <c>defer { SetVelocity(0,0) }</c>).
    /// </summary>
    public sealed class WalkBackwardBehavior : ISkillBehavior
    {
        public void Spawn(SkillContext ctx) { /* no entry side-effects */ }

        public SkillTickResult Tick(SkillContext ctx)
        {
            if (!ctx.IsGrounded()) return SkillTickResult.Completed;

            int inputDir = ctx.GetInputDir();
            if (inputDir >= 0) return SkillTickResult.Completed;

            // Backward velocity = -facing * BACK_WALK_SPEED
            float speed = ctx.Self.Data.BackWalkSpeed;
            float vx = -speed * ctx.GetFacingSign();
            ctx.SetVelocity(vx, 0f);

            return SkillTickResult.Running;
        }

        public void Kill(SkillContext ctx)
        {
            ctx.SetVelocity(0f, 0f);
        }
    }
}
