# Unity 接入指南

核心库 `src/CrowdNav` 不引用 `UnityEngine`，目标框架是 `netstandard2.1`，可以直接把 `.cs` 文件拷入 Unity 工程。下面是推荐接入方式。

> 注意：`Unity/CrowdRunner.cs` 是样板，未在 Unity 工程里编译；不同 Unity 版本的 API 可能需要微调。仿真核心已经在本仓库的 .NET 测试中验证。

---

## 1. 文件放置

```
Assets/
  CrowdNav/                 <- src/CrowdNav/*.cs
  CrowdRunner.cs            <- Unity/CrowdRunner.cs
  Prefabs/Agent.prefab
```

`CrowdNav` 只依赖 `System`、`System.Collections.Generic`、`System.Diagnostics`，不依赖 `System.Numerics` 或 Unity 的 `Vector3`，因此不会和 Unity 的类型冲突。

---

## 2. 构建高度场

```csharp
TerrainData data = terrain.terrainData;
Vector3 origin = terrain.transform.position;
float cell = 0.5f;
int w = Mathf.CeilToInt(data.size.x / cell);
int h = Mathf.CeilToInt(data.size.z / cell);

var map = new HeightMap(w, h, data.size.x / w, 0f, 0f);
for (int z = 0; z < h; z++)
for (int x = 0; x < w; x++)
{
    float wx = origin.x + (x + 0.5f) * cell;
    float wz = origin.z + (z + 0.5f) * cell;
    map.SetHeight(x, z, terrain.SampleHeight(new Vector3(wx, 0f, wz)));
}
```

- 所有仿真坐标使用 **地形局部 XZ**；点击时减去 `origin`。
- 地形高度只存在 `HeightMap`；单位 Y 在渲染时采样。
- 若地形有 `TerrainLayer`/树/石头，建议用 `Terrain.SampleHeight` + 额外碰撞体射线；当前 `NavGrid` 只处理高度坡度 + 圆形障碍。

---

## 3. 障碍

```csharp
var obstacles = new List<CircleObstacle>
{
    new CircleObstacle(new Vec2(24.5f, 20f), 3.3f),
    // center 是地形局部 XZ
};
var grid = new NavGrid(map, obstacles, agentRadius: 0.35f, maxSlopeDegrees: 42f);
```

圆形障碍物适合岩石、树桩、建筑基座。复杂多边形建议：

1. 在构建 `NavGrid` 前把多边形栅格化成不可走格；
2. 或把静态几何交给 Recast/Detour，保留本项目的目标车位/ORCA/提交层。

---

## 4. 创建仿真

```csharp
var config = new SimulationConfig
{
    AgentRadius = 0.35f,
    MaxSpeed = 3.1f,
    MaxAcceleration = 15f,
    SlotSpacing = 1.0f,
    UseOrca = true,
    OrcaTimeHorizon = 1.5f,
    OrcaSafetyMargin = 0.10f,
    FormationPreservingAssignment = true,
    PathPlanFrameBudget = 2,
    NeighborDistance = 2.0f,
    MaxNeighbors = 12
};

var sim = new CrowdSimulation(grid, config);
var spawns = new List<Vec2>(); // 每个单位的初始 XZ，必须互相不重叠
sim.Initialize(new Vec2(goalX, goalZ), spawns);
```

`Initialize` 会：

- 校验初始重叠（有重叠直接抛异常）；
- 生成目标车位、分配；
- 把路径请求入队（不一次性卡住主线程）。

如果单位是持续生成的，用：

```csharp
List<Agent> added = (List<Agent>)sim.AddAgents(newSpawns);
```

注意 `AddAgents` 返回 `IReadOnlyList<Agent>`；内部处理出生点找空位、扩车位、必要时整队重排。

---

## 5. 固定步长推进

```csharp
float accumulator;
const float dt = 1f / 60f;

void Update()
{
    accumulator += Mathf.Min(0.1f, Time.deltaTime);
    int guard = 0;
    while (accumulator >= dt && guard++ < 4)
    {
        sim.Step(dt);
        accumulator -= dt;
    }
    SyncViews();
}
```

**不要**直接用可变 `Time.deltaTime` 调 `Step`：局部避让参数、制动距离和硬提交都按固定步长设计。帧率波动时用累加器追帧；卡顿超过 0.1 s 时限制最大追帧数，避免 spiral of death。

---

## 6. 同步 Transform

```csharp
void SyncViews()
{
    for (int i = 0; i < views.Count; i++)
    {
        Agent a = sim.Agents[i];
        float y = map.SampleHeight(a.Position.X, a.Position.Z);
        views[i].position = new Vector3(a.Position.X + originX, y + 0.02f, a.Position.Z + originZ);

        Vector3 dir = new Vector3(a.Velocity.X, 0f, a.Velocity.Z);
        if (dir.sqrMagnitude > 0.0001f)
            views[i].rotation = Quaternion.LookRotation(dir, Vector3.up);
    }
}
```

- 单位的位置在仿真中是连续浮点，不需要 `CharacterController` 或物理刚体；否则物理引擎会引入额外推挤。
- 如果必须用 `CharacterController` 做碰撞，建议只把它作为视觉/查询，不要让它修改 `Agent.Position`。
- 需要更平滑的渲染，可以缓存上一 tick 位置和当前 tick 位置，按累加器比例插值；但**不要**在仿真位置之外再做位置修正。

---

## 7. 点击目标

```csharp
if (Input.GetMouseButtonDown(0))
{
    if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
    if (Physics.Raycast(ray, out var hit, 500f, groundMask))
    {
        sim.SetGoal(new Vec2(hit.point.x - originX, hit.point.z - originZ));
    }
}
```

- 目标点落在不可走格时，`GoalLayout.Build` 会自动找最近可走点；
- 目标区域容量不足时（例如悬崖边一小块地），`GoalLayout.Build` 会抛出“无法放下 N 个车位”的异常。业务层应提示“目标区域太小”，而不是让单位卡死。
- 如果地图有 UI/小地图，点击小地图时不要重复触发地面射线。

---

## 8. 性能设置（手游）

| 参数 | 桌面建议 | 手游建议 | 说明 |
|---|---:|---:|---|
| `PathPlanFrameBudget` | 2 | 1 | 每帧规划路径数；越小越不卡，但单位启动越晚 |
| `MaxNeighbors` | 12 | 8 | 密集时邻居上限 |
| `NeighborDistance` | 2.0 | 1.6 | 越小越省，但避让视野变短 |
| 网格分辨率 | 0.5 m | 0.75–1.0 m | 大地图优先降分辨率或换 Recast |
| `OrcaTimeHorizon` | 1.5 | 1.2–1.5 | 太短会急停，太长会保守 |
| `OrcaSafetyMargin` | 0.10 | 0.08–0.12 | 太小会有硬截断，太大通过性下降 |
| 渲染 | 插值 | 插值 + LOD/GPU Instancing | 100+ 单位不要每帧 SetPosition 多次 |

### 每帧开销构成

1. `UpdatePathsAndDesires`：路径跟随、速度计算；
2. 空间哈希重建；
3. ORCA 邻居/线性规划；
4. 顺序提交 + 扫掠检查；
5. 到达/让路/压实。

在本机 120 人稳态 p95 约 0.47 ms；200 人约 0.52 ms；300 人约 1.28 ms。移动端请用目标机 Profiler 重新测。

目标落点默认使用 `FormationPreservingAssignment = true`：按队伍相对队形把车位整体映射到目标圆内，避免后排单位被分到圆环远侧而绕人群。目标区仍是整圆（300 人约 12 m 半径）；需要旧的按环分配时改回 `false`。

### 常见卡顿来源

- 点击时一次性规划全部路径（本项目已用 `PathPlanFrameBudget` 分片）；
- `GetComponent`/`Instantiate` 在 tick 内；
- 每帧 LINQ、字符串、`new List`；
- 用 `NavMeshAgent` + 物理推挤，导致双重避让；
- 在 `Update` 中直接改 `Agent.Position` 后再让物理引擎碰撞。

---

## 9. 与 Unity NavMesh 的关系

本方案不要求 Unity NavMesh。若项目已经有大世界 NavMesh，可以分两层：

- **路径层**：用 NavMesh + `NavMesh.CalculatePath` / Detour 得到路径走廊；
- **局部层**：保留本项目的 ORCA、目标车位、顺序提交、让路。

这样既利用 Unity 的 NavMesh 烘焙，又保留“不重叠、不推挤、到达区压实”的行为。

---

## 10. 调试建议

- `OnDrawGizmos` 画出车位的 `GoalLayout.Slots`、目标半径；
- 把 `Agent.State` 颜色化（Moving / Arrived / Yielding / Unreachable）；
- 打开 `CrowdSimulation.HardClampCount`、`TotalCourtesyYields`、`LastPlanMs`、`LastStepMs`；
- 如果单位在某个拐角集体停住，先看是不是 `HardClampCount` 暴涨（局部避让不够保守），再考虑把 `OrcaSafetyMargin` 提高到 0.12；
- 如果单位在目标附近长时间绕圈，先确认 `GoalFootprintRadius` 是否足够，以及目标区是否真的能放下这么多胶囊。
