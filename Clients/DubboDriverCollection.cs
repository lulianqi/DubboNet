using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using static DubboNet.Clients.DubboClient;
using System.Collections;
using System.Collections.Concurrent;

namespace DubboNet.Clients
{
    /// <summary>
    /// 可复用连接器的集合
    /// </summary>
    internal class DubboDriverCollection:IEnumerable,IDisposable
    {

        internal class DubboDriverCollectionConf
        {
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            public int DubboRequestTimeout { get; set; } = 10 * 1000;
            public int MaintainServiceNum { get; set; } = 20;
            public LoadBalanceMode NowLoadBalanceMode { get; set; } = LoadBalanceMode.Random;
            public string DefaultServiceName { get; set; } = null;
        }


        /// <summary>
        /// 内部维持的DubboActuatorSuite（用于最大程度复用链接）
        /// </summary>
        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _sourceDubboActuatorSuiteCollection;

        /// <summary>
        /// 内部维持的DubboServiceDriver（使用_sourceDubboActuatorSuiteCollection资源，复用服务资源）
        /// </summary>
        //private Dictionary<string, DubboServiceDriver> _dubboServiceDriverCollection = null;
        private ConcurrentDictionary<string, DubboServiceDriver> _dubboServiceDriverCollection = null;


        private DubboDriverCollectionConf _innerDubboDriverCollectionConf = null;

        public int Count { get { return _dubboServiceDriverCollection?.Count ?? 0; } }

        public DubboDriverCollection(Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> dubboActuatorSuiteCollection , DubboDriverCollectionConf dubboDriverCollectionConf)
        {
            if (dubboActuatorSuiteCollection == null)
            {
                throw new ArgumentNullException(nameof(dubboActuatorSuiteCollection));
            }
            //_dubboServiceDriverCollection = new Dictionary<string, DubboServiceDriver>();
            _dubboServiceDriverCollection = new ConcurrentDictionary<string, DubboServiceDriver>();
            _sourceDubboActuatorSuiteCollection = dubboActuatorSuiteCollection;
            _innerDubboDriverCollectionConf = dubboDriverCollectionConf ?? new DubboDriverCollectionConf();
        }

        /// <summary>
        /// 是否存在ServiceDriver
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        public bool HasServiceDriver(string serviceName)
        {
            return _dubboServiceDriverCollection.ContainsKey(serviceName);
        }

        /// <summary>
        /// 添加&更新DubboDriverCollection中保持的DubboServiceDriver（如果需要根据dbEpList更新DubboServiceDriver里的节点信息）
        /// </summary>
        /// <param name="serviceName">服务名称</param>
        /// <param name="dbEpList">服务的节点信息</param>
        /// <returns>添加或更新是否对当前DubboDriverCollection的DubboServiceDriver有实际影响</returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public bool AddDubboServiceDriver(string serviceName, List<IPEndPoint> dbEpList)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            //dbEpList内容为空，服务节点都没有了也是有可能的，也要保留这个服务
            //if (!(dbEpList?.Count > 0))
            if (dbEpList==null)
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            //发现可复用DubboServiceDriver
            if (_dubboServiceDriverCollection.ContainsKey(serviceName))
            {
                DubboServiceDriver tempDubboServiceDriver = _dubboServiceDriverCollection[serviceName];
                return tempDubboServiceDriver.UpdateActuatorSuiteEndPoints(dbEpList)>0;
            }
            //需要创建新的DubboServiceDriver
            else
            {
                DubboServiceDriver tempDubboServiceDriver = new DubboServiceDriver(serviceName, dbEpList, _sourceDubboActuatorSuiteCollection,
                    new DubboServiceDriver.DubboServiceDriverConf()
                    {
                        DubboActuatorSuiteAssistConnectionAliveTime = _innerDubboDriverCollectionConf.DubboActuatorSuiteAssistConnectionAliveTime,
                        DubboActuatorSuiteMaxConnections = _innerDubboDriverCollectionConf.DubboActuatorSuiteMaxConnections,
                        DubboRequestTimeout = _innerDubboDriverCollectionConf.DubboRequestTimeout
                    });
                //_dubboServiceDriverCollection.Add(serviceName, tempDubboServiceDriver);
                _dubboServiceDriverCollection.TryAdd(serviceName, tempDubboServiceDriver);
                while (_innerDubboDriverCollectionConf.MaintainServiceNum > 0 && _dubboServiceDriverCollection.Count > _innerDubboDriverCollectionConf.MaintainServiceNum)
                {
                    //移除最不活跃的服务
                    var inactivityItem = _dubboServiceDriverCollection.MinBy<KeyValuePair<string, DubboServiceDriver>,long>(it=>it.Value.LastActivateTime.Ticks);
                    DubboServiceDriver inactivityServiceDriver = inactivityItem.Value;
                    if (inactivityServiceDriver != null)
                    {
                        ReMoveDubboServiceDriver(inactivityServiceDriver.ServiceName);
                    }

                }
                return true;
            }
        }

        /// <summary>
        /// 根据服务名称移除DubboServiceDriver节点
        /// </summary>
        /// <param name="serviceName">服务名称</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        private bool ReMoveDubboServiceDriver(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            if (_dubboServiceDriverCollection.ContainsKey(serviceName))
            {
                DubboServiceDriver removedServiceDriver;
                _dubboServiceDriverCollection.Remove(serviceName, out removedServiceDriver);
                if (removedServiceDriver != null)
                {
                    //DubboServiceDriver Disposes时会维护_sourceDubboActuatorSuiteCollection
                    removedServiceDriver.Dispose();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 根据服务名称获取可用的DubboActuatorSuite
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="loadBalanceMode"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public AvailableDubboActuatorInfo GetDubboActuatorSuite(string serviceName , LoadBalanceMode loadBalanceMode = LoadBalanceMode.Random)
        {
            AvailableDubboActuatorInfo availableDubboActuatorInfo = new AvailableDubboActuatorInfo();
            if (string.IsNullOrEmpty(serviceName))
            {
                serviceName = _innerDubboDriverCollectionConf.DefaultServiceName;
            }
            if(string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            if(_dubboServiceDriverCollection.ContainsKey(serviceName))
            {
                availableDubboActuatorInfo.AvailableDubboActuatorSuite = _dubboServiceDriverCollection[serviceName].GetDubboActuatorSuite(loadBalanceMode);
                if (availableDubboActuatorInfo.AvailableDubboActuatorSuite == null)
                {
                    availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoAvailableActuator;
                }
                else
                {
                    if (_dubboServiceDriverCollection[serviceName].InnerActuatorSuites.Count == 0)
                    {
                        availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoActuatorInService;
                    }
                    else
                    {
                        availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite;
                    }
                    availableDubboActuatorInfo.NowDubboServiceDriver = _dubboServiceDriverCollection[serviceName];
                }
            }
            else
            {
                availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoDubboServiceDriver;
                availableDubboActuatorInfo.ErrorMes = $"can not find {serviceName} in _dubboServiceDriverCollection";
            }
            return availableDubboActuatorInfo;
        }

        #region 迭代器实现

        public IEnumerator GetEnumerator()
        {
            return new DubboServiceDriverEnumerator(this);
        }

        internal class DubboServiceDriverEnumerator : IEnumerator
        {
            //private Dictionary<string, DubboServiceDriver>.Enumerator _innerEnumerator = default;
            //private Dictionary<string, DubboServiceDriver> _driverCollection = null;
            private IEnumerator<KeyValuePair<string, DubboServiceDriver>> _innerEnumerator = default;
            private ConcurrentDictionary<string, DubboServiceDriver> _driverCollection = null;


            object IEnumerator.Current
            {
                get
                {
                    return Current;
                }
            }

            public DubboServiceDriver Current
            {
                get
                {
                    return _innerEnumerator.Current.Value;
                }
            }


            public DubboServiceDriverEnumerator(DubboDriverCollection dubboDriverCollection)
            {
                _driverCollection = dubboDriverCollection._dubboServiceDriverCollection;
                _innerEnumerator = _driverCollection.GetEnumerator();
            }

            public bool MoveNext()
            {
                return _innerEnumerator.MoveNext();
            }

            public void Reset()
            {
                //_innerEnumerator.Dispose();
                //_innerEnumerator = _driverCollection.GetEnumerator();
                _innerEnumerator.Reset();
            }
        }
        #endregion

        public void Dispose()
        {
            if(_dubboServiceDriverCollection!=null)
            {
                foreach(var item in _dubboServiceDriverCollection)
                {
                    item.Value.Dispose();
                }
            }
            _dubboServiceDriverCollection = null;
            _sourceDubboActuatorSuiteCollection = null;
        }

    }
}
