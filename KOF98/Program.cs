using System;
using System.Threading;
using FFVM.Debug;

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
            bool debugMode = false;
            int maxFrames = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--headless") headless = true;
                if (args[i] == "--raylib") useRaylib = true;
                if (args[i] == "--debug") debugMode = true;
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

            // ── Debug setup (must be before any VM execution) ────
            EmbeddableDapServer dapServer = null;
            if (debugMode)
            {
                dapServer = new EmbeddableDapServer(4711);
                dapServer.StartListening();
                Console.WriteLine("[DEBUG] DAP server listening on port 4711");
                Console.WriteLine("[DEBUG] In VS Code: F5 → \"KOF98: C# + FFVM Debug\" to connect.");

                // Register all loaded module script paths for multi-module tracking
                RegisterDebugScriptPaths(dapServer, vmSlots, scriptsDir);

                // Attach debugger to VMWorld — use the idle program for initial breakpoint verification
                // Idle is always the first script to execute (fallback skill activated on character creation)
                if (vmSlots.Idle >= 0)
                {
                    var idleProgram = vmBridge.World.Modules.Get(vmSlots.Idle);
                    string idleScriptPath = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(scriptsDir, "skill_idle.ffs"));
                    dapServer.AttachToWorld(vmBridge.World, idleProgram, -1, idleScriptPath);
                }
                else
                {
                    // No idle script loaded — attach to world without a specific program
                    // Breakpoints will be buffered and applied when a module is loaded
                    dapServer.AttachToWorld(vmBridge.World, null, -1, null);
                }

                // Wait for VS Code to connect and complete configuration
                dapServer.WaitForConnection();
                dapServer.StopOnEntry();
            }

            // ── Define characters ────────────────────────────────
            var p1Data = CreateDefaultCharacterData(1, "Kyo", vmBridge, vmSlots);
            var p2Data = CreateDefaultCharacterData(2, "Iori", vmBridge, vmSlots);

            // ── Initial scene setup via commands ─────────────────
            var initInput = new SceneInput();
            initInput.AddCommand(new CreateCharacterCommand(0, p1Data, new FVec2(-3f, 0f)));
            initInput.AddCommand(new CreateCharacterCommand(1, p2Data, new FVec2(3f, 0f)));
            initInput.AddCommand(new SetAICommand(1, new SimpleAI(42)));
            scene.Step(initInput);

            // Check if a breakpoint was hit during initial scene setup
            dapServer?.CheckBreakpointAndWait();

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

                // ── Check for debugger breakpoint (blocks if hit) ──
                dapServer?.CheckBreakpointAndWait();

                // ── Render ───────────────────────────────────────
                view?.Render(scene);

                // ── Frame pacing (Raylib handles its own, console needs sleep) ──
                if (!useRaylib && !headless)
                    Thread.Sleep(16); // ~60fps
            }

            view?.Shutdown();
            dapServer?.Dispose();
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
            public int CrouchPunch;
            public int HitHigh;
            public int HardKnockdown;
            public int StandUp;
        }

        /// <summary>Load FFS skill scripts and return their module slot indices.</summary>
        private static VMSlots LoadSkillScripts(GameVMBridge bridge, string scriptsDir)
        {
            var slots = new VMSlots
            {
                Idle = -1, Walk = -1, Jump = -1, LightPunch = -1,
                CrouchPunch = -1, HitHigh = -1, HardKnockdown = -1, StandUp = -1
            };

            var scriptFiles = new (string field, string file)[]
            {
                ("Idle", "skill_idle.ffs"),
                ("Walk", "skill_walk_forward.ffs"),
                ("Jump", "skill_jump.ffs"),
                ("LightPunch", "skill_light_punch.ffs"),
                ("CrouchPunch", "skill_crouch_punch.ffs"),
                ("HitHigh", "skill_hit_high.ffs"),
                ("HardKnockdown", "skill_hard_knockdown.ffs"),
                ("StandUp", "skill_stand_up.ffs"),
            };

            int loaded = 0;
            foreach (var (field, file) in scriptFiles)
            {
                string path = System.IO.Path.Combine(scriptsDir, file);
                if (!System.IO.File.Exists(path)) continue;
                int slot = bridge.LoadScript(path);
                if (slot < 0) continue;

                switch (field)
                {
                    case "Idle": slots.Idle = slot; break;
                    case "Walk": slots.Walk = slot; break;
                    case "Jump": slots.Jump = slot; break;
                    case "LightPunch": slots.LightPunch = slot; break;
                    case "CrouchPunch": slots.CrouchPunch = slot; break;
                    case "HitHigh": slots.HitHigh = slot; break;
                    case "HardKnockdown": slots.HardKnockdown = slot; break;
                    case "StandUp": slots.StandUp = slot; break;
                }
                loaded++;
            }
            Console.WriteLine($"[KOF98] Loaded {loaded}/8 skill scripts from {scriptsDir}");

            return slots;
        }

        /// <summary>Register module slot → script path mappings for the DAP debugger.</summary>
        private static void RegisterDebugScriptPaths(EmbeddableDapServer dapServer, VMSlots slots, string scriptsDir)
        {
            var mappings = new (int slot, string file)[]
            {
                (slots.Idle, "skill_idle.ffs"),
                (slots.Walk, "skill_walk_forward.ffs"),
                (slots.Jump, "skill_jump.ffs"),
                (slots.LightPunch, "skill_light_punch.ffs"),
                (slots.CrouchPunch, "skill_crouch_punch.ffs"),
                (slots.HitHigh, "skill_hit_high.ffs"),
                (slots.HardKnockdown, "skill_hard_knockdown.ffs"),
                (slots.StandUp, "skill_stand_up.ffs"),
            };

            foreach (var (slot, file) in mappings)
            {
                if (slot >= 0)
                    dapServer.RegisterModuleScriptPath(slot,
                        System.IO.Path.GetFullPath(System.IO.Path.Combine(scriptsDir, file)));
            }
        }

        private static CharacterData CreateDefaultCharacterData(int id, string name, GameVMBridge vmBridge, VMSlots vmSlots)
        {
            var skills = new System.Collections.Generic.List<SkillDef>();
            int idleIndex = -1;

            // ── VM-driven skills — config extracted from ffs scripts ──

            if (vmSlots.Idle >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.Idle, 0, "Idle");
                if (def != null) { idleIndex = skills.Count; skills.Add(def); }
            }

            if (vmSlots.Walk >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.Walk, 1, "Walk");
                if (def != null) skills.Add(def);
            }

            if (vmSlots.Jump >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.Jump, 2, "Jump");
                if (def != null) skills.Add(def);
            }

            // ── Crouch skill (host-driven — no ffs yet, needs SetPushBox syscall) ──
            var crouchSkill = new SkillDef(
                id: 3, name: "Crouch", totalFrames: -1,
                priority: GameConstants.PRIORITY_MOVEMENT,
                tags: (1 << GameConstants.TAG_CROUCH));
            crouchSkill.IsLooping = true;
            crouchSkill.AllowedStances = new[] { Stance.Grounded };
            crouchSkill.ActivationPriority = 450;
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
                ch.Body.Velocity = new FVec2(0, ch.Body.Velocity.Y);
                ch.PushBox = ch.Data.CrouchPushBox;
            };
            skills.Add(crouchSkill);

            if (vmSlots.LightPunch >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.LightPunch, 10, "LightPunch");
                if (def != null)
                {
                    // Collision frames still defined host-side (KOF-T4 will move to ffs)
                    def.CollisionFrames = new[]
                    {
                        new CollisionBoxFrame(5, 10, CollisionBoxType.Hitbox, 1001,
                            new FRect(0.3f, 0.7f, 0.3f, 0.15f)),
                    };
                    skills.Add(def);
                }
            }

            if (vmSlots.CrouchPunch >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.CrouchPunch, 11, "CrouchPunch");
                if (def != null) skills.Add(def);
            }

            if (vmSlots.HitHigh >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.HitHigh, 20, "HitHigh");
                if (def != null) skills.Add(def);
            }

            if (vmSlots.HardKnockdown >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.HardKnockdown, 21, "HardKnockdown");
                if (def != null) skills.Add(def);
            }

            if (vmSlots.StandUp >= 0)
            {
                var def = vmBridge.ExtractSkillDef(vmSlots.StandUp, 22, "StandUp");
                if (def != null) skills.Add(def);
            }

            return new CharacterData
            {
                Id = id,
                Name = name,
                Skills = skills.ToArray(),
                IdleSkillIndex = idleIndex,
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
