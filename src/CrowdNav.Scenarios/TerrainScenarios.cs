using System;
using System.Collections.Generic;
using CrowdNav;

namespace CrowdNav.Scenarios
{
    public sealed class ScenarioSetup
    {
        public string Id = "";
        public string Title = "";
        public string Description = "";
        public HeightMap Map = null!;
        public NavGrid Grid = null!;
        public List<CircleObstacle> Obstacles = new List<CircleObstacle>();
        public SimulationConfig Config = new SimulationConfig();
        public Vec2 Goal;
        public readonly List<Vec2> Spawns = new List<Vec2>();
        public readonly List<Vec2> LateSpawns = new List<Vec2>();
        public float LateTime = float.PositiveInfinity;
        public bool HasGoalSwitch;
        public float GoalSwitchTime = float.PositiveInfinity;
        public Vec2 NewGoal;
        public float TimeoutSeconds = 90f;
        public float Dt = 1f / 60f;
    }

    public static class TerrainScenarios
    {
        public const int MapSize = 128;
        public const float CellSize = 0.5f;

        public static ScenarioSetup Create(string id, int unitCount)
        {
            switch (id)
            {
                case "flagship": return Flagship(unitCount);
                case "packing": return Packing(unitCount);
                case "dense": return Flagship(Math.Max(unitCount, 150));
                case "flat": return Flat(unitCount);
                case "late":
                {
                    int initial = Math.Max(40, unitCount - 40);
                    ScenarioSetup s = Flagship(initial);
                    WithLateWave(s, unitCount - initial, 60f);
                    s.TimeoutSeconds = 240f;
                    return s;
                }
                default: throw new ArgumentException("Unknown scenario: " + id);
            }
        }

        public static ScenarioSetup Flagship(int unitCount = 120)
        {
            HeightMap map = BuildHeightMap();
            var obstacles = new List<CircleObstacle>
            {
                new CircleObstacle(new Vec2(24.5f, 20.0f), 3.3f),
                new CircleObstacle(new Vec2(38.5f, 27.0f), 3.8f),
                new CircleObstacle(new Vec2(22.5f, 42.0f), 3.0f)
            };

            var config = new SimulationConfig
            {
                AgentRadius = 0.35f,
                MaxSpeed = 3.1f,
                MaxAcceleration = 15f,
                SlotSpacing = 1.0f,
                NeighborDistance = 2.0f,
                MaxNeighbors = 12,
                AvoidanceHorizon = 0.55f
            };
            var grid = new NavGrid(map, obstacles, config.AgentRadius, 42f, 0.04f);
            Vec2 goal = new Vec2(49.0f, 48.0f);

            var setup = new ScenarioSetup
            {
                Id = "flagship",
                Title = $"主场景：穿过起伏地形，{unitCount} 人集体到达高地",
                Description = $"{unitCount} 个单位从西侧谷地出发，翻越山脊、绕过三处岩体，抵达东侧高地平台。",
                Map = map,
                Grid = grid,
                Obstacles = obstacles,
                Config = config,
                Goal = goal,
                TimeoutSeconds = 120f
            };

            List<Vec2> spawns = BuildHexFormation(grid, new Vec2(12f, 14f), unitCount, 0.88f, 8.5f);
            setup.Spawns.AddRange(spawns);
            return setup;
        }

        public static ScenarioSetup Packing(int unitCount = 160)
        {
            HeightMap map = BuildHeightMap();
            var obstacles = new List<CircleObstacle>
            {
                new CircleObstacle(new Vec2(24.5f, 20.0f), 3.3f),
                new CircleObstacle(new Vec2(38.5f, 27.0f), 3.8f)
            };
            var config = new SimulationConfig
            {
                AgentRadius = 0.35f,
                MaxSpeed = 2.7f,
                MaxAcceleration = 14f,
                SlotSpacing = 1.0f,
                NeighborDistance = 1.9f,
                MaxNeighbors = 12
            };
            var grid = new NavGrid(map, obstacles, config.AgentRadius, 42f, 0.04f);
            Vec2 goal = new Vec2(49.0f, 48.0f);
            var setup = new ScenarioSetup
            {
                Id = "packing",
                Title = $"目标车位压力：{unitCount} 人填满高地平台",
                Description = "更短的行军距离，重点验证目标区容量、外圈车位和到达精度。",
                Map = map,
                Grid = grid,
                Obstacles = obstacles,
                Config = config,
                Goal = goal,
                TimeoutSeconds = 140f
            };
            setup.Spawns.AddRange(BuildHexFormation(grid, new Vec2(36f, 40f), unitCount, 0.88f, 9f));
            return setup;
        }

        /// <summary>Diagnostic arena: no obstacles, flat ground. Isolates crowd flow.</summary>
        public static ScenarioSetup Flat(int unitCount = 120)
        {
            var map = new HeightMap(MapSize, MapSize, CellSize, 0f, 0f);
            var obstacles = new List<CircleObstacle>();
            var config = new SimulationConfig
            {
                AgentRadius = 0.35f,
                MaxSpeed = 3.1f,
                MaxAcceleration = 15f,
                SlotSpacing = 1.0f,
                NeighborDistance = 2.0f,
                MaxNeighbors = 12
            };
            var grid = new NavGrid(map, obstacles, config.AgentRadius, 42f, 0.04f);
            Vec2 goal = new Vec2(42f, 32f);
            var setup = new ScenarioSetup
            {
                Id = "flat",
                Title = $"诊断场景：平地 {unitCount} 人，无静态障碍",
                Description = "隔离测试局部避让和落地阶段。",
                Map = map,
                Grid = grid,
                Obstacles = obstacles,
                Config = config,
                Goal = goal,
                TimeoutSeconds = 120f
            };
            setup.Spawns.AddRange(BuildHexFormation(grid, new Vec2(15f, 32f), unitCount, 0.88f, 9f));
            return setup;
        }

        public static ScenarioSetup WithLateWave(ScenarioSetup baseSetup, int lateCount = 40, float lateTime = 60f)
        {
            List<Vec2> late = BuildHexFormation(baseSetup.Grid, new Vec2(12f, 14f), lateCount, 0.88f, 7f);
            baseSetup.LateSpawns.Clear();
            baseSetup.LateSpawns.AddRange(late);
            baseSetup.LateTime = lateTime;
            baseSetup.Id = baseSetup.Id + "-late";
            baseSetup.Title = $"迟到增援：先 {baseSetup.Spawns.Count} 人到达，再追加 {lateCount} 人";
            baseSetup.Description = "第一队先在目标区域压实；随后追加单位，整队重新分配车位，前面的人主动挪动腾出空间。";
            return baseSetup;
        }

        public static HeightMap BuildHeightMap()
        {
            var map = new HeightMap(MapSize, MapSize, CellSize, 0f, 0f);
            map.Fill(HeightFunction);
            return map;
        }

        public static float HeightFunction(float x, float z)
        {
            float h = 0.55f * MathF.Sin(x * 0.17f) * MathF.Cos(z * 0.14f)
                      + 0.35f * MathF.Sin((x + z) * 0.23f)
                      + 3.10f * MathF.Exp(-(Sq(x - 31f) / 95f + Sq(z - 34f) / 240f))
                      + 1.20f * MathF.Exp(-(Sq(x - 18f) / 42f + Sq(z - 45f) / 90f));

            float ds = Dist(x, z, 12f, 14f);
            h = MathUtil.Lerp(h, 0.25f, MathUtil.SmoothStep(10f, 3f, ds));

            float dg = Dist(x, z, 49f, 48f);
            h = MathUtil.Lerp(h, 2.62f, MathUtil.SmoothStep(9f, 4.2f, dg));
            return h;
        }

        /// <summary>Hex-packed spawn footprint, guaranteeing no initial overlap.</summary>
        public static List<Vec2> BuildHexFormation(NavGrid grid, Vec2 center, int count,
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
                    int q = ring;
                    int r = -ring;
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

                ringPoints.Sort((a, b) =>
                {
                    float aa = MathF.Atan2(a.Z - center.Z, a.X - center.X);
                    float bb = MathF.Atan2(b.Z - center.Z, b.X - center.X);
                    return aa.CompareTo(bb);
                });

                for (int i = 0; i < ringPoints.Count && result.Count < count; i++)
                {
                    Vec2 p = ringPoints[i];
                    if (Vec2.Distance(p, center) > maxRadius) continue;
                    if (!grid.IsWorldWalkable(p)) continue;
                    bool free = true;
                    for (int j = 0; j < result.Count; j++)
                    {
                        if (Vec2.DistanceSq(p, result[j]) < sep * sep * 0.98f)
                        {
                            free = false;
                            break;
                        }
                    }
                    if (free) result.Add(p);
                }
            }

            if (result.Count < count)
                throw new InvalidOperationException($"Could only place {result.Count}/{count} spawns.");
            return result;
        }

        private static float Dist(float x, float z, float cx, float cz)
        {
            float dx = x - cx;
            float dz = z - cz;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        private static float Sq(float v) => v * v;
    }
}
