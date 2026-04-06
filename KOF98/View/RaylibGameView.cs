using System;
using Raylib_cs;
using System.Numerics;

namespace KOF98
{
    /// <summary>
    /// Raylib-based game view with proper graphical rendering.
    /// Renders characters, collision boxes (with distinct colors), HUD, and debug info.
    ///
    /// Collision box color scheme (aesthetically chosen variant tones):
    ///   - Pushbox:  Soft blue (#4A90D9 / 74,144,217)  — neutral, boundary feel
    ///   - Hurtbox:  Muted green (#5CB85C / 92,184,92)  — passive, can-be-hit
    ///   - Hitbox:   Warm red (#D9534F / 217,83,79)     — aggressive, deals damage
    ///   - Blockbox: Amber yellow (#F0AD4E / 240,173,78) — defensive, guarding
    ///
    /// Window also handles keyboard input via Raylib.IsKeyDown() which correctly
    /// detects simultaneously held keys — fixing the console single-key limitation.
    /// </summary>
    public class RaylibGameView : IGameView
    {
        // ── Window dimensions ────────────────────────────────────
        private const int ScreenWidth = 960;
        private const int ScreenHeight = 600;

        // ── Stage rendering area (in pixels) ─────────────────────
        private const int StageLeft = 40;
        private const int StageRight = 920;
        private const int StageTop = 80;
        private const int StageBottom = 440;
        private const int StageWidth = StageRight - StageLeft;
        private const int StageHeight = StageBottom - StageTop;

        // ── Character rendering ─────────────────────────────────
        private const int CharacterWidth = 16;
        private const int CharacterHeight = 44;

        // ── Coordinate mapping ───────────────────────────────────
        private const int GroundMargin = 40;
        private const float VisibleWorldUnits = 4f;

        // ── Collision box colors (semi-transparent fill + solid outline) ──
        private static readonly Color PushBoxFill    = new Color(74, 144, 217, 40);
        private static readonly Color PushBoxOutline  = new Color(74, 144, 217, 160);
        private static readonly Color HurtBoxFill    = new Color(92, 184, 92, 40);
        private static readonly Color HurtBoxOutline  = new Color(92, 184, 92, 160);
        private static readonly Color HitBoxFill     = new Color(217, 83, 79, 50);
        private static readonly Color HitBoxOutline   = new Color(217, 83, 79, 200);
        private static readonly Color BlockBoxFill   = new Color(240, 173, 78, 40);
        private static readonly Color BlockBoxOutline = new Color(240, 173, 78, 160);

        // ── Character colors ─────────────────────────────────────
        private static readonly Color P1Color = new Color(60, 140, 230, 255);   // Blue
        private static readonly Color P2Color = new Color(230, 80, 60, 255);    // Red
        private static readonly Color P1Body  = new Color(60, 140, 230, 180);
        private static readonly Color P2Body  = new Color(230, 80, 60, 180);

        // ── HUD colors ───────────────────────────────────────────
        private static readonly Color HPBarBg   = new Color(60, 60, 60, 255);
        private static readonly Color HPBarP1   = new Color(50, 200, 50, 255);
        private static readonly Color HPBarP2   = new Color(50, 200, 50, 255);
        private static readonly Color HPBarLow  = new Color(220, 60, 40, 255);
        private static readonly Color PowerBar  = new Color(80, 160, 240, 255);
        private static readonly Color FloorColor = new Color(120, 100, 80, 255);
        private static readonly Color BgColor   = new Color(30, 32, 36, 255);
        private static readonly Color DebugText = new Color(180, 180, 180, 255);

        public void Initialize(GameScene scene)
        {
            Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
            Raylib.InitWindow(ScreenWidth, ScreenHeight, "KOF98 Practice — FFVM Exploration");
            Raylib.SetTargetFPS(60);
        }

        public void Render(GameScene scene)
        {
            Raylib.BeginDrawing();
            Raylib.ClearBackground(BgColor);

            DrawStage();
            DrawCharacters(scene);
            DrawProjectiles(scene);
            DrawHUD(scene);
            DrawDebugInfo(scene);

            Raylib.EndDrawing();
        }

        public void Shutdown()
        {
            Raylib.CloseWindow();
        }

        /// <summary>
        /// Collect keyboard input via Raylib.IsKeyDown() — supports simultaneous keys.
        /// Call this each frame before scene.Step().
        /// </summary>
        public static PlayerInput CollectInput(InputButton prevHeld)
        {
            InputButton held = InputButton.None;

            if (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up))    held |= InputButton.Up;
            if (Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down))  held |= InputButton.Down;
            if (Raylib.IsKeyDown(KeyboardKey.A) || Raylib.IsKeyDown(KeyboardKey.Left))  held |= InputButton.Left;
            if (Raylib.IsKeyDown(KeyboardKey.D) || Raylib.IsKeyDown(KeyboardKey.Right)) held |= InputButton.Right;
            if (Raylib.IsKeyDown(KeyboardKey.J)) held |= InputButton.LP;
            if (Raylib.IsKeyDown(KeyboardKey.K)) held |= InputButton.HP;
            if (Raylib.IsKeyDown(KeyboardKey.U)) held |= InputButton.LK;
            if (Raylib.IsKeyDown(KeyboardKey.I)) held |= InputButton.HK;

            return PlayerInput.ComputeEdge(prevHeld, held);
        }

        /// <summary>Check whether the Raylib window should close.</summary>
        public static bool ShouldClose() => Raylib.WindowShouldClose();

        // ── Stage ────────────────────────────────────────────────

        private void DrawStage()
        {
            // Stage background
            Raylib.DrawRectangle(StageLeft, StageTop, StageWidth, StageHeight, new Color(40, 42, 48, 255));

            // Ground line
            int groundY = WorldToScreenY(0f);
            Raylib.DrawLine(StageLeft, groundY, StageRight, groundY, FloorColor);
            Raylib.DrawLine(StageLeft, groundY + 1, StageRight, groundY + 1, FloorColor);

            // Stage bounds
            Raylib.DrawRectangleLines(StageLeft, StageTop, StageWidth, StageHeight, new Color(80, 80, 80, 255));
        }

        // ── Characters ───────────────────────────────────────────

        private void DrawCharacters(GameScene scene)
        {
            for (int i = 0; i < scene.Characters.Count; i++)
            {
                var ch = scene.Characters.Characters[i];
                if (ch == null) continue;

                bool isP1 = ch.Team == 0;

                // Draw collision boxes first (behind character)
                DrawPushBox(ch);
                DrawHurtBoxes(ch);
                DrawHitBoxes(ch);
                DrawBlockBox(ch);

                // Draw character body (simple rectangle)
                DrawCharacterBody(ch, isP1);
            }
        }

        private void DrawCharacterBody(Character ch, bool isP1)
        {
            int cx = WorldToScreenX(ch.Body.Position.X);
            int cy = WorldToScreenY(ch.Body.Position.Y);

            Color bodyColor = isP1 ? P1Body : P2Body;
            Color outlineColor = isP1 ? P1Color : P2Color;

            if (!ch.IsAlive)
            {
                bodyColor = new Color(100, 100, 100, 120);
                outlineColor = new Color(100, 100, 100, 200);
            }
            else if (ch.HitstunFrames > 0)
            {
                // Flash white during hitstun
                bodyColor = new Color(255, 200, 200, 180);
            }
            else if (ch.HasTag(GameConstants.TAG_ATTACK))
            {
                bodyColor = new Color(255, 160, 60, 200);
            }

            // Character capsule (simple rectangle representing body)
            Raylib.DrawRectangle(cx - CharacterWidth / 2, cy - CharacterHeight, CharacterWidth, CharacterHeight, bodyColor);
            Raylib.DrawRectangleLines(cx - CharacterWidth / 2, cy - CharacterHeight, CharacterWidth, CharacterHeight, outlineColor);

            // Facing indicator (small triangle)
            int tipX = cx + ch.FacingSign * 14;
            int tipY = cy - CharacterHeight / 2;
            Raylib.DrawTriangle(
                new Vector2(tipX, tipY),
                new Vector2(cx + ch.FacingSign * 4, tipY - 5),
                new Vector2(cx + ch.FacingSign * 4, tipY + 5),
                outlineColor);

            // Name label
            string label = isP1 ? "P1" : "P2";
            if (ch.Data?.Name != null) label = ch.Data.Name;
            int textW = Raylib.MeasureText(label, 12);
            Raylib.DrawText(label, cx - textW / 2, cy - CharacterHeight - 14, 12, outlineColor);

            // State indicator
            string state = "";
            if (!ch.IsAlive) state = "K.O.";
            else if (ch.HitstunFrames > 0) state = "HIT";
            else if (ch.HasTag(GameConstants.TAG_BLOCK)) state = "BLOCK";
            else if (ch.HasTag(GameConstants.TAG_ATTACK)) state = "ATK";
            else if (ch.HasTag(GameConstants.TAG_CROUCH)) state = "CROUCH";
            else if (ch.HasTag(GameConstants.TAG_JUMP)) state = "JUMP";
            else if (ch.HasTag(GameConstants.TAG_WALK)) state = "WALK";

            if (state.Length > 0)
            {
                int stateW = Raylib.MeasureText(state, 10);
                Raylib.DrawText(state, cx - stateW / 2, cy + 4, 10, outlineColor);
            }
        }

        // ── Collision Box Rendering ──────────────────────────────

        private void DrawPushBox(Character ch)
        {
            if (ch.PushBox.IsEmpty) return;
            DrawWorldRect(ch.PushBox, ch.Body.Position, ch.FacingSign, PushBoxFill, PushBoxOutline);
        }

        private void DrawHurtBoxes(Character ch)
        {
            for (int i = 0; i < ch.HurtBoxCount; i++)
            {
                if (ch.HurtBoxes[i].IsEmpty) continue;
                DrawWorldRect(ch.HurtBoxes[i], ch.Body.Position, ch.FacingSign, HurtBoxFill, HurtBoxOutline);
            }
        }

        private void DrawHitBoxes(Character ch)
        {
            for (int i = 0; i < ch.HitBoxCount; i++)
            {
                if (ch.HitBoxes[i].Box.IsEmpty) continue;
                DrawWorldRect(ch.HitBoxes[i].Box, ch.Body.Position, ch.FacingSign, HitBoxFill, HitBoxOutline);
            }
        }

        private void DrawBlockBox(Character ch)
        {
            if (ch.BlockBox.IsEmpty) return;
            DrawWorldRect(ch.BlockBox, ch.Body.Position, ch.FacingSign, BlockBoxFill, BlockBoxOutline);
        }

        private void DrawWorldRect(FRect box, FVec2 charPos, int facingSign,
            Color fill, Color outline)
        {
            box.GetWorldBounds(charPos, facingSign,
                out float minX, out float minY, out float maxX, out float maxY);

            int sx1 = WorldToScreenX(minX);
            int sy1 = WorldToScreenY(maxY); // maxY → top of screen (Y inverted)
            int sx2 = WorldToScreenX(maxX);
            int sy2 = WorldToScreenY(minY);

            int w = sx2 - sx1;
            int h = sy2 - sy1;
            if (w <= 0 || h <= 0) return;

            Raylib.DrawRectangle(sx1, sy1, w, h, fill);
            Raylib.DrawRectangleLines(sx1, sy1, w, h, outline);
        }

        // ── Projectiles ──────────────────────────────────────────

        private void DrawProjectiles(GameScene scene)
        {
            for (int i = 0; i < scene.Projectiles.Projectiles.Length; i++)
            {
                ref var proj = ref scene.Projectiles.Projectiles[i];
                if (!proj.IsActive) continue;

                int sx = WorldToScreenX(proj.Position.X);
                int sy = WorldToScreenY(proj.Position.Y);
                Raylib.DrawCircle(sx, sy, 6, new Color(255, 220, 60, 220));
                Raylib.DrawCircleLines(sx, sy, 6, new Color(255, 255, 120, 255));
            }
        }

        // ── HUD ──────────────────────────────────────────────────

        private void DrawHUD(GameScene scene)
        {
            var p1 = scene.Characters.Get(0);
            var p2 = scene.Characters.Get(1);

            // Title
            Raylib.DrawText("KOF98 PRACTICE", 10, 8, 20, new Color(220, 200, 160, 255));
            string frameText = $"Frame: {scene.FrameNumber}  Round: {scene.RoundNumber}";
            int ftw = Raylib.MeasureText(frameText, 16);
            Raylib.DrawText(frameText, ScreenWidth - ftw - 10, 10, 16, DebugText);

            // HP bars
            int barW = 300;
            int barH = 20;
            int barY = 44;

            // P1 HP bar (left-aligned, fills from left)
            if (p1 != null)
            {
                DrawHPBar(StageLeft, barY, barW, barH, p1.HP, p1.Data.MaxHP, false);
                Raylib.DrawText(p1.Data?.Name ?? "P1", StageLeft, barY - 16, 16, P1Color);

                // Power meter
                DrawPowerBar(StageLeft, barY + barH + 4, 120, 8, p1.Power, p1.Data.MaxPower);
            }

            // P2 HP bar (right-aligned, fills from right)
            if (p2 != null)
            {
                DrawHPBar(StageRight - barW, barY, barW, barH, p2.HP, p2.Data.MaxHP, true);
                string p2Name = p2.Data?.Name ?? "P2";
                int nameW = Raylib.MeasureText(p2Name, 16);
                Raylib.DrawText(p2Name, StageRight - nameW, barY - 16, 16, P2Color);

                // Power meter
                DrawPowerBar(StageRight - 120, barY + barH + 4, 120, 8, p2.Power, p2.Data.MaxPower);
            }

            // Box color legend
            int legendY = StageBottom + 8;
            DrawLegendEntry(StageLeft, legendY, PushBoxOutline, "Pushbox");
            DrawLegendEntry(StageLeft + 100, legendY, HurtBoxOutline, "Hurtbox");
            DrawLegendEntry(StageLeft + 200, legendY, HitBoxOutline, "Hitbox");
            DrawLegendEntry(StageLeft + 300, legendY, BlockBoxOutline, "Blockbox");

            // Controls hint
            Raylib.DrawText("WASD:Move  J:LP  K:HP  U:LK  I:HK  ESC:Quit",
                StageLeft + 440, legendY, 12, new Color(120, 120, 120, 255));
        }

        private void DrawHPBar(int x, int y, int w, int h, float hp, float maxHP, bool reverse)
        {
            Raylib.DrawRectangle(x, y, w, h, HPBarBg);
            float ratio = Math.Clamp(hp / maxHP, 0f, 1f);
            int fillW = (int)(w * ratio);
            Color barColor = ratio > 0.3f ? HPBarP1 : HPBarLow;

            if (reverse)
                Raylib.DrawRectangle(x + w - fillW, y, fillW, h, barColor);
            else
                Raylib.DrawRectangle(x, y, fillW, h, barColor);

            Raylib.DrawRectangleLines(x, y, w, h, new Color(160, 160, 160, 200));

            string hpText = $"{hp:F0}/{maxHP:F0}";
            int textW = Raylib.MeasureText(hpText, 14);
            Raylib.DrawText(hpText, x + (w - textW) / 2, y + 3, 14, Color.White);
        }

        private void DrawPowerBar(int x, int y, int w, int h, float power, float maxPower)
        {
            Raylib.DrawRectangle(x, y, w, h, HPBarBg);
            float ratio = Math.Clamp(power / maxPower, 0f, 1f);
            Raylib.DrawRectangle(x, y, (int)(w * ratio), h, PowerBar);
            Raylib.DrawRectangleLines(x, y, w, h, new Color(100, 100, 100, 180));
        }

        private static void DrawLegendEntry(int x, int y, Color color, string label)
        {
            Raylib.DrawRectangle(x, y + 2, 12, 12, color);
            Raylib.DrawText(label, x + 16, y, 14, color);
        }

        // ── Debug Info ───────────────────────────────────────────

        private void DrawDebugInfo(GameScene scene)
        {
            int y = StageBottom + 28;
            Raylib.DrawLine(StageLeft, y - 4, StageRight, y - 4, new Color(60, 60, 60, 255));

            for (int i = 0; i < scene.Characters.Count; i++)
            {
                var ch = scene.Characters.Characters[i];
                if (ch == null) continue;

                string skillName = ch.SkillMgr.ActiveSkill?.Def?.Name ?? "none";
                int skillFrame = ch.SkillMgr.ActiveSkill?.Frame ?? 0;
                string info = $"[{(ch.Team == 0 ? "P1" : "P2")}] pos=({ch.Body.Position.X:F2},{ch.Body.Position.Y:F2}) " +
                    $"vel=({ch.Body.Velocity.X:F2},{ch.Body.Velocity.Y:F2}) " +
                    $"facing={ch.Facing} grounded={ch.IsGrounded} " +
                    $"skill={skillName}(f{skillFrame}) hitstun={ch.HitstunFrames}";

                Raylib.DrawText(info, StageLeft, y, 12, DebugText);
                y += 16;
            }

            string counts = $"Effects: {scene.Effects.Count}  Projectiles: {scene.Projectiles.Count}";
            Raylib.DrawText(counts, StageLeft, y, 12, DebugText);
        }

        // ── Coordinate Mapping ───────────────────────────────────

        /// <summary>Map world X to screen X pixel.</summary>
        private int WorldToScreenX(float worldX)
        {
            float range = GameConstants.StageRightBound - GameConstants.StageLeftBound;
            float t = (worldX - GameConstants.StageLeftBound) / range;
            return StageLeft + (int)(t * StageWidth);
        }

        /// <summary>Map world Y to screen Y pixel (inverted: higher Y = higher on screen).</summary>
        private int WorldToScreenY(float worldY)
        {
            // Ground (Y=0) is at StageBottom - margin
            int groundScreen = StageBottom - (int)GroundMargin;
            float pixelsPerUnit = (float)(StageHeight - (int)GroundMargin) / VisibleWorldUnits;
            return groundScreen - (int)(worldY * pixelsPerUnit);
        }
    }
}
