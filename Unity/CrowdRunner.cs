// Unity 接入样板（未在本仓库的 dotnet 工程中编译）。
// 使用方式：
//   1. 把 src/CrowdNav/*.cs 拷到 Assets/CrowdNav/；
//   2. 本文件放到 Assets/，挂在一个空 GameObject 上；
//   3. 指定 terrain、agentPrefab、groundMask，点击 Play 后右键/左键点地面。
// 这是“最小可运行”适配，不包含对象池、动画、UI 和网络同步。

using System.Collections.Generic;
using CrowdNav;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CrowdNav.Unity
{
    public sealed class CrowdRunner : MonoBehaviour
    {
        [Header("Scene")]
        public Terrain terrain;
        public GameObject agentPrefab;
        public LayerMask groundMask = ~0;
        public int unitCount = 120;

        [Header("Simulation")]
        public float fixedDt = 1f / 60f;
        public float agentRadius = 0.35f;
        public float maxSpeed = 3.1f;
        public float slotSpacing = 1.0f;
        public float cellSize = 0.5f;
        public bool useOrca = true;
        public int pathPlansPerFrame = 2;

        [Header("Obstacles (terrain-local XZ circles)")]
        public Vector2[] obstacleCenters;
        public float[] obstacleRadii;

        private HeightMap _heightMap;
        private NavGrid _grid;
        private CrowdSimulation _sim;
        private readonly List<Transform> _views = new List<Transform>();
        private float _accumulator;
        private float _originX, _originZ;

        private void Start()
        {
            if (terrain == null || agentPrefab == null)
            {
                Debug.LogError("CrowdRunner: terrain / agentPrefab 未设置");
                enabled = false;
                return;
            }

            BuildHeightMap();
            BuildGrid();
            BuildCrowd();
        }

        private void Update()
        {
            if (_sim == null) return;

            if (Input.GetMouseButtonDown(0) && !IsPointerOverUI())
            {
                Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit, 500f, groundMask))
                {
                    var goal = new Vec2(hit.point.x - _originX, hit.point.z - _originZ);
                    _sim.SetGoal(goal);
                }
            }

            // 固定步长：避免帧率波动改变避让结果。
            _accumulator += Mathf.Min(0.1f, Time.deltaTime);
            int steps = 0;
            while (_accumulator >= fixedDt && steps < 4)
            {
                _sim.Step(fixedDt);
                _accumulator -= fixedDt;
                steps++;
            }

            SyncViews();
        }

        private void BuildHeightMap()
        {
            TerrainData data = terrain.terrainData;
            Vector3 pos = terrain.transform.position;
            _originX = pos.x;
            _originZ = pos.z;

            int width = Mathf.Max(2, Mathf.CeilToInt(data.size.x / cellSize));
            int height = Mathf.Max(2, Mathf.CeilToInt(data.size.z / cellSize));
            float cell = data.size.x / width;

            _heightMap = new HeightMap(width, height, cell, 0f, 0f);
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float wx = pos.x + (x + 0.5f) * cell;
                    float wz = pos.z + (z + 0.5f) * cell;
                    _heightMap.SetHeight(x, z, terrain.SampleHeight(new Vector3(wx, 0f, wz)));
                }
            }
        }

        private void BuildGrid()
        {
            var obstacles = new List<CircleObstacle>();
            if (obstacleCenters != null && obstacleRadii != null)
            {
                int n = Mathf.Min(obstacleCenters.Length, obstacleRadii.Length);
                for (int i = 0; i < n; i++)
                    obstacles.Add(new CircleObstacle(new Vec2(obstacleCenters[i].x, obstacleCenters[i].y), obstacleRadii[i]));
            }

            _grid = new NavGrid(_heightMap, obstacles, agentRadius, 42f, 0.04f);
        }

        private void BuildCrowd()
        {
            var config = new SimulationConfig
            {
                AgentRadius = agentRadius,
                MaxSpeed = maxSpeed,
                SlotSpacing = slotSpacing,
                UseOrca = useOrca,
                PathPlanFrameBudget = pathPlansPerFrame
            };
            _sim = new CrowdSimulation(_grid, config);

            // 这里用一个小方阵示例；实际项目从出生点/集结点生成。
            var spawns = new List<Vec2>(unitCount);
            int cols = Mathf.CeilToInt(Mathf.Sqrt(unitCount));
            for (int i = 0; i < unitCount; i++)
            {
                int cx = i % cols;
                int cz = i / cols;
                spawns.Add(new Vec2(6f + cx * 0.9f, 6f + cz * 0.9f));
            }

            var goal = new Vec2(40f, 32f);
            _sim.Initialize(goal, spawns);

            for (int i = 0; i < unitCount; i++)
            {
                GameObject view = Instantiate(agentPrefab, transform);
                view.name = "Agent_" + i;
                _views.Add(view.transform);
            }
        }

        private void SyncViews()
        {
            int n = Mathf.Min(_views.Count, _sim.Agents.Count);
            for (int i = 0; i < n; i++)
            {
                Agent a = _sim.Agents[i];
                float y = _heightMap.SampleHeight(a.Position.X, a.Position.Z);
                Vector3 p = new Vector3(a.Position.X + _originX, y + 0.01f, a.Position.Z + _originZ);
                _views[i].position = p;

                Vec2 v = a.Velocity;
                if (v.LengthSq > 0.01f)
                {
                    Vector3 dir = new Vector3(v.X, 0f, v.Z);
                    _views[i].rotation = Quaternion.LookRotation(dir, Vector3.up);
                }
            }
        }

        private static bool IsPointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void OnDrawGizmos()
        {
            if (_sim == null || _sim.GoalLayout == null) return;
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.6f);
            foreach (GoalSlot slot in _sim.GoalLayout.Slots)
            {
                float y = _heightMap.SampleHeight(slot.Position.X, slot.Position.Z);
                Gizmos.DrawWireSphere(new Vector3(slot.Position.X + _originX, y + 0.05f, slot.Position.Z + _originZ), 0.15f);
            }
        }
    }
}
