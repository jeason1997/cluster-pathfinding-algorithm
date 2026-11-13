using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    public struct CrowdOrcaLine
    {
        public float2 Point;
        public float2 Direction;
    }

    /// <summary>
    /// RVO2-style ORCA half-planes + three linear programs, Burst-compiled per agent.
    /// The half-plane construction follows RVO2-CS (Apache-2.0); the crowd/grid integration
    /// and the commit layer are the same design as the managed prototype.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdOrcaJob : IJobParallelFor
    {
        private const float Eps = 1e-5f;

        [ReadOnly] public CrowdGrid Grid;
        [ReadOnly] public CrowdHash Hash;
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float2> PreferredVelocity;
        [ReadOnly] public NativeArray<float2> SlotPosition;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<float> MaxSpeed;
        [ReadOnly] public NativeArray<float> SpeedScale;
        [ReadOnly] public NativeArray<float> DistanceToSlot;
        [ReadOnly] public NativeArray<float> BlockedTime;
        [ReadOnly] public NativeArray<byte> State;

        public NativeArray<float2> ChosenVelocity;
        public NativeArray<float> AvoidSign;
        public NativeArray<float> AvoidSignTimer;
        [NativeDisableParallelForRestriction] public NativeArray<int> NeighborBuf;
        [NativeDisableParallelForRestriction] public NativeArray<CrowdOrcaLine> Lines;
        [NativeDisableParallelForRestriction] public NativeArray<CrowdOrcaLine> ProjLines;

        public int MaxNeighbors;
        public float NeighborDistance;
        public float AvoidanceHorizon;
        public float OrcaTimeHorizon;
        public float OrcaSafetyMargin;
        public float SafetyMargin;
        public float MaxAcceleration;
        public float TimeStep;
        public bool UseOrca;
        public bool KeepInsideGoal;
        public float2 GoalCenter;
        public float GoalRadius;

        public void Execute(int i)
        {
            byte state = State[i];
            if (state == (byte)CrowdAgentState.Arrived || state == (byte)CrowdAgentState.Unreachable)
            {
                ChosenVelocity[i] = float2.zero;
                return;
            }

            float2 pref = PreferredVelocity[i];
            float prefSpeed = math.length(pref);
            if (prefSpeed < 0.03f)
            {
                ChosenVelocity[i] = float2.zero;
                return;
            }

            int neighborOffset = i * MaxNeighbors;
            int neighborCount = Hash.GatherNearest(Position, Position[i], i, NeighborDistance,
                MaxNeighbors, NeighborBuf, neighborOffset);

            if (UseOrca)
            {
                int lineCount = 0;
                int lineBase = i * MaxNeighbors;
                float invTau = 1f / math.max(OrcaTimeHorizon, 1e-3f);
                float2 selfPos = Position[i];
                float2 selfVel = Velocity[i];
                float selfRadius = Radius[i];

                for (int ni = 0; ni < neighborCount; ni++)
                {
                    int j = NeighborBuf[neighborOffset + ni];
                    float2 relPos = Position[j] - selfPos;
                    float2 relVel = selfVel - Velocity[j];
                    float distSq = math.lengthsq(relPos);
                    float combined = selfRadius + Radius[j] + OrcaSafetyMargin;
                    float combinedSq = combined * combined;
                    CrowdOrcaLine line;
                    float2 u;
                    if (distSq > combinedSq)
                    {
                        float2 w = relVel - relPos * invTau;
                        float wLenSq = math.lengthsq(w);
                        float dot1 = math.dot(w, relPos);
                        if (dot1 < 0f && dot1 * dot1 > combinedSq * wLenSq)
                        {
                            float wLen = math.sqrt(wLenSq);
                            float2 unitW = w / math.max(wLen, Eps);
                            line.Direction = new float2(unitW.y, -unitW.x);
                            u = unitW * (combined * invTau - wLen);
                        }
                        else
                        {
                            float leg = math.sqrt(math.max(0f, distSq - combinedSq));
                            if (relPos.x * w.y - relPos.y * w.x > 0f)
                            {
                                line.Direction = new float2(
                                    relPos.x * leg - relPos.y * combined,
                                    relPos.x * combined + relPos.y * leg) / distSq;
                            }
                            else
                            {
                                line.Direction = -new float2(
                                    relPos.x * leg + relPos.y * combined,
                                    -relPos.x * combined + relPos.y * leg) / distSq;
                            }
                            float dot2 = math.dot(relVel, line.Direction);
                            u = line.Direction * dot2 - relVel;
                        }
                    }
                    else
                    {
                        float invDt = 1f / math.max(TimeStep, 1e-4f);
                        float2 w = relVel - relPos * invDt;
                        float wLen = math.length(w);
                        float2 unitW = w / math.max(wLen, Eps);
                        line.Direction = new float2(unitW.y, -unitW.x);
                        u = unitW * (combined * invDt - wLen);
                    }
                    line.Point = selfVel + u * 0.5f;
                    Lines[lineBase + lineCount] = line;
                    lineCount++;
                }

                float maxSpeed = MaxSpeed[i] * SpeedScale[i];
                int fail = LinearProgram2(Lines, lineBase, lineCount, maxSpeed, pref, false, out float2 result);
                if (fail < lineCount)
                    LinearProgram3(Lines, lineBase, lineCount, 0, fail, maxSpeed, ProjLines,
                        i * (2 * MaxNeighbors + 2), ref result);

                if (math.lengthsq(result) >= 1e-8f &&
                    StaticCandidateSafe(i, result, math.max(0.20f, TimeStep * 5f)))
                {
                    ChosenVelocity[i] = LimitAcceleration(i, result);
                    return;
                }
            }

            float separation = 2f * Radius[i] + SafetyMargin;
            float sepSq = separation * separation;
            float horizon = AvoidanceHorizon;
            float2 prefDir = pref / prefSpeed;
            float2 best = float2.zero;
            float bestScore = 0f;
            float bestMinGap = separation;

            for (int oi = 0; oi < 16; oi++)
            {
                float offsetDeg = DirectionOffsetDeg(oi);
                float offsetRad = offsetDeg * math.PI / 180f;
                float c = math.cos(offsetRad);
                float s = math.sin(offsetRad);
                float2 dir = new float2(prefDir.x * c - prefDir.y * s, prefDir.x * s + prefDir.y * c);
                float cos = math.dot(dir, prefDir);

                for (int si = 0; si < 3; si++)
                {
                    float candSpeed = prefSpeed * SpeedScaleOf(si);
                    if (candSpeed < 0.03f) continue;
                    float2 cand = dir * candSpeed;
                    if (!StaticCandidateSafe(i, cand, horizon * 0.8f)) continue;
                    if (!DynamicCandidateSafe(i, cand, horizon, sepSq, neighborCount,
                            neighborOffset, out float minGap))
                        continue;

                    float speedRatio = candSpeed / math.max(prefSpeed, 0.05f);
                    float score = 1.35f * cos + 0.65f * speedRatio - 0.20f * (math.abs(offsetRad) / math.PI);
                    if (offsetDeg > 0.1f) score -= 0.035f;
                    else if (offsetDeg < -0.1f) score += 0.035f;
                    if (AvoidSign[i] != 0f && math.sign(offsetRad) == AvoidSign[i] && math.abs(offsetRad) > 0.1f)
                        score += 0.06f;
                    if (State[i] != (byte)CrowdAgentState.Yielding && false) { }
                    float blockedTime = 0f;
                    if (blockedTime > 0.8f && math.abs(offsetDeg) > 90f) score += 1.20f;

                    float excess = math.max(0f, minGap - separation);
                    score += 0.10f * math.clamp(excess / math.max(separation, 0.01f), 0f, 1f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cand;
                        bestMinGap = minGap;
                    }
                }
            }

            if (bestScore > 0f && math.abs(best.x) + math.abs(best.y) > 1e-6f)
            {
                float sign = prefDir.x * best.y - prefDir.y * best.x;
                sign = sign > 0f ? 1f : (sign < 0f ? -1f : 0f);
                if (math.abs(sign) > 0.1f)
                {
                    AvoidSign[i] = sign;
                    AvoidSignTimer[i] = 0.7f;
                }
            }
            else
            {
                best = float2.zero;
            }
            ChosenVelocity[i] = LimitAcceleration(i, best);
        }

        private float2 LimitAcceleration(int i, float2 desired)
        {
            float2 delta = desired - Velocity[i];
            float deltaLen = math.length(delta);
            if (deltaLen > 1e-6f)
            {
                bool braking = math.lengthsq(desired) < math.lengthsq(Velocity[i]);
                float maxDelta = (braking ? MaxAcceleration * 1.8f : MaxAcceleration) * TimeStep;
                if (deltaLen > maxDelta) desired = Velocity[i] + delta * (maxDelta / deltaLen);
            }
            return desired;
        }

        private bool StaticCandidateSafe(int i, float2 velocity, float horizon)
        {
            float2 start = Position[i];
            float2 end = start + velocity * horizon;
            if (!Grid.IsWorldWalkable(end)) return false;
            if (!GoalBoundaryAllows(i, end)) return false;
            if (!GoalBoundaryAllows(i, start + velocity * (horizon * 0.5f))) return false;
            return Grid.IsSegmentWalkable(start, end, 1.15f);
        }

        private bool GoalBoundaryAllows(int i, float2 p)
        {
            if (!KeepInsideGoal || GoalRadius <= 0f) return true;
            if (State[i] == (byte)CrowdAgentState.Yielding) return true;
            float r = GoalRadius + 0.12f;
            if (math.distancesq(Position[i], GoalCenter) > r * r) return true;
            return math.distancesq(p, GoalCenter) <= r * r;
        }

        private bool DynamicCandidateSafe(int i, float2 candidate, float horizon, float sepSq,
            int neighborCount, int neighborOffset, out float minGap)
        {
            minGap = float.MaxValue;
            float2 p = Position[i];
            for (int k = 0; k < neighborCount; k++)
            {
                int j = NeighborBuf[neighborOffset + k];
                float2 bVel = State[j] == (byte)CrowdAgentState.Arrived ? float2.zero : Velocity[j];
                for (int f = 0; f < 4; f++)
                {
                    float t = horizon * PredictionFraction(f);
                    float2 pa = p + candidate * t;
                    float2 pb = Position[j] + bVel * t;
                    float d2 = math.distancesq(pa, pb);
                    if (d2 < sepSq) return false;
                    float d = math.sqrt(d2);
                    if (d < minGap) minGap = d;
                }
            }
            return true;
        }

        private static float DirectionOffsetDeg(int index)
        {
            switch (index)
            {
                case 0: return 0f; case 1: return 18f; case 2: return -18f;
                case 3: return 36f; case 4: return -36f; case 5: return 54f;
                case 6: return -54f; case 7: return 72f; case 8: return -72f;
                case 9: return 90f; case 10: return -90f; case 11: return 115f;
                case 12: return -115f; case 13: return 145f; case 14: return -145f;
                default: return 180f;
            }
        }

        private static float SpeedScaleOf(int index)
        {
            switch (index)
            {
                case 0: return 1f;
                case 1: return 0.62f;
                default: return 0.32f;
            }
        }

        private static float PredictionFraction(int index)
        {
            switch (index)
            {
                case 0: return 0.10f;
                case 1: return 0.26f;
                case 2: return 0.46f;
                default: return 0.68f;
            }
        }

        private static int LinearProgram2(NativeArray<CrowdOrcaLine> lines, int lineBase, int lineCount,
            float radius, float2 optVelocity, bool directionOpt, out float2 result)
        {
            if (directionOpt)
            {
                result = optVelocity * radius;
            }
            else if (math.lengthsq(optVelocity) > radius * radius)
            {
                result = math.normalizesafe(optVelocity) * radius;
            }
            else
            {
                result = optVelocity;
            }

            for (int i = 0; i < lineCount; i++)
            {
                CrowdOrcaLine line = lines[lineBase + i];
                if (line.Direction.x * (line.Point.y - result.y) -
                    line.Direction.y * (line.Point.x - result.x) > 0f)
                {
                    float2 temp = result;
                    if (!LinearProgram1(lines, lineBase, i, radius, optVelocity, directionOpt, ref result))
                    {
                        result = temp;
                        return i;
                    }
                }
            }
            return lineCount;
        }

        private static bool LinearProgram1(NativeArray<CrowdOrcaLine> lines, int lineBase, int lineNo,
            float radius, float2 optVelocity, bool directionOpt, ref float2 result)
        {
            CrowdOrcaLine line = lines[lineBase + lineNo];
            float dotProduct = math.dot(line.Point, line.Direction);
            float discriminant = dotProduct * dotProduct + radius * radius - math.lengthsq(line.Point);
            if (discriminant < 0f) return false;
            float sqrtDisc = math.sqrt(discriminant);
            float tLeft = -dotProduct - sqrtDisc;
            float tRight = -dotProduct + sqrtDisc;

            for (int i = 0; i < lineNo; i++)
            {
                CrowdOrcaLine other = lines[lineBase + i];
                float denominator = line.Direction.x * other.Direction.y - line.Direction.y * other.Direction.x;
                float numerator = other.Direction.x * (line.Point.y - other.Point.y) -
                                  other.Direction.y * (line.Point.x - other.Point.x);
                if (math.abs(denominator) <= Eps)
                {
                    if (numerator < 0f) return false;
                    continue;
                }
                float t = numerator / denominator;
                if (denominator >= 0f) tRight = math.min(tRight, t);
                else tLeft = math.max(tLeft, t);
                if (tLeft > tRight) return false;
            }

            if (directionOpt)
            {
                result = math.dot(optVelocity, line.Direction) > 0f
                    ? line.Point + line.Direction * tRight
                    : line.Point + line.Direction * tLeft;
            }
            else
            {
                float t = math.dot(line.Direction, optVelocity - line.Point);
                if (t < tLeft) result = line.Point + line.Direction * tLeft;
                else if (t > tRight) result = line.Point + line.Direction * tRight;
                else result = line.Point + line.Direction * t;
            }
            return true;
        }

        private static void LinearProgram3(NativeArray<CrowdOrcaLine> lines, int lineBase, int lineCount,
            int numObstLines, int beginLine, float radius, NativeArray<CrowdOrcaLine> proj, int projBase,
            ref float2 result)
        {
            float distance = 0f;
            for (int i = beginLine; i < lineCount; i++)
            {
                CrowdOrcaLine lineI = lines[lineBase + i];
                float crossI = lineI.Direction.x * (lineI.Point.y - result.y) -
                               lineI.Direction.y * (lineI.Point.x - result.x);
                if (crossI <= distance) continue;

                int projCount = 0;
                for (int ii = 0; ii < numObstLines; ii++)
                    proj[projBase + projCount++] = lines[lineBase + ii];

                for (int j = numObstLines; j < i; j++)
                {
                    CrowdOrcaLine lineJ = lines[lineBase + j];
                    CrowdOrcaLine projected;
                    float det = lineI.Direction.x * lineJ.Direction.y - lineI.Direction.y * lineJ.Direction.x;
                    if (math.abs(det) <= Eps)
                    {
                        if (math.dot(lineI.Direction, lineJ.Direction) > 0f) continue;
                        projected.Point = (lineI.Point + lineJ.Point) * 0.5f;
                    }
                    else
                    {
                        float num = lineJ.Direction.x * (lineI.Point.y - lineJ.Point.y) -
                                    lineJ.Direction.y * (lineI.Point.x - lineJ.Point.x);
                        projected.Point = lineI.Point + lineI.Direction * (num / det);
                    }
                    projected.Direction = math.normalizesafe(lineJ.Direction - lineI.Direction);
                    proj[projBase + projCount++] = projected;
                }

                float2 temp = result;
                float2 opt = new float2(-lineI.Direction.y, lineI.Direction.x);
                if (LinearProgram2(proj, projBase, projCount, radius, opt, true, out result) < projCount)
                    result = temp;

                distance = lineI.Direction.x * (lineI.Point.y - result.y) -
                           lineI.Direction.y * (lineI.Point.x - result.x);
            }
        }
    }
}