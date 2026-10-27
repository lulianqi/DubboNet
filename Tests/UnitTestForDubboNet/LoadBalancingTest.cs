using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.Clients.LoadBalancing;
using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using static DubboNet.Clients.DubboClient;

namespace UnitTestForDubboNet;

public class LoadBalancingTest
{
    [Fact]
    public void RandomFollowsConfiguredWeightsAndSupportsTotalsLargerThanInt32()
    {
        using var fixture = new Fixture(1, 3);
        var counts = new int[2];
        for (int i = 0; i < 20000; i++) counts[fixture.Index(fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.Random))]++;
        Assert.InRange(counts[0], 4700, 5300);
        fixture.Update(int.MaxValue, int.MaxValue);
        for (int i = 0; i < 100; i++) Assert.NotNull(fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.Random));
    }

    [Fact]
    public async Task RoundRobinPreservesExactRatiosUnderParallelSelection()
    {
        using var fixture = new Fixture(1, 3);
        var counts = new int[2];
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++) Interlocked.Increment(ref counts[fixture.Index(fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.RoundRobin))]);
        })));
        Assert.Equal(new[] { 4000, 12000 }, counts);
        fixture.Update(3, 1);
        counts = new int[2];
        for (int i = 0; i < 400; i++) counts[fixture.Index(fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.RoundRobin))]++;
        Assert.Equal(new[] { 300, 100 }, counts);
    }

    [Theory]
    [InlineData(LoadBalanceMode.RoundRobin)]
    [InlineData(LoadBalanceMode.LeastActive)]
    [InlineData(LoadBalanceMode.P2CLoadBalance)]
    [InlineData(LoadBalanceMode.ShortestResponse)]
    public void StrategiesSupportLargeWeightTotals(LoadBalanceMode mode)
    {
        using var fixture = new Fixture(int.MaxValue, int.MaxValue);
        for (int i = 0; i < 100; i++) Assert.NotNull(fixture.Driver.GetDubboActuatorSuite(mode));
    }

    [Theory]
    [InlineData(LoadBalanceMode.LeastActive)]
    [InlineData(LoadBalanceMode.P2CLoadBalance)]
    public void BusyProviderReceivesNoNewRequestsWhenAnotherProviderIsIdle(LoadBalanceMode mode)
    {
        using var fixture = new Fixture(1, 1);
        using var busy = fixture.Reserve(0);
        for (int i = 0; i < 100; i++)
        {
            using var selected = fixture.Select(mode);
            Assert.Equal(1, fixture.Index(selected.Suite));
            selected.Complete(true);
        }
        Assert.Equal(1, busy.Statistics.Read(fixture.Now).Active);
    }

    [Fact]
    public async Task LeastActiveSelectionReservesBeforeOtherThreadsCanSelect()
    {
        using var fixture = new Fixture(1, 1);
        var held = new ConcurrentBag<LoadBalancedInvocation>();
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => held.Add(fixture.Select(LoadBalanceMode.LeastActive)))));
        Assert.Equal(100, held.Count(call => fixture.Index(call.Suite) == 0));
        Assert.Equal(100, held.Count(call => fixture.Index(call.Suite) == 1));
        foreach (var call in held) call.Complete(true);
        Assert.All(held, call => Assert.Equal(0, call.Statistics.Read(fixture.Now).Active));
    }

    [Fact]
    public void LeastActiveUsesWeightsWhenActiveCountsTie()
    {
        using var fixture = new Fixture(1, 3);
        int first = 0;
        for (int i = 0; i < 20000; i++)
        {
            using var call = fixture.Select(LoadBalanceMode.LeastActive);
            if (fixture.Index(call.Suite) == 0) first++;
            call.Complete(true);
        }
        Assert.InRange(first, 4700, 5300);
    }

    [Theory]
    [InlineData(DubboActuatorProtocolType.NativeDubbo)]
    [InlineData(DubboActuatorProtocolType.Triple)]
    [InlineData(DubboActuatorProtocolType.Telnet)]
    [InlineData(DubboActuatorProtocolType.Http)]
    public async Task LocalStatisticsAndP2CWorkForEveryProtocolWithoutProviderDiagnostics(DubboActuatorProtocolType protocol)
    {
        using var fixture = new Fixture(new[] { 1, 1 }, protocol);
        using var busy = fixture.Reserve(0);
        Assert.Null(busy.Suite.ActuatorSuiteStatusInfo.StatusInfo);
        using var selected = fixture.Select(LoadBalanceMode.P2CLoadBalance);
        Assert.Equal(1, fixture.Index(selected.Suite));
        var gate = new TaskCompletionSource<DubboRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy(selected.Suite).Handler = () => gate.Task;
        Task<DubboRequestResult> pending = selected.InvokeAsync(() => selected.Suite.SendQuery("com.foo.Service.echo", "[]"));
        Assert.Equal(1, selected.Statistics.Read(fixture.Now).Active);
        fixture.Now += 20;
        var response = new DubboRequestResult();
        gate.SetResult(response);
        Assert.Same(response, await pending);
        Assert.Equal(0, selected.Statistics.Read(fixture.Now).Active);
        Assert.Equal(1, selected.Statistics.Read(fixture.Now).Successes);
        selected.Dispose();
        Assert.Equal(0, selected.Statistics.Read(fixture.Now).Active);
    }

    [Theory]
    [InlineData("failed-result")]
    [InlineData("throw")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("synchronous-throw")]
    public async Task EveryFailurePathReleasesExactlyOneReservation(string outcome)
    {
        using var fixture = new Fixture(1);
        using var call = fixture.Reserve(0);
        Task<DubboRequestResult> Invoke() => outcome switch
        {
            "failed-result" => Task.FromResult(new DubboRequestResult { QuerySuccess = false }),
            "timeout" => Task.FromException<DubboRequestResult>(new TimeoutException()),
            "cancel" => Task.FromCanceled<DubboRequestResult>(new CancellationToken(true)),
            "synchronous-throw" => throw new InvalidOperationException(),
            _ => Task.FromException<DubboRequestResult>(new IOException())
        };
        if (outcome == "failed-result") Assert.False((await call.InvokeAsync(Invoke)).QuerySuccess);
        else await Assert.ThrowsAnyAsync<Exception>(() => call.InvokeAsync(Invoke));
        call.Dispose();
        Assert.Equal(0, call.Statistics.Read(fixture.Now).Active);
        Assert.Equal(1, call.Statistics.Read(fixture.Now).Failures);
        Assert.Equal(0, call.Statistics.Read(fixture.Now).Successes);
    }

    [Fact]
    public void ShortestResponseUsesWindowAverageInsteadOfLastRequest()
    {
        using var fixture = new Fixture(1, 1);
        for (int i = 0; i < 10; i++) fixture.Record(0, 10);
        fixture.Record(0, 100); // Last call alone would incorrectly make provider 1 look faster.
        fixture.Record(1, 30);
        using var selected = fixture.Select(LoadBalanceMode.ShortestResponse);
        Assert.Equal(0, fixture.Index(selected.Suite));
    }

    [Fact]
    public void ShortestResponseAccountsForOutstandingRequests()
    {
        using var fixture = new Fixture(1, 1);
        fixture.Record(0, 10);
        fixture.Record(1, 30);
        using var first = fixture.Reserve(0);
        using var second = fixture.Reserve(0);
        using var third = fixture.Reserve(0);
        using var selected = fixture.Select(LoadBalanceMode.ShortestResponse);
        Assert.Equal(1, fixture.Index(selected.Suite)); // 10*(3+1) > 30*(0+1).
    }

    [Fact]
    public void FastFailuresAreNotTreatedAsFastSuccessfulResponses()
    {
        using var fixture = new Fixture(1, 1);
        fixture.Record(0, 0.01, false);
        fixture.Record(1, 50);
        using var selected = fixture.Select(LoadBalanceMode.ShortestResponse);
        Assert.Equal(1, fixture.Index(selected.Suite));
    }

    [Fact]
    public void UnknownProviderGetsOneProbeRatherThanAnUnlimitedZeroLatencyPreference()
    {
        using var fixture = new Fixture(1, 1);
        fixture.Record(0, 10);
        using var probe = fixture.Select(LoadBalanceMode.ShortestResponse);
        Assert.Equal(1, fixture.Index(probe.Suite));
        for (int i = 0; i < 100; i++)
        {
            using var selected = fixture.Select(LoadBalanceMode.ShortestResponse);
            Assert.Equal(0, fixture.Index(selected.Suite));
            selected.Complete(true);
        }
        Assert.Equal(1, probe.Statistics.Read(fixture.Now).Active);
    }

    [Fact]
    public void ExpiredWindowDiscardsOldLatenciesAndProbesIdleNodesAgain()
    {
        using var fixture = new Fixture(1, 1);
        fixture.Record(0, 10);
        fixture.Record(1, 100);
        fixture.Now += 31000;
        using var firstProbe = fixture.Select(LoadBalanceMode.ShortestResponse);
        using var secondProbe = fixture.Select(LoadBalanceMode.ShortestResponse);
        Assert.NotSame(firstProbe.Suite, secondProbe.Suite);
        Assert.Equal(0, firstProbe.Statistics.Read(fixture.Now).Successes);
        Assert.Equal(0, secondProbe.Statistics.Read(fixture.Now).Successes);
    }

    [Fact]
    public void ShortestResponseUsesWeightsForEqualScores()
    {
        using var fixture = new Fixture(1, 3);
        fixture.Record(0, 10);
        fixture.Record(1, 10);
        int first = 0;
        // Selection-only calls avoid changing equal historical scores during this distribution test.
        var invocation = Fixture.Invocation();
        for (int i = 0; i < 10000; i++)
        {
            using var selected = fixture.Select(LoadBalanceMode.ShortestResponse, invocation);
            if (fixture.Index(selected.Suite) == 0) first++;
            fixture.Now += 10;
            selected.Complete(true);
        }
        Assert.InRange(first, 2200, 2800);
    }

    [Fact]
    public void MethodsOverloadsVersionsAndGroupsDoNotShareStatistics()
    {
        using var fixture = new Fixture(1);
        using var original = fixture.Reserve(0);
        foreach (var invocation in new[]
        {
            Fixture.Invocation("other"), Fixture.Invocation(parameter: "int"),
            new DubboInvocation { Service = "com.foo.Service", Method = "echo", Version = "2" },
            new DubboInvocation { Service = "com.foo.Service", Method = "echo", Group = "other" }
        })
        {
            using var distinct = fixture.Reserve(0, invocation);
            Assert.NotSame(original.Statistics, distinct.Statistics);
            Assert.Equal(1, distinct.Statistics.Read(fixture.Now).Active);
        }
        Assert.Equal(1, original.Statistics.Read(fixture.Now).Active);
    }

    [Fact]
    public void ServicesSharingAConnectionKeepIndependentStatistics()
    {
        using var fixture = new Fixture(1);
        using var second = new DubboServiceDriver("other.Service", fixture.Endpoints(1), fixture.Shared,
            clock: () => fixture.Now, factory: _ => throw new Exception("connection should be shared"));
        using var firstCall = fixture.Reserve(0);
        using var secondCall = second.SelectAndReserve(LoadBalanceMode.LeastActive, null, null, Fixture.Invocation());
        Assert.Same(firstCall.Suite, secondCall.Suite);
        Assert.NotSame(firstCall.Statistics, secondCall.Statistics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InFlightRequestKeepsConnectionAliveAcrossProviderRemovalOrDriverEviction(bool evict)
    {
        using var fixture = new Fixture(1);
        using var call = fixture.Reserve(0);
        SuiteProxy proxy = fixture.Proxy(call.Suite);
        if (evict) fixture.Driver.Dispose();
        else fixture.Driver.UpdateActuatorSuiteEndPoints(new());
        Assert.Equal(0, proxy.DisposeCount);
        Assert.Single(fixture.Shared);
        call.Complete(true);
        Assert.Equal(1, proxy.DisposeCount);
        Assert.Empty(fixture.Shared);
        call.Dispose();
        Assert.Equal(1, proxy.DisposeCount);
    }

    [Fact]
    public async Task RegistryUpdatesAndSelectionsCanRunConcurrentlyForEveryStrategy()
    {
        using var fixture = new Fixture(1, 3);
        Task update = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) fixture.Update(i % 2, 3);
        });
        Task[] callers = Enum.GetValues<LoadBalanceMode>().Select(mode => Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                using var call = fixture.Driver.SelectAndReserve(mode, "same-key", null, Fixture.Invocation());
                call?.Complete(true);
            }
        })).ToArray();
        await Task.WhenAll(callers.Append(update));
        Assert.All(fixture.Shared.Values, value => Assert.Equal(1, value.ReferenceCount));
    }

    [Theory]
    [InlineData(LoadBalanceMode.Random)]
    [InlineData(LoadBalanceMode.RoundRobin)]
    [InlineData(LoadBalanceMode.LeastActive)]
    [InlineData(LoadBalanceMode.P2CLoadBalance)]
    [InlineData(LoadBalanceMode.ShortestResponse)]
    [InlineData(LoadBalanceMode.ConsistentHash)]
    public void ZeroWeightDisabledAndProtocolFilteredProvidersNeverReceiveReservations(LoadBalanceMode mode)
    {
        using var fixture = new Fixture(new[] { 1, 3 }, DubboActuatorProtocolType.Triple);
        var endpoints = fixture.Endpoints(0, 3);
        fixture.Driver.UpdateActuatorSuiteEndPoints(endpoints);
        for (int i = 0; i < 50; i++)
        {
            using var selected = fixture.Driver.SelectAndReserve(mode, "key", DubboActuatorProtocolType.Triple, Fixture.Invocation());
            Assert.Equal(1, fixture.Index(selected.Suite));
            selected.Complete(true);
        }
        Assert.Null(fixture.Driver.SelectAndReserve(mode, "key", DubboActuatorProtocolType.NativeDubbo, Fixture.Invocation()));
        endpoints[1].Disabled = true;
        fixture.Driver.UpdateActuatorSuiteEndPoints(endpoints);
        Assert.Null(fixture.Driver.SelectAndReserve(mode, "key", null, Fixture.Invocation()));
    }

    [Fact]
    public void HashRingIsCachedForFilteredCandidatesAndInvalidatedAfterUpdates()
    {
        using var fixture = new Fixture(1, 1);
        var selected = fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.ConsistentHash, "用户一", DubboActuatorProtocolType.NativeDubbo);
        for (int i = 0; i < 100; i++)
            Assert.Same(selected, fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.ConsistentHash, "用户一", DubboActuatorProtocolType.NativeDubbo));
        FieldInfo field = typeof(DubboServiceDriver).GetField("_candidates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(fixture.Driver));
        Assert.Equal(1, cache.Count);
        object cached = cache.Values.Cast<object>().Single();
        fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.ConsistentHash, "用户二", DubboActuatorProtocolType.NativeDubbo);
        Assert.Same(cached, cache.Values.Cast<object>().Single());
        fixture.Update(0, 1);
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, fixture.Index(fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.ConsistentHash, "用户一", DubboActuatorProtocolType.NativeDubbo)));
    }

    [Theory]
    [InlineData(DubboActuatorProtocolType.NativeDubbo)]
    [InlineData(DubboActuatorProtocolType.Triple)]
    [InlineData(DubboActuatorProtocolType.Telnet)]
    [InlineData(DubboActuatorProtocolType.Http)]
    public async Task PublicTypedInferredQueryUsesMethodStatisticsExactlyOnce(DubboActuatorProtocolType protocol)
    {
        using var fixture = new Fixture(new[] { 1, 1 }, protocol);
        using var client = new DubboClient("127.0.0.1:2181", new DubboClient.DubboClientConf { NowLoadBalanceMode = LoadBalanceMode.LeastActive });
        var metadataField = typeof(DubboClient).GetField("_metadataManager", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((IDisposable)metadataField.GetValue(client)!).Dispose();
        using var zookeeper = new MyZookeeper("127.0.0.1:2181");
        var provider = fixture.Endpoints(1).Single();
        provider.Release = "2.7.2"; // Offline metadata loader; no registry access.
        metadataField.SetValue(client, new DubboMetadataManager(zookeeper, "/dubbo",
            _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(new[] { provider }),
            (_, _) => Task.FromResult(DubboTelnetMetadataResult.Success(new[]
            {
                new DubboMethodMetadata { ServiceName = "com.foo.Service", Name = "echo", ParameterTypes = new[] { "java.lang.String" }, ReturnType = "java.lang.String" }
            }))));
        var drivers = new DubboDriverCollection(fixture.Shared, null);
        var driverMap = (ConcurrentDictionary<string, DubboServiceDriver>)typeof(DubboDriverCollection)
            .GetField("_dubboServiceDriverCollection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(drivers)!;
        driverMap["com.foo.Service"] = fixture.Driver;
        typeof(DubboClient).GetField("_dubboDriverCollection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, drivers);
        var invocation = Fixture.Invocation(parameter: "java.lang.String");
        using var busy = fixture.Reserve(0, invocation);
        using var inspect = fixture.Reserve(1, invocation);
        RequestLoadStatistics statistics = inspect.Statistics;
        inspect.Complete(true);
        var gate = new TaskCompletionSource<DubboRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy(inspect.Suite).Handler = () => { entered.TrySetResult(true); return gate.Task; };
        Task<DubboRequestResult<string>> pending = client.QueryAsync<string>("com.foo.Service#echo", "payload");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, statistics.Active);
        gate.SetResult(new DubboRequestResult { Result = "\"ok\"" });
        Assert.Equal("ok", (await pending).ResultModle);
        Assert.Equal(0, statistics.Active);
        Assert.Equal(2, statistics.Read(fixture.Now).Successes); // Inspect seed plus exactly one dispatch.
    }

    [Fact]
    public void IdleMethodCacheIsTrimmedAfterABurstWithoutEvictingInFlightCounters()
    {
        using var fixture = new Fixture(1);
        var held = Enumerable.Range(0, 1050).Select(i => fixture.Reserve(0, Fixture.Invocation("method-" + i))).ToArray();
        using var same = fixture.Reserve(0, Fixture.Invocation("method-0"));
        Assert.Same(held[0].Statistics, same.Statistics);
        same.Complete(true);
        foreach (var call in held) call.Complete(true);
        using var next = fixture.Reserve(0, Fixture.Invocation("new-method"));
        var cache = (System.Collections.IDictionary)typeof(DubboServiceDriver)
            .GetField("_methods", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Driver)!;
        Assert.Equal(1024, cache.Count);
        Assert.All(held, call => Assert.Equal(0, call.Statistics.Active));
    }

    [Fact]
    public void FailedProviderUpdateDoesNotLeaveStaleCachedCandidatesOrUndisposedConnections()
    {
        var shared = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();
        var suite = DispatchProxy.Create<IDubboActuatorSuite, SuiteProxy>();
        var proxy = (SuiteProxy)(object)suite;
        DubboServiceEndPointInfo Endpoint(int port) => new() { EndPoint = new IPEndPoint(IPAddress.Loopback, port) };
        using var driver = new DubboServiceDriver("com.foo.Service", new() { Endpoint(30000) }, shared,
            factory: endpoint => endpoint.EndPoint.Port == 30000 ? suite : throw new InvalidOperationException("bad provider"));
        Assert.Same(suite, driver.GetDubboActuatorSuite(LoadBalanceMode.Random));
        Assert.Throws<InvalidOperationException>(() => driver.UpdateActuatorSuiteEndPoints(new() { Endpoint(30001) }));
        Assert.Null(driver.GetDubboActuatorSuite(LoadBalanceMode.Random));
        Assert.Empty(shared);
        Assert.Equal(1, proxy.DisposeCount);
    }

    [Fact]
    public void ReaddedProviderRetainsItsOutstandingRequestStatistics()
    {
        using var fixture = new Fixture(1, 1);
        using var busy = fixture.Reserve(0);
        fixture.Driver.UpdateActuatorSuiteEndPoints(new());
        fixture.Update(1, 1);
        using var sameProvider = fixture.Reserve(0);
        Assert.Same(busy.Suite, sameProvider.Suite);
        Assert.Same(busy.Statistics, sameProvider.Statistics);
        Assert.Equal(2, sameProvider.Statistics.Active);
        using var selected = fixture.Select(LoadBalanceMode.LeastActive);
        Assert.Equal(1, fixture.Index(selected.Suite));
    }

    [Fact]
    public void RemovedProviderStatisticsArePrunedWhenTheLastCallCompletes()
    {
        using var fixture = new Fixture(1);
        using var call = fixture.Reserve(0);
        fixture.Driver.UpdateActuatorSuiteEndPoints(new());
        call.Complete(true);
        fixture.Update(1);
        using var replacement = fixture.Reserve(0);
        Assert.NotSame(call.Statistics, replacement.Statistics);
        Assert.NotSame(call.Suite, replacement.Suite);
    }

    [Fact]
    public void FailedMultiProviderConstructionRollsBackEarlierConnectionReferences()
    {
        var shared = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();
        var suite = DispatchProxy.Create<IDubboActuatorSuite, SuiteProxy>();
        var proxy = (SuiteProxy)(object)suite;
        var endpoints = new List<DubboServiceEndPointInfo>
        {
            new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 30000) },
            new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 30001) }
        };
        Assert.Throws<InvalidOperationException>(() => new DubboServiceDriver("com.foo.Service", endpoints, shared,
            factory: endpoint => endpoint.EndPoint.Port == 30000 ? suite : throw new InvalidOperationException("factory failure")));
        Assert.Empty(shared);
        Assert.Equal(1, proxy.DisposeCount);
    }

    [Fact]
    public void RoutingMetadataIsSnapshotIsolatedFromPublisherMutation()
    {
        using var fixture = new Fixture(1, 1);
        var published = fixture.Endpoints(1, 1);
        fixture.Driver.UpdateActuatorSuiteEndPoints(published);
        fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.Random); // Populate cached prefix sums.
        published[0].Weight = 0;
        published[0].Disabled = true;
        published[0].EndPoint.Port = 31000;
        var snapshot = fixture.Driver.InnerActuatorSuites;
        var originalEndpoint = new IPEndPoint(IPAddress.Loopback, 20880);
        Assert.Equal(1, snapshot[originalEndpoint].Weight);
        Assert.NotEqual(true, snapshot[originalEndpoint].Disabled);
        Assert.Equal(20880, snapshot[originalEndpoint].EndPoint.Port);
        fixture.Driver.UpdateActuatorSuiteEndPoints(published);
        Assert.False(fixture.Driver.InnerActuatorSuites.ContainsKey(originalEndpoint));
        Assert.Equal(1, fixture.Index(fixture.Driver.GetDubboActuatorSuite(LoadBalanceMode.Random)));
    }

    [Fact]
    public async Task ConcurrentCollectionDisposalAndRegistryCallbacksCannotResurrectDrivers()
    {
        var shared = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();
        using var collection = new DubboDriverCollection(shared, null);
        var endpoints = new List<DubboServiceEndPointInfo>
        { new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 20880), Scheme = "dubbo", Interface = "com.foo.Service" } };
        collection.AddDubboServiceDriver("com.foo.Service", endpoints);
        await Task.WhenAll(Task.Run(() => collection.Dispose()), Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                try { collection.AddDubboServiceDriver("com.foo.Service", endpoints); }
                catch (ObjectDisposedException) { }
            }
        }));
        Assert.Equal(0, collection.Count);
        Assert.False(collection.HasServiceDriver("com.foo.Service"));
        Assert.Empty(shared);
        Assert.Throws<ObjectDisposedException>(() => collection.AddDubboServiceDriver("com.foo.Service", endpoints));
        Assert.Null(collection.GetDubboActuatorSuite("com.foo.Service").AvailableDubboActuatorSuite);
    }

    [Fact]
    public async Task ConcurrentClientDisposeIsIdempotent()
    {
        var client = new DubboClient("127.0.0.1:2181");
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => client.Dispose())));
        Assert.True(client.IsDisposed);
        client.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        internal double Now;
        internal readonly DubboServiceDriver Driver;
        internal readonly Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> Shared = new();
        private readonly DubboActuatorProtocolType _protocol;
        private readonly ConcurrentDictionary<IDubboActuatorSuite, SuiteProxy> _proxies = new();
        private readonly Dictionary<int, string> _hashKeys = new();

        internal Fixture(params int[] weights) : this(weights, DubboActuatorProtocolType.NativeDubbo) { }
        internal Fixture(int[] weights, DubboActuatorProtocolType protocol)
        {
            _protocol = protocol;
            var random = new Random(12345);
            Driver = new DubboServiceDriver("com.foo.Service", Endpoints(weights), Shared,
                new DubboServiceDriver.DubboServiceDriverConf { DubboRequestTimeout = 1000 },
                clock: () => Now, random: random.NextInt64, factory: endpoint =>
                {
                    IDubboActuatorSuite suite = protocol == DubboActuatorProtocolType.NativeDubbo || protocol == DubboActuatorProtocolType.Triple
                        ? DispatchProxy.Create<IGenericSuite, SuiteProxy>() : DispatchProxy.Create<IDubboActuatorSuite, SuiteProxy>();
                    var proxy = (SuiteProxy)(object)suite;
                    proxy.Protocol = protocol;
                    proxy.Index = endpoint.EndPoint.Port - 20880;
                    _proxies[suite] = proxy;
                    return suite;
                });
        }

        internal List<DubboServiceEndPointInfo> Endpoints(params int[] weights) => weights.Select((weight, i) => new DubboServiceEndPointInfo
        { EndPoint = new IPEndPoint(IPAddress.Loopback, 20880 + i), Weight = weight, Scheme = "fake", Interface = "com.foo.Service" }).ToList();
        internal void Update(params int[] weights) => Driver.UpdateActuatorSuiteEndPoints(Endpoints(weights));
        internal int Index(IDubboActuatorSuite suite) => _proxies[suite].Index;
        internal SuiteProxy Proxy(IDubboActuatorSuite suite) => _proxies[suite];
        internal static DubboInvocation Invocation(string method = "echo", string? parameter = null) => new("com.foo.Service", method,
            parameter == null ? Array.Empty<string>() : new[] { parameter }, parameter == null ? Array.Empty<object>() : new object[] { 1 });
        internal LoadBalancedInvocation Select(LoadBalanceMode mode, DubboInvocation? invocation = null) =>
            Driver.SelectAndReserve(mode, null, null, invocation ?? Invocation());
        internal LoadBalancedInvocation Reserve(int index, DubboInvocation? invocation = null)
        {
            if (!_hashKeys.TryGetValue(index, out string? key))
            {
                key = Enumerable.Range(0, 10000).Select(i => "key-" + i).First(value => Index(Driver.GetDubboActuatorSuite(LoadBalanceMode.ConsistentHash, value)) == index);
                _hashKeys[index] = key;
            }
            return Driver.SelectAndReserve(LoadBalanceMode.ConsistentHash, key, null, invocation ?? Invocation());
        }
        internal void Record(int index, double elapsed, bool success = true, DubboInvocation? invocation = null)
        {
            using var call = Reserve(index, invocation);
            Now += elapsed;
            call.Complete(success);
        }
        public void Dispose() => Driver.Dispose();
    }

    public class SuiteProxy : DispatchProxy
    {
        internal DubboActuatorProtocolType Protocol;
        internal int Index, DisposeCount;
        internal Func<Task<DubboRequestResult>> Handler = () => Task.FromResult(new DubboRequestResult());
        private readonly DubboActuatorSuiteStatus _status = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProtocolType" => Protocol,
            "get_ServiceFuncSpit" => ".",
            "get_ActuatorSuiteStatusInfo" => _status,
            "get_LastActivateTime" => DateTime.Now,
            "Dispose" => Disposed(),
            "SendQuery" => Handler(),
            _ => throw new NotSupportedException(method.Name)
        };
        private object? Disposed() { Interlocked.Increment(ref DisposeCount); return null; }
    }

    public interface IGenericSuite : IDubboActuatorSuite, IDubboGenericInvocationActuator { }
}
