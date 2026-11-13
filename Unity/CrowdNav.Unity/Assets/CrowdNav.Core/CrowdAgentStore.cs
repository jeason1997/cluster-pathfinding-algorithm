using System;
using Unity.Collections;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    public enum CrowdAgentState : byte
    {
        Moving = 0,
        Arrived = 1,
        Unreachable = 2,
        Yielding = 3
    }

    public static class CrowdAgentFlags
    {
        public const byte HasPath = 1 << 0;
        public const byte ReplanRequested = 1 << 1;
        public const byte Unreachable = 1 << 2;
        public const byte ReturningToSlot = 1 << 3;
        public const byte PathPlanQueued = 1 << 4;
    }

    public struct CrowdConfig
    {
        public float AgentRadius;
        public float MaxSpeed;
        public float MaxAcceleration;
        public float ArrivalRadius;
        public float NeighborDistance;
        public int MaxNeighbors;
        public float AvoidanceHorizon;
        public bool UseOrca;
        public float OrcaTimeHorizon;
        public float OrcaSafetyMargin;
        public bool PredictedCommit;
        public bool SimultaneousCommit;
        public float SafetyMargin;
        public float SlotSpacing;
        public float GoalSlowdownRadius;
        public float GoalMinSpeed;
        public int MaxGoalRings;
        public float CourtesyYieldSpeed;
        public bool EnableCourtesyYield;
        public float GoalFootprintPadding;
        public float GoalSettleDwell;
        public bool AllowCompactSettlement;
        public int CompactSettlementMinUnits;
        public bool DirectionalGoalLayout;
        public bool QueueNarrowGoal;
        public bool GoalCenterAttraction;
        public bool KeepInsideGoalOnceEntered;
        public float GoalZoneMaxSpeed;

        public float RequiredSeparation => 2f * AgentRadius + SafetyMargin;

        public static CrowdConfig Default => new CrowdConfig
        {
            AgentRadius = 0.35f,
            MaxSpeed = 3.1f,
            MaxAcceleration = 15f,
            ArrivalRadius = 0.12f,
            NeighborDistance = 2.0f,
            MaxNeighbors = 14,
            AvoidanceHorizon = 0.55f,
            UseOrca = true,
            OrcaTimeHorizon = 1.5f,
            OrcaSafetyMargin = 0.10f,
            PredictedCommit = true,
            SimultaneousCommit = false,
            SafetyMargin = 0.02f,
            SlotSpacing = 0.8f,
            GoalSlowdownRadius = 1.8f,
            GoalMinSpeed = 0.38f,
            MaxGoalRings = 80,
            CourtesyYieldSpeed = 0.65f,
            EnableCourtesyYield = true,
            GoalFootprintPadding = 1.0f,
            GoalSettleDwell = 4.0f,
            AllowCompactSettlement = true,
            CompactSettlementMinUnits = 20,
            DirectionalGoalLayout = false,
            QueueNarrowGoal = false,
            GoalCenterAttraction = true,
            KeepInsideGoalOnceEntered = false,
            GoalZoneMaxSpeed = 0f
        };
    }

    public static class CrowdStatsIndex
    {
        public const int HardClamps = 0;
        public const int OverlapRollbacks = 1;
        public const int ResidualPasses = 2;
        public const int PathsPlanned = 3;
        public const int PathsFailed = 4;
        public const int CourtesyYields = 5;
        public const int Arrived = 6;
        public const int Settled = 7;
        public const int Count = 8;
    }

    /// <summary>
    /// Structure-of-arrays agent state. Every hot field is its own NativeArray so jobs can
    /// declare exactly what they read and write; no per-agent managed object exists.
    /// </summary>
    public sealed class CrowdAgentStore : IDisposable
    {
        public int Capacity { get; }
        public int Count { get; private set; }
        public int PathStride { get; }

        public NativeArray<float2> Position;
        public NativeArray<float2> PrevPosition;
        public NativeArray<float2> Velocity;
        public NativeArray<float2> PreferredVelocity;
        public NativeArray<float2> ChosenVelocity;
        public NativeArray<float2> SlotPosition;
        public NativeArray<float2> YieldTarget;
        public NativeArray<float2> Path;

        public NativeArray<float> Radius;
        public NativeArray<float> MaxSpeed;
        public NativeArray<float> SpeedScale;
        public NativeArray<float> DesiredSpeed;
        public NativeArray<float> DistanceToSlot;
        public NativeArray<float> BlockedTime;
        public NativeArray<float> StuckTime;
        public NativeArray<float> ReplanCooldown;
        public NativeArray<float> AvoidSignTimer;
        public NativeArray<float> AvoidSign;
        public NativeArray<float> Facing;
        public NativeArray<float> LastSpeed;
        public NativeArray<float> ArrivalTime;
        public NativeArray<float> YieldTimer;
        public NativeArray<float> LastYieldTime;
        public NativeArray<float> LastYieldRequestTime;
        public NativeArray<float> TotalPathLength;
        public NativeArray<float> DistanceTravelled;

        public NativeArray<int> SlotIndex;
        public NativeArray<int> WaypointIndex;
        public NativeArray<int> PathStart;
        public NativeArray<int> PathCount;
        public NativeArray<byte> State;
        public NativeArray<byte> Flags;

        public CrowdAgentStore(int capacity, int pathStride, CrowdConfig config)
        {
            Capacity = capacity;
            PathStride = pathStride;
            Position = new NativeArray<float2>(capacity, Allocator.Persistent);
            PrevPosition = new NativeArray<float2>(capacity, Allocator.Persistent);
            Velocity = new NativeArray<float2>(capacity, Allocator.Persistent);
            PreferredVelocity = new NativeArray<float2>(capacity, Allocator.Persistent);
            ChosenVelocity = new NativeArray<float2>(capacity, Allocator.Persistent);
            SlotPosition = new NativeArray<float2>(capacity, Allocator.Persistent);
            YieldTarget = new NativeArray<float2>(capacity, Allocator.Persistent);
            Path = new NativeArray<float2>(capacity * pathStride, Allocator.Persistent);
            Radius = new NativeArray<float>(capacity, Allocator.Persistent);
            MaxSpeed = new NativeArray<float>(capacity, Allocator.Persistent);
            SpeedScale = new NativeArray<float>(capacity, Allocator.Persistent);
            DesiredSpeed = new NativeArray<float>(capacity, Allocator.Persistent);
            DistanceToSlot = new NativeArray<float>(capacity, Allocator.Persistent);
            BlockedTime = new NativeArray<float>(capacity, Allocator.Persistent);
            StuckTime = new NativeArray<float>(capacity, Allocator.Persistent);
            ReplanCooldown = new NativeArray<float>(capacity, Allocator.Persistent);
            AvoidSignTimer = new NativeArray<float>(capacity, Allocator.Persistent);
            AvoidSign = new NativeArray<float>(capacity, Allocator.Persistent);
            Facing = new NativeArray<float>(capacity, Allocator.Persistent);
            LastSpeed = new NativeArray<float>(capacity, Allocator.Persistent);
            ArrivalTime = new NativeArray<float>(capacity, Allocator.Persistent);
            YieldTimer = new NativeArray<float>(capacity, Allocator.Persistent);
            LastYieldTime = new NativeArray<float>(capacity, Allocator.Persistent);
            LastYieldRequestTime = new NativeArray<float>(capacity, Allocator.Persistent);
            TotalPathLength = new NativeArray<float>(capacity, Allocator.Persistent);
            DistanceTravelled = new NativeArray<float>(capacity, Allocator.Persistent);
            SlotIndex = new NativeArray<int>(capacity, Allocator.Persistent);
            WaypointIndex = new NativeArray<int>(capacity, Allocator.Persistent);
            PathStart = new NativeArray<int>(capacity, Allocator.Persistent);
            PathCount = new NativeArray<int>(capacity, Allocator.Persistent);
            State = new NativeArray<byte>(capacity, Allocator.Persistent);
            Flags = new NativeArray<byte>(capacity, Allocator.Persistent);

            for (int i = 0; i < capacity; i++)
            {
                Radius[i] = config.AgentRadius;
                MaxSpeed[i] = config.MaxSpeed;
                SpeedScale[i] = 1f;
                PathStart[i] = i * pathStride;
                SlotIndex[i] = -1;
                State[i] = (byte)CrowdAgentState.Moving;
                LastYieldTime[i] = -100f;
                LastYieldRequestTime[i] = -100f;
            }
        }

        public void SetAgents(NativeArray<float2> spawns, float2 facingTarget)
        {
            Count = math.min(spawns.Length, Capacity);
            for (int i = 0; i < Count; i++)
            {
                float2 p = spawns[i];
                Position[i] = p;
                PrevPosition[i] = p;
                Velocity[i] = float2.zero;
                PreferredVelocity[i] = float2.zero;
                ChosenVelocity[i] = float2.zero;
                SlotPosition[i] = p;
                YieldTarget[i] = p;
                SpeedScale[i] = 1f;
                DesiredSpeed[i] = 0f;
                DistanceToSlot[i] = 0f;
                BlockedTime[i] = 0f;
                StuckTime[i] = 0f;
                ReplanCooldown[i] = 0f;
                AvoidSignTimer[i] = 0f;
                AvoidSign[i] = 0f;
                State[i] = (byte)CrowdAgentState.Moving;
                Flags[i] = 0;
                WaypointIndex[i] = 0;
                PathCount[i] = 0;
                SlotIndex[i] = -1;
                ArrivalTime[i] = -1f;
                YieldTimer[i] = 0f;
                TotalPathLength[i] = 0f;
                DistanceTravelled[i] = 0f;
                float2 to = facingTarget - p;
                Facing[i] = math.lengthsq(to) > 1e-5f ? math.atan2(to.y, to.x) : 0f;
                LastSpeed[i] = 0f;
            }
            for (int i = Count; i < Capacity; i++)
            {
                State[i] = (byte)CrowdAgentState.Arrived;
                Velocity[i] = float2.zero;
                ChosenVelocity[i] = float2.zero;
            }
        }

        public void MarkAllReplan()
        {
            for (int i = 0; i < Count; i++)
            {
                State[i] = (byte)CrowdAgentState.Moving;
                Flags[i] = (byte)((Flags[i] & ~CrowdAgentFlags.HasPath) | CrowdAgentFlags.ReplanRequested);
                PathCount[i] = 0;
                WaypointIndex[i] = 0;
                Velocity[i] = float2.zero;
                PreferredVelocity[i] = float2.zero;
                ChosenVelocity[i] = float2.zero;
                SpeedScale[i] = 1f;
                BlockedTime[i] = 0f;
                StuckTime[i] = 0f;
                ReplanCooldown[i] = 0f;
                AvoidSignTimer[i] = 0f;
                AvoidSign[i] = 0f;
                ArrivalTime[i] = -1f;
                YieldTimer[i] = 0f;
                DistanceTravelled[i] = 0f;
            }
        }

        public int ArrivedCount()
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
                if (State[i] == (byte)CrowdAgentState.Arrived) n++;
            return n;
        }

        public void Dispose()
        {
            Position.Dispose(); PrevPosition.Dispose(); Velocity.Dispose();
            PreferredVelocity.Dispose(); ChosenVelocity.Dispose(); SlotPosition.Dispose();
            YieldTarget.Dispose(); Path.Dispose();
            Radius.Dispose(); MaxSpeed.Dispose(); SpeedScale.Dispose(); DesiredSpeed.Dispose();
            DistanceToSlot.Dispose(); BlockedTime.Dispose(); StuckTime.Dispose();
            ReplanCooldown.Dispose(); AvoidSignTimer.Dispose(); AvoidSign.Dispose();
            Facing.Dispose(); LastSpeed.Dispose(); ArrivalTime.Dispose(); YieldTimer.Dispose();
            LastYieldTime.Dispose(); LastYieldRequestTime.Dispose(); TotalPathLength.Dispose();
            DistanceTravelled.Dispose();
            SlotIndex.Dispose(); WaypointIndex.Dispose(); PathStart.Dispose(); PathCount.Dispose();
            State.Dispose(); Flags.Dispose();
        }
    }
}