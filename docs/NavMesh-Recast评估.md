# Unity NavMesh / Recast 替代单层高度场：评估与落地方案

本轮不更换导航表面，仍是“高度场网格 + 共享 Flow Field + ORCA + 顺序提交”。这份文档回答两个问题：

1. **现在的单层高度场够不够？**
2. **以后要上多层/桥/洞/大地图，Unity NavMesh 或 Recast/Detour 怎么接，才能不破坏 1000 人 Jobs/Burst 核心和跨平台一致性？**

结论先说：

- **单层、RTS 平地/缓坡/窄口地图：继续用高度场**。它便宜、可 Burst、可确定性 Bake、和当前 Job 图零摩擦。
- **多层、桥洞、室内外一体、运行时门/电梯：不要用 `NavMeshAgent` 逐单位寻路，也不要在设备上运行时 Bake。** 推荐“离线 Bake NavMesh → 导出三角形/多边形数据 → 自己的 Burst 图搜索 + 漏斗/流场 → 保留 ORCA/提交层”。
- **Recast/Detour 原生插件只在需要动态障碍、Off-Mesh Link、Tile Streaming 时才值得**；否则 Unity 自带 NavMesh 的离线数据 + 自己写三角形图搜索，工程量更小。

---

## 1. 当前方案：单层高度场

实现：`CrowdGrid`（`Assets/CrowdNav.Core/CrowdGrid.cs`）。

- 数据：`Heights`、`BaseBlocked`、`Walkable`、`Clearance`，格子中心高度 + 双线性采样；
- 可走性：边界/坡度/净空距离变换/圆形障碍（Unity 适配层把岩石/树桩栅格化成 `walkableMask`）；
- 查询：`IsWorldWalkable`（圆 vs 阻挡格精确测试）、`IsSegmentWalkable`（扫掠 + 坡度采样）、`FindNearestWalkableCell`；
- 流场：反向 Dijkstra，每个格子存 `Dist`/`Next`；
- 每单位路径：从所在格沿 `Next` 走到目标格，存 cell center；转向/ORCA/提交都在平面 (X,Z) 上。

### 1.1 实测规模

| 项目 | `neicheng_walk01` |
|---|---:|
| 格子 | 235 × 177 = 41,595 |
| 0.8 m/格 | 地图约 188 × 142 m |
| 可走格（半径 0.35 m） | 10,069 |
| 高度范围 | 0–17.8 m |
| 高度场 + 阻挡 + 可走 + 净空 | `41,595×(4+1+1+4)` ≈ 416 KB |
| 流场 Dist/Next/堆 | ≈ 832 KB |
| Bake（Editor，距离变换 + 坡度） | 41–62 ms |
| 流场重建（Burst，后台 IJob） | 约 2–5 ms |

### 1.2 优点

- **便宜**：41k 格，查询是几个格子循环；Burst 后单帧 1.5 ms 量级。
- **确定性**：Bake 只依赖输入高度/掩码和固定参数；同一份数据在不同平台得到相同 `Walkable`/`Clearance`（浮点距离变换在同版本下一致）。
- **和 ORCA/提交零阻抗**：静态线段检查直接读 `BaseBlocked`；不需要把每个单位塞进 `NavMeshAgent`。
- **流场天然共享**：1000 人去同一点，一次 Dijkstra，不是 1000 次 A*。
- **可运行时改**：单位半径/速度变了，重建 `CrowdGrid` 即可；门/动态障碍可以改 `walkableMask` 后重建流场。

### 1.3 限制

- **单层**：桥、洞、上下层、室内楼梯间会互相“压平”；高度场每 (x,z) 只能有一个高度。
- **格子分辨率**：0.8 m 格 + 0.35 m 半径，窄口/台阶边缘会有栅格化误差；更细的格子增加内存/流场成本。
- **没有 Off-Mesh Link**：跳、梯子、传送门要额外做。
- **没有 Area Cost**：现在只用坡度/上坡代价；不能区分马路/草地/沼泽。
- **大地图**：格子数按面积增长；500×500 m / 0.5 m 格就是 1M 格，流场/内存都需要分块。

---

## 2. 候选方案对比

| 方案 | 多层 | 1000 人 tick 成本 | 与 Burst/Jobs | 跨平台一致性 | 工程成本 | 适用 |
|---|---|---|---|---|---|---|
| **A. 继续单层高度场** | ❌ | 最低（当前 1.5 ms） | 原生契合 | 最高 | 0 | 平地/缓坡/窄口 RTS |
| **B. Unity NavMesh 离线 Bake + 三角剖分数据** | ✅ | 低（自写三角图搜索） | 数据可转 NativeArray，搜索可 Burst | 高（固定 Unity 版本离线 Bake，数据随包） | 中 | 多层、桥洞、美术导航网格 |
| **C. Unity `NavMeshAgent` 逐单位** | ✅ | ❌ 1000 个 Agent + 内部避让 + Transform，远超预算 | ❌ 内部 C++，不可 Burst/Job 化 | 中 | 低（接入快） | 少量单位、非 RTS |
| **D. Recast/Detour 原生插件** | ✅ | 低–中（Detour 查询 C++） | 需要 C#/Native 绑定，Job 集成复杂 | 高（自管数据/版本） | 高 | 动态障碍、Tile Streaming、生产级大地图 |
| **E. 多层高度场堆叠** | ⚠️ 手工 | 低 | 契合 | 高 | 中 | 少量固定楼层、无桥洞 |

**排除 C**：Unity 自带 `NavMeshAgent` 的避让/推挤和本项目“绝不推别人、顺序提交、目标车位”的语义冲突；1000 个 Agent 的 `Transform`/`NavMeshAgent` 更新也不是 Burst 路径能接的。

**排除 E**：层与层之间的连接（楼梯/桥洞）要手工维护，和“多平台一致”目标相比不划算，除非地图只有 2–3 个完全分离的固定层。

---

## 3. 推荐路线：Unity NavMesh 离线 Bake → 三角形图 → 自己的 Burst 搜索

### 3.1 数据从哪来

两条可行的数据源：

1. **`NavMesh.CalculateTriangulation()`**
   - Unity 自带 API，返回 `vertices`/`indices`（世界空间三角形）；
   - 覆盖烘焙出的所有 NavMesh 表面（多层/桥/洞都可以）；
   - 不包含 Area/Off-Mesh Link/Polygon 元数据；只有几何。
2. **`UnityEngine.Experimental.AI.NavMeshQuery` / `NavMeshLocation` / `PolygonId`**
   - 能拿到多边形级信息（区域、邻接、边界）；
   - API 标记 Experimental，跨 Unity 版本稳定性不如三角剖分；
   - 需要把多边形邻接自己转成 NativeArray。

建议：

- **第一版用 `NavMesh.CalculateTriangulation()`**：简单、稳定、足够做多层流场；
- 需要 Area Cost/Off-Mesh Link 时再评估 `NavMeshQuery` 或 Recast/Detour。

### 3.2 运行时数据结构

```csharp
struct CrowdNavMesh
{
    NativeArray<float3> Vertices;      // x,z + y 用于渲染/高度
    NativeArray<int3>   Triangles;     // 每个三角形 3 个顶点索引
    NativeArray<int3>   Adjacency;     // 每条边的邻接三角形（-1 = 边界）
    NativeArray<int3>   PortalEdge;    // 邻接边在对面的顶点对
    NativeArray<float>  TriangleCost;  // 面积/区域代价
    NativeArray<float3> Centroids;
}
```

构建（离线/加载时，一次）：

1. 读三角形，按顶点对（排序后的 `(min,max)`）建边 → 三角形映射；
2. 每条边最多 2 个三角形；>2 视为非流形，报错/拆分；
3. 写入 `Adjacency`/`PortalEdge`；
4. 校验：三角形绕序、退化面积、孤立三角形、边界数量。

### 3.3 流场 / 路径

- **共享流场**：以目标三角形为源，在三角形邻接图上做 Dijkstra/A*；每个三角形存 `NextTriangle` 与 `Cost`。
- **每单位路径**：
  - 起点：`NavMesh.SamplePosition` 或自己找最近三角形（离线网格可以做 BVH/网格哈希）；
  - 沿 `NextTriangle` 走到目标三角形，收集经过的 portal 边；
  - 用 **Simple Stupid Funnel Algorithm** 把 portal 序列压成拐点；
  - 把拐点写进现有 `CrowdAgentStore.Path`（同样扁平 `NativeArray<float2>`），后续 `CrowdSteerJob`/ORCA/提交完全不用改。
- **目标点/车位**：仍用现有 `CrowdGoalData`；车位生成需要“三角形上最近点 + 半径净空”，可以把三角形的内切圆/边界距离预计算成 `Clearance`，或把目标区投影到高度场做局部车位。
- **高度**：`CrowdGrid.SampleHeight` 换成三角形重心插值/BVH 查询；渲染层用三角形网格本身，仿真层只需要 (x,z)。

### 3.4 为什么这条路线和 1000 人兼容

- 三角形图搜索是**一次/目标点击**，不是每单位每 tick；1000 人共享同一流场。
- 每单位只做“沿 NextTriangle 走 + funnel 拐点”，可以 Burst 并行；拐点数量远小于 cell path（通常 5–20），路径内存从 4 MB 降到 < 1 MB。
- ORCA/提交层只依赖平面位置、半径、静态线段可走性；把 `IsSegmentWalkable` 改成“三角形间扫掠/高度场局部采样”即可，不需要改 ORCA 数学。
- 多层连接由 NavMesh 烘焙保证；不需要在 ORCA 里处理 Y。

### 3.5 跨平台一致性

这是“保证多平台一致”的核心，不是“运行时在每台设备上 Bake”：

1. **固定 Unity 版本 + 固定 Bake 参数**（agent radius/height/slope/step height/min region area），在 CI/编辑器离线 Bake；
2. 导出 `navmesh.bin`（vertices/indices/区域/版本/hash）到 `StreamingAssets`，随包发布；
3. 运行时只加载二进制 → NativeArray，**绝不在设备上重新 Bake**（Recast Bake 在低端机上可能几百 ms 到数秒，且不同 CPU/浮点可能产生不同三角形）；
4. 启动时校验文件 hash/版本；如果和客户端版本不匹配，直接报错而不是悄悄用错网格；
5. 服务器/客户端回放：使用**同一份导出的 navmesh.bin**，不要各自 Bake；
6. 浮点：三角形图搜索的代价用 double 或固定量化；跨 Windows/Android 的 Burst 浮点可能有极小差异，路径回放验收用容差（例如 1e-3 m），不要要求 bit-exact。

---

## 4. Recast/Detour 原生插件：什么时候值得

### 4.1 适合

- 需要 **Tile Streaming**（大地图分块加载）；
- 需要 **动态障碍**（门、车辆、可破坏物）和 `dtNavMesh` 的 tile 更新；
- 需要 **Off-Mesh Link**（跳、梯子、传送）；
- 需要和服务器/其他引擎共享 Recast 数据格式；
- 团队能维护 Android arm64 / Windows 原生插件和 C# 绑定。

### 4.2 代价

- 每个平台一份 `.so`/`.dll`（Android arm64、Windows、可能的 iOS）；
- C# 侧要写 blittable struct 绑定，且要避免每帧 `Marshal`/托管分配；
- Detour 查询本身是 C++，无法直接进入 Burst Job；只能：
  - 主线程/后台线程调用 Detour 查询，再把结果拷进 NativeArray；或
  - 把 Detour 的流场/路径结果预计算成 NativeArray，ORCA 仍在 Burst 里；
- 构建管线、符号、ABI、崩溃调试成本明显高于“Unity 离线 Bake + 自写三角形图”；
- 需要自己保证 Recast 版本/参数固定，才能跨平台一致。

### 4.3 建议

- **现在不做**。当前地图是单层高度场，Recast 的收益抵不过插件/绑定成本。
- **触发条件**：出现“多层 + 动态门/桥 + 大地图分块”三个需求中的两个，再立项。
- 届时优先用 **Recast 离线 CLI/Editor 工具** 生成 tile 数据，运行时只读；避免在设备上 `dtNavMesh` 重建。

---

## 5. 落地步骤（建议排期）

### Phase 1（当前，已完成）

- 单层高度场 + Burst 流场 + ORCA/提交；
- 1000 人 Unity Editor 1.5 ms/tick、点击目标 0.7 ms；
- 设备基线/自动基准 APK。

### Phase 2（多层地图，1–2 周）

1. `NavMeshSurface` / `NavMeshBuilder` 离线 Bake 一张带桥/洞的测试图；
2. Editor 工具导出 `navmesh.bin`（vertices/indices/hash/version）；
3. 运行时 `CrowdNavMesh` 加载 + 邻接构建 Job；
4. 三角形 Dijkstra 流场 + funnel 路径 Job；
5. 把 `CrowdSteerJob` 的 `Grid.IsSegmentWalkable` 换成 `CrowdNavMesh` 版本；
6. 同一套 ORCA/提交/车位；用 200/500 人验收无重叠、全部到达；
7. 在 Windows/Android 加载同一 `navmesh.bin`，校验 hash 和路径容差。

### Phase 3（只有需要时才做）

- Recast/Detour 插件、Tile Streaming、动态障碍；
- 或者 `NavMeshQuery` 多边形级 Area/Off-Mesh Link 接入；
- 目标：仍然保持“共享流场 + 每单位只读路径 + ORCA 并行 + 顺序提交”的 Job 图。

---

## 6. 结论摘要

| 问题 | 结论 |
|---|---|
| 现在要换 NavMesh 吗？ | **不换**。单层地图高度场更快、更确定、和 Burst/Jobs 更契合。 |
| 多层地图怎么办？ | 离线 Bake Unity NavMesh → 导出三角形 → 自写 Burst 三角形图流场 + funnel；不要用 `NavMeshAgent`。 |
| 要不要上 Recast/Detour？ | 只有动态障碍/Tile Streaming/Off-Mesh Link 成为硬需求时才上；否则工程成本大于收益。 |
| 怎么保证多平台一致？ | **离线 Bake、随包发布、运行时只读**；固定 Unity/Recast 版本与参数；启动校验 hash；跨端验收用容差而不是 bit-exact。 |
| 和当前 1000 人核心的关系 | ORCA/提交/车位层完全复用；只替换“导航表面 + 路径层”，Job 图不变。 |
