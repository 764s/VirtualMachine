using KOF98.Game;

namespace KOF98.CsSim
{
    /// <summary>
    /// CS-simulation Walk Forward behavior — pure C# mirror of skill_walkfwd.ffs.
    ///
    /// FFS reference:
    ///   func main() {
    ///       while 1 {
    ///           SetVelocity(Data.WalkSpeed * facingSign, 0)
    ///           yield
    ///       }
    ///   }
    /// </summary>
    public sealed class WalkForwardBehavior : ISkillBehavior
    {
        public void Spawn(SkillContext ctx) { }

        public SkillTickResult Tick(SkillContext ctx)
        {
            float speed = ctx.Data.WalkSpeed * ctx.GetFacingSign();
            ctx.SetVelocity(speed, 0f);
            return SkillTickResult.Running;
        }

        public void Kill(SkillContext ctx)
        {
            ctx.SetVelocity(0f, 0f);
        }
    }
}
