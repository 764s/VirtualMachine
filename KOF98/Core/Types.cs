namespace KOF98
{
    /// <summary>
    /// Facing direction. Right = positive X axis.
    /// </summary>
    public enum Direction : byte
    {
        Right = 0,
        Left = 1,
    }

    /// <summary>
    /// 2D vector using float. Future: replace with Fix64 for deterministic simulation.
    /// </summary>
    public struct FVec2
    {
        public float X;
        public float Y;

        public FVec2(float x, float y) { X = x; Y = y; }

        public static FVec2 Zero => new FVec2(0, 0);

        public static FVec2 operator +(FVec2 a, FVec2 b) => new FVec2(a.X + b.X, a.Y + b.Y);
        public static FVec2 operator -(FVec2 a, FVec2 b) => new FVec2(a.X - b.X, a.Y - b.Y);
        public static FVec2 operator *(FVec2 v, float s) => new FVec2(v.X * s, v.Y * s);
        public static FVec2 operator *(float s, FVec2 v) => new FVec2(v.X * s, v.Y * s);
        public static FVec2 operator -(FVec2 v) => new FVec2(-v.X, -v.Y);

        public float SqrMagnitude => X * X + Y * Y;
        public float DistanceTo(FVec2 other) => (float)System.Math.Sqrt((this - other).SqrMagnitude);

        /// <summary>Horizontal distance (absolute value).</summary>
        public float HDistanceTo(FVec2 other) => System.Math.Abs(X - other.X);

        public override string ToString() => $"({X:F2}, {Y:F2})";
    }

    /// <summary>
    /// Axis-aligned bounding box for collision detection.
    /// Defined as center offset + half extents, relative to character anchor.
    /// </summary>
    public struct FRect
    {
        public float OffsetX;
        public float OffsetY;
        public float HalfWidth;
        public float HalfHeight;

        public FRect(float offsetX, float offsetY, float halfWidth, float halfHeight)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
        }

        public static readonly FRect Empty = default;

        public bool IsEmpty => HalfWidth <= 0 || HalfHeight <= 0;

        /// <summary>Get world-space AABB given character position and facing direction sign (+1/-1).</summary>
        public void GetWorldBounds(FVec2 pos, int facingSign,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            float cx = pos.X + OffsetX * facingSign;
            float cy = pos.Y + OffsetY;
            minX = cx - HalfWidth;
            minY = cy - HalfHeight;
            maxX = cx + HalfWidth;
            maxY = cy + HalfHeight;
        }

        /// <summary>Test AABB overlap between two world-space boxes.</summary>
        public static bool Overlaps(
            float minAX, float minAY, float maxAX, float maxAY,
            float minBX, float minBY, float maxBX, float maxBY)
        {
            return minAX < maxBX && maxAX > minBX && minAY < maxBY && maxAY > minBY;
        }
    }
}
