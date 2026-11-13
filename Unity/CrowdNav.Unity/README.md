# CrowdNav.Unity — 1000 单位 Jobs/Burst 核心

Unity 6000.3.18f1 工程。两条可切换后端：

- **Legacy**：`Assets/CrowdNav.Legacy`，原 `src/CrowdNav` 原样拷入，只用于基线/行为对照；
- **Jobs**：`Assets/CrowdNav.Core`（`CrowdNav.Hpc`），SoA `NativeArray` + Burst Job 图，1000 人 30/60 Hz。

显示层是一个最小 `Graphics.DrawMeshInstanced`（1000 个低模柱体，一批 draw call），不是 1000 个 `GameObject`；这样看到的 FPS 主要由仿真逻辑决定。

## 打开

1. Unity Hub 添加 `Unity/CrowdNav.Unity`，用 **6000.3.18f1** 打开；
2. 打开 `Assets/Scenes/CrowdDemo.unity`（`CrowdNav/Prepare Demo Scene` 菜单也能重建）；
3. Play。默认 1000 人、Jobs 30 Hz、播放 x4、自动去目标。

按钮：

| 按钮 | 作用 |
|---|---|
| Legacy 30 / Legacy 60 | 旧 OOP 核心，30/60 Hz |
| Jobs 30 / Jobs 60 | 新 Burst 核心，30/60 Hz（默认） |
| 去目标 | 重新下达地图建议目标 |
| 设备基准 | 依次跑 Legacy/Jobs × 30/60 Hz，写 logcat + `crowd_bench.json` |
| 播放 x1 / x2 / x4 | 播放倍速：只增加 tick 频率/仿真时间，不改变单 tick 行为；默认 x4 |
| 重置视角 | 回到整张地图的初始机位 |
| 数量 / 大小 / 速度 | 分别循环预设；改完点“应用参数”重建网格、车位和仿真 |
| 应用参数 | 100/300/500/1000 人，0.5×–2.0× 单位大小，2–6 m/s 移动速度 |

单位颜色和 Web 版一致（`#4ea8ff` 移动、`#3ddc84` 到达、`#ff5c5c` 不可达、`#ffb547` 让路）。这是 GPU instancing 的 per-instance color，一个 draw call，不是按颜色拆多个 GameObject。

地图默认做 **X 镜像**（`mirrorMapX = true`）：FBX 是 Y-up 右手系，Unity 导入会翻 X；JSON 烘焙保留了原始 FBX X，所以显示/高度场/可走格/出生点/目标点一起镜像，才能和 Unity 场景里的 FBX 对上。截图异常时可在 Inspector 关掉 `mirrorMapX` 再应用参数（或调 `CrowdMap.LoadFromText(..., mirrorX:false)`）。

手机默认请求 60 Hz 显示模式 + `Application.targetFrameRate=60`。Samsung A16 的面板支持 90 Hz（mode 1）和 60 Hz（mode 2）；如果只设 60 帧目标，90 Hz vsync 会把 60 量化成 45。工程在 Android 上用 JNI 把 `WindowManager.LayoutParams.preferredDisplayModeId = 2`（60 Hz）设到 UI 线程，HUD 稳定 60 FPS；如果机型没有 mode 2，会保留原刷新率。HUD/按钮里的 `FPS 60/90/30` 可改目标帧率，`CROWD DISPLAY ...` 日志会打印实际刷新率。

触屏/鼠标操作（和 Web 版一致）：

- **轻点地面 = 下达目标**（黄色小球标记目标；点 UI 不会误触发）；
- **单指拖动 / 左键拖动 = 旋转视角**；
- **双指拖动 / 中键拖动 = 平移**；
- **双指捏合 / 滚轮 = 缩放**。

触摸和鼠标的旋转/平移符号是**分开实现**的：触摸按“手指推动场景”的方向，鼠标保持桌面 orbit 的常规方向；两者不共用同一个 delta 符号。

HUD 显示 FPS、仿真 mean/p95、已到达、点击耗时、播放倍速、目标点、网格/出生耗时、托管堆大小。

## 首次打开包下载卡住

本机验证时遇到 Unity Package Manager 下载 `com.unity.burst` 极慢/卡住；Burst/Collections/Mathematics 已在本机 Unity 缓存里。可以临时把 manifest 的 Burst 指向本地缓存（`file:` 绝对路径，只影响本机）：

```powershell
powershell -ExecutionPolicy Bypass -File tools/use_unity_offline_burst.ps1
# 恢复 registry 版本：
powershell -ExecutionPolicy Bypass -File tools/use_unity_offline_burst.ps1 -Restore
```

正常网络环境不需要这一步。

## 构建 Android

```powershell
# 交互 Demo
& 'C:\Program Files\Unity 6000.3.18f1\Editor\Unity.exe' -batchmode -nographics `
  -projectPath Unity\CrowdNav.Unity `
  -executeMethod CrowdNav.Unity.EditorTools.UnityBuilder.BuildAndroid `
  -logFile out\unity\android_build.log

# 自动基准
& 'C:\Program Files\Unity 6000.3.18f1\Editor\Unity.exe' -batchmode -nographics `
  -projectPath Unity\CrowdNav.Unity `
  -executeMethod CrowdNav.Unity.EditorTools.UnityBuilder.BuildAndroidBench `
  -logFile out\unity\android_bench_build.log

adb install -r Unity\CrowdNav.Unity\Builds\Android\CrowdNavDemo.apk
adb logcat -c
adb shell monkey -p com.crowdnav.demo -c android.intent.category.LAUNCHER 1
adb logcat -d -s Unity:I
```

构建设置（脚本自动应用）：

- IL2CPP、ARM64、Release、Low stripping、.NET Standard 2.1；
- OpenGLES3（低端机兼容性优先）；
- 横屏、60 fps target、vSync off、`Screen.sleepTimeout = NeverSleep`；
- 包名 `com.crowdnav.demo`，min API 26。

自动基准 APK 会在 1.5 s 后依次跑 4 个配置（Legacy/Jobs × 30/60 Hz，每个 warm-up 30 tick 后测 300/600 tick），输出：

```
CROWD_BENCH mode=Legacy hz=30 ticks=300 meanMs=... p95Ms=... goalMs=... allocPerTick=... arrived=...
```

并写 `/storage/emulated/0/Android/data/com.crowdnav.demo/files/crowd_bench.json`。

## 编辑器基准

```powershell
# 旧核心 1000 人 30/60 Hz
& Unity.exe -batchmode -nographics -projectPath Unity\CrowdNav.Unity `
  -executeMethod CrowdNav.Probe.LegacyBaseline.Run -logFile out\unity\legacy_editor.log

# 新核心 1000 人 30/60 Hz
& Unity.exe -batchmode -nographics -projectPath Unity\CrowdNav.Unity `
  -executeMethod CrowdNav.Probe.CoreBenchmark.Run -logFile out\unity\jobs_editor.log
```

结果与解释见 [`docs/Unity-Jobs-Burst优化.md`](../../docs/Unity-Jobs-Burst优化.md)。

## 目录

```
Assets/
  CrowdNav.Core/           SoA + Burst Job 核心（netstandard/HPC#，不引用 UnityEngine）
  CrowdNav.Legacy/         旧 OOP 核心（基线）
  CrowdNav.App/            Demo、地图加载、设备基准
  Editor/CrowdNav.Editor/  场景/APK 构建、基准 Runner（Unity 只在 Editor 编译）
  Scenes/CrowdDemo.unity   唯一场景
  CrowdNav.App/Resources/   内城地图 JSON
  CrowdNav.App/Materials/   Standard 材质（保证 shader 不被裁剪）
Packages/manifest.json      UPM 依赖
```

## 注意

- 新核心是**实验性生产候选**：1000 人单帧预算已达标，但 courtesy yield（已到达单位让路）尚未移植，目标区压实会比旧核心慢；300 人验收阈值也还没全套搬过来。
- `CrowdNavDemo` 的 HUD/OnGUI 会有少量托管分配，不属于仿真 tick；零分配结论只针对 `CrowdNavSimulation.Tick`。
- 本工程没有改 Three.js / HTML / WebSocket；那些在仓库其它目录。
