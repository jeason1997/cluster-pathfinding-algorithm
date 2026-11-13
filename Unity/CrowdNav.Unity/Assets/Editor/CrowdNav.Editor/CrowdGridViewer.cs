using System;
using System.Collections.Generic;
using CrowdNav.Hpc;
using CrowdNav.Unity;
using UnityEditor;
using UnityEngine;

namespace CrowdNav.Unity.EditorTools
{
    /// <summary>
    /// Scene-view overlay of the exact radius-aware walkable cells used by CrowdGridData.
    /// Open with CrowdNav/Grid Viewer; edit radius/slope and click Bake to see the new region.
    /// </summary>
    public sealed class CrowdGridViewer : EditorWindow
    {
        private const string MapPath = "Assets/CrowdNav.App/Resources/neicheng_walk01.json";

        [SerializeField] private float agentRadius = 0.35f;
        [SerializeField] private float maxSlope = 42f;
        [SerializeField] private float clearanceMargin = 0.04f;
        [SerializeField] private bool mirrorX = true;
        [SerializeField] private bool showWalkable = true;
        [SerializeField] private bool showClearance;

        private CrowdGridData _data;
        private Mesh _mesh;
        private Material _material;
        private int _walkableCount;
        private int _cellCount;
        private string _message = "";
        private Vector2 _scroll;
        private bool _subscribed;

        [MenuItem("CrowdNav/Grid Viewer")]
        public static void Open()
        {
            var window = GetWindow<CrowdGridViewer>("Crowd Grid");
            window.minSize = new Vector2(320f, 300f);
            window.Show();
        }

        private void OnEnable()
        {
            if (!_subscribed)
            {
                SceneView.duringSceneGui += OnSceneGui;
                _subscribed = true;
            }
            EditorApplication.delayCall += Bake;
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                SceneView.duringSceneGui -= OnSceneGui;
                _subscribed = false;
            }
            Cleanup();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Walkable region overlay", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Same CrowdGridData.Build used by the runtime: clearance >= radius + margin + cell/2. " +
                "Green = walkable; clearance mode shows narrow (red) to wide (green).",
                MessageType.Info);

            agentRadius = EditorGUILayout.Slider("Agent radius (m)", agentRadius, 0.05f, 2f);
            maxSlope = EditorGUILayout.Slider("Max slope (deg)", maxSlope, 5f, 60f);
            clearanceMargin = EditorGUILayout.Slider("Clearance margin (m)", clearanceMargin, 0f, 0.5f);
            mirrorX = EditorGUILayout.Toggle("Mirror X (Unity FBX)", mirrorX);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake / Refresh", GUILayout.Height(28f))) Bake();
                if (GUILayout.Button("Use scene unit size", GUILayout.Height(28f))) UseSceneUnitSize();
            }

            showWalkable = EditorGUILayout.Toggle("Show walkable", showWalkable);
            showClearance = EditorGUILayout.Toggle("Color by clearance", showClearance);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cells", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Walkable", _walkableCount.ToString());
            EditorGUILayout.LabelField("Total", _cellCount.ToString());
            if (!string.IsNullOrEmpty(_message))
                EditorGUILayout.HelpBox(_message, MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime rebake", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The demo's '应用参数/重烘' button calls the same Build at runtime (~25–35 ms), " +
                "then restarts the simulation with the new grid.",
                MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void UseSceneUnitSize()
        {
            var demo = UnityEngine.Object.FindFirstObjectByType<CrowdNavDemo>();
            if (demo == null)
            {
                _message = "No CrowdNavDemo in the open scene.";
                return;
            }
            agentRadius = 0.35f * Mathf.Clamp(demo.unitSize, 0.5f, 2f);
            Bake();
        }

        private void Bake()
        {
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(MapPath);
                if (asset == null)
                {
                    _message = "Missing " + MapPath;
                    return;
                }

                Cleanup();
                var map = CrowdMap.LoadFromText(asset.text, agentRadius, mirrorX);
                var walkable = new bool[map.Json.walkable.Length];
                for (int i = 0; i < walkable.Length; i++) walkable[i] = map.Json.walkable[i] != 0;

                _data = CrowdGridData.Build(map.Json.heights, walkable, map.Json.width, map.Json.height,
                    map.Json.cell, map.Json.originX, map.Json.originZ, agentRadius, maxSlope, clearanceMargin);
                _cellCount = _data.Grid.CellCount;
                BuildMesh(_data.Grid);
                _message = string.Format("Baked radius {0:0.00} m, slope {1:0}°, cell {2:0.00} m",
                    agentRadius, maxSlope, map.Json.cell);
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                _message = "Bake failed: " + ex.Message;
                Debug.LogException(ex);
            }
        }

        private void BuildMesh(CrowdGrid grid)
        {
            _walkableCount = 0;
            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            float cell = grid.CellSize;

            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int i = z * grid.Width + x;
                    if (!grid.IsWalkableCell(x, z)) continue;
                    _walkableCount++;

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

                    Color c;
                    if (showClearance)
                    {
                        float clear = grid.Clearance[i];
                        float min = grid.AgentRadius + grid.ClearanceMargin;
                        float t = Mathf.Clamp01((clear - min) / 2f);
                        c = Color.Lerp(new Color(1f, 0.22f, 0.15f, 0.45f),
                            new Color(0.2f, 1f, 0.45f, 0.35f), t);
                    }
                    else
                    {
                        c = new Color(0.2f, 1f, 0.45f, 0.32f);
                    }
                    colors.Add(c); colors.Add(c); colors.Add(c); colors.Add(c);

                    triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 1);
                    triangles.Add(v); triangles.Add(v + 3); triangles.Add(v + 2);
                }
            }

            if (vertices.Count == 0)
            {
                _message = "No walkable cells at this radius.";
                return;
            }

            _mesh = new Mesh { name = "CrowdGridWalkable", hideFlags = HideFlags.HideAndDontSave };
            _mesh.SetVertices(vertices);
            _mesh.SetColors(colors);
            _mesh.SetTriangles(triangles, 0, true);
            _mesh.RecalculateBounds();

            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
        }

        private void OnSceneGui(SceneView sceneView)
        {
            if (!showWalkable || _mesh == null || _material == null) return;
            if (Event.current.type != EventType.Repaint) return;
            _material.SetPass(0);
            Graphics.DrawMeshNow(_mesh, Matrix4x4.identity);
        }

        private void Cleanup()
        {
            if (_data != null)
            {
                _data.Dispose();
                _data = null;
            }
            if (_mesh != null)
            {
                DestroyImmediate(_mesh);
                _mesh = null;
            }
            if (_material != null)
            {
                DestroyImmediate(_material);
                _material = null;
            }
        }

        // Batch-mode smoke test: verify the exact bake path without opening a window.
        public static void BakeForTest()
        {
            var viewer = CreateInstance<CrowdGridViewer>();
            viewer.Bake();
            Debug.Log(string.Format("GRIDVIEW walkable={0}/{1} radius={2:0.00} message={3}",
                viewer._walkableCount, viewer._cellCount, viewer.agentRadius, viewer._message));
            viewer.Cleanup();
            DestroyImmediate(viewer);
            EditorApplication.Exit(viewer._walkableCount > 0 ? 0 : 1);
        }
    }
}
