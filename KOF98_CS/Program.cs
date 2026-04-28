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
    ///   - KOF98.Game     (game-layer ECS framework, no FFVM dependency)
    ///   - KOF98.CsSim    (pure C# implementations of ISkillBehavior)
    ///
    /// Usage:
    ///   dotnet run                        Console view, interactive
    ///   dotnet run -- --headless          No view, simulation only
    ///   dotnet run -- --frames 300        Run N frames then exit
    ///   dotnet run -- --raylib            Raylib graphical view (ECS-aware)
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

            if (headless) useRaylib = false;

            if (!headless)
            {
                Console.WriteLine("KOF98_CS — ECS edition");
                Console.WriteLine("Skills: Idle / WalkForward / WalkBack");
                Console.WriteLine("Ctrl+C to stop");
                Console.WriteLine();
            }

            var scene = new GameScene();
            var settings = new GameSettings();

            var p1Data = CsSimSkillCatalog.BuildDefaultCharacterData(1, "Kyo");
            var p2Data = CsSimSkillCatalog.BuildDefaultCharacterData(2, "Iori");

            var initInput = new SceneInput();
            initInput.Commands.Add(new CreateCharacterCommand(0, p1Data, new FVec2(-3f, 0f)));
            initInput.Commands.Add(new CreateCharacterCommand(1, p2Data, new FVec2(3f, 0f)));
            scene.Step(initInput);

            IGameView view = headless
                ? null
                : (useRaylib ? (IGameView)new RaylibGameView(settings) : new ConsoleGameView());
            view?.Initialize(scene);

            bool running = true;
            Console.CancelKeyPress += (_, e) => { running = false; e.Cancel = true; };

            var frameInput = new SceneInput();
            InputButton prevP1 = InputButton.None;
            InputButton prevP2 = InputButton.None;

            while (running)
            {
                if (maxFrames >= 0 && scene.FrameNumber >= maxFrames)
                    break;

                if (useRaylib && RaylibGameView.ShouldClose())
                    break;

                if (settings.RestartRequested)
                {
                    settings.RestartRequested = false;
                    settings.PanelOpen = false;
                    scene.ResetRound();
                }

                if (settings.AutoRevive && scene.IsRoundOver)
                {
                    scene.ResetRound();
                }

                if (scene.IsRoundOver && !settings.AutoRevive)
                {
                    view?.Render(scene);
                    if (!headless)
                    {
                        string winner = "Draw";
                        if (scene.WinnerId >= 0)
                            winner = scene.World.Identity[scene.WinnerId].Data?.Name ?? "?";
                        Console.WriteLine($"\n  Round Over! Winner: {winner}");
                    }
                    Thread.Sleep(2000);
                    scene.ResetRound();
                }

                scene.IsPaused = settings.PanelOpen;

                // ── Collect P1 input ─────────────────────────────
                PlayerInput p1Input;
                if (settings.PanelOpen || headless)
                {
                    p1Input = PlayerInput.Empty;
                }
                else if (useRaylib)
                {
                    p1Input = RaylibGameView.CollectInput(prevP1);
                    prevP1 = p1Input.Held;
                }
                else
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

                frameInput.Commands.Clear();
                frameInput.CharacterInputs.Clear();
                frameInput.CharacterInputs[0] = p1Input;
                frameInput.CharacterInputs[1] = PlayerInput.ComputeEdge(prevP2, InputButton.None);

                scene.Step(frameInput);
                view?.Render(scene);

                if (!headless && !useRaylib)
                    Thread.Sleep(16); // ~60fps (Raylib paces itself via SetTargetFPS)
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
