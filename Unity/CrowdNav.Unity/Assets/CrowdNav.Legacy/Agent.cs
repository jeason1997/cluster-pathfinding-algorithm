using System;
using System.Collections.Generic;

namespace CrowdNav
{
    public enum AgentState
    {
        Moving = 0,
        Arrived = 1,
        Unreachable = 2,
        Yielding = 3
    }

    public sealed class Agent
    {
        public int Id;
        public Vec2 Position;
        public Vec2 Velocity;
        public Vec2 PreferredVelocity;
        public Vec2 ChosenVelocity;
        public float Radius;
        public float MaxSpeed;
        public AgentState State = AgentState.Moving;
        public int SlotIndex = -1;
        public Vec2 SlotPosition;

        public readonly List<Vec2> Path = new List<Vec2>(64);
        public int WaypointIndex;
        public bool HasPath;

        public float ArrivalTime = -1f;
        public float DistanceToSlot;
        public float DesiredSpeed;
        public float BlockedTime;
        public float StuckTime;
        public float ReplanCooldown;
        public bool ReplanQueued;
        public bool PathPlanQueued;
        public float AvoidSignTimer;
        public float AvoidSign;
        public float Facing;
        public float LastSpeed;
        public float SpeedScale = 1f;
        public bool ReturningToSlot;
        public float YieldTimer;
        public Vec2 YieldTarget;
        public int YieldCount;
        public float LastYieldTime = -100f;
        public float LastYieldRequestTime = -100f;

        // Diagnostics.
        public float TotalPathLength;
        public float DistanceTravelled;
        public int Replans;
        public float MaxStepSeen;
        public float MaxAccelerationSeen;

        public bool IsSettled => State == AgentState.Arrived;

        public void ResetForNewGoal()
        {
            State = AgentState.Moving;
            ArrivalTime = -1f;
            HasPath = false;
            WaypointIndex = 0;
            Path.Clear();
            Velocity = Vec2.Zero;
            ChosenVelocity = Vec2.Zero;
            BlockedTime = 0f;
            StuckTime = 0f;
            ReplanCooldown = 0f;
            ReplanQueued = false;
            PathPlanQueued = false;
            AvoidSignTimer = 0f;
            SpeedScale = 1f;
            ReturningToSlot = false;
            YieldTimer = 0f;
            YieldTarget = Vec2.Zero;
            AvoidSign = 0f;
            DistanceTravelled = 0f;
            TotalPathLength = 0f;
            Replans = 0;
        }
    }

    public sealed class SimulationConfig
    {
        public float AgentRadius = 0.35f;
        public float MaxSpeed = 3.1f;
        public float MaxAcceleration = 15f;
        public float ArrivalRadius = 0.12f;
        public float NeighborDistance = 2.0f;
        public int MaxNeighbors = 14;
        public float AvoidanceHorizon = 0.55f;
        public bool UseOrca = true;
        public float OrcaTimeHorizon = 1.5f;
        public float OrcaSafetyMargin = 0.10f;
        public bool SimultaneousCommit = false;
        public bool PredictedCommit = false;
        public float SafetyMargin = 0.02f;
        public float SlotSpacing = 1.0f;
        public float GoalSlowdownRadius = 1.8f;
        public float GoalMinSpeed = 0.38f;
        public int MaxGoalRings = 40;
        public float CourtesyYieldSpeed = 0.65f;
        public bool EnableCourtesyYield = true;
        public int PathPlanFrameBudget = 2; // time-sliced initial planning; 0 = all at once
        public float GoalFootprintPadding = 1.0f;
        public float GoalSettleDwell = 4.0f;
        public bool AllowCompactSettlement = true;
        public int CompactSettlementMinUnits = 20;
        public bool DirectionalGoalLayout = false;
        public bool FormationPreservingAssignment = true;
        public bool QueueNarrowGoal = true;
        public float QueueGoalMaxRadius = 0f;
        public bool SharedFlowField = false;
        public bool SpaceAwareGoalLayout = false;
        public bool GoalCenterAttraction = false;
        public bool KeepInsideGoalOnceEntered = false;
        public float GoalZoneMaxSpeed = 1.2f;

        public float RequiredSeparation => 2f * AgentRadius + SafetyMargin;

        public SimulationConfig Clone() => (SimulationConfig)MemberwiseClone();
    }
}
