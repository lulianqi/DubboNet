using DubboNet.Clients.DataModle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{
    internal static class ConsumerRegistrationConfiguration
    {
        private static readonly ConsumerRegistrationManager Manager = new();

        static ConsumerRegistrationConfiguration() => AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        internal static void Configure(ConsumerRegistrationOptions options) => Manager.Configure(options);

        internal static ConsumerRegistrationLease CreateLease(MyZookeeper zooKeeper, MyNacos nacos, string root) =>
            Manager.CreateLease((object)zooKeeper ?? nacos,
                () => zooKeeper != null ? new ZooKeeperConsumerRegistry(zooKeeper) : new NacosConsumerRegistry(nacos), root);

        internal static Task FlushAsync(CancellationToken cancellationToken = default) => Manager.FlushAsync(cancellationToken);

        internal static void StopRegistry(object registry, bool drain = false) => Manager.StopRegistry(registry, drain);

        private static void OnProcessExit(object sender, EventArgs args)
        {
            try { Manager.ShutdownAsync().Wait(Manager.ShutdownTimeout); }
            catch { /* Ephemeral registrations are ultimately cleaned up by the registry server. */ }
        }
    }

    // Testable without mutating process-wide state. Never holds its lock while waiting for I/O.
    internal sealed class ConsumerRegistrationManager
    {
        private readonly object _sync = new();
        private readonly ConditionalWeakTable<object, ConsumerRegistrationTracker> _trackers = new();
        private readonly List<WeakReference<ConsumerRegistrationTracker>> _active = new();
        private ConsumerRegistrationOptions _options = new ConsumerRegistrationOptions().Snapshot();
        private bool _shutdown;

        internal TimeSpan ShutdownTimeout { get { lock (_sync) return _options.ShutdownTimeout; } }

        internal void Configure(ConsumerRegistrationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            lock (_sync)
            {
                if (_shutdown) throw new InvalidOperationException("Consumer registration is shutting down.");
                // Validate before changing any tracker; omitted identity fields inherit prior values.
                ConsumerRegistrationOptions snapshot = options.Snapshot(_options);
                _options = snapshot;
                foreach (ConsumerRegistrationTracker tracker in Snapshot()) tracker.Configure(snapshot);
            }
        }

        internal ConsumerRegistrationLease CreateLease(object registryKey, Func<IConsumerRegistrationRegistry> factory, string root)
        {
            lock (_sync)
            {
                if (_shutdown) return null;
                ConsumerRegistrationTracker tracker = _trackers.GetValue(registryKey, key =>
                {
                    var created = new ConsumerRegistrationTracker(factory(), _options);
                    _active.RemoveAll(reference => !reference.TryGetTarget(out _));
                    _active.Add(new WeakReference<ConsumerRegistrationTracker>(created));
                    return created;
                });
                // Even disabled clients track references so enabling needs no new client or RPC.
                return tracker.CreateLease(root);
            }
        }

        internal Task FlushAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync) return Task.WhenAll(Snapshot().Select(tracker => tracker.Request())).WaitAsync(cancellationToken);
        }

        internal Task ShutdownAsync()
        {
            lock (_sync)
            {
                _shutdown = true;
                return Task.WhenAll(Snapshot().Select(tracker => tracker.ShutdownAsync()));
            }
        }

        internal void StopRegistry(object registry, bool drain)
        {
            ConsumerRegistrationTracker tracker;
            lock (_sync)
            {
                if (!_trackers.TryGetValue(registry, out tracker)) return;
                _trackers.Remove(registry);
                _active.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, tracker));
            }
            // ZooKeeper is already closing its session; Nacos needs explicit deregistration.
            if (drain) tracker.Drain();
            tracker.Dispose();
        }

        // Call with _sync held: configuration, new trackers and process shutdown are ordered.
        private ConsumerRegistrationTracker[] Snapshot() => _active
            .Select(reference => reference.TryGetTarget(out var tracker) ? tracker : null)
            .Where(tracker => tracker != null).ToArray();
    }
}
