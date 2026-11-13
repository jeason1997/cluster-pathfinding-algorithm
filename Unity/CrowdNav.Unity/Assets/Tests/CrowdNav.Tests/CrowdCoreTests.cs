using System;
using NUnit.Framework;
using CrowdNav.Hpc;
using Unity.Collections;
using Unity.Mathematics;

namespace CrowdNav.Tests
{
    public sealed class CrowdCoreTests
    {
        private static CrowdGridData FlatGrid(int width = 160, int height = 100)
        {
            var heights = new float[width * height];
            var walkable = new bool[width * height];
            for (int i = 0; i < walkable.Length; i++) walkable[i] = true;
            return CrowdGridData.Build(heights, walkable, width, height, 0.5f, 0f, 0f, 0.35f, 42f, 0.04f);
        }

        private static NativeArray<float2> HexSpawns(float2 center, int count, float spacing, float radius)
        {
            var result = new NativeArray<float2>(count, Allocator.Persistent);
            int written = 0;
            int maxRing = (int)math.ceil(radius / spacing) + 2;
            for (int ring = 0; ring <= maxRing && written < count; ring++)
            {
                if (ring == 0) { result[written++] = center; continue; }
                int q = ring, r = -ring;
                for (int side = 0; side < 6 && written < count; side++)
                {
                    for (int step = 0; step < ring && written < count; step++)
                    {
                        float2 p = new float2(center.x + spacing * (q + r * 0.5f),
                            center.y + spacing * 0.8660254f * r);
                        if (math.distance(p, center) <= radius + 1e-3f) result[written++] = p;
                        if (side == 0) r += 1;
                        else if (side == 1) { q -= 1; r += 1; }
                        else if (side == 2) q -= 1;
                        else if (side == 3) r -= 1;
                        else if (side == 4) { q += 1; r -= 1; }
                        else q += 1;
                    }
                }
            }
            Assert.GreaterOrEqual(written, count, "spawn formation did not fill");
            return result;
        }

        [Test]
        public void GridBake_FlatMap_IsWalkable()
        {
            using (CrowdGridData data = FlatGrid())
            {
                var grid = data.Grid;
                Assert.IsTrue(grid.IsWorldWalkable(new float2(10f, 10f)));
                Assert.IsTrue(grid.IsSegmentWalkable(new float2(10f, 10f), new float2(30f, 20f)));
                Assert.Greater(grid.FindNearestWalkableCell(new float2(20f, 20f)), 0);
            }
        }

        [Test]
        public void FlowField_PlansPathToGoal()
        {
            using (CrowdGridData data = FlatGrid())
            {
                var spawns = new NativeArray<float2>(1, Allocator.Persistent);
                spawns[0] = new float2(10f, 20f);
                try
                {
                    var config = CrowdConfig.Default;
                    config.GoalCenterAttraction = true;
                    var sim = new CrowdNavSimulation(data, config, spawns, new float2(50f, 20f), false);
                    try
                    {
                        sim.SetGoal(new float2(50f, 20f));
                        for (int i = 0; i < 30 && !sim.FlowReady; i++) sim.Tick(1f / 30f);
                        for (int i = 0; i < 5; i++) sim.Tick(1f / 30f);
                        Assert.IsTrue(sim.FlowReady, "flow field never became ready");
                        Assert.Greater(sim.Agents.PathCount[0], 0, "no path extracted");
                        int count = sim.Agents.PathCount[0];
                        float2 last = sim.Agents.Path[sim.Agents.PathStart[0] + count - 1];
                        Assert.Less(math.distance(last, new float2(50f, 20f)), 2f, "path does not end near goal");
                    }
                    finally { sim.Dispose(); }
                }
                finally { spawns.Dispose(); }
            }
        }

        [Test]
        public void Simulation_NoOverlap_AndFinite()
        {
            using (CrowdGridData data = FlatGrid())
            using (var spawns = HexSpawns(new float2(20f, 20f), 100, 0.9f, 7f))
            {
                var config = CrowdConfig.Default;
                config.GoalCenterAttraction = true;
                var sim = new CrowdNavSimulation(data, config, spawns, new float2(60f, 20f), false);
                try
                {
                    sim.SetGoal(new float2(60f, 20f));
                    for (int i = 0; i < 120; i++) sim.Tick(1f / 30f);
                    float min = float.MaxValue;
                    for (int i = 0; i < 100; i++)
                    {
                        Assert.IsFalse(float.IsNaN(sim.Agents.Position[i].x) || float.IsNaN(sim.Agents.Position[i].y));
                        for (int j = i + 1; j < 100; j++)
                            min = math.min(min, math.distance(sim.Agents.Position[i], sim.Agents.Position[j]));
                    }
                    Assert.GreaterOrEqual(min, 0.69f, "overlap below required separation");
                }
                finally { sim.Dispose(); }
            }
        }

        [Test]
        public void Simulation_SameInput_IsDeterministic()
        {
            using (CrowdGridData data = FlatGrid())
            using (var spawns = HexSpawns(new float2(20f, 20f), 80, 0.9f, 7f))
            {
                var config = CrowdConfig.Default;
                config.GoalCenterAttraction = true;
                var a = new CrowdNavSimulation(data, config, spawns, new float2(60f, 20f), false);
                var b = new CrowdNavSimulation(data, config, spawns, new float2(60f, 20f), false);
                try
                {
                    a.SetGoal(new float2(60f, 20f));
                    b.SetGoal(new float2(60f, 20f));
                    // The async flow-field build can finish on different ticks; force both
                    // so the comparison measures the deterministic job graph, not job timing.
                    a.CompleteFlowNow();
                    b.CompleteFlowNow();
                    for (int i = 0; i < 90; i++) { a.Tick(1f / 30f); b.Tick(1f / 30f); }
                    for (int i = 0; i < 80; i++)
                    {
                        Assert.AreEqual(a.Agents.Position[i].x, b.Agents.Position[i].x, 1e-5f);
                        Assert.AreEqual(a.Agents.Position[i].y, b.Agents.Position[i].y, 1e-5f);
                    }
                }
                finally { a.Dispose(); b.Dispose(); }
            }
        }

        [Test]
        public void Tick_DoesNotAllocateManagedMemory()
        {
            using (CrowdGridData data = FlatGrid())
            using (var spawns = HexSpawns(new float2(20f, 20f), 100, 0.9f, 7f))
            {
                var config = CrowdConfig.Default;
                config.GoalCenterAttraction = true;
                var sim = new CrowdNavSimulation(data, config, spawns, new float2(60f, 20f), false);
                try
                {
                    sim.SetGoal(new float2(60f, 20f));
                    for (int i = 0; i < 60; i++) sim.Tick(1f / 30f);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 60; i++) sim.Tick(1f / 30f);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.LessOrEqual(allocated, 0L,
                        "simulation tick allocated managed memory: " + allocated + " bytes");
                }
                finally { sim.Dispose(); }
            }
        }
    }
}