using System;
using System.Collections.Generic;

namespace CrowdNav
{
    /// <summary>
    /// One reverse Dijkstra potential field per goal. Every agent follows the same
    /// next-cell pointers instead of running its own A* search. For a crowd moving to
    /// one clicked point this removes N independent searches (the main goal-command
    /// spike at 1000 units).
    /// </summary>
    public sealed class FlowField
    {
        private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] DirLen =
            { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

        private readonly NavGrid _grid;
        private readonly float[] _dist;
        private readonly int[] _next;
        private readonly MinHeap _heap;
        private int _goalCell = -1;

        public FlowField(NavGrid grid)
        {
            _grid = grid;
            _dist = new float[grid.CellCount];
            _next = new int[grid.CellCount];
            _heap = new MinHeap(Math.Max(4096, grid.CellCount / 2));
        }

        public bool IsBuilt => _goalCell >= 0;

        public void Build(Vec2 goal)
        {
            int goalCell = _grid.FindNearestWalkableCell(goal);
            if (goalCell < 0)
            {
                _goalCell = -1;
                return;
            }

            Array.Fill(_dist, float.PositiveInfinity);
            Array.Fill(_next, -1);
            _heap.Clear();
            _dist[goalCell] = 0f;
            _heap.Push(goalCell, 0f);

            while (_heap.Count > 0)
            {
                int cur = _heap.Pop(out float key);
                if (key > _dist[cur] + 1e-4f) continue;
                int cx = cur % _grid.Width;
                int cz = cur / _grid.Width;
                float hc = _grid.HeightAtCell(cx, cz);

                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + Dx[k];
                    int nz = cz + Dz[k];
                    if (!_grid.InBounds(nx, nz)) continue;
                    int ni = _grid.Index(nx, nz);
                    if (!_grid.IsWalkableCell(nx, nz)) continue;
                    if (k >= 4 &&
                        (!_grid.IsWalkableCell(cx + Dx[k], cz) ||
                         !_grid.IsWalkableCell(cx, cz + Dz[k])))
                        continue;

                    float len = _grid.CellSize * DirLen[k];
                    float dh = hc - _grid.HeightAtCell(nx, nz); // neighbour -> current
                    float slope = MathF.Abs(dh) / len;
                    if (slope > _grid.TanMaxSlope * 1.05f) continue;

                    float uphill = dh > 0f ? dh : 0f;
                    float cost = len * (1f + 1.7f * slope * slope) +
                                 0.30f * uphill + 0.08f * MathF.Abs(dh);
                    float nd = _dist[cur] + cost;
                    if (nd + 1e-4f >= _dist[ni]) continue;
                    _dist[ni] = nd;
                    _next[ni] = cur;
                    _heap.Push(ni, nd);
                }
            }

            _goalCell = goalCell;
        }

        public bool BuildPath(Vec2 start, PathResult result)
        {
            result.Clear();
            if (_goalCell < 0) return false;
            int startCell = _grid.FindNearestWalkableCell(start);
            result.StartCell = startCell;
            result.GoalCell = _goalCell;
            if (startCell < 0 || float.IsPositiveInfinity(_dist[startCell])) return false;

            result.Success = true;
            result.Cost = _dist[startCell];
            result.CellPath.Add(_grid.CellCenter(startCell));

            int cur = startCell;
            int guard = 0;
            while (cur != _goalCell && guard++ < _grid.CellCount)
            {
                int n = _next[cur];
                if (n < 0)
                {
                    result.Success = false;
                    result.CellPath.Clear();
                    return false;
                }
                result.CellPath.Add(_grid.CellCenter(n));
                cur = n;
            }
            if (cur != _goalCell)
            {
                result.Success = false;
                result.CellPath.Clear();
            }
            return result.Success;
        }
    }
}
