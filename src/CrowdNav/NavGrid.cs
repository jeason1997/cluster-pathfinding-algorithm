using System;
using System.Collections.Generic;

namespace CrowdNav
{
    /// <summary>
    /// Walkability, slope and clearance baked from a height field. The grid is deliberately
    /// engine independent; a Unity adapter only has to fill HeightMap and obstacle circles.
    /// </summary>
    public sealed class NavGrid
    {
        private const float Inf = 1e20f;

        public readonly HeightMap Map;
        public readonly int Width;
        public readonly int Height;
        public readonly float CellSize;
        public readonly float AgentRadius;
        public readonly float MaxSlopeRadians;
        public readonly float ClearanceMargin;
        public readonly IReadOnlyList<CircleObstacle> Obstacles;

        private readonly bool[] _baseBlocked;
        private readonly bool[] _walkable;
        private readonly float[] _clearance;
        private readonly float[] _height;

        public NavGrid(HeightMap map, IReadOnlyList<CircleObstacle> obstacles,
            float agentRadius, float maxSlopeDegrees, float clearanceMargin = 0.04f)
        {
            Map = map;
            Width = map.Width;
            Height = map.Height;
            CellSize = map.CellSize;
            AgentRadius = agentRadius;
            MaxSlopeRadians = maxSlopeDegrees * MathF.PI / 180f;
            ClearanceMargin = clearanceMargin;
            Obstacles = obstacles ?? Array.Empty<CircleObstacle>();

            int n = Width * Height;
            _baseBlocked = new bool[n];
            _walkable = new bool[n];
            _clearance = new float[n];
            _height = new float[n];
            for (int z = 0; z < Height; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = z * Width + x;
                    _height[i] = map.GetHeight(x, z);
                }
            }

            BakeBaseBlocked();
            BakeClearance();
            BakeAgentWalkable();
        }

        public float TanMaxSlope => MathF.Tan(MaxSlopeRadians);
        public int CellCount => Width * Height;

        public int Index(int x, int z) => z * Width + x;
        public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;

        public float HeightAtCell(int x, int z) => _height[z * Width + x];
        public float ClearanceAtCell(int x, int z) => _clearance[z * Width + x];
        public bool IsBlockedBase(int x, int z) => _baseBlocked[z * Width + x];
        public bool IsWalkableCell(int x, int z) => _walkable[z * Width + x];
        public bool IsWalkableIndex(int i) => _walkable[i];

        public void CellCoords(int index, out int x, out int z)
        {
            x = index % Width;
            z = index / Width;
        }

        public Vec2 CellCenter(int index)
        {
            int x = index % Width;
            int z = index / Width;
            return Map.CellCenter(x, z);
        }

        public bool WorldToCell(Vec2 p, out int x, out int z)
        {
            x = (int)MathF.Floor((p.X - Map.OriginX) / CellSize);
            z = (int)MathF.Floor((p.Z - Map.OriginZ) / CellSize);
            return x >= 0 && z >= 0 && x < Width && z < Height;
        }

        public int NearestCellIndex(Vec2 p)
        {
            if (!WorldToCell(p, out int x, out int z))
            {
                x = MathUtil.ClampInt((int)MathF.Floor((p.X - Map.OriginX) / CellSize), 0, Width - 1);
                z = MathUtil.ClampInt((int)MathF.Floor((p.Z - Map.OriginZ) / CellSize), 0, Height - 1);
            }
            return Index(x, z);
        }

        public float SampleHeight(float x, float z) => Map.SampleHeight(x, z);

        public bool IsPointInsideObstacle(Vec2 p, float extraRadius = 0f)
        {
            for (int i = 0; i < Obstacles.Count; i++)
            {
                CircleObstacle o = Obstacles[i];
                float rr = o.Radius + AgentRadius + extraRadius;
                if (Vec2.DistanceSq(p, o.Center) < rr * rr) return true;
            }
            return false;
        }

        /// <summary>
        /// Exact (for the blocked-cell representation) circle-vs-cells test. Cell walkability
        /// alone is not enough: a unit centre can sit exactly on the border of a blocked cell
        /// and still have its radius deeply inside it. This function prevents that trap.
        /// </summary>
        public bool IsWorldWalkable(Vec2 p)
        {
            return IsWorldWalkable(p, 0f);
        }

        public bool IsWorldWalkable(Vec2 p, float extraMargin)
        {
            if (!WorldToCell(p, out int px, out int pz)) return false;
            if (px < 0 || pz < 0) return false;
            if (IsPointInsideObstacle(p, extraMargin)) return false;

            float r = AgentRadius + ClearanceMargin + extraMargin;
            float rSq = r * r;
            int minX = (int)MathF.Floor((p.X - r - Map.OriginX) / CellSize);
            int maxX = (int)MathF.Floor((p.X + r - Map.OriginX) / CellSize);
            int minZ = (int)MathF.Floor((p.Z - r - Map.OriginZ) / CellSize);
            int maxZ = (int)MathF.Floor((p.Z + r - Map.OriginZ) / CellSize);
            minX = MathUtil.ClampInt(minX, 0, Width - 1);
            maxX = MathUtil.ClampInt(maxX, 0, Width - 1);
            minZ = MathUtil.ClampInt(minZ, 0, Height - 1);
            maxZ = MathUtil.ClampInt(maxZ, 0, Height - 1);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!_baseBlocked[Index(x, z)]) continue;
                    Vec2 c = Map.CellCenter(x, z);
                    float hx = CellSize * 0.5f;
                    float dx = MathF.Max(0f, MathF.Abs(p.X - c.X) - hx);
                    float dz = MathF.Max(0f, MathF.Abs(p.Z - c.Z) - hx);
                    if (dx * dx + dz * dz < rSq) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Line-of-sight test with exact swept clearance against blocked cells, plus slope
        /// continuity. Blocked cell rectangles are expanded by the unit radius, which also
        /// prevents cutting corners between two blocked cells.
        /// </summary>
        public bool IsSegmentWalkable(Vec2 a, Vec2 b, float heightToleranceMultiplier = 1.35f)
        {
            Vec2 d = b - a;
            float len = d.Length;
            if (len < 1e-5f) return IsWorldWalkable(a);
            if (!IsWorldWalkable(a) || !IsWorldWalkable(b)) return false;

            float r = AgentRadius + ClearanceMargin;
            int minX = (int)MathF.Floor((MathF.Min(a.X, b.X) - r - Map.OriginX) / CellSize);
            int maxX = (int)MathF.Floor((MathF.Max(a.X, b.X) + r - Map.OriginX) / CellSize);
            int minZ = (int)MathF.Floor((MathF.Min(a.Z, b.Z) - r - Map.OriginZ) / CellSize);
            int maxZ = (int)MathF.Floor((MathF.Max(a.Z, b.Z) + r - Map.OriginZ) / CellSize);
            minX = MathUtil.ClampInt(minX, 0, Width - 1);
            maxX = MathUtil.ClampInt(maxX, 0, Width - 1);
            minZ = MathUtil.ClampInt(minZ, 0, Height - 1);
            maxZ = MathUtil.ClampInt(maxZ, 0, Height - 1);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!_baseBlocked[Index(x, z)]) continue;
                    Vec2 c = Map.CellCenter(x, z);
                    if (SegmentIntersectsExpandedCell(a, b, c, CellSize * 0.5f + r))
                        return false;
                }
            }

            // Slope continuity, sampled independently of the exact static test.
            int steps = Math.Max(1, (int)MathF.Ceiling(len / (CellSize * 0.35f)));
            float prevH = SampleHeight(a.X, a.Z);
            Vec2 prev = a;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vec2 p = a + d * t;
                float h = SampleHeight(p.X, p.Z);
                float run = Vec2.Distance(p, prev);
                if (run > 1e-5f)
                {
                    float slope = MathF.Abs(h - prevH) / run;
                    if (slope > TanMaxSlope * heightToleranceMultiplier) return false;
                }
                prev = p;
                prevH = h;
            }
            return true;
        }

        private static bool SegmentIntersectsExpandedCell(Vec2 a, Vec2 b, Vec2 center, float half)
        {
            float minX = center.X - half;
            float maxX = center.X + half;
            float minZ = center.Z - half;
            float maxZ = center.Z + half;
            float tMin = 0f;
            float tMax = 1f;
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;

            if (MathF.Abs(dx) < 1e-9f)
            {
                if (a.X < minX || a.X > maxX) return false;
            }
            else
            {
                float inv = 1f / dx;
                float t1 = (minX - a.X) * inv;
                float t2 = (maxX - a.X) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax) return false;
            }

            if (MathF.Abs(dz) < 1e-9f)
            {
                if (a.Z < minZ || a.Z > maxZ) return false;
            }
            else
            {
                float inv = 1f / dz;
                float t1 = (minZ - a.Z) * inv;
                float t2 = (maxZ - a.Z) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax) return false;
            }

            return tMax >= 0f && tMin <= 1f;
        }

        /// <summary>Finds nearest radius-walkable cell. Returns -1 when none exists.</summary>
        public int FindNearestWalkableCell(Vec2 p)
        {
            if (!WorldToCell(p, out int sx, out int sz))
            {
                sx = MathUtil.ClampInt((int)MathF.Floor((p.X - Map.OriginX) / CellSize), 0, Width - 1);
                sz = MathUtil.ClampInt((int)MathF.Floor((p.Z - Map.OriginZ) / CellSize), 0, Height - 1);
            }

            int start = Index(sx, sz);
            if (_walkable[start]) return start;

            var q = new Queue<int>();
            var seen = new bool[CellCount];
            q.Enqueue(start);
            seen[start] = true;
            int best = -1;
            float bestDist = float.MaxValue;
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                int cx = cur % Width;
                int cz = cur / Width;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx;
                        int nz = cz + dz;
                        if (!InBounds(nx, nz)) continue;
                        int ni = Index(nx, nz);
                        if (seen[ni]) continue;
                        seen[ni] = true;
                        if (_walkable[ni])
                        {
                            float dist = Vec2.DistanceSq(CellCenter(ni), p);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                best = ni;
                            }
                        }
                        q.Enqueue(ni);
                    }
                }
            }
            return best;
        }

        public int CountWalkableCells()
        {
            int n = 0;
            for (int i = 0; i < _walkable.Length; i++) if (_walkable[i]) n++;
            return n;
        }

        private void BakeBaseBlocked()
        {
            float tan = MathF.Tan(MaxSlopeRadians);
            for (int z = 0; z < Height; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = Index(x, z);
                    bool blocked = x == 0 || z == 0 || x == Width - 1 || z == Height - 1 ||
                        !Map.IsWalkableCell(x, z);

                    if (!blocked && x > 0 && x < Width - 1 && z > 0 && z < Height - 1)
                    {
                        float hx = (_height[Index(x + 1, z)] - _height[Index(x - 1, z)]) / (2f * CellSize);
                        float hz = (_height[Index(x, z + 1)] - _height[Index(x, z - 1)]) / (2f * CellSize);
                        if (MathF.Sqrt(hx * hx + hz * hz) > tan) blocked = true;
                    }

                    if (!blocked)
                    {
                        Vec2 c = Map.CellCenter(x, z);
                        for (int oi = 0; oi < Obstacles.Count; oi++)
                        {
                            CircleObstacle o = Obstacles[oi];
                            if (Vec2.DistanceSq(c, o.Center) <= o.Radius * o.Radius)
                            {
                                blocked = true;
                                break;
                            }
                        }
                    }

                    _baseBlocked[i] = blocked;
                }
            }
        }

        private void BakeClearance()
        {
            int maxDim = Math.Max(Width, Height);
            float[] f = new float[maxDim];
            float[] d = new float[maxDim];
            int[] v = new int[maxDim];
            float[] envelope = new float[maxDim + 1];

            for (int row = 0; row < Height; row++)
            {
                for (int x = 0; x < Width; x++)
                    f[x] = _baseBlocked[Index(x, row)] ? 0f : Inf;
                DistanceTransform1D(f, Width, d, v, envelope);
                for (int x = 0; x < Width; x++)
                    _clearance[Index(x, row)] = d[x];
            }

            for (int col = 0; col < Width; col++)
            {
                for (int z = 0; z < Height; z++)
                    f[z] = _clearance[Index(col, z)];
                DistanceTransform1D(f, Height, d, v, envelope);
                for (int z = 0; z < Height; z++)
                    _clearance[Index(col, z)] = MathF.Sqrt(d[z]) * CellSize;
            }
        }

        private void BakeAgentWalkable()
        {
            // The extra half-cell accounts for the distance field being sampled at cell centres.
            float required = AgentRadius + ClearanceMargin + CellSize * 0.5f;
            for (int i = 0; i < _walkable.Length; i++)
                _walkable[i] = !_baseBlocked[i] && _clearance[i] >= required;
        }

        private static void DistanceTransform1D(float[] f, int n, float[] d, int[] v, float[] z)
        {
            int k = -1;
            for (int q = 0; q < n; q++)
            {
                if (f[q] >= Inf * 0.5f) continue;
                if (k < 0)
                {
                    k = 0;
                    v[0] = q;
                    z[0] = -Inf;
                    z[1] = Inf;
                    continue;
                }

                float fq = f[q] + q * q;
                float s;
                while (true)
                {
                    int p = v[k];
                    s = (fq - (f[p] + p * p)) / (2f * (q - p));
                    if (s > z[k]) break;
                    k--;
                    if (k < 0) break;
                }

                if (k < 0)
                {
                    k = 0;
                    v[0] = q;
                    z[0] = -Inf;
                    z[1] = Inf;
                    continue;
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = Inf;
            }

            if (k < 0)
            {
                for (int q = 0; q < n; q++) d[q] = Inf;
                return;
            }

            int j = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[j + 1] < q) j++;
                int p = v[j];
                d[q] = (q - p) * (q - p) + f[p];
            }
        }
    }
}
