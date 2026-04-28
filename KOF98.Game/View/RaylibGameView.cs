using System;
using System.IO;
using Raylib_cs;
using System.Numerics;

namespace KOF98.Game
{
    /// <summary>
    /// Raylib-based ECS view. Re-implementation of the legacy
    /// <c>KOF98.View.RaylibGameView</c> against the ECS data layout — every
    /// per-frame read goes through <see cref="GameWorld"/> instead of the
    /// retired Character / SkillManager / ProjectileManager objects.
    ///
    /// Collision box color scheme:
    ///   Pushbox  blue / Hurtbox  green / Hitbox  red / Blockbox  amber
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
        private static readonly Color PushBoxFill     = new Color(74, 144, 217, 40);
        private static readonly Color PushBoxOutline  = new Color(74, 144, 217, 160);
        private static readonly Color HurtBoxFill     = new Color(92, 184, 92, 40);
        private static readonly Color HurtBoxOutline  = new Color(92, 184, 92, 160);
        private static readonly Color HitBoxFill      = new Color(217, 83, 79, 50);
        private static readonly Color HitBoxOutline   = new Color(217, 83, 79, 200);
        private static readonly Color BlockBoxFill    = new Color(240, 173, 78, 40);
        private static readonly Color BlockBoxOutline = new Color(240, 173, 78, 160);

        // ── Character colors ─────────────────────────────────────
        private static readonly Color P1Color = new Color(60, 140, 230, 255);
        private static readonly Color P2Color = new Color(230, 80, 60, 255);
        private static readonly Color P1Body  = new Color(60, 140, 230, 180);
        private static readonly Color P2Body  = new Color(230, 80, 60, 180);

        // ── HUD colors ───────────────────────────────────────────
        private static readonly Color HPBarBg    = new Color(60, 60, 60, 255);
        private static readonly Color HPBarOk    = new Color(50, 200, 50, 255);
        private static readonly Color HPBarLow   = new Color(220, 60, 40, 255);
        private static readonly Color PowerBar   = new Color(80, 160, 240, 255);
        private static readonly Color FloorColor = new Color(120, 100, 80, 255);
        private static readonly Color BgColor    = new Color(30, 32, 36, 255);
        private static readonly Color DebugText  = new Color(180, 180, 180, 255);
        private static readonly Color FrozenTint = new Color(255, 220, 60, 220);

        // ── Control panel colors ─────────────────────────────────
        private static readonly Color PanelOverlay = new Color(0, 0, 0, 180);
        private static readonly Color PanelBg      = new Color(40, 42, 50, 245);
        private static readonly Color PanelBorder  = new Color(100, 100, 120, 255);
        private static readonly Color TabBg        = new Color(50, 52, 62, 255);
        private static readonly Color TabSelected  = new Color(70, 100, 160, 255);
        private static readonly Color TabHover     = new Color(60, 65, 80, 255);
        private static readonly Color BtnBg        = new Color(60, 65, 80, 255);
        private static readonly Color BtnHover     = new Color(80, 90, 110, 255);
        private static readonly Color BtnActive    = new Color(70, 130, 200, 255);
        private static readonly Color BtnText      = new Color(220, 220, 230, 255);
        private static readonly Color ToggleOn     = new Color(50, 180, 80, 255);
        private static readonly Color ToggleOff    = new Color(120, 120, 130, 255);

        // ── Font ─────────────────────────────────────────────────
        private Font _font;
        private bool _fontLoaded;

        private static readonly string[] CjkFontPaths = new[]
        {
            @"C:\Windows\Fonts\msyh.ttc",
            @"C:\Windows\Fonts\msyh.ttf",
            @"C:\Windows\Fonts\simsun.ttc",
            @"C:\Windows\Fonts\simhei.ttf",
            "/System/Library/Fonts/PingFang.ttc",
            "/System/Library/Fonts/STHeiti Light.ttc",
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
            Raylib.InitWindow(ScreenWidth, ScreenHeight, "KOF98 Practice — ECS");
            Raylib.SetExitKey(0);
            Raylib.SetTargetFPS(60);
            LoadCjkFont();
        }

        public void Render(GameScene scene)
        {
            bool tabDown = Raylib.IsKeyDown(KeyboardKey.Tab);
            if (tabDown && !_prevTabKey)
                _settings.PanelOpen = !_settings.PanelOpen;
            _prevTabKey = tabDown;

            Raylib.BeginDrawing();
            Raylib.ClearBackground(BgColor);

            DrawStage();
            DrawCharacters(scene);
            DrawProjectiles(scene);
            DrawHUD(scene);
            DrawDebugInfo(scene);

            if (_settings.PanelOpen)
                DrawControlPanel();

            Raylib.EndDrawing();
        }

        public void Shutdown()
        {
            if (_fontLoaded) Raylib.UnloadFont(_font);
            Raylib.CloseWindow();
        }

        /// <summary>Collect keyboard input for P1 via Raylib.IsKeyDown().</summary>
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

        public static bool ShouldClose() => Raylib.WindowShouldClose();

        // ── Stage ────────────────────────────────────────────────

        private void DrawStage()
        {
            Raylib.DrawRectangle(StageLeft, StageTop, StageWidth, StageHeight,
                new Color(40, 42, 48, 255));

            int groundY = WorldToScreenY(0f);
            Raylib.DrawLine(StageLeft, groundY, StageRight, groundY, FloorColor);
            Raylib.DrawLine(StageLeft, groundY + 1, StageRight, groundY + 1, FloorColor);

            Raylib.DrawRectangleLines(StageLeft, StageTop, StageWidth, StageHeight,
                new Color(80, 80, 80, 255));
        }

        // ── Characters ───────────────────────────────────────────

        private void DrawCharacters(GameScene scene)
        {
            var w = scene.World;
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;

                bool isP1 = w.Identity[e].Team == 0;
                DrawPushBox(w, e);
                DrawHurtBoxes(w, e);
                DrawHitBoxes(w, e);
                DrawBlockBox(w, e);
                DrawCharacterBody(scene, w, e, isP1);
            }
        }

        private void DrawCharacterBody(GameScene scene, GameWorld w, int e, bool isP1)
        {
            ref var tr = ref w.Transform[e];
            int cx = WorldToScreenX(tr.Position.X);
            int cy = WorldToScreenY(tr.Position.Y);

            Color bodyColor = isP1 ? P1Body : P2Body;
            Color outlineColor = isP1 ? P1Color : P2Color;

            bool frozen = scene.IsCharacterFrozen(e);

            if (!w.Life[e].IsAlive)
            {
                bodyColor = new Color(100, 100, 100, 120);
                outlineColor = new Color(100, 100, 100, 200);
            }
            else if (frozen)
            {
                bodyColor = FrozenTint;
            }
            else if (w.Status[e].HitstunFrames > 0)
            {
                bodyColor = new Color(255, 200, 200, 180);
            }
            else if (w.HasTag(e, GameConstants.TAG_ATTACK))
            {
                bodyColor = new Color(255, 160, 60, 200);
            }

            Raylib.DrawRectangle(cx - CharacterWidth / 2, cy - CharacterHeight,
                CharacterWidth, CharacterHeight, bodyColor);
            Raylib.DrawRectangleLines(cx - CharacterWidth / 2, cy - CharacterHeight,
                CharacterWidth, CharacterHeight, outlineColor);

            int facingSign = tr.FacingSign;
            int tipX = cx + facingSign * 14;
            int tipY = cy - CharacterHeight / 2;
            Raylib.DrawTriangle(
                new Vector2(tipX, tipY),
                new Vector2(cx + facingSign * 4, tipY - 5),
                new Vector2(cx + facingSign * 4, tipY + 5),
                outlineColor);

            string label = isP1 ? "P1" : "P2";
            var data = w.Identity[e].Data;
            if (data?.Name != null) label = data.Name;
            int textW = MeasureTextF(label, 12);
            DrawTextF(label, cx - textW / 2, cy - CharacterHeight - 14, 12, outlineColor);

            string state = "";
            if (!w.Life[e].IsAlive) state = "击倒";
            else if (frozen) state = "停顿";
            else if (w.Status[e].HitstunFrames > 0) state = "受击";
            else if (w.HasTag(e, GameConstants.TAG_BLOCK)) state = "防御";
            else if (w.HasTag(e, GameConstants.TAG_ATTACK)) state = "攻击";
            else if (w.HasTag(e, GameConstants.TAG_CROUCH)) state = "蹲下";
            else if (w.HasTag(e, GameConstants.TAG_JUMP)) state = "跳跃";
            else if (w.HasTag(e, GameConstants.TAG_WALK)) state = "移动";

            if (state.Length > 0)
            {
                int stateW = MeasureTextF(state, 10);
                DrawTextF(state, cx - stateW / 2, cy + 4, 10, outlineColor);
            }
        }

        // ── Collision Box Rendering ──────────────────────────────

        private void DrawPushBox(GameWorld w, int e)
        {
            if (w.PushBoxes[e].IsEmpty) return;
            DrawWorldRect(w.PushBoxes[e], w.Transform[e].Position, w.Transform[e].FacingSign,
                PushBoxFill, PushBoxOutline);
        }

        private void DrawHurtBoxes(GameWorld w, int e)
        {
            int n = w.HurtBoxCounts[e];
            for (int i = 0; i < n; i++)
            {
                ref var box = ref w.HurtBox(e, i);
                if (box.IsEmpty) continue;
                DrawWorldRect(box, w.Transform[e].Position, w.Transform[e].FacingSign,
                    HurtBoxFill, HurtBoxOutline);
            }
        }

        private void DrawHitBoxes(GameWorld w, int e)
        {
            int n = w.HitBoxCounts[e];
            for (int i = 0; i < n; i++)
            {
                ref var entry = ref w.HitBox(e, i);
                if (entry.Box.IsEmpty) continue;
                DrawWorldRect(entry.Box, w.Transform[e].Position, w.Transform[e].FacingSign,
                    HitBoxFill, HitBoxOutline);
            }
        }

        private void DrawBlockBox(GameWorld w, int e)
        {
            if (w.BlockBoxes[e].IsEmpty) return;
            DrawWorldRect(w.BlockBoxes[e], w.Transform[e].Position, w.Transform[e].FacingSign,
                BlockBoxFill, BlockBoxOutline);
        }

        private void DrawWorldRect(FRect box, FVec2 charPos, int facingSign,
            Color fill, Color outline)
        {
            box.GetWorldBounds(charPos, facingSign,
                out float minX, out float minY, out float maxX, out float maxY);

            int sx1 = WorldToScreenX(minX);
            int sy1 = WorldToScreenY(maxY);
            int sx2 = WorldToScreenX(maxX);
            int sy2 = WorldToScreenY(minY);

            int wPx = sx2 - sx1;
            int hPx = sy2 - sy1;
            if (wPx <= 0 || hPx <= 0) return;

            Raylib.DrawRectangle(sx1, sy1, wPx, hPx, fill);
            Raylib.DrawRectangleLines(sx1, sy1, wPx, hPx, outline);
        }

        // ── Projectiles ──────────────────────────────────────────

        private void DrawProjectiles(GameScene scene)
        {
            var w = scene.World;
            for (int e = w.NonCharacterSlotStart; e < GameWorld.MaxEntities; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Projectile) continue;
                ref var tr = ref w.Transform[e];
                int sx = WorldToScreenX(tr.Position.X);
                int sy = WorldToScreenY(tr.Position.Y);
                Raylib.DrawCircle(sx, sy, 6, new Color(255, 220, 60, 220));
                Raylib.DrawCircleLines(sx, sy, 6, new Color(255, 255, 120, 255));
            }
        }

        // ── HUD ──────────────────────────────────────────────────

        private void DrawHUD(GameScene scene)
        {
            var w = scene.World;
            int p1 = FindCharacterByTeam(w, 0);
            int p2 = FindCharacterByTeam(w, 1);

            DrawTextF("KOF98 练习模式 — ECS", 10, 8, 20, new Color(220, 200, 160, 255));
            string frameText = $"线F: {scene.GlobalFrame}  回合: {scene.RoundNumber}";
            if (scene.GlobalPauseFrames > 0) frameText += $"  时停{scene.GlobalPauseFrames}f";
            int ftw = MeasureTextF(frameText, 16);
            DrawTextF(frameText, ScreenWidth - ftw - 10, 10, 16, DebugText);

            int barW = 300;
            int barH = 20;
            int barY = 44;

            if (p1 >= 0)
            {
                var data1 = w.Identity[p1].Data;
                DrawHPBar(StageLeft, barY, barW, barH, w.Life[p1].HP,
                    data1?.MaxHP ?? GameConstants.DefaultMaxHP, false);
                DrawTextF(data1?.Name ?? "P1", StageLeft, barY - 16, 16, P1Color);
                DrawPowerBar(StageLeft, barY + barH + 4, 120, 8, w.Life[p1].Power,
                    data1?.MaxPower ?? GameConstants.DefaultMaxPower);
            }

            if (p2 >= 0)
            {
                var data2 = w.Identity[p2].Data;
                DrawHPBar(StageRight - barW, barY, barW, barH, w.Life[p2].HP,
                    data2?.MaxHP ?? GameConstants.DefaultMaxHP, true);
                string p2Name = data2?.Name ?? "P2";
                int nameW = MeasureTextF(p2Name, 16);
                DrawTextF(p2Name, StageRight - nameW, barY - 16, 16, P2Color);
                DrawPowerBar(StageRight - 120, barY + barH + 4, 120, 8, w.Life[p2].Power,
                    data2?.MaxPower ?? GameConstants.DefaultMaxPower);
            }

            int legendY = StageBottom + 8;
            DrawLegendEntry(StageLeft,       legendY, PushBoxOutline,  "推箱");
            DrawLegendEntry(StageLeft + 100, legendY, HurtBoxOutline,  "受击框");
            DrawLegendEntry(StageLeft + 200, legendY, HitBoxOutline,   "攻击框");
            DrawLegendEntry(StageLeft + 300, legendY, BlockBoxOutline, "防御框");

            DrawTextF("WASD:移动  J:轻拳  K:重拳  U:轻脚  I:重脚  Tab:设置  ESC:退出",
                StageLeft + 420, legendY, 12, new Color(120, 120, 120, 255));
        }

        private static int FindCharacterByTeam(GameWorld w, int team)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;
                if (w.Identity[e].Team == team) return e;
            }
            return -1;
        }

        private void DrawHPBar(int x, int y, int wPx, int h, float hp, float maxHP, bool reverse)
        {
            Raylib.DrawRectangle(x, y, wPx, h, HPBarBg);
            float ratio = Math.Clamp(hp / maxHP, 0f, 1f);
            int fillW = (int)(wPx * ratio);
            Color barColor = ratio > 0.3f ? HPBarOk : HPBarLow;

            if (reverse)
                Raylib.DrawRectangle(x + wPx - fillW, y, fillW, h, barColor);
            else
                Raylib.DrawRectangle(x, y, fillW, h, barColor);

            Raylib.DrawRectangleLines(x, y, wPx, h, new Color(160, 160, 160, 200));

            string hpText = $"{hp:F0}/{maxHP:F0}";
            int textW = MeasureTextF(hpText, 14);
            DrawTextF(hpText, x + (wPx - textW) / 2, y + 3, 14, Color.White);
        }

        private void DrawPowerBar(int x, int y, int wPx, int h, float power, float maxPower)
        {
            Raylib.DrawRectangle(x, y, wPx, h, HPBarBg);
            float ratio = Math.Clamp(power / maxPower, 0f, 1f);
            Raylib.DrawRectangle(x, y, (int)(wPx * ratio), h, PowerBar);
            Raylib.DrawRectangleLines(x, y, wPx, h, new Color(100, 100, 100, 180));
        }

        private void DrawLegendEntry(int x, int y, Color color, string label)
        {
            Raylib.DrawRectangle(x, y + 2, 12, 12, color);
            DrawTextF(label, x + 16, y, 14, color);
        }

        // ── Debug Info ───────────────────────────────────────────

        private void DrawDebugInfo(GameScene scene)
        {
            var w = scene.World;
            int y = StageBottom + 28;
            Raylib.DrawLine(StageLeft, y - 4, StageRight, y - 4, new Color(60, 60, 60, 255));

            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!w.IsAliveSlot(e) || w.Kinds[e] != EntityKind.Character) continue;

                ref var tr = ref w.Transform[e];
                ref var phys = ref w.Physics[e];
                ref var fl = ref w.FrameLine[e];
                var inst = w.Skill[e].ActiveSkill;
                string skillName = inst?.Def?.Name ?? "none";
                int skillFrame = inst?.Frame ?? 0;
                string tag = w.Identity[e].Team == 0 ? "P1" : "P2";

                string info = $"[{tag}] 位置=({tr.Position.X:F2},{tr.Position.Y:F2}) "
                    + $"速度=({phys.Velocity.X:F2},{phys.Velocity.Y:F2}) "
                    + $"朝向={tr.Facing} 着地={phys.IsGrounded} "
                    + $"技能={skillName}(帧{skillFrame}) 硬直={w.Status[e].HitstunFrames} "
                    + $"本{fl.LocalFrame} 停{fl.PauseFrames}f";

                DrawTextF(info, StageLeft, y, 12, DebugText);
                y += 16;
            }

            string counts = $"效果: {w.EffectCount}  弹幕: {w.ProjectileCount}";
            DrawTextF(counts, StageLeft, y, 12, DebugText);
        }

        // ── Control Panel ────────────────────────────────────────

        private static readonly string[] TabNames = { "游戏设置" };

        private void DrawControlPanel()
        {
            Raylib.DrawRectangle(0, 0, ScreenWidth, ScreenHeight, PanelOverlay);
            Raylib.DrawRectangle(PanelLeft, PanelTop, PanelWidth, PanelHeight, PanelBg);
            Raylib.DrawRectangleLines(PanelLeft, PanelTop, PanelWidth, PanelHeight, PanelBorder);

            DrawTextF("控制界面", PanelLeft + PanelWidth / 2 - 40, PanelTop + 12, 22, Color.White);

            string closeHint = "Tab / ESC 关闭";
            int closeW = MeasureTextF(closeHint, 14);
            DrawTextF(closeHint, PanelLeft + PanelWidth - closeW - 16, PanelTop + 16, 14,
                new Color(140, 140, 150, 255));

            Raylib.DrawLine(PanelLeft + 8, PanelTop + 44,
                PanelLeft + PanelWidth - 8, PanelTop + 44, PanelBorder);

            DrawTabList();

            int sepX = PanelLeft + TabListWidth;
            Raylib.DrawLine(sepX, PanelTop + 44, sepX, PanelTop + PanelHeight - 8, PanelBorder);

            DrawTabContent();

            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
                _settings.PanelOpen = false;
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

            DrawToggle(x, y, "AI 开关", "控制 AI 角色是否自动行动",
                _settings.AIEnabled, v => _settings.AIEnabled = v);
            y += spacing;

            DrawToggle(x, y, "自动满血复活", "血量归零时自动满血复活",
                _settings.AutoRevive, v => _settings.AutoRevive = v);
            y += spacing;

            DrawButton(x, y, ButtonWidth, ButtonHeight, "重新开始", "重置场景到初始状态",
                () => _settings.RestartRequested = true);
        }

        private void DrawToggle(int x, int y, string label, string desc, bool value, Action<bool> onChanged)
        {
            int mx = Raylib.GetMouseX();
            int my = Raylib.GetMouseY();
            bool clicked = Raylib.IsMouseButtonPressed(MouseButton.Left);

            DrawTextF(label, x, y, 18, Color.White);
            DrawTextF(desc, x, y + 22, 12, new Color(140, 140, 150, 255));

            int toggleW = 48;
            int toggleH = 24;
            int toggleX = x + ButtonWidth + 80;
            int toggleY = y + 4;

            bool hover = mx >= toggleX && mx < toggleX + toggleW && my >= toggleY && my < toggleY + toggleH;

            Color bg = value ? ToggleOn : ToggleOff;
            if (hover) bg = value ? new Color(60, 200, 100, 255) : new Color(140, 140, 150, 255);

            Raylib.DrawRectangleRounded(
                new Rectangle(toggleX, toggleY, toggleW, toggleH), 0.5f, 8, bg);

            int knobR = toggleH / 2 - 3;
            int knobX = value ? toggleX + toggleW - knobR - 5 : toggleX + knobR + 5;
            int knobY = toggleY + toggleH / 2;
            Raylib.DrawCircle(knobX, knobY, knobR, Color.White);

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

            if (desc != null)
                DrawTextF(desc, x + w + 16, y + (h - 12) / 2, 12, new Color(140, 140, 150, 255));

            if (hover && clicked) onClick();
        }

        // ── Coordinate Mapping ───────────────────────────────────

        private int WorldToScreenX(float worldX)
        {
            float range = GameConstants.StageRightBound - GameConstants.StageLeftBound;
            float t = (worldX - GameConstants.StageLeftBound) / range;
            return StageLeft + (int)(t * StageWidth);
        }

        private int WorldToScreenY(float worldY)
        {
            int groundScreen = StageBottom - GroundMargin;
            float pixelsPerUnit = (float)(StageHeight - GroundMargin) / VisibleWorldUnits;
            return groundScreen - (int)(worldY * pixelsPerUnit);
        }

        // ── Font Loading ─────────────────────────────────────────

        private void LoadCjkFont()
        {
            string uiChars = "KOF98练习模式帧回合击倒受击防御攻击蹲下跳跃移动推箱框" +
                             "位置速度朝向着地技能硬直效果弹幕轻拳重脚退出" +
                             "控制界面关闭游戏设置开关角色管理调试选项" +
                             "自动满血复活重新开始场景到初始状态" +
                             "是否行动量归零时线本停顿时";
            int asciiStart = 32, asciiEnd = 126;
            int asciiCount = asciiEnd - asciiStart + 1;
            int count = asciiCount + uiChars.Length;
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
            _font = Raylib.GetFontDefault();
            _fontLoaded = false;
        }

        // ── Text Helpers ─────────────────────────────────────────

        private void DrawTextF(string text, int x, int y, int fontSize, Color color)
        {
            if (_fontLoaded)
                Raylib.DrawTextEx(_font, text, new Vector2(x, y), fontSize, 1, color);
            else
                Raylib.DrawText(text, x, y, fontSize, color);
        }

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
