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
            public long IdentityRevision, Generation;
            public bool Registered;
            // Even a failed registration can have reached the server. Always attempt deletion.
            public bool MayExist;
            public readonly RetryState Retry = new();
        }

        private sealed class RetryState
        {
            public int Failures;
            public DateTimeOffset NextAttempt, LastWarning;
            public void Reset() { Failures = 0; NextAttempt = default; LastWarning = default; }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly IConsumerRegistrationRegistry _registry;
        private ConsumerRegistrationOptions _options;
        private readonly string _ownerId = Guid.NewGuid().ToString("N");
        private readonly Timer _timer;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string> _warning, _info;
        private readonly RetryState _sessionRetry = new();
        private long _identityRevision, _configurationGeneration;
        private TimeSpan _timerInterval = Timeout.InfiniteTimeSpan;
        private long? _session;
        private bool _running, _requested, _forceRetry, _stopped, _shutdown;
        private Task _worker = Task.CompletedTask;

        public ConsumerRegistrationTracker(IConsumerRegistrationRegistry registry, ConsumerRegistrationOptions options,
            Action<string> warning = null, Action<string> info = null, Func<DateTimeOffset> now = null)
        {
            _registry = registry;
            _options = options;
            _warning = warning ?? MyLogger.LogWarning;
            _info = info ?? MyLogger.LogInfo;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _timer = new Timer(_ => Request(forceRetry: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal TimeSpan ShutdownTimeout { get { lock (_sync) return _options.ShutdownTimeout; } }

        public ConsumerRegistrationLease CreateLease(string root) => new ConsumerRegistrationLease(this, root);

        internal void Configure(ConsumerRegistrationOptions options)
        {
            lock (_sync)
            {
                if (_stopped || _shutdown) return;
                bool identityChanged = options.ApplicationName != _options.ApplicationName
                    || options.Host != _options.Host || options.InstanceId != _options.InstanceId;
                bool changed = identityChanged || options.Enabled != _options.Enabled
                    || options.RetryInterval != _options.RetryInterval || options.ShutdownTimeout != _options.ShutdownTimeout;
                if (identityChanged) _identityRevision++;
                _options = options;
                if (changed)
                {
                    _configurationGeneration++;
                    _sessionRetry.Reset();
                    foreach (Entry entry in _entries.Values) { entry.Generation++; entry.Retry.Reset(); }
                }
                UpdateTimer();
                Request(forceRetry: false);
            }
        }

        public string Acquire(string root, string service, string version, string group)
        {
            lock (_sync)
            {
                if (_stopped || _shutdown) return null;
                string key = ConsumerRegistration.MakeKey(root, service, version, group);
                if (!_entries.TryGetValue(key, out Entry entry))
                {
                    entry = new Entry
                    {
                        Registration = new ConsumerRegistration(_options, _ownerId, root, service, version, group),
                        IdentityRevision = _identityRevision
                    };
                    _entries.Add(key, entry);
                }
                if (entry.References++ == 0) { entry.Generation++; entry.Retry.Reset(); }
                UpdateTimer();
                if (_options.Enabled) Request(forceRetry: false);
                return key;
            }
        }

        public Task Release(IEnumerable<string> keys)
        {
            lock (_sync)
            {
                foreach (string key in keys)
                    if (_entries.TryGetValue(key, out Entry entry) && entry.References > 0 && --entry.References == 0)
                    { entry.Generation++; entry.Retry.Reset(); }
                return Request(forceRetry: false);
            }
        }

        internal Task Request(bool forceRetry = true)
        {
            lock (_sync)
            {
                if (_stopped) return Task.CompletedTask;
                _requested = true;
                _forceRetry |= forceRetry;
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
                _configurationGeneration++;
                foreach (Entry entry in _entries.Values)
                { entry.References = 0; entry.Generation++; entry.Retry.Reset(); }
                return Request();
            }
        }

        private async Task ReconcileAsync()
        {
            while (true)
            {
                Entry[] entries;
                bool forceRetry;
                long configurationGeneration;
                lock (_sync)
                {
                    if (_stopped || !_requested)
                    {
                        _running = false;
                        return;
                    }
                    _requested = false;
                    forceRetry = _forceRetry;
                    configurationGeneration = _configurationGeneration;
                    _forceRetry = false;
                    foreach (string key in _entries.Where(pair => pair.Value.References == 0 && !pair.Value.MayExist)
                        .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
                    UpdateTimer();
                    entries = _entries.Values.Where(entry => Desired(entry) || entry.MayExist).ToArray();
                }
                try
                {
                    if (entries.Length == 0) continue;
                    lock (_sync)
                        if (!forceRetry && _sessionRetry.NextAttempt > _now()) continue;
                    long session = await _registry.GetSessionAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        Recovered(_sessionRetry, "session", null);
                        if (_session != session)
                        {
                            if (_session.HasValue) Log(_info, Context("session-restored", null)
                                + $" oldSession={_session.Value:x} newSession={session:x}; replaying live references.");
                            foreach (Entry entry in entries)
                            { entry.Registered = false; entry.Generation++; entry.Retry.Reset(); }
                            _session = session;
                        }
                    }
                    foreach (Entry entry in entries)
                    {
                        string operation = null;
                        long generation = 0;
                        try
                        {
                            bool register, unregister;
                            lock (_sync)
                            {
                                if (_stopped) return;
                                // Never replace an identity while the old registration might still
                                // exist. In Nacos, metadata changes alone can have the same instance key.
                                if (!entry.MayExist && entry.IdentityRevision != _identityRevision)
                                {
                                    ConsumerRegistration old = entry.Registration;
                                    entry.Registration = new ConsumerRegistration(_options, _ownerId,
                                        old.Root, old.Service, old.Version, old.Group);
                                    entry.IdentityRevision = _identityRevision;
                                    entry.Retry.Reset();
                                }
                                unregister = entry.MayExist && (!Desired(entry) || entry.IdentityRevision != _identityRevision);
                                register = Desired(entry) && !entry.Registered && !unregister;
                                if (!register && !unregister) continue;
                                if (!forceRetry && entry.Retry.NextAttempt > _now()) continue;
                                operation = register ? "register" : "unregister";
                                generation = entry.Generation;
                                if (register) entry.MayExist = true;
                                // A timed-out deletion may already have succeeded. A new owner
                                // must re-register rather than trusting the previous acknowledgement.
                                if (unregister) entry.Registered = false;
                            }
                            if (register)
                            {
                                await _registry.RegisterAsync(entry.Registration).ConfigureAwait(false);
                                lock (_sync) { entry.Registered = true; Recovered(entry.Retry, operation, entry); }
                            }
                            else if (unregister)
                            {
                                await _registry.UnregisterAsync(entry.Registration).ConfigureAwait(false);
                                lock (_sync)
                                {
                                    entry.Registered = false; entry.MayExist = false;
                                    Recovered(entry.Retry, operation, entry);
                                    // Migration and a concurrent re-enable need a fresh registration.
                                    if (Desired(entry)) _requested = true;
                                }
                            }
                            lock (_sync)
                            {
                                if (entry.References == 0 && !entry.MayExist) _entries.Remove(entry.Registration.Key);
                            }
                        }
                        catch (Exception exception)
                        {
                            lock (_sync)
                            {
                                if (_stopped) return;
                                Failed(entry.Retry, operation ?? "reconcile", entry, exception);
                                // A failure from an obsolete in-flight operation must not postpone
                                // the latest intent (in particular disabling or releasing a client).
                                if (generation != entry.Generation)
                                { entry.Retry.Reset(); _requested = true; }
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        if (_stopped) return;
                        Failed(_sessionRetry, "session", null, exception);
                        if (configurationGeneration != _configurationGeneration)
                        { _sessionRetry.Reset(); _requested = true; }
                    }
                }
                lock (_sync)
                {
                    if (!_stopped) UpdateTimer();
                }
            }
        }

        private bool Desired(Entry entry) => _options.Enabled && !_shutdown && entry.References > 0;

        // Disabled tracking is entirely local; don't connect merely to check a session or heartbeat.
        private void UpdateTimer()
        {
            if (_stopped) return;
            bool work = _entries.Values.Any(entry => Desired(entry) || entry.MayExist);
            TimeSpan interval = work ? _options.RetryInterval : Timeout.InfiniteTimeSpan;
            // New calls and flushes must not continually postpone the next recovery check.
            if (interval != _timerInterval)
            {
                _timerInterval = interval;
                _timer.Change(interval, interval);
            }
        }

        private void Failed(RetryState retry, string operation, Entry entry, Exception exception)
        {
            retry.Failures = Math.Min(retry.Failures + 1, int.MaxValue - 1);
            double milliseconds = Math.Min(_options.RetryInterval.TotalMilliseconds * Math.Pow(2, Math.Min(retry.Failures - 1, 6)),
                Math.Max(_options.RetryInterval.TotalMilliseconds, TimeSpan.FromMinutes(1).TotalMilliseconds));
            DateTimeOffset now = _now();
            retry.NextAttempt = now.AddMilliseconds(milliseconds);
            // First failure, exponential milestones, then at least once a minute. Never log raw
            // exception messages/URLs, which can contain registry usernames, passwords or tokens.
            if (retry.Failures == 1 || (retry.Failures & (retry.Failures - 1)) == 0 || now - retry.LastWarning >= TimeSpan.FromMinutes(1))
            {
                retry.LastWarning = now;
                string types = string.Join(" -> ", ExceptionTypes(exception));
                Exception cause = exception.GetBaseException();
                string detail = cause is System.Net.Sockets.SocketException socket ? $" socket={socket.SocketErrorCode}" : string.Empty;
                Log(_warning, Context(operation, entry) + $" failures={retry.Failures} error={types} hresult=0x{cause.HResult:x8}{detail}"
                    + $" retryAfter={milliseconds / 1000:F1}s; latest desired state will be retried. RPC calls are unaffected.");
            }
        }

        private void Recovered(RetryState retry, string operation, Entry entry)
        {
            if (retry.Failures > 0) Log(_info, Context(operation + "-recovered", entry) + $" failures={retry.Failures}.");
            retry.Reset();
        }

        private static IEnumerable<string> ExceptionTypes(Exception exception)
        {
            for (int depth = 0; exception != null && depth < 5; depth++, exception = exception.InnerException)
                yield return exception.GetType().Name;
        }

        private string Context(string operation, Entry entry)
        {
            ConsumerRegistration registration = entry?.Registration;
            return $"[ConsumerRegistration] registry={_registry.GetType().Name} connection={_ownerId} operation={operation}"
                + (registration == null ? string.Empty : $" root={Clean(registration.Root)} service={Clean(registration.Service)}"
                    + $" version={Clean(registration.Version)} group={Clean(registration.Group)} identityRevision={entry.IdentityRevision}");
        }

        private static string Clean(string value) => value?.Replace("\r", "\\r").Replace("\n", "\\n");
        private static void Log(Action<string> sink, string message)
        {
            try { sink(message); } catch { /* A failing log sink must not break registration or RPC. */ }
        }

        internal void Drain()
        {
            try { ShutdownAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            { Log(_warning, Context("shutdown-timeout", null) + "; cleanup continues in the background; ephemeral expiry is the final fallback."); }
        }

        internal void WaitForRelease(Task cleanup)
        {
            try { cleanup.WaitAsync(ShutdownTimeout).ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            { Log(_warning, Context("release-timeout", null) + "; cleanup continues in the background while the shared registry is available."); }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _stopped = true;
                _timer.Dispose();
                if (_entries.Values.Any(entry => entry.MayExist))
                    Log(_warning, Context("registry-disposed", null) + "; unconfirmed registrations remain; relying on server-side ephemeral expiry.");
                _entries.Clear();
            }
        }
    }

    internal sealed class ConsumerRegistrationLease : IDisposable
    {
        private readonly ConsumerRegistrationTracker _tracker;
        private readonly string _root;
        private readonly HashSet<string> _services = new HashSet<string>(StringComparer.Ordinal);
        private bool _disposed;
        private Task _releaseTask = Task.CompletedTask;

        public ConsumerRegistrationLease(ConsumerRegistrationTracker tracker, string root)
        {
            _tracker = tracker;
            _root = root;
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
                if (_disposed) return _releaseTask;
                _disposed = true;
                if (_services.Count == 0) return Task.CompletedTask;
                _releaseTask = _tracker.Release(_services);
                _services.Clear();
                return _releaseTask;
            }
        }

        public void Dispose()
        {
            Task cleanup = ReleaseAsync();
            _tracker.WaitForRelease(cleanup);
        }
    }
}
