using System;
using System.IO;
using Raylib_cs;
using System.Numerics;

namespace KOF98.Game
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
    /// Font: Attempts to load a system CJK font for Chinese label support.
    /// Falls back to Raylib default font if no CJK font is available.
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

        // ── Control Panel ────────────────────────────────────────
        private const int PanelLeft = 120;
        private const int PanelTop = 60;
        private const int PanelWidth = 720;
        private const int PanelHeight = 480;
        private const int TabListWidth = 160;
        private const int TabItemHeight = 40;
        private const int ContentLeft = PanelLeft + TabListWidth + 1;
        private const int ContentTop = PanelTop + 50;
        private const int ContentWidth = PanelWidth - TabListWidth - 1;
        private const int ButtonHeight = 36;
        private const int ButtonWidth = 200;

        // ── Settings reference ───────────────────────────────────
        private GameSettings _settings;
        private int _selectedTab;
        private bool _prevTabKey;

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

        // ── Control Panel colors ─────────────────────────────────
        private static readonly Color PanelOverlay    = new Color(0, 0, 0, 180);
        private static readonly Color PanelBg         = new Color(40, 42, 50, 245);
        private static readonly Color PanelBorder     = new Color(100, 100, 120, 255);
        private static readonly Color TabBg           = new Color(50, 52, 62, 255);
        private static readonly Color TabSelected     = new Color(70, 100, 160, 255);
        private static readonly Color TabHover        = new Color(60, 65, 80, 255);
        private static readonly Color BtnBg           = new Color(60, 65, 80, 255);
        private static readonly Color BtnHover        = new Color(80, 90, 110, 255);
        private static readonly Color BtnActive       = new Color(70, 130, 200, 255);
        private static readonly Color BtnText         = new Color(220, 220, 230, 255);
        private static readonly Color ToggleOn        = new Color(50, 180, 80, 255);
        private static readonly Color ToggleOff       = new Color(120, 120, 130, 255);

        // ── Font ─────────────────────────────────────────────────
        private Font _font;
        private bool _fontLoaded;

        // Common system CJK font paths (tried in order)
        private static readonly string[] CjkFontPaths = new[]
        {
            // Windows
            @"C:\Windows\Fonts\msyh.ttc",    // Microsoft YaHei
            @"C:\Windows\Fonts\msyh.ttf",
            @"C:\Windows\Fonts\simsun.ttc",   // SimSun
            @"C:\Windows\Fonts\simhei.ttf",   // SimHei
            // macOS
            "/System/Library/Fonts/PingFang.ttc",
            "/System/Library/Fonts/STHeiti Light.ttc",
            // Linux
            "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
            "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
        };

        public RaylibGameView(GameSettings settings)
        {
            _settings = settings ?? new GameSettings();
        }

        public void Initialize(GameScene scene)
        {
            Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
            Raylib.InitWindow(ScreenWidth, ScreenHeight, "KOF98 Practice — Game Layer");
            Raylib.SetExitKey(0); // Disable ESC as window-close key; ESC is used to close UI panel
            Raylib.SetTargetFPS(60);
            LoadCjkFont();
        }

        public void Render(GameScene scene)
        {
            // Handle Tab key toggle for control panel
            bool tabDown = Raylib.IsKeyDown(KeyboardKey.Tab);
            if (tabDown && !_prevTabKey)
            {
                _settings.PanelOpen = !_settings.PanelOpen;
            }
            _prevTabKey = tabDown;

            Raylib.BeginDrawing();
            Raylib.ClearBackground(BgColor);

            DrawStage();
            DrawCharacters(scene);
            DrawProjectiles(scene);
            DrawHUD(scene);
            DrawDebugInfo(scene);

            if (_settings.PanelOpen)
            {
                DrawControlPanel();
            }

            Raylib.EndDrawing();
        }

        public void Shutdown()
        {
            if (_fontLoaded) Raylib.UnloadFont(_font);
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
            int textW = MeasureTextF(label, 12);
            DrawTextF(label, cx - textW / 2, cy - CharacterHeight - 14, 12, outlineColor);

            // State indicator
            string state = "";
            if (!ch.IsAlive) state = "击倒";
            else if (ch.HitstunFrames > 0) state = "受击";
            else if (ch.HasTag(GameConstants.TAG_BLOCK)) state = "防御";
            else if (ch.HasTag(GameConstants.TAG_ATTACK)) state = "攻击";
            else if (ch.HasTag(GameConstants.TAG_CROUCH)) state = "蹲下";
            else if (ch.HasTag(GameConstants.TAG_JUMP)) state = "跳跃";
            else if (ch.HasTag(GameConstants.TAG_WALK)) state = "移动";

            if (state.Length > 0)
            {
                int stateW = MeasureTextF(state, 10);
                DrawTextF(state, cx - stateW / 2, cy + 4, 10, outlineColor);
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
            DrawTextF("KOF98 练习模式", 10, 8, 20, new Color(220, 200, 160, 255));
            string frameText = $"帧: {scene.FrameNumber}  回合: {scene.RoundNumber}";
            int ftw = MeasureTextF(frameText, 16);
            DrawTextF(frameText, ScreenWidth - ftw - 10, 10, 16, DebugText);

            // HP bars
            int barW = 300;
            int barH = 20;
            int barY = 44;

            // P1 HP bar (left-aligned, fills from left)
            if (p1 != null)
            {
                DrawHPBar(StageLeft, barY, barW, barH, p1.HP, p1.Data.MaxHP, false);
                DrawTextF(p1.Data?.Name ?? "P1", StageLeft, barY - 16, 16, P1Color);

                // Power meter
                DrawPowerBar(StageLeft, barY + barH + 4, 120, 8, p1.Power, p1.Data.MaxPower);
            }

            // P2 HP bar (right-aligned, fills from right)
            if (p2 != null)
            {
                DrawHPBar(StageRight - barW, barY, barW, barH, p2.HP, p2.Data.MaxHP, true);
                string p2Name = p2.Data?.Name ?? "P2";
                int nameW = MeasureTextF(p2Name, 16);
                DrawTextF(p2Name, StageRight - nameW, barY - 16, 16, P2Color);

                // Power meter
                DrawPowerBar(StageRight - 120, barY + barH + 4, 120, 8, p2.Power, p2.Data.MaxPower);
            }

            // Box color legend
            int legendY = StageBottom + 8;
            DrawLegendEntry(StageLeft, legendY, PushBoxOutline, "推箱");
            DrawLegendEntry(StageLeft + 100, legendY, HurtBoxOutline, "受击框");
            DrawLegendEntry(StageLeft + 200, legendY, HitBoxOutline, "攻击框");
            DrawLegendEntry(StageLeft + 300, legendY, BlockBoxOutline, "防御框");

            // Controls hint
            DrawTextF("WASD:移动  J:轻拳  K:重拳  U:轻脚  I:重脚  Tab:设置  ESC:退出",
                StageLeft + 420, legendY, 12, new Color(120, 120, 120, 255));
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
            int textW = MeasureTextF(hpText, 14);
            DrawTextF(hpText, x + (w - textW) / 2, y + 3, 14, Color.White);
        }

        private void DrawPowerBar(int x, int y, int w, int h, float power, float maxPower)
        {
            Raylib.DrawRectangle(x, y, w, h, HPBarBg);
            float ratio = Math.Clamp(power / maxPower, 0f, 1f);
            Raylib.DrawRectangle(x, y, (int)(w * ratio), h, PowerBar);
            Raylib.DrawRectangleLines(x, y, w, h, new Color(100, 100, 100, 180));
        }

        private void DrawLegendEntry(int x, int y, Color color, string label)
        {
            Raylib.DrawRectangle(x, y + 2, 12, 12, color);
            DrawTextF(label, x + 16, y, 14, color);
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
                string info = $"[{(ch.Team == 0 ? "P1" : "P2")}] 位置=({ch.Body.Position.X:F2},{ch.Body.Position.Y:F2}) " +
                    $"速度=({ch.Body.Velocity.X:F2},{ch.Body.Velocity.Y:F2}) " +
                    $"朝向={ch.Facing} 着地={ch.IsGrounded} " +
                    $"技能={skillName}(帧{skillFrame}) 硬直={ch.HitstunFrames}";

                DrawTextF(info, StageLeft, y, 12, DebugText);
                y += 16;
            }

            string counts = $"效果: {scene.Effects.Count}  弹幕: {scene.Projectiles.Count}";
            DrawTextF(counts, StageLeft, y, 12, DebugText);
        }

        // ── Control Panel ────────────────────────────────────────

        private static readonly string[] TabNames = { "游戏设置" };

        private void DrawControlPanel()
        {
            // Semi-transparent overlay
            Raylib.DrawRectangle(0, 0, ScreenWidth, ScreenHeight, PanelOverlay);

            // Panel background
            Raylib.DrawRectangle(PanelLeft, PanelTop, PanelWidth, PanelHeight, PanelBg);
            Raylib.DrawRectangleLines(PanelLeft, PanelTop, PanelWidth, PanelHeight, PanelBorder);

            // Title
            DrawTextF("控制界面", PanelLeft + PanelWidth / 2 - 40, PanelTop + 12, 22, Color.White);

            // Close hint
            string closeHint = "Tab / ESC 关闭";
            int closeW = MeasureTextF(closeHint, 14);
            DrawTextF(closeHint, PanelLeft + PanelWidth - closeW - 16, PanelTop + 16, 14, new Color(140, 140, 150, 255));

            // Separator under title
            Raylib.DrawLine(PanelLeft + 8, PanelTop + 44, PanelLeft + PanelWidth - 8, PanelTop + 44, PanelBorder);

            // Tab list (left side)
            DrawTabList();

            // Vertical separator
            int sepX = PanelLeft + TabListWidth;
            Raylib.DrawLine(sepX, PanelTop + 44, sepX, PanelTop + PanelHeight - 8, PanelBorder);

            // Content area (right side)
            DrawTabContent();

            // Handle ESC to close
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                _settings.PanelOpen = false;
            }
        }

        private void DrawTabList()
        {
            int mx = Raylib.GetMouseX();
            int my = Raylib.GetMouseY();
            bool clicked = Raylib.IsMouseButtonPressed(MouseButton.Left);

            for (int i = 0; i < TabNames.Length; i++)
            {
                int tx = PanelLeft + 4;
                int ty = PanelTop + 50 + i * TabItemHeight;
                int tw = TabListWidth - 8;
                int th = TabItemHeight - 4;

                bool hover = mx >= tx && mx < tx + tw && my >= ty && my < ty + th;
                bool selected = i == _selectedTab;

                Color bg = selected ? TabSelected : (hover ? TabHover : TabBg);
                Raylib.DrawRectangle(tx, ty, tw, th, bg);
                if (selected)
                    Raylib.DrawRectangle(tx, ty, 3, th, new Color(100, 160, 240, 255));

                DrawTextF(TabNames[i], tx + 12, ty + (th - 16) / 2, 16, BtnText);

                if (hover && clicked) _selectedTab = i;
            }
        }

        private void DrawTabContent()
        {
            switch (_selectedTab)
            {
                case 0: DrawGameSettingsTab(); break;
            }
        }

        private void DrawGameSettingsTab()
        {
            int x = ContentLeft + 24;
            int y = ContentTop + 20;
            int spacing = 56;

            // AI Toggle
            DrawToggle(x, y, "AI 开关", "控制 AI 角色是否自动行动", _settings.AIEnabled, v => _settings.AIEnabled = v);
            y += spacing;

            // Auto-Revive Toggle
            DrawToggle(x, y, "自动满血复活", "血量归零时自动满血复活", _settings.AutoRevive, v => _settings.AutoRevive = v);
            y += spacing;

            // Restart Button
            DrawButton(x, y, ButtonWidth, ButtonHeight, "重新开始", "重置场景到初始状态", () => _settings.RestartRequested = true);
        }

        private void DrawToggle(int x, int y, string label, string desc, bool value, Action<bool> onChanged)
        {
            int mx = Raylib.GetMouseX();
            int my = Raylib.GetMouseY();
            bool clicked = Raylib.IsMouseButtonPressed(MouseButton.Left);

            // Label
            DrawTextF(label, x, y, 18, Color.White);

            // Description
            DrawTextF(desc, x, y + 22, 12, new Color(140, 140, 150, 255));

            // Toggle switch (right-aligned)
            int toggleW = 48;
            int toggleH = 24;
            int toggleX = x + ButtonWidth + 80;
            int toggleY = y + 4;

            bool hover = mx >= toggleX && mx < toggleX + toggleW && my >= toggleY && my < toggleY + toggleH;

            Color bg = value ? ToggleOn : ToggleOff;
            if (hover) bg = value ? new Color(60, 200, 100, 255) : new Color(140, 140, 150, 255);

            // Track
            Raylib.DrawRectangleRounded(
                new Rectangle(toggleX, toggleY, toggleW, toggleH), 0.5f, 8, bg);

            // Knob
            int knobR = toggleH / 2 - 3;
            int knobX = value ? toggleX + toggleW - knobR - 5 : toggleX + knobR + 5;
            int knobY = toggleY + toggleH / 2;
            Raylib.DrawCircle(knobX, knobY, knobR, Color.White);

            // Status text
            string statusText = value ? "ON" : "OFF";
            DrawTextF(statusText, toggleX + toggleW + 8, toggleY + 4, 14, bg);

            if (hover && clicked) onChanged(!value);
        }

        private void DrawButton(int x, int y, int w, int h, string label, string desc, Action onClick)
        {
            int mx = Raylib.GetMouseX();
            int my = Raylib.GetMouseY();
            bool clicked = Raylib.IsMouseButtonPressed(MouseButton.Left);

            bool hover = mx >= x && mx < x + w && my >= y && my < y + h;
            bool pressing = hover && Raylib.IsMouseButtonDown(MouseButton.Left);

            Color bg = pressing ? BtnActive : (hover ? BtnHover : BtnBg);
            Raylib.DrawRectangleRounded(new Rectangle(x, y, w, h), 0.15f, 4, bg);
            Raylib.DrawRectangleRoundedLines(new Rectangle(x, y, w, h), 0.15f, 4, 1f, PanelBorder);

            int textW = MeasureTextF(label, 16);
            DrawTextF(label, x + (w - textW) / 2, y + (h - 16) / 2, 16, BtnText);

            // Description to the right of button
            if (desc != null)
                DrawTextF(desc, x + w + 16, y + (h - 12) / 2, 12, new Color(140, 140, 150, 255));

            if (hover && clicked) onClick();
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

        // ── Font Loading ─────────────────────────────────────────

        private void LoadCjkFont()
        {
            // Collect all unique characters used in the game UI
            string uiChars = "KOF98练习模式帧回合击倒受击防御攻击蹲下跳跃移动推箱框" +
                             "位置速度朝向着地技能硬直效果弹幕轻拳重脚退出" +
                             "控制界面关闭游戏设置开关角色管理调试选项" +
                             "自动满血复活重新开始场景到初始状态" +
                             "是否行动量归零时";
            // Build codepoints: ASCII (32-126) + UI Chinese characters
            int asciiStart = 32, asciiEnd = 126;
            int asciiCount = asciiEnd - asciiStart + 1;
            int cjkCount = uiChars.Length;
            int count = asciiCount + cjkCount;
            int[] codepoints = new int[count];
            int idx = 0;

            for (int c = asciiStart; c <= asciiEnd; c++) codepoints[idx++] = c;
            for (int i = 0; i < uiChars.Length; i++) codepoints[idx++] = uiChars[i];

            foreach (string path in CjkFontPaths)
            {
                if (!File.Exists(path)) continue;
                _font = Raylib.LoadFontEx(path, 24, codepoints, count);
                if (_font.GlyphCount > 0)
                {
                    _fontLoaded = true;
                    Raylib.SetTextureFilter(_font.Texture, TextureFilter.Bilinear);
                    return;
                }
            }

            // Fallback: use default font
            _font = Raylib.GetFontDefault();
            _fontLoaded = false;
        }

        // ── Text Helpers ─────────────────────────────────────────

        /// <summary>Draw text using the loaded CJK font (or fallback).</summary>
        private void DrawTextF(string text, int x, int y, int fontSize, Color color)
        {
            if (_fontLoaded)
            {
                Raylib.DrawTextEx(_font, text, new Vector2(x, y), fontSize, 1, color);
            }
            else
            {
                Raylib.DrawText(text, x, y, fontSize, color);
            }
        }

        /// <summary>Measure text width using the loaded CJK font (or fallback).</summary>
        private int MeasureTextF(string text, int fontSize)
        {
            if (_fontLoaded)
            {
                Vector2 size = Raylib.MeasureTextEx(_font, text, fontSize, 1);
                return (int)size.X;
            }
            return Raylib.MeasureText(text, fontSize);
        }
    }
}
