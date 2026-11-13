using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CrowdNav
{
    /// <summary>
    /// Engine-independent crowd simulation:
    ///   A* over a slope-aware height-field grid -> per-agent smoothed path ->
    ///   goal-slot reservation -> predictive local avoidance -> collision-free sequential commit.
    /// Nothing in the tick ever translates another agent to resolve an overlap.
    /// </summary>
    public sealed class CrowdSimulation
    {
        private static readonly float[] DirectionOffsetsDeg =
        {
            0f, 18f, -18f, 36f, -36f, 54f, -54f, 72f, -72f,
            90f, -90f, 115f, -115f, 145f, -145f, 180f
        };

        private static readonly float[] SpeedScales = { 1f, 0.62f, 0.32f };

        private static readonly float[] PredictionFractions = { 0.10f, 0.26f, 0.46f, 0.68f };
        private static readonly float[] YieldAngles = { 0f, 0.55f, -0.55f, 1.05f, -1.05f, 1.55f, -1.55f, 0.9f, -0.9f };

        public readonly NavGrid Grid;
        public readonly SimulationConfig Config;
        public readonly List<Agent> Agents = new List<Agent>();
        public GoalLayout GoalLayout = null!;
        public Vec2 GoalCenter;
        public float Time;
        public long TickCount;
        public int NextAgentId;

        public double LastStepMs;
        public double LastPlanMs;
        public double TotalPlanMs;
        public double PeakPlanFrameMs;
        public int PlanFrames;
        public int PendingPathPlans => _pathQueue.Count;
        public double LastAvoidanceMs;
        public double LastCommitMs;
        public int TotalReplans;
        public int TotalPathsPlanned;
        public int TotalCourtesyYields;
        public int ReflowCount;
        public long HardClampCount;
        public int OverlapRollbacks;
        public bool GroupGoalFinalized;
        public bool HasActiveGoal;
        public Vec2 GoalApproachSide => _goalApproachSide;
        public float GroupGoalFinalizedTime = -1f;
        public float GoalFootprintRadius => _goalMaxRadius + Config.GoalFootprintPadding;

        private readonly PathFinder _pathFinder;
        private readonly FlowField _flowField;
        private bool _flowReady;
        private readonly OrcaSolver _orca = new OrcaSolver();
        private readonly SpatialHash _hash;
        private readonly List<int> _neighbors = new List<int>(32);
        private readonly List<int> _query = new List<int>(64);
        private readonly List<int> _order = new List<int>(256);
        private readonly Comparison<int> _orderComparison;
        private readonly List<int> _processed = new List<int>(256);
        private readonly Queue<Agent> _replanQueue = new Queue<Agent>(64);
        private readonly Queue<Agent> _pathQueue = new Queue<Agent>(256);
        private float _goalMaxRadius;
        private Vec2 _goalApproachSide;
        private float _allInsideSince = -1f;
        private float _settleBestSum = float.MaxValue;
        private float _settleProgressTime = -1f;
        private Vec2[] _newPositions;
        private Vec2[] _newVelocities;
        private bool[] _hasMoved;
        private float _commitDt = 1f / 60f;
        private int _overlapRollbacks;
        private readonly PathResult _pathResult = new PathResult();
        private readonly List<Vec2> _smooth = new List<Vec2>(64);
        private readonly Stopwatch _stopwatch = new Stopwatch();

        public CrowdSimulation(NavGrid grid, SimulationConfig config)
        {
            Grid = grid;
            Config = config;
            _pathFinder = new PathFinder(grid);
            _flowField = new FlowField(grid);
            _hash = new SpatialHash(grid.Map.WorldMinX, grid.Map.WorldMinZ,
                grid.Map.WorldMaxX, grid.Map.WorldMaxZ, 1.25f);
            _newPositions = new Vec2[4096];
            _newVelocities = new Vec2[4096];
            _hasMoved = new bool[4096];
            _orderComparison = CompareCommitOrder;
        }

        private int CompareCommitOrder(int ia, int ib)
        {
            Agent a = Agents[ia];
            Agent b = Agents[ib];
            int c = a.DistanceToSlot.CompareTo(b.DistanceToSlot);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }

        /// <summary>
        /// Creates the crowd at rest, without a goal. An RTS click later calls SetGoal.
        /// </summary>
        public void InitializeIdle(IReadOnlyList<Vec2> spawns)
        {
            Agents.Clear();
            NextAgentId = 0;
            Time = 0f;
            TickCount = 0;
            TotalReplans = 0;
            TotalPathsPlanned = 0;
            TotalPlanMs = 0;
            PeakPlanFrameMs = 0;
            PlanFrames = 0;
            _pathQueue.Clear();
            TotalCourtesyYields = 0;
            ReflowCount = 0;
            GroupGoalFinalized = false;
            GroupGoalFinalizedTime = -1f;
            HasActiveGoal = false;
            _allInsideSince = -1f;
            GoalCenter = Vec2.Zero;
            _goalMaxRadius = 0f;
            GoalLayout = null!;
            for (int i = 0; i < spawns.Count; i++)
                Agents.Add(CreateAgent(spawns[i], Vec2.Zero));

            ValidateNoInitialOverlap();
            Time = 0f;
        }

        public void Initialize(Vec2 goal, IReadOnlyList<Vec2> spawns)
        {
            Agents.Clear();
            NextAgentId = 0;
            Time = 0f;
            TickCount = 0;
            TotalReplans = 0;
            TotalPathsPlanned = 0;
            TotalPlanMs = 0;
            PeakPlanFrameMs = 0;
            PlanFrames = 0;
            _pathQueue.Clear();
            TotalCourtesyYields = 0;
            GroupGoalFinalized = false;
            GroupGoalFinalizedTime = -1f;
            _allInsideSince = -1f;
            for (int i = 0; i < spawns.Count; i++)
                Agents.Add(CreateAgent(spawns[i]));

            ValidateNoInitialOverlap();
            RebuildGoal(goal, true);
            HasActiveGoal = true;
            QueueAllPathPlans();
            Time = 0f;
        }

        public void SetGoal(Vec2 goal)
        {
            for (int i = 0; i < Agents.Count; i++)
                Agents[i].ResetForNewGoal();
            GroupGoalFinalized = false;
            GroupGoalFinalizedTime = -1f;
            _allInsideSince = -1f;
            RebuildGoal(goal, true);
            HasActiveGoal = true;
            QueueAllPathPlans();
        }

        public IReadOnlyList<Agent> AddAgents(IReadOnlyList<Vec2> spawns)
        {
            var existing = new List<Agent>(Agents);
            var added = new List<Agent>(spawns.Count);
            for (int i = 0; i < spawns.Count; i++)
            {
                Vec2 p = FindFreeSpawn(spawns[i]);
                Agent a = CreateAgent(p);
                Agents.Add(a);
                added.Add(a);
            }

            bool reflow = GroupGoalFinalized;
            RebuildGoalForCount(GoalCenter, Agents.Count, existing);
            GroupGoalFinalized = false;
            GroupGoalFinalizedTime = -1f;
            _allInsideSince = -1f;

            if (reflow)
            {
                // The click point now has to hold more units. Re-pack the whole formation:
                // earlier units voluntarily walk to their new reserved slots, making room for
                // the newcomers. Nobody is translated or pushed.
                ReflowCount++;
                GoalAssigner.AssignInitial(Agents, GoalLayout);
                for (int i = 0; i < existing.Count; i++)
                {
                    Agent a = existing[i];
                    a.ResetForNewGoal();
                    a.DistanceToSlot = Vec2.Distance(a.Position, a.SlotPosition);
                }
                QueueAllPathPlans();
            }
            else
            {
                for (int i = 0; i < added.Count; i++)
                {
                    added[i].ResetForNewGoal();
                    added[i].DistanceToSlot = Vec2.Distance(added[i].Position, added[i].SlotPosition);
                    added[i].PathPlanQueued = true;
                    _pathQueue.Enqueue(added[i]);
                }
            }
            return added;
        }

        public void Step(float dt)
        {
            if (dt <= 0f) return;
            if (dt > 1f / 30f)
            {
                // Keep behaviour stable if the host frame hitches.
                int substeps = Math.Min(4, (int)MathF.Ceiling(dt / (1f / 60f)));
                float sub = dt / substeps;
                for (int i = 0; i < substeps; i++) StepInternal(sub);
                return;
            }
            StepInternal(dt);
        }

        private void StepInternal(float dt)
        {
            TickCount++;
            TimeStepHint = dt;
            EnsureCapacity(Agents.Count);
            var frameWatch = Stopwatch.StartNew();

            ProcessPathQueue(Config.SharedFlowField
                ? Math.Max(32, Config.PathPlanFrameBudget)
                : Config.PathPlanFrameBudget);
            ProcessReplans(2);
            UpdatePathsAndDesires(dt);

            _hash.Rebuild(Agents);
            _stopwatch.Restart();
            for (int i = 0; i < Agents.Count; i++)
            {
                Agent a = Agents[i];
                if (a.State == AgentState.Arrived || a.State == AgentState.Unreachable)
                {
                    a.ChosenVelocity = Vec2.Zero;
                    continue;
                }
                a.ChosenVelocity = ComputeAvoidance(a);
            }
            LastAvoidanceMs = _stopwatch.Elapsed.TotalMilliseconds;

            _stopwatch.Restart();
            CommitMoves(dt);
            LastCommitMs = _stopwatch.Elapsed.TotalMilliseconds;

            for (int i = 0; i < Agents.Count; i++)
                UpdateArrivalAndFacing(Agents[i], dt);
            UpdateCourtesyYields(dt);
            UpdateGroupSettlement(dt);
            OverlapRollbacks = _overlapRollbacks;

            frameWatch.Stop();
            LastStepMs = frameWatch.Elapsed.TotalMilliseconds;
            Time += dt;
        }

        private void UpdatePathsAndDesires(float dt)
        {
            for (int i = 0; i < Agents.Count; i++)
            {
                Agent a = Agents[i];
                if (a.State == AgentState.Arrived)
                {
                    a.DesiredSpeed = 0f;
                    a.PreferredVelocity = Vec2.Zero;
                    continue;
                }

                a.ReplanCooldown = MathF.Max(0f, a.ReplanCooldown - dt);
                if (a.BlockedTime > 0.55f && a.DistanceToSlot > Config.ArrivalRadius * 2f)
                    RequestReplan(a, 1.4f);

                if (!a.HasPath)
                {
                    a.PreferredVelocity = Vec2.Zero;
                    a.DesiredSpeed = 0f;
                    continue;
                }

                AdvanceWaypoints(a);
                Vec2 target = a.WaypointIndex >= 0 && a.WaypointIndex < a.Path.Count
                    ? a.Path[a.WaypointIndex]
                    : a.SlotPosition;
                bool final = a.WaypointIndex >= a.Path.Count - 1;
                Vec2 to = target - a.Position;
                float dist = to.Length;
                if (dist < 1e-5f)
                {
                    a.PreferredVelocity = Vec2.Zero;
                    a.DesiredSpeed = 0f;
                    continue;
                }

                float speed = Config.MaxSpeed;
                if (final)
                {
                    float near = MathF.Max(Config.GoalMinSpeed, dist * 2.2f);
                    speed = MathF.Min(speed, near);
                }

                if (a.Velocity.LengthSq > 0.04f)
                {
                    float currentAngle = MathF.Atan2(a.Velocity.Z, a.Velocity.X);
                    float targetAngle = MathF.Atan2(to.Z, to.X);
                    float turn = MathF.Abs(MathUtil.AngleDelta(currentAngle, targetAngle));
                    if (!final)
                    {
                        if (turn > 1.9f) speed *= 0.35f;
                        else if (turn > 1.0f) speed *= 0.65f;
                    }
                }

                if (Config.GoalZoneMaxSpeed > 0f && HasActiveGoal &&
                    a.DistanceToSlot > 1.2f &&
                    Vec2.DistanceSq(a.Position, GoalCenter) <= GoalFootprintRadius * GoalFootprintRadius)
                {
                    speed = MathF.Min(speed, Config.GoalZoneMaxSpeed);
                }

                speed *= a.SpeedScale;
                a.DesiredSpeed = speed;
                a.PreferredVelocity = to * (speed / dist);
            }
        }

        /// <summary>
        /// Queue the initial path batch front-first. The simulation plans a small number of
        /// paths per tick, so a 100+ unit click never produces a multi-frame CPU spike.
        /// </summary>
        public void QueueAllPathPlans()
        {
            _pathQueue.Clear();
            var order = new List<Agent>(Agents.Count);
            for (int i = 0; i < Agents.Count; i++)
            {
                Agent a = Agents[i];
                a.DistanceToSlot = Vec2.Distance(a.Position, a.SlotPosition);
                a.HasPath = false;
                a.Path.Clear();
                a.WaypointIndex = 0;
                a.PathPlanQueued = true;
                order.Add(a);
            }
            order.Sort((a, b) =>
            {
                int c = a.DistanceToSlot.CompareTo(b.DistanceToSlot);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            for (int i = 0; i < order.Count; i++) _pathQueue.Enqueue(order[i]);
        }

        private void ProcessPathQueue(int budget)
        {
            if (_pathQueue.Count == 0) return;
            if (budget <= 0) budget = Agents.Count;

            _stopwatch.Restart();
            int done = 0;
            while (done < budget && _pathQueue.Count > 0)
            {
                Agent a = _pathQueue.Dequeue();
                if (!a.PathPlanQueued) continue;
                a.PathPlanQueued = false;
                if (a.State == AgentState.Arrived || a.State == AgentState.Unreachable) continue;
                PlanAgent(a);
                done++;
            }
            double ms = _stopwatch.Elapsed.TotalMilliseconds;
            if (done > 0)
            {
                LastPlanMs = ms;
                TotalPlanMs += ms;
                PlanFrames++;
                if (ms > PeakPlanFrameMs) PeakPlanFrameMs = ms;
            }
        }

        private void ProcessReplans(int budget)
        {
            int done = 0;
            while (done < budget && _replanQueue.Count > 0)
            {
                Agent a = _replanQueue.Dequeue();
                a.ReplanQueued = false;
                if (a.State != AgentState.Moving) continue;
                PlanAgent(a);
                a.Replans++;
                TotalReplans++;
                a.BlockedTime = 0.25f;
                a.StuckTime = 0f;
                done++;
            }
            // Stale requests (e.g. arrived while queued) are simply dropped next tick.
            while (_replanQueue.Count > 0 && _replanQueue.Peek().ReplanQueued == false)
                _replanQueue.Dequeue();
        }

        private void AdvanceWaypoints(Agent a)
        {
            if (a.Path.Count == 0) return;
            if (a.WaypointIndex <= 0) a.WaypointIndex = 1;
            while (a.WaypointIndex < a.Path.Count)
            {
                int wpIndex = a.WaypointIndex;
                Vec2 wp = a.Path[wpIndex];
                float d = Vec2.Distance(a.Position, wp);
                bool last = wpIndex == a.Path.Count - 1;
                float threshold = last ? Config.ArrivalRadius * 0.9f : 0.55f;
                if (d <= threshold)
                {
                    a.WaypointIndex++;
                    continue;
                }

                if (!last && a.Velocity.LengthSq > 0.04f)
                {
                    Vec2 to = wp - a.Position;
                    float closing = Vec2.Dot(to, a.Velocity);

                    // A local-avoidance sidestep can push a unit more than one metre
                    // off a waypoint. The old code then kept the waypoint behind the
                    // unit until it physically walked back to within 1 m, producing a
                    // visible 10-30 m U-turn. If the next waypoint is visible, skip
                    // the stale one as soon as we are moving away from it.
                    bool passed = false;
                    if (wpIndex > 0)
                    {
                        Vec2 incoming = wp - a.Path[wpIndex - 1];
                        if (incoming.LengthSq > 1e-6f)
                            passed = Vec2.Dot(a.Position - wp, incoming.Normalized()) > 0.10f;
                    }

                    if (closing < 0f && d < 2.8f)
                    {
                        Vec2 next = a.Path[wpIndex + 1];
                        if (Grid.IsSegmentWalkable(a.Position, next))
                        {
                            a.WaypointIndex++;
                            continue;
                        }
                    }

                    if (passed && d < 4.0f)
                    {
                        Vec2 next = a.Path[wpIndex + 1];
                        if (Grid.IsSegmentWalkable(a.Position, next))
                        {
                            a.WaypointIndex++;
                            continue;
                        }
                    }

                    // The waypoint is behind us and cannot be skipped safely (the next
                    // chord is blocked). Walking back to it can mean a 10-30 m detour on
                    // the wrong side of a rock. Replan from the current position instead.
                    // Do not do this inside the goal footprint: there the "backward"
                    // motion is usually a short traffic detour, and replanning every
                    // blocked unit would thrash the goal packing.
                    if ((closing < 0f && d > 1.2f) || passed)
                    {
                        float goalKeepOut = GoalFootprintRadius + 1.0f;
                        if (!HasActiveGoal ||
                            Vec2.DistanceSq(a.Position, GoalCenter) > goalKeepOut * goalKeepOut)
                            RequestReplan(a, 1.5f);
                    }
                }
                break;
            }

            if (a.WaypointIndex >= a.Path.Count)
            {
                a.WaypointIndex = a.Path.Count - 1;
                a.HasPath = false;
            }
        }

        private void RequestReplan(Agent a, float cooldown)
        {
            if (!a.HasPath || a.ReplanQueued || a.ReplanCooldown > 0f) return;
            a.ReplanQueued = true;
            a.ReplanCooldown = cooldown;
            _replanQueue.Enqueue(a);
        }

        private Vec2 ComputeAvoidance(Agent a)
        {
            Vec2 pref = a.PreferredVelocity;
            float prefSpeed = pref.Length;
            if (prefSpeed < 0.03f)
                return Vec2.Zero;

            GatherNeighbors(a);

            if (Config.UseOrca)
            {
                Vec2 orca = _orca.Compute(a, Agents, _neighbors, pref,
                    a.MaxSpeed * a.SpeedScale, Config.OrcaTimeHorizon, TimeStepHint,
                    Config.OrcaSafetyMargin);
                if (orca.LengthSq < 1e-8f) return Vec2.Zero;
                if (StaticCandidateSafe(a, orca, MathF.Max(0.20f, TimeStepHint * 5f)))
                    return LimitAcceleration(a, orca);
            }

            float separation = Config.RequiredSeparation;
            float sepSq = separation * separation;
            float horizon = Config.AvoidanceHorizon;
            Vec2 prefDir = pref / prefSpeed;

            Vec2 best = Vec2.Zero;
            float bestScore = 0f; // stopping is always an available, safe fallback
            float bestMinGap = separation;

            for (int oi = 0; oi < DirectionOffsetsDeg.Length; oi++)
            {
                float offsetDeg = DirectionOffsetsDeg[oi];
                float offsetRad = offsetDeg * MathF.PI / 180f;
                Vec2 dir = prefDir.Rotated(offsetRad);
                float cos = Vec2.Dot(dir, prefDir);

                for (int si = 0; si < SpeedScales.Length; si++)
                {
                    float candSpeed = prefSpeed * SpeedScales[si];
                    if (candSpeed < 0.03f) continue;
                    Vec2 cand = dir * candSpeed;

                    if (!StaticCandidateSafe(a, cand, horizon * 0.8f))
                        continue;

                    if (!DynamicCandidateSafe(a, cand, horizon, sepSq, out float minGap))
                        continue;

                    float speedRatio = candSpeed / MathF.Max(prefSpeed, 0.05f);
                    float score = 1.35f * cos + 0.65f * speedRatio
                                  - 0.20f * (MathF.Abs(offsetRad) / MathF.PI);

                    if (offsetDeg > 0.1f) score -= 0.035f;
                    else if (offsetDeg < -0.1f) score += 0.035f;

                    if (a.AvoidSign != 0f && MathF.Sign(offsetRad) == a.AvoidSign &&
                        MathF.Abs(offsetRad) > 0.1f)
                        score += 0.06f;

                    // Deterministic deadlock escape: if pinned for a while, a safe backward
                    // or wide sidestep becomes attractive. The unit still chooses to move itself.
                    if (a.BlockedTime > 0.8f && MathF.Abs(offsetDeg) > 90f)
                        score += 1.20f;

                    float excess = MathF.Max(0f, minGap - separation);
                    score += 0.10f * MathUtil.Clamp01(excess / MathF.Max(separation, 0.01f));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cand;
                        bestMinGap = minGap;
                    }
                }
            }

            if (bestScore > 0f && MathF.Abs(best.X) + MathF.Abs(best.Z) > 1e-6f)
            {
                float sign = MathF.Sign(Vec2.Cross(prefDir, best));
                if (MathF.Abs(sign) > 0.1f)
                {
                    a.AvoidSign = sign;
                    a.AvoidSignTimer = 0.7f;
                }
            }
            else
            {
                best = Vec2.Zero;
            }

            // Smooth the velocity change. Deceleration is allowed to be a little quicker than
            // acceleration; the hard commit below still has the final say.
            return LimitAcceleration(a, best);
        }

        private Vec2 LimitAcceleration(Agent a, Vec2 desired)
        {
            Vec2 delta = desired - a.Velocity;
            float deltaLen = delta.Length;
            if (deltaLen > 1e-6f)
            {
                bool braking = desired.LengthSq < a.Velocity.LengthSq;
                float maxDelta = (braking ? Config.MaxAcceleration * 1.8f : Config.MaxAcceleration) * TimeStepHint;
                if (deltaLen > maxDelta)
                    desired = a.Velocity + delta * (maxDelta / deltaLen);
            }
            return desired;
        }

        private float TimeStepHint = 1f / 60f;

        private void GatherNeighbors(Agent a)
        {
            _neighbors.Clear();
            _hash.Query(a.Position, Config.NeighborDistance, _query);
            float maxDistSq = Config.NeighborDistance * Config.NeighborDistance;
            for (int k = 0; k < _query.Count; k++)
            {
                int j = _query[k];
                if (j == a.Id) continue;
                Agent b = Agents[j];
                if (Vec2.DistanceSq(a.Position, b.Position) > maxDistSq) continue;

                // Keep the nearest N neighbours (insertion into a tiny list).
                int insert = _neighbors.Count;
                for (int m = 0; m < _neighbors.Count; m++)
                {
                    Agent other = Agents[_neighbors[m]];
                    if (Vec2.DistanceSq(a.Position, b.Position) < Vec2.DistanceSq(a.Position, other.Position))
                    {
                        insert = m;
                        break;
                    }
                }
                if (insert < Config.MaxNeighbors)
                {
                    _neighbors.Insert(insert, j);
                    if (_neighbors.Count > Config.MaxNeighbors)
                        _neighbors.RemoveAt(_neighbors.Count - 1);
                }
            }
        }

        private bool StaticCandidateSafe(Agent a, Vec2 velocity, float horizon)
        {
            Vec2 start = a.Position;
            Vec2 end = start + velocity * horizon;
            if (!Grid.IsWorldWalkable(end)) return false;
            if (!GoalBoundaryAllows(a, end)) return false;
            if (!GoalBoundaryAllows(a, start + velocity * (horizon * 0.5f))) return false;
            return Grid.IsSegmentWalkable(start, end, 1.15f);
        }

        /// <summary>
        /// A unit that has entered the capacity-aware goal area should not leave it just
        /// because local avoidance wants to swing wide. Its reserved slot is inside the
        /// area; if the direct line is blocked it can slow down, wait, or go around inside.
        /// Yielding is exempt because stepping aside is an explicit temporary behaviour.
        /// </summary>
        private bool GoalBoundaryAllows(Agent a, Vec2 p)
        {
            if (!Config.KeepInsideGoalOnceEntered || !HasActiveGoal) return true;
            if (a.State == AgentState.Yielding) return true;
            float r = GoalFootprintRadius + 0.12f;
            if (Vec2.DistanceSq(a.Position, GoalCenter) > r * r) return true;
            return Vec2.DistanceSq(p, GoalCenter) <= r * r;
        }

        private bool DynamicCandidateSafe(Agent a, Vec2 candidate, float horizon, float sepSq,
            out float minGap)
        {
            minGap = float.MaxValue;
            for (int k = 0; k < _neighbors.Count; k++)
            {
                Agent b = Agents[_neighbors[k]];
                Vec2 bVel = b.State == AgentState.Arrived ? Vec2.Zero : b.Velocity;
                for (int p = 0; p < PredictionFractions.Length; p++)
                {
                    float t = horizon * PredictionFractions[p];
                    Vec2 pa = a.Position + candidate * t;
                    Vec2 pb = b.Position + bVel * t;
                    float d2 = Vec2.DistanceSq(pa, pb);
                    if (d2 < sepSq) return false;
                    float d = MathF.Sqrt(d2);
                    if (d < minGap) minGap = d;
                }
            }
            return true;
        }

        private void CommitMoves(float dt)
        {
            _commitDt = dt;
            _order.Clear();
            for (int i = 0; i < Agents.Count; i++)
            {
                if (Agents[i].State == AgentState.Moving || Agents[i].State == AgentState.Yielding)
                    _order.Add(i);
            }

            _order.Sort(_orderComparison);

            for (int i = 0; i < Agents.Count; i++)
            {
                _newPositions[i] = Agents[i].Position;
                _newVelocities[i] = Agents[i].Velocity;
                _hasMoved[i] = false;
            }

            _processed.Clear();
            if (Config.SimultaneousCommit)
            {
                for (int oi = 0; oi < _order.Count; oi++)
                {
                    int idx = _order[oi];
                    Agent a = Agents[idx];
                    Vec2 step = a.ChosenVelocity * dt;
                    if (step.LengthSq < 1e-10f)
                    {
                        a.Velocity = Vec2.Zero;
                        continue;
                    }

                    Vec2 safe = FindStaticSafeStep(a, step);
                    if (Vec2.DistanceSq(safe, step) > 1e-8f) HardClampCount++;

                    a.Position += safe;
                    a.Velocity = safe / dt;
                    a.DistanceTravelled += safe.Length;
                    _newPositions[idx] = a.Position;
                    _newVelocities[idx] = a.Velocity;
                    _hasMoved[idx] = true;
                }
            }
            else
            {
                for (int oi = 0; oi < _order.Count; oi++)
                {
                    int idx = _order[oi];
                    Agent a = Agents[idx];
                    Vec2 step = a.ChosenVelocity * dt;
                    if (step.LengthSq < 1e-10f)
                    {
                        a.Velocity = Vec2.Zero;
                        _processed.Add(idx);
                        continue;
                    }

                    Vec2 safe = FindSafeStep(a, step);
                    if (Vec2.DistanceSq(safe, step) > 1e-8f) HardClampCount++;
                    float maxStep = Config.MaxSpeed * dt;
                    float stepLen = safe.Length;
                    if (stepLen > maxStep)
                        safe *= maxStep / stepLen;

                    a.Position += safe;
                    a.Velocity = safe / dt;
                    a.DistanceTravelled += safe.Length;

                    _newPositions[idx] = a.Position;
                    _newVelocities[idx] = a.Velocity;
                    _hasMoved[idx] = true;
                    _processed.Add(idx);
                }
            }

            ResolveResidualOverlaps();
        }

        private Vec2 FindStaticSafeStep(Agent a, Vec2 fullStep)
        {
            if (GoalBoundaryAllows(a, a.Position + fullStep) &&
                Grid.IsSegmentWalkable(a.Position, a.Position + fullStep, 1.05f))
                return fullStep;

            float lo = 0f;
            float hi = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Grid.IsSegmentWalkable(a.Position, a.Position + fullStep * mid, 1.05f)) lo = mid;
                else hi = mid;
            }
            return lo < 0.02f ? Vec2.Zero : fullStep * lo;
        }

        /// <summary>
        /// ORCA predicts reciprocal motion, so the sequential commit uses the predicted
        /// position of not-yet-processed neighbours instead of their current position. That
        /// avoids the classic "everyone brakes for a space that is about to open" jam. If the
        /// prediction nevertheless leaves a contact, the lower-priority mover cancels its own
        /// step; no agent is ever translated by another agent.
        /// </summary>
        private void ResolveResidualOverlaps()
        {
            float sep = Config.RequiredSeparation;
            float sepSq = sep * sep;
            for (int pass = 0; pass < 6; pass++)
            {
                _hash.Rebuild(Agents);
                bool any = false;
                for (int i = 0; i < Agents.Count; i++)
                {
                    Vec2 pi = Agents[i].Position;
                    _hash.Query(pi, sep, _query);
                    for (int q = 0; q < _query.Count; q++)
                    {
                        int j = _query[q];
                        if (j <= i) continue;
                        Vec2 pj = Agents[j].Position;
                        if (Vec2.DistanceSq(pi, pj) >= sepSq) continue;
                        any = true;
                        int revert = Agents[i].DistanceToSlot >= Agents[j].DistanceToSlot ? i : j;
                        Agent a = Agents[revert];
                        a.Position -= a.Velocity * _commitDt;
                        a.Velocity = Vec2.Zero;
                        _newPositions[revert] = a.Position;
                        _hasMoved[revert] = false;
                        _overlapRollbacks++;
                    }
                }
                if (!any) return;
            }

            // Extremely dense fallback: restore all agents that moved this tick to their
            // pre-tick positions. This still never moves an agent due to another's push.
            for (int i = 0; i < Agents.Count; i++)
            {
                Agent a = Agents[i];
                if (a.State == AgentState.Arrived) continue;
                a.Position -= a.Velocity * _commitDt;
                a.Velocity = Vec2.Zero;
                _hasMoved[i] = false;
                _newPositions[i] = a.Position;
                _overlapRollbacks++;
            }
        }

        private Vec2 FindSafeStep(Agent a, Vec2 fullStep)
        {
            if (IsPrefixSafe(a, fullStep)) return fullStep;

            float lo = 0f;
            float hi = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (IsPrefixSafe(a, fullStep * mid)) lo = mid;
                else hi = mid;
            }
            if (lo < 0.02f) return Vec2.Zero;
            return fullStep * lo;
        }

        private bool IsPrefixSafe(Agent a, Vec2 step)
        {
            if (step.LengthSq < 1e-10f) return true;
            Vec2 end = a.Position + step;
            if (!Grid.IsWorldWalkable(end)) return false;
            if (!GoalBoundaryAllows(a, end)) return false;
            if (!Grid.IsSegmentWalkable(a.Position, end, 1.05f)) return false;

            float sep = Config.RequiredSeparation;
            float sepSq = sep * sep;
            // A neighbour that has not committed yet may move into range this
            // tick, so query with its maximum possible displacement included.
            float queryRadius = sep + Config.MaxSpeed * _commitDt + 0.05f;
            _hash.Query(end, queryRadius, _query);
            for (int qi = 0; qi < _query.Count; qi++)
            {
                int j = _query[qi];
                if (ReferenceEquals(Agents[j], a)) continue;
                Agent b = Agents[j];
                Vec2 q;
                if (_hasMoved[j])
                {
                    q = _newPositions[j];
                }
                else if (!Config.PredictedCommit ||
                         b.State == AgentState.Arrived || b.State == AgentState.Unreachable)
                {
                    q = b.Position;
                }
                else
                {
                    // Use the neighbour's intended new position. It will re-check this agent's
                    // committed position when its own turn comes, so a late clamp cannot push.
                    q = b.Position + b.ChosenVelocity * _commitDt;
                }
                if (DistanceSqPointSegment(q, a.Position, end) < sepSq)
                    return false;
            }
            return true;
        }

        private static float DistanceSqPointSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            float lenSq = ab.LengthSq;
            if (lenSq < 1e-12f) return Vec2.DistanceSq(p, a);
            float t = Vec2.Dot(p - a, ab) / lenSq;
            t = MathUtil.Clamp01(t);
            Vec2 closest = a + ab * t;
            return Vec2.DistanceSq(p, closest);
        }

        private void UpdateArrivalAndFacing(Agent a, float dt)
        {
            a.DistanceToSlot = Vec2.Distance(a.Position, a.SlotPosition);

            if (a.State == AgentState.Yielding)
            {
                a.YieldTimer -= dt;
                if (a.YieldTimer <= 0f)
                    StartReturnFromYield(a);
            }
            else if (a.State == AgentState.Moving)
            {
                bool centerAttraction = Config.GoalCenterAttraction && HasActiveGoal;
                bool atSlot = centerAttraction
                    ? Vec2.DistanceSq(a.Position, GoalCenter) <= 1.0f
                    : a.DistanceToSlot <= Config.ArrivalRadius;
                if (atSlot)
                {
                    a.State = AgentState.Arrived;
                    a.ArrivalTime = Time;
                    a.Velocity = Vec2.Zero;
                    a.DesiredSpeed = 0f;
                    a.PreferredVelocity = Vec2.Zero;
                    a.HasPath = false;
                    a.SpeedScale = 1f;
                    a.ReturningToSlot = false;
                }
                else
                {
                    float actual = a.Velocity.Length;
                    if (a.DesiredSpeed > 0.3f && actual < 0.10f)
                        a.BlockedTime += dt;
                    else
                        a.BlockedTime = MathF.Max(0f, a.BlockedTime - dt * 2f);

                    if (a.DesiredSpeed > 0.3f && actual < 0.25f * a.DesiredSpeed)
                        a.StuckTime += dt;
                    else
                        a.StuckTime = MathF.Max(0f, a.StuckTime - dt * 1.5f);
                }

                a.AvoidSignTimer = MathF.Max(0f, a.AvoidSignTimer - dt);
                if (a.AvoidSignTimer <= 0f) a.AvoidSign = 0f;
            }

            float targetFacing = a.Facing;
            if (a.Velocity.LengthSq > 0.01f)
                targetFacing = MathF.Atan2(a.Velocity.Z, a.Velocity.X);
            else if (a.State == AgentState.Yielding)
            {
                Vec2 to = a.YieldTarget - a.Position;
                if (to.LengthSq > 0.01f) targetFacing = MathF.Atan2(to.Z, to.X);
            }
            else if (a.State != AgentState.Arrived)
            {
                Vec2 to = a.SlotPosition - a.Position;
                if (to.LengthSq > 0.01f) targetFacing = MathF.Atan2(to.Z, to.X);
            }

            float maxTurn = 10f * dt;
            a.Facing += MathUtil.Clamp(MathUtil.AngleDelta(a.Facing, targetFacing), -maxTurn, maxTurn);
            a.LastSpeed = a.Velocity.Length;
        }

        /// <summary>
        /// Requirement-3 behaviour: a unit that is already parked but physically blocks a
        /// still-moving unit's final approach is asked to walk (not be pushed) to a nearby
        /// free spot. Its reserved slot stays reserved, and it returns afterwards.
        /// </summary>
        private void UpdateCourtesyYields(float dt)
        {
            if (!Config.EnableCourtesyYield) return;
            int issued = 0;
            for (int i = 0; i < Agents.Count && issued < 2; i++)
            {
                Agent mover = Agents[i];
                if (mover.State != AgentState.Moving || mover.ReturningToSlot) continue;
                if (mover.BlockedTime < 0.65f) continue;
                if (Time - mover.LastYieldRequestTime < 0.9f) continue;
                if (mover.DistanceToSlot < Config.ArrivalRadius * 1.5f) continue;
                if (Vec2.Distance(mover.Position, GoalCenter) > _goalMaxRadius + 2.8f) continue;

                int blockerIndex = FindYieldBlocker(mover);
                if (blockerIndex < 0) continue;
                Agent blocker = Agents[blockerIndex];
                if (TryStartYield(blocker, mover))
                {
                    mover.LastYieldRequestTime = Time;
                    issued++;
                }
            }
        }

        private int FindYieldBlocker(Agent mover)
        {
            Vec2 start = mover.Position;
            Vec2 end = mover.SlotPosition;
            Vec2 delta = end - start;
            float lenSq = delta.LengthSq;
            float len = MathF.Sqrt(lenSq);
            if (len < 0.2f) return -1;

            int best = -1;
            float bestT = float.MaxValue;
            for (int j = 0; j < Agents.Count; j++)
            {
                if (j == mover.Id) continue;
                Agent b = Agents[j];
                if (b.State != AgentState.Arrived) continue;
                if (Time - b.LastYieldTime < 4.0f) continue;
                if (Vec2.DistanceSq(b.Position, start) > 3.4f * 3.4f) continue;

                float t = Vec2.Dot(b.Position - start, delta) / lenSq;
                if (t <= 0.04f || t >= 1.02f) continue;
                Vec2 closest = start + delta * t;
                if (Vec2.DistanceSq(closest, b.Position) > 1.05f * 1.05f) continue;
                if (t < bestT)
                {
                    bestT = t;
                    best = j;
                }
            }
            return best;
        }

        private bool TryStartYield(Agent blocker, Agent mover)
        {
            if (blocker.State != AgentState.Arrived) return false;
            if (Time - blocker.LastYieldTime < 4.0f) return false;

            Vec2 pathDir = (mover.SlotPosition - mover.Position).Normalized();
            Vec2 pathPerp = pathDir.PerpLeft;
            Vec2 away = blocker.Position - GoalCenter;
            if (away.LengthSq < 0.04f) away = pathPerp;
            away = away.Normalized();

            float[] angles = YieldAngles;
            float sep = Config.RequiredSeparation;
            Vec2 bestTarget = Vec2.Zero;
            float bestScore = float.MinValue;
            for (int li = 0; li < 3; li++)
            {
                float dist = 1.05f + li * 0.42f;
                for (int ai = 0; ai < angles.Length; ai++)
                {
                    Vec2 dir = away.Rotated(angles[ai]);
                    if (ai >= 7) dir = pathPerp.Rotated(angles[ai] > 0f ? 0.35f : -0.35f);
                    Vec2 target = blocker.Position + dir * dist;
                    if (!Grid.IsWorldWalkable(target)) continue;
                    if (!Grid.IsSegmentWalkable(blocker.Position, target)) continue;
                    if (Vec2.DistanceSq(target, mover.SlotPosition) < sep * sep * 1.1f) continue;
                    if (Vec2.DistanceSq(target, GoalCenter) + 0.05f <
                        Vec2.DistanceSq(blocker.Position, GoalCenter)) continue;

                    bool blocked = false;
                    for (int k = 0; k < Agents.Count; k++)
                    {
                        if (k == blocker.Id) continue;
                        if (Vec2.DistanceSq(target, Agents[k].Position) < (sep + 0.04f) * (sep + 0.04f))
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked) continue;

                    float corridor = DistanceSqPointSegment(target, mover.Position, mover.SlotPosition);
                    corridor = MathF.Sqrt(corridor);
                    float outward = Vec2.Distance(target, GoalCenter) - Vec2.Distance(blocker.Position, GoalCenter);
                    float score = 0.85f * MathF.Min(corridor, 1.6f) + 0.30f * outward - 0.22f * dist;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = target;
                    }
                }
            }

            if (bestScore == float.MinValue) return false;

            blocker.State = AgentState.Yielding;
            blocker.YieldTimer = 3.0f;
            blocker.SpeedScale = 0.42f;
            blocker.YieldTarget = bestTarget;
            blocker.YieldCount++;
            blocker.LastYieldTime = Time;
            blocker.ArrivalTime = -1f;
            blocker.ReturningToSlot = false;
            blocker.BlockedTime = 0f;
            blocker.YieldTarget = bestTarget;
            SetDirectPath(blocker, bestTarget);
            TotalCourtesyYields++;
            return true;
        }

        private void StartReturnFromYield(Agent a)
        {
            a.State = AgentState.Moving;
            a.ReturningToSlot = true;
            a.SpeedScale = 0.85f;
            a.HasPath = false;
            a.YieldTimer = 0f;
            a.BlockedTime = 0f;
            PlanAgent(a);
        }

        private void SetDirectPath(Agent a, Vec2 target)
        {
            a.Path.Clear();
            a.Path.Add(a.Position);
            if (Vec2.DistanceSq(a.Position, target) > 1e-6f) a.Path.Add(target);
            a.WaypointIndex = a.Path.Count > 1 ? 1 : 0;
            a.HasPath = a.Path.Count > 0;
            a.TotalPathLength = Vec2.Distance(a.Position, target);
        }

        /// <summary>
        /// A destination point has no physical capacity for 100+ capsules. The reserved slots
        /// define the target footprint. Once every unit is inside that footprint and the pack
        /// has stopped improving for a short dwell time, the group is declared arrived. This
        /// is the finite-capacity counterpart of "everyone reaches the clicked point".
        /// </summary>
        private void UpdateGroupSettlement(float dt)
        {
            if (!Config.AllowCompactSettlement || GroupGoalFinalized || !HasActiveGoal) return;
            if (Agents.Count == 0 || Agents.Count < Config.CompactSettlementMinUnits) return;

            if (ArrivedCount() == Agents.Count)
            {
                GroupGoalFinalized = true;
                if (GroupGoalFinalizedTime < 0f) GroupGoalFinalizedTime = Time;
                return;
            }

            if (Config.GoalCenterAttraction)
            {
                // For the portal-style goal, completion means: at least one unit
                // reached the click, and the crowd has stopped making progress
                // toward it. Do not require a capacity circle that may include
                // the spawn area.
                bool reached = false;
                float sum = 0f;
                for (int i = 0; i < Agents.Count; i++)
                {
                    float d = Vec2.Distance(Agents[i].Position, GoalCenter);
                    sum += d;
                    if (d <= 1.2f) reached = true;
                }
                if (!reached) return;
                if (sum < _settleBestSum - 0.04f)
                {
                    _settleBestSum = sum;
                    _settleProgressTime = Time;
                }
                if (_settleProgressTime < 0f)
                {
                    _settleBestSum = sum;
                    _settleProgressTime = Time;
                }
                if (Time - _settleProgressTime < Config.GoalSettleDwell) return;
            }
            else
            {
                // A queue goal can have a capacity envelope that reaches back to the
                // spawn area. Queue units must reach their actual slots.
                if (GoalLayout.QueueMode) return;

                float radius = GoalFootprintRadius;
            float rSq = radius * radius;
            bool allInside = true;
            for (int i = 0; i < Agents.Count; i++)
            {
                if (Agents[i].State == AgentState.Unreachable)
                {
                    allInside = false;
                    break;
                }
                if (Vec2.DistanceSq(Agents[i].Position, GoalCenter) > rSq)
                {
                    allInside = false;
                    break;
                }
            }

            if (!allInside)
            {
                _allInsideSince = -1f;
                return;
            }

                if (_allInsideSince < 0f) _allInsideSince = Time;
                if (Time - _allInsideSince < Config.GoalSettleDwell) return;
            }

            for (int i = 0; i < Agents.Count; i++)
            {
                Agent a = Agents[i];
                if (a.State == AgentState.Arrived) continue;
                a.State = AgentState.Arrived;
                a.ArrivalTime = Time;
                a.Velocity = Vec2.Zero;
                a.ChosenVelocity = Vec2.Zero;
                a.PreferredVelocity = Vec2.Zero;
                a.DesiredSpeed = 0f;
                a.HasPath = false;
                a.SpeedScale = 1f;
                a.ReturningToSlot = false;
            }
            GroupGoalFinalized = true;
            GroupGoalFinalizedTime = Time;
        }

        private void RebuildGoal(Vec2 goal, bool assignInitial)
        {
            GoalCenter = goal;
            _goalApproachSide = ComputeApproachSide(goal);
            Vec2 side = Config.DirectionalGoalLayout ? _goalApproachSide : Vec2.Zero;
            bool narrowPoint = Config.QueueNarrowGoal && IsNarrowAt(goal);
            bool spaceAware = Config.SpaceAwareGoalLayout;
            Vec2 approach = ComputeAgentCentroid();
            try
            {
                GoalLayout = (spaceAware || narrowPoint)
                    ? GoalLayout.BuildQueue(Grid, goal, Agents.Count, Config.SlotSpacing, approach,
                        Config.QueueGoalMaxRadius)
                    : GoalLayout.Build(Grid, goal, Agents.Count, Config.SlotSpacing,
                        Config.MaxGoalRings, side);
            }
            catch (InvalidOperationException) when (!narrowPoint)
            {
                // A narrow corridor can make the circular slot generator give up.
                // Fall back to a queue along the connected walkable cells.
                GoalLayout = GoalLayout.BuildQueue(Grid, goal, Agents.Count, Config.SlotSpacing, approach,
                    Config.QueueGoalMaxRadius);
            }
            GoalCenter = GoalLayout.Center;
            _goalMaxRadius = ComputeGoalMaxRadius();
            _settleBestSum = float.MaxValue;
            _settleProgressTime = -1f;
            if (assignInitial)
            {
                bool queue = Config.QueueNarrowGoal && (narrowPoint || GoalLayout.QueueMode);
                GoalAssigner.AssignInitial(Agents, GoalLayout,
                    Config.FormationPreservingAssignment, queue);
                if (Config.GoalCenterAttraction)
                {
                    // Do not reserve outer standing slots: every unit heads for the
                    // click and settles only when blocked. Queue slots still define
                    // the capacity check, but are not parking targets.
                    for (int i = 0; i < Agents.Count; i++)
                    {
                        Agents[i].SlotIndex = 0;
                        Agents[i].SlotPosition = GoalCenter;
                        Agents[i].DistanceToSlot = Vec2.Distance(Agents[i].Position, GoalCenter);
                    }
                }
            }

            _flowReady = false;
            if (Config.SharedFlowField)
            {
                _flowField.Build(GoalCenter);
                _flowReady = _flowField.IsBuilt;
            }
        }

        private void RebuildGoalForCount(Vec2 goal, int count, IReadOnlyList<Agent> existing)
        {
            if (_goalApproachSide.LengthSq < 1e-6f)
                _goalApproachSide = ComputeApproachSide(goal);
            Vec2 side = Config.DirectionalGoalLayout ? _goalApproachSide : Vec2.Zero;
            GoalLayout = GoalLayout.Build(Grid, goal, count, Config.SlotSpacing,
                Config.MaxGoalRings, side);
            GoalCenter = GoalLayout.Center;
            _goalMaxRadius = ComputeGoalMaxRadius();
            _settleBestSum = float.MaxValue;
            _settleProgressTime = -1f;
            GoalAssigner.AssignPreservingExisting(Agents, GoalLayout, existing);
            _flowReady = false;
            if (Config.SharedFlowField)
            {
                _flowField.Build(GoalCenter);
                _flowReady = _flowField.IsBuilt;
            }
        }

        private Vec2 ComputeApproachSide(Vec2 goal)
        {
            if (!Config.DirectionalGoalLayout || Agents.Count == 0) return Vec2.Zero;
            Vec2 sum = Vec2.Zero;
            for (int i = 0; i < Agents.Count; i++) sum += Agents[i].Position;
            Vec2 centroid = sum / Agents.Count;
            Vec2 side = centroid - goal;
            return side.LengthSq > 1.0f ? side.Normalized() : Vec2.Zero;
        }

        private bool IsGoalInNarrowPassage()
        {
            return IsNarrowAt(GoalCenter);
        }

        private Vec2 ComputeAgentCentroid()
        {
            if (Agents.Count == 0) return Vec2.Zero;
            Vec2 sum = Vec2.Zero;
            for (int i = 0; i < Agents.Count; i++) sum += Agents[i].Position;
            return sum / Agents.Count;
        }

        private bool IsNarrowAt(Vec2 p)
        {
            if (!Grid.WorldToCell(p, out int x, out int z))
                return false;
            if (!Grid.IsWalkableCell(x, z))
            {
                int nearest = Grid.FindNearestWalkableCell(p);
                if (nearest < 0) return false;
                Grid.CellCoords(nearest, out x, out z);
            }
            float clearance = Grid.ClearanceAtCell(x, z);
            return clearance < Config.AgentRadius * 2f + 0.65f;
        }

        private float ComputeGoalMaxRadius()
        {
            float r = 0f;
            for (int i = 0; i < GoalLayout.Slots.Count; i++)
                r = MathF.Max(r, Vec2.Distance(GoalLayout.Slots[i].Position, GoalCenter));
            return r;
        }

        public void PlanAll()
        {
            _pathQueue.Clear();
            _stopwatch.Restart();
            for (int i = 0; i < Agents.Count; i++)
            {
                Agent a = Agents[i];
                a.PathPlanQueued = false;
                a.DistanceToSlot = Vec2.Distance(a.Position, a.SlotPosition);
                PlanAgent(a);
            }
            LastPlanMs = _stopwatch.Elapsed.TotalMilliseconds;
            TotalPlanMs += LastPlanMs;
            if (LastPlanMs > PeakPlanFrameMs) PeakPlanFrameMs = LastPlanMs;
            PlanFrames++;
        }

        public void PlanAgent(Agent a)
        {
            if (a.SlotIndex < 0) return;
            if (_flowReady)
                _flowField.BuildPath(a.Position, _pathResult);
            else
                _pathFinder.FindPath(a.Position, a.SlotPosition, _pathResult);
            TotalPathsPlanned++;
            a.TotalPathLength = 0f;
            if (!_pathResult.Success)
            {
                a.HasPath = false;
                a.Path.Clear();
                a.State = AgentState.Unreachable;
                return;
            }

            a.Path.Clear();
            if (_flowReady)
            {
                // The field already encodes the route; skipping per-agent string
                // pulling keeps a 1000-unit command cheap.
                a.Path.Add(a.Position);
                for (int i = 0; i < _pathResult.CellPath.Count; i++)
                {
                    Vec2 p = _pathResult.CellPath[i];
                    if (Vec2.DistanceSq(p, a.Path[a.Path.Count - 1]) > 1e-6f)
                        a.Path.Add(p);
                }
            }
            else
            {
                PathSmoother.Smooth(Grid, a.Position, _pathResult.CellPath, a.SlotPosition, _smooth);
                for (int i = 0; i < _smooth.Count; i++) a.Path.Add(_smooth[i]);
            }
            a.WaypointIndex = a.Path.Count > 1 ? 1 : 0;
            a.HasPath = a.Path.Count > 0;
            a.State = AgentState.Moving;

            for (int i = 1; i < a.Path.Count; i++)
                a.TotalPathLength += Vec2.Distance(a.Path[i - 1], a.Path[i]);
        }

        private Agent CreateAgent(Vec2 spawn) => CreateAgent(spawn, GoalCenter);

        private Agent CreateAgent(Vec2 spawn, Vec2 facingTarget)
        {
            if (!Grid.IsWorldWalkable(spawn))
            {
                int cell = Grid.FindNearestWalkableCell(spawn);
                if (cell < 0) throw new InvalidOperationException("Spawn is outside the walkable grid.");
                spawn = Grid.CellCenter(cell);
            }

            var a = new Agent
            {
                Id = NextAgentId++,
                Position = spawn,
                Radius = Config.AgentRadius,
                MaxSpeed = Config.MaxSpeed,
                State = AgentState.Moving
            };
            Vec2 toGoal = facingTarget - spawn;
            a.Facing = toGoal.LengthSq > 1e-5f ? MathF.Atan2(toGoal.Z, toGoal.X) : 0f;
            return a;
        }

        private Vec2 FindFreeSpawn(Vec2 desired)
        {
            if (!Grid.IsWorldWalkable(desired))
            {
                int cell = Grid.FindNearestWalkableCell(desired);
                if (cell < 0) throw new InvalidOperationException("No walkable spawn cell.");
                desired = Grid.CellCenter(cell);
            }

            float sep = Config.RequiredSeparation;
            for (float radius = 0f; radius <= 8f; radius += 0.5f)
            {
                int samples = radius < 1e-4f ? 1 : 16;
                for (int i = 0; i < samples; i++)
                {
                    float ang = i * MathUtil.TwoPi / samples;
                    Vec2 p = desired + Vec2.FromAngle(ang) * radius;
                    if (!Grid.IsWorldWalkable(p)) continue;
                    bool free = true;
                    for (int j = 0; j < Agents.Count; j++)
                    {
                        if (Vec2.DistanceSq(p, Agents[j].Position) < sep * sep)
                        {
                            free = false;
                            break;
                        }
                    }
                    if (free) return p;
                }
            }
            throw new InvalidOperationException("Could not find a free spawn position.");
        }

        private void ValidateNoInitialOverlap()
        {
            float sep = Config.RequiredSeparation;
            for (int i = 0; i < Agents.Count; i++)
            {
                for (int j = i + 1; j < Agents.Count; j++)
                {
                    if (Vec2.DistanceSq(Agents[i].Position, Agents[j].Position) < sep * sep)
                        throw new InvalidOperationException($"Initial agents {i} and {j} overlap.");
                }
            }
        }

        private void EnsureCapacity(int count)
        {
            if (count <= _newPositions.Length) return;
            int size = _newPositions.Length;
            while (size < count) size *= 2;
            Array.Resize(ref _newPositions, size);
            Array.Resize(ref _newVelocities, size);
            Array.Resize(ref _hasMoved, size);
        }

        public void SetTimeStepHint(float dt)
        {
            TimeStepHint = MathUtil.Clamp(dt, 1f / 120f, 1f / 30f);
        }

        public Vec3 WorldPosition(Agent a)
        {
            return new Vec3(a.Position.X, Grid.SampleHeight(a.Position.X, a.Position.Z), a.Position.Z);
        }

        public int ArrivedCount()
        {
            int n = 0;
            for (int i = 0; i < Agents.Count; i++) if (Agents[i].State == AgentState.Arrived) n++;
            return n;
        }

        public float MaxDistanceToSlot()
        {
            float m = 0f;
            for (int i = 0; i < Agents.Count; i++)
            {
                float d = Vec2.Distance(Agents[i].Position, Agents[i].SlotPosition);
                if (d > m) m = d;
            }
            return m;
        }
    }
}
