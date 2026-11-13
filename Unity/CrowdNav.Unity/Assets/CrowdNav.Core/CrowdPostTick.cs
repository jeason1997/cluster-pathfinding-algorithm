using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdPostTickJob : IJobParallelFor
    {
        public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;
        public NativeArray<float2> PreferredVelocity;
        public NativeArray<float2> ChosenVelocity;
        [ReadOnly] public NativeArray<float2> SlotPosition;
        [ReadOnly] public NativeArray<float2> YieldTarget;
        public NativeArray<float> DesiredSpeed;
        public NativeArray<float> DistanceToSlot;
        public NativeArray<float> BlockedTime;
        public NativeArray<float> StuckTime;
        public NativeArray<float> AvoidSignTimer;
        public NativeArray<float> AvoidSign;
        public NativeArray<float> Facing;
        public NativeArray<float> LastSpeed;
        public NativeArray<float> ArrivalTime;
        public NativeArray<float> YieldTimer;
        public NativeArray<float> SpeedScale;
        public NativeArray<byte> State;
        public NativeArray<byte> Flags;
        public NativeArray<int> WaypointIndex;
        public NativeArray<int> PathCount;

        public float TimeStep;
        public float Time;
        public float ArrivalRadius;
        public bool GoalCenterAttraction;
        public float2 GoalCenter;

        public void Execute(int i)
        {
            float2 pos = Position[i];
            DistanceToSlot[i] = math.distance(pos, SlotPosition[i]);
            byte state = State[i];

            if (state == (byte)CrowdAgentState.Yielding)
            {
                YieldTimer[i] -= TimeStep;
                if (YieldTimer[i] <= 0f)
                {
                    State[i] = (byte)CrowdAgentState.Moving;
                    SpeedScale[i] = 0.85f;
                    Flags[i] = (byte)((Flags[i] | CrowdAgentFlags.ReturningToSlot |
                                       CrowdAgentFlags.ReplanRequested) & ~CrowdAgentFlags.HasPath);
                    YieldTimer[i] = 0f;
                    BlockedTime[i] = 0f;
                }
            }
            else if (state == (byte)CrowdAgentState.Moving)
            {
                bool atSlot = GoalCenterAttraction
                    ? math.distancesq(pos, GoalCenter) <= 1.0f
                    : DistanceToSlot[i] <= ArrivalRadius;
                if (atSlot)
                {
                    State[i] = (byte)CrowdAgentState.Arrived;
                    ArrivalTime[i] = Time;
                    Velocity[i] = float2.zero;
                    ChosenVelocity[i] = float2.zero;
                    PreferredVelocity[i] = float2.zero;
                    DesiredSpeed[i] = 0f;
                    Flags[i] = (byte)(Flags[i] & ~CrowdAgentFlags.HasPath);
                    SpeedScale[i] = 1f;
                }
                else
                {
                    float actual = math.length(Velocity[i]);
                    float desired = DesiredSpeed[i];
                    if (desired > 0.3f && actual < 0.10f)
                        BlockedTime[i] += TimeStep;
                    else
                        BlockedTime[i] = math.max(0f, BlockedTime[i] - TimeStep * 2f);

                    if (desired > 0.3f && actual < 0.25f * desired)
                        StuckTime[i] += TimeStep;
                    else
                        StuckTime[i] = math.max(0f, StuckTime[i] - TimeStep * 1.5f);

                    AvoidSignTimer[i] = math.max(0f, AvoidSignTimer[i] - TimeStep);
                    if (AvoidSignTimer[i] <= 0f) AvoidSign[i] = 0f;
                }
            }

            float targetFacing = Facing[i];
            if (math.lengthsq(Velocity[i]) > 0.01f)
            {
                targetFacing = math.atan2(Velocity[i].y, Velocity[i].x);
            }
            else if (State[i] == (byte)CrowdAgentState.Yielding)
            {
                float2 to = YieldTarget[i] - Position[i];
                if (math.lengthsq(to) > 0.01f) targetFacing = math.atan2(to.y, to.x);
            }
            else if (State[i] != (byte)CrowdAgentState.Arrived)
            {
                float2 to = SlotPosition[i] - Position[i];
                if (math.lengthsq(to) > 0.01f) targetFacing = math.atan2(to.y, to.x);
            }

            float maxTurn = 10f * TimeStep;
            Facing[i] += math.clamp(AngleDelta(Facing[i], targetFacing), -maxTurn, maxTurn);
            LastSpeed[i] = math.length(Velocity[i]);
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

    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdSettlementJob : IJob
    {
        public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;
        public NativeArray<float2> PreferredVelocity;
        public NativeArray<float2> ChosenVelocity;
        public NativeArray<float> DesiredSpeed;
        public NativeArray<float> SpeedScale;
        public NativeArray<float> ArrivalTime;
        public NativeArray<byte> State;
        public NativeArray<byte> Flags;
        public NativeArray<byte> Finalized;
        public NativeArray<float> SettleState;

        public int Count;
        public float Time;
        public float2 GoalCenter;
        public float GoalRadius;
        public float Dwell;
        public bool CenterAttraction;
        public bool AllowCompact;
        public int MinUnits;
        public bool HasActiveGoal;
        public NativeArray<int> Stats;

        public void Execute()
        {
            if (Finalized[0] != 0 || !HasActiveGoal) return;
            if (!AllowCompact || Count < MinUnits)
            {
                CountArrived();
                return;
            }

            int arrived = 0;
            for (int i = 0; i < Count; i++)
                if (State[i] == (byte)CrowdAgentState.Arrived) arrived++;
            Stats[CrowdStatsIndex.Arrived] = arrived;
            if (arrived == Count)
            {
                Finalized[0] = 1;
                Stats[CrowdStatsIndex.Settled] = 1;
                return;
            }

            if (CenterAttraction)
            {
                bool reached = false;
                float sum = 0f;
                for (int i = 0; i < Count; i++)
                {
                    float d = math.distance(Position[i], GoalCenter);
                    sum += d;
                    if (d <= 1.2f) reached = true;
                }
                if (!reached) return;
                if (SettleState[0] < 0f) { SettleState[0] = sum; SettleState[1] = Time; }
                if (sum < SettleState[0] - 0.04f)
                {
                    SettleState[0] = sum;
                    SettleState[1] = Time;
                }
                if (Time - SettleState[1] < Dwell) return;
            }
            else
            {
                float rSq = GoalRadius * GoalRadius;
                bool allInside = true;
                for (int i = 0; i < Count; i++)
                {
                    if (State[i] == (byte)CrowdAgentState.Unreachable)
                    {
                        allInside = false;
                        break;
                    }
                    if (math.distancesq(Position[i], GoalCenter) > rSq)
                    {
                        allInside = false;
                        break;
                    }
                }
                if (!allInside)
                {
                    SettleState[2] = -1f;
                    return;
                }
                if (SettleState[2] < 0f) SettleState[2] = Time;
                if (Time - SettleState[2] < Dwell) return;
            }

            for (int i = 0; i < Count; i++)
            {
                if (State[i] == (byte)CrowdAgentState.Arrived) continue;
                State[i] = (byte)CrowdAgentState.Arrived;
                ArrivalTime[i] = Time;
                Velocity[i] = float2.zero;
                ChosenVelocity[i] = float2.zero;
                PreferredVelocity[i] = float2.zero;
                DesiredSpeed[i] = 0f;
                SpeedScale[i] = 1f;
                Flags[i] = (byte)(Flags[i] & ~CrowdAgentFlags.HasPath);
            }
            Finalized[0] = 1;
            Stats[CrowdStatsIndex.Settled] = 1;
            Stats[CrowdStatsIndex.Arrived] = Count;
        }

        private void CountArrived()
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
                if (State[i] == (byte)CrowdAgentState.Arrived) n++;
            Stats[CrowdStatsIndex.Arrived] = n;
        }
    }
}