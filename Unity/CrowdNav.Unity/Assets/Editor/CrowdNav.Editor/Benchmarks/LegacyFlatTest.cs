using System;
using System.Collections.Generic;
using System.Globalization;
using CrowdNav;
using UnityEditor;
using UnityEngine;

namespace CrowdNav.Probe
{
    public static class LegacyFlatTest
    {
        public static void Run()
        {
            int exit = 2;
            try
            {
                var map = new HeightMap(240, 160, 0.5f, 0f, 0f);
                var grid = new NavGrid(map, Array.Empty<CircleObstacle>(), 0.35f, 42f, 0.04f);
                var spawns = BuildHex(grid, new Vec2(25f, 20f), 1000, 0.9f, 18f);
                var config = new SimulationConfig
                {
                    AgentRadius = 0.35f,
                    MaxSpeed = 3.1f,
                    SlotSpacing = 0.8f,
                    GoalCenterAttraction = true,
                    SharedFlowField = true,
                    UseOrca = true,
                    OrcaSafetyMargin = 0.10f,
                    GoalZoneMaxSpeed = 0f
                };
                var sim = new CrowdSimulation(grid, config);
                sim.SetTimeStepHint(1f / 30f);
                sim.InitializeIdle(spawns);
                var goal = new Vec2(85f, 20f);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                sim.SetGoal(goal);
                sw.Stop();
                Debug.Log("LEGACYFLAT goalMs=" + sw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
                for (int i = 0; i < 300; i++)
                {
                    sim.Step(1f / 30f);
                    if (i % 60 == 0 || i == 299)
                    {
                        float avgD = 0, avgSpeed = 0, maxSpeed = 0;
                        int arrived = 0;
                        for (int k = 0; k < 1000; k++)
                        {
                            Agent a = sim.Agents[k];
                            avgD += Vec2.Distance(a.Position, goal);
                            float sp = a.Velocity.Length;
                            avgSpeed += sp;
                            if (sp > maxSpeed) maxSpeed = sp;
                            if (a.State == AgentState.Arrived) arrived++;
                        }
                        Debug.Log(string.Format(CultureInfo.InvariantCulture,
                            "LEGACYFLAT tick={0} avgDist={1:0.00} avgSpeed={2:0.00} maxSpeed={3:0.00} arrived={4}",
                            i, avgD / 1000, avgSpeed / 1000, maxSpeed, arrived));
                    }
                }
                exit = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("LEGACYFLAT_FAIL " + ex);
            }
            EditorApplication.Exit(exit);
        }

        private static List<Vec2> BuildHex(NavGrid grid, Vec2 center, int count, float spacing, float maxRadius)
        {
            var result = new List<Vec2>(count);
            int maxRing = Math.Max(1, (int)Math.Ceiling(maxRadius / spacing));
            for (int ring = 0; ring <= maxRing && result.Count < count; ring++)
            {
                if (ring == 0) { result.Add(center); continue; }
                int q = ring, r = -ring;
                for (int side = 0; side < 6 && result.Count < count; side++)
                    for (int step = 0; step < ring && result.Count < count; step++)
                    {
                        Vec2 p = new Vec2(center.X + spacing * (q + r * 0.5f),
                            center.Z + spacing * 0.8660254f * r);
                        if (Vec2.Distance(p, center) <= maxRadius + 1e-3f) result.Add(p);
                        if (side == 0) r += 1;
                        else if (side == 1) { q -= 1; r += 1; }
                        else if (side == 2) q -= 1;
                        else if (side == 3) r -= 1;
                        else if (side == 4) { q += 1; r -= 1; }
                        else q += 1;
                    }
            }
            return result;
        }
    }
}