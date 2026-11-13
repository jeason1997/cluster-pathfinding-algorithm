using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CrowdNav.Hpc
{
    [BurstCompile(CompileSynchronously = true)]
    public struct CrowdHashBuildJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Position;
        public NativeArray<float2> PrevPosition;
        public NativeArray<int> CellStart;
        public NativeArray<int> Cursor;
        public NativeArray<int> Sorted;
        public NativeArray<int> AgentCell;

        public int Count;
        public int Cols;
        public int Rows;
        public float CellSize;
        public float MinX;
        public float MinZ;

        public void Execute()
        {
            int cells = Cols * Rows;
            for (int i = 0; i <= cells; i++) CellStart[i] = 0;
            for (int i = 0; i < Count; i++)
            {
                int x = (int)math.floor((Position[i].x - MinX) / CellSize);
                int z = (int)math.floor((Position[i].y - MinZ) / CellSize);
                x = math.clamp(x, 0, Cols - 1);
                z = math.clamp(z, 0, Rows - 1);
                int c = z * Cols + x;
                AgentCell[i] = c;
                CellStart[c + 1]++;
                PrevPosition[i] = Position[i];
            }
            for (int c = 1; c <= cells; c++) CellStart[c] += CellStart[c - 1];
            for (int c = 0; c < cells; c++) Cursor[c] = CellStart[c];
            for (int i = 0; i < Count; i++)
            {
                int c = AgentCell[i];
                Sorted[Cursor[c]++] = i;
            }
        }
    }
}