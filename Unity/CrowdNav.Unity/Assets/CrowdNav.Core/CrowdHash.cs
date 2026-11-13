using System;
using Unity.Collections;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    /// <summary>
    /// Counting-sort uniform grid. Built once per tick by a Burst job; every neighbour query
    /// is a handful of contiguous cell ranges (no List buckets, no per-query allocation).
    /// </summary>
    public struct CrowdHash
    {
        public int Cols;
        public int Rows;
        public float CellSize;
        public float MinX;
        public float MinZ;

        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> Sorted;
        [ReadOnly] public NativeArray<int> AgentCell;

        public int CellCount => Cols * Rows;

        public int CellOf(float2 p)
        {
            int x = (int)math.floor((p.x - MinX) / CellSize);
            int z = (int)math.floor((p.y - MinZ) / CellSize);
            x = math.clamp(x, 0, Cols - 1);
            z = math.clamp(z, 0, Rows - 1);
            return z * Cols + x;
        }

        public int GatherNearest(NativeArray<float2> positions, float2 p, int self,
            float radius, int maxNeighbors, NativeArray<int> dst, int dstOffset)
        {
            int x0 = (int)math.floor((p.x - radius - MinX) / CellSize);
            int x1 = (int)math.floor((p.x + radius - MinX) / CellSize);
            int z0 = (int)math.floor((p.y - radius - MinZ) / CellSize);
            int z1 = (int)math.floor((p.y + radius - MinZ) / CellSize);
            x0 = math.clamp(x0, 0, Cols - 1);
            x1 = math.clamp(x1, 0, Cols - 1);
            z0 = math.clamp(z0, 0, Rows - 1);
            z1 = math.clamp(z1, 0, Rows - 1);

            float maxDistSq = radius * radius;
            int count = 0;
            for (int z = z0; z <= z1; z++)
            {
                int row = z * Cols;
                for (int x = x0; x <= x1; x++)
                {
                    int c = row + x;
                    int start = CellStart[c];
                    int end = CellStart[c + 1];
                    for (int k = start; k < end; k++)
                    {
                        int j = Sorted[k];
                        if (j == self) continue;
                        float d2 = math.distancesq(p, positions[j]);
                        if (d2 > maxDistSq) continue;

                        int insert = count;
                        while (insert > 0)
                        {
                            int prev = dst[dstOffset + insert - 1];
                            if (d2 >= math.distancesq(p, positions[prev])) break;
                            insert--;
                        }
                        if (insert >= maxNeighbors) continue;
                        if (count < maxNeighbors) count++;
                        for (int m = count - 1; m > insert; m--)
                            dst[dstOffset + m] = dst[dstOffset + m - 1];
                        dst[dstOffset + insert] = j;
                    }
                }
            }
            return count;
        }
    }

    public sealed class CrowdSpatialHash : IDisposable
    {
        public readonly int Cols;
        public readonly int Rows;
        public readonly float CellSize;
        public readonly float MinX;
        public readonly float MinZ;
        public NativeArray<int> CellStart;
        public NativeArray<int> Cursor;
        public NativeArray<int> Sorted;
        public NativeArray<int> AgentCell;

        public CrowdSpatialHash(float minX, float minZ, float maxX, float maxZ, float cellSize, int capacity)
        {
            CellSize = cellSize;
            MinX = minX;
            MinZ = minZ;
            Cols = math.max(1, (int)math.ceil((maxX - minX) / cellSize));
            Rows = math.max(1, (int)math.ceil((maxZ - minZ) / cellSize));
            int cells = Cols * Rows;
            CellStart = new NativeArray<int>(cells + 1, Allocator.Persistent);
            Cursor = new NativeArray<int>(cells, Allocator.Persistent);
            Sorted = new NativeArray<int>(capacity, Allocator.Persistent);
            AgentCell = new NativeArray<int>(capacity, Allocator.Persistent);
        }

        public CrowdHash AsRef() => new CrowdHash
        {
            Cols = Cols,
            Rows = Rows,
            CellSize = CellSize,
            MinX = MinX,
            MinZ = MinZ,
            CellStart = CellStart,
            Sorted = Sorted,
            AgentCell = AgentCell
        };

        public void Dispose()
        {
            CellStart.Dispose();
            Cursor.Dispose();
            Sorted.Dispose();
            AgentCell.Dispose();
        }
    }
}