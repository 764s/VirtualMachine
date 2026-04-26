namespace KOF98.Game
{
    /// <summary>
    /// Replaceable view interface for rendering the game scene.
    ///
    /// Design: The view layer is decoupled from the simulation.
    /// Current implementation: ConsoleGameView (ASCII-based, no Unity dependency).
    /// Future: UnityGameView (sprite rendering, animation, camera).
    ///
    /// The view reads scene state after Step() and renders accordingly.
    /// It never modifies game state.
    /// </summary>
    public interface IGameView
    {
        /// <summary>Initialize the view (allocate buffers, set up rendering).</summary>
        void Initialize(GameScene scene);

        /// <summary>Render the current game state. Called once per frame after Step().</summary>
        void Render(GameScene scene);

        /// <summary>Shutdown the view (release resources).</summary>
        void Shutdown();
    }
}
