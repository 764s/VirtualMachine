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
            int maxFrames = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--headless") headless = true;
                if (args[i] == "--frames" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out maxFrames);
            }

            Console.WriteLine("╔══════════════════════════════════════════╗");
            Console.WriteLine("║     KOF98 Practice — FFVM Exploration   ║");
            Console.WriteLine("╠══════════════════════════════════════════╣");
            Console.WriteLine("║  Ctrl+C to stop                         ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");
            Console.WriteLine();

            // ── Create scene ─────────────────────────────────────
            var scene = new GameScene();

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
            IGameView view = headless ? null : new ConsoleGameView();
            view?.Initialize(scene);

            // ── Game loop ────────────────────────────────────────
            bool running = true;
            Console.CancelKeyPress += (_, e) => { running = false; e.Cancel = true; };

            var frameInput = new SceneInput();
            InputButton prevP1 = InputButton.None;

            while (running)
            {
                if (maxFrames >= 0 && scene.FrameNumber >= maxFrames)
                    break;

                if (scene.IsRoundOver)
                {
                    view?.Render(scene);
                    Console.WriteLine($"\n  Round Over! Winner: {(scene.WinnerId >= 0 ? scene.Characters.Get(scene.WinnerId)?.Data?.Name ?? "?" : "Draw")}");
                    Thread.Sleep(2000);
                    scene.ResetRound(new FVec2(-3f, 0f), new FVec2(3f, 0f));
                }

                // ── Collect P1 input (keyboard) ──────────────────
                InputButton p1Held = InputButton.None;
                if (!headless && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    p1Held = MapKeyToInput(key.Key);
                }

                var p1Input = PlayerInput.ComputeEdge(prevP1, p1Held);
                prevP1 = p1Held;

                frameInput.Clear();
                frameInput.SetInput(0, p1Input);

                // ── Step simulation ──────────────────────────────
                scene.Step(frameInput);

                // ── Render ───────────────────────────────────────
                view?.Render(scene);

                // ── Frame pacing ─────────────────────────────────
                if (!headless)
                    Thread.Sleep(16); // ~60fps
            }

            view?.Shutdown();
            Console.WriteLine("\nKOF98 Practice ended.");
        }

        private static CharacterData CreateDefaultCharacterData(int id, string name)
        {
            // Create basic idle skill (host-driven, looping)
            var idleSkill = new SkillDef(
                id: 0, name: "Idle", totalFrames: -1,
                priority: GameConstants.PRIORITY_IDLE,
                tags: (1 << GameConstants.TAG_IDLE),
                looping: true);

            // Create basic light punch skill (host-driven, 20 frames)
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
                Skills = new[] { idleSkill, lpSkill },
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
