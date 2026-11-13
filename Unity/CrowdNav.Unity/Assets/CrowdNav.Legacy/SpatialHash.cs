using System;
using System.Collections.Generic;

namespace CrowdNav
{
    /// <summary>Uniform-grid broad phase, rebuilt once per simulation tick.</summary>
    public sealed class SpatialHash
    {
        private readonly float _cellSize;
        private readonly float _minX;
        private readonly float _minZ;
        private readonly int _cols;
        private readonly int _rows;
        private readonly List<int>[] _buckets;
        private readonly List<int> _active = new List<int>();

        public SpatialHash(float minX, float minZ, float maxX, float maxZ, float cellSize)
        {
            _cellSize = cellSize;
            _minX = minX;
            _minZ = minZ;
            _cols = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellSize));
            _rows = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / cellSize));
            _buckets = new List<int>[_cols * _rows];
            for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new List<int>(4);
        }

        public void Rebuild(IReadOnlyList<Agent> agents)
        {
            for (int i = 0; i < _active.Count; i++)
                _buckets[_active[i]].Clear();
            _active.Clear();

            for (int i = 0; i < agents.Count; i++)
            {
                int b = Bucket(agents[i].Position);
                if (_buckets[b].Count == 0) _active.Add(b);
                _buckets[b].Add(i);
            }
        }

        public void Query(Vec2 p, float radius, List<int> results)
        {
            results.Clear();
            int x0 = (int)MathF.Floor((p.X - radius - _minX) / _cellSize);
            int x1 = (int)MathF.Floor((p.X + radius - _minX) / _cellSize);
            int z0 = (int)MathF.Floor((p.Z - radius - _minZ) / _cellSize);
            int z1 = (int)MathF.Floor((p.Z + radius - _minZ) / _cellSize);
            x0 = MathUtil.ClampInt(x0, 0, _cols - 1);
            x1 = MathUtil.ClampInt(x1, 0, _cols - 1);
            z0 = MathUtil.ClampInt(z0, 0, _rows - 1);
            z1 = MathUtil.ClampInt(z1, 0, _rows - 1);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    List<int> bucket = _buckets[z * _cols + x];
                    for (int k = 0; k < bucket.Count; k++) results.Add(bucket[k]);
                }
            }
        }

        private int Bucket(Vec2 p)
        {
            int x = MathUtil.ClampInt((int)MathF.Floor((p.X - _minX) / _cellSize), 0, _cols - 1);
            int z = MathUtil.ClampInt((int)MathF.Floor((p.Z - _minZ) / _cellSize), 0, _rows - 1);
            return z * _cols + x;
        }
    }
}
