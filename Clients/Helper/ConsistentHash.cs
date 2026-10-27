using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace DubboNet.Clients.Helper
{
    /// <summary>一致性 Hash 环；更新原子发布，读取使用不可变快照。EN: Consistent hash ring with atomically published immutable snapshots.</summary>
    public class ConsistentHash<T>
    {
        private sealed class Ring
        {
            internal readonly int[] Keys;
            internal readonly T[] Values;
            internal Ring(SortedDictionary<int, T> points)
            { Keys = points.Keys.ToArray(); Values = points.Values.ToArray(); }
        }

        private readonly object _sync = new();
        private List<T> _nodes = new();
        private int _replicate;
        private Ring _ring = new(new SortedDictionary<int, T>());

        public ConsistentHash(int replicate = 100)
        {
            if (replicate <= 0) throw new ArgumentOutOfRangeException(nameof(replicate));
            _replicate = replicate;
        }

        public void Init(IEnumerable<T> nodes = null)
        {
            lock (_sync) Init(nodes, _replicate);
        }

        public void Init(IEnumerable<T> nodes, int replicate)
        {
            if (replicate <= 0) throw new ArgumentOutOfRangeException(nameof(replicate));
            lock (_sync)
            {
                var snapshot = nodes?.Distinct().ToList() ?? new List<T>();
                Publish(snapshot, replicate);
            }
        }

        public void Add(T node)
        {
            if (node is null) throw new ArgumentNullException(nameof(node));
            lock (_sync)
            {
                if (_nodes.Contains(node)) return;
                var nodes = new List<T>(_nodes) { node };
                Publish(nodes, _replicate);
            }
        }

        public void Remove(T node)
        {
            lock (_sync)
            {
                var nodes = new List<T>(_nodes);
                if (!nodes.Remove(node)) throw new InvalidOperationException("Cannot remove a node that was not added.");
                // Rebuild from logical nodes: hash collisions must not remove another node's points.
                Publish(nodes, _replicate);
            }
        }

        private void Publish(List<T> nodes, int replicate)
        {
            var points = new SortedDictionary<int, T>();
            foreach (T node in nodes)
            {
                if (node is null) throw new ArgumentException("Hash nodes cannot contain null.", nameof(nodes));
                for (int i = 0; i < replicate; i++) points[AddNodeHash(node, i)] = node;
            }
            var ring = new Ring(points);
            _nodes = nodes;
            _replicate = replicate;
            Volatile.Write(ref _ring, ring);
        }

        /// <summary>获取 key 对应的节点。EN: Resolves the node owning a key.</summary>
        public T GetNode(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            Ring ring = Volatile.Read(ref _ring);
            if (ring.Keys.Length == 0) throw new InvalidOperationException("The consistent hash ring is empty.");
            int index = Array.BinarySearch(ring.Keys, GetNodeHash(key));
            if (index < 0) index = ~index;
            if (index == ring.Keys.Length) index = 0;
            return ring.Values[index];
        }

        // Override with a stable node identity when cross-process determinism is required.
        public virtual int AddNodeHash(T node, int replicateIndex) => BetterHash(
            node.GetHashCode().ToString(CultureInfo.InvariantCulture) + "#" + replicateIndex.ToString(CultureInfo.InvariantCulture));

        public virtual int GetNodeHash(string key) => BetterHash(key);

        public static int BetterHash(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return unchecked((int)MyCommonHelper.EncryptionHelper.MyHash.GetMurmurHash2(Encoding.UTF8.GetBytes(key)));
        }
    }
}
