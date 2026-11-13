using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    /// <summary>
    /// Deterministic sequential commit: sort by distance-to-slot, move one agent at a time,
    /// test the swept capsule against already-committed positions, then resolve any residual
    /// contact by cancelling a step. No agent is ever translated by another agent.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdCommitJob : IJob
    {
        [ReadOnly] public CrowdGrid Grid;
        [ReadOnly] public NativeArray<float2> ChosenVelocity;
        [ReadOnly] public NativeArray<float2> SlotPosition;
        [ReadOnly] public NativeArray<float> DistanceToSlot;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> State;
        [ReadOnly] public NativeArray<byte> Flags;

        public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;
        public NativeArray<float> DistanceTravelled;

        public NativeArray<float2> NewPositions;
        public NativeArray<float2> NewVelocities;
        public NativeArray<byte> HasMoved;
        public NativeArray<int> Order;
        public NativeArray<int> TempOrder;
        public NativeArray<ulong> SortKeys;
        public NativeArray<ulong> TempKeys;
        public NativeArray<int> SortCounts;

        public NativeArray<int> HashCellStart;
        public NativeArray<int> HashCursor;
        public NativeArray<int> HashSorted;
        public NativeArray<int> HashAgentCell;

        public NativeArray<int> Stats;

        public int Count;
        public int Cols;
        public int Rows;
        public float CellSize;
        public float MinX;
        public float MinZ;
        public float MaxSpeed;
        public float RequiredSeparation;
        public float MaxAcceleration;
        public float TimeStep;
        public bool PredictedCommit;
        public bool SimultaneousCommit;
        public bool KeepInsideGoal;
        public float2 GoalCenter;
        public float GoalRadius;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                NewPositions[i] = Position[i];
                NewVelocities[i] = Velocity[i];
                HasMoved[i] = 0;
                SortOrderInit(i);
            }

            BuildHash();

            if (SimultaneousCommit)
            {
                CommitSimultaneous();
            }
            else
            {
                CommitSequential();
            }
            ResolveResidual();
            Stats[CrowdStatsIndex.OverlapRollbacks] = _rollbacks;
            Stats[CrowdStatsIndex.HardClamps] = _hardClamps;
            Stats[CrowdStatsIndex.ResidualPasses] = _residualPasses;
        }

        private int _rollbacks;
        private int _hardClamps;
        private int _residualPasses;

        private void SortOrderInit(int i)
        {
            float d = DistanceToSlot[i];
            ulong bits = math.asuint(d);
            SortKeys[i] = ((ulong)bits << 32) | (uint)i;
            Order[i] = i;
        }

        private void CommitSequential()
        {
            RadixSort();
            NativeArray<int> order = Order;
            for (int oi = 0; oi < Count; oi++)
            {
                int idx = order[oi];
                byte state = State[idx];
                if (state != (byte)CrowdAgentState.Moving && state != (byte)CrowdAgentState.Yielding)
                    continue;

                float2 step = ChosenVelocity[idx] * TimeStep;
                if (math.lengthsq(step) < 1e-10f)
                {
                    Velocity[idx] = float2.zero;
                    continue;
                }

                float2 safe = FindSafeStep(idx, step);
                if (math.distancesq(safe, step) > 1e-8f) _hardClamps++;
                float maxStep = MaxSpeed * TimeStep;
                float stepLen = math.length(safe);
                if (stepLen > maxStep) safe *= maxStep / stepLen;

                Position[idx] += safe;
                Velocity[idx] = safe / TimeStep;
                DistanceTravelled[idx] += math.length(safe);
                NewPositions[idx] = Position[idx];
                NewVelocities[idx] = Velocity[idx];
                HasMoved[idx] = 1;
            }
        }

        private void CommitSimultaneous()
        {
            for (int oi = 0; oi < Count; oi++)
            {
                int idx = Order[oi];
                byte state = State[idx];
                if (state != (byte)CrowdAgentState.Moving && state != (byte)CrowdAgentState.Yielding)
                    continue;
                float2 step = ChosenVelocity[idx] * TimeStep;
                if (math.lengthsq(step) < 1e-10f)
                {
                    Velocity[idx] = float2.zero;
                    continue;
                }
                float2 safe = FindStaticSafeStep(idx, step);
                if (math.distancesq(safe, step) > 1e-8f) _hardClamps++;
                Position[idx] += safe;
                Velocity[idx] = safe / TimeStep;
                DistanceTravelled[idx] += math.length(safe);
                NewPositions[idx] = Position[idx];
                NewVelocities[idx] = Velocity[idx];
                HasMoved[idx] = 1;
            }
        }

        private void RadixSort()
        {
            NativeArray<ulong> keys = SortKeys;
            NativeArray<ulong> tempKeys = TempKeys;
            NativeArray<int> order = Order;
            NativeArray<int> tempOrder = TempOrder;
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0; i < 256; i++) SortCounts[i] = 0;
                int shift = pass * 8;
                for (int i = 0; i < Count; i++)
                {
                    int bucket = (int)((keys[i] >> shift) & 255UL);
                    SortCounts[bucket]++;
                }
                int sum = 0;
                for (int i = 0; i < 256; i++)
                {
                    int c = SortCounts[i];
                    SortCounts[i] = sum;
                    sum += c;
                }
                for (int i = 0; i < Count; i++)
                {
                    int bucket = (int)((keys[i] >> shift) & 255UL);
                    int dst = SortCounts[bucket]++;
                    tempOrder[dst] = order[i];
                    tempKeys[dst] = keys[i];
                }
                NativeArray<ulong> tk = keys; keys = tempKeys; tempKeys = tk;
                NativeArray<int> to = order; order = tempOrder; tempOrder = to;
            }
            // Eight passes: the result ends in the swapped buffers.
            SortKeys = keys;
            TempKeys = tempKeys;
            Order = order;
            TempOrder = tempOrder;
        }

        private float2 FindSafeStep(int i, float2 fullStep)
        {
            if (IsPrefixSafe(i, fullStep)) return fullStep;
            float lo = 0f;
            float hi = 1f;
            for (int k = 0; k < 8; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (IsPrefixSafe(i, fullStep * mid)) lo = mid;
                else hi = mid;
            }
            return lo < 0.02f ? float2.zero : fullStep * lo;
        }

        private float2 FindStaticSafeStep(int i, float2 fullStep)
        {
            if (GoalBoundaryAllows(i, Position[i] + fullStep) &&
                Grid.IsSegmentWalkable(Position[i], Position[i] + fullStep, 1.05f))
                return fullStep;
            float lo = 0f;
            float hi = 1f;
            for (int k = 0; k < 8; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Grid.IsSegmentWalkable(Position[i], Position[i] + fullStep * mid, 1.05f)) lo = mid;
                else hi = mid;
            }
            return lo < 0.02f ? float2.zero : fullStep * lo;
        }

        private bool IsPrefixSafe(int i, float2 step)
        {
            if (math.lengthsq(step) < 1e-10f) return true;
            float2 start = Position[i];
            float2 end = start + step;
            if (!Grid.IsWorldWalkable(end)) return false;
            if (!GoalBoundaryAllows(i, end)) return false;
            if (!Grid.IsSegmentWalkable(start, end, 1.05f)) return false;

            float sepSq = RequiredSeparation * RequiredSeparation;
            float queryRadius = RequiredSeparation + MaxSpeed * TimeStep + 0.05f;
            int x0 = (int)math.floor((end.x - queryRadius - MinX) / CellSize);
            int x1 = (int)math.floor((end.x + queryRadius - MinX) / CellSize);
            int z0 = (int)math.floor((end.y - queryRadius - MinZ) / CellSize);
            int z1 = (int)math.floor((end.y + queryRadius - MinZ) / CellSize);
            x0 = math.clamp(x0, 0, Cols - 1);
            x1 = math.clamp(x1, 0, Cols - 1);
            z0 = math.clamp(z0, 0, Rows - 1);
            z1 = math.clamp(z1, 0, Rows - 1);

            for (int z = z0; z <= z1; z++)
            {
                int row = z * Cols;
                for (int x = x0; x <= x1; x++)
                {
                    int cell = row + x;
                    int cstart = HashCellStart[cell];
                    int cend = HashCellStart[cell + 1];
                    for (int k = cstart; k < cend; k++)
                    {
                        int j = HashSorted[k];
                        if (j == i) continue;
                        float2 q;
                        if (HasMoved[j] != 0)
                        {
                            q = NewPositions[j];
                        }
                        else if (!PredictedCommit ||
                                 State[j] == (byte)CrowdAgentState.Arrived ||
                                 State[j] == (byte)CrowdAgentState.Unreachable)
                        {
                            q = Position[j];
                        }
                        else
                        {
                            q = Position[j] + ChosenVelocity[j] * TimeStep;
                        }
                        if (CrowdGrid.DistanceSqPointSegment(q, start, end) < sepSq) return false;
                    }
                }
            }
            return true;
        }

        private bool GoalBoundaryAllows(int i, float2 p)
        {
            if (!KeepInsideGoal || GoalRadius <= 0f) return true;
            if (State[i] == (byte)CrowdAgentState.Yielding) return true;
            float r = GoalRadius + 0.12f;
            if (math.distancesq(Position[i], GoalCenter) > r * r) return true;
            return math.distancesq(p, GoalCenter) <= r * r;
        }

        private void ResolveResidual()
        {
            float sep = RequiredSeparation;
            float sepSq = sep * sep;
            for (int pass = 0; pass < 5; pass++)
            {
                BuildHash();
                bool any = false;
                for (int i = 0; i < Count; i++)
                {
                    float2 pi = Position[i];
                    int x0 = (int)math.floor((pi.x - sep - MinX) / CellSize);
                    int x1 = (int)math.floor((pi.x + sep - MinX) / CellSize);
                    int z0 = (int)math.floor((pi.y - sep - MinZ) / CellSize);
                    int z1 = (int)math.floor((pi.y + sep - MinZ) / CellSize);
                    x0 = math.clamp(x0, 0, Cols - 1);
                    x1 = math.clamp(x1, 0, Cols - 1);
                    z0 = math.clamp(z0, 0, Rows - 1);
                    z1 = math.clamp(z1, 0, Rows - 1);
                    for (int z = z0; z <= z1; z++)
                    {
                        int row = z * Cols;
                        for (int x = x0; x <= x1; x++)
                        {
                            int cell = row + x;
                            int cstart = HashCellStart[cell];
                            int cend = HashCellStart[cell + 1];
                            for (int k = cstart; k < cend; k++)
                            {
                                int j = HashSorted[k];
                                if (j <= i) continue;
                                if (math.distancesq(Position[i], Position[j]) >= sepSq) continue;
                                any = true;
                                int revert = DistanceToSlot[i] >= DistanceToSlot[j] ? i : j;
                                Position[revert] -= Velocity[revert] * TimeStep;
                                Velocity[revert] = float2.zero;
                                NewPositions[revert] = Position[revert];
                                HasMoved[revert] = 0;
                                _rollbacks++;
                            }
                        }
                    }
                }
                _residualPasses++;
                if (!any) return;
            }

            for (int i = 0; i < Count; i++)
            {
                if (State[i] == (byte)CrowdAgentState.Arrived) continue;
                Position[i] -= Velocity[i] * TimeStep;
                Velocity[i] = float2.zero;
                HasMoved[i] = 0;
                NewPositions[i] = Position[i];
                _rollbacks++;
            }
        }

        private void BuildHash()
        {
            int cells = Cols * Rows;
            for (int i = 0; i <= cells; i++) HashCellStart[i] = 0;
            for (int i = 0; i < Count; i++)
            {
                int x = (int)math.floor((Position[i].x - MinX) / CellSize);
                int z = (int)math.floor((Position[i].y - MinZ) / CellSize);
                x = math.clamp(x, 0, Cols - 1);
                z = math.clamp(z, 0, Rows - 1);
                int c = z * Cols + x;
                HashAgentCell[i] = c;
                HashCellStart[c + 1]++;
            }
            for (int c = 1; c <= cells; c++) HashCellStart[c] += HashCellStart[c - 1];
            for (int c = 0; c < cells; c++) HashCursor[c] = HashCellStart[c];
            for (int i = 0; i < Count; i++)
            {
                int c = HashAgentCell[i];
                HashSorted[HashCursor[c]++] = i;
            }
        }
    }
}