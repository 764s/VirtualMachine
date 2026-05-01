using System;

namespace UnityEngine
{
    public static class Debug
    {
        // Centralized fail-fast counter for the StandaloneRunner test driver.
        // Production code under Assets/Scripts/VM/ does NOT call LogError; every
        // LogError under Assets/Scripts/VM/Tests/ marks an assertion failure
        // (always with a "[FAIL]" prefix). Program.Main inspects this counter
        // after RunAll() and exits non-zero if any test reported a failure.
        // Tests that legitimately need to log diagnostic errors without
        // signaling a CI failure should call LogWarning or LogInfo instead.
        public static int LogErrorCount;

        public static void Log(object msg) => Console.WriteLine($"[LOG] {msg}");

        public static void LogError(object msg)
        {
            System.Threading.Interlocked.Increment(ref LogErrorCount);
            Console.Error.WriteLine($"[ERR] {msg}");
        }
    }

    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItemAttribute : Attribute
    {
        public MenuItemAttribute(string itemName) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction, int priority) { }
    }
}
