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
    internal class DubboClientZookeeperWatcher : Watcher
    {

        public string Name { get; private set; }
        public DubboClient InnerDubboClient { get; private set; }

        /// <summary>
        /// DubboClientZookeeperWatcher构造函数(仅用于DubboClient内部使用)
        /// </summary>
        /// <param name="dubboClient"></param>
        /// <param name="name"></param>
        public DubboClientZookeeperWatcher(DubboClient dubboClient, string name = null)
        {
            Name = name ?? "DubboClientZookeeperWatcher";
            InnerDubboClient = dubboClient;
        }

        public override async Task process(WatchedEvent @event)
        {
            MyLogger.LogInfo($"{Name} recieve: Path-{@event.getPath()}     State-{@event.getState()}    Type-{@event.get_Type()}");
            //节点信息发生变化
            if (@event.getState() == Event.KeeperState.SyncConnected && @event.get_Type() == Event.EventType.NodeChildrenChanged)
            {
                //如果@event.getPath()为空ReflushProviderAsync会抛出异常
                if (string.IsNullOrEmpty(@event.getPath()))
                {
                    MyLogger.LogError("[DubboClientZookeeperWatcher] get empty path");
                }
                else if (InnerDubboClient.HasServiceDriver(@event.getPath()))
                {
                    await InnerDubboClient.ReflushProviderAsync(@event.getPath(), true);
                }
                else
                {
                    MyLogger.LogInfo($"[DubboClientZookeeperWatcher] service has removed {@event.getPath()}");
                }
            }
            //注册中心连接超过（或断开后马上连接成功，连接没有重置）
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

        private async Task StartConnecTaskAsync(int timeout = 0)
        {
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
