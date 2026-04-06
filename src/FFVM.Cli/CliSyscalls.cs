using System;
using System.Collections.Generic;
using FFVM;

namespace FFVM.Cli
{
    /// <summary>
    /// Built-in syscall set for the ffvm CLI.
    /// Provides logging, time, math, and control syscalls.
    /// </summary>
    internal static class CliSyscalls
    {
        private static readonly Dictionary<int, string> StringLabels = new Dictionary<int, string>
        {
            { 0, "result" }, { 1, "value" }, { 2, "count" }, { 3, "time" },
            { 4, "delta" }, { 5, "frame" }, { 6, "error" }, { 7, "debug" },
            { 8, "x" }, { 9, "y" }, { 10, "sum" }, { 11, "diff" },
            { 12, "product" }, { 13, "quotient" }, { 14, "min" }, { 15, "max" },
        };

        private static long _startTimeMs;
        private static long _lastTickTimeMs;
        private static long _currentTickTimeMs;
        private static int _frameCount;
        private static bool _exitRequested;
        private static Random _random = new Random();

        public static bool ExitRequested => _exitRequested;

        public static void Reset()
        {
            _startTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastTickTimeMs = _startTimeMs;
            _currentTickTimeMs = _startTimeMs;
            _frameCount = 0;
            _exitRequested = false;
        }

        public static void BeginTick(int frameCount)
        {
            _lastTickTimeMs = _currentTickTimeMs;
            _currentTickTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _frameCount = frameCount;
        }

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

        public static void RegisterAll(SyscallTable table)
        {
            table.Register(0, "print", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                Console.WriteLine(args.GetNumber(0));
            });
            table.RegisterSignature(0, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number") },
                "void", "Print a numeric value"));

            table.Register(1, "print_str", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int labelId = args.GetInt(0);
                var val = args.GetNumber(1);
                string label = StringLabels.TryGetValue(labelId, out var l) ? l : $"#{labelId}";
                Console.WriteLine($"{label} = {val}");
            });
            table.RegisterSignature(1, new SyscallSignature(
                new[] { new SyscallParamInfo("labelId", "int"), new SyscallParamInfo("value", "number") },
                "void", "Print a labeled value"));

            table.Register(2, "time", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                args.SetReturnInt((int)(_currentTickTimeMs - _startTimeMs));
            });
            table.RegisterSignature(2, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(), "int", "Get elapsed milliseconds since run start"));

            table.Register(3, "delta_time", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                args.SetReturnInt((int)(_currentTickTimeMs - _lastTickTimeMs));
            });
            table.RegisterSignature(3, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(), "int", "Get milliseconds since last tick"));

            table.Register(4, "random", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int upper = args.GetInt(0);
                args.SetReturnInt(upper > 0 ? _random.Next(upper) : 0);
            });
            table.RegisterSignature(4, new SyscallSignature(
                new[] { new SyscallParamInfo("upperBound", "int") }, "int", "Random integer in [0, upperBound)"));

            table.Register(5, "abs", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var val = args.GetNumber(0);
                if (val < Number.Zero) args.SetReturn(-val);
            });
            table.RegisterSignature(5, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number") }, "number", "Absolute value"));

            table.Register(6, "min", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var a = args.GetNumber(0);
                var b = args.GetNumber(1);
                args.SetReturn(a < b ? a : b);
            });
            table.RegisterSignature(6, new SyscallSignature(
                new[] { new SyscallParamInfo("a", "number"), new SyscallParamInfo("b", "number") },
                "number", "Smaller of two values"));

            table.Register(7, "max", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var a = args.GetNumber(0);
                var b = args.GetNumber(1);
                args.SetReturn(a > b ? a : b);
            });
            table.RegisterSignature(7, new SyscallSignature(
                new[] { new SyscallParamInfo("a", "number"), new SyscallParamInfo("b", "number") },
                "number", "Larger of two values"));

            table.Register(8, "clamp", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                var val = args.GetNumber(0);
                var lo = args.GetNumber(1);
                var hi = args.GetNumber(2);
                if (val < lo) val = lo;
                else if (val > hi) val = hi;
                args.SetReturn(val);
            });
            table.RegisterSignature(8, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number"), new SyscallParamInfo("lo", "number"), new SyscallParamInfo("hi", "number") },
                "number", "Clamp value between lo and hi"));

            table.Register(9, "sqrt", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                float f = args.GetFloat(0);
                args.SetReturnFloat(f >= 0 ? (float)Math.Sqrt(f) : 0f);
            });
            table.RegisterSignature(9, new SyscallSignature(
                new[] { new SyscallParamInfo("value", "number") }, "number", "Square root"));

            table.Register(10, "frame_count", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                args.SetReturnInt(_frameCount);
            });
            table.RegisterSignature(10, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(), "int", "Current frame number"));

            table.Register(11, "exit", (ref VMInstanceState s) =>
            {
                _exitRequested = true;
            });
            table.RegisterSignature(11, new SyscallSignature(
                Array.Empty<SyscallParamInfo>(), "void", "Stop execution"));
        }
    }
}
