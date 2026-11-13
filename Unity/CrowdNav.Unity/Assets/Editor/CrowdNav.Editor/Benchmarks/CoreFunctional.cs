using System;
using System.Globalization;
using CrowdNav.Hpc;
using CrowdNav.Unity;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace CrowdNav.Probe
{
    public static class CoreFunctional
    {
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
                Debug.LogError("FUNC_FAIL " + ex);
            }
            EditorApplication.Exit(exit);
        }

        private static void RunCore()
        {
            CrowdNav.Unity.CrowdMap map = CrowdNav.Unity.CrowdMap.LoadFromText(
                System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath,
                    "CrowdNav.App/Resources/neicheng_walk01.json")));
            var gridData = CrowdGridData.Build(map.Json.heights, ToBool(map.Json.walkable),
                map.Json.width, map.Json.height, map.Json.cell, map.Json.originX, map.Json.originZ,
                0.35f, 42f, 0.04f);
            NativeArray<float2> spawns = CoreBenchmark.BuildSpawns(map, 1000);
            var config = CrowdConfig.Default;
            config.GoalCenterAttraction = true;
            config.SlotSpacing = 0.8f;
            config.PredictedCommit = false;
            var sim = new CrowdNavSimulation(gridData, config, spawns,
                new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z), false);
            sim.SetGoal(new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z));

            float dt = 1f / 30f;
            int ticks = 3600;
            for (int i = 0; i < ticks; i++)
            {
                sim.Tick(dt);
                if (i % 300 == 0 || i == ticks - 1)
                {
                    float avgD = 0, avgSpeed = 0;
                    int arrived = 0, hasPath = 0;
                    for (int k = 0; k < 1000; k++)
                    {
                        avgD += math.distance(sim.Agents.Position[k],
                            new float2(map.SuggestedGoal.X, map.SuggestedGoal.Z));
                        avgSpeed += math.length(sim.Agents.Velocity[k]);
                        if (sim.Agents.State[k] == (byte)CrowdAgentState.Arrived) arrived++;
                        if ((sim.Agents.Flags[k] & CrowdAgentFlags.HasPath) != 0) hasPath++;
                    }
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "FUNC tick={0} simSec={1:0.0} avgGoalDist={2:0.00} avgSpeed={3:0.00} arrived={4} hasPath={5} " +
                        "settled={6} clamps={7} rollbacks={8} flow={9}",
                        i, i * dt, avgD / 1000, avgSpeed / 1000, arrived, hasPath,
                        sim.Stats[CrowdStatsIndex.Settled], sim.Stats[CrowdStatsIndex.HardClamps],
                        sim.Stats[CrowdStatsIndex.OverlapRollbacks], sim.FlowReady));
                }
            }

            double minSep = double.MaxValue;
            int overlaps = 0;
            for (int i = 0; i < 1000; i++)
            {
                for (int j = i + 1; j < 1000; j++)
                {
                    float d = math.distance(sim.Agents.Position[i], sim.Agents.Position[j]);
                    if (d < minSep) minSep = d;
                    if (d < 0.7f - 1e-3f) overlaps++;
                }
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "FUNC_RESULT arrived={0} settled={1} minSep={2:0.000} overlaps={3} timeSec={4:0.0}",
                sim.ArrivedCount(), sim.Stats[CrowdStatsIndex.Settled], minSep, overlaps, ticks * dt));
            sim.Dispose();
            gridData.Dispose();
            spawns.Dispose();
        }

        private static bool[] ToBool(int[] values)
        {
            var result = new bool[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = values[i] != 0;
            return result;
        }
    }
}