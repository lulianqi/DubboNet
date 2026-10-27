using System.Collections.Concurrent;
using System.Reflection;
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using Nacos.V2;
using Nacos.V2.Naming.Dtos;

namespace UnitTestForDubboNet;

public class ConsumerRegistrationTest
{
    private static ConsumerRegistrationOptions Options() => new ConsumerRegistrationOptions
    {
        Enabled = true, ApplicationName = "orders-consumer", Host = "127.0.0.1", InstanceId = "test-instance"
    }.Snapshot();

    [Fact]
    public void Options_DefaultsAreDisabledAndEnabledRequiresApplication()
    {
        Assert.False(new ConsumerRegistrationOptions().Snapshot().Enabled);
        Assert.Throws<ArgumentException>(() => new ConsumerRegistrationOptions { Enabled = true }.Snapshot());
        Assert.Throws<ArgumentException>(() => new ConsumerRegistrationOptions { Host = "not-an-ip" }.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsumerRegistrationOptions { RetryInterval = TimeSpan.Zero }.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsumerRegistrationOptions { ShutdownTimeout = TimeSpan.Zero }.Snapshot());
    }

    [Fact]
    public void Options_AreCopiedAndGenerateIndependentInstanceIds()
    {
        var mutable = new ConsumerRegistrationOptions { Enabled = true, ApplicationName = " original ", Host = "127.0.0.1" };
        ConsumerRegistrationOptions snapshot = mutable.Snapshot();
        mutable.ApplicationName = "changed";
        Assert.Equal("original", snapshot.ApplicationName);
        Assert.NotEmpty(snapshot.InstanceId);
        Assert.NotEqual(snapshot.InstanceId, mutable.Snapshot().InstanceId);
    }

    [Fact]
    public async Task CreatingLeaseDoesNotRegisterUntilAServiceIsRecorded()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        await tracker.Request();
        Assert.Empty(registry.Registrations);
        Assert.Equal(0, registry.SessionChecks);
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        Assert.Single(registry.Registrations);
    }

    [Fact]
    public async Task ParallelCallsFromSameClientRegisterOnce()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => lease.Record("com.foo.Echo", "1", "prod"))));
        await tracker.Request();
        Assert.Single(registry.Registrations);
        await lease.ReleaseAsync();
        Assert.Single(registry.Deletions);
    }

    [Fact]
    public async Task SharedServiceIsRemovedOnlyAfterLastClientReleasesIt()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var first = tracker.CreateLease("/dubbo/");
        using var second = tracker.CreateLease("/dubbo/");
        first.Record("com.foo.Echo", null, null);
        second.Record("com.foo.Echo", null, null);
        second.Record("com.foo.Other", null, null);
        await tracker.Request();
        Assert.Equal(2, registry.Registrations.Count);
        await first.ReleaseAsync();
        Assert.Empty(registry.Deletions);
        await second.ReleaseAsync();
        Assert.Equal(2, registry.Deletions.Count);
        Assert.Empty(registry.Nodes);
    }

    [Fact]
    public async Task RootsVersionsAndGroupsRemainIsolated()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var first = tracker.CreateLease("/one/");
        using var second = tracker.CreateLease("/two/");
        first.Record("com.foo.Echo", "1", "prod");
        first.Record("com.foo.Echo", "2", "prod");
        first.Record("com.foo.Echo", "1", "dev");
        second.Record("com.foo.Echo", "1", "prod");
        await tracker.Request();
        Assert.Equal(4, registry.Nodes.Count);
        await first.ReleaseAsync();
        Assert.Single(registry.Nodes);
        Assert.Equal("/two/", registry.Nodes.Values.Single().Root);
    }

    [Fact]
    public async Task FailedRegisterIsRetriedAndDoesNotEscapeToCaller()
    {
        var registry = new FakeRegistry { FailRegister = true };
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.FailRegister = false;
        await tracker.Request();
        Assert.True(registry.Registrations.Count >= 2);
        Assert.Single(registry.Nodes);
    }

    [Fact]
    public async Task LostRegisterAcknowledgementStillRequiresDeregistration()
    {
        var registry = new FakeRegistry { FailRegister = true };
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        Assert.Single(registry.Nodes); // Server applied the write before the simulated timeout.
        await lease.ReleaseAsync();
        Assert.Single(registry.Deletions);
        Assert.Empty(registry.Nodes);
    }

    [Fact]
    public async Task FailedDeregistrationKeepsTombstoneForRetryOnRetainedConnection()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("DEFAULT_GROUP");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.FailDelete = true;
        await lease.ReleaseAsync();
        Assert.Single(registry.Nodes);
        registry.FailDelete = false;
        await tracker.Request();
        Assert.Empty(registry.Nodes);
        Assert.True(registry.Deletions.Count >= 2);
    }

    [Fact]
    public async Task LostDeleteAcknowledgementDoesNotHideANewOwner()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var first = tracker.CreateLease("/dubbo/");
        using var second = tracker.CreateLease("/dubbo/");
        first.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.FailDelete = true;
        registry.DeleteAppliedBeforeFailure = true;
        await first.ReleaseAsync();
        Assert.Empty(registry.Nodes);
        registry.FailDelete = false;
        second.Record("com.foo.Echo", null, null);
        await tracker.Request();
        Assert.Single(registry.Nodes);
        Assert.Equal(2, registry.Registrations.Count);
    }

    [Fact]
    public async Task ReleaseWhileRegistrationIsInFlightCleansUpLateWrite()
    {
        var registry = new FakeRegistry { RegisterGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await registry.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task cleanup = lease.ReleaseAsync();
        registry.RegisterGate.SetResult(true);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(registry.Deletions);
        Assert.Empty(registry.Nodes);
    }

    [Fact]
    public async Task NewOwnerDuringDeregistrationIsRegisteredAgain()
    {
        var registry = new FakeRegistry { DeleteGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var first = tracker.CreateLease("/dubbo/");
        using var second = tracker.CreateLease("/dubbo/");
        first.Record("com.foo.Echo", null, null);
        await tracker.Request();
        Task cleanup = first.ReleaseAsync();
        await registry.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        second.Record("com.foo.Echo", null, null);
        registry.DeleteGate.SetResult(true);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(registry.Nodes);
        Assert.Equal(2, registry.Registrations.Count);
    }

    [Fact]
    public async Task NewZooKeeperSessionReplaysOnlyActiveReferences()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var first = tracker.CreateLease("/dubbo/");
        using var second = tracker.CreateLease("/dubbo/");
        first.Record("com.foo.Echo", null, null);
        second.Record("com.foo.Other", null, null);
        await tracker.Request();
        await second.ReleaseAsync();
        registry.Nodes.Clear();
        registry.Session++;
        await tracker.Request();
        Assert.Single(registry.Nodes);
        Assert.Equal("com.foo.Echo", registry.Nodes.Values.Single().Service);
        Assert.Equal(3, registry.Registrations.Count);
    }

    [Fact]
    public async Task ReleasedClientCannotResurrectARegistration()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        await lease.ReleaseAsync();
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        Assert.Empty(registry.Registrations);
    }

    [Fact]
    public async Task ProcessShutdownRemovesAllReferencesAndRejectsNewOnes()
    {
        var registry = new FakeRegistry();
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        await tracker.ShutdownAsync();
        lease.Record("com.foo.Other", null, null);
        await tracker.Request();
        Assert.Empty(registry.Nodes);
        Assert.Single(registry.Registrations);
    }

    [Fact]
    public void ConsumerIdentityIsEncodedAndNeverAdvertisesAProvider()
    {
        var options = Options();
        options.ApplicationName = "orders & 中文";
        options.Host = "::1";
        var registration = new ConsumerRegistration(options, "owner", "/dubbo/", "com.foo.Echo", "1", "prod");
        string url = registration.ConsumerUrl;
        Assert.StartsWith("consumer://[::1]/com.foo.Echo?", url);
        Assert.Contains("category=consumers", url);
        Assert.Equal(url, Uri.UnescapeDataString(registration.ZooKeeperPath.Split('/').Last()));
        Assert.Contains("application=orders%20%26%20", url);
        Assert.Equal("consumers:com.foo.Echo:1:prod", registration.NacosServiceName);
    }

    [Fact]
    public void NacosEphemeralIdentityIsUniqueForConnectionsOnSameHost()
    {
        var first = new ConsumerRegistration(Options(), "connection-a", "DEFAULT_GROUP", "com.foo.Echo", null, null);
        var second = new ConsumerRegistration(Options(), "connection-b", "DEFAULT_GROUP", "com.foo.Echo", null, null);
        Instance instanceA = NacosConsumerRegistry.CreateInstance(first);
        Instance instanceB = NacosConsumerRegistry.CreateInstance(second);
        Assert.True(instanceA.Ephemeral);
        Assert.Equal(0, instanceA.Port);
        Assert.NotEqual(instanceA.ClusterName, instanceB.ClusterName);
        Assert.Equal("consumer", instanceA.Metadata["side"]);
        Assert.Equal("consumers", instanceA.Metadata["category"]);
        Assert.Equal("orders-consumer", instanceA.Metadata["application"]);
    }

    [Fact]
    public void ServiceIdentityDoesNotCollideOnEmbeddedSeparators()
    {
        Assert.NotEqual(
            ConsumerRegistration.MakeKey("/dubbo/", "com.foo.Echo", "1\nprod", ""),
            ConsumerRegistration.MakeKey("/dubbo/", "com.foo.Echo", "1", "prod\n"));
    }

    [Fact]
    public void AuthenticatedZooKeeperDoesNotCreateWorldWritableNodes()
    {
        var authenticated = ZooKeeperConsumerRegistry.GetCreateAcl(new MyZookeeper.MyAuthInfo
        { Scheme = "digest", Auth = System.Text.Encoding.UTF8.GetBytes("consumer:secret") });
        Assert.Equal("auth", Assert.Single(authenticated).getId().getScheme());
        Assert.Equal("world", Assert.Single(ZooKeeperConsumerRegistry.GetCreateAcl(null)).getId().getScheme());
    }

    [Fact]
    public async Task NacosAdapterUsesSdkForRegistrationAndMatchingDeregistration()
    {
        var naming = DispatchProxy.Create<INacosNamingService, SdkProxy>();
        var calls = new List<(string Method, object?[] Arguments)>();
        ((SdkProxy)(object)naming).Handler = (method, args) => { calls.Add((method.Name, args)); return Task.CompletedTask; };
        var config = DispatchProxy.Create<INacosConfigService, SdkProxy>();
        ((SdkProxy)(object)config).Handler = (_, _) => Task.CompletedTask;
        using var client = new MyNacos("nacos://localhost:8848", naming, config, Timeout.InfiniteTimeSpan);
        var adapter = new NacosConsumerRegistry(client);
        var registration = new ConsumerRegistration(Options(), "owner", "prod", "com.foo.Echo", "1", "group");
        await adapter.RegisterAsync(registration);
        await adapter.UnregisterAsync(registration);
        Assert.Equal("RegisterInstance", calls[0].Method);
        Assert.Equal("DeregisterInstance", calls[1].Method);
        Assert.Equal("consumers:com.foo.Echo:1:group", calls[0].Arguments[0]);
        Assert.Equal("prod", calls[0].Arguments[1]);
        var registered = Assert.IsType<Instance>(calls[0].Arguments[2]);
        var removed = Assert.IsType<Instance>(calls[1].Arguments[2]);
        Assert.True(registered.Ephemeral);
        Assert.Equal(registered.Ip, removed.Ip);
        Assert.Equal(registered.Port, removed.Port);
        Assert.Equal(registered.ClusterName, removed.ClusterName);
    }

    public class SdkProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args!);
    }

    private sealed class FakeRegistry : IConsumerRegistrationRegistry
    {
        public ConcurrentQueue<ConsumerRegistration> Registrations { get; } = new();
        public ConcurrentQueue<ConsumerRegistration> Deletions { get; } = new();
        public ConcurrentDictionary<string, ConsumerRegistration> Nodes { get; } = new();
        public bool FailRegister, FailDelete, DeleteAppliedBeforeFailure;
        public long Session = 1;
        public int SessionChecks;
        public TaskCompletionSource<bool>? RegisterGate, DeleteGate;
        public TaskCompletionSource<bool> RegisterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> GetSessionAsync() { Interlocked.Increment(ref SessionChecks); return Task.FromResult(Session); }
        public async Task RegisterAsync(ConsumerRegistration registration)
        {
            Registrations.Enqueue(registration);
            RegisterEntered.TrySetResult(true);
            if (RegisterGate != null) await RegisterGate.Task;
            Nodes[registration.Key] = registration;
            if (FailRegister) throw new IOException("lost acknowledgement");
        }
        public async Task UnregisterAsync(ConsumerRegistration registration)
        {
            Deletions.Enqueue(registration);
            DeleteEntered.TrySetResult(true);
            if (DeleteGate != null) await DeleteGate.Task;
            if (FailDelete && !DeleteAppliedBeforeFailure) throw new IOException("offline");
            Nodes.TryRemove(registration.Key, out _);
            if (FailDelete) throw new IOException("lost deletion acknowledgement");
        }
    }
}
