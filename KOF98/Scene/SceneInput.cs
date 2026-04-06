using System.Collections.Generic;

namespace KOF98
{
    /// <summary>
    /// Aggregated input for a single frame, submitted to GameScene.Step().
    /// Contains scene-level commands and per-character inputs.
    /// </summary>
    public class SceneInput
    {
        /// <summary>Scene-level commands (create character, reset round, etc.).</summary>
        public List<ISceneCommand> Commands = new();

        /// <summary>Per-character input. Key = character ID.</summary>
        public Dictionary<int, PlayerInput> CharacterInputs = new();

        public void AddCommand(ISceneCommand cmd) => Commands.Add(cmd);
        public void SetInput(int charId, PlayerInput input) => CharacterInputs[charId] = input;

        public void Clear()
        {
            Commands.Clear();
            CharacterInputs.Clear();
        }
    }

    /// <summary>
    /// Interface for scene-level commands.
    /// Commands are executed at the start of each frame before character updates.
    /// </summary>
    public interface ISceneCommand
    {
        void Execute(GameScene scene);
    }

    /// <summary>Create a new character in the scene.</summary>
    public class CreateCharacterCommand : ISceneCommand
    {
        public int Team;
        public CharacterData Data;
        public FVec2 StartPosition;

        public CreateCharacterCommand(int team, CharacterData data, FVec2 startPos)
        {
            Team = team;
            Data = data;
            StartPosition = startPos;
        }

        public void Execute(GameScene scene)
        {
            scene.Characters.CreateCharacter(Team, Data, StartPosition);
        }
    }

    /// <summary>Assign an AI controller to a character.</summary>
    public class SetAICommand : ISceneCommand
    {
        public int CharId;
        public IAIController AI;

        public SetAICommand(int charId, IAIController ai) { CharId = charId; AI = ai; }

        public void Execute(GameScene scene)
        {
            scene.SetAI(CharId, AI);
        }
    }
}
