namespace KOF98.Game
{
    /// <summary>
    /// Two-tier time clock for the simulation:
    ///
    ///   Scene line     — global heartbeat. Drives <see cref="GameScene.FrameNumber"/>
    ///                    and round / match timing. Always advances unless the
    ///                    menu pause (<see cref="GameScene.IsPaused"/>) is held.
    ///   Character line — per-entity heartbeat stored in
    ///                    <see cref="FrameLineComponent"/>. A character with
    ///                    <c>PauseFrames &gt; 0</c> is frozen this frame:
    ///                    physics, skill ticks, status decrement and auto-face
    ///                    are all skipped so hit-pause and time-stop fall out
    ///                    naturally.
    ///
    /// Pause-frame stacking policy: <c>max(old, new)</c> — overlapping hit-pause
    /// windows produce the longest of the two, never their sum. This matches
    /// classic 2D fighter behavior.
    /// </summary>
    public static class FrameLineSystem
    {
        /// <summary>True if the character entity is frozen this frame.</summary>
        public static bool IsCharacterPaused(GameWorld world, int entity)
        {
            if (world == null) return false;
            if (entity < 0 || entity >= GameWorld.MaxEntities) return false;
            return world.FrameLine[entity].PauseFrames > 0;
        }

        /// <summary>Add (max-stacked) pause frames to a single character.</summary>
        public static void PauseCharacter(GameWorld world, int entity, int frames)
        {
            if (frames <= 0) return;
            if (entity < 0 || entity >= GameWorld.MaxEntities) return;
            if (!world.IsAliveSlot(entity)) return;
            ref var fl = ref world.FrameLine[entity];
            if (frames > fl.PauseFrames) fl.PauseFrames = frames;
        }

        /// <summary>
        /// End-of-frame: advance every alive character's frame line. A character
        /// that was paused this frame consumes one pause frame and does NOT
        /// advance its <see cref="FrameLineComponent.LocalFrame"/>.
        /// </summary>
        public static void AdvanceCharacters(GameWorld world)
        {
            for (int e = 0; e < GameConstants.MaxCharacters; e++)
            {
                if (!world.IsAliveSlot(e) || world.Kinds[e] != EntityKind.Character) continue;
                ref var fl = ref world.FrameLine[e];
                if (fl.PauseFrames > 0) fl.PauseFrames--;
                else fl.LocalFrame++;
            }
        }

        /// <summary>
        /// End-of-frame: advance the scene line. The scene's pause counter
        /// is decremented here so a freshly requested time-stop covers the
        /// next frame, not the current one.
        /// </summary>
        public static void AdvanceScene(GameScene scene)
        {
            if (scene.GlobalPauseFrames > 0) scene.GlobalPauseFrames--;
            scene.GlobalFrame++;
        }
    }
}
