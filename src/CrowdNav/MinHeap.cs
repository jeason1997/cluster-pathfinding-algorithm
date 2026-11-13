using System;

namespace CrowdNav
{
    /// <summary>Small allocation-free binary min-heap for integer node ids.</summary>
    public sealed class MinHeap
    {
        private int[] _nodes;
        private float[] _keys;
        private int _count;

        public MinHeap(int capacity)
        {
            _nodes = new int[Math.Max(4, capacity)];
            _keys = new float[Math.Max(4, capacity)];
        }

        public int Count => _count;
        public float PeekKey => _keys[0];

        public void Clear() => _count = 0;

        public void Push(int node, float key)
        {
            if (_count == _nodes.Length)
            {
                Array.Resize(ref _nodes, _nodes.Length * 2);
                Array.Resize(ref _keys, _keys.Length * 2);
            }

            int i = _count++;
            _nodes[i] = node;
            _keys[i] = key;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_keys[parent] <= _keys[i]) break;
                Swap(i, parent);
                i = parent;
            }
        }

        public int Pop(out float key)
        {
            int result = _nodes[0];
            key = _keys[0];
            _count--;
            if (_count > 0)
            {
                _nodes[0] = _nodes[_count];
                _keys[0] = _keys[_count];
                int i = 0;
                while (true)
                {
                    int left = (i << 1) + 1;
                    if (left >= _count) break;
                    int right = left + 1;
                    int child = right < _count && _keys[right] < _keys[left] ? right : left;
                    if (_keys[i] <= _keys[child]) break;
                    Swap(i, child);
                    i = child;
                }
            }
            return result;
        }

        private void Swap(int a, int b)
        {
            int tn = _nodes[a];
            _nodes[a] = _nodes[b];
            _nodes[b] = tn;
            float tk = _keys[a];
            _keys[a] = _keys[b];
            _keys[b] = tk;
        }
    }
}
