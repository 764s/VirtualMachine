using UnityEngine;

public static class SkillScriptTests
{
#if UNITY_EDITOR
	[UnityEditor.MenuItem("TestVM/RunSkillScriptTests")]
#endif
	public static void RunAll()
	{
		Debug.Log("[SkillScriptTests] Placeholder test entrypoint.");
	}
}
