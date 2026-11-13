using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdBuildGoalJob : IJob
    {
        [ReadOnly] public CrowdGrid Grid;
        public float2 RequestedCenter;
        public int Count;
        public float Spacing;
        public int MaxRings;
        public bool CenterAttraction;

        public NativeArray<float2> Slots;
        public NativeArray<int> OutCount;
        public NativeArray<float2> OutCenter;
        public NativeArray<float> OutRadius;

        public void Execute()
        {
            int nearest = Grid.FindNearestWalkableCell(RequestedCenter);
            float2 center = nearest >= 0 ? Grid.CellCenter(nearest) : RequestedCenter;
            OutCenter[0] = center;

            if (CenterAttraction)
            {
                float radius = Spacing * math.sqrt((float)Count) * 0.55f + Spacing;
                OutRadius[0] = radius;
                OutCount[0] = Count;
                for (int i = 0; i < Count; i++) Slots[i] = center;
                return;
            }

            int slotCount = 0;
            for (int ring = 0; ring <= MaxRings && slotCount < Count; ring++)
            {
                if (ring == 0)
                {
                    if (Grid.IsWorldWalkable(center))
                    {
                        Slots[slotCount++] = center;
                    }
                    continue;
                }

                int q = ring;
                int r = -ring;
                for (int side = 0; side < 6 && slotCount < Count; side++)
                {
                    for (int step = 0; step < ring && slotCount < Count; step++)
                    {
                        float wx = Spacing * (q + r * 0.5f);
                        float wz = Spacing * 0.86602540378f * r;
                        float2 p = new float2(center.x + wx, center.y + wz);
                        if (Grid.IsWorldWalkable(p) && Grid.IsSegmentWalkable(center, p))
                            Slots[slotCount++] = p;

                        if (side == 0) { r += 1; }
                        else if (side == 1) { q -= 1; r += 1; }
                        else if (side == 2) { q -= 1; }
                        else if (side == 3) { r -= 1; }
                        else if (side == 4) { q += 1; r -= 1; }
                        else { q += 1; }
                    }
                }
            }

            float maxRadius = 0f;
            for (int i = 0; i < slotCount; i++)
                maxRadius = math.max(maxRadius, math.distance(Slots[i], center));
            OutCount[0] = slotCount;
            OutRadius[0] = maxRadius;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdAssignSlotsJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Slots;
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<byte> State;
        [ReadOnly] public float2 Center;
        [ReadOnly] public int Count;
        [ReadOnly] public int SlotCount;
        [ReadOnly] public bool CenterAttraction;

        public NativeArray<float2> SlotPosition;
        public NativeArray<int> SlotIndex;
        public NativeArray<float> DistanceToSlot;
        public NativeArray<byte> Used;
        public NativeArray<int> Order;

        public void Execute()
        {
            if (CenterAttraction)
            {
                for (int i = 0; i < Count; i++)
                {
                    SlotPosition[i] = Center;
                    SlotIndex[i] = 0;
                    DistanceToSlot[i] = math.distance(Position[i], Center);
                }
                return;
            }

            float2 centroid = float2.zero;
            for (int i = 0; i < Count; i++) centroid += Position[i];
            centroid /= math.max(1, Count);

            float maxOffset = 0f;
            for (int i = 0; i < Count; i++)
                maxOffset = math.max(maxOffset, math.distance(Position[i], centroid));
            float maxSlot = 0f;
            for (int s = 0; s < SlotCount; s++)
                maxSlot = math.max(maxSlot, math.distance(Slots[s], Center));
            float scale = maxOffset > 1e-3f
                ? math.min(1f, math.max(0.2f, (maxSlot - 0.35f) / maxOffset))
                : 1f;

            for (int i = 0; i < Count; i++) Order[i] = i;
            for (int i = 1; i < Count; i++)
            {
                int key = Order[i];
                float keyD = math.distancesq(Position[key], centroid);
                int j = i - 1;
                while (j >= 0 && math.distancesq(Position[Order[j]], centroid) < keyD)
                {
                    Order[j + 1] = Order[j];
                    j--;
                }
                Order[j + 1] = key;
            }

            for (int s = 0; s < SlotCount; s++) Used[s] = 0;
            for (int oi = 0; oi < Count; oi++)
            {
                int i = Order[oi];
                float2 desired = Center + (Position[i] - centroid) * scale;
                int best = -1;
                float bestD = float.MaxValue;
                for (int s = 0; s < SlotCount; s++)
                {
                    if (Used[s] != 0) continue;
                    float d = math.distancesq(desired, Slots[s]);
                    if (d < bestD) { bestD = d; best = s; }
                }
                if (best < 0) break;
                Used[best] = 1;
                SlotIndex[i] = best;
                SlotPosition[i] = Slots[best];
                DistanceToSlot[i] = math.distance(Position[i], Slots[best]);
            }
        }
    }

    public sealed class CrowdGoalData : IDisposable
    {
        public NativeArray<float2> Slots;
        public NativeArray<byte> Used;
        public NativeArray<int> Order;
        public NativeArray<int> BuildCount;
        public NativeArray<float2> BuildCenter;
        public NativeArray<float> BuildRadius;
        public int SlotCount;
        public float2 Center;
        public float Radius;
        public bool CenterAttraction;

        public CrowdGoalData(int capacity)
        {
            Slots = new NativeArray<float2>(capacity, Allocator.Persistent);
            Used = new NativeArray<byte>(capacity, Allocator.Persistent);
            Order = new NativeArray<int>(capacity, Allocator.Persistent);
            BuildCount = new NativeArray<int>(1, Allocator.Persistent);
            BuildCenter = new NativeArray<float2>(1, Allocator.Persistent);
            BuildRadius = new NativeArray<float>(1, Allocator.Persistent);
        }

        public void Build(CrowdGrid grid, float2 requested, int count, float spacing, int maxRings,
            bool centerAttraction)
        {
            CenterAttraction = centerAttraction;
            var build = new CrowdBuildGoalJob
            {
                Grid = grid,
                RequestedCenter = requested,
                Count = count,
                Spacing = spacing,
                MaxRings = maxRings,
                CenterAttraction = centerAttraction,
                Slots = Slots,
                OutCount = BuildCount,
                OutCenter = BuildCenter,
                OutRadius = BuildRadius
            };
            build.Schedule().Complete();
            SlotCount = BuildCount[0];
            Center = BuildCenter[0];
            Radius = BuildRadius[0];
        }

        public void Assign(CrowdGrid grid, CrowdAgentStore agents, int count)
        {
            var assign = new CrowdAssignSlotsJob
            {
                Slots = Slots,
                Position = agents.Position,
                State = agents.State,
                Center = Center,
                Count = count,
                SlotCount = SlotCount,
                CenterAttraction = CenterAttraction,
                SlotPosition = agents.SlotPosition,
                SlotIndex = agents.SlotIndex,
                DistanceToSlot = agents.DistanceToSlot,
                Used = Used,
                Order = Order
            };
            assign.Schedule().Complete();
        }

        public void Dispose()
        {
            Slots.Dispose();
            Used.Dispose();
            Order.Dispose();
            BuildCount.Dispose();
            BuildCenter.Dispose();
            BuildRadius.Dispose();
        }
    }
}