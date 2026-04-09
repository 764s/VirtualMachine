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
            // Resolve scripts directory first so we can pass it to VMBridge for include support.
            string scriptsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Scripts");
            // Fallback: try relative to working directory
            if (!System.IO.Directory.Exists(scriptsDir))
                scriptsDir = System.IO.Path.Combine("KOF98", "Scripts");
            // Final fallback: try absolute path relative to project
            if (!System.IO.Directory.Exists(scriptsDir))
                scriptsDir = "Scripts";

            string resolvedScriptsDir = System.IO.Directory.Exists(scriptsDir)
                ? System.IO.Path.GetFullPath(scriptsDir) : null;
            var vmBridge = new GameVMBridge(scene, resolvedScriptsDir);
            scene.VMBridge = vmBridge;

            // ── Load FFS skill scripts ───────────────────────────

            var vmSlots = LoadSkillScripts(vmBridge, scriptsDir);

            // ── Define characters ────────────────────────────────
            var p1Data = CreateDefaultCharacterData(1, "Kyo", vmSlots);
            var p2Data = CreateDefaultCharacterData(2, "Iori", vmSlots);

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

        /// <summary>
        /// VM module slot indices for loaded skill scripts.
        /// -1 = script not loaded (fall back to host-driven behavior).
        /// </summary>
        private struct VMSlots
        {
            public int Idle;
            public int Walk;
            public int Jump;
            public int LightPunch;
        }

        /// <summary>Load FFS skill scripts and return their module slot indices.</summary>
        private static VMSlots LoadSkillScripts(GameVMBridge bridge, string scriptsDir)
        {
            var slots = new VMSlots { Idle = -1, Walk = -1, Jump = -1, LightPunch = -1 };

            string idlePath = System.IO.Path.Combine(scriptsDir, "skill_idle.ffs");
            string walkPath = System.IO.Path.Combine(scriptsDir, "skill_walk_forward.ffs");
            string jumpPath = System.IO.Path.Combine(scriptsDir, "skill_jump.ffs");
            string lpPath = System.IO.Path.Combine(scriptsDir, "skill_light_punch.ffs");

            if (System.IO.File.Exists(idlePath))
                slots.Idle = bridge.LoadScript(idlePath);
            if (System.IO.File.Exists(walkPath))
                slots.Walk = bridge.LoadScript(walkPath);
            if (System.IO.File.Exists(jumpPath))
                slots.Jump = bridge.LoadScript(jumpPath);
            if (System.IO.File.Exists(lpPath))
                slots.LightPunch = bridge.LoadScript(lpPath);

            int loaded = (slots.Idle >= 0 ? 1 : 0) + (slots.Walk >= 0 ? 1 : 0)
                + (slots.Jump >= 0 ? 1 : 0) + (slots.LightPunch >= 0 ? 1 : 0);
            Console.WriteLine($"[KOF98] Loaded {loaded}/4 skill scripts from {scriptsDir}");

            return slots;
        }

        private static CharacterData CreateDefaultCharacterData(int id, string name, VMSlots vmSlots)
        {
            // Stance arrays (shared references, no per-skill allocation)
            var groundOnly = new[] { Stance.Grounded };
            var groundAndCrouch = new[] { Stance.Grounded, Stance.Crouching };

            // ── Idle skill (VM-driven, looping) ──────────────────
            var idleSkill = new SkillDef(
                id: 0, name: "Idle", totalFrames: -1,
                priority: GameConstants.PRIORITY_IDLE,
                tags: (1 << GameConstants.TAG_IDLE),
                looping: true);
            idleSkill.AllowedStances = groundOnly;
            idleSkill.ActivationPriority = 900;   // Lowest — fallback
            idleSkill.InterruptPriority = 900;
            idleSkill.VMModuleSlot = vmSlots.Idle;

            // ── Walk skill (VM-driven, looping, deactivates when direction released) ──
            var walkSkill = new SkillDef(
                id: 1, name: "Walk", totalFrames: -1,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_WALK),
                looping: true);
            walkSkill.AllowedStances = groundOnly;
            walkSkill.ActivationPriority = 500;   // Movement tier
            walkSkill.InterruptPriority = 500;
            walkSkill.VMModuleSlot = vmSlots.Walk;
            walkSkill.CanActivate = (ch, input) =>
                input.HasAny(InputButton.Left | InputButton.Right)
                && !input.IsHeld(InputButton.Up)  // Don't walk when jumping
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            walkSkill.CanContinue = (ch, input) =>
                input.HasAny(InputButton.Left | InputButton.Right)
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            // OnFrame replaced by VM script (skill_walk_forward.ffs)

            // ── Jump skill (VM-driven, finite, ends on landing) ────
            var jumpSkill = new SkillDef(
                id: 2, name: "Jump", totalFrames: JumpTimeoutFrames,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_JUMP) | (1 << GameConstants.TAG_AIR_STATE));
            jumpSkill.AllowedStances = groundAndCrouch;
            jumpSkill.ActivationPriority = 400;   // Jump > walk
            jumpSkill.InterruptPriority = 500;
            jumpSkill.VMModuleSlot = vmSlots.Jump;
            jumpSkill.CanActivate = (ch, input) =>
                input.IsPressed(InputButton.Up)
                && ch.IsGrounded
                && ch.HitstunFrames <= 0;
            // CanContinue not needed — script returns when character lands
            // OnFrame replaced by VM script (skill_jump.ffs)

            // ── Crouch skill (looping, deactivates when Down released) ──
            var crouchSkill = new SkillDef(
                id: 3, name: "Crouch", totalFrames: -1,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_CROUCH));
            crouchSkill.IsLooping = true;
            crouchSkill.AllowedStances = groundOnly;
            crouchSkill.ActivationPriority = 450;  // Crouch between walk and jump
            crouchSkill.InterruptPriority = 500;
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

            // ── Light Punch (VM-driven, 20 frames) ────────────────
            var lpSkill = new SkillDef(
                id: 10, name: "LightPunch", totalFrames: 20,
                priority: GameConstants.PRIORITY_ATTACK,
                tags: (1 << GameConstants.TAG_ATTACK));
            lpSkill.AllowedStances = groundOnly;
            lpSkill.ActivationPriority = 200;     // Attack tier
            lpSkill.InterruptPriority = 200;
            lpSkill.VMModuleSlot = vmSlots.LightPunch;
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
