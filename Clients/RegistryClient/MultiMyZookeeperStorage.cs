using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{

    internal class MultiMyZookeeperStorage:IDisposable
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

        public MyZookeeper GetMyZookeeper(string connectionString)
        {
            if(_innerMultiMyZookeeperCollection.ContainsKey(connectionString))
            {
                _innerMultiMyZookeeperCollection[connectionString].ReferenceCount++;
                return _innerMultiMyZookeeperCollection[connectionString].NowMyZookeeper;
            }
            else
            {   
                MyZookeeper.MyAuthInfo authInfo = null;
                if(connectionString.EndsWith("]"))
                {
                    int tempStart = connectionString.IndexOf('[');
                    if(tempStart<0 || connectionString.Length-tempStart-2 <= 0)
                    {
                        throw new ArgumentException($"“{nameof(connectionString)}” is not valid (lost '[')", nameof(connectionString));
                    }
                    string authString = connectionString.Substring(tempStart+1, connectionString.Length-tempStart-2).Trim();
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
                    connectionString = connectionString.Remove(tempStart).Trim();
                }
                MyZookeeperStorageInfo myZookeeperStorageInfo = new MyZookeeperStorageInfo()
                {
                    ConnectionString = connectionString,
                    NowMyZookeeper = new MyZookeeper(connectionString),
                    ReferenceCount = 1
                };
                if(authInfo!=null)
                {
                    myZookeeperStorageInfo.NowMyZookeeper.AuthInfo = authInfo;
                }
                _innerMultiMyZookeeperCollection.TryAdd(connectionString, myZookeeperStorageInfo);
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

        public void Dispose()
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
