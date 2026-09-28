using MyCommonHelper;
using org.apache.zookeeper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static DubboNet.Clients.DubboClient;

namespace DubboNet.Clients
{
    /// <summary>
    /// 处理 Dubbo 注册中心节点变化与连接状态事件的 ZooKeeper 观察器。
    /// EN: ZooKeeper watcher that handles Dubbo registry-node changes and connection-state events.
    /// </summary>
    internal class DubboClientZookeeperWatcher : Watcher
    {

        /// <summary>观察器名称，用于日志标识。EN: Watcher name used for log correlation.</summary>
        public string Name { get; private set; }
        /// <summary>接收并处理事件的 DubboClient。EN: DubboClient that receives and handles watcher events.</summary>
        public DubboClient InnerDubboClient { get; private set; }

        /// <summary>
        /// DubboClientZookeeperWatcher构造函数(仅用于DubboClient内部使用)
        /// EN: Initializes a ZooKeeper watcher for internal DubboClient use.
        /// </summary>
        /// <param name="dubboClient">接收注册中心事件的客户端。EN: Client that receives registry events.</param>
        /// <param name="name">可选的日志名称。EN: Optional name used in logs.</param>
        public DubboClientZookeeperWatcher(DubboClient dubboClient, string name = null)
        {
            Name = name ?? "DubboClientZookeeperWatcher";
            InnerDubboClient = dubboClient;
        }

        /// <summary>
        /// 处理 ZooKeeper 事件，刷新 Provider 或元数据缓存，并在连接中断或过期时触发重连。
        /// EN: Processes a ZooKeeper event, refreshes providers or metadata caches, and reconnects after disconnection or session expiration.
        /// </summary>
        /// <param name="event">ZooKeeper 观察事件。EN: ZooKeeper watcher event.</param>
        /// <returns>表示异步事件处理过程的任务。EN: Task representing asynchronous event processing.</returns>
        public override async Task process(WatchedEvent @event)
        {
            MyLogger.LogInfo($"{Name} recieve: Path-{@event.getPath()}     State-{@event.getState()}    Type-{@event.get_Type()}");
            if(InnerDubboClient.IsDisposed)
            {
                MyLogger.LogWarning("[DubboClientZookeeperWatcher] dubbo client is disposed");
                return;
            }
            //节点信息发生变化
            if (@event.getState() == Event.KeeperState.SyncConnected && @event.get_Type() == Event.EventType.NodeChildrenChanged)
            {
                //如果@event.getPath()为空ReflushProviderAsync会抛出异常
                if (string.IsNullOrEmpty(@event.getPath()))
                {
                    MyLogger.LogError("[DubboClientZookeeperWatcher] get empty path");
                }
                else if (InnerDubboClient.TryGetServiceNameFromProviderPath(
                    @event.getPath(),
                    out string serviceName))
                {
                    if (InnerDubboClient.HasServiceDriver(serviceName))
                    {
                        await InnerDubboClient.ReflushProviderAsync(@event.getPath(), true);
                    }
                    else if (InnerDubboClient.HasCachedMetadata(serviceName))
                    {
                        await InnerDubboClient.RefreshCachedMetadataAsync(serviceName);
                    }
                    else
                    {
                        MyLogger.LogInfo(
                            $"[DubboClientZookeeperWatcher] service is no longer cached: {serviceName}");
                    }
                }
                else
                {
                    MyLogger.LogInfo($"[DubboClientZookeeperWatcher] service has removed {@event.getPath()}");
                }
            }
            //注册中心连接超时（或断开后马上连接成功，连接没有重置）
            if (@event.getState() == Event.KeeperState.SyncConnected && @event.get_Type() == Event.EventType.None)
            {
                MyLogger.LogInfo($"[DubboClientZookeeperWatcher] SyncConnected");
            }
            //注册中心断开时
            else if (@event.getState() == Event.KeeperState.Disconnected && @event.get_Type() == Event.EventType.None)
            {
                InnerDubboClient.InnerRegistryState = RegistryState.DisConnect;
                MyLogger.LogInfo($"[DubboClientZookeeperWatcher] Disconnected , TryDoConnectRegistryTaskAsync start ");
                _ = StartConnecTaskAsync(1000 * 60 * 30);
            }
            //注册中心连接失效（连接断开，现在网络恢复了，不过已经超时之前的连接实例不能使用了，要重置）
            else if (@event.getState() == Event.KeeperState.Expired && @event.get_Type() == Event.EventType.None)
            {
                if (InnerDubboClient.InnerRegistryState == RegistryState.TryConnect)
                {
                    //如果在TryConnect状态，至少等待一个时间片，避免重复ReLoadDubboDriverCollection
                    await Task.Delay(2000);
                }
                if (InnerDubboClient.InnerRegistryState == RegistryState.LostConnect || InnerDubboClient.InnerRegistryState == RegistryState.DisConnect)
                {
                    await StartConnecTaskAsync(0);
                }
                else if (InnerDubboClient.InnerRegistryState == RegistryState.Connected)
                {
                    MyLogger.LogInfo($"[DubboClientZookeeperWatcher] Expired , and Connected by other task");
                }
                else
                {
                    MyLogger.LogError($"[DubboClientZookeeperWatcher] Expired ,deal event fial InnerDubboClient.InnerRegistryState is {InnerDubboClient.InnerRegistryState}");
                }
            }
            else
            {
                MyLogger.LogWarning($"[DubboClientZookeeperWatcher] Unprocessed status > {Name} recieve: Path-{@event.getPath()}     State-{@event.getState()}    Type-{@event.get_Type()}");
            }
            //return Task.FromResult(0);
        }

        /// <summary>
        /// 尝试重新连接注册中心，并在成功后重新加载服务驱动集合。
        /// EN: Attempts to reconnect to the registry and reloads the service-driver collection after success.
        /// </summary>
        /// <param name="timeout">持续重试的最长时间，单位毫秒；零表示使用调用方定义的即时策略。EN: Maximum retry duration in milliseconds; zero uses the caller-defined immediate strategy.</param>
        /// <returns>表示重连与重新加载过程的任务。EN: Task representing reconnection and reload processing.</returns>
        private async Task StartConnecTaskAsync(int timeout = 0)
        {
            if (InnerDubboClient.IsDisposed)
            {
                MyLogger.LogWarning("[StartConnecTaskAsync] dubbo client is disposed");
                return;
            }
            if (await InnerDubboClient.TryDoConnectRegistryTaskAsync(1000, timeout))
            {
                MyLogger.LogInfo($"[DubboClientZookeeperWatcher] Reconnect , ReLoadDubboDriverCollection start ");
                await InnerDubboClient.ReLoadDubboDriverCollection();
            }
            else
            {
                MyLogger.LogInfo($"[DubboClientZookeeperWatcher] LostConnect , TryDoConnectRegistryTaskAsync end and can not reconnect ");
            }
        }

    }
}
