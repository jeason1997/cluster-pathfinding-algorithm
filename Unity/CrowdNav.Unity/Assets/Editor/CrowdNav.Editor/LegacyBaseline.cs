using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CrowdNav;
using UnityEditor;
using UnityEngine;

namespace CrowdNav.Probe
{
    [Serializable]
    public sealed class CrowdMapJson
    {
        public string id;
        public string name;
        public int width;
        public int height;
        public float cell;
        public float originX;
        public float originZ;
        public float[] spawn;
        public float spawnRadius;
        public float[] suggestedGoal;
        public float[] heights;
        public int[] walkable;
        public CrowdMapMeshJson mesh;
    }

    [Serializable]
    public sealed class CrowdMapMeshJson
    {
        public float[] vertices;
        public int[] indices;
    }

    public sealed class CrowdMap
    {
        public CrowdMapJson Json;
        public HeightMap HeightMap;
        public NavGrid Grid;
        public Vec2 SpawnCenter;
        public float SpawnRadius;
        public Vec2 SuggestedGoal;
        public string Name;

        public static CrowdMap Load(string resourceName)
        {
            string text = null;
#if UNITY_EDITOR
            string path = System.IO.Path.Combine(Application.dataPath,
                "CrowdNav.App/Resources/" + resourceName + ".json");
            if (System.IO.File.Exists(path)) text = System.IO.File.ReadAllText(path);
#endif
            if (string.IsNullOrEmpty(text))
            {
                TextAsset asset = Resources.Load<TextAsset>(resourceName);
                if (asset != null) text = asset.text;
            }
            if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("Map resource not found: " + resourceName);
            var json = JsonUtility.FromJson<CrowdMapJson>(text);
            if (json == null || json.heights == null || json.walkable == null)
                throw new InvalidOperationException("Map JSON parse failed: " + resourceName);
            var map = new CrowdMap
            {
                Json = json,
                Name = string.IsNullOrEmpty(json.name) ? json.id : json.name,
                SpawnCenter = new Vec2(json.spawn[0], json.spawn[1]),
                SpawnRadius = json.spawnRadius,
                SuggestedGoal = new Vec2(json.suggestedGoal[0], json.suggestedGoal[1])
            };
            var mask = new bool[json.walkable.Length];
            for (int i = 0; i < mask.Length; i++) mask[i] = json.walkable[i] != 0;
            map.HeightMap = new HeightMap(json.width, json.height, json.cell, json.originX, json.originZ, mask);
            for (int i = 0; i < json.heights.Length; i++)
            {
                int x = i % json.width;
                int z = i / json.width;
                map.HeightMap.SetHeight(x, z, json.heights[i]);
            }
            map.Grid = new NavGrid(map.HeightMap, Array.Empty<CircleObstacle>(), 0.35f, 42f, 0.04f);
            return map;
        }
    }

    public static class LegacyBaseline
    {
        private const int Count = 1000;

        public static void Run()
        {
            try
            {
                RunCore();
            }
            catch (Exception ex)
            {
                Debug.LogError("BENCH_FAIL " + ex);
                EditorApplication.Exit(2);
                return;
            }
            EditorApplication.Exit(0);
        }

        private static void RunCore()
        {
            Debug.Log("BENCH_START legacy editor=" + Application.unityVersion + " count=" + Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CrowdMap map = CrowdMap.Load("neicheng_walk01");
            sw.Stop();
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "BENCH_MAP loadMs={0:0.00} cells={1} spawn=({2:0.0},{3:0.0}) goal=({4:0.0},{5:0.0})",
                sw.Elapsed.TotalMilliseconds, map.Grid.CellCount, map.SpawnCenter.X, map.SpawnCenter.Z,
                map.SuggestedGoal.X, map.SuggestedGoal.Z));

            RunRate(map, 1f / 30f, 600, "legacy_30hz");
            RunRate(map, 1f / 60f, 1200, "legacy_60hz");
        }

        private static void RunRate(CrowdMap map, float dt, int ticks, string label)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var config = new SimulationConfig
            {
                AgentRadius = 0.35f,
                MaxSpeed = 3.1f,
                SlotSpacing = 0.8f,
                PathPlanFrameBudget = 2,
                GoalCenterAttraction = true,
                SharedFlowField = true,
                GoalZoneMaxSpeed = 0f,
                UseOrca = true,
                OrcaSafetyMargin = 0.10f,
                SpaceAwareGoalLayout = false,
                GoalFootprintPadding = 1.0f
            };
            var sim = new CrowdSimulation(map.Grid, config);
            sim.SetTimeStepHint(dt);

            float spawnRadius = MathF.Max(map.SpawnRadius, 0.55f * MathF.Sqrt(Count) * 0.88f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<Vec2> spawns = BuildHexFormation(map.Grid, map.SpawnCenter, Count, 0.88f, spawnRadius);
            sim.InitializeIdle(spawns);
            sw.Stop();
            double spawnMs = sw.Elapsed.TotalMilliseconds;

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            sim.SetGoal(map.SuggestedGoal);
            sw.Stop();
            double goalMs = sw.Elapsed.TotalMilliseconds;
            long goalAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            var times = new double[ticks];
            var allocs = new long[ticks];
            int totalAlloc = 0;
            for (int i = 0; i < ticks; i++)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                sim.Step(dt);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                times[i] = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                allocs[i] = a1 - a0;
                totalAlloc += (int)allocs[i];
            }

            int arrived = sim.ArrivedCount();
            double totalSim = ticks * (double)dt;
            var all = new double[ticks];
            Array.Copy(times, all, ticks);
            Array.Sort(all);
            int first = Math.Min(60, ticks);
            var firstTimes = new double[first];
            Array.Copy(times, firstTimes, first);
            Array.Sort(firstTimes);
            var steady = new double[ticks - first];
            Array.Copy(times, first, steady, 0, ticks - first);
            double steadyMean = 0;
            for (int i = 0; i < steady.Length; i++) steadyMean += steady[i];
            steadyMean /= Math.Max(1, steady.Length);
            Array.Sort(steady);

            double mean = 0;
            for (int i = 0; i < ticks; i++) mean += times[i];
            mean /= ticks;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "BENCH_RATE {0} n={1} dt={2:0.0000} ticks={3} simSec={4:0.0} spawnMs={5:0.00} goalMs={6:0.00} goalAlloc={7} " +
                "mean={8:0.000} p50={9:0.000} p95={10:0.000} p99={11:0.000} max={12:0.000} first60p95={13:0.000} steadyMean={14:0.000} steadyP95={15:0.000} " +
                "allocTotal={16} allocPerTick={17:0.0} arrived={18} peakPlan={19:0.00} pending={20}",
                label, Count, dt, ticks, totalSim, spawnMs, goalMs, goalAlloc,
                mean, Pct(all, 50), Pct(all, 95), Pct(all, 99), all[all.Length - 1], Pct(firstTimes, 95),
                steadyMean, Pct(steady, 95), totalAlloc, (double)totalAlloc / ticks, arrived,
                sim.PeakPlanFrameMs, sim.PendingPathPlans));

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "BENCH_MEM {0} managedHeapMB={1:0.0} workingSetMB={2:0.0}",
                label, GC.GetTotalMemory(false) / 1048576.0,
                System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576.0));
        }

        private static double Pct(double[] sorted, int p)
        {
            if (sorted.Length == 0) return 0;
            int idx = (int)Math.Round((p / 100.0) * (sorted.Length - 1));
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        private static List<Vec2> BuildHexFormation(NavGrid grid, Vec2 center, int count,
            float spacing, float maxRadius)
        {
            var result = new List<Vec2>(count);
            float sep = 2f * grid.AgentRadius + 0.03f;
            spacing = MathF.Max(spacing, sep);
            int maxRing = Math.Max(1, (int)MathF.Ceiling(maxRadius / spacing));
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
                ringPoints.Sort((a, b) => MathF.Atan2(a.Z - center.Z, a.X - center.X)
                    .CompareTo(MathF.Atan2(b.Z - center.Z, b.X - center.X)));
                for (int i = 0; i < ringPoints.Count && result.Count < count; i++)
                {
                    Vec2 p = ringPoints[i];
                    if (Vec2.Distance(p, center) > maxRadius) continue;
                    if (!grid.IsWorldWalkable(p)) continue;
                    bool free = true;
                    for (int j = 0; j < result.Count; j++)
                    {
                        if (Vec2.DistanceSq(p, result[j]) < sep * sep * 0.98f) { free = false; break; }
                    }
                    if (free) result.Add(p);
                }
            }
            if (result.Count < count)
                throw new InvalidOperationException($"Could only place {result.Count}/{count} spawns.");
            return result;
        }
    }
}