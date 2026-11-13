using System;

namespace CrowdNav
{
    /// <summary>
    /// Regular height field. Heights are stored at cell centres; cell (0,0) centre is at
    /// (OriginX + CellSize/2, OriginZ + CellSize/2). Bilinear sampling is used by both the
    /// simulation and the renderer so that the walkable surface is continuous.
    /// </summary>
    public sealed class HeightMap
    {
        public readonly int Width;
        public readonly int Height;
        public readonly float CellSize;
        public readonly float OriginX;
        public readonly float OriginZ;
        private readonly float[] _heights;
        private readonly bool[]? _walkableMask;

        public HeightMap(int width, int height, float cellSize, float originX, float originZ,
            bool[]? walkableMask = null)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width;
            Height = height;
            CellSize = cellSize;
            OriginX = originX;
            OriginZ = originZ;
            _heights = new float[width * height];
            if (walkableMask != null && walkableMask.Length != _heights.Length)
                throw new ArgumentException("Walkable mask size must match height map.", nameof(walkableMask));
            _walkableMask = walkableMask;
        }

        public float[] RawHeights => _heights;
        public bool HasWalkableMask => _walkableMask != null;

        public bool IsWalkableCell(int x, int z) =>
            _walkableMask == null || _walkableMask[z * Width + x];

        public void SetWalkableCell(int x, int z, bool walkable)
        {
            if (_walkableMask == null) return;
            _walkableMask[z * Width + x] = walkable;
        }
        public float WorldMinX => OriginX;
        public float WorldMinZ => OriginZ;
        public float WorldMaxX => OriginX + Width * CellSize;
        public float WorldMaxZ => OriginZ + Height * CellSize;

        public float GetHeight(int x, int z) => _heights[z * Width + x];

        public void SetHeight(int x, int z, float h) => _heights[z * Width + x] = h;

        public void Fill(Func<float, float, float> heightFunction)
        {
            for (int z = 0; z < Height; z++)
            {
                float wz = OriginZ + (z + 0.5f) * CellSize;
                for (int x = 0; x < Width; x++)
                {
                    float wx = OriginX + (x + 0.5f) * CellSize;
                    _heights[z * Width + x] = heightFunction(wx, wz);
                }
            }
        }

        public Vec2 CellCenter(int x, int z) =>
            new Vec2(OriginX + (x + 0.5f) * CellSize, OriginZ + (z + 0.5f) * CellSize);

        public bool Contains(float x, float z) =>
            x >= OriginX && z >= OriginZ && x < WorldMaxX && z < WorldMaxZ;

        public bool TryWorldToCell(float x, float z, out int cx, out int cz)
        {
            cx = (int)MathF.Floor((x - OriginX) / CellSize);
            cz = (int)MathF.Floor((z - OriginZ) / CellSize);
            return cx >= 0 && cz >= 0 && cx < Width && cz < Height;
        }

        public float SampleHeight(float x, float z)
        {
            float gx = (x - OriginX) / CellSize - 0.5f;
            float gz = (z - OriginZ) / CellSize - 0.5f;
            gx = MathUtil.Clamp(gx, 0f, Width - 1f);
            gz = MathUtil.Clamp(gz, 0f, Height - 1f);

            int x0 = (int)gx;
            int z0 = (int)gz;
            int x1 = Math.Min(x0 + 1, Width - 1);
            int z1 = Math.Min(z0 + 1, Height - 1);
            float tx = gx - x0;
            float tz = gz - z0;

            float h00 = _heights[z0 * Width + x0];
            float h10 = _heights[z0 * Width + x1];
            float h01 = _heights[z1 * Width + x0];
            float h11 = _heights[z1 * Width + x1];

            float a = h00 + (h10 - h00) * tx;
            float b = h01 + (h11 - h01) * tx;
            return a + (b - a) * tz;
        }

        /// <summary>Surface normal, useful for Unity adapters.</summary>
        public Vec3 SampleNormal(float x, float z)
        {
            const float e = 0.25f;
            float hl = SampleHeight(x - e, z);
            float hr = SampleHeight(x + e, z);
            float hd = SampleHeight(x, z - e);
            float hu = SampleHeight(x, z + e);
            float nx = hl - hr;
            float ny = 2f * e;
            float nz = hd - hu;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) return new Vec3(0f, 1f, 0f);
            return new Vec3(nx / len, ny / len, nz / len);
        }
    }
}
