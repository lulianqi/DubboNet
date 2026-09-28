using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{

    /// <summary>
    /// 按连接字符串复用 ZooKeeper 客户端，并通过引用计数管理其生命周期。
    /// <para>EN: Reuses ZooKeeper clients by connection string and manages their lifetime through reference counting.</para>
    /// </summary>
    internal class MultiMyZookeeperStorage:IDisposable
    {
        /// <summary>
        /// 记录一个共享 ZooKeeper 客户端及其引用数。
        /// <para>EN: Holds a shared ZooKeeper client and its reference count.</para>
        /// </summary>
        internal class MyZookeeperStorageInfo
        {
            public string ConnectionString { get; set; }
            public MyZookeeper NowMyZookeeper { get; set; }
            public int ReferenceCount { get; internal set; } = 0;
        }

        Dictionary<string, MyZookeeperStorageInfo> _innerMultiMyZookeeperCollection = null;
        private readonly object _syncRoot = new object();

        /// <summary>
        /// 创建空的 ZooKeeper 客户端池。
        /// <para>EN: Creates an empty ZooKeeper client pool.</para>
        /// </summary>
        public MultiMyZookeeperStorage()
        {
            _innerMultiMyZookeeperCollection = new Dictionary<string, MyZookeeperStorageInfo>();
        }

        /// <summary>
        /// 获取或创建指定地址的共享 ZooKeeper 客户端，并增加引用计数。
        /// <para>EN: Gets or creates the shared ZooKeeper client for an address and increments its reference count.</para>
        /// </summary>
        /// <param name="connectionString">连接字符串；可使用 <c>address[scheme credential]</c> 附带认证信息。<para>EN: Connection string; authentication may be appended as <c>address[scheme credential]</c>.</para></param>
        /// <returns>共享客户端。<para>EN: The shared client.</para></returns>
        public MyZookeeper GetMyZookeeper(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("connectionString can not be empty", nameof(connectionString));
            }
            string storageKey = connectionString.Trim();

            lock (_syncRoot)
            {
                if (_innerMultiMyZookeeperCollection.TryGetValue(
                    storageKey,
                    out MyZookeeperStorageInfo existing))
                {
                    existing.ReferenceCount++;
                    return existing.NowMyZookeeper;
                }

                MyZookeeper.MyAuthInfo authInfo = null;
                string zookeeperAddress = storageKey;
                if(zookeeperAddress.EndsWith("]"))
                {
                    int tempStart = zookeeperAddress.IndexOf('[');
                    if(tempStart<0 || zookeeperAddress.Length-tempStart-2 <= 0)
                    {
                        throw new ArgumentException($"“{nameof(connectionString)}” is not valid (lost '[')", nameof(connectionString));
                    }
                    string authString = zookeeperAddress.Substring(
                        tempStart + 1,
                        zookeeperAddress.Length - tempStart - 2).Trim();
                    if(!authString.Contains(" "))
                    {
                       throw new ArgumentException($"“{nameof(connectionString)}” is not valid (lost ' ')", nameof(connectionString));
                    }
                    string[] strings = authString.Split(' ',2);
                    authInfo = new MyZookeeper.MyAuthInfo()
                    {
                        Scheme = strings[0],
                        Auth = Encoding.UTF8.GetBytes(strings[1])
                    };
                    zookeeperAddress = zookeeperAddress.Remove(tempStart).Trim();
                }
                MyZookeeperStorageInfo myZookeeperStorageInfo = new MyZookeeperStorageInfo()
                {
                    ConnectionString = storageKey,
                    NowMyZookeeper = new MyZookeeper(zookeeperAddress),
                    ReferenceCount = 1
                };
                if(authInfo!=null)
                {
                    myZookeeperStorageInfo.NowMyZookeeper.AuthInfo = authInfo;
                }
                _innerMultiMyZookeeperCollection.Add(storageKey, myZookeeperStorageInfo);
                return myZookeeperStorageInfo.NowMyZookeeper;
            }
        }


        /// <summary>
        /// 按连接字符串释放一次引用；最后一个引用释放时关闭客户端。
        /// <para>EN: Releases one reference by connection string and closes the client after the final release.</para>
        /// </summary>
        /// <param name="ConnectionString">创建客户端时使用的连接字符串。<para>EN: Connection string used to acquire the client.</para></param>
        public void RemoveMyZookeeper(string ConnectionString)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                return;
            }
            lock (_syncRoot)
            {
                string storageKey = ConnectionString.Trim();
                if (_innerMultiMyZookeeperCollection.TryGetValue(
                    storageKey,
                    out MyZookeeperStorageInfo storageInfo))
                {
                    Release(storageKey, storageInfo);
                }
            }
        }

        /// <summary>
        /// 按客户端实例释放一次引用；最后一个引用释放时关闭客户端。
        /// <para>EN: Releases one reference by client instance and closes the client after the final release.</para>
        /// </summary>
        /// <param name="zookeeper">共享客户端。<para>EN: Shared client instance.</para></param>
        public void RemoveMyZookeeper(MyZookeeper zookeeper)
        {
            if (zookeeper == null)
            {
                return;
            }
            lock (_syncRoot)
            {
                KeyValuePair<string, MyZookeeperStorageInfo>? match =
                    _innerMultiMyZookeeperCollection.FirstOrDefault(item =>
                        ReferenceEquals(item.Value.NowMyZookeeper, zookeeper));
                if (match.HasValue && match.Value.Value != null)
                {
                    Release(match.Value.Key, match.Value.Value);
                }
            }
        }

        private void Release(string storageKey, MyZookeeperStorageInfo storageInfo)
        {
            storageInfo.ReferenceCount--;
            if (storageInfo.ReferenceCount <= 0)
            {
                storageInfo.NowMyZookeeper.Dispose();
                _innerMultiMyZookeeperCollection.Remove(storageKey);
            }
        }

        /// <summary>
        /// 关闭池中的所有 ZooKeeper 客户端。
        /// <para>EN: Closes all ZooKeeper clients held by the pool.</para>
        /// </summary>
        public void Dispose()
        {
            lock (_syncRoot)
            {
                if(_innerMultiMyZookeeperCollection!=null)
                {
                    foreach(var item in _innerMultiMyZookeeperCollection)
                    {
                        item.Value.NowMyZookeeper.Dispose();
                    }
                    _innerMultiMyZookeeperCollection.Clear();
                    _innerMultiMyZookeeperCollection = null;
                }
            }
        }
    }
}
