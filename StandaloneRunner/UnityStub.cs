using System;

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object msg) => Console.WriteLine($"[LOG] {msg}");
        public static void LogError(object msg) => Console.Error.WriteLine($"[ERR] {msg}");
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
