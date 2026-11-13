using System.Text.Json;
using CrowdNav;
using CrowdNav.Scenarios;

namespace CrowdSim.Interactive;

/// <summary>
/// A playable map: height field + obstacles + a spawn area. The default map is the
/// procedural height field used by the automated tests; imported maps are baked from
/// a walkable mesh into the same regular height-field representation.
/// </summary>
public sealed class MapEntry
{
    public required string Id;
    public required string Name;
    public required string Source;
    public required HeightMap Map;
    public required NavGrid Grid;
    public required List<CircleObstacle> Obstacles;
    public required SimulationConfig Config;
    public Vec2 SpawnCenter;
    public float SpawnRadius = 10f;
    public Vec2 SuggestedGoal;
    public MapMesh? Mesh;
}

public sealed class MapMesh
{
    public float[] Vertices = Array.Empty<float>();
    public int[] Indices = Array.Empty<int>();
}

public static class MapCatalog
{
    private static readonly object Gate = new();
    private static List<MapEntry>? _all;

    public static IReadOnlyList<MapEntry> All
    {
        get
        {
            EnsureLoaded();
            return _all!;
        }
    }

    public static MapEntry Default
    {
        get
        {
            EnsureLoaded();
            return _all![0];
        }
    }

    public static MapEntry? Find(string id)
    {
        EnsureLoaded();
        foreach (MapEntry e in _all!)
            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    private static void EnsureLoaded()
    {
        if (_all != null) return;
        lock (Gate)
        {
            if (_all != null) return;
            var list = new List<MapEntry> { BuildDefault() };
            string mapDir = Path.Combine(AppContext.BaseDirectory, "Maps");
            if (!Directory.Exists(mapDir)) mapDir = Path.Combine(Directory.GetCurrentDirectory(), "Maps");
            if (Directory.Exists(mapDir))
            {
                foreach (string imported in Directory.GetFiles(mapDir, "*.json").OrderBy(p => p))
                {
                    try { list.Add(BuildImported(imported)); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("map import failed (" + Path.GetFileName(imported) + "): " + ex.Message);
                    }
                }
            }
            _all = list;
        }
    }

    private static MapEntry BuildDefault()
    {
        ScenarioSetup setup = TerrainScenarios.Flagship(120);
        return new MapEntry
        {
            Id = "flagship",
            Name = "默认：山脊与岩体",
            Source = "procedural height field",
            Map = setup.Map,
            Grid = setup.Grid,
            Obstacles = setup.Obstacles,
            Config = setup.Config,
            SpawnCenter = new Vec2(12f, 14f),
            SpawnRadius = 12f,
            SuggestedGoal = new Vec2(49f, 48f)
        };
    }

    private static MapEntry BuildImported(string path)
    {
        MapFile? f = JsonSerializer.Deserialize<MapFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (f == null || f.Width < 2 || f.Height < 2 || f.Heights == null || f.Walkable == null)
            throw new InvalidOperationException("Invalid map file.");
        if (f.Heights.Length != f.Width * f.Height || f.Walkable.Length != f.Heights.Length)
            throw new InvalidOperationException("Map array size mismatch.");
        if (f.Spawn == null || f.Spawn.Length < 2 || f.SuggestedGoal == null || f.SuggestedGoal.Length < 2)
            throw new InvalidOperationException("Map must define spawn and suggestedGoal.");

        bool[] mask = new bool[f.Walkable.Length];
        for (int i = 0; i < mask.Length; i++) mask[i] = f.Walkable[i] != 0;
        var map = new HeightMap(f.Width, f.Height, f.Cell, f.OriginX, f.OriginZ, mask);
        Array.Copy(f.Heights, map.RawHeights, f.Heights.Length);

        var config = new SimulationConfig
        {
            AgentRadius = 0.35f,
            MaxSpeed = 3.1f,
            MaxAcceleration = 15f,
            SlotSpacing = 1.0f,
            NeighborDistance = 2.0f,
            MaxNeighbors = 12,
            AvoidanceHorizon = 0.55f,
            UseOrca = true,
            OrcaSafetyMargin = 0.10f
        };
        var grid = new NavGrid(map, Array.Empty<CircleObstacle>(), config.AgentRadius, 42f, 0.04f);
        return new MapEntry
        {
            Id = f.Id,
            Name = f.Name,
            Source = f.Source,
            Map = map,
            Grid = grid,
            Obstacles = new List<CircleObstacle>(),
            Config = config,
            SpawnCenter = new Vec2(f.Spawn[0], f.Spawn[1]),
            SpawnRadius = f.SpawnRadius > 0 ? f.SpawnRadius : 10f,
            SuggestedGoal = new Vec2(f.SuggestedGoal[0], f.SuggestedGoal[1]),
            Mesh = f.Mesh == null ? null : new MapMesh
            {
                Vertices = f.Mesh.Vertices,
                Indices = f.Mesh.Indices
            }
        };
    }

    private sealed class MapFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public float Cell { get; set; }
        public float OriginX { get; set; }
        public float OriginZ { get; set; }
        public float[] Heights { get; set; } = Array.Empty<float>();
        public int[] Walkable { get; set; } = Array.Empty<int>();
        public float[] Spawn { get; set; } = Array.Empty<float>();
        public float SpawnRadius { get; set; } = 10f;
        public float[] SuggestedGoal { get; set; } = Array.Empty<float>();
        public MapMeshFile? Mesh { get; set; }
    }

    private sealed class MapMeshFile
    {
        public float[] Vertices { get; set; } = Array.Empty<float>();
        public int[] Indices { get; set; } = Array.Empty<int>();
    }
}
