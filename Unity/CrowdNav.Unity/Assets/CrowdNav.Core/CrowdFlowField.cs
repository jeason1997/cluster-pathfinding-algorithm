using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    /// <summary>Binary min-heap with decrease-key; every grid cell is in the heap at most once.</summary>
    public struct CrowdMinHeap
    {
        public NativeArray<int> Items;
        public NativeArray<float> Keys;
        public NativeArray<int> Positions;
        public int Count;

        public void Clear(int capacity)
        {
            Count = 0;
            for (int i = 0; i < capacity; i++) Positions[i] = -1;
        }

        public void PushOrDecrease(int item, float key)
        {
            int pos = Positions[item];
            if (pos >= 0)
            {
                Keys[pos] = key;
                SiftUp(pos);
                return;
            }
            pos = Count++;
            Items[pos] = item;
            Keys[pos] = key;
            Positions[item] = pos;
            SiftUp(pos);
        }

        public int Pop(out float key)
        {
            int root = Items[0];
            key = Keys[0];
            Positions[root] = -1;
            Count--;
            if (Count > 0)
            {
                Items[0] = Items[Count];
                Keys[0] = Keys[Count];
                Positions[Items[0]] = 0;
                SiftDown(0);
            }
            return root;
        }

        private void SiftUp(int pos)
        {
            while (pos > 0)
            {
                int parent = (pos - 1) >> 1;
                if (Keys[parent] <= Keys[pos]) break;
                Swap(parent, pos);
                pos = parent;
            }
        }

        private void SiftDown(int pos)
        {
            while (true)
            {
                int left = (pos << 1) + 1;
                if (left >= Count) break;
                int right = left + 1;
                int smallest = left;
                if (right < Count && Keys[right] < Keys[left]) smallest = right;
                if (Keys[pos] <= Keys[smallest]) break;
                Swap(pos, smallest);
                pos = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            int item = Items[a]; Items[a] = Items[b]; Items[b] = item;
            float key = Keys[a]; Keys[a] = Keys[b]; Keys[b] = key;
            Positions[Items[a]] = a;
            Positions[Items[b]] = b;
        }
    }

    public sealed class CrowdFlowFieldData : IDisposable
    {
        public NativeArray<float> Dist;
        public NativeArray<int> Next;
        public NativeArray<int> HeapItems;
        public NativeArray<float> HeapKeys;
        public NativeArray<int> HeapPositions;
        public readonly int CellCount;

        public CrowdFlowFieldData(int cellCount)
        {
            CellCount = cellCount;
            Dist = new NativeArray<float>(cellCount, Allocator.Persistent);
            Next = new NativeArray<int>(cellCount, Allocator.Persistent);
            HeapItems = new NativeArray<int>(cellCount, Allocator.Persistent);
            HeapKeys = new NativeArray<float>(cellCount, Allocator.Persistent);
            HeapPositions = new NativeArray<int>(cellCount, Allocator.Persistent);
        }

        public void Dispose()
        {
            Dist.Dispose();
            Next.Dispose();
            HeapItems.Dispose();
            HeapKeys.Dispose();
            HeapPositions.Dispose();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdBuildFlowFieldJob : IJob
    {
        [ReadOnly] public CrowdGrid Grid;
        public NativeArray<float> Dist;
        public NativeArray<int> Next;
        public NativeArray<int> HeapItems;
        public NativeArray<float> HeapKeys;
        public NativeArray<int> HeapPositions;
        public int GoalCell;

        public void Execute()
        {
            int n = Grid.CellCount;
            for (int i = 0; i < n; i++)
            {
                Dist[i] = float.PositiveInfinity;
                Next[i] = -1;
            }
            if (GoalCell < 0) return;

            var heap = new CrowdMinHeap
            {
                Items = HeapItems,
                Keys = HeapKeys,
                Positions = HeapPositions,
                Count = 0
            };
            heap.Clear(n);
            Dist[GoalCell] = 0f;
            heap.PushOrDecrease(GoalCell, 0f);

            while (heap.Count > 0)
            {
                int cur = heap.Pop(out float currentDist);
                int cx = cur % Grid.Width;
                int cz = cur / Grid.Width;
                float hc = Grid.Heights[cur];

                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + FlowDx(k);
                    int nz = cz + FlowDz(k);
                    if (!Grid.InBounds(nx, nz)) continue;
                    int ni = nz * Grid.Width + nx;
                    if (Grid.Walkable[ni] == 0) continue;
                    if (k >= 4 && (Grid.Walkable[cz * Grid.Width + nx] == 0 ||
                                   Grid.Walkable[nz * Grid.Width + cx] == 0))
                        continue;

                    float len = Grid.CellSize * FlowDirLen(k);
                    float dh = hc - Grid.Heights[ni];
                    float slope = math.abs(dh) / len;
                    if (slope > Grid.TanMaxSlope * 1.05f) continue;

                    float uphill = dh > 0f ? dh : 0f;
                    float cost = len * (1f + 1.7f * slope * slope) +
                                 0.30f * uphill + 0.08f * math.abs(dh);
                    float nd = currentDist + cost;
                    if (nd + 1e-4f >= Dist[ni]) continue;
                    Dist[ni] = nd;
                    Next[ni] = cur;
                    heap.PushOrDecrease(ni, nd);
                }
            }
        }

        private static int FlowDx(int k)
        {
            switch (k)
            {
                case 0: return 1;
                case 1: return -1;
                case 2: return 0;
                case 3: return 0;
                case 4: return 1;
                case 5: return 1;
                case 6: return -1;
                default: return -1;
            }
        }

        private static int FlowDz(int k)
        {
            switch (k)
            {
                case 0: return 0;
                case 1: return 0;
                case 2: return 1;
                case 3: return -1;
                case 4: return 1;
                case 5: return -1;
                case 6: return 1;
                default: return -1;
            }
        }

        private static float FlowDirLen(int k) => k >= 4 ? 1.41421356f : 1f;
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdPlanPathsJob : IJobParallelFor
    {
        [ReadOnly] public CrowdGrid Grid;
        [ReadOnly] public NativeArray<float> Dist;
        [ReadOnly] public NativeArray<int> Next;
        [ReadOnly] public NativeArray<float2> Position;
        public NativeArray<byte> Flags;
        public NativeArray<int> WaypointIndex;
        public NativeArray<int> PathCount;
        [NativeDisableParallelForRestriction] public NativeArray<float2> Path;
        public int PathStride;
        public int GoalCell;

        public void Execute(int i)
        {
            if ((Flags[i] & CrowdAgentFlags.ReplanRequested) == 0) return;
            Flags[i] = unchecked((byte)(Flags[i] & (byte)~CrowdAgentFlags.ReplanRequested));
            int start = Grid.FindNearestWalkableCell(Position[i]);
            if (start < 0 || float.IsPositiveInfinity(Dist[start]) || GoalCell < 0)
            {
                PathCount[i] = 0;
                WaypointIndex[i] = 0;
                Flags[i] |= CrowdAgentFlags.Unreachable;
                return;
            }

            int baseIndex = i * PathStride;
            int count = 0;
            int cur = start;
            bool truncated = false;
            while (cur != GoalCell)
            {
                if (count >= PathStride) { truncated = true; break; }
                Path[baseIndex + count] = Grid.CellCenter(cur);
                count++;
                int n = Next[cur];
                if (n < 0) break;
                cur = n;
            }
            if (!truncated && cur == GoalCell && count < PathStride)
            {
                Path[baseIndex + count] = Grid.CellCenter(cur);
                count++;
            }
            if (count == 0)
            {
                PathCount[i] = 0;
                WaypointIndex[i] = 0;
                Flags[i] |= CrowdAgentFlags.Unreachable;
                return;
            }

            PathCount[i] = count;
            WaypointIndex[i] = count > 1 ? 1 : 0;
            Flags[i] = (byte)((Flags[i] | CrowdAgentFlags.HasPath) & ~CrowdAgentFlags.Unreachable);
        }
    }
}