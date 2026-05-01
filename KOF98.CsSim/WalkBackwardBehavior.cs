using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// CS-simulation Walk Backward behavior — mirror of skill_walkback.ffs.
    /// </summary>
    public sealed class WalkBackwardBehavior : ISkillBehavior
    {
        public void Spawn(SkillContext ctx) { }

        public SkillTickResult Tick(SkillContext ctx)
        {
            float speed = -ctx.Movement.BackWalkSpeed * ctx.GetFacingSign();
            ctx.SetVelocity(speed, 0f);
            return SkillTickResult.Running;
        }

        public void Kill(SkillContext ctx)
        {
            ctx.SetVelocity(0f, 0f);
        }
    }
}
