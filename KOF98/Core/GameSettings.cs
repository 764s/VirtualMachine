namespace KOF98
{
    /// <summary>
    /// Shared game settings accessible by both view and game loop.
    /// Modified by the control panel UI; read by the main loop to affect behavior.
    /// </summary>
    public class GameSettings
    {
        /// <summary>Whether AI is active for AI-controlled characters.</summary>
        public bool AIEnabled { get; set; } = true;

        /// <summary>Whether characters auto-revive at full HP when killed.</summary>
        public bool AutoRevive { get; set; }

        /// <summary>Set to true by the UI to request a scene restart.</summary>
        public bool RestartRequested { get; set; }

        /// <summary>Whether the control panel overlay is open (pauses game).</summary>
        public bool PanelOpen { get; set; }
    }
}
