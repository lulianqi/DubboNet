using DubboNet.Clients.DataModle;
using DubboNet.Clients.Helper;
using DubboNet.Clients.LoadBalancing;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using static DubboNet.Clients.DubboClient;

namespace DubboNet.Clients
{
    /// <summary>管理单个服务的 Provider、共享连接及负载状态。EN: Manages providers, shared connections and load state for one service.</summary>
    internal class DubboServiceDriver : IDisposable
    {
        internal class DubboServiceDriverConf
        {
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
        }

        private sealed class MethodProvider
        {
            internal readonly RequestLoadStatistics Statistics = new();
            internal long CurrentWeight;
            internal volatile bool Retired;
        }

        private sealed class MethodState
        {
            internal readonly Dictionary<IPEndPoint, MethodProvider> Providers = new();
            internal double LastUsed;
            internal MethodProvider For(IPEndPoint endpoint)
            {
                if (!Providers.TryGetValue(endpoint, out var provider)) Providers.Add(endpoint, provider = new MethodProvider());
                return provider;
            }
        }

        private sealed class CandidateSet
        {
            internal readonly DubboServiceEndPointInfo[] Providers;
            internal readonly long[] PrefixWeights;
            internal readonly long TotalWeight;
            internal DubboSuiteConsistentHash Hash;

            internal CandidateSet(IEnumerable<DubboServiceEndPointInfo> providers)
            {
                Providers = providers.OrderBy(item => item.EndPoint.ToString(), StringComparer.Ordinal).ToArray();
                PrefixWeights = new long[Providers.Length];
                for (int i = 0; i < Providers.Length; i++) PrefixWeights[i] = TotalWeight += Providers[i].Weight;
            }
        }

        private readonly object _sync = new();
        private readonly Dictionary<IPEndPoint, DubboServiceEndPointInfo> _endpoints = new();
        private readonly Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _shared;
        private readonly Dictionary<int, CandidateSet> _candidates = new();
        private readonly Dictionary<string, MethodState> _methods = new(StringComparer.Ordinal);
        private readonly DubboServiceDriverConf _configuration;
        private readonly Func<double> _clock;
        private readonly Func<long, long> _random;
        private readonly Func<DubboServiceEndPointInfo, IDubboActuatorSuite> _factory;
        private bool _disposed;
        private long _lastActivateTicks;

        public string ServiceName { get; }
        public DateTime LastActivateTime => new DateTime(System.Threading.Interlocked.Read(ref _lastActivateTicks));
        // Callers cannot enumerate a dictionary while a registry watcher mutates it.
        public Dictionary<IPEndPoint, DubboServiceEndPointInfo> InnerActuatorSuites
        { get { lock (_sync) return new Dictionary<IPEndPoint, DubboServiceEndPointInfo>(_endpoints); } }
        internal int ProviderCount { get { lock (_sync) return _endpoints.Count; } }

        public DubboServiceDriver(string serviceName, List<DubboServiceEndPointInfo> dbEpList,
            Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> dubboActuatorSuiteCollection,
            DubboServiceDriverConf dubboServiceDriverConf = null, Func<double> clock = null,
            Func<long, long> random = null, Func<DubboServiceEndPointInfo, IDubboActuatorSuite> factory = null)
        {
            ArgumentNullException.ThrowIfNull(dbEpList);
            ArgumentNullException.ThrowIfNull(dubboActuatorSuiteCollection);
            ServiceName = serviceName;
            _shared = dubboActuatorSuiteCollection;
            _configuration = dubboServiceDriverConf ?? new DubboServiceDriverConf();
            _clock = clock ?? RequestLoadStatistics.MonotonicMilliseconds;
            _random = random ?? Random.Shared.NextInt64;
            _factory = factory ?? CreateSuite;
            _lastActivateTicks = DateTime.Now.Ticks;
            try { UpdateActuatorSuiteEndPoints(dbEpList); }
            catch { Dispose(); throw; }
        }

        private IDubboActuatorSuite CreateSuite(DubboServiceEndPointInfo endpoint)
        {
            var options = new DubboActuatorSuiteConf
            {
                AssistConnectionAliveTime = _configuration.DubboActuatorSuiteAssistConnectionAliveTime,
                MasterConnectionAliveTime = _configuration.DubboActuatorSuiteMasterConnectionAliveTime,
                DubboRequestTimeout = _configuration.DubboRequestTimeout,
                MaxConnections = _configuration.DubboActuatorSuiteMaxConnections
            };
            if (string.Equals(endpoint.Scheme, "dubbo", StringComparison.OrdinalIgnoreCase))
                return new NativeDubboActuatorSuite(endpoint.EndPoint, options);
            if (string.Equals(endpoint.Scheme, "tri", StringComparison.OrdinalIgnoreCase))
                return new TripleDubboActuatorSuite(endpoint.EndPoint, options);
            return new TelnetDubboActuatorSuite(endpoint.EndPoint, options);
        }

        private void RegisterMetadata(DubboServiceEndPointInfo endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Interface)) endpoint.Interface = ServiceName;
            if (endpoint.InnerDubboActuatorSuite is NativeDubboActuatorSuite native) native.RegisterService(endpoint);
            else if (endpoint.InnerDubboActuatorSuite is TripleDubboActuatorSuite triple) triple.RegisterService(endpoint);
        }

        public int UpdateActuatorSuiteEndPoints(List<DubboServiceEndPointInfo> dbEpList)
        {
            ArgumentNullException.ThrowIfNull(dbEpList);
            if (dbEpList.Any(item => item?.EndPoint == null))
                throw new ArgumentException("Provider snapshots must contain non-null endpoints.", nameof(dbEpList));
            var retired = new List<IDubboActuatorSuite>();
            int changes = 0;
            try
            {
                lock (_sync)
                {
                    if (_disposed) return 0;
                    var latest = dbEpList.Where(item => item.Disabled != true).Select(item => item.RoutingSnapshot()).GroupBy(item => item.EndPoint)
                        .ToDictionary(group => group.Key, group => group.Last());
                    // Invalidate before mutating, including when creating a new connection fails.
                    _candidates.Clear();
                    foreach (IPEndPoint endpoint in _endpoints.Keys.Where(key => !latest.ContainsKey(key)).ToArray())
                    {
                        _endpoints.Remove(endpoint);
                        foreach (MethodState method in _methods.Values)
                            if (method.Providers.TryGetValue(endpoint, out var state))
                            {
                                state.Retired = true;
                                if (state.Statistics.Active == 0) method.Providers.Remove(endpoint);
                            }
                        IDubboActuatorSuite unused = ReleaseReference(endpoint);
                        if (unused != null) retired.Add(unused);
                        changes++;
                    }
                    foreach (var pair in latest)
                    {
                        DubboServiceEndPointInfo endpoint = pair.Value;
                        if (_endpoints.TryGetValue(pair.Key, out var previous))
                        {
                            endpoint.InnerDubboActuatorSuite = previous.InnerDubboActuatorSuite;
                            if (previous.Weight != endpoint.Weight)
                                foreach (MethodState method in _methods.Values)
                                    if (method.Providers.TryGetValue(pair.Key, out var state)) state.CurrentWeight = 0;
                        }
                        else
                        {
                            lock (_shared)
                            {
                                if (!_shared.TryGetValue(pair.Key, out var shared))
                                {
                                    var suite = _factory(endpoint) ?? throw new InvalidOperationException("The provider factory returned no actuator suite.");
                                    shared = new DubboActuatorSuiteEndPintInfo { EndPoint = pair.Key, ActuatorSuite = suite };
                                    _shared.Add(pair.Key, shared);
                                }
                                shared.ReferenceCount++;
                                endpoint.InnerDubboActuatorSuite = shared.ActuatorSuite;
                            }
                        }
                        RegisterMetadata(endpoint);
                        foreach (MethodState method in _methods.Values)
                            if (method.Providers.TryGetValue(pair.Key, out var state)) state.Retired = false;
                        _endpoints[pair.Key] = endpoint;
                        changes++;
                    }
                }
            }
            finally { DisposeRetired(retired); }
            return changes;
        }

        public IDubboActuatorSuite GetDubboActuatorSuite(LoadBalanceMode loadBalanceMode, string qurey = null,
            DubboActuatorProtocolType? requiredProtocol = null)
        {
            lock (_sync) return Select(loadBalanceMode, qurey, requiredProtocol, null)?.InnerDubboActuatorSuite;
        }

        internal LoadBalancedInvocation SelectAndReserve(LoadBalanceMode mode, string query,
            DubboActuatorProtocolType? protocol, DubboInvocation invocation)
        {
            lock (_sync)
            {
                if (_disposed) return null;
                MethodState method = Method(invocation);
                DubboServiceEndPointInfo endpoint = Select(mode, query, protocol, invocation, method);
                if (endpoint == null) return null;
                MethodProvider provider = method.For(endpoint.EndPoint);
                RequestLoadStatistics statistics = provider.Statistics;
                lock (_shared) _shared[endpoint.EndPoint].ReferenceCount++;
                // Selection and active increment are indivisible to competing requests on this driver.
                return new LoadBalancedInvocation(endpoint.InnerDubboActuatorSuite, statistics, _clock,
                    () => ReleaseInvocation(endpoint.EndPoint, method, provider));
            }
        }

        private DubboServiceEndPointInfo Select(LoadBalanceMode mode, string query,
            DubboActuatorProtocolType? protocol, DubboInvocation invocation, MethodState method = null)
        {
            if ((uint)mode > (uint)LoadBalanceMode.P2CLoadBalance) throw new ArgumentOutOfRangeException(nameof(mode));
            System.Threading.Interlocked.Exchange(ref _lastActivateTicks, DateTime.Now.Ticks);
            if (_disposed) return null;
            int key = protocol.HasValue ? (int)protocol.Value : -1;
            if (!_candidates.TryGetValue(key, out var set))
            {
                set = new CandidateSet(_endpoints.Values.Where(item => item.Disabled != true && item.Weight > 0
                    && (!protocol.HasValue || item.InnerDubboActuatorSuite.ProtocolType == protocol)));
                _candidates.Add(key, set);
            }
            DubboServiceEndPointInfo[] providers = set.Providers;
            if (providers.Length == 0) return null;
            if (providers.Length == 1) return providers[0];
            MethodState state;
            switch (mode)
            {
                case LoadBalanceMode.Random: return providers[WeightedIndex(set)];
                case LoadBalanceMode.ConsistentHash:
                    if (query == null) return providers[WeightedIndex(set)];
                    if (set.Hash == null)
                    {
                        set.Hash = new DubboSuiteConsistentHash(160);
                        set.Hash.Init(providers.Select(item => item.EndPoint));
                    }
                    return _endpoints[set.Hash.GetNode(query)];
                case LoadBalanceMode.RoundRobin:
                    state = method ?? Method(invocation);
                    DubboServiceEndPointInfo best = null;
                    long maximum = long.MinValue;
                    foreach (var provider in providers)
                    {
                        long current = state.For(provider.EndPoint).CurrentWeight += provider.Weight;
                        provider.NowDispatchWeight = (int)Math.Clamp(current, int.MinValue, int.MaxValue);
                        if (current > maximum) { maximum = current; best = provider; }
                    }
                    long remaining = state.For(best.EndPoint).CurrentWeight -= set.TotalWeight;
                    best.NowDispatchWeight = (int)Math.Clamp(remaining, int.MinValue, int.MaxValue);
                    return best;
                case LoadBalanceMode.LeastActive:
                    return LeastActive(providers, method ?? Method(invocation));
                case LoadBalanceMode.P2CLoadBalance:
                    state = method ?? Method(invocation);
                    int a = WeightedIndex(set), b = WeightedIndex(set, a);
                    int activeA = state.For(providers[a].EndPoint).Statistics.Active;
                    int activeB = state.For(providers[b].EndPoint).Statistics.Active;
                    if (activeA != activeB) return providers[activeA < activeB ? a : b];
                    return _random((long)providers[a].Weight + providers[b].Weight) < providers[a].Weight ? providers[a] : providers[b];
                case LoadBalanceMode.ShortestResponse:
                    return ShortestResponse(providers, method ?? Method(invocation));
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        private int WeightedIndex(CandidateSet set, int excluded = -1)
        {
            long offset = _random(set.TotalWeight - (excluded < 0 ? 0 : set.Providers[excluded].Weight));
            if (excluded >= 0 && offset >= set.PrefixWeights[excluded] - set.Providers[excluded].Weight)
                offset += set.Providers[excluded].Weight;
            int index = Array.BinarySearch(set.PrefixWeights, offset + 1);
            return index < 0 ? ~index : index;
        }

        private DubboServiceEndPointInfo LeastActive(DubboServiceEndPointInfo[] providers, MethodState state)
        {
            int minimum = int.MaxValue;
            long total = 0;
            DubboServiceEndPointInfo best = null;
            foreach (var provider in providers)
            {
                int active = state.For(provider.EndPoint).Statistics.Active;
                if (active < minimum) { minimum = active; total = 0; best = null; }
                if (active == minimum && _random(total += provider.Weight) < provider.Weight) best = provider;
            }
            return best;
        }

        private DubboServiceEndPointInfo ShortestResponse(DubboServiceEndPointInfo[] providers, MethodState state)
        {
            double now = _clock(), minimum = double.PositiveInfinity;
            long total = 0, probeWeight = 0;
            DubboServiceEndPointInfo best = null, probe = null;
            foreach (var provider in providers)
            {
                var statistics = state.For(provider.EndPoint).Statistics;
                var snapshot = statistics.Read(now);
                if (!snapshot.HasSamples)
                {
                    if (snapshot.Active == 0 && now - snapshot.LastProbe >= RequestLoadStatistics.ProbeIntervalMilliseconds
                        && _random(probeWeight += provider.Weight) < provider.Weight) probe = provider;
                    continue;
                }
                // Failure samples are not fast successful responses. This is a score penalty,
                // not a circuit breaker: no provider is excluded and no RPC is retried here.
                double score = snapshot.EstimateResponse(Math.Max(snapshot.AverageMilliseconds,
                    Math.Max(1, provider.Timeout ?? _configuration.DubboRequestTimeout)));
                if (score < minimum) { minimum = score; total = 0; best = null; }
                if (score == minimum && _random(total += provider.Weight) < provider.Weight) best = provider;
            }
            if (probe != null)
            {
                state.For(probe.EndPoint).Statistics.MarkProbe(now);
                return probe;
            }
            return best ?? LeastActive(providers, state);
        }

        private MethodState Method(DubboInvocation invocation)
        {
            var builder = new StringBuilder();
            void Part(string value) { value ??= string.Empty; builder.Append(value.Length).Append(':').Append(value); }
            Part(invocation?.Method); Part(invocation?.Version); Part(invocation?.Group);
            foreach (string parameter in invocation?.ParameterTypes ?? Array.Empty<string>()) Part(parameter);
            string key = builder.ToString();
            if (!_methods.TryGetValue(key, out var state))
            {
                // Bound idle method-signature state; never evict observations of in-flight calls.
                if (_methods.Count >= 1024)
                {
                    var idle = _methods.Where(pair => pair.Value.Providers.Values.All(provider => provider.Statistics.Active == 0))
                        .OrderBy(pair => pair.Value.LastUsed).Take(_methods.Count - 1024 + 1).Select(pair => pair.Key).ToArray();
                    foreach (string unused in idle) _methods.Remove(unused);
                }
                _methods.Add(key, state = new MethodState());
            }
            state.LastUsed = _clock();
            return state;
        }

        private void ReleaseInvocation(IPEndPoint endpoint, MethodState method, MethodProvider provider)
        {
            if (provider.Retired && provider.Statistics.Active == 0)
                lock (_sync)
                    if (provider.Retired && provider.Statistics.Active == 0
                        && method.Providers.TryGetValue(endpoint, out var current) && ReferenceEquals(current, provider))
                        method.Providers.Remove(endpoint);
            IDubboActuatorSuite retired = ReleaseReference(endpoint);
            if (retired != null) DisposeRetired(new[] { retired });
        }

        private IDubboActuatorSuite ReleaseReference(IPEndPoint endpoint)
        {
            lock (_shared)
            {
                if (_shared.TryGetValue(endpoint, out var shared) && --shared.ReferenceCount == 0)
                { _shared.Remove(endpoint); return shared.ActuatorSuite; }
                return null;
            }
        }

        private static void DisposeRetired(IEnumerable<IDubboActuatorSuite> retired)
        {
            foreach (IDubboActuatorSuite suite in retired)
                try { suite.Dispose(); }
                catch (Exception exception)
                {
                    try { MyLogger.LogWarning($"[LoadBalance] connection disposal failed: {exception.GetType().Name}."); }
                    catch { /* Cleanup diagnostics must not replace an RPC result. */ }
                }
        }

        public void Dispose()
        {
            var retired = new List<IDubboActuatorSuite>();
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (IPEndPoint endpoint in _endpoints.Keys)
                {
                    IDubboActuatorSuite unused = ReleaseReference(endpoint);
                    if (unused != null) retired.Add(unused);
                }
                _endpoints.Clear(); _candidates.Clear(); _methods.Clear();
            }
            DisposeRetired(retired);
        }
    }
}
