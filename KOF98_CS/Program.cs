using System;
using System.Threading;
using KOF98.Game;
using KOF98.CsSim;

namespace KOF98_CS
{
    /// <summary>
    /// KOF98_CS — entry point for the CS-simulation flavour of the KOF98 practice app.
    ///
    /// Wires together:
    ///   - KOF98.Game     (game-layer framework, no FFVM dependency)
    ///   - KOF98.CsSim    (pure C# implementations of ISkillBehavior)
    ///   - Raylib-cs view (re-uses RaylibGameView from KOF98.Game)
    ///
    /// This mirrors the existing KOF98 app's behavior, but with skills driven
    /// by C# objects instead of FFVM instances. The two apps run in parallel:
    /// KOF98 will later evolve into Game + VM-layer + view, while KOF98_CS
    /// stays as the CS baseline used for parity comparison and benchmarking.
    ///
    /// Usage:
    ///   dotnet run                    Console view (Raylib if --raylib)
    ///   dotnet run -- --headless      No view, simulation only
    ///   dotnet run -- --raylib        Graphical Raylib view
    ///   dotnet run -- --frames 300    Run N frames then exit
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

            if (!useRaylib && !headless)
            {
                Console.WriteLine("╔══════════════════════════════════════════╗");
                Console.WriteLine("║   KOF98 Practice — CS Simulation Layer  ║");
                Console.WriteLine("╠══════════════════════════════════════════╣");
                Console.WriteLine("║  Skills: Idle / WalkForward / WalkBack  ║");
                Console.WriteLine("║  Ctrl+C to stop                         ║");
                Console.WriteLine("║  Use --raylib for graphical display     ║");
                Console.WriteLine("╚══════════════════════════════════════════╝");
                Console.WriteLine();
            }

            // ── Create scene ─────────────────────────────────────
            var scene = new GameScene();
            var settings = new GameSettings();

            // ── Define characters with CS-sim skills ─────────────
            var p1Data = CsSimSkillCatalog.BuildDefaultCharacterData(1, "Kyo");
            var p2Data = CsSimSkillCatalog.BuildDefaultCharacterData(2, "Iori");

            var initInput = new SceneInput();
            initInput.AddCommand(new CreateCharacterCommand(0, p1Data, new FVec2(-3f, 0f)));
            initInput.AddCommand(new CreateCharacterCommand(1, p2Data, new FVec2(3f, 0f)));
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
            InputButton prevP2 = InputButton.None;

            while (running)
            {
                if (useRaylib && RaylibGameView.ShouldClose())
                    break;

                if (maxFrames >= 0 && scene.FrameNumber >= maxFrames)
                    break;

                if (settings.RestartRequested)
                {
                    settings.RestartRequested = false;
                    settings.PanelOpen = false;
                    scene.ResetRound(new FVec2(-3f, 0f), new FVec2(3f, 0f));
                }

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
                    if (!useRaylib && !headless)
                    {
                        Console.WriteLine($"\n  Round Over! Winner: {(scene.WinnerId >= 0 ? scene.Characters.Get(scene.WinnerId)?.Data?.Name ?? "?" : "Draw")}");
                    }
                    Thread.Sleep(2000);
                    scene.ResetRound(new FVec2(-3f, 0f), new FVec2(3f, 0f));
                }

                scene.IsPaused = settings.PanelOpen;

                // ── Collect P1 input ─────────────────────────────
                PlayerInput p1Input;
                if (settings.PanelOpen)
                {
                    p1Input = PlayerInput.Empty;
                }
                else if (useRaylib)
                {
                    p1Input = RaylibGameView.CollectInput(prevP1);
                    prevP1 = p1Input.Held;
                }
                else if (!headless)
                {
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

                frameInput.Clear();
                frameInput.SetInput(0, p1Input);
                // P2 is idle by default in the CS-sim baseline (no AI in this minimal app).
                frameInput.SetInput(1, PlayerInput.ComputeEdge(prevP2, InputButton.None));

                scene.Step(frameInput);

                view?.Render(scene);

                if (!useRaylib && !headless)
                    Thread.Sleep(16); // ~60fps
            }

            view?.Shutdown();
            Console.WriteLine("\nKOF98_CS ended.");
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
