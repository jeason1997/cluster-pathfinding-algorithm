using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CrowdNav;

namespace CrowdNav.Scenarios
{
    /// <summary>Exports a run to a JS data file consumed by visualizer/index.html.</summary>
    public static class SimulationExporter
    {
        public static void WriteVisualizerData(SimulationRun run, string path, string sceneId)
        {
            var sb = new StringBuilder(8 * 1024 * 1024);
            var ci = CultureInfo.InvariantCulture;
            ScenarioSetup setup = run.Setup;
            CrowdSimulation sim = run.Simulation;
            RunMetrics m = run.Metrics;

            sb.Append("window.CROWD_SCENES=window.CROWD_SCENES||{};window.CROWD_SCENES[");
            AppendString(sb, sceneId);
            sb.Append("]={");
            sb.Append("\"id\":"); AppendString(sb, sceneId);
            sb.Append(",\"title\":"); AppendString(sb, setup.Title);
            sb.Append(",\"description\":"); AppendString(sb, setup.Description);
            sb.Append(",\"dt\":").Append(setup.Dt.ToString("0.######", ci));
            sb.Append(",\"frameDt\":").Append((setup.Dt * 3f).ToString("0.######", ci));
            sb.Append(",\"unitRadius\":").Append(sim.Config.AgentRadius.ToString("0.###", ci));
            sb.Append(",\"slotSpacing\":").Append(sim.Config.SlotSpacing.ToString("0.###", ci));
            sb.Append(",\"goal\":[").Append(F(sim.GoalCenter.X)).Append(',').Append(F(sim.GoalCenter.Z)).Append(']');
            sb.Append(",\"goalRadius\":").Append(F(sim.GoalFootprintRadius));
            sb.Append(",\"approachSide\":[").Append(F(sim.GoalApproachSide.X)).Append(',')
                .Append(F(sim.GoalApproachSide.Z)).Append(']');
            sb.Append(",\"goalFinalized\":").Append(sim.GroupGoalFinalized ? "true" : "false");
            sb.Append(",\"goalFinalizedTime\":").Append(F(sim.GroupGoalFinalizedTime));

            sb.Append(",\"terrain\":{");
            sb.Append("\"width\":").Append(setup.Map.Width);
            sb.Append(",\"height\":").Append(setup.Map.Height);
            sb.Append(",\"cell\":").Append(setup.Map.CellSize.ToString("0.###", ci));
            sb.Append(",\"origin\":[").Append(F(setup.Map.OriginX)).Append(',').Append(F(setup.Map.OriginZ)).Append(']');
            sb.Append(",\"heights\":[");
            for (int z = 0; z < setup.Map.Height; z++)
            {
                if (z > 0) sb.Append(',');
                for (int x = 0; x < setup.Map.Width; x++)
                {
                    if (x > 0) sb.Append(',');
                    sb.Append(F(setup.Map.GetHeight(x, z)));
                }
            }
            sb.Append("]}");

            sb.Append(",\"obstacles\":[");
            for (int i = 0; i < setup.Obstacles.Count; i++)
            {
                if (i > 0) sb.Append(',');
                CircleObstacle o = setup.Obstacles[i];
                sb.Append('[').Append(F(o.Center.X)).Append(',').Append(F(o.Center.Z)).Append(',').Append(F(o.Radius)).Append(']');
            }
            sb.Append(']');

            sb.Append(",\"slots\":[");
            for (int i = 0; i < sim.GoalLayout.Slots.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Vec2 p = sim.GoalLayout.Slots[i].Position;
                sb.Append('[').Append(F(p.X)).Append(',').Append(F(p.Z)).Append(']');
            }
            sb.Append(']');

            sb.Append(",\"metrics\":{");
            sb.Append("\"units\":").Append(sim.Agents.Count);
            sb.Append(",\"simSeconds\":").Append(F((float)m.SimSeconds));
            sb.Append(",\"arrived\":").Append(m.Arrived);
            sb.Append(",\"allArrived\":").Append(m.AllArrived ? "true" : "false");
            sb.Append(",\"allArrivedTime\":").Append(F(m.LastArrivalTime));
            sb.Append(",\"meanArrival\":").Append(F(m.MeanArrivalTime));
            sb.Append(",\"minSeparation\":").Append(F(m.MinSeparation));
            sb.Append(",\"overlapTicks\":").Append(m.OverlapTicks);
            sb.Append(",\"teleports\":").Append(m.TeleportEvents);
            sb.Append(",\"finalMaxGoalDistance\":").Append(F(m.FinalMaxGoalDistance));
            sb.Append(",\"yields\":").Append(m.TotalCourtesyYields);
            sb.Append(",\"hardClamps\":").Append(m.HardClampCount);
            sb.Append(",\"planTotalMs\":").Append(F((float)m.TotalPlanMs));
            sb.Append(",\"planPeakMs\":").Append(F((float)m.PeakPlanFrameMs));
            sb.Append(",\"steadyMeanMs\":").Append(F((float)m.SteadyStepMeanMs));
            sb.Append(",\"steadyP95Ms\":").Append(F((float)m.SteadyStepP95Ms));
            sb.Append(",\"maxAcceleration\":").Append(F(m.MaxAcceleration));
            sb.Append('}');

            sb.Append(",\"frames\":[");
            for (int fi = 0; fi < run.Frames.Count; fi++)
            {
                FrameSnapshot f = run.Frames[fi];
                if (fi > 0) sb.Append(',');
                sb.Append("{\"t\":").Append(F(f.Time));
                sb.Append(",\"n\":").Append(f.X.Length);
                sb.Append(",\"x\":[");
                for (int i = 0; i < f.X.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(F(f.X[i]));
                }
                sb.Append("],\"z\":[");
                for (int i = 0; i < f.Z.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(F(f.Z[i]));
                }
                sb.Append("],\"s\":[");
                for (int i = 0; i < f.State.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(f.State[i]);
                }
                sb.Append("],\"v\":[");
                for (int i = 0; i < f.Speed.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(F(f.Speed[i]));
                }
                sb.Append("],\"min\":").Append(F(MinSeparation(f)));
                int arrived = 0;
                for (int i = 0; i < f.State.Length; i++) if (f.State[i] == 1) arrived++;
                sb.Append(",\"arr\":").Append(arrived);
                sb.Append('}');
            }
            sb.Append("]};");

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static void WriteSummaryJson(SimulationRun run, string path)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            RunMetrics m = run.Metrics;
            sb.Append('{');
            sb.Append("\"scenario\":\"").Append(run.Setup.Id).Append("\",");
            sb.Append("\"units\":").Append(run.Simulation.Agents.Count).Append(',');
            sb.Append("\"seconds\":").Append(m.SimSeconds.ToString("0.000", ci)).Append(',');
            sb.Append("\"allArrived\":").Append(m.AllArrived ? "true" : "false").Append(',');
            sb.Append("\"minSeparation\":").Append(m.MinSeparation.ToString("0.0000", ci)).Append(',');
            sb.Append("\"overlapTicks\":").Append(m.OverlapTicks).Append(',');
            sb.Append("\"teleports\":").Append(m.TeleportEvents).Append(',');
            sb.Append("\"finalMaxGoalDistance\":").Append(m.FinalMaxGoalDistance.ToString("0.000", ci)).Append(',');
            sb.Append("\"goalFootprintRadius\":").Append(m.GoalFootprintRadius.ToString("0.000", ci)).Append(',');
            sb.Append("\"maxPostArrivalMovement\":").Append(m.MaxPostArrivalMovement.ToString("0.00000", ci)).Append(',');
            sb.Append("\"planTotalMs\":").Append(m.TotalPlanMs.ToString("0.000", ci)).Append(',');
            sb.Append("\"planPeakMs\":").Append(m.PeakPlanFrameMs.ToString("0.000", ci)).Append(',');
            sb.Append("\"steadyMeanMs\":").Append(m.SteadyStepMeanMs.ToString("0.000", ci)).Append(',');
            sb.Append("\"steadyP95Ms\":").Append(m.SteadyStepP95Ms.ToString("0.000", ci)).Append(',');
            sb.Append("\"stepMaxMs\":").Append(m.StepMaxMs.ToString("0.000", ci)).Append(',');
            sb.Append("\"yields\":").Append(m.TotalCourtesyYields).Append(',');
            sb.Append("\"hardClamps\":").Append(m.HardClampCount).Append(',');
            sb.Append("\"accelP99\":").Append(m.AccelP99.ToString("0.000", ci));
            sb.Append('}');
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static float MinSeparation(FrameSnapshot f)
        {
            float min = float.MaxValue;
            int n = f.X.Length;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float dx = f.X[i] - f.X[j];
                    float dz = f.Z[i] - f.Z[j];
                    float d2 = dx * dx + dz * dz;
                    if (d2 < min) min = d2;
                }
            }
            return n <= 1 ? 0f : MathF.Sqrt(min);
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
