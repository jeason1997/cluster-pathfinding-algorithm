using System;
using System.Collections.Generic;
using System.Globalization;
using CrowdNav.Hpc;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace CrowdNav.Probe
{
    public static class CoreFlatTest
    {
        public static void Run()
        {
            int exit = 2;
            try
            {
                int w = 240, h = 160;
                float cell = 0.5f;
                var heights = new float[w * h];
                var walkable = new bool[w * h];
                for (int i = 0; i < walkable.Length; i++) walkable[i] = true;
                var gridData = CrowdGridData.Build(heights, walkable, w, h, cell, 0f, 0f, 0.35f, 42f);
                var spawns = new NativeArray<float2>(1000, Allocator.Persistent);
                var list = BuildHex(new float2(25f, 20f), 1000, 0.9f, 18f);
                for (int i = 0; i < 1000; i++) spawns[i] = list[i];
                var config = CrowdConfig.Default;
                config.GoalCenterAttraction = true;
                config.PredictedCommit = false;
                var goal = new float2(85f, 20f);
                var sim = new CrowdNavSimulation(gridData, config, spawns, goal, false);
                sim.SetGoal(goal);
                float dt = 1f / 30f;
                for (int i = 0; i < 1800; i++)
                {
                    sim.Tick(dt);
                    if (i % 150 == 0 || i == 1799)
                    {
                        float avgD = 0, avgSpeed = 0, maxSpeed = 0;
                        int arrived = 0;
                        for (int k = 0; k < 1000; k++)
                        {
                            avgD += math.distance(sim.Agents.Position[k], goal);
                            float sp = math.length(sim.Agents.Velocity[k]);
                            avgSpeed += sp;
                            if (sp > maxSpeed) maxSpeed = sp;
                            if (sim.Agents.State[k] == (byte)CrowdAgentState.Arrived) arrived++;
                        }
                        Debug.Log(string.Format(CultureInfo.InvariantCulture,
                            "FLAT tick={0} t={1:0.0} avgDist={2:0.00} avgSpeed={3:0.00} maxSpeed={4:0.00} " +
                            "arrived={5} settled={6} clamps={7}",
                            i, i * dt, avgD / 1000, avgSpeed / 1000, maxSpeed, arrived,
                            sim.Stats[CrowdStatsIndex.Settled], sim.Stats[CrowdStatsIndex.HardClamps]));
                        for (int dbg = 0; dbg < 1000; dbg += 250)
                        {
                            float2 pp = sim.Agents.Position[dbg];
                            float2 vv = sim.Agents.Velocity[dbg];
                            float2 pv = sim.Agents.PreferredVelocity[dbg];
                            float2 cv = sim.Agents.ChosenVelocity[dbg];
                            int wpi = sim.Agents.WaypointIndex[dbg];
                            int pc = sim.Agents.PathCount[dbg];
                            float2 wp = pc > 0 ? sim.Agents.Path[sim.Agents.PathStart[dbg] + math.min(wpi, pc - 1)] : pp;
                            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                                "FLAT_AGENT tick={0} i={1} p=({2:0.0},{3:0.0}) v=({4:0.00},{5:0.00}) pref=({6:0.00},{7:0.00}) " +
                                "chosen=({8:0.00},{9:0.00}) desired={10:0.00} idx={11}/{12} wpDist={13:0.00} state={14} flags={15}",
                                i, dbg, pp.x, pp.y, vv.x, vv.y, pv.x, pv.y, cv.x, cv.y,
                                sim.Agents.DesiredSpeed[dbg], wpi, pc, math.distance(pp, wp),
                                sim.Agents.State[dbg], sim.Agents.Flags[dbg]));
                        }
                    }
                }
                double minSep = double.MaxValue;
                int overlaps = 0;
                for (int i = 0; i < 1000; i++)
                    for (int j = i + 1; j < 1000; j++)
                    {
                        float d = math.distance(sim.Agents.Position[i], sim.Agents.Position[j]);
                        if (d < minSep) minSep = d;
                        if (d < 0.7f - 1e-3f) overlaps++;
                    }
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "FLAT_RESULT arrived={0} settled={1} minSep={2:0.000} overlaps={3}",
                    sim.ArrivedCount(), sim.Stats[CrowdStatsIndex.Settled], minSep, overlaps));
                sim.Dispose();
                gridData.Dispose();
                spawns.Dispose();
                exit = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("FLAT_FAIL " + ex);
            }
            EditorApplication.Exit(exit);
        }

        private static List<float2> BuildHex(float2 center, int count, float spacing, float maxRadius)
        {
            var result = new List<float2>(count);
            int maxRing = Math.Max(1, (int)Math.Ceiling(maxRadius / spacing));
            const float sqrt3 = 1.7320508f;
            for (int ring = 0; ring <= maxRing && result.Count < count; ring++)
            {
                if (ring == 0) { result.Add(center); continue; }
                int q = ring, r = -ring;
                for (int side = 0; side < 6 && result.Count < count; side++)
                    for (int step = 0; step < ring && result.Count < count; step++)
                    {
                        float2 p = new float2(center.x + spacing * (q + r * 0.5f),
                            center.y + spacing * 0.5f * sqrt3 * r);
                        if (math.distance(p, center) <= maxRadius + 1e-3f) result.Add(p);
                        if (side == 0) r += 1;
                        else if (side == 1) { q -= 1; r += 1; }
                        else if (side == 2) q -= 1;
                        else if (side == 3) r -= 1;
                        else if (side == 4) { q += 1; r -= 1; }
                        else q += 1;
                    }
            }
            if (result.Count < count) throw new InvalidOperationException("hex fill " + result.Count);
            return result;
        }
    }
}