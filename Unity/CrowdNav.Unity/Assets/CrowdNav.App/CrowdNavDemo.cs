using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CrowdNav;
using CrowdNav.Hpc;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace CrowdNav.Unity
{
    /// <summary>
    /// Phone-facing demo and device benchmark. Two interchangeable backends:
    ///   Legacy  - the original managed CrowdSimulation (baseline);
    ///   Jobs    - SoA NativeArrays + Burst job graph.
    /// Rendering is deliberately one instanced draw batch so the measured frame cost is
    /// dominated by crowd logic, not 1000 GameObjects.
    /// </summary>
    public sealed class CrowdNavDemo : MonoBehaviour
    {
        private enum SimMode { Legacy = 0, Jobs = 1 }

        public TextAsset mapAsset;
        public int unitCount = 1000;
        public int targetHz = 30;
        public bool autoStart = true;
        public Material unitMaterial;
        public Material groundMaterial;
        public Material walkableMaterial;
        public bool mirrorMapX = true;

        [Header("Playback")]
        [Range(1, 4)] public int playbackSpeed = 4;
        public int targetFps = 0; // 0 = unlimited / platform max

        [Header("Crowd parameters")]
        [Range(0.1f, 2f)] public float unitSize = 1f;
        [Range(1f, 6f)] public float moveSpeed = 3.1f;

        [Header("Grid debug / rebake")]
        public bool showWalkable;
        public bool showGridPanel;
        public bool followUnitRadius = true;
        public float bakeRadius = 0.35f;
        public float maxSlope = 42f;
        public float clearanceMargin = 0.04f;
        private float _agentRadius = 0.35f;
        private float _slotSpacing = 0.8f;
        private float _renderScale = 1f;
        private string _status = "";
        private string _labelCount = "";
        private string _labelSize = "";
        private string _labelSpeed = "";
        private string _labelFps = "";
        private string _labelPlay = "";
        private string _labelWalkable = "";
        private int _gc0Start;
        private bool _stressRunning;
        private string _stressStatus = "";

        private CrowdMap _map;
        private CrowdSimulation _legacy;
        private CrowdNavSimulation _jobs;
        private CrowdGridData _gridData;
        private GameObject _walkableGo;
        private Mesh _walkableMesh;
        private MeshRenderer _walkableRenderer;
        private Material _walkableMaterialInstance;
        private float EffectiveBakeRadius => followUnitRadius ? _agentRadius : Mathf.Max(0.05f, bakeRadius);
        private List<Vec2> _spawnList;
        private NativeArray<float2> _jobSpawns;
        private SimMode _mode;
        private int _hz;
        private int _count;

        private Mesh _unitMesh;
        private Material _unitMaterial;
        private readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();
        private readonly Vector4[] _instanceColors = new Vector4[1024];
        private static readonly Color[] StateColors =
        {
            new Color(0x4e / 255f, 0xa8 / 255f, 0xff / 255f), // Moving
            new Color(0x3d / 255f, 0xdc / 255f, 0x84 / 255f), // Arrived
            new Color(0xff / 255f, 0x5c / 255f, 0x5c / 255f), // Unreachable
            new Color(0xff / 255f, 0xb5 / 255f, 0x47 / 255f)  // Yielding
        };
        private Camera _camera;
        private readonly Matrix4x4[] _matrices = new Matrix4x4[1024];
        private readonly double[] _tickMs = new double[240];
        private int _tickCursor;
        private int _tickSamples;
        private float _accumulator;
        private float _alpha;
        private double _lastTickMs;
        private double _p95Ms;
        private double _meanMs;
        private float _fps;
        private float _hudTimer;
        private string _hud = "";
        private long _tick;
        private double _goalMs;
        private double _spawnMs;
        private double _gridMs;
        private bool _started;
        private bool _benchRunning;
        private string _benchMessage = "";
        private readonly List<string> _benchLines = new List<string>();
        private GUIStyle _labelStyle;
        private int _simSpeed = 4;
        private Transform _goalMarker;
        private Vector2 _goalXz;

        // Simple orbit/pan/zoom rig, matching the web demo's controls.
        private Vector3 _camTarget;
        private float _camYaw;
        private float _camPitch = 48f;
        private float _camDistance = 120f;
        private bool _pointerDown;
        private bool _pointerMoved;
        private bool _pointerOverUi;
        private Vector2 _pointerDownPos;
        private Vector2 _pointerPrevPos;
        private float _pointerDownTime;
        private float _lastPinchDistance;

        private void Start()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Screen.orientation = ScreenOrientation.LandscapeLeft;
            Application.targetFrameRate = targetFps <= 0 ? MaxRefreshRate() : targetFps;
            QualitySettings.vSyncCount = 0;
            _gc0Start = GC.CollectionCount(0);
            _simSpeed = Mathf.Clamp(playbackSpeed, 1, 4);
            _agentRadius = 0.35f * Mathf.Clamp(unitSize, 0.1f, 2f);
            _slotSpacing = Mathf.Max(0.20f, 2f * _agentRadius + 0.10f);
            _renderScale = _agentRadius / 0.35f;
            _goalXz = Vector2.zero;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.52f);
            if (mapAsset == null)
            {
                Debug.LogError("CrowdNavDemo: mapAsset not assigned");
                enabled = false;
                return;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _map = CrowdMap.LoadFromText(mapAsset.text, EffectiveBakeRadius, mirrorMapX,
                    maxSlope, clearanceMargin);
                sw.Stop();
                _gridMs = sw.Elapsed.TotalMilliseconds;
                BuildVisuals();
                BuildSpawns();
                Restart(SimMode.Jobs, targetHz);
                _started = true;
                if (autoStart) SetGoalNow();
                RefreshUiLabels();
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "CROWD DEMO ready units={0} gridMs={1:0.0} spawnMs={2:0.0} goalMs={3:0.0}",
                    _count, _gridMs, _spawnMs, _goalMs));
            }
            catch (Exception ex)
            {
                Debug.LogError("CrowdNavDemo init failed: " + ex);
                enabled = false;
                return;
            }
#if CROWD_AUTOBENCH
            StartCoroutine(RunDeviceBenchmark(true));
#endif
        }

        private void RefreshWalkableOverlay()
        {
            if (!showWalkable || _mode != SimMode.Jobs || _gridData == null)
            {
                if (_walkableGo != null) _walkableGo.SetActive(false);
                return;
            }
            BuildWalkableOverlay(_gridData.Grid);
        }

        private void ToggleWalkable()
        {
            showWalkable = !showWalkable;
            RefreshUiLabels();
            RefreshWalkableOverlay();
            _status = showWalkable
                ? (_mode == SimMode.Jobs ? "可走区：开（青色覆盖；青色格子=按当前半径可走）" : "可走区：仅 Jobs 模式显示")
                : "可走区：关";
        }

        private void BuildWalkableOverlay(CrowdGrid grid)
        {
            if (_walkableMaterialInstance == null) return;

            if (_walkableGo == null)
            {
                _walkableGo = new GameObject("WalkableOverlay");
                _walkableGo.AddComponent<MeshFilter>();
                _walkableRenderer = _walkableGo.AddComponent<MeshRenderer>();
                _walkableRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _walkableRenderer.receiveShadows = false;
            }
            if (_walkableMesh != null)
            {
                Destroy(_walkableMesh);
                _walkableMesh = null;
            }

            int estimated = 0;
            for (int i = 0; i < grid.CellCount; i++) if (grid.Walkable[i] != 0) estimated++;
            if (estimated == 0)
            {
                _walkableGo.SetActive(false);
                return;
            }

            var vertices = new List<Vector3>(estimated * 4);
            var colors = new List<Color>(estimated * 4);
            var triangles = new List<int>(estimated * 6);
            float cell = grid.CellSize;
            Color color = new Color(0.10f, 0.95f, 1.0f, 0.28f);

            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int i = z * grid.Width + x;
                    if (grid.Walkable[i] == 0) continue;

                    float y = grid.Heights[i] + 0.06f;
                    float x0 = grid.OriginX + x * cell;
                    float z0 = grid.OriginZ + z * cell;
                    float x1 = x0 + cell;
                    float z1 = z0 + cell;
                    int v = vertices.Count;
                    vertices.Add(new Vector3(x0, y, z0));
                    vertices.Add(new Vector3(x1, y, z0));
                    vertices.Add(new Vector3(x1, y, z1));
                    vertices.Add(new Vector3(x0, y, z1));
                    colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
                    triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 1);
                    triangles.Add(v); triangles.Add(v + 3); triangles.Add(v + 2);
                }
            }

            var mesh = new Mesh { name = "WalkableOverlay" };
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
            _walkableMesh = mesh;
            _walkableGo.GetComponent<MeshFilter>().sharedMesh = mesh;
            _walkableRenderer.sharedMaterial = _walkableMaterialInstance;
            _walkableGo.SetActive(true);
        }

        private void BuildSpawns()
        {
            _count = Mathf.Clamp(unitCount, 10, 20000);
            float spacing = Mathf.Max(0.88f * _renderScale, 2f * _agentRadius + 0.10f);
            float spawnRadius = Mathf.Max(_map.SpawnRadius, 0.62f * Mathf.Sqrt(_count) * spacing);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _spawnList = BuildHexFormation(_map.Grid, _map.SpawnCenter, _count, spacing, spawnRadius);
            if (_spawnList.Count == 0)
                throw new InvalidOperationException("spawn area cannot fit any unit at this size");
            if (_spawnList.Count < _count)
            {
                _status = string.Format(CultureInfo.InvariantCulture,
                    "出生区只放下 {0}/{1} 人（单位太大），先用 {0} 人", _spawnList.Count, _count);
                _count = _spawnList.Count;
            }
            if (_jobSpawns.IsCreated) _jobSpawns.Dispose();
            _jobSpawns = new NativeArray<float2>(_count, Allocator.Persistent);
            for (int i = 0; i < _count; i++) _jobSpawns[i] = new float2(_spawnList[i].X, _spawnList[i].Z);
            sw.Stop();
            _spawnMs = sw.Elapsed.TotalMilliseconds;
        }

        private void Restart(SimMode mode, int hz)
        {
            _mode = mode;
            _hz = Mathf.Clamp(hz, 1, 120);
            if (_legacy != null) _legacy = null;
            if (_jobs != null) { _jobs.Dispose(); _jobs = null; }
            if (_gridData != null) { _gridData.Dispose(); _gridData = null; }
            _accumulator = 0f;
            _alpha = 0f;
            _tick = 0;
            _tickSamples = 0;
            _tickCursor = 0;
            _lastTickMs = 0;
            _p95Ms = 0;
            _meanMs = 0;
            _goalMs = 0;

            if (mode == SimMode.Legacy)
            {
                var config = new SimulationConfig
                {
                    AgentRadius = _agentRadius,
                    MaxSpeed = moveSpeed,
                    MaxAcceleration = 15f * (moveSpeed / 3.1f),
                    SlotSpacing = _slotSpacing,
                    PathPlanFrameBudget = 2,
                    GoalCenterAttraction = true,
                    SharedFlowField = true,
                    GoalZoneMaxSpeed = 0f,
                    UseOrca = true,
                    OrcaSafetyMargin = 0.10f,
                    SpaceAwareGoalLayout = false,
                    GoalFootprintPadding = 1.0f
                };
                _legacy = new CrowdSimulation(_map.Grid, config);
                _legacy.SetTimeStepHint(1f / _hz);
                _legacy.InitializeIdle(_spawnList);
            }
            else
            {
                var config = CrowdConfig.Default;
                config.GoalCenterAttraction = true;
                config.AgentRadius = _agentRadius;
                config.MaxSpeed = moveSpeed;
                config.MaxAcceleration = 15f * (moveSpeed / 3.1f);
                config.SlotSpacing = _slotSpacing;
                config.PredictedCommit = false;
                config.MaxNeighbors = 14;
                config.NeighborDistance = 2.0f;
                _gridData = CrowdGridData.Build(_map.Json.heights, ToBool(_map.Json.walkable),
                    _map.Json.width, _map.Json.height, _map.Json.cell, _map.Json.originX,
                    _map.Json.originZ, EffectiveBakeRadius, maxSlope, clearanceMargin);
                _jobs = new CrowdNavSimulation(_gridData, config, _jobSpawns,
                    new float2(_map.SuggestedGoal.X, _map.SuggestedGoal.Z), false);
            }
            _started = true;
            RefreshWalkableOverlay();
        }

        private void SetGoalNow()
        {
            SetGoalAt(_map.SuggestedGoal.X, _map.SuggestedGoal.Z);
        }

        /// <summary>Click/tap a walkable floor point to command the whole crowd there.</summary>
        private void SetGoalAt(float x, float z)
        {
            _goalXz = new Vector2(x, z);
            if (_goalMarker != null)
            {
                float y = _map.HeightMap.SampleHeight(x, z);
                _goalMarker.position = new Vector3(x, y + 0.35f, z);
                _goalMarker.gameObject.SetActive(true);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_mode == SimMode.Legacy)
                _legacy.SetGoal(new Vec2(x, z));
            else
                _jobs.SetGoal(new float2(x, z));
            sw.Stop();
            _goalMs = sw.Elapsed.TotalMilliseconds;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "CROWD GOAL mode={0} hz={1} goal=({2:0.0},{3:0.0}) goalMs={4:0.00}",
                _mode, _hz, x, z, _goalMs));
            _tickSamples = 0;
            _tickCursor = 0;
        }

        private bool IsGuiTop(Vector2 screenPoint)
        {
            float guiY = Screen.height - screenPoint.y;
            return guiY < (showGridPanel ? 820f : 540f) || guiY > Screen.height - 70f; // HUD/controls/panel/legend
        }

        private void HandlePointer()
        {
            if (_benchRunning) return;
            if (Input.touchCount > 0)
            {
                HandleTouch();
                return;
            }
            HandleMouse();
        }

        private void HandleTouch()
        {
            if (Input.touchCount >= 2)
            {
                _pointerMoved = true;
                Touch a = Input.GetTouch(0);
                Touch b = Input.GetTouch(1);
                Vector2 avg = (a.position + b.position) * 0.5f;
                float pinch = Vector2.Distance(a.position, b.position);
                if (_lastPinchDistance <= 0.01f)
                {
                    _pointerPrevPos = avg;
                    _lastPinchDistance = pinch;
                    return;
                }
                if (!_pointerOverUi)
                {
                    PanTouch(avg - _pointerPrevPos);
                    if (pinch > 1f) Zoom(_lastPinchDistance / pinch);
                }
                _pointerPrevPos = avg;
                _lastPinchDistance = pinch;
                return;
            }

            Touch touch = Input.GetTouch(0);
            switch (touch.phase)
            {
                case TouchPhase.Began:
                    _pointerDown = true;
                    _pointerMoved = false;
                    _pointerOverUi = IsGuiTop(touch.position);
                    _pointerDownPos = touch.position;
                    _pointerPrevPos = touch.position;
                    _pointerDownTime = Time.unscaledTime;
                    _lastPinchDistance = 0f;
                    break;
                case TouchPhase.Moved:
                    if (_pointerDown && !_pointerOverUi)
                    {
                        if (!_pointerMoved &&
                            (touch.position - _pointerDownPos).sqrMagnitude > 24f * 24f)
                            _pointerMoved = true;
                        if (_pointerMoved) OrbitTouch(touch.position - _pointerPrevPos);
                        _pointerPrevPos = touch.position;
                    }
                    break;
                case TouchPhase.Ended:
                case TouchPhase.Canceled:
                    if (_pointerDown && !_pointerMoved && !_pointerOverUi &&
                        Time.unscaledTime - _pointerDownTime < 0.55f)
                        TrySetGoalFromScreen(touch.position);
                    _pointerDown = false;
                    _pointerMoved = false;
                    _lastPinchDistance = 0f;
                    break;
            }
        }

        private void HandleMouse()
        {
            Vector2 mouse = Input.mousePosition;
            bool overUi = IsGuiTop(mouse);
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) Zoom(1f - scroll * 0.10f);

            if (Input.GetMouseButtonDown(0) && !overUi)
            {
                _pointerDown = true;
                _pointerMoved = false;
                _pointerOverUi = false;
                _pointerDownPos = mouse;
                _pointerPrevPos = mouse;
                _pointerDownTime = Time.unscaledTime;
            }

            if (Input.GetMouseButton(0) && _pointerDown && !_pointerOverUi)
            {
                if (!_pointerMoved && (mouse - _pointerDownPos).sqrMagnitude > 8f * 8f)
                    _pointerMoved = true;
                if (_pointerMoved) OrbitMouse(mouse - _pointerPrevPos);
            }
            else if (Input.GetMouseButton(1) && !overUi)
            {
                OrbitMouse(mouse - _pointerPrevPos);
            }
            else if (Input.GetMouseButton(2) && !overUi)
            {
                PanMouse(mouse - _pointerPrevPos);
            }

            if (Input.GetMouseButtonUp(0))
            {
                if (_pointerDown && !_pointerMoved && !_pointerOverUi &&
                    Time.unscaledTime - _pointerDownTime < 0.55f)
                    TrySetGoalFromScreen(mouse);
                _pointerDown = false;
                _pointerMoved = false;
            }
            _pointerPrevPos = mouse;
        }

        // Touch and mouse deliberately do NOT share sign conventions: on a touchscreen,
        // dragging up should feel like pushing the scene up; on desktop, dragging up with
        // the mouse is the inverse. Keep the two paths separate.
        private void OrbitTouch(Vector2 delta)
        {
            _camYaw += delta.x * 0.18f;
            _camPitch = Mathf.Clamp(_camPitch - delta.y * 0.18f, 16f, 78f);
            UpdateCameraTransform();
        }

        private void OrbitMouse(Vector2 delta)
        {
            _camYaw += delta.x * 0.18f;
            _camPitch = Mathf.Clamp(_camPitch + delta.y * 0.18f, 16f, 78f);
            UpdateCameraTransform();
        }

        private void PanTouch(Vector2 delta)
        {
            if (_camera == null) return;
            Vector3 right = Vector3.ProjectOnPlane(_camera.transform.right, Vector3.up).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(_camera.transform.forward, Vector3.up).normalized;
            float scale = _camDistance * 0.0015f;
            _camTarget -= right * (delta.x * scale);
            _camTarget -= forward * (delta.y * scale);
            ClampCameraTarget();
            UpdateCameraTransform();
        }

        private void PanMouse(Vector2 delta)
        {
            if (_camera == null) return;
            Vector3 right = Vector3.ProjectOnPlane(_camera.transform.right, Vector3.up).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(_camera.transform.forward, Vector3.up).normalized;
            float scale = _camDistance * 0.0015f;
            _camTarget -= right * (delta.x * scale);
            _camTarget += forward * (delta.y * scale);
            ClampCameraTarget();
            UpdateCameraTransform();
        }

        private void Zoom(float factor)
        {
            _camDistance = Mathf.Clamp(_camDistance * factor, 28f, 220f);
            UpdateCameraTransform();
        }

        private void ClampCameraTarget()
        {
            float w = _map != null ? _map.Json.width * _map.Json.cell : 200f;
            float h = _map != null ? _map.Json.height * _map.Json.cell : 160f;
            _camTarget.x = Mathf.Clamp(_camTarget.x, -10f, w + 10f);
            _camTarget.z = Mathf.Clamp(_camTarget.z, -10f, h + 10f);
        }

        private void UpdateCameraTransform()
        {
            if (_camera == null) return;
            Quaternion rotation = Quaternion.Euler(_camPitch, _camYaw, 0f);
            _camera.transform.rotation = rotation;
            _camera.transform.position = _camTarget - rotation * Vector3.forward * _camDistance;
        }

        private void ResetView()
        {
            float cx = _map.Json.width * _map.Json.cell * 0.5f;
            float cz = _map.Json.height * _map.Json.cell * 0.5f;
            _camTarget = new Vector3(cx, 8f, cz);
            _camYaw = 0f;
            _camPitch = 48f;
            _camDistance = 120f;
            UpdateCameraTransform();
        }

        private void TrySetGoalFromScreen(Vector2 screenPoint)
        {
            if (_camera == null || _map == null) return;
            if (IsGuiTop(screenPoint)) return;
            Ray ray = _camera.ScreenPointToRay(screenPoint);
            if (Physics.Raycast(ray, out RaycastHit hit, 600f, ~0, QueryTriggerInteraction.Ignore))
                SetGoalAt(hit.point.x, hit.point.z);
        }

        private void Update()
        {
            if (!_started || _benchRunning || _stressRunning) return;
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.08f);
            HandlePointer();
            UpdateCameraTransform();

            float dt = 1f / _hz;
            _accumulator += Mathf.Min(0.15f, Time.unscaledDeltaTime) * _simSpeed;
            int steps = 0;
            while (_accumulator >= dt && steps < 8)
            {
                double ms = TickOnce(dt);
                RecordTick(ms);
                _accumulator -= dt;
                _tick++;
                steps++;
            }
            _alpha = Mathf.Clamp01(_accumulator / dt);

            _hudTimer += Time.unscaledDeltaTime;
            if (_hudTimer >= 1f)
            {
                _hudTimer = 0f;
                UpdateHud();
            }
            RenderCrowd(_alpha);
        }

        private double TickOnce(float dt)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_mode == SimMode.Legacy) _legacy.Step(dt);
            else _jobs.Tick(dt);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            _lastTickMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return _lastTickMs;
        }

        private void RecordTick(double ms)
        {
            _tickMs[_tickCursor] = ms;
            _tickCursor = (_tickCursor + 1) % _tickMs.Length;
            if (_tickSamples < _tickMs.Length) _tickSamples++;
        }

        private void UpdateHud()
        {
            double sum = 0;
            for (int i = 0; i < _tickSamples; i++) sum += _tickMs[i];
            _meanMs = _tickSamples > 0 ? sum / _tickSamples : 0;
            var sorted = new double[_tickSamples];
            Array.Copy(_tickMs, sorted, _tickSamples);
            Array.Sort(sorted);
            _p95Ms = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.95)] : 0;
            int arrived = _mode == SimMode.Legacy ? _legacy.ArrivedCount() : _jobs.ArrivedCount();
            _hud = string.Format(CultureInfo.InvariantCulture,
                "FPS {0:0.0}   {1} {2}Hz   SIM mean {3:0.00} ms  p95 {4:0.00} ms   play x{5}\n" +
                "units {6}   arrived {7}   tick {8}   goal {9:0.0} ms   target ({10:0.0},{11:0.0})\n" +
                "grid {12:0.0} ms   spawn {13:0.0} ms   HEAP {14:0} KB   GC0 {15}   tap floor = new goal",
                _fps, _mode, _hz, _meanMs, _p95Ms, _simSpeed, _count, arrived, _tick, _goalMs,
                _goalXz.x, _goalXz.y, _gridMs, _spawnMs, GC.GetTotalMemory(false) / 1024,
                GC.CollectionCount(0) - _gc0Start);
        }

        private void RenderCrowd(float alpha)
        {
            if (_unitMesh == null) return;
            int count = _count;
            int drawn = 0;
            while (drawn < count)
            {
                int batch = Mathf.Min(1024, count - drawn);
                for (int i = 0; i < batch; i++)
                {
                    float x, z, yaw;
                    int state;
                    if (_mode == SimMode.Legacy)
                    {
                        Agent a = _legacy.Agents[drawn + i];
                        x = a.Position.X; z = a.Position.Z; yaw = a.Facing * Mathf.Rad2Deg;
                        state = (int)a.State;
                    }
                    else
                    {
                        int idx = drawn + i;
                        float2 p = math.lerp(_jobs.Agents.PrevPosition[idx],
                            _jobs.Agents.Position[idx], alpha);
                        x = p.x; z = p.y; yaw = _jobs.Agents.Facing[idx] * Mathf.Rad2Deg;
                        state = _jobs.Agents.State[idx];
                    }
                    _instanceColors[i] = StateColors[Mathf.Clamp(state, 0, 3)];
                    float y = _map.HeightMap.SampleHeight(x, z);
                    _matrices[i] = Matrix4x4.TRS(
                        new Vector3(x, y + 0.8f * _renderScale, z),
                        Quaternion.Euler(0f, yaw + 90f, 0f),
                        new Vector3(0.7f * _renderScale, 1.6f * _renderScale, 0.7f * _renderScale));
                }
                _propertyBlock.SetVectorArray("_Color", _instanceColors);
                Graphics.DrawMeshInstanced(_unitMesh, 0, _unitMaterial, _matrices, batch, _propertyBlock,
                    UnityEngine.Rendering.ShadowCastingMode.Off, false, 0, _camera,
                    UnityEngine.Rendering.LightProbeUsage.Off, null);
                drawn += batch;
            }
        }

        private void OnGUI()
        {
            if (_labelStyle == null) _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 30 };
            GUI.Label(new Rect(24, 24, 1500, 140), _hud, _labelStyle);
            if (_benchRunning)
            {
                GUI.Label(new Rect(24, 180, 1800, 80), _benchMessage, _labelStyle);
                return;
            }
            if (_stressRunning)
            {
                GUI.Label(new Rect(24, 180, 2200, 100), _stressStatus, _labelStyle);
                return;
            }
            float y = 190;
            if (GUI.Button(new Rect(24, y, 240, 80), "Legacy 30")) RestartAndGoal(SimMode.Legacy, 30);
            if (GUI.Button(new Rect(280, y, 240, 80), "Legacy 60")) RestartAndGoal(SimMode.Legacy, 60);
            if (GUI.Button(new Rect(536, y, 240, 80), "Jobs 30")) RestartAndGoal(SimMode.Jobs, 30);
            if (GUI.Button(new Rect(792, y, 240, 80), "Jobs 60")) RestartAndGoal(SimMode.Jobs, 60);
            if (GUI.Button(new Rect(1048, y, 240, 80), "去目标")) SetGoalNow();
            if (GUI.Button(new Rect(1304, y, 300, 80), "设备基准"))
                StartCoroutine(RunDeviceBenchmark(false));
            if (GUI.Button(new Rect(1620, y, 260, 80), _labelPlay))
            {
                _simSpeed = _simSpeed >= 4 ? 1 : _simSpeed * 2;
                RefreshUiLabels();
            }
            if (GUI.Button(new Rect(1900, y, 300, 80), "重置视角")) ResetView();

            float y2 = 280;
            GUI.Label(new Rect(24, y2, 600, 30), "数量 " + unitCount);
            int newCount = Mathf.RoundToInt(GUI.HorizontalSlider(
                new Rect(24, y2 + 30, 600, 28), unitCount, 10f, 20000f));
            if (newCount != unitCount)
            {
                unitCount = newCount;
                RefreshUiLabels();
            }

            GUI.Label(new Rect(640, y2, 420, 30), "大小 x" + unitSize.ToString("0.00"));
            float newSize = GUI.HorizontalSlider(
                new Rect(640, y2 + 30, 420, 28), unitSize, 0.1f, 2f);
            if (Mathf.Abs(newSize - unitSize) > 0.001f)
            {
                unitSize = newSize;
                RefreshUiLabels();
            }

            GUI.Label(new Rect(1080, y2, 360, 30), "速度 " + moveSpeed.ToString("0.0") + " m/s");
            float newSpeed = GUI.HorizontalSlider(
                new Rect(1080, y2 + 30, 360, 28), moveSpeed, 1f, 6f);
            if (Mathf.Abs(newSpeed - moveSpeed) > 0.001f)
            {
                moveSpeed = newSpeed;
                RefreshUiLabels();
            }

            if (GUI.Button(new Rect(1460, y2, 300, 70), "应用参数/重烘")) ApplyParameters();
            if (GUI.Button(new Rect(1780, y2, 240, 70), _labelFps)) CycleTargetFps();
            if (GUI.Button(new Rect(2040, y2, 240, 70), "网格参数")) showGridPanel = !showGridPanel;

            float y3 = 360;
            if (GUI.Button(new Rect(24, y3, 260, 60), _labelWalkable)) ToggleWalkable();
            if (GUI.Button(new Rect(300, y3, 260, 60), "极限测试")) StartCoroutine(RunStressTest());

            if (showGridPanel) DrawGridPanel();

            GUI.Label(new Rect(24, 435, 1100, 50), _status, GUI.skin.label);
            GUI.Label(new Rect(24, 480, 2200, 60),
                "拖动=旋转  双指/中键=平移  捏合/滚轮=缩放  轻点地面=目标  播放倍速只加 tick 频率",
                GUI.skin.label);

            float ly = Screen.height - 48f;
            DrawLegend(new Rect(24, ly, 210, 40), "● 移动", StateColors[0]);
            DrawLegend(new Rect(240, ly, 210, 40), "● 到达", StateColors[1]);
            DrawLegend(new Rect(456, ly, 210, 40), "● 不可达", StateColors[2]);
            DrawLegend(new Rect(672, ly, 210, 40), "● 让路", StateColors[3]);
        }

        private void DrawGridPanel()
        {
            var r = new Rect(1160, 450, 1120, 350);
            GUI.Box(r, "网格参数（改完点应用并重烘）");
            float x = r.x + 24f;
            float y = r.y + 48f;

            followUnitRadius = GUI.Toggle(new Rect(x, y, 360, 40), followUnitRadius, "跟随单位半径（推荐）");
            y += 52f;

            float radius = followUnitRadius ? _agentRadius : bakeRadius;
            GUI.Label(new Rect(x, y, 230, 40), string.Format(CultureInfo.InvariantCulture,
                "烘焙半径 {0:0.00} m", radius));
            bool oldEnabled = GUI.enabled;
            GUI.enabled = !followUnitRadius;
            float newRadius = GUI.HorizontalSlider(new Rect(x + 250, y + 12, 430, 30), radius, 0.10f, 1.0f);
            GUI.enabled = oldEnabled;
            if (!followUnitRadius) bakeRadius = newRadius;
            y += 50f;

            GUI.Label(new Rect(x, y, 230, 40), string.Format(CultureInfo.InvariantCulture,
                "最大坡度 {0:0}°", maxSlope));
            maxSlope = GUI.HorizontalSlider(new Rect(x + 250, y + 12, 430, 30), maxSlope, 5f, 60f);
            y += 50f;

            GUI.Label(new Rect(x, y, 230, 40), string.Format(CultureInfo.InvariantCulture,
                "净空余量 {0:0.00} m", clearanceMargin));
            clearanceMargin = GUI.HorizontalSlider(new Rect(x + 250, y + 12, 430, 30), clearanceMargin, 0f, 0.3f);
            y += 56f;

            if (GUI.Button(new Rect(x, y, 280, 56), "应用并重烘")) ApplyParameters();
            if (GUI.Button(new Rect(x + 300, y, 180, 56), "关闭")) showGridPanel = false;
            GUI.Label(new Rect(x + 500, y + 4, 540, 50),
                "青色覆盖 = 当前走格；重烘会重置人群到出生区", GUI.skin.label);
        }

        private void DrawLegend(Rect rect, string text, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.Label(rect, text, GUI.skin.label);
            GUI.color = old;
        }

        private void RefreshUiLabels()
        {
            _labelCount = "数量 " + unitCount;
            _labelSize = "大小 x" + unitSize.ToString("0.0");
            _labelSpeed = "速度 " + moveSpeed.ToString("0.0");
            _labelFps = "FPS " + (targetFps <= 0 ? "不限" : targetFps.ToString());
            _labelPlay = "播放 x" + _simSpeed;
            _labelWalkable = "可走区 " + (showWalkable ? "开" : "关");
        }

        private static int MaxRefreshRate()
        {
            float hz = (float)Screen.currentResolution.refreshRateRatio.value;
            int value = Mathf.RoundToInt(hz);
            return value >= 30 ? value : 60;
        }

        private void CycleCount()
        {
            int[] values = { 100, 300, 500, 1000 };
            int idx = Array.IndexOf(values, Mathf.Clamp(unitCount, 10, 1000));
            unitCount = values[idx < 0 ? 3 : (idx + 1) % values.Length];
            _status = "数量待应用：" + unitCount + "（点“应用参数”）";
            RefreshUiLabels();
        }

        private void CycleSize()
        {
            float[] values = { 0.5f, 1f, 1.5f, 2f };
            int idx = Array.FindIndex(values, v => Mathf.Abs(v - unitSize) < 0.01f);
            unitSize = values[idx < 0 ? 1 : (idx + 1) % values.Length];
            _status = "大小待应用：x" + unitSize.ToString("0.0") + "（点“应用参数”）";
            RefreshUiLabels();
        }

        private void CycleSpeed()
        {
            float[] values = { 2f, 3.1f, 4.5f, 6f };
            int idx = Array.FindIndex(values, v => Mathf.Abs(v - moveSpeed) < 0.01f);
            moveSpeed = values[idx < 0 ? 1 : (idx + 1) % values.Length];
            _status = "速度待应用：" + moveSpeed.ToString("0.0") + " m/s（点“应用参数”）";
            RefreshUiLabels();
        }

        private void CycleTargetFps()
        {
            int[] values = { 0, 90, 60, 30 };
            int idx = Array.IndexOf(values, targetFps);
            targetFps = values[idx < 0 ? 0 : (idx + 1) % values.Length];
            Application.targetFrameRate = targetFps <= 0 ? MaxRefreshRate() : targetFps;
            QualitySettings.vSyncCount = 0;
            _status = targetFps <= 0
                ? "帧率目标：不限（按屏幕最高刷新率）"
                : "帧率目标：" + targetFps + "（90 Hz 面板上 60 可能被 vsync 量化成 45）";
            Debug.Log("CROWD FPS target=" + (targetFps <= 0 ? "unlimited(" + MaxRefreshRate() + ")" : targetFps.ToString()));
            RefreshUiLabels();
        }

        private void ApplyParameters()
        {
            try
            {
                unitCount = Mathf.Clamp(unitCount, 10, 20000);
                unitSize = Mathf.Clamp(unitSize, 0.1f, 2f);
                moveSpeed = Mathf.Clamp(moveSpeed, 1f, 6f);
                _agentRadius = 0.35f * unitSize;
                if (followUnitRadius) bakeRadius = _agentRadius;
                _slotSpacing = Mathf.Max(0.20f, 2f * _agentRadius + 0.10f);
                _renderScale = _agentRadius / 0.35f;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _map = CrowdMap.LoadFromText(mapAsset.text, EffectiveBakeRadius, mirrorMapX,
                    maxSlope, clearanceMargin);
                BuildSpawns();
                Restart(_mode, _hz);
                sw.Stop();
                SetGoalAt(_map.SuggestedGoal.X, _map.SuggestedGoal.Z);
                _status = string.Format(CultureInfo.InvariantCulture,
                    "已应用：{0} 人，大小 x{1:0.0}，烘焙半径 {2:0.00} m，坡度 {3:0}°，净空 {4:0.00} m，速度 {5:0.0} m/s，重烘 {6:0} ms",
                    _count, unitSize, EffectiveBakeRadius, maxSlope, clearanceMargin, moveSpeed,
                    sw.Elapsed.TotalMilliseconds);
                if (_count < unitCount) _status += "（出生区只放下 " + _count + "/" + unitCount + "）";
                if (_count > 10000) _status += "（真实地图高密度：帧率可能很低）";
                RefreshUiLabels();
                Debug.Log("CROWD PARAMS " + _status);
            }
            catch (Exception ex)
            {
                _status = "参数应用失败：" + ex.Message;
                Debug.LogError("CROWD PARAMS FAIL " + ex);
            }
        }

        private void RestartAndGoal(SimMode mode, int hz)
        {
            Restart(mode, hz);
            SetGoalNow();
        }

        private struct StressResult
        {
            public int Units;
            public double MeanMs;
            public double P95Ms;
            public double MaxMs;
            public long AllocBytes;
        }

        private IEnumerator RunStressTest()
        {
            if (_stressRunning) yield break;
            _stressRunning = true;
            int[] counts = { 1000, 2000, 4000, 8000, 10000, 12000, 16000, 20000, 24000, 32000 };
            for (int i = 0; i < counts.Length; i++)
            {
                int count = counts[i];
                _stressStatus = string.Format(CultureInfo.InvariantCulture,
                    "极限测试 {0}/{1}：{2} 单位（不渲染）...", i + 1, counts.Length, count);
                yield return null;

                StressResult result = RunStressCount(count);
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "CROWD_STRESS units={0} meanMs={1:0.00} p95Ms={2:0.00} maxMs={3:0.00} allocBytes={4}",
                    result.Units, result.MeanMs, result.P95Ms, result.MaxMs, result.AllocBytes));

                _stressStatus = string.Format(CultureInfo.InvariantCulture,
                    "极限测试 {0}/{1}：{2} 单位 mean {3:0.00} / p95 {4:0.00} ms",
                    i + 1, counts.Length, count, result.MeanMs, result.P95Ms);
                yield return null;
            }
            _stressRunning = false;
            _status = "极限测试完成，结果见 CROWD_STRESS 日志";
            Debug.Log("CROWD_STRESS_DONE");
        }

        private StressResult RunStressCount(int count)
        {
            const int width = 384;
            const int height = 256;
            const float cell = 0.8f;
            float[] heights = new float[width * height];
            bool[] walkable = new bool[width * height];
            for (int i = 0; i < walkable.Length; i++) walkable[i] = true;

            using (var grid = CrowdGridData.Build(heights, walkable, width, height, cell, 0f, 0f,
                       0.35f, 42f, 0.04f))
            {
                int side = Mathf.CeilToInt(Mathf.Sqrt(count));
                const float spacing = 0.85f;
                var spawns = new NativeArray<float2>(count, Allocator.Persistent);
                for (int i = 0; i < count; i++)
                {
                    int row = i / side;
                    int col = i % side;
                    spawns[i] = new float2(10f + col * spacing, 10f + row * spacing);
                }

                var config = CrowdConfig.Default;
                config.AgentRadius = 0.35f;
                config.MaxSpeed = 3.1f;
                config.MaxAcceleration = 15f;
                config.SlotSpacing = 0.8f;
                config.GoalCenterAttraction = true;
                config.PredictedCommit = false;
                config.MaxNeighbors = 14;
                config.NeighborDistance = 2f;

                var sim = new CrowdNavSimulation(grid, config, spawns, new float2(260f, 100f), true);
                try
                {
                    sim.CompleteFlowNow();
                    for (int i = 0; i < 30; i++) sim.Tick(1f / 30f);

                    long alloc0 = SafeAllocatedBytes();
                    int ticks = count >= 16000 ? 60 : 90;
                    var samples = new double[ticks];
                    var sw = new System.Diagnostics.Stopwatch();
                    for (int i = 0; i < ticks; i++)
                    {
                        sw.Restart();
                        sim.Tick(1f / 30f);
                        sw.Stop();
                        samples[i] = sw.Elapsed.TotalMilliseconds;
                    }
                    long alloc = SafeAllocatedBytes() - alloc0;
                    Array.Sort(samples);
                    double mean = 0;
                    for (int i = 0; i < samples.Length; i++) mean += samples[i];
                    mean /= samples.Length;
                    return new StressResult
                    {
                        Units = count,
                        MeanMs = mean,
                        P95Ms = Pct(samples, 95),
                        MaxMs = samples[samples.Length - 1],
                        AllocBytes = alloc
                    };
                }
                finally
                {
                    sim.Dispose();
                    spawns.Dispose();
                }
            }
        }

        private IEnumerator RunDeviceBenchmark(bool quitWhenDone)
        {
            _benchRunning = true;
            int previousSpeed = _simSpeed;
            _simSpeed = 1;
            _benchMessage = "benchmark warmup...";
            _benchLines.Clear();
            Debug.Log("CROWD_BENCH_START");
            yield return new WaitForSeconds(1.5f);

            var modes = new[] { SimMode.Legacy, SimMode.Jobs };
            var rates = new[] { 30, 60 };
            foreach (SimMode mode in modes)
            {
                foreach (int hz in rates)
                {
                    _benchMessage = "bench " + mode + " " + hz + "Hz";
                    Restart(mode, hz);
                    SetGoalNow();
                    yield return null;

                    float dt = 1f / hz;
                    int warmup = 30;
                    for (int i = 0; i < warmup; i++) TickOnce(dt);
                    int ticks = hz == 30 ? 300 : 600;
                    var samples = new double[ticks];
                    long heap0 = GC.GetTotalMemory(false);
                    long alloc0 = SafeAllocatedBytes();
                    for (int i = 0; i < ticks; i++)
                    {
                        samples[i] = TickOnce(dt);
                        _tick++;
                    }
                    long alloc = SafeAllocatedBytes() - alloc0;
                    long heap = GC.GetTotalMemory(false) - heap0;
                    Array.Sort(samples);
                    double mean = 0;
                    for (int i = 0; i < ticks; i++) mean += samples[i];
                    mean /= ticks;
                    int arrived;
                    int settled;
                    if (mode == SimMode.Legacy)
                    {
                        arrived = _legacy.ArrivedCount();
                        settled = _legacy.GroupGoalFinalized ? 1 : 0;
                    }
                    else
                    {
                        arrived = _jobs.ArrivedCount();
                        settled = _jobs.Stats[CrowdStatsIndex.Settled];
                    }
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "CROWD_BENCH mode={0} hz={1} ticks={2} meanMs={3:0.000} p50Ms={4:0.000} " +
                        "p95Ms={5:0.000} maxMs={6:0.000} goalMs={7:0.00} allocPerTick={8:0.0} " +
                        "heapDeltaKB={9:0.0} arrived={10} settled={11} units={12}",
                        mode, hz, ticks, mean, Pct(samples, 50), Pct(samples, 95),
                        samples[samples.Length - 1], _goalMs, (double)alloc / ticks,
                        heap / 1024.0, arrived, settled, _count);
                    Debug.Log(line);
                    _benchLines.Add(line);
                    yield return null;
                }
            }

            string path = Path.Combine(Application.persistentDataPath, "crowd_bench.json");
            var sb = new StringBuilder();
            sb.Append("{\"device\":\"").Append(SystemInfo.deviceModel)
              .Append("\",\"gpu\":\"").Append(SystemInfo.graphicsDeviceName)
              .Append("\",\"fps\":").Append(_fps.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(",\"lines\":[");
            for (int i = 0; i < _benchLines.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('\"').Append(_benchLines[i].Replace("\\", "\\\\").Replace("\"", "\\\""))
                  .Append('\"');
            }
            sb.Append("]}");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("CROWD_BENCH_WROTE " + path);
            _benchMessage = "benchmark done";
            _benchRunning = false;
            _simSpeed = previousSpeed;
            if (quitWhenDone)
            {
                yield return new WaitForSeconds(1f);
                Application.Quit();
            }
        }

        private static long SafeAllocatedBytes()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); }
            catch { return 0; }
        }

        private static double Pct(double[] sorted, int p)
        {
            int idx = (int)Math.Round((p / 100.0) * (sorted.Length - 1));
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        private void BuildVisuals()
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            _camera = camGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.10f, 0.12f, 0.16f);
            _camera.fieldOfView = 55f;
            _camera.nearClipPlane = 0.5f;
            _camera.farClipPlane = 600f;
            float cx = _map.Json.width * _map.Json.cell * 0.5f;
            float cz = _map.Json.height * _map.Json.cell * 0.5f;
            _camTarget = new Vector3(cx, 8f, cz);
            ResetView();

            var lightGo = new GameObject("Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.96f, 0.88f);
            lightGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            if (_map.Json.mesh != null && _map.Json.mesh.vertices != null && _map.Json.mesh.indices != null)
            {
                var mesh = new Mesh { name = "map" };
                var verts = new Vector3[_map.Json.mesh.vertices.Length / 3];
                for (int i = 0; i < verts.Length; i++)
                {
                    verts[i] = new Vector3(_map.Json.mesh.vertices[i * 3],
                        _map.Json.mesh.vertices[i * 3 + 1], _map.Json.mesh.vertices[i * 3 + 2]);
                }
                mesh.vertices = verts;
                mesh.triangles = _map.Json.mesh.indices;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var go = new GameObject("TerrainMesh");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = groundMaterial != null ? new Material(groundMaterial) : new Material(Shader.Find("Standard"));
                mat.color = new Color(0.34f, 0.36f, 0.38f);
                mat.SetFloat("_Glossiness", 0.05f);
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            _unitMesh = BuildUnitMesh();
            Shader unitShader = Shader.Find("CrowdNav/UnitInstanced");
            _unitMaterial = unitMaterial != null
                ? new Material(unitMaterial)
                : new Material(unitShader != null ? unitShader : Shader.Find("Standard"));
            _unitMaterial.color = new Color(1.0f, 0.55f, 0.18f);
            _unitMaterial.SetFloat("_Glossiness", 0.15f);
            _unitMaterial.enableInstancing = true;

            Shader overlayShader = Shader.Find("CrowdNav/GridOverlay");
            if (overlayShader == null) overlayShader = Shader.Find("Sprites/Default");
            if (overlayShader == null) overlayShader = Shader.Find("Hidden/Internal-Colored");
            _walkableMaterialInstance = walkableMaterial != null
                ? new Material(walkableMaterial)
                : new Material(overlayShader);
            _walkableMaterialInstance.color = Color.white;

            var marker = new GameObject("GoalMarker");
            marker.AddComponent<MeshFilter>().sharedMesh = _unitMesh;
            marker.transform.localScale = new Vector3(1.4f, 0.35f, 1.4f);
            var markerRenderer = marker.AddComponent<MeshRenderer>();
            var markerMat = new Material(Shader.Find("Standard"));
            markerMat.color = new Color(1f, 0.85f, 0.15f);
            markerMat.SetFloat("_Glossiness", 0.3f);
            markerRenderer.sharedMaterial = markerMat;
            markerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            marker.SetActive(false);
            _goalMarker = marker.transform;
        }

        private static Mesh BuildUnitMesh()
        {
            const int sides = 6;
            var verts = new List<Vector3>(sides * 4);
            var tris = new List<int>(sides * 12);
            const float r = 0.5f;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides;
                float a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * r, 0f, Mathf.Sin(a0) * r);
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * r, 0f, Mathf.Sin(a1) * r);
                int b = verts.Count;
                verts.Add(p0); verts.Add(p1);
                verts.Add(p0 + Vector3.up); verts.Add(p1 + Vector3.up);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
            }
            var mesh = new Mesh { name = "unit" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDestroy()
        {
            if (_jobs != null) { _jobs.Dispose(); _jobs = null; }
            if (_gridData != null) { _gridData.Dispose(); _gridData = null; }
            if (_jobSpawns.IsCreated) _jobSpawns.Dispose();
            if (_walkableGo != null) Destroy(_walkableGo);
            if (_walkableMesh != null) Destroy(_walkableMesh);
            if (_walkableMaterialInstance != null) Destroy(_walkableMaterialInstance);
        }

        private static bool[] ToBool(int[] values)
        {
            var result = new bool[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = values[i] != 0;
            return result;
        }

        private static List<Vec2> BuildHexFormation(NavGrid grid, Vec2 center, int count,
            float spacing, float maxRadius)
        {
            var result = new List<Vec2>(count);
            float sep = 2f * grid.AgentRadius + 0.03f;
            spacing = Mathf.Max(spacing, sep);
            int maxRing = Mathf.Max(1, Mathf.CeilToInt(maxRadius / spacing));
            for (int ring = 0; ring <= maxRing && result.Count < count; ring++)
            {
                var ringPoints = new List<Vec2>();
                if (ring == 0) ringPoints.Add(center);
                else
                {
                    int q = ring, r = -ring;
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
                ringPoints.Sort((a, b) => Mathf.Atan2(a.Z - center.Z, a.X - center.X)
                    .CompareTo(Mathf.Atan2(b.Z - center.Z, b.X - center.X)));
                for (int i = 0; i < ringPoints.Count && result.Count < count; i++)
                {
                    Vec2 p = ringPoints[i];
                    if (Vec2.Distance(p, center) > maxRadius) continue;
                    if (!grid.IsWorldWalkable(p)) continue;
                    bool free = true;
                    for (int j = 0; j < result.Count; j++)
                        if (Vec2.DistanceSq(p, result[j]) < sep * sep * 0.98f) { free = false; break; }
                    if (free) result.Add(p);
                }
            }
            // Return the partial formation; BuildSpawns() reports/fits the actual count.
            return result;
        }
    }
}