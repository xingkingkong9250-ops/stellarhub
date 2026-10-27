using QL.World.Services;

namespace QL.World.Tests;

/// <summary>0.16.0 DUP-C: BK-tree 半径查询与暴力枚举结果必须完全一致。</summary>
public sealed class PhashBkTreeTests
{
    [Fact]
    public void Radius_search_matches_brute_force()
    {
        var random = new Random(20261027);
        var hashes = new List<(long Id, ulong Hash)>();

        // 随机基值 + 少量比特翻转，保证查询半径内既有命中也有远点。
        for (int i = 0; i < 80; i++)
        {
            ulong baseHash = RandomUlong(random);
            hashes.Add((i * 2 + 1, baseHash));

            ulong variant = baseHash;
            int flips = 1 + i % 3;
            for (int f = 0; f < flips; f++)
                variant ^= 1UL << ((i * 7 + f * 13) % 64);
            hashes.Add((i * 2 + 2, variant));
        }

        var tree = new PhashBkTree();
        foreach ((long id, ulong hash) in hashes)
            tree.Add(id, hash);

        const int maxDistance = 10;
        foreach ((long id, ulong hash) in hashes)
        {
            var expected = hashes
                .Where(candidate => candidate.Id != id &&
                    PerceptualHashService.HammingDistance(hash, candidate.Hash) <= maxDistance)
                .Select(candidate => (candidate.Id,
                    PerceptualHashService.HammingDistance(hash, candidate.Hash)))
                .OrderBy(item => item.Id)
                .ToList();

            var actual = new List<(long ImageId, int Distance)>();
            tree.Search(hash, maxDistance, id, actual);
            actual.Sort((x, y) => x.ImageId.CompareTo(y.ImageId));

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Search_includes_boundary_and_excludes_beyond()
    {
        var tree = new PhashBkTree();
        tree.Add(1, 0UL);
        tree.Add(2, (1UL << 10) - 1);   // 距离 10，应命中
        tree.Add(3, (1UL << 11) - 1);   // 距离 11，应排除

        var results = new List<(long ImageId, int Distance)>();
        tree.Search(0UL, 10, excludeId: 1, results);

        (long ImageId, int Distance) hit = Assert.Single(results);
        Assert.Equal(2L, hit.ImageId);
        Assert.Equal(10, hit.Distance);
    }

    [Fact]
    public void Add_ignores_duplicate_image_ids()
    {
        var tree = new PhashBkTree();
        tree.Add(1, 0UL);
        tree.Add(1, ulong.MaxValue);

        Assert.Equal(1, tree.Count);

        var results = new List<(long ImageId, int Distance)>();
        tree.Search(0UL, 10, excludeId: 0, results);
        (long ImageId, int Distance) hit = Assert.Single(results);
        Assert.Equal(1L, hit.ImageId);
        Assert.Equal(0, hit.Distance);
    }

    private static ulong RandomUlong(Random random) =>
        ((ulong)(uint)random.Next() << 32) | (uint)random.Next();
}
