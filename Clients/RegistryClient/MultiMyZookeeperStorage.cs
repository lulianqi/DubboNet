using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{

    internal class MultiMyZookeeperStorage
    {
        internal class MyZookeeperStorageInfo
        {
            public string ConnectionString { get; set; }
            public MyZookeeper NowMyZookeeper { get; set; }
            public int ReferenceCount { get; internal set; } = 0;
        }

        Dictionary<string, MyZookeeperStorageInfo> _innerMultiMyZookeeperCollection = null;

        public MultiMyZookeeperStorage()
        {
            _innerMultiMyZookeeperCollection = new Dictionary<string, MyZookeeperStorageInfo>();
        }

        public MyZookeeper GetMyZookeeper(string ConnectionString)
        {
            if(_innerMultiMyZookeeperCollection.ContainsKey(ConnectionString))
            {
                _innerMultiMyZookeeperCollection[ConnectionString].ReferenceCount++;
                return _innerMultiMyZookeeperCollection[ConnectionString].NowMyZookeeper;
            }
            else
            {
                MyZookeeperStorageInfo myZookeeperStorageInfo = new MyZookeeperStorageInfo()
                {
                    ConnectionString = ConnectionString,
                    NowMyZookeeper = new MyZookeeper(ConnectionString),
                    ReferenceCount = 1
                };
                _innerMultiMyZookeeperCollection.TryAdd(ConnectionString, myZookeeperStorageInfo);
                return myZookeeperStorageInfo.NowMyZookeeper;
            }
        }

        public void RemoveMyZookeeper(string ConnectionString)
        {
            if (_innerMultiMyZookeeperCollection.ContainsKey(ConnectionString))
            {
                _innerMultiMyZookeeperCollection[ConnectionString].ReferenceCount--;
                if(_innerMultiMyZookeeperCollection[ConnectionString].ReferenceCount<=0)
                {
                    _innerMultiMyZookeeperCollection[ConnectionString].NowMyZookeeper.Dispose();
                    _innerMultiMyZookeeperCollection.Remove(ConnectionString);
                }
            }
        }
    }
}
