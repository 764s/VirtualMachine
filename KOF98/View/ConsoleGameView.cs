using System;
using System.IO;
using System.Text;

namespace KOF98
{
    /// <summary>
    /// Console-based game view that renders characters as position + physics boxes.
    /// Uses ASCII art to display the stage, characters, and HUD.
    /// No Unity dependency — purely System.Console based.
    ///
    /// Display:
    ///   - Top: HUD (HP bars, power meters, round/frame info)
    ///   - Middle: Stage grid with character markers and collision boxes
    ///   - Bottom: Debug info (positions, velocities, active skills)
    /// </summary>
    public class ConsoleGameView : IGameView
    {
        private const int ViewWidth = 72;
        private const int ViewHeight = 16;
        private const int StageRow = 12;  // Ground line row in the buffer
        private char[,] _buffer;
        private readonly StringBuilder _sb = new StringBuilder(ViewWidth * (ViewHeight + 10));

        public void Initialize(GameScene scene)
        {
            _buffer = new char[ViewHeight, ViewWidth];
            Console.Clear();
            Console.CursorVisible = false;
        }

        public void Render(GameScene scene)
        {
            _sb.Clear();

            // ── HUD ──────────────────────────────────────────────
            RenderHUD(scene);

            // ── Stage ────────────────────────────────────────────
            ClearBuffer();
            DrawStageFloor();
            DrawCharacters(scene);
            DrawProjectiles(scene);
            FlushBufferToSB();

            // ── Debug Info ───────────────────────────────────────
            RenderDebugInfo(scene);

            // ── Output ───────────────────────────────────────────
            try
            {
                Console.SetCursorPosition(0, 0);
                Console.Write(_sb);
            }
            catch (IOException)
            {
                // Console may not support cursor positioning in all environments
            }
        }

        public void Shutdown()
        {
            Console.CursorVisible = true;
        }

        // ── HUD ──────────────────────────────────────────────────

        private void RenderHUD(GameScene scene)
        {
            var p1 = scene.Characters.Get(0);
            var p2 = scene.Characters.Get(1);

            string p1Name = p1?.Data?.Name ?? "P1";
            string p2Name = p2?.Data?.Name ?? "P2";
            float p1HP = p1?.HP ?? 0;
            float p1Max = p1?.Data?.MaxHP ?? 1;
            float p2HP = p2?.HP ?? 0;
            float p2Max = p2?.Data?.MaxHP ?? 1;

            int barLen = 20;
            int p1Fill = (int)(p1HP / p1Max * barLen);
            int p2Fill = (int)(p2HP / p2Max * barLen);

            string p1Bar = new string('█', Math.Max(0, p1Fill)) + new string('░', Math.Max(0, barLen - p1Fill));
            string p2Bar = new string('█', Math.Max(0, p2Fill)) + new string('░', Math.Max(0, barLen - p2Fill));

            _sb.AppendLine($"╔══════════════════════════════════════════════════════════════════════╗");
            _sb.AppendLine($"║ {p1Name,-8} [{p1Bar}] {p1HP,5:F0}/{p1Max,-5:F0}  R{scene.RoundNumber}  {p2HP,5:F0}/{p2Max,-5:F0} [{p2Bar}] {p2Name,8} ║");
            _sb.AppendLine($"║ POW: {p1?.Power ?? 0:F1}                  F:{scene.FrameNumber,5}                  POW: {p2?.Power ?? 0:F1} ║");
            _sb.AppendLine($"╚══════════════════════════════════════════════════════════════════════╝");
        }

        // ── Stage Rendering ──────────────────────────────────────

        private void ClearBuffer()
        {
            for (int y = 0; y < ViewHeight; y++)
                for (int x = 0; x < ViewWidth; x++)
                    _buffer[y, x] = ' ';
        }

        private void DrawStageFloor()
        {
            for (int x = 0; x < ViewWidth; x++)
                _buffer[StageRow, x] = '═';
        }

        private void DrawCharacters(GameScene scene)
        {
            for (int i = 0; i < scene.Characters.Count; i++)
            {
                var ch = scene.Characters.Characters[i];
                if (ch == null) continue;

                int sx = WorldToScreenX(ch.Body.Position.X);
                int sy = WorldToScreenY(ch.Body.Position.Y);

                char marker = ch.Team == 0 ? 'P' : 'E';
                if (!ch.IsAlive) marker = 'X';
                else if (ch.HitstunFrames > 0) marker = '*';
                else if (ch.HasTag(GameConstants.TAG_BLOCK)) marker = 'B';
                else if (ch.HasTag(GameConstants.TAG_ATTACK)) marker = 'A';
                else if (ch.HasTag(GameConstants.TAG_CROUCH)) marker = 'c';

                // Draw pushbox outline
                DrawBox(ch.PushBox, ch.Body.Position, ch.FacingSign, ch.Team == 0 ? '│' : '┃');

                // Draw hitboxes
                for (int h = 0; h < ch.HitBoxCount; h++)
                    DrawBox(ch.HitBoxes[h].Box, ch.Body.Position, ch.FacingSign, '!');

                // Draw character marker
                PlotChar(sx, sy, marker);

                // Facing indicator
                int fDir = ch.FacingSign;
                PlotChar(sx + fDir, sy, ch.Facing == Direction.Right ? '>' : '<');
            }
        }

        private void DrawProjectiles(GameScene scene)
        {
            for (int i = 0; i < scene.Projectiles.Projectiles.Length; i++)
            {
                ref var proj = ref scene.Projectiles.Projectiles[i];
                if (!proj.IsActive) continue;

                int sx = WorldToScreenX(proj.Position.X);
                int sy = WorldToScreenY(proj.Position.Y);
                PlotChar(sx, sy, 'o');
            }
        }

        private void DrawBox(FRect box, FVec2 charPos, int facingSign, char ch)
        {
            if (box.IsEmpty) return;

            box.GetWorldBounds(charPos, facingSign,
                out float minX, out float minY, out float maxX, out float maxY);

            int sx1 = WorldToScreenX(minX);
            int sx2 = WorldToScreenX(maxX);
            int sy1 = WorldToScreenY(maxY); // Note: screen Y is inverted
            int sy2 = WorldToScreenY(minY);

            // Draw corners
            PlotChar(sx1, sy1, '+');
            PlotChar(sx2, sy1, '+');
            PlotChar(sx1, sy2, '+');
            PlotChar(sx2, sy2, '+');

            // Draw edges
            for (int x = sx1 + 1; x < sx2; x++)
            {
                PlotChar(x, sy1, '-');
                PlotChar(x, sy2, '-');
            }
            for (int y = sy1 + 1; y < sy2; y++)
            {
                PlotChar(sx1, y, ch);
                PlotChar(sx2, y, ch);
            }
        }

        private void FlushBufferToSB()
        {
            for (int y = 0; y < ViewHeight; y++)
            {
                for (int x = 0; x < ViewWidth; x++)
                    _sb.Append(_buffer[y, x]);
                _sb.AppendLine();
            }
        }

        // ── Debug Info ───────────────────────────────────────────

        private void RenderDebugInfo(GameScene scene)
        {
            _sb.AppendLine("──────────────────── Debug ────────────────────────────────────────────");
            for (int i = 0; i < scene.Characters.Count; i++)
            {
                var ch = scene.Characters.Characters[i];
                if (ch == null) continue;

                string skillName = ch.SkillMgr.ActiveSkill?.Def?.Name ?? "none";
                int skillFrame = ch.SkillMgr.ActiveSkill?.Frame ?? 0;
                _sb.AppendLine(
                    $"  [{(ch.Team == 0 ? "P1" : "P2")}] pos={ch.Body.Position} vel={ch.Body.Velocity} " +
                    $"facing={ch.Facing} grounded={ch.IsGrounded} " +
                    $"skill={skillName}(f{skillFrame}) hitstun={ch.HitstunFrames}");
            }
            _sb.AppendLine(
                $"  Effects: {scene.Effects.Count}  Projectiles: {scene.Projectiles.Count}");
        }

        // ── Coordinate Mapping ───────────────────────────────────

        private const int ScreenMargin = 2;
        private const float VerticalScale = 3.5f;

        /// <summary>Map world X to screen column.</summary>
        private int WorldToScreenX(float worldX)
        {
            float range = GameConstants.StageRightBound - GameConstants.StageLeftBound;
            float t = (worldX - GameConstants.StageLeftBound) / range;
            int usable = ViewWidth - ScreenMargin * 2 - 1;
            return Math.Clamp((int)(ScreenMargin + t * usable), 0, ViewWidth - 1);
        }

        /// <summary>Map world Y to screen row (inverted: higher Y = lower row number).</summary>
        private int WorldToScreenY(float worldY)
        {
            int row = StageRow - (int)(worldY * VerticalScale);
            return Math.Clamp(row, 0, ViewHeight - 1);
        }

        private void PlotChar(int x, int y, char c)
        {
            if (x >= 0 && x < ViewWidth && y >= 0 && y < ViewHeight)
                _buffer[y, x] = c;
        }
    }
}
