using System;
using Unity.Collections;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    /// <summary>Immutable (per-bake) navigation grid shared by every Burst job.</summary>
    public struct CrowdGrid
    {
        public int Width;
        public int Height;
        public float CellSize;
        public float OriginX;
        public float OriginZ;
        public float AgentRadius;
        public float ClearanceMargin;
        public float TanMaxSlope;

        [ReadOnly] public NativeArray<float> Heights;
        [ReadOnly] public NativeArray<byte> BaseBlocked;
        [ReadOnly] public NativeArray<byte> Walkable;
        [ReadOnly] public NativeArray<float> Clearance;

        public int CellCount => Width * Height;

        public int Index(int x, int z) => z * Width + x;

        public bool InBounds(int x, int z) => (uint)x < (uint)Width && (uint)z < (uint)Height;

        public float HeightAtCell(int x, int z) => Heights[z * Width + x];

        public float ClearanceAtCell(int x, int z) => Clearance[z * Width + x];

        public bool IsWalkableCell(int x, int z) => Walkable[z * Width + x] != 0;

        public bool IsWalkableIndex(int i) => Walkable[i] != 0;

        public float2 CellCenter(int index)
        {
            int x = index % Width;
            int z = index / Width;
            return new float2(OriginX + (x + 0.5f) * CellSize, OriginZ + (z + 0.5f) * CellSize);
        }

        public bool WorldToCell(float2 p, out int x, out int z)
        {
            x = (int)math.floor((p.x - OriginX) / CellSize);
            z = (int)math.floor((p.y - OriginZ) / CellSize);
            return (uint)x < (uint)Width && (uint)z < (uint)Height;
        }

        public int NearestCellIndex(float2 p)
        {
            int x = (int)math.floor((p.x - OriginX) / CellSize);
            int z = (int)math.floor((p.y - OriginZ) / CellSize);
            x = math.clamp(x, 0, Width - 1);
            z = math.clamp(z, 0, Height - 1);
            return z * Width + x;
        }

        public float SampleHeight(float x, float z)
        {
            float gx = (x - OriginX) / CellSize - 0.5f;
            float gz = (z - OriginZ) / CellSize - 0.5f;
            gx = math.clamp(gx, 0f, Width - 1f);
            gz = math.clamp(gz, 0f, Height - 1f);
            int x0 = (int)gx;
            int z0 = (int)gz;
            int x1 = math.min(x0 + 1, Width - 1);
            int z1 = math.min(z0 + 1, Height - 1);
            float tx = gx - x0;
            float tz = gz - z0;
            float h00 = Heights[z0 * Width + x0];
            float h10 = Heights[z0 * Width + x1];
            float h01 = Heights[z1 * Width + x0];
            float h11 = Heights[z1 * Width + x1];
            float a = h00 + (h10 - h00) * tx;
            float b = h01 + (h11 - h01) * tx;
            return a + (b - a) * tz;
        }

        /// <summary>Exact circle-vs-blocked-cell test for the baked blocked mask.</summary>
        public bool IsWorldWalkable(float2 p)
        {
            int px = (int)math.floor((p.x - OriginX) / CellSize);
            int pz = (int)math.floor((p.y - OriginZ) / CellSize);
            if (!InBounds(px, pz)) return false;

            float r = AgentRadius + ClearanceMargin;
            float rSq = r * r;
            int minX = (int)math.floor((p.x - r - OriginX) / CellSize);
            int maxX = (int)math.floor((p.x + r - OriginX) / CellSize);
            int minZ = (int)math.floor((p.y - r - OriginZ) / CellSize);
            int maxZ = (int)math.floor((p.y + r - OriginZ) / CellSize);
            minX = math.clamp(minX, 0, Width - 1);
            maxX = math.clamp(maxX, 0, Width - 1);
            minZ = math.clamp(minZ, 0, Height - 1);
            maxZ = math.clamp(maxZ, 0, Height - 1);
            float half = CellSize * 0.5f;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (BaseBlocked[z * Width + x] == 0) continue;
                    float cx = OriginX + (x + 0.5f) * CellSize;
                    float cz = OriginZ + (z + 0.5f) * CellSize;
                    float dx = math.max(0f, math.abs(p.x - cx) - half);
                    float dz = math.max(0f, math.abs(p.y - cz) - half);
                    if (dx * dx + dz * dz < rSq) return false;
                }
            }
            return true;
        }

        public bool IsSegmentWalkable(float2 a, float2 b, float heightToleranceMultiplier = 1.35f)
        {
            float2 d = b - a;
            float len = math.length(d);
            if (len < 1e-5f) return IsWorldWalkable(a);
            if (!IsWorldWalkable(a) || !IsWorldWalkable(b)) return false;

            float r = AgentRadius + ClearanceMargin;
            int minX = (int)math.floor((math.min(a.x, b.x) - r - OriginX) / CellSize);
            int maxX = (int)math.floor((math.max(a.x, b.x) + r - OriginX) / CellSize);
            int minZ = (int)math.floor((math.min(a.y, b.y) - r - OriginZ) / CellSize);
            int maxZ = (int)math.floor((math.max(a.y, b.y) + r - OriginZ) / CellSize);
            minX = math.clamp(minX, 0, Width - 1);
            maxX = math.clamp(maxX, 0, Width - 1);
            minZ = math.clamp(minZ, 0, Height - 1);
            maxZ = math.clamp(maxZ, 0, Height - 1);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (BaseBlocked[z * Width + x] == 0) continue;
                    float2 c = new float2(OriginX + (x + 0.5f) * CellSize, OriginZ + (z + 0.5f) * CellSize);
                    if (SegmentIntersectsExpandedCell(a, b, c, CellSize * 0.5f + r)) return false;
                }
            }

            int steps = math.max(1, (int)math.ceil(len / (CellSize * 0.35f)));
            float prevH = SampleHeight(a.x, a.y);
            float2 prev = a;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float2 p = a + d * t;
                float h = SampleHeight(p.x, p.y);
                float run = math.distance(p, prev);
                if (run > 1e-5f)
                {
                    float slope = math.abs(h - prevH) / run;
                    if (slope > TanMaxSlope * heightToleranceMultiplier) return false;
                }
                prev = p;
                prevH = h;
            }
            return true;
        }

        public static bool SegmentIntersectsExpandedCell(float2 a, float2 b, float2 center, float half)
        {
            float minX = center.x - half;
            float maxX = center.x + half;
            float minZ = center.y - half;
            float maxZ = center.y + half;
            float tMin = 0f;
            float tMax = 1f;
            float dx = b.x - a.x;
            float dz = b.y - a.y;

            if (math.abs(dx) < 1e-9f)
            {
                if (a.x < minX || a.x > maxX) return false;
            }
            else
            {
                float inv = 1f / dx;
                float t1 = (minX - a.x) * inv;
                float t2 = (maxX - a.x) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = math.max(tMin, t1);
                tMax = math.min(tMax, t2);
                if (tMin > tMax) return false;
            }

            if (math.abs(dz) < 1e-9f)
            {
                if (a.y < minZ || a.y > maxZ) return false;
            }
            else
            {
                float inv = 1f / dz;
                float t1 = (minZ - a.y) * inv;
                float t2 = (maxZ - a.y) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = math.max(tMin, t1);
                tMax = math.min(tMax, t2);
                if (tMin > tMax) return false;
            }
            return tMax >= 0f && tMin <= 1f;
        }

        /// <summary>Nearest radius-walkable cell; ring search with a bounded fallback.</summary>
        public int FindNearestWalkableCell(float2 p)
        {
            int sx = (int)math.floor((p.x - OriginX) / CellSize);
            int sz = (int)math.floor((p.y - OriginZ) / CellSize);
            sx = math.clamp(sx, 0, Width - 1);
            sz = math.clamp(sz, 0, Height - 1);
            int start = sz * Width + sx;
            if (Walkable[start] != 0) return start;

            for (int radius = 1; radius <= 8; radius++)
            {
                int minX = sx - radius;
                int maxX = sx + radius;
                int minZ = sz - radius;
                int maxZ = sz + radius;
                int ringBest = -1;
                float bestD = float.MaxValue;
                for (int z = minZ; z <= maxZ; z++)
                {
                    if ((uint)z >= (uint)Height) continue;
                    for (int x = minX; x <= maxX; x++)
                    {
                        if ((uint)x >= (uint)Width) continue;
                        if (x != minX && x != maxX && z != minZ && z != maxZ) continue;
                        int ni = z * Width + x;
                        if (Walkable[ni] == 0) continue;
                        float2 c = new float2(OriginX + (x + 0.5f) * CellSize, OriginZ + (z + 0.5f) * CellSize);
                        float d = math.distancesq(c, p);
                        if (d < bestD) { bestD = d; ringBest = ni; }
                    }
                }
                if (ringBest >= 0) return ringBest;
            }

            // Bounded fallback: scan a 64m square. Avoids allocating a visited array in a job.
            int limit = (int)math.ceil(64f / CellSize);
            int rx0 = math.max(0, sx - limit);
            int rx1 = math.min(Width - 1, sx + limit);
            int rz0 = math.max(0, sz - limit);
            int rz1 = math.min(Height - 1, sz + limit);
            int best = -1;
            float bestDist = float.MaxValue;
            for (int z = rz0; z <= rz1; z++)
            {
                for (int x = rx0; x <= rx1; x++)
                {
                    int ni = z * Width + x;
                    if (Walkable[ni] == 0) continue;
                    float2 c = new float2(OriginX + (x + 0.5f) * CellSize, OriginZ + (z + 0.5f) * CellSize);
                    float d = math.distancesq(c, p);
                    if (d < bestDist) { bestDist = d; best = ni; }
                }
            }
            return best;
        }

        public static float DistanceSqPointSegment(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a;
            float lenSq = math.lengthsq(ab);
            if (lenSq < 1e-12f) return math.distancesq(p, a);
            float t = math.clamp(math.dot(p - a, ab) / lenSq, 0f, 1f);
            return math.distancesq(p, a + ab * t);
        }
    }

    /// <summary>Owns the native grid arrays; the struct view is what jobs receive.</summary>
    public sealed class CrowdGridData : IDisposable
    {
        public readonly CrowdGrid Grid;
        public readonly bool IsCreated;

        private CrowdGridData(CrowdGrid grid)
        {
            Grid = grid;
            IsCreated = true;
        }

        public static CrowdGridData Build(float[] heights, bool[] walkableMask, int width, int height,
            float cellSize, float originX, float originZ, float agentRadius, float maxSlopeDegrees,
            float clearanceMargin = 0.04f)
        {
            if (heights == null || heights.Length != width * height)
                throw new ArgumentException("Height field size mismatch.");
            int n = width * height;
            float tan = math.tan(maxSlopeDegrees * math.PI / 180f);
            var baseBlocked = new byte[n];
            var walkable = new byte[n];
            var clearance = new float[n];

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    bool blocked = x == 0 || z == 0 || x == width - 1 || z == height - 1 ||
                                   (walkableMask != null && !walkableMask[i]);
                    if (!blocked && x > 0 && x < width - 1 && z > 0 && z < height - 1)
                    {
                        float hx = (heights[z * width + x + 1] - heights[z * width + x - 1]) / (2f * cellSize);
                        float hz = (heights[(z + 1) * width + x] - heights[(z - 1) * width + x]) / (2f * cellSize);
                        if (math.sqrt(hx * hx + hz * hz) > tan) blocked = true;
                    }
                    baseBlocked[i] = blocked ? (byte)1 : (byte)0;
                }
            }

            const float inf = 1e20f;
            var f = new float[math.max(width, height)];
            var d = new float[math.max(width, height)];
            var v = new int[math.max(width, height)];
            var env = new float[math.max(width, height) + 1];

            for (int row = 0; row < height; row++)
            {
                for (int x = 0; x < width; x++) f[x] = baseBlocked[row * width + x] != 0 ? 0f : inf;
                DistanceTransform1D(f, width, d, v, env);
                for (int x = 0; x < width; x++) clearance[row * width + x] = d[x];
            }
            for (int col = 0; col < width; col++)
            {
                for (int z = 0; z < height; z++) f[z] = clearance[z * width + col];
                DistanceTransform1D(f, height, d, v, env);
                for (int z = 0; z < height; z++) clearance[z * width + col] = math.sqrt(d[z]) * cellSize;
            }

            float required = agentRadius + clearanceMargin + cellSize * 0.5f;
            for (int i = 0; i < n; i++)
                walkable[i] = baseBlocked[i] == 0 && clearance[i] >= required ? (byte)1 : (byte)0;

            var grid = new CrowdGrid
            {
                Width = width,
                Height = height,
                CellSize = cellSize,
                OriginX = originX,
                OriginZ = originZ,
                AgentRadius = agentRadius,
                ClearanceMargin = clearanceMargin,
                TanMaxSlope = tan,
                Heights = new NativeArray<float>(heights, Allocator.Persistent),
                BaseBlocked = new NativeArray<byte>(baseBlocked, Allocator.Persistent),
                Walkable = new NativeArray<byte>(walkable, Allocator.Persistent),
                Clearance = new NativeArray<float>(clearance, Allocator.Persistent)
            };
            return new CrowdGridData(grid);
        }

        private static void DistanceTransform1D(float[] f, int n, float[] d, int[] v, float[] z)
        {
            const float inf = 1e20f;
            int k = -1;
            for (int q = 0; q < n; q++)
            {
                if (f[q] >= inf * 0.5f) continue;
                if (k < 0)
                {
                    k = 0; v[0] = q; z[0] = -inf; z[1] = inf;
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
                    k = 0; v[0] = q; z[0] = -inf; z[1] = inf;
                    continue;
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = inf;
            }
            if (k < 0)
            {
                for (int q = 0; q < n; q++) d[q] = inf;
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

        public void Dispose()
        {
            Grid.Heights.Dispose();
            Grid.BaseBlocked.Dispose();
            Grid.Walkable.Dispose();
            Grid.Clearance.Dispose();
        }
    }
}