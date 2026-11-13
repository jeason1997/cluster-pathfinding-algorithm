using System;
using System.Collections.Generic;
using CrowdNav;

namespace CrowdNav.Scenarios
{
    public sealed class FrameSnapshot
    {
        public float Time;
        public float[] X = Array.Empty<float>();
        public float[] Z = Array.Empty<float>();
        public byte[] State = Array.Empty<byte>();
        public float[] Speed = Array.Empty<float>();
    }

    public sealed class RunMetrics
    {
        public float MinSeparation = float.MaxValue;
        public int OverlapTicks;
        public int OverlapPairInstances;
        public float MaxOverlapDepth;
        public float MaxSpeedSeen;
        public float MaxAcceleration;
        public float MaxStepDistance;
        public int TeleportEvents;
        public float MaxPostArrivalMovement;
        public float FirstArrivalTime = -1f;
        public float LastArrivalTime = -1f;
        public float MedianArrivalTime = -1f;
        public float P90ArrivalTime = -1f;
        public float MeanArrivalTime = -1f;
        public int Arrived;
        public int Unreachable;
        public bool AllArrived;
        public float MaxFinalSlotError;
        public float FinalMaxGoalDistance;
        public float GoalFootprintRadius;
        public float GroupGoalFinalizedTime = -1f;
        public int TotalCourtesyYields;
        public long HardClampCount;
        public float LongestStallSeconds;
        public int StallsOver2s;
        public int TotalReplans;
        public int TotalPathsPlanned;
        public double InitialPlanMs;
        public double TotalPlanMs;
        public double PeakPlanFrameMs;
        public int PlanFrames;
        public double StepMeanMs;
        public double StepP95Ms;
        public double StepMaxMs;
        public double SteadyStepMeanMs;
        public double SteadyStepP95Ms;
        public double SteadyStepMaxMs;
        public double AccelP95;
        public double AccelP99;
        public double AvoidMeanMs;
        public double CommitMeanMs;
        public double SimSeconds;
        public long Ticks;
        public float MinTerrainHeight = float.MaxValue;
        public float MaxTerrainHeight = float.MinValue;
        public float PathHeightRange;
        public readonly List<float> StepTimes = new List<float>(4096);
        public readonly List<float> SteadyStepTimes = new List<float>(4096);
        public readonly List<float> TickMaxAccels = new List<float>(4096);
    }

    public sealed class SimulationRun
    {
        public ScenarioSetup Setup = null!;
        public CrowdSimulation Simulation = null!;
        public RunMetrics Metrics = new RunMetrics();
        public readonly List<FrameSnapshot> Frames = new List<FrameSnapshot>();
        public bool Completed;
        public string FailureReason = "";
    }

    public static class SimulationRunner
    {
        public static SimulationRun Run(ScenarioSetup setup, bool captureFrames = true,
            int frameInterval = 2, Action<string>? log = null)
        {
            var sim = new CrowdSimulation(setup.Grid, setup.Config);
            sim.SetTimeStepHint(setup.Dt);
            sim.Initialize(setup.Goal, setup.Spawns);
            var run = new SimulationRun { Setup = setup, Simulation = sim };
            RunMetrics m = run.Metrics;
            log?.Invoke($"queued {setup.Spawns.Count} paths ({setup.Config.PathPlanFrameBudget}/tick); " +
                        $"walkable cells={setup.Grid.CountWalkableCells()}, slots={sim.GoalLayout.Slots.Count}");

            int n = sim.Agents.Count;
            var lastPositions = new Vec2[n];
            var lastVelocities = new Vec2[n];
            var arrivedPrev = new bool[n];
            var stallTimers = new float[n];
            var allArrivedAt = -1f;
            bool lateAdded = setup.LateSpawns.Count == 0;
            bool goalSwitched = !setup.HasGoalSwitch;

            lastPositions = new Vec2[n];
            lastVelocities = new Vec2[n];
            arrivedPrev = new bool[n];
            stallTimers = new float[n];
            for (int i = 0; i < n; i++)
            {
                lastPositions[i] = sim.Agents[i].Position;
                lastVelocities[i] = sim.Agents[i].Velocity;
            }

            int maxTicks = (int)MathF.Ceiling(setup.TimeoutSeconds / setup.Dt);
            int nextFrame = 0;
            for (int tick = 0; tick < maxTicks; tick++)
            {
                if (!lateAdded && setup.LateSpawns.Count > 0 && sim.Time >= setup.LateTime)
                {
                    AddLateIfNeeded(sim, setup, ref lateAdded, log);
                    n = sim.Agents.Count;
                    Array.Resize(ref lastPositions, n);
                    Array.Resize(ref lastVelocities, n);
                    Array.Resize(ref arrivedPrev, n);
                    Array.Resize(ref stallTimers, n);
                    for (int i = 0; i < n; i++)
                    {
                        lastPositions[i] = sim.Agents[i].Position;
                        lastVelocities[i] = sim.Agents[i].Velocity;
                        arrivedPrev[i] = false;
                    }
                }

                if (!goalSwitched && sim.Time >= setup.GoalSwitchTime)
                {
                    sim.SetGoal(setup.NewGoal);
                    goalSwitched = true;
                    for (int i = 0; i < n; i++)
                    {
                        lastPositions[i] = sim.Agents[i].Position;
                        lastVelocities[i] = sim.Agents[i].Velocity;
                        arrivedPrev[i] = false;
                    }
                    log?.Invoke($"goal switched at t={sim.Time:0.0}s to {setup.NewGoal}");
                }

                sim.Step(setup.Dt);
                SampleTick(sim, m, lastPositions, lastVelocities, arrivedPrev, stallTimers, setup.Dt);

                if (captureFrames && tick >= nextFrame)
                {
                    run.Frames.Add(CaptureFrame(sim));
                    nextFrame = tick + frameInterval;
                }

                int arrived = sim.ArrivedCount();
                m.Arrived = arrived;
                bool allExpected = lateAdded && goalSwitched;
                if (arrived == n && allExpected)
                {
                    if (allArrivedAt < 0f) allArrivedAt = sim.Time;
                    if (sim.Time - allArrivedAt >= 1.5f) break;
                }
                else
                {
                    allArrivedAt = -1f;
                }
            }

            m.Ticks = sim.TickCount;
            m.SimSeconds = sim.Time;
            m.Arrived = sim.ArrivedCount();
            m.AllArrived = m.Arrived == n;
            m.Unreachable = 0;
            for (int i = 0; i < sim.Agents.Count; i++)
            {
                if (sim.Agents[i].State == AgentState.Unreachable) m.Unreachable++;
                if (sim.Agents[i].State == AgentState.Arrived)
                {
                    float err = Vec2.Distance(sim.Agents[i].Position, sim.Agents[i].SlotPosition);
                    if (err > m.MaxFinalSlotError) m.MaxFinalSlotError = err;
                }
                else
                {
                    run.FailureReason += $"Agent {sim.Agents[i].Id} not arrived at {sim.Time:0.0}s; ";
                }
            }

            if (m.StepTimes.Count > 0)
            {
                var sorted = new List<float>(m.StepTimes);
                sorted.Sort();
                double sum = 0;
                for (int i = 0; i < sorted.Count; i++) sum += sorted[i];
                m.StepMeanMs = sum / sorted.Count;
                m.StepP95Ms = sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.95)];
                m.StepMaxMs = sorted[sorted.Count - 1];
            }
            if (m.SteadyStepTimes.Count > 0)
            {
                var sorted = new List<float>(m.SteadyStepTimes);
                sorted.Sort();
                double sum = 0;
                for (int i = 0; i < sorted.Count; i++) sum += sorted[i];
                m.SteadyStepMeanMs = sum / sorted.Count;
                m.SteadyStepP95Ms = sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.95)];
                m.SteadyStepMaxMs = sorted[sorted.Count - 1];
            }
            if (m.TickMaxAccels.Count > 0)
            {
                var sorted = new List<float>(m.TickMaxAccels);
                sorted.Sort();
                m.AccelP95 = sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.95)];
                m.AccelP99 = sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * 0.99)];
            }
            if (m.Ticks > 0)
            {
                m.AvoidMeanMs /= m.Ticks;
                m.CommitMeanMs /= m.Ticks;
            }
            m.TotalReplans = sim.TotalReplans;
            m.TotalPathsPlanned = sim.TotalPathsPlanned;
            m.InitialPlanMs = sim.TotalPlanMs;
            m.TotalPlanMs = sim.TotalPlanMs;
            m.PeakPlanFrameMs = sim.PeakPlanFrameMs;
            m.PlanFrames = sim.PlanFrames;
            m.TotalCourtesyYields = sim.TotalCourtesyYields;
            m.HardClampCount = sim.HardClampCount;
            m.GroupGoalFinalizedTime = sim.GroupGoalFinalizedTime;
            m.GoalFootprintRadius = sim.GoalFootprintRadius;
            var arrivals = new List<float>();
            for (int i = 0; i < sim.Agents.Count; i++)
            {
                if (sim.Agents[i].ArrivalTime >= 0f) arrivals.Add(sim.Agents[i].ArrivalTime);
            }
            if (arrivals.Count > 0)
            {
                arrivals.Sort();
                m.MedianArrivalTime = arrivals[arrivals.Count / 2];
                m.P90ArrivalTime = arrivals[(int)Math.Min(arrivals.Count - 1, arrivals.Count * 0.9)];
                float sum = 0f;
                for (int i = 0; i < arrivals.Count; i++) sum += arrivals[i];
                m.MeanArrivalTime = sum / arrivals.Count;
            }
            for (int i = 0; i < sim.Agents.Count; i++)
            {
                float dg = Vec2.Distance(sim.Agents[i].Position, sim.GoalCenter);
                if (dg > m.FinalMaxGoalDistance) m.FinalMaxGoalDistance = dg;
            }
            run.Completed = m.AllArrived && m.OverlapTicks == 0 && m.TeleportEvents == 0;
            if (!run.Completed && run.FailureReason.Length == 0)
                run.FailureReason = "validation failed";
            log?.Invoke($"finished: {m.Arrived}/{n} arrived in {sim.Time:0.0}s, " +
                        $"minSep={m.MinSeparation:0.000}, overlaps={m.OverlapTicks}, " +
                        $"maxErr={m.MaxFinalSlotError:0.000}m, stepMean={m.StepMeanMs:0.000}ms");
            return run;
        }

        private static void AddLateIfNeeded(CrowdSimulation sim, ScenarioSetup setup, ref bool lateAdded,
            Action<string>? log)
        {
            if (lateAdded || setup.LateSpawns.Count == 0) return;
            sim.AddAgents(setup.LateSpawns);
            lateAdded = true;
            log?.Invoke($"added late wave at t={sim.Time:0.0}s; agents={sim.Agents.Count}; " +
                        $"replanned in {sim.LastPlanMs:0.0} ms");
        }

        private static void SampleTick(CrowdSimulation sim, RunMetrics m, Vec2[] lastPositions,
            Vec2[] lastVelocities, bool[] arrivedPrev, float[] stallTimers, float dt)
        {
            float sep = 2f * sim.Config.AgentRadius;
            float sepSq = sep * sep;
            int count = sim.Agents.Count;
            int overlapPairsThisTick = 0;
            float tickMaxAccel = 0f;
            for (int i = 0; i < count; i++)
            {
                Agent a = sim.Agents[i];
                float h = sim.Grid.SampleHeight(a.Position.X, a.Position.Z);
                if (h < m.MinTerrainHeight) m.MinTerrainHeight = h;
                if (h > m.MaxTerrainHeight) m.MaxTerrainHeight = h;

                for (int j = i + 1; j < count; j++)
                {
                    float d2 = Vec2.DistanceSq(a.Position, sim.Agents[j].Position);
                    float d = MathF.Sqrt(d2);
                    if (d < m.MinSeparation) m.MinSeparation = d;
                    if (d2 < sepSq)
                    {
                        overlapPairsThisTick++;
                        float depth = sep - d;
                        if (depth > m.MaxOverlapDepth) m.MaxOverlapDepth = depth;
                    }
                }

                float speed = a.Velocity.Length;
                if (speed > m.MaxSpeedSeen) m.MaxSpeedSeen = speed;
                float step = Vec2.Distance(a.Position, lastPositions[i]);
                if (step > m.MaxStepDistance) m.MaxStepDistance = step;
                if (step > sim.Config.MaxSpeed * dt * 1.03f + 1e-4f) m.TeleportEvents++;

                float dv = Vec2.Distance(a.Velocity, lastVelocities[i]);
                float accel = dv / dt;
                if (accel > m.MaxAcceleration) m.MaxAcceleration = accel;
                if (accel > tickMaxAccel) tickMaxAccel = accel;

                if (a.State == AgentState.Arrived)
                {
                    if (a.ArrivalTime >= 0f)
                    {
                        if (m.FirstArrivalTime < 0f || a.ArrivalTime < m.FirstArrivalTime)
                            m.FirstArrivalTime = a.ArrivalTime;
                        if (a.ArrivalTime > m.LastArrivalTime) m.LastArrivalTime = a.ArrivalTime;
                    }
                    if (arrivedPrev[i] && step > m.MaxPostArrivalMovement)
                        m.MaxPostArrivalMovement = step;
                }
                else
                {
                    if (a.State == AgentState.Moving && a.DesiredSpeed > 0.45f &&
                        a.DistanceToSlot > 0.6f && speed < 0.03f)
                    {
                        stallTimers[i] += dt;
                        if (stallTimers[i] > m.LongestStallSeconds)
                            m.LongestStallSeconds = stallTimers[i];
                        if (stallTimers[i] > 2f && stallTimers[i] - dt <= 2f) m.StallsOver2s++;
                    }
                    else
                    {
                        stallTimers[i] = 0f;
                    }
                }

                arrivedPrev[i] = a.State == AgentState.Arrived;
                lastPositions[i] = a.Position;
                lastVelocities[i] = a.Velocity;
            }

            if (overlapPairsThisTick > 0)
            {
                m.OverlapTicks++;
                m.OverlapPairInstances += overlapPairsThisTick;
            }

            m.StepTimes.Add((float)sim.LastStepMs);
            if (sim.Time > 3f) m.SteadyStepTimes.Add((float)sim.LastStepMs);
            m.TickMaxAccels.Add(tickMaxAccel);
            m.AvoidMeanMs += sim.LastAvoidanceMs;
            m.CommitMeanMs += sim.LastCommitMs;
        }

        private static FrameSnapshot CaptureFrame(CrowdSimulation sim)
        {
            int n = sim.Agents.Count;
            var f = new FrameSnapshot
            {
                Time = sim.Time,
                X = new float[n],
                Z = new float[n],
                State = new byte[n],
                Speed = new float[n]
            };
            for (int i = 0; i < n; i++)
            {
                Agent a = sim.Agents[i];
                f.X[i] = a.Position.X;
                f.Z[i] = a.Position.Z;
                f.State[i] = (byte)a.State;
                f.Speed[i] = a.Velocity.Length;
            }
            return f;
        }
    }
}
