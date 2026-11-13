using System;
using System.Collections.Generic;

namespace CrowdNav
{
    /// <summary>
    /// ORCA linear-programming local avoidance. The half-plane construction and the
    /// three linear programs are adapted from RVO2-CS (Apache-2.0, University of North
    /// Carolina at Chapel Hill). The surrounding crowd, terrain and hard non-overlap
    /// commit are original to this project.
    /// </summary>
    internal sealed class OrcaSolver
    {
        private const float Eps = 1e-5f;

        private struct Line
        {
            public Vec2 Point;
            public Vec2 Direction;
        }

        private readonly List<Line> _lines = new List<Line>(32);
        private readonly List<Line> _proj = new List<Line>(64);

        public Vec2 Compute(Agent self, IReadOnlyList<Agent> agents, IReadOnlyList<int> neighbors,
            Vec2 preferredVelocity, float maxSpeed, float timeHorizon, float dt, float safetyMargin)
        {
            _lines.Clear();
            float invTau = 1f / MathF.Max(timeHorizon, 1e-3f);
            float combinedMargin = safetyMargin;

            for (int ni = 0; ni < neighbors.Count; ni++)
            {
                Agent other = agents[neighbors[ni]];
                Vec2 relPos = other.Position - self.Position;
                Vec2 relVel = self.Velocity - other.Velocity;
                float distSq = relPos.LengthSq;
                float combined = self.Radius + other.Radius + combinedMargin;
                float combinedSq = combined * combined;

                Line line;
                Vec2 u;
                if (distSq > combinedSq)
                {
                    Vec2 w = relVel - relPos * invTau;
                    float wLenSq = w.LengthSq;
                    float dot1 = Vec2.Dot(w, relPos);
                    if (dot1 < 0f && dot1 * dot1 > combinedSq * wLenSq)
                    {
                        float wLen = MathF.Sqrt(wLenSq);
                        Vec2 unitW = w / MathF.Max(wLen, Eps);
                        line.Direction = new Vec2(unitW.Z, -unitW.X);
                        u = unitW * (combined * invTau - wLen);
                    }
                    else
                    {
                        float leg = MathF.Sqrt(MathF.Max(0f, distSq - combinedSq));
                        if (Vec2.Cross(relPos, w) > 0f)
                        {
                            line.Direction = new Vec2(
                                relPos.X * leg - relPos.Z * combined,
                                relPos.X * combined + relPos.Z * leg) / distSq;
                        }
                        else
                        {
                            line.Direction = -new Vec2(
                                relPos.X * leg + relPos.Z * combined,
                                -relPos.X * combined + relPos.Z * leg) / distSq;
                        }

                        float dot2 = Vec2.Dot(relVel, line.Direction);
                        u = line.Direction * dot2 - relVel;
                    }
                }
                else
                {
                    float invDt = 1f / MathF.Max(dt, 1e-4f);
                    Vec2 w = relVel - relPos * invDt;
                    float wLen = w.Length;
                    Vec2 unitW = w / MathF.Max(wLen, Eps);
                    line.Direction = new Vec2(unitW.Z, -unitW.X);
                    u = unitW * (combined * invDt - wLen);
                }

                line.Point = self.Velocity + u * 0.5f;
                _lines.Add(line);
            }

            int fail = LinearProgram2(_lines, maxSpeed, preferredVelocity, false, out Vec2 result);
            if (fail < _lines.Count)
                LinearProgram3(_lines, 0, fail, maxSpeed, ref result);
            return result;
        }

        private static bool LinearProgram1(List<Line> lines, int lineNo, float radius,
            Vec2 optVelocity, bool directionOpt, ref Vec2 result)
        {
            float dotProduct = Vec2.Dot(lines[lineNo].Point, lines[lineNo].Direction);
            float discriminant = dotProduct * dotProduct + radius * radius - lines[lineNo].Point.LengthSq;
            if (discriminant < 0f) return false;

            float sqrtDisc = MathF.Sqrt(discriminant);
            float tLeft = -dotProduct - sqrtDisc;
            float tRight = -dotProduct + sqrtDisc;

            for (int i = 0; i < lineNo; i++)
            {
                float denominator = Vec2.Cross(lines[lineNo].Direction, lines[i].Direction);
                float numerator = Vec2.Cross(lines[i].Direction, lines[lineNo].Point - lines[i].Point);
                if (MathF.Abs(denominator) <= Eps)
                {
                    if (numerator < 0f) return false;
                    continue;
                }

                float t = numerator / denominator;
                if (denominator >= 0f) tRight = MathF.Min(tRight, t);
                else tLeft = MathF.Max(tLeft, t);

                if (tLeft > tRight) return false;
            }

            if (directionOpt)
            {
                result = Vec2.Dot(optVelocity, lines[lineNo].Direction) > 0f
                    ? lines[lineNo].Point + lines[lineNo].Direction * tRight
                    : lines[lineNo].Point + lines[lineNo].Direction * tLeft;
            }
            else
            {
                float t = Vec2.Dot(lines[lineNo].Direction, optVelocity - lines[lineNo].Point);
                if (t < tLeft) result = lines[lineNo].Point + lines[lineNo].Direction * tLeft;
                else if (t > tRight) result = lines[lineNo].Point + lines[lineNo].Direction * tRight;
                else result = lines[lineNo].Point + lines[lineNo].Direction * t;
            }
            return true;
        }

        private static int LinearProgram2(List<Line> lines, float radius, Vec2 optVelocity,
            bool directionOpt, out Vec2 result)
        {
            if (directionOpt)
            {
                result = optVelocity * radius;
            }
            else if (optVelocity.LengthSq > radius * radius)
            {
                result = optVelocity.Normalized() * radius;
            }
            else
            {
                result = optVelocity;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (Vec2.Cross(lines[i].Direction, lines[i].Point - result) > 0f)
                {
                    Vec2 temp = result;
                    if (!LinearProgram1(lines, i, radius, optVelocity, directionOpt, ref result))
                    {
                        result = temp;
                        return i;
                    }
                }
            }
            return lines.Count;
        }

        private void LinearProgram3(List<Line> lines, int numObstLines, int beginLine,
            float radius, ref Vec2 result)
        {
            float distance = 0f;
            for (int i = beginLine; i < lines.Count; i++)
            {
                if (Vec2.Cross(lines[i].Direction, lines[i].Point - result) <= distance) continue;

                _proj.Clear();
                for (int ii = 0; ii < numObstLines; ii++) _proj.Add(lines[ii]);

                for (int j = numObstLines; j < i; j++)
                {
                    Line line;
                    float det = Vec2.Cross(lines[i].Direction, lines[j].Direction);
                    if (MathF.Abs(det) <= Eps)
                    {
                        if (Vec2.Dot(lines[i].Direction, lines[j].Direction) > 0f) continue;
                        line.Point = (lines[i].Point + lines[j].Point) * 0.5f;
                    }
                    else
                    {
                        line.Point = lines[i].Point + lines[i].Direction *
                            (Vec2.Cross(lines[j].Direction, lines[i].Point - lines[j].Point) / det);
                    }

                    line.Direction = (lines[j].Direction - lines[i].Direction).Normalized();
                    _proj.Add(line);
                }

                Vec2 temp = result;
                if (LinearProgram2(_proj, radius,
                        new Vec2(-lines[i].Direction.Z, lines[i].Direction.X), true, out result) < _proj.Count)
                {
                    result = temp;
                }

                distance = Vec2.Cross(lines[i].Direction, lines[i].Point - result);
            }
        }
    }
}
