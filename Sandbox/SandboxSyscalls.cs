using System;
using System.Collections.Generic;
using FFVM;

namespace Sandbox
{
    /// <summary>
    /// Predefined SysCall set for the FFScript Sandbox.
    /// Provides logging, time, math, and control syscalls independent of Unity.
    /// </summary>
    public static class SandboxSyscalls
    {
        // --- String label table for print_str ---
        private static readonly Dictionary<int, string> StringLabels = new Dictionary<int, string>
        {
            { 0, "result" },
            { 1, "value" },
            { 2, "count" },
            { 3, "time" },
            { 4, "delta" },
            { 5, "frame" },
            { 6, "error" },
            { 7, "debug" },
            { 8, "x" },
            { 9, "y" },
            { 10, "sum" },
            { 11, "diff" },
            { 12, "product" },
            { 13, "quotient" },
            { 14, "min" },
            { 15, "max" },
        };

        // --- Runtime state shared with SandboxRunner ---
        private static long _startTimeMs;
        private static long _lastTickTimeMs;
        private static long _currentTickTimeMs;
        private static int _frameCount;
        private static bool _exitRequested;
        private static Random _random = new Random();

        /// <summary>Action invoked for each print/print_str output. Defaults to Console.WriteLine.</summary>
        public static Action<string> LogOutput = msg => Console.WriteLine($"[SANDBOX] {msg}");

        /// <summary>Whether the script has requested exit via the exit() syscall.</summary>
        public static bool ExitRequested => _exitRequested;

        /// <summary>Current frame count (set by SandboxRunner before each Tick).</summary>
        public static int FrameCount => _frameCount;

        /// <summary>
        /// Reset runtime state. Call before starting a new run.
        /// </summary>
        public static void Reset()
        {
            _startTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastTickTimeMs = _startTimeMs;
            _currentTickTimeMs = _startTimeMs;
            _frameCount = 0;
            _exitRequested = false;
        }

        /// <summary>
        /// Update tick timing. Call before each VMWorld.Tick().
        /// </summary>
        public static void BeginTick(int frameCount)
        {
            _lastTickTimeMs = _currentTickTimeMs;
            _currentTickTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _frameCount = frameCount;
        }

        /// <summary>
        /// Build the syscall name → slot mapping for the compiler.
        /// </summary>
        public static Dictionary<string, int> GetSyscallMap()
        {
            return new Dictionary<string, int>
            {
                { "print",       0 },
                { "print_str",   1 },
                { "time",        2 },
                { "delta_time",  3 },
                { "random",      4 },
                { "abs",         5 },
                { "min",         6 },
                { "max",         7 },
                { "clamp",       8 },
                { "sqrt",        9 },
                { "frame_count", 10 },
                { "exit",        11 },
            };
        }

        /// <summary>
        /// Register all sandbox syscall handlers on the given SyscallTable.
        /// Also registers LSP6 signature metadata for editor support.
        /// </summary>
        public static void RegisterAll(SyscallTable table)
        {
            // Slot 0: print(value)  — print a numeric value
            table.Register(0, "print", (ref VMInstanceState s) =>
            {
                var val = s.Registers.Get(0);
                LogOutput(val.ToString());
            });
            table.RegisterSignature(0, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number") },
                "void", "Print a numeric value to the console"));

            // Slot 1: print_str(labelId, value) — print a labeled value
            table.Register(1, "print_str", (ref VMInstanceState s) =>
            {
                int labelId = s.Registers.Get(0).ToInt();
                var val = s.Registers.Get(1);
                string label = StringLabels.TryGetValue(labelId, out var labelText) ? labelText : $"#{labelId}";
                LogOutput($"{label} = {val}");
            });
            table.RegisterSignature(1, new SyscallSignature(
                new[] { new SyscallParamInfo("labelId", "int"), new SyscallParamInfo("value", "number") },
                "void", "Print a labeled value (labelId: 0=result,1=value,2=count,3=time,4=delta,5=frame,6=error,7=debug,8=x,9=y,10=sum)"));

            // Slot 2: time() → elapsed ms since run start
            table.Register(2, "time", (ref VMInstanceState s) =>
            {
                long elapsed = _currentTickTimeMs - _startTimeMs;
                s.Registers.Set(0, Number.FromInt((int)elapsed));
            });
            table.RegisterSignature(2, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(),
                "int", "Get elapsed milliseconds since run start"));

            // Slot 3: delta_time() → ms since last tick
            table.Register(3, "delta_time", (ref VMInstanceState s) =>
            {
                long delta = _currentTickTimeMs - _lastTickTimeMs;
                s.Registers.Set(0, Number.FromInt((int)delta));
            });
            table.RegisterSignature(3, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(),
                "int", "Get milliseconds since last tick"));

            // Slot 4: random(upperBound) → random int in [0, upperBound)
            table.Register(4, "random", (ref VMInstanceState s) =>
            {
                int upper = s.Registers.Get(0).ToInt();
                int result = upper > 0 ? _random.Next(upper) : 0;
                s.Registers.Set(0, Number.FromInt(result));
            });
            table.RegisterSignature(4, new SyscallSignature(
                new[] { new SyscallParamInfo("upperBound", "int") },
                "int", "Return a random integer in [0, upperBound)"));

            // Slot 5: abs(value) → |value|
            table.Register(5, "abs", (ref VMInstanceState s) =>
            {
                var val = s.Registers.Get(0);
                if (val < Number.Zero)
                    s.Registers.Set(0, -val);
            });
            table.RegisterSignature(5, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number") },
                "number", "Return the absolute value"));

            // Slot 6: min(a, b) → smaller of a and b
            table.Register(6, "min", (ref VMInstanceState s) =>
            {
                var a = s.Registers.Get(0);
                var b = s.Registers.Get(1);
                s.Registers.Set(0, a < b ? a : b);
            });
            table.RegisterSignature(6, new SyscallSignature(
                new[] { new SyscallParamInfo("a", "number"), new SyscallParamInfo("b", "number") },
                "number", "Return the smaller of two values"));

            // Slot 7: max(a, b) → larger of a and b
            table.Register(7, "max", (ref VMInstanceState s) =>
            {
                var a = s.Registers.Get(0);
                var b = s.Registers.Get(1);
                s.Registers.Set(0, a > b ? a : b);
            });
            table.RegisterSignature(7, new SyscallSignature(
                new[] { new SyscallParamInfo("a", "number"), new SyscallParamInfo("b", "number") },
                "number", "Return the larger of two values"));

            // Slot 8: clamp(value, lo, hi) → clamped value
            table.Register(8, "clamp", (ref VMInstanceState s) =>
            {
                var val = s.Registers.Get(0);
                var lo = s.Registers.Get(1);
                var hi = s.Registers.Get(2);
                if (val < lo) val = lo;
                else if (val > hi) val = hi;
                s.Registers.Set(0, val);
            });
            table.RegisterSignature(8, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number"), new SyscallParamInfo("lo", "number"), new SyscallParamInfo("hi", "number") },
                "number", "Clamp value between lo and hi"));

            // Slot 9: sqrt(value) → approximate square root
            table.Register(9, "sqrt", (ref VMInstanceState s) =>
            {
                var val = s.Registers.Get(0);
                float f = val.ToFloat();
                float result = f >= 0 ? (float)Math.Sqrt(f) : 0f;
                s.Registers.Set(0, Number.FromFloat(result));
            });
            table.RegisterSignature(9, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number") },
                "number", "Return the approximate square root"));

            // Slot 10: frame_count() → current frame number
            table.Register(10, "frame_count", (ref VMInstanceState s) =>
            {
                s.Registers.Set(0, Number.FromInt(_frameCount));
            });
            table.RegisterSignature(10, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(),
                "int", "Get the current frame number"));

            // Slot 11: exit() — request run loop to stop
            table.Register(11, "exit", (ref VMInstanceState s) =>
            {
                _exitRequested = true;
            });
            table.RegisterSignature(11, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(),
                "void", "Request the sandbox to stop running"));
        }

        /// <summary>
        /// Register a custom string label for use with print_str.
        /// </summary>
        public static void RegisterStringLabel(int id, string label)
        {
            StringLabels[id] = label;
        }
    }
}
