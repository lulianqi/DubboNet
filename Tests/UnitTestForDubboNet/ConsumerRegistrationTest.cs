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

    [Fact]
    public void SnapshotInheritsIdentityWithoutMutatingPreviousOptions()
    {
        var previous = Options();
        var disabled = new ConsumerRegistrationOptions { Enabled = false }.Snapshot(previous);
        var enabled = new ConsumerRegistrationOptions { Enabled = true }.Snapshot(disabled);
        Assert.True(previous.Enabled);
        Assert.Equal(previous.ApplicationName, enabled.ApplicationName);
        Assert.Equal(previous.Host, enabled.Host);
        Assert.Equal(previous.InstanceId, enabled.InstanceId);
    }

    [Fact]
    public async Task LateEnableBackfillsExistingClientsAcrossSharedAndDifferentRegistries()
    {
        var manager = new ConsumerRegistrationManager();
        var one = new FakeRegistry();
        var two = new FakeRegistry();
        using var first = manager.CreateLease(one, () => one, "/dubbo/");
        using var second = manager.CreateLease(one, () => throw new Exception("shared registry recreated"), "/dubbo/");
        using var other = manager.CreateLease(two, () => two, "/other/");
        first.Record("com.foo.Echo", "1", "prod");
        second.Record("com.foo.Echo", "1", "prod");
        other.Record("com.foo.Other", null, null);
        await manager.FlushAsync();
        Assert.Equal(0, one.SessionChecks + two.SessionChecks);
        manager.Configure(Options());
        await manager.FlushAsync();
        Assert.Single(one.Nodes);
        Assert.Single(two.Nodes);
        await first.ReleaseAsync();
        Assert.Single(one.Nodes);
        await manager.ShutdownAsync();
        Assert.Empty(one.Nodes);
        Assert.Empty(two.Nodes);
    }

    [Fact]
    public async Task RepeatedConfigureDisablesAndReenablesWithoutChangingGeneratedIdentity()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry();
        var mutable = new ConsumerRegistrationOptions { Enabled = true, ApplicationName = "orders", Host = "127.0.0.1" };
        manager.Configure(mutable);
        using var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await manager.FlushAsync();
        var original = registry.Nodes.Values.Single();
        mutable.Enabled = false;
        await manager.FlushAsync();
        Assert.Single(registry.Nodes); // Mutating the caller's object alone has no effect.
        manager.Configure(new ConsumerRegistrationOptions { Enabled = false });
        await manager.FlushAsync();
        Assert.Empty(registry.Nodes);
        int checks = registry.SessionChecks;
        lease.Record("com.foo.Other", null, null);
        await manager.FlushAsync();
        Assert.Equal(checks, registry.SessionChecks);
        manager.Configure(new ConsumerRegistrationOptions { Enabled = true });
        await manager.FlushAsync();
        Assert.Equal(2, registry.Nodes.Count);
        Assert.All(registry.Nodes.Values, value => Assert.Equal(original.Metadata["instance-id"], value.Metadata["instance-id"]));
        await manager.ShutdownAsync();
    }

    [Fact]
    public async Task InvalidReconfigurationIsAtomicAndDoesNotDisableExistingRegistrations()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry();
        manager.Configure(Options());
        using var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await manager.FlushAsync();
        Assert.Throws<ArgumentException>(() => manager.Configure(new ConsumerRegistrationOptions { Enabled = false, Host = "invalid" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.Configure(new ConsumerRegistrationOptions { RetryInterval = TimeSpan.Zero }));
        await manager.FlushAsync();
        Assert.Single(registry.Nodes);
        Assert.Empty(registry.Deletions);
        await manager.ShutdownAsync();
    }

    [Fact]
    public async Task ReleasedDisabledClientIsNotBackfilled()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry();
        using var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await lease.ReleaseAsync();
        manager.Configure(Options());
        await manager.FlushAsync();
        Assert.Empty(registry.Registrations);
        Assert.Equal(0, registry.SessionChecks);
        await manager.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableCleansUpLateRegistrationIncludingLostAcknowledgement(bool fail)
    {
        var registry = new FakeRegistry { FailRegister = fail, RegisterGate = NewGate() };
        var options = Options();
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await registry.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tracker.Configure(new ConsumerRegistrationOptions { Enabled = false }.Snapshot(options));
        Task flush = tracker.Request(forceRetry: false);
        registry.RegisterGate.SetResult(true);
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(registry.Nodes);
        Assert.Single(registry.Deletions);
        int checks = registry.SessionChecks;
        await tracker.Request();
        Assert.Equal(checks, registry.SessionChecks);
    }

    [Fact]
    public async Task ReenableWhileDeleteIsInFlightRestoresRegistration()
    {
        var registry = new FakeRegistry { DeleteGate = NewGate() };
        var options = Options();
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        tracker.Configure(new ConsumerRegistrationOptions { Enabled = false }.Snapshot(options));
        await registry.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tracker.Configure(options);
        Task flush = tracker.Request();
        registry.DeleteGate.SetResult(true);
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(registry.Nodes);
        Assert.Equal(2, registry.Registrations.Count);
    }

    [Fact]
    public async Task ReenableRepairsADeleteThatWasAppliedButLostItsAcknowledgement()
    {
        var registry = new FakeRegistry();
        var options = Options();
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.FailDelete = registry.DeleteAppliedBeforeFailure = true;
        tracker.Configure(new ConsumerRegistrationOptions { Enabled = false }.Snapshot(options));
        await tracker.Request();
        Assert.Empty(registry.Nodes);
        registry.FailDelete = false;
        tracker.Configure(options);
        await tracker.Request();
        Assert.Single(registry.Nodes);
        Assert.Equal(2, registry.Registrations.Count);
    }

    [Fact]
    public async Task IdentityMigrationWaitsForOldDeregistrationAndUsesLatestConfiguration()
    {
        var registry = new FakeRegistry();
        var options = Options();
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.FailDelete = true;
        var replacement = Options();
        replacement.ApplicationName = "replacement";
        replacement.Host = "127.0.0.2";
        tracker.Configure(replacement);
        await tracker.Request();
        Assert.Single(registry.Registrations);
        Assert.Equal("orders-consumer", registry.Nodes.Values.Single().Metadata["application"]);
        replacement = Options();
        replacement.ApplicationName = "latest";
        replacement.InstanceId = "new-instance";
        tracker.Configure(replacement);
        registry.FailDelete = false;
        await tracker.Request();
        Assert.Single(registry.Nodes);
        Assert.Equal("latest", registry.Nodes.Values.Single().Metadata["application"]);
        Assert.Equal("new-instance", registry.Nodes.Values.Single().Metadata["instance-id"]);
        Assert.Equal(2, registry.Registrations.Count);
    }

    [Fact]
    public async Task RapidChangesDuringRegistrationOnlyPublishTheLatestReplacement()
    {
        var registry = new FakeRegistry { RegisterGate = NewGate() };
        var options = Options();
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await registry.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 100; i++)
        {
            var update = Options();
            update.Enabled = i % 2 == 0;
            update.ApplicationName = "revision-" + i;
            tracker.Configure(update);
        }
        var final = Options();
        final.ApplicationName = "final";
        tracker.Configure(final);
        Task flush = tracker.Request();
        registry.RegisterGate.SetResult(true);
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(registry.Nodes);
        Assert.Equal("final", registry.Nodes.Values.Single().Metadata["application"]);
        Assert.Equal(2, registry.Registrations.Count);
        Assert.Single(registry.Deletions);
    }

    [Fact]
    public async Task DisabledSessionChangeDoesNotReplayUntilEnabled()
    {
        var registry = new FakeRegistry();
        var options = Options();
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        tracker.Configure(new ConsumerRegistrationOptions { Enabled = false }.Snapshot(options));
        await tracker.Request();
        registry.Session++;
        await tracker.Request();
        Assert.Single(registry.Registrations);
        tracker.Configure(options);
        await tracker.Request();
        Assert.Equal(2, registry.Registrations.Count);
        Assert.Single(registry.Nodes);
    }

    [Fact]
    public async Task FailedSessionRecoveryRetriesAndOnlyReplaysLiveReferences()
    {
        var registry = new FakeRegistry { FailSession = true };
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var first = tracker.CreateLease("/dubbo/");
        using var second = tracker.CreateLease("/dubbo/");
        first.Record("com.foo.Echo", null, null);
        second.Record("com.foo.Other", null, null);
        await tracker.Request();
        Assert.Empty(registry.Registrations);
        await second.ReleaseAsync();
        registry.FailSession = false;
        registry.Session++;
        await tracker.Request();
        Assert.Single(registry.Nodes);
        Assert.Equal("com.foo.Echo", registry.Nodes.Values.Single().Service);
    }

    [Fact]
    public async Task AutomaticRetryUsesBackoffAndExplicitFlushCanRetryImmediately()
    {
        var registry = new FakeRegistry { FailRegister = true };
        var options = Options();
        options.RetryInterval = TimeSpan.FromSeconds(10); // A virtual clock avoids sleeping for backoff.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var tracker = new ConsumerRegistrationTracker(registry, options, now: () => now);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request(forceRetry: false);
        Assert.Single(registry.Registrations);
        await tracker.Request(forceRetry: false);
        Assert.Single(registry.Registrations);
        now = now.AddSeconds(10);
        await tracker.Request(forceRetry: false);
        Assert.Equal(2, registry.Registrations.Count);
        now = now.AddSeconds(10);
        await tracker.Request(forceRetry: false);
        Assert.Equal(2, registry.Registrations.Count); // Second failure waits 20 seconds, not 10.
        now = now.AddSeconds(10);
        await tracker.Request(forceRetry: false);
        Assert.Equal(3, registry.Registrations.Count);
        registry.FailRegister = false;
        await tracker.Request();
        Assert.Equal(4, registry.Registrations.Count);
        Assert.Single(registry.Nodes);
    }

    [Fact]
    public async Task BackgroundTimerRetriesWithoutRpcOrFlush()
    {
        var registry = new FakeRegistry { FailDelete = true };
        var options = Options();
        options.RetryInterval = TimeSpan.FromSeconds(1);
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        await lease.ReleaseAsync();
        Assert.Single(registry.Nodes);
        registry.FailDelete = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!registry.Nodes.IsEmpty) await Task.Delay(25, deadline.Token);
        Assert.True(registry.Deletions.Count >= 2);
    }

    [Fact]
    public async Task LogsIdentifyFailedOperationAndRecoveryWithoutLeakingExceptionCredentials()
    {
        var warnings = new ConcurrentQueue<string>();
        var info = new ConcurrentQueue<string>();
        var registry = new FakeRegistry
        {
            FailRegister = true,
            FailureException = new InvalidOperationException("nacos://user:secret-password@host", new IOException("token=private"))
        };
        using var tracker = new ConsumerRegistrationTracker(registry, Options(), warnings.Enqueue, info.Enqueue);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", "1", "prod");
        await tracker.Request();
        string message = warnings.First();
        Assert.Contains("operation=register", message);
        Assert.Contains("service=com.foo.Echo version=1 group=prod", message);
        Assert.Contains("connection=", message);
        Assert.Contains("InvalidOperationException -> IOException", message);
        Assert.Contains("retryAfter=", message);
        Assert.DoesNotContain("secret-password", string.Join("", warnings));
        Assert.DoesNotContain("private", string.Join("", warnings));
        registry.FailRegister = false;
        await tracker.Request();
        Assert.Contains(info, line => line.Contains("register-recovered"));
    }

    [Fact]
    public async Task BrokenLogSinkDoesNotBreakRetryOrDeregistration()
    {
        var registry = new FakeRegistry { FailRegister = true };
        using var tracker = new ConsumerRegistrationTracker(registry, Options(), _ => throw new IOException("logging failed"));
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.FailRegister = false;
        await tracker.Request();
        await lease.ReleaseAsync();
        Assert.Empty(registry.Nodes);
    }

    [Fact]
    public async Task ConfigureAndClientCreationAreSerializedAcrossConcurrentCallers()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry();
        manager.Configure(Options());
        var leases = new ConcurrentBag<ConsumerRegistrationLease>();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            if (i % 3 == 0) manager.Configure(new ConsumerRegistrationOptions { Enabled = i % 2 == 0 });
            var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
            lease.Record("com.foo.Echo", null, null);
            leases.Add(lease);
        })));
        manager.Configure(new ConsumerRegistrationOptions { Enabled = true });
        await manager.FlushAsync();
        Assert.Single(registry.Nodes);
        await Task.WhenAll(leases.Select(lease => lease.ReleaseAsync()));
        await manager.FlushAsync();
        Assert.Empty(registry.Nodes);
        await manager.ShutdownAsync();
    }

    [Fact]
    public async Task ShutdownCannotBeUndoneByReconfiguration()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry();
        manager.Configure(Options());
        using var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await manager.FlushAsync();
        await manager.ShutdownAsync();
        Assert.Throws<InvalidOperationException>(() => manager.Configure(Options()));
        Assert.Null(manager.CreateLease(registry, () => registry, "/dubbo/"));
        Assert.Empty(registry.Nodes);
    }

    [Fact]
    public async Task CancelledFlushDoesNotCancelBackgroundCleanup()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry { RegisterGate = NewGate() };
        manager.Configure(Options());
        using var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await registry.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.FlushAsync(cancellation.Token));
        manager.Configure(new ConsumerRegistrationOptions { Enabled = false });
        registry.RegisterGate.SetResult(true);
        await manager.FlushAsync();
        Assert.Empty(registry.Nodes);
        await manager.ShutdownAsync();
    }

    [Fact]
    public async Task OneFailingServiceDoesNotPreventOtherServicesFromRegistering()
    {
        var registry = new FakeRegistry { FailingService = "com.foo.Broken" };
        using var tracker = new ConsumerRegistrationTracker(registry, Options());
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Broken", null, null);
        lease.Record("com.foo.Working", null, null);
        await tracker.Request();
        Assert.Contains(registry.Nodes.Values, value => value.Service == "com.foo.Working");
        registry.FailingService = null;
        await tracker.Request();
        Assert.Equal(2, registry.Nodes.Count);
    }

    [Fact]
    public async Task RepeatedFailuresAreRateLimitedAndSuccessfulRecoveryIsLogged()
    {
        var warnings = new ConcurrentQueue<string>();
        var info = new ConcurrentQueue<string>();
        var registry = new FakeRegistry { FailRegister = true };
        using var tracker = new ConsumerRegistrationTracker(registry, Options(), warnings.Enqueue, info.Enqueue);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request(forceRetry: false);
        for (int i = 1; i < 10; i++) await tracker.Request();
        Assert.Equal(10, registry.Registrations.Count);
        Assert.Equal(4, warnings.Count); // Failure counts 1, 2, 4, 8, not every attempt.
        registry.FailRegister = false;
        await tracker.Request();
        Assert.Contains(info, line => line.Contains("register-recovered") && line.Contains("failures=10"));
    }

    [Fact]
    public async Task SessionTimerReplaysLiveConsumersWithoutFurtherCalls()
    {
        var registry = new FakeRegistry();
        var options = Options();
        options.RetryInterval = TimeSpan.FromSeconds(1);
        using var tracker = new ConsumerRegistrationTracker(registry, options);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await tracker.Request();
        registry.Nodes.Clear();
        Interlocked.Increment(ref registry.Session);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (registry.Nodes.IsEmpty) await Task.Delay(25, deadline.Token);
        Assert.Equal(2, registry.Registrations.Count);
    }

    [Fact]
    public async Task UpdatedShutdownTimeoutBoundsDisposeButCleanupContinues()
    {
        var registry = new FakeRegistry { RegisterGate = NewGate() };
        var options = Options();
        var warnings = new ConcurrentQueue<string>();
        using var tracker = new ConsumerRegistrationTracker(registry, options, warnings.Enqueue);
        using var lease = tracker.CreateLease("/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await registry.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var update = Options();
        update.ShutdownTimeout = TimeSpan.FromMilliseconds(50);
        tracker.Configure(update);
        await Task.Run(() => lease.Dispose()).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(warnings, line => line.Contains("release-timeout"));
        registry.RegisterGate.SetResult(true);
        await tracker.Request();
        Assert.Empty(registry.Nodes);
        Assert.Single(registry.Deletions);
    }

    [Fact]
    public async Task ExplicitRegistryDrainDeregistersBeforeStoppingItsTracker()
    {
        var manager = new ConsumerRegistrationManager();
        var registry = new FakeRegistry();
        manager.Configure(Options());
        using var lease = manager.CreateLease(registry, () => registry, "/dubbo/");
        lease.Record("com.foo.Echo", null, null);
        await manager.FlushAsync();
        manager.StopRegistry(registry, drain: true);
        Assert.Empty(registry.Nodes);
        manager.Configure(new ConsumerRegistrationOptions { Enabled = true });
        await manager.FlushAsync();
        Assert.Single(registry.Registrations);
    }

    private static TaskCompletionSource<bool> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public bool FailRegister, FailDelete, DeleteAppliedBeforeFailure, FailSession;
        public Exception? FailureException;
        public string? FailingService;
        public long Session = 1;
        public int SessionChecks;
        public TaskCompletionSource<bool>? RegisterGate, DeleteGate;
        public TaskCompletionSource<bool> RegisterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> GetSessionAsync()
        {
            Interlocked.Increment(ref SessionChecks);
            return FailSession ? Task.FromException<long>(new IOException("session offline")) : Task.FromResult(Session);
        }
        public async Task RegisterAsync(ConsumerRegistration registration)
        {
            Registrations.Enqueue(registration);
            RegisterEntered.TrySetResult(true);
            if (RegisterGate != null) await RegisterGate.Task;
            Nodes[registration.ZooKeeperPath] = registration;
            if (FailRegister || registration.Service == FailingService) throw FailureException ?? new IOException("lost acknowledgement");
        }
        public async Task UnregisterAsync(ConsumerRegistration registration)
        {
            Deletions.Enqueue(registration);
            DeleteEntered.TrySetResult(true);
            if (DeleteGate != null) await DeleteGate.Task;
            if (FailDelete && !DeleteAppliedBeforeFailure) throw new IOException("offline");
            Nodes.TryRemove(registration.ZooKeeperPath, out _);
            if (FailDelete) throw new IOException("lost deletion acknowledgement");
        }
    }
}
