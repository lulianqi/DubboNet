using Nacos.V2.Naming.Dtos;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{
    internal sealed class ZooKeeperConsumerRegistry : IConsumerRegistrationRegistry
    {
        private readonly MyZookeeper _client;
        public ZooKeeperConsumerRegistry(MyZookeeper client) { _client = client; }

        public async Task<long> GetSessionAsync()
        {
            if (!await _client.ConnectZooKeeperAsync().ConfigureAwait(false))
                throw new InvalidOperationException("ZooKeeper is unavailable for consumer registration.");
            return _client.zooKeeper.getSessionId();
        }

        public Task RegisterAsync(ConsumerRegistration registration) => _client.UpdateConsumerAsync(registration, false);
        public Task UnregisterAsync(ConsumerRegistration registration) => _client.UpdateConsumerAsync(registration, true);

        internal static List<ACL> GetCreateAcl(MyZookeeper.MyAuthInfo authInfo) =>
            authInfo?.Scheme != null && authInfo.Auth != null
                ? ZooDefs.Ids.CREATOR_ALL_ACL : ZooDefs.Ids.OPEN_ACL_UNSAFE;

        internal static async Task UpdateAsync(ZooKeeper client, ConsumerRegistration registration, bool unregister, List<ACL> acl)
        {
            string path = registration.ZooKeeperPath;
            if (unregister)
            {
                Stat stat = await client.existsAsync(path, false).ConfigureAwait(false);
                // Never delete another session's registration, even after a collision or reconnect.
                if (stat != null && stat.getEphemeralOwner() == client.getSessionId())
                {
                    try { await client.deleteAsync(path, -1).ConfigureAwait(false); }
                    catch (KeeperException.NoNodeException) { }
                }
                return;
            }
            string parent = path.Substring(0, path.LastIndexOf('/'));
            string current = string.Empty;
            foreach (string part in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + part;
                try { await client.createAsync(current, Array.Empty<byte>(), acl, CreateMode.PERSISTENT).ConfigureAwait(false); }
                catch (KeeperException.NodeExistsException) { }
            }
            try { await client.createAsync(path, Array.Empty<byte>(), acl, CreateMode.EPHEMERAL).ConfigureAwait(false); }
            catch (KeeperException.NodeExistsException)
            {
                Stat stat = await client.existsAsync(path, false).ConfigureAwait(false);
                if (stat == null || stat.getEphemeralOwner() != client.getSessionId())
                    throw new InvalidOperationException("Consumer registration is owned by a different ZooKeeper session.");
            }
        }
    }

    internal sealed class NacosConsumerRegistry : IConsumerRegistrationRegistry
    {
        private readonly MyNacos _client;
        public NacosConsumerRegistry(MyNacos client) { _client = client; }

        // The Nacos V2 SDK owns connection recovery, registration redo and liveness checks.
        public Task<long> GetSessionAsync() => Task.FromResult(0L);
        public Task RegisterAsync(ConsumerRegistration registration) => _client.UpdateConsumerAsync(registration, false);
        public Task UnregisterAsync(ConsumerRegistration registration) => _client.UpdateConsumerAsync(registration, true);

        internal static Instance CreateInstance(ConsumerRegistration registration) => new Instance
        {
            Ip = registration.Host,
            Port = 0,
            // ip:port:cluster is Nacos instance identity. A per-connection cluster prevents two
            // consumers on the same host from overwriting or deregistering one another.
            ClusterName = "dubbonet-" + registration.OwnerId,
            Ephemeral = true,
            Enabled = true,
            Healthy = true,
            Weight = 1,
            Metadata = new Dictionary<string, string>(registration.Metadata)
        };
    }
}
