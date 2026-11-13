using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    /// <summary>
    /// Owns all native state and the per-tick Burst job graph. The managed layer only
    /// schedules/completes jobs and swaps goal state; it never touches an agent object.
    /// </summary>
    public sealed class CrowdNavSimulation : IDisposable
    {
        public readonly CrowdGridData GridData;
        public readonly CrowdGrid Grid;
        public readonly CrowdConfig Config;
        public readonly CrowdAgentStore Agents;
        public readonly CrowdSpatialHash Hash;
        public readonly CrowdFlowFieldData Flow;
        public readonly CrowdGoalData Goal;
        public NativeArray<int> Stats;

        public float2 GoalCenter;
        public float GoalRadius;
        public bool HasActiveGoal;
        public float Time;
        public long TickCount;
        public bool FlowReady;
        public int Capacity => Agents.Count;

        private NativeArray<CrowdOrcaLine> _lines;
        private NativeArray<CrowdOrcaLine> _projLines;
        private NativeArray<int> _neighbors;
        private NativeArray<int> _order;
        private NativeArray<int> _tempOrder;
        private NativeArray<ulong> _sortKeys;
        private NativeArray<ulong> _tempKeys;
        private NativeArray<int> _sortCounts;
        private NativeArray<float2> _newPositions;
        private NativeArray<float2> _newVelocities;
        private NativeArray<byte> _hasMoved;
        private NativeArray<byte> _finalized;
        private NativeArray<float> _settleState;

        private JobHandle _flowHandle;
        private bool _flowPending;
        private int _goalCell = -1;
        private int _count;

        public CrowdNavSimulation(CrowdGridData grid, CrowdConfig config, NativeArray<float2> spawns,
            float2 goal, bool startGoal = true)
        {
            GridData = grid;
            Grid = grid.Grid;
            Config = config;
            _count = spawns.Length;
            int stride = math.max(64, math.ceilpow2(grid.Grid.Width + grid.Grid.Height));
            Agents = new CrowdAgentStore(_count, stride, config);
            Hash = new CrowdSpatialHash(grid.Grid.OriginX, grid.Grid.OriginZ,
                grid.Grid.OriginX + grid.Grid.Width * grid.Grid.CellSize,
                grid.Grid.OriginZ + grid.Grid.Height * grid.Grid.CellSize, 1.25f, _count);
            Flow = new CrowdFlowFieldData(grid.Grid.CellCount);
            Goal = new CrowdGoalData(_count);

            _lines = new NativeArray<CrowdOrcaLine>(_count * math.max(1, config.MaxNeighbors), Allocator.Persistent);
            _projLines = new NativeArray<CrowdOrcaLine>(_count * (2 * math.max(1, config.MaxNeighbors) + 2), Allocator.Persistent);
            _neighbors = new NativeArray<int>(_count * math.max(1, config.MaxNeighbors), Allocator.Persistent);
            _order = new NativeArray<int>(_count, Allocator.Persistent);
            _tempOrder = new NativeArray<int>(_count, Allocator.Persistent);
            _sortKeys = new NativeArray<ulong>(_count, Allocator.Persistent);
            _tempKeys = new NativeArray<ulong>(_count, Allocator.Persistent);
            _sortCounts = new NativeArray<int>(256, Allocator.Persistent);
            _newPositions = new NativeArray<float2>(_count, Allocator.Persistent);
            _newVelocities = new NativeArray<float2>(_count, Allocator.Persistent);
            _hasMoved = new NativeArray<byte>(_count, Allocator.Persistent);
            _finalized = new NativeArray<byte>(1, Allocator.Persistent);
            _settleState = new NativeArray<float>(4, Allocator.Persistent);
            Stats = new NativeArray<int>(CrowdStatsIndex.Count, Allocator.Persistent);

            Agents.SetAgents(spawns, goal);
            if (startGoal) SetGoal(goal);
        }

        public void SetGoal(float2 goal)
        {
            if (_flowPending)
            {
                _flowHandle.Complete();
                _flowPending = false;
            }

            Goal.Build(Grid, goal, _count, Config.SlotSpacing, Config.MaxGoalRings,
                Config.GoalCenterAttraction);
            if (!Config.GoalCenterAttraction && Goal.SlotCount < _count)
                throw new InvalidOperationException(
                    $"Could only fit {Goal.SlotCount}/{_count} goal slots; enable GoalCenterAttraction or a queue layout.");

            GoalCenter = Goal.Center;
            GoalRadius = Goal.Radius + Config.GoalFootprintPadding;
            HasActiveGoal = true;
            _finalized[0] = 0;
            _settleState[0] = -1f;
            _settleState[1] = -1f;
            _settleState[2] = -1f;
            Agents.MarkAllReplan();
            Goal.Assign(Grid, Agents, _count);

            _goalCell = Grid.FindNearestWalkableCell(GoalCenter);
            FlowReady = false;
            _goalCell = Grid.FindNearestWalkableCell(GoalCenter);
            var flowJob = new CrowdBuildFlowFieldJob
            {
                Grid = Grid,
                Dist = Flow.Dist,
                Next = Flow.Next,
                HeapItems = Flow.HeapItems,
                HeapKeys = Flow.HeapKeys,
                HeapPositions = Flow.HeapPositions,
                GoalCell = _goalCell
            };
            _flowHandle = flowJob.Schedule();
            _flowPending = true;
        }

        /// <summary>
        /// Force the async goal flow field to finish. Useful for deterministic tests/replays;
        /// the normal path keeps it asynchronous so a click never blocks the main thread.
        /// </summary>
        public void CompleteFlowNow()
        {
            if (_flowPending)
            {
                _flowHandle.Complete();
                _flowPending = false;
                FlowReady = true;
            }
        }

        public void Tick(float dt)
        {
            if (!HasActiveGoal || dt <= 0f) return;
            for (int i = 0; i < CrowdStatsIndex.Count; i++) Stats[i] = 0;

            if (_flowPending && _flowHandle.IsCompleted)
            {
                _flowHandle.Complete();
                _flowPending = false;
                FlowReady = true;
            }

            var hashJob = new CrowdHashBuildJob
            {
                Position = Agents.Position,
                PrevPosition = Agents.PrevPosition,
                CellStart = Hash.CellStart,
                Cursor = Hash.Cursor,
                Sorted = Hash.Sorted,
                AgentCell = Hash.AgentCell,
                Count = _count,
                Cols = Hash.Cols,
                Rows = Hash.Rows,
                CellSize = Hash.CellSize,
                MinX = Hash.MinX,
                MinZ = Hash.MinZ
            };
            JobHandle hHash = hashJob.Schedule();

            JobHandle hPlan = hHash;
            if (FlowReady)
            {
                var planJob = new CrowdPlanPathsJob
                {
                    Grid = Grid,
                    Dist = Flow.Dist,
                    Next = Flow.Next,
                    Position = Agents.Position,
                    Flags = Agents.Flags,
                    WaypointIndex = Agents.WaypointIndex,
                    PathCount = Agents.PathCount,
                    Path = Agents.Path,
                    PathStride = Agents.PathStride,
                    GoalCell = _goalCell
                };
                hPlan = planJob.Schedule(_count, 64, hHash);
            }

            var steerJob = new CrowdSteerJob
            {
                Grid = Grid,
                Position = Agents.Position,
                Velocity = Agents.Velocity,
                SlotPosition = Agents.SlotPosition,
                Path = Agents.Path,
                MaxSpeed = Agents.MaxSpeed,
                SpeedScale = Agents.SpeedScale,
                BlockedTime = Agents.BlockedTime,
                DistanceToSlot = Agents.DistanceToSlot,
                PathStart = Agents.PathStart,
                PathCount = Agents.PathCount,
                State = Agents.State,
                PreferredVelocity = Agents.PreferredVelocity,
                DesiredSpeed = Agents.DesiredSpeed,
                ReplanCooldown = Agents.ReplanCooldown,
                WaypointIndex = Agents.WaypointIndex,
                Flags = Agents.Flags,
                TimeStep = dt,
                ArrivalRadius = Config.ArrivalRadius,
                GoalMinSpeed = Config.GoalMinSpeed,
                GoalZoneMaxSpeed = Config.GoalZoneMaxSpeed,
                GoalFootprintRadius = GoalRadius,
                HasActiveGoal = HasActiveGoal,
                GoalCenter = GoalCenter
            };
            JobHandle hSteer = steerJob.Schedule(_count, 64, hPlan);

            var orcaJob = new CrowdOrcaJob
            {
                Grid = Grid,
                Hash = Hash.AsRef(),
                Position = Agents.Position,
                Velocity = Agents.Velocity,
                PreferredVelocity = Agents.PreferredVelocity,
                SlotPosition = Agents.SlotPosition,
                Radius = Agents.Radius,
                MaxSpeed = Agents.MaxSpeed,
                SpeedScale = Agents.SpeedScale,
                DistanceToSlot = Agents.DistanceToSlot,
                BlockedTime = Agents.BlockedTime,
                State = Agents.State,
                ChosenVelocity = Agents.ChosenVelocity,
                AvoidSign = Agents.AvoidSign,
                AvoidSignTimer = Agents.AvoidSignTimer,
                NeighborBuf = _neighbors,
                Lines = _lines,
                ProjLines = _projLines,
                MaxNeighbors = Config.MaxNeighbors,
                NeighborDistance = Config.NeighborDistance,
                AvoidanceHorizon = Config.AvoidanceHorizon,
                OrcaTimeHorizon = Config.OrcaTimeHorizon,
                OrcaSafetyMargin = Config.OrcaSafetyMargin,
                SafetyMargin = Config.SafetyMargin,
                MaxAcceleration = Config.MaxAcceleration,
                TimeStep = dt,
                UseOrca = Config.UseOrca,
                KeepInsideGoal = Config.KeepInsideGoalOnceEntered,
                GoalCenter = GoalCenter,
                GoalRadius = GoalRadius
            };
            JobHandle hOrca = orcaJob.Schedule(_count, 32,
                JobHandle.CombineDependencies(hHash, hSteer));

            var commitJob = new CrowdCommitJob
            {
                Grid = Grid,
                ChosenVelocity = Agents.ChosenVelocity,
                SlotPosition = Agents.SlotPosition,
                DistanceToSlot = Agents.DistanceToSlot,
                Radius = Agents.Radius,
                State = Agents.State,
                Flags = Agents.Flags,
                Position = Agents.Position,
                Velocity = Agents.Velocity,
                DistanceTravelled = Agents.DistanceTravelled,
                NewPositions = _newPositions,
                NewVelocities = _newVelocities,
                HasMoved = _hasMoved,
                Order = _order,
                TempOrder = _tempOrder,
                SortKeys = _sortKeys,
                TempKeys = _tempKeys,
                SortCounts = _sortCounts,
                HashCellStart = Hash.CellStart,
                HashCursor = Hash.Cursor,
                HashSorted = Hash.Sorted,
                HashAgentCell = Hash.AgentCell,
                Stats = Stats,
                Count = _count,
                Cols = Hash.Cols,
                Rows = Hash.Rows,
                CellSize = Hash.CellSize,
                MinX = Hash.MinX,
                MinZ = Hash.MinZ,
                MaxSpeed = Config.MaxSpeed,
                RequiredSeparation = Config.RequiredSeparation,
                MaxAcceleration = Config.MaxAcceleration,
                TimeStep = dt,
                PredictedCommit = Config.PredictedCommit,
                SimultaneousCommit = Config.SimultaneousCommit,
                KeepInsideGoal = Config.KeepInsideGoalOnceEntered,
                GoalCenter = GoalCenter,
                GoalRadius = GoalRadius
            };
            JobHandle hCommit = commitJob.Schedule(hOrca);

            var postJob = new CrowdPostTickJob
            {
                Position = Agents.Position,
                Velocity = Agents.Velocity,
                PreferredVelocity = Agents.PreferredVelocity,
                ChosenVelocity = Agents.ChosenVelocity,
                SlotPosition = Agents.SlotPosition,
                YieldTarget = Agents.YieldTarget,
                DesiredSpeed = Agents.DesiredSpeed,
                DistanceToSlot = Agents.DistanceToSlot,
                BlockedTime = Agents.BlockedTime,
                StuckTime = Agents.StuckTime,
                AvoidSignTimer = Agents.AvoidSignTimer,
                AvoidSign = Agents.AvoidSign,
                Facing = Agents.Facing,
                LastSpeed = Agents.LastSpeed,
                ArrivalTime = Agents.ArrivalTime,
                YieldTimer = Agents.YieldTimer,
                SpeedScale = Agents.SpeedScale,
                State = Agents.State,
                Flags = Agents.Flags,
                WaypointIndex = Agents.WaypointIndex,
                PathCount = Agents.PathCount,
                TimeStep = dt,
                Time = Time,
                ArrivalRadius = Config.ArrivalRadius,
                GoalCenterAttraction = Config.GoalCenterAttraction,
                GoalCenter = GoalCenter
            };
            JobHandle hPost = postJob.Schedule(_count, 64, hCommit);

            var settleJob = new CrowdSettlementJob
            {
                Position = Agents.Position,
                Velocity = Agents.Velocity,
                PreferredVelocity = Agents.PreferredVelocity,
                ChosenVelocity = Agents.ChosenVelocity,
                DesiredSpeed = Agents.DesiredSpeed,
                SpeedScale = Agents.SpeedScale,
                ArrivalTime = Agents.ArrivalTime,
                State = Agents.State,
                Flags = Agents.Flags,
                Finalized = _finalized,
                SettleState = _settleState,
                Count = _count,
                Time = Time,
                GoalCenter = GoalCenter,
                GoalRadius = GoalRadius,
                Dwell = Config.GoalSettleDwell,
                CenterAttraction = Config.GoalCenterAttraction,
                AllowCompact = Config.AllowCompactSettlement,
                MinUnits = Config.CompactSettlementMinUnits,
                HasActiveGoal = HasActiveGoal,
                Stats = Stats
            };
            JobHandle hSettle = settleJob.Schedule(hPost);
            hSettle.Complete();

            Time += dt;
            TickCount++;
        }

        public int ArrivedCount()
        {
            int n = 0;
            for (int i = 0; i < _count; i++)
                if (Agents.State[i] == (byte)CrowdAgentState.Arrived) n++;
            return n;
        }

        public bool IsFlowPending => _flowPending;

        public void Dispose()
        {
            if (_flowPending)
            {
                _flowHandle.Complete();
                _flowPending = false;
            }
            Agents.Dispose();
            Hash.Dispose();
            Flow.Dispose();
            Goal.Dispose();
            _lines.Dispose();
            _projLines.Dispose();
            _neighbors.Dispose();
            _order.Dispose();
            _tempOrder.Dispose();
            _sortKeys.Dispose();
            _tempKeys.Dispose();
            _sortCounts.Dispose();
            _newPositions.Dispose();
            _newVelocities.Dispose();
            _hasMoved.Dispose();
            _finalized.Dispose();
            _settleState.Dispose();
            Stats.Dispose();
        }
    }
}