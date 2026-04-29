namespace KOF98.Game
{
    /// <summary>
    /// External commands queued for the next scene step (spawn character,
    /// set AI controller, etc.) and per-character input for that step.
    /// </summary>
    public class SceneInput
    {
        public readonly ISceneCommand[] Commands = new ISceneCommand[GameConstants.MaxSceneCommands];
        public int CommandCount;

        public readonly PlayerInput[] CharacterInputs = new PlayerInput[GameConstants.MaxCharacters];
        public readonly bool[] HasCharacterInput = new bool[GameConstants.MaxCharacters];

        public void ClearCharacterInputs()
        {
            for (int i = 0; i < GameConstants.MaxCharacters; i++)
            {
                CharacterInputs[i] = PlayerInput.Empty;
                HasCharacterInput[i] = false;
            }
        }

        public void SetCharacterInput(int characterEntity, PlayerInput input)
        {
            if (characterEntity < 0 || characterEntity >= GameConstants.MaxCharacters) return;
            CharacterInputs[characterEntity] = input;
            HasCharacterInput[characterEntity] = true;
        }

        public bool EnqueueCommand(ISceneCommand command)
        {
            if (command == null) return true;
            if (CommandCount >= Commands.Length) return false;
            Commands[CommandCount++] = command;
            return true;
        }

        public void ClearCommands()
        {
            for (int i = 0; i < CommandCount; i++)
                Commands[i] = null;
            CommandCount = 0;
        }
    }

    public interface ISceneCommand
    {
        void Apply(GameScene scene);
    }

    /// <summary>Spawn a new character in the scene.</summary>
    public class CreateCharacterCommand : ISceneCommand
    {
        public int Team;
        public int CharacterId;
        public FVec2 StartPosition;

        public CreateCharacterCommand(int team, int characterId, FVec2 startPosition)
        {
            Team = team;
            CharacterId = characterId;
            StartPosition = startPosition;
        }

        public void Apply(GameScene scene)
        {
            CharacterFactory.Spawn(scene.World, Team, CharacterId, StartPosition);
        }
    }

    /// <summary>Attach an AI controller to a character.</summary>
    public class SetAICommand : ISceneCommand
    {
        public int CharacterEntity;
        public AIKind Kind;

        public SetAICommand(int charEntity, AIKind kind)
        {
            CharacterEntity = charEntity;
            Kind = kind;
        }

        public void Apply(GameScene scene) => scene.SetAI(CharacterEntity, Kind);
    }
}
