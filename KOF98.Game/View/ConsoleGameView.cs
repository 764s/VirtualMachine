using System;

namespace KOF98.Game
{
    /// <summary>
    /// Minimal ASCII view that prints a one-line per-character status. Designed
    /// for headless smoke testing and quick parity checks — no cursor magic, no
    /// double buffering, just stdout.
    /// </summary>
    public class ConsoleGameView : IGameView
    {
        public int Stride = 30;  // print every Nth frame

        public void Initialize(GameScene scene) { }

        public void Render(GameScene scene)
        {
            if (Stride > 0 && scene.FrameNumber % Stride != 0) return;

            var w = scene.World;
            Console.Write($"F{scene.FrameNumber,5}");
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;

                string name = w.Identity[e].Data?.Name ?? $"E{e}";
                var pos = w.Transform[e].Position;
                float hp = w.Life[e].HP;
                int hs = w.Status[e].HitstunFrames;
                string skill = w.Skill[e].ActiveSkill?.Def?.Name ?? "-";
                Console.Write($"  [{name} T{w.Identity[e].Team} hp{hp:F0} hs{hs} pos({pos.X:F2},{pos.Y:F2}) {skill}]");
            }
            Console.WriteLine();
        }

        public void Shutdown() { }
    }
}
