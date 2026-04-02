using System;
using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// Unified numeric type for deterministic game logic.
    /// Development: float for fast iteration.
    /// Release: Fix64 for deterministic frame-sync.
    /// Toggle via USE_FIXPOINT compilation symbol.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public readonly struct Number : IEquatable<Number>, IComparable<Number>
    {
#if USE_FIXPOINT
        // Fix64: Q31.32 fixed-point (raw long)
        [FieldOffset(0)] public readonly long Raw;

        private const long ONE = 1L << 32;
        private const long HALF = 1L << 31;

        public Number(long raw)
        {
            Raw = raw;
        }

        public static Number FromInt(int value) => new Number((long)value << 32);
        public static Number FromFloat(float value) => new Number((long)(value * ONE));

        public int ToInt() => (int)(Raw >> 32);
        public float ToFloat() => (float)Raw / ONE;

        public static readonly Number Zero = new Number(0);
        public static readonly Number One = new Number(ONE);
        public static readonly Number MinusOne = new Number(-ONE);
        public static readonly Number Half = new Number(HALF);
        public static readonly Number MaxValue = new Number(long.MaxValue);
        public static readonly Number MinValue = new Number(long.MinValue);

        public static Number operator +(Number a, Number b) => new Number(a.Raw + b.Raw);
        public static Number operator -(Number a, Number b) => new Number(a.Raw - b.Raw);
        public static Number operator -(Number a) => new Number(-a.Raw);

        public static Number operator *(Number a, Number b)
        {
            // Q31.32 * Q31.32 → shift right 32
            long al = a.Raw;
            long bl = b.Raw;
            // Split to avoid overflow
            long aHi = al >> 32;
            long aLo = al & 0xFFFFFFFFL;
            long bHi = bl >> 32;
            long bLo = bl & 0xFFFFFFFFL;
            long result = (aHi * bHi) << 32;
            result += aHi * bLo;
            result += aLo * bHi;
            result += (aLo * bLo) >> 32;
            return new Number(result);
        }

        public static Number operator /(Number a, Number b)
        {
            if (b.Raw == 0) return Zero; // Panic handled at VM level

            // (a.Raw << 32) / b.Raw overflows 64-bit; use split long division.
            long la = a.Raw;
            long lb = b.Raw;
            bool neg = (la < 0) != (lb < 0);
            ulong ula = la >= 0 ? (ulong)la : (ulong)(-la);
            ulong ulb = lb >= 0 ? (ulong)lb : (ulong)(-lb);

            ulong quotient = ula / ulb;   // integer part of Q31.32 result
            ulong remainder = ula % ulb;

            // Compute 32 fractional bits via iterative long division
            ulong frac = 0;
            for (int i = 31; i >= 0; i--)
            {
                bool overflowed = (remainder & 0x8000000000000000UL) != 0;
                remainder <<= 1;
                if (overflowed || remainder >= ulb)
                {
                    remainder -= ulb;
                    frac |= 1UL << i;
                }
            }

            ulong raw = (quotient << 32) | frac;
            long result = (long)raw;
            return new Number(neg ? -result : result);
        }

        public static Number operator %(Number a, Number b)
        {
            if (b.Raw == 0) return Zero;
            return new Number(a.Raw % b.Raw);
        }

        public static bool operator ==(Number a, Number b) => a.Raw == b.Raw;
        public static bool operator !=(Number a, Number b) => a.Raw != b.Raw;
        public static bool operator <(Number a, Number b) => a.Raw < b.Raw;
        public static bool operator >(Number a, Number b) => a.Raw > b.Raw;
        public static bool operator <=(Number a, Number b) => a.Raw <= b.Raw;
        public static bool operator >=(Number a, Number b) => a.Raw >= b.Raw;

        public bool Equals(Number other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Number n && Equals(n);
        public override int GetHashCode() => Raw.GetHashCode();
        public int CompareTo(Number other) => Raw.CompareTo(other.Raw);
        public override string ToString() => ToFloat().ToString("F4");

#else
        // Development mode: raw float stored in 8 bytes for layout compatibility
        [FieldOffset(0)] public readonly double RawDouble;

        public Number(double value)
        {
            RawDouble = value;
        }

        public static Number FromInt(int value) => new Number(value);
        public static Number FromFloat(float value) => new Number(value);

        public int ToInt() => (int)RawDouble;
        public float ToFloat() => (float)RawDouble;

        public static readonly Number Zero = new Number(0.0);
        public static readonly Number One = new Number(1.0);
        public static readonly Number MinusOne = new Number(-1.0);
        public static readonly Number Half = new Number(0.5);
        public static readonly Number MaxValue = new Number(double.MaxValue);
        public static readonly Number MinValue = new Number(double.MinValue);

        public static Number operator +(Number a, Number b) => new Number(a.RawDouble + b.RawDouble);
        public static Number operator -(Number a, Number b) => new Number(a.RawDouble - b.RawDouble);
        public static Number operator -(Number a) => new Number(-a.RawDouble);
        public static Number operator *(Number a, Number b) => new Number(a.RawDouble * b.RawDouble);

        public static Number operator /(Number a, Number b)
        {
            if (b.RawDouble == 0.0) return Zero;
            return new Number(a.RawDouble / b.RawDouble);
        }

        public static Number operator %(Number a, Number b)
        {
            if (b.RawDouble == 0.0) return Zero;
            return new Number(a.RawDouble % b.RawDouble);
        }

        public static bool operator ==(Number a, Number b) => a.RawDouble == b.RawDouble;
        public static bool operator !=(Number a, Number b) => a.RawDouble != b.RawDouble;
        public static bool operator <(Number a, Number b) => a.RawDouble < b.RawDouble;
        public static bool operator >(Number a, Number b) => a.RawDouble > b.RawDouble;
        public static bool operator <=(Number a, Number b) => a.RawDouble <= b.RawDouble;
        public static bool operator >=(Number a, Number b) => a.RawDouble >= b.RawDouble;

        public bool Equals(Number other) => RawDouble == other.RawDouble;
        public override bool Equals(object obj) => obj is Number n && Equals(n);
        public override int GetHashCode() => RawDouble.GetHashCode();
        public int CompareTo(Number other) => RawDouble.CompareTo(other.RawDouble);
        public override string ToString() => RawDouble.ToString("F4");
#endif

        // Implicit conversions for ergonomic scripting
        public static implicit operator Number(int value) => FromInt(value);
        public static implicit operator Number(float value) => FromFloat(value);
    }
}
