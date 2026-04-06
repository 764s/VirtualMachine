using System;
using System.Threading;

namespace KOF98
{
    /// <summary>
    /// KOF98 Practice — Entry point.
    /// Runs a simple KOF98 fight simulation with console-based rendering.
    ///
    /// Usage:
    ///   dotnet run                     — Run with console view
    ///   dotnet run -- --headless       — Run without rendering (simulation only)
    ///   dotnet run -- --frames 300     — Run for N frames then exit
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            bool headless = false;
            bool useRaylib = false;
            int maxFrames = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--headless") headless = true;
                if (args[i] == "--raylib") useRaylib = true;
                if (args[i] == "--frames" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out maxFrames);
            }

            if (!useRaylib)
            {
                Console.WriteLine("╔══════════════════════════════════════════╗");
                Console.WriteLine("║     KOF98 Practice — FFVM Exploration   ║");
                Console.WriteLine("╠══════════════════════════════════════════╣");
                Console.WriteLine("║  Ctrl+C to stop                         ║");
                Console.WriteLine("║  Use --raylib for graphical display     ║");
                Console.WriteLine("╚══════════════════════════════════════════╝");
                Console.WriteLine();
            }

            // ── Create scene ─────────────────────────────────────
            var scene = new GameScene();
            var settings = new GameSettings();

            // ── Create VM bridge (optional — comment out to run host-only) ──
            var vmBridge = new GameVMBridge(scene);
            scene.VMBridge = vmBridge;

            // ── Define characters ────────────────────────────────
            var p1Data = CreateDefaultCharacterData(1, "Kyo");
            var p2Data = CreateDefaultCharacterData(2, "Iori");

            // ── Initial scene setup via commands ─────────────────
            var initInput = new SceneInput();
            initInput.AddCommand(new CreateCharacterCommand(0, p1Data, new FVec2(-3f, 0f)));
            initInput.AddCommand(new CreateCharacterCommand(1, p2Data, new FVec2(3f, 0f)));
            initInput.AddCommand(new SetAICommand(1, new SimpleAI(42)));
            scene.Step(initInput);

            // ── Create view ──────────────────────────────────────
            IGameView view;
            if (headless)
                view = null;
            else if (useRaylib)
                view = new RaylibGameView(settings);
            else
                view = new ConsoleGameView();
            view?.Initialize(scene);

            // ── Game loop ────────────────────────────────────────
            bool running = true;
            Console.CancelKeyPress += (_, e) => { running = false; e.Cancel = true; };

            var frameInput = new SceneInput();
            InputButton prevP1 = InputButton.None;

            while (running)
            {
                // Check Raylib window close
                if (useRaylib && RaylibGameView.ShouldClose())
                    break;

                if (maxFrames >= 0 && scene.FrameNumber >= maxFrames)
                    break;

                // ── Handle restart request ───────────────────────
                if (settings.RestartRequested)
                {
                    settings.RestartRequested = false;
                    settings.PanelOpen = false;
                    scene.ResetRound(new FVec2(-3f, 0f), new FVec2(3f, 0f));
                }

                // ── Auto-revive: revive dead characters ──────────
                if (settings.AutoRevive && scene.IsRoundOver)
                {
                    for (int i = 0; i < scene.Characters.Count; i++)
                    {
                        var ch = scene.Characters.Characters[i];
                        if (ch != null && !ch.IsAlive)
                        {
                            ch.HP = ch.Data.MaxHP;
                            ch.IsAlive = true;
                            ch.HitstunFrames = 0;
                            ch.BlockstunFrames = 0;
                            ch.IsKnockedDown = false;
                            ch.ClearAllTags();
                            ch.ClearHitBoxes();
                            ch.SkillMgr.ClearAll();
                        }
                    }
                    scene.ResetRound(new FVec2(-3f, 0f), new FVec2(3f, 0f));
                }

                if (scene.IsRoundOver && !settings.AutoRevive)
                {
                    view?.Render(scene);
                    if (!useRaylib)
                    {
                        Console.WriteLine($"\n  Round Over! Winner: {(scene.WinnerId >= 0 ? scene.Characters.Get(scene.WinnerId)?.Data?.Name ?? "?" : "Draw")}");
                    }
                    Thread.Sleep(2000);
                    scene.ResetRound(new FVec2(-3f, 0f), new FVec2(3f, 0f));
                }

                // ── Pause when control panel is open ─────────────
                scene.IsPaused = settings.PanelOpen;

                // ── Collect P1 input ─────────────────────────────
                PlayerInput p1Input;
                if (settings.PanelOpen)
                {
                    // Don't collect game input while panel is open
                    p1Input = PlayerInput.Empty;
                }
                else if (useRaylib)
                {
                    // Raylib: proper simultaneous key detection
                    p1Input = RaylibGameView.CollectInput(prevP1);
                    prevP1 = p1Input.Held;
                }
                else if (!headless)
                {
                    // Console: read all available key events (limited)
                    InputButton p1Held = InputButton.None;
                    while (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        p1Held |= MapKeyToInput(key.Key);
                    }
                    p1Input = PlayerInput.ComputeEdge(prevP1, p1Held);
                    prevP1 = p1Held;
                }
                else
                {
                    p1Input = PlayerInput.Empty;
                }

                // ── AI toggle: supply empty input when AI disabled ──
                frameInput.Clear();
                frameInput.SetInput(0, p1Input);

                if (!settings.AIEnabled)
                {
                    // Override AI characters with empty input to disable AI
                    for (int i = 0; i < scene.Characters.Count; i++)
                    {
                        var ch = scene.Characters.Characters[i];
                        if (ch != null && ch.Team != 0 && !frameInput.CharacterInputs.ContainsKey(ch.Id))
                        {
                            frameInput.SetInput(ch.Id, PlayerInput.Empty);
                        }
                    }
                }

                // ── Step simulation ──────────────────────────────
                scene.Step(frameInput);

                // ── Render ───────────────────────────────────────
                view?.Render(scene);

                // ── Frame pacing (Raylib handles its own, console needs sleep) ──
                if (!useRaylib && !headless)
                    Thread.Sleep(16); // ~60fps
            }

            view?.Shutdown();
            Console.WriteLine("\nKOF98 Practice ended.");
        }

        private const int JumpTimeoutFrames = 120;

        private static CharacterData CreateDefaultCharacterData(int id, string name)
        {
            // ── Idle skill (host-driven, looping) ─────────────────
            var idleSkill = new SkillDef(
                id: 0, name: "Idle", totalFrames: -1,
                priority: GameConstants.PRIORITY_IDLE,
                tags: (1 << GameConstants.TAG_IDLE),
                looping: true);

            // ── Walk skill (looping, deactivates when direction released) ──
            var walkSkill = new SkillDef(
                id: 1, name: "Walk", totalFrames: -1,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_WALK),
                looping: true);
            walkSkill.CanActivate = (ch, input) =>
                input.HasAny(InputButton.Left | InputButton.Right)
                && !input.IsHeld(InputButton.Up)  // Don't walk when jumping
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            walkSkill.CanContinue = (ch, input) =>
                input.HasAny(InputButton.Left | InputButton.Right)
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            walkSkill.OnFrame = (ch, input) =>
            {
                // Determine raw world direction from input
                bool right = input.IsHeld(InputButton.Right);
                bool left = input.IsHeld(InputButton.Left);
                if (right == left)
                {
                    ch.Body.Velocity = new FVec2(0, ch.Body.Velocity.Y);
                    return;
                }

                int rawDir = right ? 1 : -1;
                int fwd = input.GetForwardDir(ch.Facing);
                float speed = fwd > 0 ? ch.Data.WalkSpeed : ch.Data.BackWalkSpeed;
                ch.Body.Velocity = new FVec2(speed * rawDir, ch.Body.Velocity.Y);
            };

            // ── Jump skill (finite, ends on landing) ──────────────
            var jumpSkill = new SkillDef(
                id: 2, name: "Jump", totalFrames: JumpTimeoutFrames,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_JUMP) | (1 << GameConstants.TAG_AIR_STATE));
            jumpSkill.CanActivate = (ch, input) =>
                input.IsPressed(InputButton.Up)
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            jumpSkill.CanContinue = (ch, input) =>
            {
                // Stay active until we land (after the initial launch frames)
                if (ch.SkillMgr.ActiveSkill == null) return false;
                return ch.SkillMgr.ActiveSkill.Frame < 4 || !ch.IsGrounded;
            };
            jumpSkill.OnFrame = (ch, input) =>
            {
                if (ch.SkillMgr.ActiveSkill == null) return;
                if (ch.SkillMgr.ActiveSkill.Frame == 0)
                {
                    // Launch: set Y velocity, optionally X for directional jump
                    float vx = 0f;
                    if (input.IsHeld(InputButton.Right)) vx = ch.Data.WalkSpeed;
                    else if (input.IsHeld(InputButton.Left)) vx = -ch.Data.WalkSpeed;
                    ch.Body.Velocity = new FVec2(vx, ch.Data.JumpSpeedY);
                    ch.Body.IsGrounded = false;
                }
            };

            // ── Crouch skill (looping, deactivates when Down released) ──
            var crouchSkill = new SkillDef(
                id: 3, name: "Crouch", totalFrames: -1,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_CROUCH));
            crouchSkill.IsLooping = true;
            crouchSkill.CanActivate = (ch, input) =>
                input.IsHeld(InputButton.Down)
                && !input.IsHeld(InputButton.Up)
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            crouchSkill.CanContinue = (ch, input) =>
                input.IsHeld(InputButton.Down)
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            crouchSkill.OnFrame = (ch, input) =>
            {
                // Zero horizontal velocity while crouching
                ch.Body.Velocity = new FVec2(0, ch.Body.Velocity.Y);
                // Use crouch pushbox
                ch.PushBox = ch.Data.CrouchPushBox;
            };

            // ── Light Punch (host-driven, 20 frames) ──────────────
            var lpSkill = new SkillDef(
                id: 10, name: "LightPunch", totalFrames: 20,
                priority: GameConstants.PRIORITY_ATTACK,
                tags: (1 << GameConstants.TAG_ATTACK));
            lpSkill.CanActivate = (ch, input) =>
                input.IsPressed(InputButton.LP) && ch.IsGrounded && ch.HitstunFrames <= 0;
            lpSkill.CollisionFrames = new[]
            {
                new CollisionBoxFrame(5, 10, CollisionBoxType.Hitbox, 1001,
                    new FRect(0.3f, 0.7f, 0.3f, 0.15f)),
            };

            return new CharacterData
            {
                Id = id,
                Name = name,
                Skills = new[] { idleSkill, walkSkill, jumpSkill, crouchSkill, lpSkill },
                IdleSkillIndex = 0,
            };
        }

        private static InputButton MapKeyToInput(ConsoleKey key)
        {
            return key switch
            {
                ConsoleKey.W or ConsoleKey.UpArrow => InputButton.Up,
                ConsoleKey.S or ConsoleKey.DownArrow => InputButton.Down,
                ConsoleKey.A or ConsoleKey.LeftArrow => InputButton.Left,
                ConsoleKey.D or ConsoleKey.RightArrow => InputButton.Right,
                ConsoleKey.J => InputButton.LP,
                ConsoleKey.K => InputButton.HP,
                ConsoleKey.U => InputButton.LK,
                ConsoleKey.I => InputButton.HK,
                _ => InputButton.None,
            };
        }
    }
}
