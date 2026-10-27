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
        private static readonly object Sync = new object();
        private static readonly ConditionalWeakTable<object, ConsumerRegistrationTracker> Trackers = new();
        private static readonly List<WeakReference<ConsumerRegistrationTracker>> Active = new();
        private static ConsumerRegistrationOptions _options = new ConsumerRegistrationOptions();
        private static bool _frozen;

        internal static void Configure(ConsumerRegistrationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            lock (Sync)
            {
                if (_frozen) throw new InvalidOperationException("Configure consumer registration before creating the first DubboClient.");
                _options = options.Snapshot();
            }
        }

        internal static ConsumerRegistrationLease CreateLease(MyZookeeper zooKeeper, MyNacos nacos, string root)
        {
            lock (Sync)
            {
                if (!_frozen)
                {
                    _frozen = true;
                    if (_options.Enabled) AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                }
                if (!_options.Enabled) return null;
                object key = (object)zooKeeper ?? nacos;
                ConsumerRegistrationTracker tracker = Trackers.GetValue(key, registryKey =>
                {
                    IConsumerRegistrationRegistry registry = zooKeeper != null
                        ? new ZooKeeperConsumerRegistry(zooKeeper) : new NacosConsumerRegistry(nacos);
                    var created = new ConsumerRegistrationTracker(registry, _options);
                    Active.RemoveAll(reference => !reference.TryGetTarget(out _));
                    Active.Add(new WeakReference<ConsumerRegistrationTracker>(created));
                    return created;
                });
                return tracker.CreateLease(root);
            }
        }

        internal static Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.WhenAll(Snapshot().Select(tracker => tracker.Request())).WaitAsync(cancellationToken);

        internal static void StopRegistry(object registry)
        {
            lock (Sync)
            {
                if (Trackers.TryGetValue(registry, out ConsumerRegistrationTracker tracker)) tracker.Dispose();
                Trackers.Remove(registry);
            }
        }

        private static ConsumerRegistrationTracker[] Snapshot()
        {
            lock (Sync)
                return Active.Select(reference => reference.TryGetTarget(out var tracker) ? tracker : null)
                    .Where(tracker => tracker != null).ToArray();
        }

        private static void OnProcessExit(object sender, EventArgs args)
        {
            try { Task.WhenAll(Snapshot().Select(tracker => tracker.ShutdownAsync())).Wait(_options.ShutdownTimeout); }
            catch { /* Ephemeral registrations are ultimately cleaned up by the registry server. */ }
        }
    }
}
