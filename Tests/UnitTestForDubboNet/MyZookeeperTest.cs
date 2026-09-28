using DubboNet.Clients.RegistryClient;
using org.apache.zookeeper.data;

namespace UnitTestForDubboNet
{
    public class MyZookeeperTest
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Constructor_RejectsEmptyConnectionString(string connectionString)
        {
            Assert.Throws<ArgumentException>(() => new MyZookeeper(connectionString));
        }

        [Fact]
        public void Constructor_RejectsInvalidSessionTimeout()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MyZookeeper("127.0.0.1:2181", 0));
        }

        [Fact]
        public async Task DisposeAsync_IsIdempotentAndPreventsReconnect()
        {
            MyZookeeper client = new MyZookeeper("127.0.0.1:2181");

            await client.DisposeAsync();
            await client.DisposeAsync();

            Assert.False(client.IsConnected);
            Assert.False(client.CanWrite);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.ConnectZooKeeperAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.ExistsAsync("/"));
        }

        [Fact]
        public async Task ExistsAsync_WhenRegistryIsUnavailable_DoesNotPretendNodeIsMissing()
        {
            await using MyZookeeper client = new MyZookeeper("127.0.0.1:1", 100);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ExistsAsync("/", watcher: null, retryTime: 0));

            Assert.Contains("failed after 1 attempt", exception.Message);
        }

        [Fact]
        public async Task ConnectZooKeeperAsync_ObservesCancellation()
        {
            await using MyZookeeper client = new MyZookeeper("127.0.0.1:1", 1000);
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.ConnectZooKeeperAsync(cancellation.Token));
        }

        [Fact]
        public void MyStatClone_PreservesMyStatType()
        {
            Stat source = new Stat(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            MyZookeeper.MyStat stat = new MyZookeeper.MyStat(source);

            MyZookeeper.MyStat clone = Assert.IsType<MyZookeeper.MyStat>(stat.Clone());

            Assert.NotSame(stat, clone);
            Assert.Equal(stat.getVersion(), clone.getVersion());
            Assert.Equal(stat.getNumChildren(), clone.getNumChildren());
        }
    }
}
