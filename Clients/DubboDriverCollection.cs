using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using static DubboNet.Clients.DubboClient;
using System.Collections;
using System.Collections.Concurrent;
using DubboNet.Clients.DataModle;

namespace DubboNet.Clients
{
    /// <summary>
    /// 可复用连接器的集合
    /// EN: Collection of reusable service drivers and their shared actuator suites.
    /// </summary>
    internal class DubboDriverCollection:IEnumerable,IDisposable
    {

        /// <summary>
        /// 服务驱动集合的连接、缓存与负载均衡配置。
        /// EN: Connection, cache, and load-balancing options for the service-driver collection.
        /// </summary>
        internal class DubboDriverCollectionConf
        {
            /// <summary>单个 Provider 端点允许创建的最大连接数。EN: Maximum number of connections allowed for one provider endpoint.</summary>
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            /// <summary>辅助连接的空闲存活时间，单位秒。EN: Idle lifetime of auxiliary connections, in seconds.</summary>
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            /// <summary>主连接的空闲存活时间，单位秒。EN: Idle lifetime of the primary connection, in seconds.</summary>
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            /// <summary>Dubbo 请求超时时间，单位毫秒。EN: Dubbo request timeout, in milliseconds.</summary>
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
            /// <summary>最多缓存的服务驱动数量；零表示不限制。EN: Maximum number of cached service drivers; zero means unlimited.</summary>
            public int MaintainServiceNum { get; set; } = 20;
            /// <summary>默认负载均衡策略。EN: Default load-balancing strategy.</summary>
            public LoadBalanceMode NowLoadBalanceMode { get; set; } = LoadBalanceMode.Random;
            /// <summary>调用方未指定服务时使用的默认服务名。EN: Default service name used when the caller does not provide one.</summary>
            public string DefaultServiceName { get; set; } = null;
        }


        /// <summary>
        /// 内部维持的DubboActuatorSuite（用于最大程度复用链接）
        /// EN: Shared actuator suites retained to maximize connection reuse across services.
        /// </summary>
        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _sourceDubboActuatorSuiteCollection;

        /// <summary>
        /// 内部维持的DubboServiceDriver（使用_sourceDubboActuatorSuiteCollection资源，复用服务资源）
        /// EN: Cached service drivers that reuse resources from the shared actuator-suite collection.
        /// </summary>
        //private Dictionary<string, DubboServiceDriver> _dubboServiceDriverCollection = null;
        private ConcurrentDictionary<string, DubboServiceDriver> _dubboServiceDriverCollection = null;


        private DubboDriverCollectionConf _innerDubboDriverCollectionConf = null;

        /// <summary>当前缓存的服务驱动数量。EN: Number of service drivers currently cached.</summary>
        public int Count { get { return _dubboServiceDriverCollection?.Count ?? 0; } }

        /// <summary>
        /// 初始化服务驱动集合。
        /// EN: Initializes the service-driver collection.
        /// </summary>
        /// <param name="dubboActuatorSuiteCollection">跨服务共享的执行器集合。EN: Actuator-suite collection shared across services.</param>
        /// <param name="dubboDriverCollectionConf">集合配置；为空时使用默认配置。EN: Collection configuration; defaults are used when null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="dubboActuatorSuiteCollection"/> 为空。EN: <paramref name="dubboActuatorSuiteCollection"/> is null.</exception>
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
        /// EN: Determines whether a service driver is cached for the specified service.
        /// </summary>
        /// <param name="serviceName">服务名称。EN: Service name.</param>
        /// <returns>存在对应驱动时为 <see langword="true"/>。EN: <see langword="true"/> when a matching driver exists.</returns>
        public bool HasServiceDriver(string serviceName)
        {
            return _dubboServiceDriverCollection.ContainsKey(serviceName);
        }

        /// <summary>
        /// 添加并更新集合中的 DubboServiceDriver，必要时根据节点列表刷新端点。
        /// EN: Adds a service driver or updates its provider endpoints from the supplied endpoint list.
        /// </summary>
        /// <param name="serviceName">服务名称。EN: Service name.</param>
        /// <param name="dbEpList">服务节点信息。EN: Provider endpoint metadata for the service.</param>
        /// <returns>集合或现有驱动发生实际变化时为 <see langword="true"/>。EN: <see langword="true"/> when the collection or an existing driver changed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="serviceName"/> 为空。EN: <paramref name="serviceName"/> is null or empty.</exception>
        /// <exception cref="ArgumentException"><paramref name="dbEpList"/> 为空。EN: <paramref name="dbEpList"/> is null.</exception>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public bool AddDubboServiceDriver(string serviceName, List<DubboServiceEndPointInfo> dbEpList)
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
                        DubboActuatorSuiteMasterConnectionAliveTime = _innerDubboDriverCollectionConf.DubboActuatorSuiteMasterConnectionAliveTime,
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
        /// EN: Removes and disposes the service driver for the specified service.
        /// </summary>
        /// <param name="serviceName">服务名称。EN: Service name.</param>
        /// <returns>找到并移除驱动时为 <see langword="true"/>。EN: <see langword="true"/> when a driver was found and removed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="serviceName"/> 为空。EN: <paramref name="serviceName"/> is null or empty.</exception>
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
        /// EN: Resolves an available actuator suite for a service using the requested load-balancing strategy.
        /// </summary>
        /// <param name="serviceName">服务名称；为空时使用默认服务名。EN: Service name; the configured default is used when omitted.</param>
        /// <param name="loadBalanceMode">负载均衡策略。EN: Load-balancing strategy.</param>
        /// <param name="qurey">请求内容，仅用于一致性 Hash 计算。EN: Request content used only as the consistent-hash key.</param>
        /// <returns>包含选择结果、服务驱动和执行器的结果对象。EN: Result describing the selection outcome, service driver, and actuator suite.</returns>
        /// <exception cref="ArgumentNullException">服务名和默认服务名均为空。EN: Both the service name and configured default service name are empty.</exception>
        public AvailableDubboActuatorInfo GetDubboActuatorSuite(string serviceName , LoadBalanceMode loadBalanceMode = LoadBalanceMode.Random ,string qurey = null) 
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
                availableDubboActuatorInfo.AvailableDubboActuatorSuite = _dubboServiceDriverCollection[serviceName].GetDubboActuatorSuite(loadBalanceMode, qurey);
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

        /// <summary>
        /// 返回用于遍历当前服务驱动快照的枚举器。
        /// EN: Returns an enumerator over the current service-driver snapshot.
        /// </summary>
        /// <returns>服务驱动枚举器。EN: Service-driver enumerator.</returns>
        public IEnumerator GetEnumerator()
        {
            return new DubboServiceDriverEnumerator(this);
        }

        /// <summary>
        /// 服务驱动集合的内部枚举器。
        /// EN: Internal enumerator for the service-driver collection.
        /// </summary>
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

            /// <summary>获取枚举器当前位置的服务驱动。EN: Gets the service driver at the current enumerator position.</summary>
            public DubboServiceDriver Current
            {
                get
                {
                    return _innerEnumerator.Current.Value;
                }
            }


            /// <summary>
            /// 初始化服务驱动枚举器。
            /// EN: Initializes a service-driver enumerator.
            /// </summary>
            /// <param name="dubboDriverCollection">要遍历的集合。EN: Collection to enumerate.</param>
            public DubboServiceDriverEnumerator(DubboDriverCollection dubboDriverCollection)
            {
                _driverCollection = dubboDriverCollection._dubboServiceDriverCollection;
                _innerEnumerator = _driverCollection.GetEnumerator();
            }

            /// <summary>将枚举器推进到下一项。EN: Advances the enumerator to the next item.</summary>
            /// <returns>成功推进时为 <see langword="true"/>。EN: <see langword="true"/> when the enumerator advanced successfully.</returns>
            public bool MoveNext()
            {
                return _innerEnumerator.MoveNext();
            }

            /// <summary>将枚举器重置到初始位置。EN: Resets the enumerator to its initial position.</summary>
            public void Reset()
            {
                //_innerEnumerator.Dispose();
                //_innerEnumerator = _driverCollection.GetEnumerator();
                _innerEnumerator.Reset();
            }
        }
        #endregion

        /// <summary>
        /// 释放所有缓存的服务驱动，并断开对共享执行器集合的引用。
        /// EN: Disposes all cached service drivers and releases the reference to the shared actuator-suite collection.
        /// </summary>
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
