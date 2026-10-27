namespace QL.World.Services;

/// <summary>
/// 0.16.0 DUP-C: 64 位感知哈希的 BK-tree。
/// 以汉明距离为边权重，半径查询只下探 [d-max, d+max] 范围内的子树，
/// 避免全库两两比对。节点允许重复哈希（边 0 链），因此多张完全同图也能查全。
/// 仅由 <see cref="DuplicateScanService"/> 在内存中使用，不做持久化。
/// </summary>
internal sealed class PhashBkTree
{
    private readonly Dictionary<long, ulong> _hashes = [];
    private Node? _root;

    /// <summary>已插入的去重图片数（同一 imageId 重复插入被忽略）。</summary>
    public int Count => _hashes.Count;

    /// <summary>插入一张图的哈希；imageId 已存在时保持首次值。</summary>
    public void Add(long imageId, ulong hash)
    {
        if (!_hashes.TryAdd(imageId, hash))
            return;

        if (_root is null)
        {
            _root = new Node(hash, imageId);
            return;
        }

        Node current = _root;
        while (true)
        {
            int distance = PerceptualHashService.HammingDistance(hash, current.Hash);
            if (!current.Children.TryGetValue(distance, out Node? child))
            {
                current.Children[distance] = new Node(hash, imageId);
                return;
            }
            current = child;
        }
    }

    /// <summary>
    /// 查询与 <paramref name="hash"/> 汉明距离 ≤ <paramref name="maxDistance"/>
    /// 的所有节点，追加到 <paramref name="results"/>（含距离）。
    /// <paramref name="excludeId"/> 用于排除查询图自身。
    /// </summary>
    public void Search(
        ulong hash,
        int maxDistance,
        long excludeId,
        List<(long ImageId, int Distance)> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (_root is null)
            return;

        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            int distance = PerceptualHashService.HammingDistance(hash, node.Hash);

            if (distance <= maxDistance && node.ImageId != excludeId)
                results.Add((node.ImageId, distance));

            int low = distance - maxDistance;
            int high = distance + maxDistance;
            foreach ((int edge, Node child) in node.Children)
            {
                if (edge >= low && edge <= high)
                    stack.Push(child);
            }
        }
    }

    private sealed class Node
    {
        public Node(ulong hash, long imageId)
        {
            Hash = hash;
            ImageId = imageId;
        }

        public ulong Hash { get; }
        public long ImageId { get; }
        public Dictionary<int, Node> Children { get; } = [];
    }
}
