using System;
using System.Collections.Generic;

namespace CrowdNav
{
    public sealed class PathResult
    {
        public readonly List<Vec2> CellPath = new List<Vec2>(64);
        public bool Success;
        public float Cost;
        public int Expanded;
        public int GoalCell;
        public int StartCell;

        public void Clear()
        {
            CellPath.Clear();
            Success = false;
            Cost = 0f;
            Expanded = 0;
        }
    }

    /// <summary>
    /// One-shot A* on the baked height-field grid. Each agent plans once per goal change;
    /// local avoidance handles dynamic neighbours. The search workspace is reused.
    /// </summary>
    public sealed class PathFinder
    {
        private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] DirLen = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

        private readonly NavGrid _grid;
        private readonly float[] _g;
        private readonly int[] _parent;
        private readonly int[] _stamp;
        private readonly int[] _closed;
        private readonly MinHeap _heap;
        private readonly int[] _pathCells;
        private int _generation;

        public PathFinder(NavGrid grid, int heapCapacity = 4096)
        {
            _grid = grid;
            int n = grid.CellCount;
            _g = new float[n];
            _parent = new int[n];
            _stamp = new int[n];
            _closed = new int[n];
            _pathCells = new int[n];
            _heap = new MinHeap(heapCapacity);
        }

        public void FindPath(Vec2 start, Vec2 goal, PathResult result)
        {
            result.Clear();
            int startCell = _grid.FindNearestWalkableCell(start);
            int goalCell = _grid.FindNearestWalkableCell(goal);
            result.StartCell = startCell;
            result.GoalCell = goalCell;
            if (startCell < 0 || goalCell < 0) return;

            _generation++;
            if (_generation == int.MaxValue)
            {
                Array.Clear(_stamp, 0, _stamp.Length);
                Array.Clear(_closed, 0, _closed.Length);
                _generation = 1;
            }

            _heap.Clear();
            _g[startCell] = 0f;
            _stamp[startCell] = _generation;
            _parent[startCell] = -1;
            _heap.Push(startCell, Heuristic(startCell, goalCell));

            bool found = false;
            while (_heap.Count > 0)
            {
                int cur = _heap.Pop(out float key);
                if (_closed[cur] == _generation) continue;

                float gc = _stamp[cur] == _generation ? _g[cur] : float.PositiveInfinity;
                _closed[cur] = _generation;
                result.Expanded++;

                if (cur == goalCell)
                {
                    found = true;
                    break;
                }

                int cx = cur % _grid.Width;
                int cz = cur / _grid.Width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + Dx[k];
                    int nz = cz + Dz[k];
                    if (!_grid.InBounds(nx, nz)) continue;
                    int ni = _grid.Index(nx, nz);
                    if (!_grid.IsWalkableCell(nx, nz)) continue;
                    if (_closed[ni] == _generation) continue;

                    bool diagonal = k >= 4;
                    if (diagonal)
                    {
                        // Do not squeeze between two blocked cells.
                        if (!_grid.IsWalkableCell(cx + Dx[k], cz) || !_grid.IsWalkableCell(cx, cz + Dz[k]))
                            continue;
                    }

                    float len = _grid.CellSize * DirLen[k];
                    float dh = _grid.HeightAtCell(nx, nz) - _grid.HeightAtCell(cx, cz);
                    float slope = MathF.Abs(dh) / len;
                    if (slope > _grid.TanMaxSlope * 1.05f) continue;

                    float uphill = dh > 0f ? dh : 0f;
                    float stepCost = len * (1f + 1.7f * slope * slope) + 0.30f * uphill + 0.08f * MathF.Abs(dh);
                    float ng = gc + stepCost;

                    if (_stamp[ni] != _generation || ng + 1e-4f < _g[ni])
                    {
                        _stamp[ni] = _generation;
                        _g[ni] = ng;
                        _parent[ni] = cur;
                        _heap.Push(ni, ng + Heuristic(ni, goalCell));
                    }
                }
            }

            if (!found) return;

            int count = 0;
            int node = goalCell;
            while (node != -1)
            {
                _pathCells[count++] = node;
                if (node == startCell) break;
                node = _parent[node];
            }

            // If start was not reached (should not happen), reject.
            if (count == 0 || _pathCells[count - 1] != startCell) return;

            result.Success = true;
            result.Cost = _g[goalCell];
            for (int i = count - 1; i >= 0; i--)
                result.CellPath.Add(_grid.CellCenter(_pathCells[i]));
        }

        private float Heuristic(int cell, int goalCell)
        {
            float ax = cell % _grid.Width;
            float az = cell / _grid.Width;
            float bx = goalCell % _grid.Width;
            float bz = goalCell / _grid.Width;
            float dx = (ax - bx) * _grid.CellSize;
            float dz = (az - bz) * _grid.CellSize;
            return MathF.Sqrt(dx * dx + dz * dz);
        }
    }

    public static class PathSmoother
    {
        /// <summary>
        /// Greedy string-pulling with a bounded look-ahead. Cell paths are jagged; this keeps
        /// only long visible chords, which removes axis-aligned zig-zag and looks natural.
        /// </summary>
        public static void Smooth(NavGrid grid, Vec2 start, List<Vec2> cellPath, Vec2 goal, List<Vec2> result)
        {
            result.Clear();
            if (cellPath == null || cellPath.Count == 0)
            {
                if (grid.IsWorldWalkable(goal)) result.Add(goal);
                return;
            }

            var points = new List<Vec2>(cellPath.Count + 2) { start };
            for (int i = 0; i < cellPath.Count; i++)
            {
                Vec2 p = cellPath[i];
                if (Vec2.DistanceSq(p, points[points.Count - 1]) > 1e-6f) points.Add(p);
            }

            Vec2 lastCell = points[points.Count - 1];
            bool goalOk = grid.IsWorldWalkable(goal) &&
                          grid.IsSegmentWalkable(lastCell, goal);
            if (goalOk && Vec2.DistanceSq(goal, lastCell) > 1e-6f)
                points.Add(goal);
            else if (Vec2.DistanceSq(goal, lastCell) > 1e-6f)
                points[points.Count - 1] = lastCell;

            if (points.Count == 1)
            {
                result.Add(points[0]);
                return;
            }

            const int maxLookahead = 24;
            int anchor = 0;
            result.Add(points[0]);
            while (anchor < points.Count - 1)
            {
                int limit = Math.Min(points.Count - 1, anchor + maxLookahead);
                int chosen = anchor + 1;
                for (int j = limit; j > anchor + 1; j--)
                {
                    if (grid.IsSegmentWalkable(points[anchor], points[j]))
                    {
                        chosen = j;
                        break;
                    }
                }

                // Avoid duplicate consecutive points.
                if (Vec2.DistanceSq(result[result.Count - 1], points[chosen]) > 1e-6f)
                    result.Add(points[chosen]);
                anchor = chosen;
            }
        }
    }
}
