using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdSteerJob : IJobParallelFor
    {
        [ReadOnly] public CrowdGrid Grid;
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float2> SlotPosition;
        [ReadOnly] public NativeArray<float2> Path;
        [ReadOnly] public NativeArray<float> MaxSpeed;
        [ReadOnly] public NativeArray<float> SpeedScale;
        [ReadOnly] public NativeArray<float> BlockedTime;
        [ReadOnly] public NativeArray<float> DistanceToSlot;
        [ReadOnly] public NativeArray<int> PathStart;
        [ReadOnly] public NativeArray<int> PathCount;
        [ReadOnly] public NativeArray<byte> State;

        public NativeArray<float2> PreferredVelocity;
        public NativeArray<float> DesiredSpeed;
        public NativeArray<float> ReplanCooldown;
        public NativeArray<int> WaypointIndex;
        public NativeArray<byte> Flags;

        public float TimeStep;
        public float ArrivalRadius;
        public float GoalMinSpeed;
        public float GoalZoneMaxSpeed;
        public float GoalFootprintRadius;
        public bool HasActiveGoal;
        public float2 GoalCenter;

        public void Execute(int i)
        {
            byte state = State[i];
            if (state == (byte)CrowdAgentState.Arrived || state == (byte)CrowdAgentState.Unreachable)
            {
                PreferredVelocity[i] = float2.zero;
                DesiredSpeed[i] = 0f;
                return;
            }

            ReplanCooldown[i] = math.max(0f, ReplanCooldown[i] - TimeStep);
            if (BlockedTime[i] > 0.55f && DistanceToSlot[i] > ArrivalRadius * 2f)
                RequestReplan(i, 1.4f);

            int count = PathCount[i];
            if ((Flags[i] & CrowdAgentFlags.HasPath) == 0 || count == 0)
            {
                PreferredVelocity[i] = float2.zero;
                DesiredSpeed[i] = 0f;
                return;
            }

            AdvanceWaypoints(i, count);

            int idx = WaypointIndex[i];
            float2 target;
            bool final;
            if (idx < count)
            {
                target = Path[PathStart[i] + idx];
                final = idx >= count - 1;
            }
            else
            {
                target = SlotPosition[i];
                final = true;
                if (!Grid.IsSegmentWalkable(Position[i], target))
                {
                    RequestReplan(i, 1.5f);
                    PreferredVelocity[i] = float2.zero;
                    DesiredSpeed[i] = 0f;
                    return;
                }
            }

            float2 to = target - Position[i];
            float dist = math.length(to);
            if (dist < 1e-5f)
            {
                PreferredVelocity[i] = float2.zero;
                DesiredSpeed[i] = 0f;
                return;
            }

            float speed = MaxSpeed[i];
            if (final)
            {
                float near = math.max(GoalMinSpeed, dist * 2.2f);
                speed = math.min(speed, near);
            }

            float2 vel = Velocity[i];
            if (math.lengthsq(vel) > 0.04f)
            {
                float currentAngle = math.atan2(vel.y, vel.x);
                float targetAngle = math.atan2(to.y, to.x);
                float turn = math.abs(AngleDelta(currentAngle, targetAngle));
                if (!final)
                {
                    if (turn > 1.9f) speed *= 0.35f;
                    else if (turn > 1.0f) speed *= 0.65f;
                }
            }

            if (GoalZoneMaxSpeed > 0f && HasActiveGoal && DistanceToSlot[i] > 1.2f &&
                math.distancesq(Position[i], GoalCenter) <= GoalFootprintRadius * GoalFootprintRadius)
            {
                speed = math.min(speed, GoalZoneMaxSpeed);
            }

            speed *= SpeedScale[i];
            DesiredSpeed[i] = speed;
            PreferredVelocity[i] = to * (speed / dist);
        }

        private void AdvanceWaypoints(int i, int count)
        {
            int idx = WaypointIndex[i];
            if (idx <= 0) idx = 1;
            int baseIndex = PathStart[i];
            while (idx < count)
            {
                float2 wp = Path[baseIndex + idx];
                float d = math.distance(Position[i], wp);
                bool last = idx == count - 1;
                float threshold = last ? ArrivalRadius * 0.9f : 0.55f;
                if (d <= threshold)
                {
                    idx++;
                    continue;
                }

                if (!last && math.lengthsq(Velocity[i]) > 0.04f)
                {
                    float2 to = wp - Position[i];
                    float closing = math.dot(to, Velocity[i]);
                    bool passed = false;
                    if (idx > 0)
                    {
                        float2 incoming = wp - Path[baseIndex + idx - 1];
                        if (math.lengthsq(incoming) > 1e-6f)
                            passed = math.dot(Position[i] - wp, math.normalizesafe(incoming)) > 0.10f;
                    }

                    if (closing < 0f && d < 2.8f)
                    {
                        float2 next = Path[baseIndex + idx + 1];
                        if (Grid.IsSegmentWalkable(Position[i], next))
                        {
                            idx++;
                            continue;
                        }
                    }

                    if (passed && d < 4.0f)
                    {
                        float2 next = Path[baseIndex + idx + 1];
                        if (Grid.IsSegmentWalkable(Position[i], next))
                        {
                            idx++;
                            continue;
                        }
                    }

                    if ((closing < 0f && d > 1.2f) || passed)
                    {
                        float goalKeepOut = GoalFootprintRadius + 1.0f;
                        if (!HasActiveGoal ||
                            math.distancesq(Position[i], GoalCenter) > goalKeepOut * goalKeepOut)
                            RequestReplan(i, 1.5f);
                    }
                }
                break;
            }
            WaypointIndex[i] = math.min(idx, count);
        }

        private void RequestReplan(int i, float cooldown)
        {
            if ((Flags[i] & CrowdAgentFlags.HasPath) == 0) return;
            if ((Flags[i] & CrowdAgentFlags.ReplanRequested) != 0) return;
            if (ReplanCooldown[i] > 0f) return;
            Flags[i] |= CrowdAgentFlags.ReplanRequested;
            ReplanCooldown[i] = cooldown;
        }

        private static float AngleDelta(float a, float b)
        {
            float d = b - a;
            float twoPi = 6.28318530718f;
            while (d > math.PI) d -= twoPi;
            while (d < -math.PI) d += twoPi;
            return d;
        }
    }
}