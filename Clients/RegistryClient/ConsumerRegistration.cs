using DubboNet.Clients.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{
    // Registration belongs to the shared registry connection; leases belong to individual clients.
    internal interface IConsumerRegistrationRegistry
    {
        Task<long> GetSessionAsync();
        Task RegisterAsync(ConsumerRegistration registration);
        Task UnregisterAsync(ConsumerRegistration registration);
    }

    internal sealed class ConsumerRegistration
    {
        public string Root { get; }
        public string Service { get; }
        public string Version { get; }
        public string Group { get; }
        public string Host { get; }
        public string OwnerId { get; }
        public string Key { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }

        public ConsumerRegistration(ConsumerRegistrationOptions options, string ownerId,
            string root, string service, string version, string group)
        {
            Root = root;
            Service = service;
            Version = version ?? string.Empty;
            Group = group ?? string.Empty;
            Host = options.Host;
            OwnerId = ownerId;
            Key = MakeKey(Root, Service, Version, Group);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["application"] = options.ApplicationName,
                ["interface"] = Service,
                ["side"] = "consumer",
                ["category"] = "consumers",
                ["client"] = "dubbonet",
                ["dubbo"] = "2.0.2",
                ["dynamic"] = "true",
                ["pid"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["instance-id"] = options.InstanceId,
                ["registry-id"] = OwnerId
            };
            if (Version.Length != 0) metadata["version"] = Version;
            if (Group.Length != 0) metadata["group"] = Group;
            Metadata = metadata;
        }

        internal static string MakeKey(string root, string service, string version, string group) =>
            string.Concat(new[] { root, service, version ?? string.Empty, group ?? string.Empty }
                .Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));

        public string ConsumerUrl => "consumer://" + (Host.Contains(':') ? "[" + Host + "]" : Host)
            + "/" + Uri.EscapeDataString(Service) + "?"
            + string.Join("&", Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

        public string ZooKeeperPath => Root.TrimEnd('/') + "/" + Uri.EscapeDataString(Service)
            + "/consumers/" + Uri.EscapeDataString(ConsumerUrl);

        public string NacosServiceName => string.Join(":", "consumers", Service, Version, Group);
    }

    internal sealed class ConsumerRegistrationTracker : IDisposable
    {
        private sealed class Entry
        {
            public ConsumerRegistration Registration;
            public int References;
            public bool Registered;
            // Even a failed registration can have reached the server. Always attempt deletion.
            public bool MayExist;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly IConsumerRegistrationRegistry _registry;
        private readonly ConsumerRegistrationOptions _options;
        private readonly string _ownerId = Guid.NewGuid().ToString("N");
        private readonly Timer _timer;
        private long? _session;
        private bool _running, _requested, _stopped, _shutdown;
        private Task _worker = Task.CompletedTask;

        public ConsumerRegistrationTracker(IConsumerRegistrationRegistry registry, ConsumerRegistrationOptions options)
        {
            _registry = registry;
            _options = options;
            _timer = new Timer(_ => Request(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public ConsumerRegistrationLease CreateLease(string root) => new ConsumerRegistrationLease(this, root, _options.ShutdownTimeout);

        public string Acquire(string root, string service, string version, string group)
        {
            lock (_sync)
            {
                if (_stopped || _shutdown) return null;
                string key = ConsumerRegistration.MakeKey(root, service, version, group);
                if (!_entries.TryGetValue(key, out Entry entry))
                {
                    entry = new Entry { Registration = new ConsumerRegistration(_options, _ownerId, root, service, version, group) };
                    _entries.Add(key, entry);
                }
                entry.References++;
                _timer.Change(_options.RetryInterval, _options.RetryInterval);
                Request();
                return key;
            }
        }

        public Task Release(IEnumerable<string> keys)
        {
            lock (_sync)
            {
                foreach (string key in keys)
                    if (_entries.TryGetValue(key, out Entry entry)) entry.References = Math.Max(0, entry.References - 1);
                return Request();
            }
        }

        internal Task Request()
        {
            lock (_sync)
            {
                if (_stopped) return Task.CompletedTask;
                _requested = true;
                if (!_running)
                {
                    _running = true;
                    _worker = Task.Run(ReconcileAsync);
                }
                return _worker;
            }
        }

        public Task ShutdownAsync()
        {
            lock (_sync)
            {
                _shutdown = true;
                foreach (Entry entry in _entries.Values) entry.References = 0;
                return Request();
            }
        }

        private async Task ReconcileAsync()
        {
            while (true)
            {
                Entry[] entries;
                lock (_sync)
                {
                    if (_stopped || !_requested)
                    {
                        _running = false;
                        return;
                    }
                    _requested = false;
                    foreach (string key in _entries.Where(pair => pair.Value.References == 0 && !pair.Value.MayExist)
                        .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
                    if (_entries.Count == 0) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    entries = _entries.Values.ToArray();
                }
                try
                {
                    if (entries.Length == 0) continue;
                    long session = await _registry.GetSessionAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_session != session)
                        {
                            foreach (Entry entry in entries) entry.Registered = false;
                            _session = session;
                        }
                    }
                    foreach (Entry entry in entries)
                    {
                        try
                        {
                            bool register, unregister;
                            lock (_sync)
                            {
                                if (_stopped) return;
                                register = entry.References > 0 && !entry.Registered;
                                unregister = entry.References == 0 && entry.MayExist;
                                if (register) entry.MayExist = true;
                                // A timed-out deletion may already have succeeded. A new owner
                                // must re-register rather than trusting the previous acknowledgement.
                                if (unregister) entry.Registered = false;
                            }
                            if (register)
                            {
                                await _registry.RegisterAsync(entry.Registration).ConfigureAwait(false);
                                lock (_sync) entry.Registered = true;
                            }
                            else if (unregister)
                            {
                                await _registry.UnregisterAsync(entry.Registration).ConfigureAwait(false);
                                lock (_sync) { entry.Registered = false; entry.MayExist = false; }
                            }
                            lock (_sync)
                            {
                                if (entry.References == 0 && !entry.MayExist) _entries.Remove(entry.Registration.Key);
                            }
                        }
                        catch (Exception exception)
                        {
                            Warn(entry.Registration.Service, exception);
                        }
                    }
                }
                catch (Exception exception) { Warn("registry session", exception); }
                lock (_sync)
                {
                    if (!_stopped && _entries.Count == 0) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private static void Warn(string service, Exception exception) => MyLogger.LogWarning(
            $"[ConsumerRegistration] {service}: {exception.GetType().Name}; registration will be retried. RPC calls are unaffected.");

        public void Dispose()
        {
            lock (_sync)
            {
                _stopped = true;
                _timer.Dispose();
                _entries.Clear();
            }
        }
    }

    internal sealed class ConsumerRegistrationLease : IDisposable
    {
        private readonly ConsumerRegistrationTracker _tracker;
        private readonly string _root;
        private readonly TimeSpan _shutdownTimeout;
        private readonly HashSet<string> _services = new HashSet<string>(StringComparer.Ordinal);
        private bool _disposed;

        public ConsumerRegistrationLease(ConsumerRegistrationTracker tracker, string root, TimeSpan shutdownTimeout)
        {
            _tracker = tracker;
            _root = root;
            _shutdownTimeout = shutdownTimeout;
        }

        public void Record(string service, string version, string group)
        {
            lock (_services)
            {
                if (_disposed) return;
                string identity = ConsumerRegistration.MakeKey(_root, service, version, group);
                if (_services.Contains(identity)) return;
                string key = _tracker.Acquire(_root, service, version, group);
                if (key != null) _services.Add(key);
            }
        }

        public Task ReleaseAsync()
        {
            lock (_services)
            {
                if (_disposed) return Task.CompletedTask;
                _disposed = true;
                if (_services.Count == 0) return Task.CompletedTask;
                Task cleanup = _tracker.Release(_services);
                _services.Clear();
                return cleanup;
            }
        }

        public void Dispose()
        {
            try { ReleaseAsync().WaitAsync(_shutdownTimeout).ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch (TimeoutException) { /* The shared tracker continues retrying in the background. */ }
        }
    }
}
