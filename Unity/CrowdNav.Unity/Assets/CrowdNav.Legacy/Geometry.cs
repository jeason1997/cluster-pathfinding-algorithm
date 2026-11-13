using System;
using System.Globalization;

namespace CrowdNav
{
    /// <summary>2D ground-plane vector (X, Z). The core deliberately avoids UnityEngine types.</summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        public float X;
        public float Z;

        public Vec2(float x, float z)
        {
            X = x;
            Z = z;
        }

        public static readonly Vec2 Zero = new Vec2(0f, 0f);
        public static readonly Vec2 UnitX = new Vec2(1f, 0f);

        public float LengthSq => X * X + Z * Z;
        public float Length => MathF.Sqrt(LengthSq);

        public Vec2 Normalized()
        {
            float len = Length;
            return len > 1e-8f ? new Vec2(X / len, Z / len) : Zero;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Z + b.Z);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Z - b.Z);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Z);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Z * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Z * s);
        public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Z / s);

        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;
        public static float Cross(Vec2 a, Vec2 b) => a.X * b.Z - a.Z * b.X;
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;
        public static float DistanceSq(Vec2 a, Vec2 b) => (a - b).LengthSq;

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);

        public static Vec2 FromAngle(float radians) => new Vec2(MathF.Cos(radians), MathF.Sin(radians));
        public float Angle => MathF.Atan2(Z, X);

        public Vec2 Rotated(float radians)
        {
            float c = MathF.Cos(radians);
            float s = MathF.Sin(radians);
            return new Vec2(X * c - Z * s, X * s + Z * c);
        }

        public Vec2 PerpLeft => new Vec2(-Z, X);
        public Vec2 PerpRight => new Vec2(Z, -X);

        public bool Equals(Vec2 other) => X == other.X && Z == other.Z;

        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Z.GetHashCode();
            }
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", X, Z);
    }

    public struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    public static class MathUtil
    {
        public const float Epsilon = 1e-6f;
        public const float TwoPi = 6.28318530718f;

        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        public static float MoveTowards(float current, float target, float maxDelta)
        {
            float delta = target - current;
            if (MathF.Abs(delta) <= maxDelta) return target;
            return current + MathF.Sign(delta) * maxDelta;
        }

        public static Vec2 MoveTowards(Vec2 current, Vec2 target, float maxDelta)
        {
            Vec2 delta = target - current;
            float len = delta.Length;
            if (len <= maxDelta || len < 1e-8f) return target;
            return current + delta * (maxDelta / len);
        }

        /// <summary>Signed shortest angular difference from a to b, in (-pi, pi].</summary>
        public static float AngleDelta(float a, float b)
        {
            float d = b - a;
            while (d > MathF.PI) d -= TwoPi;
            while (d < -MathF.PI) d += TwoPi;
            return d;
        }

        public static float ExpApproach(float current, float target, float rate, float dt)
        {
            float t = 1f - MathF.Exp(-rate * dt);
            return current + (target - current) * t;
        }

        public static Vec2 ExpApproach(Vec2 current, Vec2 target, float rate, float dt)
        {
            float t = 1f - MathF.Exp(-rate * dt);
            return current + (target - current) * t;
        }
    }

    /// <summary>Static circular obstacle in the ground plane.</summary>
    public struct CircleObstacle
    {
        public Vec2 Center;
        public float Radius;

        public CircleObstacle(Vec2 center, float radius)
        {
            Center = center;
            Radius = radius;
        }
    }
}
