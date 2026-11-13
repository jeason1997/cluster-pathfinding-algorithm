using System;
using CrowdNav;
using CrowdNav.Scenarios;

namespace CrowdSim
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string scenario = args.Length > 0 ? args[0] : "flagship";
            int units = args.Length > 1 ? int.Parse(args[1]) : 120;
            bool export = Array.IndexOf(args, "export") >= 0;
            bool fullDisk = Array.IndexOf(args, "fulldisk") >= 0;
            bool directional = Array.IndexOf(args, "directional") >= 0;
            bool noContain = Array.IndexOf(args, "nocontain") >= 0;
            ScenarioSetup setup = TerrainScenarios.Create(scenario, units);
            if (fullDisk) setup.Config.DirectionalGoalLayout = false;
            if (directional) setup.Config.DirectionalGoalLayout = true;
            if (noContain) setup.Config.KeepInsideGoalOnceEntered = false;
            if (args.Length > 2 && !args[2].StartsWith("export", StringComparison.OrdinalIgnoreCase) &&
                args[2] != "fulldisk")
                setup.Config.SlotSpacing = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            if (args.Length > 3) setup.Config.AvoidanceHorizon = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            if (args.Length > 4) setup.Config.OrcaTimeHorizon = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            if (args.Length > 5 && args[5] == "sampling") setup.Config.UseOrca = false;
            if (args.Length > 6) setup.Config.OrcaSafetyMargin = float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine(setup.Title);
            var run = SimulationRunner.Run(setup, captureFrames: export,
                frameInterval: 3, log: Console.WriteLine);
            RunMetrics m = run.Metrics;
            Console.WriteLine($"arrived={m.Arrived}/{run.Simulation.Agents.Count} all={m.AllArrived} " +
                              $"time={m.SimSeconds:0.00}s planTotal={m.InitialPlanMs:0.0}ms " +
                              $"planPeak={m.PeakPlanFrameMs:0.0}ms frames={m.PlanFrames}");
            Console.WriteLine($"arrival mean={m.MeanArrivalTime:0.0}s median={m.MedianArrivalTime:0.0}s " +
                              $"p90={m.P90ArrivalTime:0.0}s last={m.LastArrivalTime:0.0}s");
            Console.WriteLine($"minSep={m.MinSeparation:0.000} overlaps={m.OverlapTicks} " +
                              $"teleports={m.TeleportEvents} maxSlotErr={m.MaxFinalSlotError:0.000} " +
                              $"maxAccel={m.MaxAcceleration:0.0} accelP99={m.AccelP99:0.0} " +
                              $"stepP95={m.StepP95Ms:0.000}ms stepMax={m.StepMaxMs:0.000}ms");
            Console.WriteLine($"steadyMean={m.SteadyStepMeanMs:0.000}ms steadyP95={m.SteadyStepP95Ms:0.000}ms " +
                              $"steadyMax={m.SteadyStepMaxMs:0.000}ms planPeak={m.PeakPlanFrameMs:0.000}ms");
            Console.WriteLine($"stallMax={m.LongestStallSeconds:0.0}s stalls2s={m.StallsOver2s} " +
                              $"unreachable={m.Unreachable} yields={m.TotalCourtesyYields} " +
                              $"reflow={run.Simulation.ReflowCount} clamps={m.HardClampCount} " +
                              $"finalized={m.GroupGoalFinalizedTime:0.0}s " +
                              $"goalR={m.GoalFootprintRadius:0.00} finalGoalDist={m.FinalMaxGoalDistance:0.00}");
            if (export)
            {
                string sceneId = setup.Id.Replace('-', '_') + "_" + run.Simulation.Agents.Count;
                SimulationExporter.WriteVisualizerData(run, $"visualizer/data/{sceneId}.js", sceneId);
                SimulationExporter.WriteSummaryJson(run, $"out/{sceneId}_summary.json");
                Console.WriteLine($"exported visualizer/data/{sceneId}.js");
            }

            if (!run.Completed) Console.WriteLine("FAILED: " + run.FailureReason);
            return run.Completed ? 0 : 1;
        }
    }
}
