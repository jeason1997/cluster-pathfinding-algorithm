using System;
using System.Collections.Generic;
using System.Globalization;
using CrowdNav.Hpc;
using CrowdNav.Unity;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace CrowdNav.Probe
{
    public static class CoreBenchmark
    {
        private const int Count = 1000;

        public static void Run()
        {
            int exit = 2;
            try
            {
                RunCore();
                exit = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("CORE_FAIL " + ex);
            }
            EditorApplication.Exit(exit);
        }

        private static void RunCore()
        {
            Debug.Log("CORE_START " + Application.unityVersion + " count=" + Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CrowdNav.Unity.CrowdMap map = CrowdNav.Unity.CrowdMap.LoadFromText(
                System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath,
                    "CrowdNav.App/Resources/neicheng_walk01.json")));
            var gridData = CrowdGridData.Build(map.Json.heights, ToBool(map.Json.walkable),
                map.Json.width, map.Json.height, map.Json.cell, map.Json.originX, map.Json.originZ,
                0.35f, 42f, 0.04f);
            sw.Stop();
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "CORE_GRID bakeMs={0:0.0} cells={1} walkable={2}",
                sw.Elapsed.TotalMilliseconds, gridData.Grid.CellCount, CountWalkable(gridData.Grid)));

            var spawns = BuildSpawns(map, Count);
            Debug.Log("CORE_SPAWNS " + spawns.Length);

            RunRate(gridData, map, spawns, 1f / 30f, 600, "jobs_30hz");
            RunRate(gridData, map, spawns, 1f / 60f, 1200, "jobs_60hz");
            gridData.Dispose();
        }

        private static void RunRate(CrowdGridData gridData, CrowdNav.Unity.CrowdMap map, NativeArray<float2> spawns,
            float dt, int ticks, string label)
        {
            var config = CrowdConfig.Default;
            config.GoalCenterAttraction = true;
            config.SlotSpacing = 0.8f;
            config.PredictedCommit = false;
            config.SimultaneousCommit = false;
            config.MaxNeighbors = 14;
            config.NeighborDistance = 2.0f;
            config.GoalSettleDwell = 4.0f;

            var sim = new CrowdNavSimulation(gridData, config, spawns, new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z), false);
            // First schedule compiles Burst variants; do not include it in the goal/tick numbers.
            var warm = new CrowdNavSimulation(gridData, config, spawns, new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z), true);
            for (int i = 0; i < 3; i++) warm.Tick(dt);
            warm.Dispose();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long heap0 = GC.GetTotalMemory(false);
            long alloc0 = SafeAllocatedBytes();

            var goalWatch = System.Diagnostics.Stopwatch.StartNew();
            sim.SetGoal(new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z));
            goalWatch.Stop();
            double goalMs = goalWatch.Elapsed.TotalMilliseconds;
            long goalAlloc = SafeAllocatedBytes() - alloc0;

            var times = new double[ticks];
            var allocs = new long[ticks];
            for (int i = 0; i < ticks; i++)
            {
                long a0 = SafeAllocatedBytes();
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                sim.Tick(dt);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                long a1 = SafeAllocatedBytes();
                times[i] = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                allocs[i] = a1 - a0;
                if (i == 0 || i == 10 || i == 60 || i == ticks - 1)
                {
                    double avgD = 0;
                    int hasPath = 0;
                    int avgPath = 0;
                    for (int k = 0; k < Count; k++)
                    {
                        avgD += math.distance(sim.Agents.Position[k],
                            new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z));
                        if ((sim.Agents.Flags[k] & CrowdAgentFlags.HasPath) != 0) hasPath++;
                        avgPath += sim.Agents.PathCount[k];
                    }
                    avgD /= Count;
                    avgPath /= Count;
                    float avgSpeed = 0f;
                    for (int k = 0; k < Count; k++) avgSpeed += math.length(sim.Agents.Velocity[k]);
                    avgSpeed /= Count;
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "CORE_DIAG {0} tick={1} flowReady={2} pending={3} avgGoalDist={4:0.00} hasPath={5} avgPath={6} " +
                        "avgSpeed={7:0.00} a0=({8:0.00},{9:0.00}) v0=({10:0.00},{11:0.00}) pref0=({12:0.00},{13:0.00}) " +
                        "chosen0=({14:0.00},{15:0.00}) desired0={16:0.00} blocked0={17:0.00} d0={18:0.00} state0={19} flags0={20} wp0={21}",
                        label, i, sim.FlowReady, sim.IsFlowPending, avgD, hasPath, avgPath, avgSpeed,
                        sim.Agents.Position[0].x, sim.Agents.Position[0].y,
                        sim.Agents.Velocity[0].x, sim.Agents.Velocity[0].y,
                        sim.Agents.PreferredVelocity[0].x, sim.Agents.PreferredVelocity[0].y,
                        sim.Agents.ChosenVelocity[0].x, sim.Agents.ChosenVelocity[0].y,
                        sim.Agents.DesiredSpeed[0], sim.Agents.BlockedTime[0],
                        sim.Agents.DistanceToSlot[0], sim.Agents.State[0], sim.Agents.Flags[0],
                        sim.Agents.WaypointIndex[0]));
                }
            }
            long allocTotal = 0;
            for (int i = 0; i < allocs.Length; i++) allocTotal += allocs[i];
            long heap1 = GC.GetTotalMemory(false);

            int arrived = sim.ArrivedCount();
            int settled = sim.Stats[CrowdStatsIndex.Settled];
            double minSep;
            int overlaps;
            MeasureSeparation(sim.Agents, Count, out minSep, out overlaps);

            var all = new double[ticks];
            Array.Copy(times, all, ticks);
            Array.Sort(all);
            int first = Math.Min(120, ticks);
            var steady = new double[ticks - first];
            Array.Copy(times, first, steady, 0, steady.Length);
            double steadyMean = 0;
            for (int i = 0; i < steady.Length; i++) steadyMean += steady[i];
            steadyMean /= Math.Max(1, steady.Length);
            Array.Sort(steady);

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "CORE_RATE {0} n={1} dt={2:0.0000} ticks={3} goalMs={4:0.00} goalAlloc={5} " +
                "mean={6:0.000} p50={7:0.000} p95={8:0.000} p99={9:0.000} max={10:0.000} " +
                "steadyMean={11:0.000} steadyP95={12:0.000} allocTotal={13} allocPerTick={14:0.0} " +
                "heapDelta={15} arrived={16} settled={17} minSep={18:0.000} overlaps={19} " +
                "clamps={20} rollbacks={21} paths={22}",
                label, Count, dt, ticks, goalMs, goalAlloc,
                Mean(all), Pct(all, 50), Pct(all, 95), Pct(all, 99), all[all.Length - 1],
                steadyMean, Pct(steady, 95), allocTotal, (double)allocTotal / ticks,
                heap1 - heap0, arrived, settled, minSep, overlaps,
                sim.Stats[CrowdStatsIndex.HardClamps], sim.Stats[CrowdStatsIndex.OverlapRollbacks],
                sim.Stats[CrowdStatsIndex.PathsPlanned]));
            sim.Dispose();
        }

        private static long SafeAllocatedBytes()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); }
            catch { return 0; }
        }

        private static int CountWalkable(CrowdGrid grid)
        {
            int n = 0;
            for (int i = 0; i < grid.Walkable.Length; i++) if (grid.Walkable[i] != 0) n++;
            return n;
        }

        private static void MeasureSeparation(CrowdAgentStore agents, int count, out double minSep, out int overlaps)
        {
            minSep = double.MaxValue;
            overlaps = 0;
            float required = 0.7f;
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float d = math.distance(agents.Position[i], agents.Position[j]);
                    if (d < minSep) minSep = d;
                    if (d < required - 1e-3f) overlaps++;
                }
            }
        }

        public static NativeArray<float2> BuildSpawns(CrowdNav.Unity.CrowdMap map, int count)
        {
            var list = BuildHex(map.Grid, map.SpawnCenter, count, 0.88f,
                Mathf.Max(map.SpawnRadius, 0.55f * Mathf.Sqrt(count) * 0.88f));
            var result = new NativeArray<float2>(count, Allocator.Persistent);
            for (int i = 0; i < count; i++) result[i] = new float2(list[i].X, list[i].Z);
            return result;
        }

        private static List<Vec2> BuildHex(NavGrid grid, Vec2 center, int count, float spacing, float maxRadius)
        {
            var result = new List<Vec2>(count);
            float sep = 2f * grid.AgentRadius + 0.03f;
            spacing = Mathf.Max(spacing, sep);
            int maxRing = Mathf.Max(1, Mathf.CeilToInt(maxRadius / spacing));
            for (int ring = 0; ring <= maxRing && result.Count < count; ring++)
            {
                var ringPoints = new List<Vec2>();
                if (ring == 0) ringPoints.Add(center);
                else
                {
                    int q = ring, r = -ring;
                    for (int side = 0; side < 6; side++)
                    {
                        for (int step = 0; step < ring; step++)
                        {
                            float wx = spacing * (q + r * 0.5f);
                            float wz = spacing * 0.8660254f * r;
                            ringPoints.Add(new Vec2(center.X + wx, center.Z + wz));
                            if (side == 0) { r += 1; }
                            else if (side == 1) { q -= 1; r += 1; }
                            else if (side == 2) { q -= 1; }
                            else if (side == 3) { r -= 1; }
                            else if (side == 4) { q += 1; r -= 1; }
                            else { q += 1; }
                        }
                    }
                }
                ringPoints.Sort((a, b) => Mathf.Atan2(a.Z - center.Z, a.X - center.X)
                    .CompareTo(Mathf.Atan2(b.Z - center.Z, b.X - center.X)));
                for (int i = 0; i < ringPoints.Count && result.Count < count; i++)
                {
                    Vec2 p = ringPoints[i];
                    if (Vec2.Distance(p, center) > maxRadius) continue;
                    if (!grid.IsWorldWalkable(p)) continue;
                    bool free = true;
                    for (int j = 0; j < result.Count; j++)
                        if (Vec2.DistanceSq(p, result[j]) < sep * sep * 0.98f) { free = false; break; }
                    if (free) result.Add(p);
                }
            }
            if (result.Count < count) throw new InvalidOperationException("spawn fail " + result.Count);
            return result;
        }

        private static bool[] ToBool(int[] values)
        {
            var result = new bool[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = values[i] != 0;
            return result;
        }

        private static double Mean(double[] sorted)
        {
            double s = 0;
            for (int i = 0; i < sorted.Length; i++) s += sorted[i];
            return s / sorted.Length;
        }

        private static double Pct(double[] sorted, int p)
        {
            int idx = (int)Math.Round((p / 100.0) * (sorted.Length - 1));
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }
    }
}