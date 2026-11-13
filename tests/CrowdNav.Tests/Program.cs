using System;
using System.Collections.Generic;
using System.Globalization;
using CrowdNav;
using CrowdNav.Scenarios;

namespace CrowdNav.Tests
{
    internal static class Program
    {
        private sealed class TestCase
        {
            public string Name = "";
            public Func<SimulationRun> Run = null!;
            public Action<SimulationRun>? Validate;
        }

        private static int Main()
        {
            var cases = new List<TestCase>
            {
                new TestCase
                {
                    Name = "slope_120",
                    Run = () =>
                    {
                        ScenarioSetup s = TerrainScenarios.Flagship(120);
                        s.Config.SlotSpacing = 1.0f;
                        s.TimeoutSeconds = 120f;
                        return SimulationRunner.Run(s);
                    },
                    Validate = run => ValidateNoGoalExit(run, 1.0f)
                },
                new TestCase
                {
                    Name = "goal_packing_160",
                    Run = () =>
                    {
                        ScenarioSetup s = TerrainScenarios.Packing(160);
                        s.Config.SlotSpacing = 1.0f;
                        s.Config.PathPlanFrameBudget = 2;
                        s.TimeoutSeconds = 140f;
                        return SimulationRunner.Run(s);
                    }
                },
                new TestCase
                {
                    Name = "flat_200",
                    Run = () =>
                    {
                        ScenarioSetup s = TerrainScenarios.Flat(200);
                        s.Config.SlotSpacing = 1.0f;
                        s.TimeoutSeconds = 120f;
                        return SimulationRunner.Run(s);
                    }
                },
                new TestCase
                {
                    Name = "flagship_300",
                    Run = () =>
                    {
                        ScenarioSetup s = TerrainScenarios.Flagship(300);
                        s.TimeoutSeconds = 100f;
                        return SimulationRunner.Run(s);
                    },
                    Validate = run => ValidateNoGoalExit(run, 1.0f)
                },
                new TestCase
                {
                    Name = "late_reinforcement",
                    Run = () =>
                    {
                        ScenarioSetup s = TerrainScenarios.Flagship(100);
                        TerrainScenarios.WithLateWave(s, 40, 60f);
                        s.Config.SlotSpacing = 1.0f;
                        s.TimeoutSeconds = 240f;
                        return SimulationRunner.Run(s);
                    },
                    Validate = run => Require(run.Simulation.ReflowCount > 0,
                        "expected the settled group to reflow for late units")
                },
                new TestCase
                {
                    Name = "goal_switch_80",
                    Run = () =>
                    {
                        ScenarioSetup s = TerrainScenarios.Flagship(80);
                        s.Config.SlotSpacing = 1.0f;
                        s.HasGoalSwitch = true;
                        s.GoalSwitchTime = 8f;
                        s.NewGoal = new Vec2(30f, 50f);
                        s.TimeoutSeconds = 180f;
                        return SimulationRunner.Run(s);
                    }
                }
            };

            int failed = 0;
            var report = new List<string>();
            foreach (TestCase test in cases)
            {
                Console.WriteLine();
                Console.WriteLine("=== " + test.Name + " ===");
                try
                {
                    SimulationRun run = test.Run();
                    PrintRun(run);
                    test.Validate?.Invoke(run);
                    ValidateCommon(run);
                    Console.WriteLine("PASS " + test.Name);
                    report.Add(FormatReport(test.Name, run, "PASS"));
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                    report.Add($"## {test.Name}\n\nFAIL: {ex.Message}\n");
                }
            }

            System.IO.Directory.CreateDirectory("out");
            System.IO.File.WriteAllText("out/test_report.md", "# 自动测试报告\n\n" + string.Join("\n", report));
            Console.WriteLine();
            Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : $"{failed} TEST(S) FAILED");
            return failed == 0 ? 0 : 1;
        }

        private static void ValidateCommon(SimulationRun run)
        {
            RunMetrics m = run.Metrics;
            CrowdSimulation sim = run.Simulation;
            int n = sim.Agents.Count;
            Require(m.AllArrived, $"only {m.Arrived}/{n} arrived");
            Require(sim.ArrivedCount() == n, "ArrivedCount mismatch");
            Require(m.OverlapTicks == 0, $"overlap ticks = {m.OverlapTicks}");
            Require(m.MinSeparation >= 2f * sim.Config.AgentRadius - 1e-3f,
                $"min separation {m.MinSeparation:0.0000} below diameter");
            Require(m.TeleportEvents == 0, "teleport/step > max speed detected");
            Require(m.MaxPostArrivalMovement <= 1e-4f,
                $"arrived unit moved {m.MaxPostArrivalMovement:0.0000} m");
            Require(m.Unreachable == 0, "some units have no path");
            Require(sim.PendingPathPlans == 0, "path planning did not finish");
            Require(m.FinalMaxGoalDistance <= sim.GoalFootprintRadius + 0.02f,
                $"final unit is {m.FinalMaxGoalDistance:0.00}m from goal, footprint {sim.GoalFootprintRadius:0.00}m");
            Require(sim.GroupGoalFinalized, "group goal never reached its settlement state");
            Require(m.PeakPlanFrameMs < 8.0, $"path-planning frame spike {m.PeakPlanFrameMs:0.00}ms");
            Require(m.StepMaxMs < 15.0, $"simulation frame spike {m.StepMaxMs:0.00}ms");
            Require(m.SteadyStepP95Ms < 4.0, $"steady p95 {m.SteadyStepP95Ms:0.00}ms");
            Require(m.HardClampCount < n * 2, $"too many hard safety clamps: {m.HardClampCount}");

            for (int i = 0; i < n; i++)
            {
                Agent a = sim.Agents[i];
                Require(sim.Grid.IsWorldWalkable(a.Position), $"agent {a.Id} ended inside a static obstacle");
                Require(a.State == AgentState.Arrived, $"agent {a.Id} is {a.State}");
            }
        }

        private static void ValidateNoGoalExit(SimulationRun run, float tolerance)
        {
            float r = run.Simulation.GoalFootprintRadius;
            float limit = r + tolerance;
            var entered = new bool[run.Simulation.Agents.Count];
            float[] maxAfter = new float[run.Simulation.Agents.Count];
            foreach (FrameSnapshot f in run.Frames)
            {
                for (int i = 0; i < f.X.Length; i++)
                {
                    float d = MathF.Sqrt((f.X[i] - run.Simulation.GoalCenter.X) *
                                         (f.X[i] - run.Simulation.GoalCenter.X) +
                                         (f.Z[i] - run.Simulation.GoalCenter.Z) *
                                         (f.Z[i] - run.Simulation.GoalCenter.Z));
                    if (d <= r) entered[i] = true;
                    if (entered[i] && d > maxAfter[i]) maxAfter[i] = d;
                }
            }
            for (int i = 0; i < maxAfter.Length; i++)
            {
                Require(!entered[i] || maxAfter[i] <= limit,
                    $"agent {i} entered goal r={r:0.00} then reached {maxAfter[i]:0.00} (limit {limit:0.00})");
            }
        }

        private static void PrintRun(SimulationRun run)
        {
            RunMetrics m = run.Metrics;
            int n = run.Simulation.Agents.Count;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "n={0} time={1:0.0}s arrived={2} minSep={3:0.000} " +
                "goalDist={4:0.00}/{5:0.00} planPeak={6:0.00}ms " +
                "steadyP95={7:0.000}ms stepMax={8:0.000}ms yields={9} clamps={10}",
                n, m.SimSeconds, m.Arrived, m.MinSeparation, m.FinalMaxGoalDistance,
                m.GoalFootprintRadius, m.PeakPlanFrameMs, m.SteadyStepP95Ms, m.StepMaxMs,
                m.TotalCourtesyYields, m.HardClampCount));
        }

        private static string FormatReport(string name, SimulationRun run, string status)
        {
            RunMetrics m = run.Metrics;
            int n = run.Simulation.Agents.Count;
            return $@"## {name}

- 结果: **{status}**
- 单位数: {n}
- 完成时间: {m.SimSeconds:0.0} s
- 全部到达: {m.AllArrived} ({m.Arrived}/{n})
- 最小间距: {m.MinSeparation:0.000} m (要求 >= {2f * run.Simulation.Config.AgentRadius:0.00})
- 重叠帧数: {m.OverlapTicks}
- 瞬移/超速步: {m.TeleportEvents}
- 最终离目标点最远: {m.FinalMaxGoalDistance:0.00} m (目标区域半径 {m.GoalFootprintRadius:0.00} m)
- 到达后残余移动: {m.MaxPostArrivalMovement:0.0000} m
- 路径规划总耗时: {m.TotalPlanMs:0.0} ms / {m.PlanFrames} 帧, 峰值 {m.PeakPlanFrameMs:0.0} ms
- 稳态单帧: mean {m.SteadyStepMeanMs:0.000} ms, p95 {m.SteadyStepP95Ms:0.000} ms, max {m.SteadyStepMaxMs:0.000} ms
- 主动让路: {m.TotalCourtesyYields}
- 整队重排: {run.Simulation.ReflowCount}
- 硬安全截断: {m.HardClampCount}
";
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
