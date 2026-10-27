using DubboNet.Clients.Helper;
using System.Globalization;
using System.Net;

namespace UnitTestForDubboNet;

public class ConsistentHashTest
{
    private sealed class PositionHash : ConsistentHash<int>
    {
        internal PositionHash() : base(1) { }
        public override int AddNodeHash(int node, int replicateIndex) => node;
        public override int GetNodeHash(string key) => int.Parse(key, CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData(-101, -100)]
    [InlineData(-100, -100)]
    [InlineData(-99, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 100)]
    [InlineData(100, 100)]
    [InlineData(101, -100)]
    public void RingSearchHandlesExactPointsBoundariesAndWraparound(int key, int expected)
    {
        var ring = new PositionHash();
        ring.Init(new[] { -100, 0, 100 });
        Assert.Equal(expected, ring.GetNode(key.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void EmptyAndSingleNodeRingsHaveDefinedBehavior()
    {
        var ring = new PositionHash();
        Assert.Throws<InvalidOperationException>(() => ring.GetNode("1"));
        ring.Add(42);
        Assert.Equal(42, ring.GetNode("-100"));
        Assert.Equal(42, ring.GetNode("42"));
        Assert.Equal(42, ring.GetNode("100"));
        ring.Remove(42);
        Assert.Throws<InvalidOperationException>(() => ring.GetNode("1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositionHash().Init(new[] { 1 }, 0));
    }

    [Fact]
    public void Utf8PreservesNonAsciiKeysAndUsesStableMurmurVectors()
    {
        Assert.Equal(unchecked((int)0x7f1ddbbd), ConsistentHash<int>.BetterHash("hello"));
        Assert.NotEqual(ConsistentHash<int>.BetterHash("中文"), ConsistentHash<int>.BetterHash("用户"));
        Assert.NotEqual(ConsistentHash<int>.BetterHash("中文"), ConsistentHash<int>.BetterHash("??"));
    }

    [Fact]
    public void EndpointHashUsesStableIdentityAndIsIndependentOfNodeOrder()
    {
        var endpoints = Enumerable.Range(0, 4).Select(i => new IPEndPoint(IPAddress.Loopback, 20880 + i)).ToArray();
        var first = new DubboSuiteConsistentHash(160);
        var second = new DubboSuiteConsistentHash(160);
        first.Init(endpoints);
        second.Init(endpoints.Reverse());
        Assert.Equal(ConsistentHash<IPEndPoint>.BetterHash("[127.0.0.1]:20880#0"), first.AddNodeHash(endpoints[0], 0));
        for (int i = 0; i < 1000; i++) Assert.Equal(first.GetNode("用户-" + i), second.GetNode("用户-" + i));
    }

    [Fact]
    public void MembershipChangesOnlyRemapKeysToTheNewNodeOrAwayFromTheRemovedNode()
    {
        var nodes = Enumerable.Range(0, 4).Select(i => new IPEndPoint(IPAddress.Loopback, 20880 + i)).ToArray();
        var ring = new DubboSuiteConsistentHash(160);
        ring.Init(nodes.Take(3));
        var before = Enumerable.Range(0, 2000).ToDictionary(i => i, i => ring.GetNode("key-" + i));
        ring.Add(nodes[3]);
        int changed = 0;
        foreach (var item in before)
        {
            var after = ring.GetNode("key-" + item.Key);
            if (!after.Equals(item.Value)) { changed++; Assert.Equal(nodes[3], after); }
        }
        Assert.InRange(changed, 250, 800);
        ring.Remove(nodes[3]);
        foreach (var item in before) Assert.Equal(item.Value, ring.GetNode("key-" + item.Key));
    }

    [Fact]
    public async Task ReadersNeverObserveHalfRebuiltRings()
    {
        var ring = new PositionHash();
        ring.Init(new[] { -100, 0, 100 });
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++) ring.Init(i % 2 == 0 ? new[] { -100, 100 } : new[] { -100, 0, 100 });
        });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++) Assert.Contains(ring.GetNode((i % 300 - 150).ToString()), new[] { -100, 0, 100 });
        })).Append(writer));
    }
}
