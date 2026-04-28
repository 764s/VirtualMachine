using System.Collections.Generic;

namespace KOF98.Game
{
    /// <summary>
    /// External commands queued for the next scene step (spawn character,
    /// set AI controller, etc.) and per-character input for that step.
    /// </summary>
    public class SceneInput
    {
        public List<ISceneCommand> Commands = new List<ISceneCommand>();

        /// <summary>
        /// Per-character input keyed by character entity slot index
        /// (the same id that GameScene.SpawnCharacter returned).
        /// </summary>
        public Dictionary<int, PlayerInput> CharacterInputs = new Dictionary<int, PlayerInput>();
    }

    public interface ISceneCommand
    {
        void Apply(GameScene scene);
    }

    /// <summary>Spawn a new character in the scene.</summary>
    public class CreateCharacterCommand : ISceneCommand
    {
        public int Team;
        public CharacterData Data;
        public FVec2 StartPosition;

        public CreateCharacterCommand(int team, CharacterData data, FVec2 startPosition)
        {
            Team = team;
            Data = data;
            StartPosition = startPosition;
        }

        public void Apply(GameScene scene)
        {
            CharacterFactory.Spawn(scene.World, Team, Data, StartPosition);
        }
    }

    /// <summary>Attach an AI controller to a character.</summary>
    public class SetAICommand : ISceneCommand
    {
        public int CharacterEntity;
        public IAIController AI;

        public SetAICommand(int charEntity, IAIController ai)
        {
            CharacterEntity = charEntity;
            AI = ai;
        }

        public void Apply(GameScene scene) => scene.SetAI(CharacterEntity, AI);
    }
}
