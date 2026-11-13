using System;
using CrowdNav;
using UnityEngine;

namespace CrowdNav.Unity
{
    [Serializable]
    public sealed class CrowdMapJson
    {
        public string id;
        public string name;
        public int width;
        public int height;
        public float cell;
        public float originX;
        public float originZ;
        public float[] spawn;
        public float spawnRadius;
        public float[] suggestedGoal;
        public float[] heights;
        public int[] walkable;
        public CrowdMapMeshJson mesh;
    }

    [Serializable]
    public sealed class CrowdMapMeshJson
    {
        public float[] vertices;
        public int[] indices;
    }

    public sealed class CrowdMap
    {
        public CrowdMapJson Json;
        public HeightMap HeightMap;
        public NavGrid Grid;
        public Vec2 SpawnCenter;
        public float SpawnRadius;
        public Vec2 SuggestedGoal;
        public string Name;
        public bool MirroredX;

        /// <summary>
        /// Loads the baked map. Unity imports a Y-up right-handed FBX by flipping its X
        /// axis (left-handed conversion); the JSON bake keeps the raw FBX X. mirrorX=true
        /// mirrors the whole map (heights, walkable mask, mesh winding, spawn/goal) so the
        /// demo matches the FBX as seen in a Unity scene.
        /// </summary>
        public static CrowdMap LoadFromText(string text, float agentRadius = 0.35f,
            bool mirrorX = true, float maxSlopeDegrees = 42f, float clearanceMargin = 0.04f)
        {
            var json = JsonUtility.FromJson<CrowdMapJson>(text);
            if (json == null || json.heights == null || json.walkable == null)
                throw new InvalidOperationException("Map JSON parse failed.");

            if (mirrorX) MirrorX(json);

            var map = new CrowdMap
            {
                Json = json,
                Name = string.IsNullOrEmpty(json.name) ? json.id : json.name,
                SpawnCenter = new Vec2(json.spawn[0], json.spawn[1]),
                SpawnRadius = json.spawnRadius,
                SuggestedGoal = new Vec2(json.suggestedGoal[0], json.suggestedGoal[1]),
                MirroredX = mirrorX
            };
            var mask = new bool[json.walkable.Length];
            for (int i = 0; i < mask.Length; i++) mask[i] = json.walkable[i] != 0;
            map.HeightMap = new HeightMap(json.width, json.height, json.cell, json.originX, json.originZ, mask);
            for (int i = 0; i < json.heights.Length; i++)
                map.HeightMap.SetHeight(i % json.width, i / json.width, json.heights[i]);
            map.Grid = new NavGrid(map.HeightMap, Array.Empty<CircleObstacle>(), agentRadius, maxSlopeDegrees, clearanceMargin);
            return map;
        }

        private static void MirrorX(CrowdMapJson json)
        {
            int w = json.width;
            int n = json.heights.Length;
            var heights = new float[n];
            var walkable = new int[n];
            for (int z = 0; z < json.height; z++)
            {
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    int src = row + x;
                    int dst = row + (w - 1 - x);
                    heights[dst] = json.heights[src];
                    walkable[dst] = json.walkable[src];
                }
            }
            json.heights = heights;
            json.walkable = walkable;

            float worldWidth = w * json.cell;
            json.spawn[0] = worldWidth - json.spawn[0];
            json.suggestedGoal[0] = worldWidth - json.suggestedGoal[0];

            if (json.mesh != null && json.mesh.vertices != null)
            {
                var v = json.mesh.vertices;
                for (int i = 0; i < v.Length; i += 3)
                    v[i] = worldWidth - v[i];
                // Mirroring flips triangle winding; swap two indices so the surface stays front-facing.
                var idx = json.mesh.indices;
                if (idx != null)
                {
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        int t = idx[i + 1];
                        idx[i + 1] = idx[i + 2];
                        idx[i + 2] = t;
                    }
                }
            }
        }
    }
}
