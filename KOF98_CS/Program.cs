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
            int snapshotRoundtripFrames = -1;
            int snapshotFuzzIterations = -1;
            int snapshotFuzzSeed = 1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--headless") headless = true;
                if (args[i] == "--raylib") useRaylib = true;
                if (args[i] == "--frames" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out maxFrames);
                if (args[i] == "--snapshot-roundtrip" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out snapshotRoundtripFrames);
                if (args[i] == "--snapshot-fuzz" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out snapshotFuzzIterations);
                if (args[i] == "--seed" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out snapshotFuzzSeed);
            }

            if (headless) useRaylib = false;

            if (snapshotRoundtripFrames > 0)
            {
                Environment.ExitCode = RunSnapshotRoundtrip(snapshotRoundtripFrames);
                return;
            }

            if (snapshotFuzzIterations > 0)
            {
                Environment.ExitCode = RunSnapshotFuzz(snapshotFuzzIterations, snapshotFuzzSeed);
                return;
            }

            if (!headless)
            {
                Console.WriteLine("KOF98_CS — ECS edition");
                Console.WriteLine("Skills: Idle / WalkForward / WalkBack");
                Console.WriteLine("Ctrl+C to stop");
                Console.WriteLine();
            }

            var scene = new GameScene();
            var settings = new GameSettings();

            var p1Data = CsSimSkillCatalog.BuildDefaultCharacterData("Kyo");
            var p2Data = CsSimSkillCatalog.BuildDefaultCharacterData("Iori");

            var initInput = new SceneInput();
            initInput.EnqueueCommand(new CreateCharacterCommand(0, p1Data.CatalogCharacterId, new FVec2(-3f, 0f)));
            initInput.EnqueueCommand(new CreateCharacterCommand(1, p2Data.CatalogCharacterId, new FVec2(3f, 0f)));
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
                            winner = GameCatalog.GetCharacter(scene.World.Identity[scene.WinnerId].CharacterId)?.Name ?? "?";
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

                frameInput.ClearCommands();
                frameInput.ClearCharacterInputs();
                frameInput.SetCharacterInput(0, p1Input);
                frameInput.SetCharacterInput(1, PlayerInput.ComputeEdge(prevP2, InputButton.None));

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

        /// <summary>
        /// Determinism self-test:
        ///   1. Spawn → step N frames → capture A.
        ///   2. Step M frames → capture B.
        ///   3. Restore A → step M frames → capture C.
        ///   4. Assert FirstDiff(B, C) == null.
        /// Exit code 0 on success, 1 on mismatch.
        /// </summary>
        private static int RunSnapshotRoundtrip(int n)
        {
            int m = System.Math.Max(1, n / 2);
            Console.WriteLine($"Snapshot roundtrip self-test: N={n}, M={m}");

            var scene = new GameScene();
            var p1 = CsSimSkillCatalog.BuildDefaultCharacterData("Kyo");
            var p2 = CsSimSkillCatalog.BuildDefaultCharacterData("Iori");
            var init = new SceneInput();
            init.EnqueueCommand(new CreateCharacterCommand(0, p1.CatalogCharacterId, new FVec2(-3f, 0f)));
            init.EnqueueCommand(new CreateCharacterCommand(1, p2.CatalogCharacterId, new FVec2(3f, 0f)));
            scene.Step(init);

            var empty = new SceneInput();
            for (int i = 0; i < n; i++) scene.Step(empty);

            var snapA = GameSnapshot.CreateBuffer();
            snapA.CaptureFrom(scene.World);

            for (int i = 0; i < m; i++) scene.Step(empty);
            var snapB = GameSnapshot.CreateBuffer();
            snapB.CaptureFrom(scene.World);

            // Restore A and replay M frames.
            snapA.RestoreTo(scene.World);
            for (int i = 0; i < m; i++) scene.Step(empty);
            var snapC = GameSnapshot.CreateBuffer();
            snapC.CaptureFrom(scene.World);

            string diff = GameSnapshot.FirstDiff(snapB, snapC);
            if (diff == null)
            {
                Console.WriteLine("Snapshot roundtrip OK (B == C).");
                return 0;
            }
            Console.WriteLine($"Snapshot roundtrip FAIL: first diff = {diff}");
            return 1;
        }

        /// <summary>
        /// Random-input fuzz over the snapshot roundtrip invariant.
        /// Each iteration:
        ///   - Random N in [60, 600] (warm-up), M in [30, N] (replay window).
        ///   - Random per-frame P1/P2 inputs (bounded button menu).
        ///   - Run N → capture A → run M with sequence S → capture B
        ///     → restore A → replay sequence S → capture C.
        ///   - Assert FirstDiff(B, C) == null.
        /// Strictly on-demand. Not part of the standard headless smoke.
        /// </summary>
        private static int RunSnapshotFuzz(int iterations, int seed)
        {
            Console.WriteLine($"Snapshot fuzz: iterations={iterations}, seed={seed}");
            var rng = new Random(seed);

            // Bounded menu — weighted toward None to keep things meaningful.
            InputButton[] menu = new[]
            {
                InputButton.None, InputButton.None, InputButton.None,
                InputButton.Left, InputButton.Right,
                InputButton.Up, InputButton.Down,
                InputButton.LP, InputButton.HP, InputButton.LK, InputButton.HK,
            };

            var snapA = GameSnapshot.CreateBuffer();
            var snapB = GameSnapshot.CreateBuffer();
            var snapC = GameSnapshot.CreateBuffer();

            for (int iter = 0; iter < iterations; iter++)
            {
                int n = 60 + rng.Next(0, 541);                       // [60, 600]
                int m = 30 + rng.Next(0, System.Math.Max(1, n - 30)); // [30, ~n]

                var scene = new GameScene();
                var p1 = CsSimSkillCatalog.BuildDefaultCharacterData("Kyo");
                var p2 = CsSimSkillCatalog.BuildDefaultCharacterData("Iori");
                var init = new SceneInput();
                init.EnqueueCommand(new CreateCharacterCommand(0, p1.CatalogCharacterId, new FVec2(-3f, 0f)));
                init.EnqueueCommand(new CreateCharacterCommand(1, p2.CatalogCharacterId, new FVec2(3f, 0f)));
                scene.Step(init);

                var stepInput = new SceneInput();
                InputButton prev1 = InputButton.None, prev2 = InputButton.None;

                for (int f = 0; f < n; f++)
                {
                    var h1 = menu[rng.Next(menu.Length)];
                    var h2 = menu[rng.Next(menu.Length)];
                    stepInput.ClearCharacterInputs();
                    stepInput.SetCharacterInput(0, PlayerInput.ComputeEdge(prev1, h1));
                    stepInput.SetCharacterInput(1, PlayerInput.ComputeEdge(prev2, h2));
                    prev1 = h1; prev2 = h2;
                    scene.Step(stepInput);
                }

                snapA.CaptureFrom(scene.World);
                InputButton savedPrev1 = prev1, savedPrev2 = prev2;

                // Pre-generate the replay sequence so the two paths see identical inputs.
                var seq1 = new InputButton[m];
                var seq2 = new InputButton[m];
                for (int f = 0; f < m; f++)
                {
                    seq1[f] = menu[rng.Next(menu.Length)];
                    seq2[f] = menu[rng.Next(menu.Length)];
                }

                // Path 1: continue from current state.
                for (int f = 0; f < m; f++)
                {
                    stepInput.ClearCharacterInputs();
                    stepInput.SetCharacterInput(0, PlayerInput.ComputeEdge(prev1, seq1[f]));
                    stepInput.SetCharacterInput(1, PlayerInput.ComputeEdge(prev2, seq2[f]));
                    prev1 = seq1[f]; prev2 = seq2[f];
                    scene.Step(stepInput);
                }
                snapB.CaptureFrom(scene.World);

                // Path 2: restore A and replay the same sequence.
                snapA.RestoreTo(scene.World);
                prev1 = savedPrev1; prev2 = savedPrev2;
                for (int f = 0; f < m; f++)
                {
                    stepInput.ClearCharacterInputs();
                    stepInput.SetCharacterInput(0, PlayerInput.ComputeEdge(prev1, seq1[f]));
                    stepInput.SetCharacterInput(1, PlayerInput.ComputeEdge(prev2, seq2[f]));
                    prev1 = seq1[f]; prev2 = seq2[f];
                    scene.Step(stepInput);
                }
                snapC.CaptureFrom(scene.World);

                string diff = GameSnapshot.FirstDiff(snapB, snapC);
                if (diff != null)
                {
                    Console.WriteLine($"Snapshot fuzz FAIL @ iter={iter} N={n} M={m} seed={seed}: first diff = {diff}");
                    return 1;
                }

                if ((iter + 1) % 100 == 0)
                    Console.WriteLine($"  ... {iter + 1}/{iterations} OK");
            }

            Console.WriteLine($"Snapshot fuzz OK ({iterations} iterations).");
            return 0;
        }
    }
}
