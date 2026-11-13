# tools

## Unity / Android（当前阶段）

| 文件 | 作用 |
|---|---|
| `build_unity_android.ps1` | 构建交互 APK；仓库路径含非 ASCII 时自动复制到 ASCII 临时目录再构建 |
| `run_unity_device_bench.ps1` | 构建 `CROWD_AUTOBENCH` APK、安装、等跑完、拉 logcat/JSON |
| `install_unity_android.ps1` | 安装并启动已构建的 APK |
| `use_unity_offline_burst.ps1` | 本机 Burst 包下载卡住时，把 manifest 临时指向 Unity 缓存；`-Restore` 恢复 |
| `sync_legacy_unity.ps1` | 把 `src/CrowdNav` 单向同步到 `Assets/CrowdNav.Legacy`；`-Check` 只检查漂移（CI 可用） |

## 地图 / 可视化

| 文件 | 作用 |
|---|---|
| `fbx_to_map.py` | 把 ASCII FBX 烘焙成 `neicheng_walk01.json`（高度场 + walkable + 原始三角面） |
| `render_visuals.py` / `bundle_visualizer.py` | 预录回放/截图/GIF 生成 |
| `analyze_backtrack.py` | 轨迹回退分析 |
| `check_links.py` | 静态链接检查 |

## Web 原型探针（归档，不参与 Unity 核心）

`web/` 下的 `cdp_*.mjs` / `ws_*.mjs` 是早期 Three.js + WebSocket 原型的浏览器/服务端调试与性能探针；Unity 1000 人核心不依赖它们，也没有修改 WebSocket JSON 协议。
