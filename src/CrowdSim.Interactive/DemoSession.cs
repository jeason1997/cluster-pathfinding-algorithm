using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CrowdNav;
using CrowdNav.Scenarios;

namespace CrowdSim.Interactive;

/// <summary>
/// One browser session = one independent real-time CrowdSimulation. The server owns the
/// clock; the browser only sends commands (reset / goal / speed) and renders snapshots.
/// </summary>
public sealed class DemoSession
{
    private readonly object _gate = new();
    private MapEntry _map;
    private CrowdSimulation _sim;
    private int _count;
    private float _agentRadius = 0.35f;
    private float _maxSpeed = 3.1f;
    private string _error = "";
    private float _minSepEver = float.MaxValue;
    private float _lastActiveStepMs = 0.3f;
    private float _minSepCache;
    private float _minSepCacheTime = float.NegativeInfinity;

    public int Speed { get; set; } = 1;

    public int Count => _sim.Agents.Count;

    public Vec2 CurrentGoal
    {
        get
        {
            lock (_gate) return _sim.HasActiveGoal ? _sim.GoalCenter : Vec2.Zero;
        }
    }

    public DemoSession()
    {
        _map = MapCatalog.Default;
        _sim = CreateSimulation(_map, 120);
        _count = 120;
    }

    public string BuildWorldJson()
    {
        lock (_gate)
        {
            HeightMap map = _map.Map;
            int[]? walkable = null;
            if (map.HasWalkableMask)
            {
                walkable = new int[map.Width * map.Height];
                for (int z = 0; z < map.Height; z++)
                for (int x = 0; x < map.Width; x++)
                    walkable[z * map.Width + x] = map.IsWalkableCell(x, z) ? 1 : 0;
            }

            var terrain = new
            {
                width = map.Width,
                height = map.Height,
                cell = map.CellSize,
                origin = new[] { map.OriginX, map.OriginZ },
                heights = map.RawHeights,
                walkable,
                mesh = _map.Mesh == null ? null : new
                {
                    vertices = _map.Mesh.Vertices,
                    indices = _map.Mesh.Indices
                }
            };
            var obstacles = _map.Obstacles
                .Select(o => new[] { o.Center.X, o.Center.Z, o.Radius })
                .ToArray();
            var maps = MapCatalog.All
                .Select(m => new { id = m.Id, name = m.Name, source = m.Source })
                .ToArray();

            return JsonSerializer.Serialize(new
            {
                type = "world",
                mapId = _map.Id,
                mapName = _map.Name,
                maps,
                terrain,
                obstacles,
                unitRadius = _agentRadius,
                unitDiameter = _agentRadius * 2f,
                unitScale = _agentRadius / 0.35f,
                slotSpacing = MathF.Max(0.45f, _agentRadius * 2f + 0.10f),
                maxSpeed = _maxSpeed,
                count = _count,
                start = new[] { _map.SpawnCenter.X, _map.SpawnCenter.Z },
                suggestedGoal = new[] { _map.SuggestedGoal.X, _map.SuggestedGoal.Z }
            });
        }
    }

    public bool SetUnitConfig(float radius, float speed, int count, out string error)
    {
        lock (_gate)
        {
            float newRadius = Math.Clamp(radius, 0.18f, 0.8f);
            float newSpeed = Math.Clamp(speed, 1.0f, 7.0f);
            if (count < 10 || count > 1000)
            {
                error = "数量支持 10–1000。";
                _error = error;
                return false;
            }

            try
            {
                CrowdSimulation sim = CreateSimulation(_map, count, newRadius, newSpeed);
                _agentRadius = newRadius;
                _maxSpeed = newSpeed;
                _sim = sim;
                _count = count;
                _error = "";
                _minSepEver = float.MaxValue;
                _minSepCacheTime = float.NegativeInfinity;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _error = error;
                return false;
            }
        }
    }

    public bool SetMap(string id, int count, out string error)
    {
        lock (_gate)
        {
            MapEntry? target = MapCatalog.Find(id);
            if (target == null)
            {
                error = "未知地图：" + id;
                _error = error;
                return false;
            }
            if (count < 10 || count > 1000)
            {
                error = "数量支持 10–1000。";
                _error = error;
                return false;
            }

            try
            {
                CrowdSimulation sim = CreateSimulation(target, count);
                _map = target;
                _sim = sim;
                _count = count;
                _error = "";
                _minSepEver = float.MaxValue;
                _minSepCacheTime = float.NegativeInfinity;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _error = error;
                return false;
            }
        }
    }

    public bool Reset(int count, out string error)
    {
        lock (_gate)
        {
            if (count < 10 || count > 1000)
            {
                error = "数量支持 10–1000。";
                _error = error;
                return false;
            }

            try
            {
                CrowdSimulation sim = CreateSimulation(_map, count);
                _sim = sim;
                _count = count;
                _error = "";
                _minSepEver = float.MaxValue;
                _minSepCacheTime = float.NegativeInfinity;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _error = error;
                return false;
            }
        }
    }

    public bool SetGoal(Vec2 goal, out string error)
    {
        lock (_gate)
        {
            if (!_sim.Grid.Map.Contains(goal.X, goal.Z))
            {
                error = "目标点在地图外。";
                _error = error;
                return false;
            }

            try
            {
                _sim.SetGoal(goal);
                _minSepEver = float.MaxValue;
                _minSepCacheTime = float.NegativeInfinity;
                _error = "";
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (msg.Contains("Could only fit", StringComparison.Ordinal))
                {
                    msg = "目标空间不足：窄通道附近放不下这么多单位，请减少数量或把目标放到更开阔的位置。";
                }
                error = msg;
                _error = error;
                return false;
            }
        }
    }

    public void Step(int substeps)
    {
        lock (_gate)
        {
            int n = Math.Clamp(substeps, 1, 4);
            for (int i = 0; i < n; i++) _sim.Step(1f / 60f);
        }
    }

    public string BuildStateJson(string type)
    {
        lock (_gate)
        {
            CrowdSimulation sim = _sim;
            int n = sim.Agents.Count;
            int arrived = 0;
            for (int i = 0; i < n; i++)
            {
                Agent a = sim.Agents[i];
                if (a.State == AgentState.Arrived || a.State == AgentState.Yielding || a.ReturningToSlot)
                    arrived++;
            }

            // O(n^2) min-separation is only a HUD/telemetry value. At 1000 units
            // recomputing it at 30 Hz dominates serialization; sample it at 2 Hz.
            if (n <= 1)
            {
                _minSepCache = 0f;
                _minSepCacheTime = sim.Time;
            }
            else if (sim.Time - _minSepCacheTime >= 0.5f)
            {
                float minD2 = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    Vec2 pi = sim.Agents[i].Position;
                    for (int j = i + 1; j < n; j++)
                    {
                        float d2 = Vec2.DistanceSq(pi, sim.Agents[j].Position);
                        if (d2 < minD2) minD2 = d2;
                    }
                }
                _minSepCache = MathF.Sqrt(minD2);
                _minSepCacheTime = sim.Time;
            }
            float minSep = _minSepCache;
            if (sim.HasActiveGoal && minSep < _minSepEver) _minSepEver = minSep;
            if (sim.HasActiveGoal && arrived < n && sim.LastStepMs > 0.02)
                _lastActiveStepMs = (float)sim.LastStepMs;

            var sb = new StringBuilder(n > 600 ? 4096 * 4 : 4096);
            sb.Append("{\"type\":\"");
            sb.Append(type);
            sb.Append("\",\"t\":").Append(F(sim.Time));
            sb.Append(",\"count\":").Append(n);
            sb.Append(",\"arrived\":").Append(arrived);
            sb.Append(",\"minSep\":").Append(F(minSep));
            sb.Append(",\"minSepEver\":").Append(F(_minSepEver == float.MaxValue ? 0f : _minSepEver));
            sb.Append(",\"yields\":").Append(sim.TotalCourtesyYields);
            sb.Append(",\"reflows\":").Append(sim.ReflowCount);
            sb.Append(",\"stepMs\":").Append(F(_lastActiveStepMs));
            sb.Append(",\"pending\":").Append(sim.PendingPathPlans);
            sb.Append(",\"intervalMs\":").Append(n > 600 ? 50 : 33);
            sb.Append(",\"finalized\":").Append(sim.GroupGoalFinalized ? "true" : "false");
            sb.Append(",\"hasGoal\":").Append(sim.HasActiveGoal ? "true" : "false");
            if (sim.HasActiveGoal)
            {
                sb.Append(",\"goal\":[").Append(F(sim.GoalCenter.X)).Append(',').Append(F(sim.GoalCenter.Z)).Append(']');
                sb.Append(",\"goalRadius\":").Append(F(sim.GoalFootprintRadius));
                sb.Append(",\"goalSide\":[").Append(F(sim.GoalApproachSide.X)).Append(',')
                    .Append(F(sim.GoalApproachSide.Z)).Append(']');
                sb.Append(",\"finalizedAt\":").Append(F(sim.GroupGoalFinalizedTime));
            }
            else
            {
                sb.Append(",\"goal\":null,\"goalRadius\":0,\"finalizedAt\":-1");
            }
            sb.Append(",\"error\":\"");
            AppendEscaped(sb, _error);
            sb.Append("\",\"agents\":[");
            for (int i = 0; i < n; i++)
            {
                Agent a = sim.Agents[i];
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(F(a.Position.X)).Append(',').Append(F(a.Position.Z)).Append(',')
                    .Append((int)a.State).Append(',').Append(F(a.Velocity.Length)).Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Compact periodic state snapshot. Positions are float32 and state is a byte;
    /// 1000 units fit in ~13 KB and avoid JSON number parsing on the browser main thread.
    /// JSON is still used for world/init/reset/goal messages.
    /// </summary>
    public byte[] BuildStateBinary()
    {
        lock (_gate)
        {
            CrowdSimulation sim = _sim;
            int n = sim.Agents.Count;
            int arrived = 0;
            for (int i = 0; i < n; i++)
            {
                Agent a = sim.Agents[i];
                if (a.State == AgentState.Arrived || a.State == AgentState.Yielding || a.ReturningToSlot)
                    arrived++;
            }

            if (n <= 1)
            {
                _minSepCache = 0f;
                _minSepCacheTime = sim.Time;
            }
            else if (sim.Time - _minSepCacheTime >= 0.5f)
            {
                float minD2 = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    Vec2 pi = sim.Agents[i].Position;
                    for (int j = i + 1; j < n; j++)
                    {
                        float d2 = Vec2.DistanceSq(pi, sim.Agents[j].Position);
                        if (d2 < minD2) minD2 = d2;
                    }
                }
                _minSepCache = MathF.Sqrt(minD2);
                _minSepCacheTime = sim.Time;
            }
            if (sim.HasActiveGoal && _minSepCache < _minSepEver) _minSepEver = _minSepCache;
            if (sim.HasActiveGoal && arrived < n && sim.LastStepMs > 0.02)
                _lastActiveStepMs = (float)sim.LastStepMs;

            int intervalMs = n > 600 ? 50 : 33;
            using var ms = new MemoryStream(64 + n * 13);
            using var w = new BinaryWriter(ms);
            w.Write(0x43534E31u); // CSN1
            w.Write((byte)1);     // periodic state
            w.Write((byte)(sim.HasActiveGoal ? 1 : 0));
            w.Write((byte)(sim.GroupGoalFinalized ? 1 : 0));
            w.Write((byte)0);
            w.Write(sim.Time);
            w.Write(n);
            w.Write(arrived);
            w.Write(_minSepCache);
            w.Write(_minSepEver == float.MaxValue ? 0f : _minSepEver);
            w.Write(sim.TotalCourtesyYields);
            w.Write(sim.ReflowCount);
            w.Write(_lastActiveStepMs);
            w.Write(sim.PendingPathPlans);
            w.Write(intervalMs);
            w.Write(sim.GoalCenter.X);
            w.Write(sim.GoalCenter.Z);
            w.Write(sim.HasActiveGoal ? sim.GoalFootprintRadius : 0f);
            w.Write(sim.GoalApproachSide.X);
            w.Write(sim.GoalApproachSide.Z);
            w.Write(sim.GroupGoalFinalizedTime);
            for (int i = 0; i < n; i++)
            {
                Agent a = sim.Agents[i];
                w.Write(a.Position.X);
                w.Write(a.Position.Z);
                w.Write(a.Velocity.Length);
                w.Write((byte)a.State);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    public string BuildGoalJson(bool ok, Vec2 goal, string error)
    {
        lock (_gate)
        {
            if (!ok)
                return "{\"type\":\"goal\",\"ok\":false,\"error\":\"" + Escape(error) + "\"}";

            Vec2 center = _sim.GoalCenter;
            var sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"goal\",\"ok\":true,\"goal\":[")
                .Append(F(center.X)).Append(',').Append(F(center.Z)).Append(']');
            sb.Append(",\"goalRadius\":").Append(F(_sim.GoalFootprintRadius));
            sb.Append(",\"goalSide\":[").Append(F(_sim.GoalApproachSide.X)).Append(',')
                .Append(F(_sim.GoalApproachSide.Z)).Append(']');
            sb.Append(",\"slots\":[");
            for (int i = 0; i < _sim.GoalLayout.Slots.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Vec2 p = _sim.GoalLayout.Slots[i].Position;
                sb.Append('[').Append(F(p.X)).Append(',').Append(F(p.Z)).Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    private CrowdSimulation CreateSimulation(MapEntry map, int count)
        => CreateSimulation(map, count, _agentRadius, _maxSpeed);

    private static CrowdSimulation CreateSimulation(MapEntry map, int count,
        float agentRadius, float maxSpeed)
    {
        var config = map.Config.Clone();
        config.AgentRadius = agentRadius;
        config.MaxSpeed = maxSpeed;
        config.PathPlanFrameBudget = 2;
        // Slot spacing must scale with the unit size; otherwise shrinking units still
        // makes them park on a wide 0.9 m lattice and look scattered.
        config.SlotSpacing = MathF.Max(0.45f, agentRadius * 2f + 0.10f);
        // Open goals use the compact circular layout; only a goal that is actually
        // inside a narrow passage falls back to the connected-space queue layout.
        config.SpaceAwareGoalLayout = false;
        // In open goals, units that have entered the footprint keep pressing toward
        // the click instead of parking on a far outer slot. Narrow queue goals keep
        // their ordered slots.
        // The imported sandbox is the “walk to the portal” test: keep pressing
        // toward the click. The procedural benchmark keeps its old flow.
        config.GoalCenterAttraction = map.Id != "flagship";
        // All units share the clicked goal; one reverse Dijkstra is enough and
        // avoids 1000 independent A* searches on every command.
        config.SharedFlowField = map.Id != "flagship";
        config.GoalZoneMaxSpeed = 0f; // keep pressing toward the click
        config.UseOrca = true;
        config.OrcaSafetyMargin = 0.10f;
        // Agent radius affects clearance, so the NavGrid is rebuilt for the chosen size.
        var grid = new NavGrid(map.Map, map.Obstacles, agentRadius, 42f, 0.04f);
        var sim = new CrowdSimulation(grid, config);
        sim.SetTimeStepHint(1f / 60f);

        // For very large crowds the fixed spawn radius is too small; expand the
        // spawn search only when the requested count exceeds the normal 300 range.
        float spawnRadius = map.SpawnRadius;
        if (count > 300)
            spawnRadius = MathF.Max(spawnRadius, 0.55f * MathF.Sqrt(count) * 0.88f);
        List<Vec2> spawns = TerrainScenarios.BuildHexFormation(
            grid, map.SpawnCenter, count, 0.88f, spawnRadius);
        if (spawns.Count < count)
            throw new InvalidOperationException(
                $"地图“{map.Name}”的出生区域只能放下 {spawns.Count}/{count} 个单位。");
        sim.InitializeIdle(spawns);
        return sim;
    }

    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else sb.Append(c);
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        AppendEscaped(sb, s);
        return sb.ToString();
    }
}
