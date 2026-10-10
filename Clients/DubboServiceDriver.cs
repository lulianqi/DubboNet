using DubboNet.Clients.DataModle;
using DubboNet.Clients.Helper;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using static DubboNet.Clients.DubboClient;


namespace DubboNet.Clients
{
    /// <summary>
    /// 管理单个服务的所有连接器（单个服务可以有N个服务节点，每个服务节点可以有N个连接）
    /// EN: Manages all actuator suites for one service; a service may have multiple provider endpoints and each endpoint may own multiple connections.
    /// </summary>
    internal class DubboServiceDriver:IDisposable
    {
        /// <summary>
        /// 单个服务驱动的连接与超时配置。
        /// EN: Connection and timeout options for one service driver.
        /// </summary>
        internal class DubboServiceDriverConf
        {
            /// <summary>单个 Provider 端点允许创建的最大连接数。EN: Maximum number of connections allowed for one provider endpoint.</summary>
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            /// <summary>辅助连接的空闲存活时间，单位秒。EN: Idle lifetime of auxiliary connections, in seconds.</summary>
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            /// <summary>主连接的空闲存活时间，单位秒。EN: Idle lifetime of the primary connection, in seconds.</summary>
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            /// <summary>Dubbo 请求超时时间，单位毫秒。EN: Dubbo request timeout, in milliseconds.</summary>
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
        }

        /// <summary>
        /// 服务名称
        /// EN: Fully qualified Dubbo service name managed by this driver.
        /// </summary>
        public string ServiceName { get; private set; }

        /// <summary>
        /// 最后激活时间
        /// EN: Time at which this service driver was most recently selected.
        /// </summary>
        public DateTime LastActivateTime { get; private set; } = default(DateTime);

        /// <summary>
        /// 当前DubboServiceDriver可使用的各EndPoint的DubboActuatorSuite集合
        /// EN: Provider endpoint metadata and actuator suites currently available to this service driver.
        /// </summary>
        public Dictionary<IPEndPoint, DubboServiceEndPointInfo> InnerActuatorSuites { get; private set; }

        /// <summary>
        /// DubboClient的源ActuatorSuiteCollection（不要直接使用，保留的这份引用是为了释放时同时清理）
        /// EN: Shared actuator-suite collection owned by DubboClient; retained only for reference counting and cleanup.
        /// </summary>
        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _sourceDubboActuatorSuiteCollection;

        private DubboServiceDriverConf _innerDubboServiceDriverConf = null;

        private int _totalWeightForActuatorSuites = 0;

        private DubboSuiteConsistentHash _dubboSuiteConsistentHash = new DubboSuiteConsistentHash(16);

        /// <summary>
        /// 初始化DubboServiceDriver
        /// EN: Initializes a service driver and registers its initial provider endpoints.
        /// </summary>
        /// <param name="serviceName">当前服务名称。EN: Name of the service managed by this driver.</param>
        /// <param name="dbEpList">当前服务的 Provider 节点列表。EN: Provider endpoints currently registered for the service.</param>
        /// <param name="dubboActuatorSuiteCollection">用于跨服务复用连接的共享执行器集合。EN: Shared actuator-suite collection used to reuse connections across services.</param>
        /// <param name="dubboServiceDriverConf">可选的连接与超时配置。EN: Optional connection and timeout configuration.</param>
        /// <exception cref="ArgumentException"><paramref name="dbEpList"/> 为空或不包含节点。EN: <paramref name="dbEpList"/> is null or contains no endpoints.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="dubboActuatorSuiteCollection"/> 为空。EN: <paramref name="dubboActuatorSuiteCollection"/> is null.</exception>
        public DubboServiceDriver(string serviceName, List<DubboServiceEndPointInfo> dbEpList, Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> dubboActuatorSuiteCollection , DubboServiceDriverConf dubboServiceDriverConf = null)
        {
            ServiceName = serviceName;
            LastActivateTime = DateTime.Now;
            _sourceDubboActuatorSuiteCollection = dubboActuatorSuiteCollection;
            _innerDubboServiceDriverConf = dubboServiceDriverConf ?? new DubboServiceDriverConf();
            InnerActuatorSuites = new Dictionary<IPEndPoint, DubboServiceEndPointInfo>();
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            if (dubboActuatorSuiteCollection == null)
            {
                throw new ArgumentNullException(nameof(dubboActuatorSuiteCollection));
            }
            foreach (DubboServiceEndPointInfo ep in dbEpList)
            {
                AddActuatorSuite(ep);
            }
            UpdateTotalWeight();
            UpdateConsistentHash();
        }

        /// <summary>
        /// 更新TotalWeightForActuatorSuites
        /// EN: Recalculates the total provider weight used by weighted load balancing.
        /// </summary>
        private void UpdateTotalWeight()
        {
            if (InnerActuatorSuites != null)
            {
                _totalWeightForActuatorSuites = InnerActuatorSuites.Sum(item => item.Value.Weight);
            }
            else
            {
                _totalWeightForActuatorSuites = 0;
            }
        }

        /// <summary>
        /// 更新一致性Hash环
        /// EN: Rebuilds the consistent-hash ring from the current provider endpoints.
        /// </summary>
        private void UpdateConsistentHash()
        {
            if(InnerActuatorSuites?.Count>0)
            {
                _dubboSuiteConsistentHash.Init(InnerActuatorSuites.Keys);
            }
            else
            {
                _dubboSuiteConsistentHash.Init();
            }
        }

        /// <summary>
        /// 通过IPEndPoint添加ActuatorSuite (内部函数)
        /// EN: Adds or reuses an actuator suite for one provider endpoint.
        /// </summary>
        /// <param name="ep">待添加的 Provider 端点信息。EN: Provider endpoint metadata to add.</param>
        /// <returns>成功加入当前服务时为 <see langword="true"/>。EN: <see langword="true"/> when the endpoint was added to this service.</returns>
        private bool AddActuatorSuite(DubboServiceEndPointInfo ep)
        {
            //判断服务节点是否禁用
            if(ep.Disabled==true)
            {
                return false;
            }
            if (_sourceDubboActuatorSuiteCollection.ContainsKey(ep.EndPoint))
            {
                //多个DubboServiceEndPointInfo（不同的服务）会复用同一个ActuatorSuite（因为这些服务都使用同一个网络EndPoint节点），他们的DubboServiceEndPointInfo的信息是不同的（只是其引用的InnerDubboActuatorSuite是同一个）
                ep.InnerDubboActuatorSuite=_sourceDubboActuatorSuiteCollection[ep.EndPoint].ActuatorSuite;
                if (ep.InnerDubboActuatorSuite is NativeDubboActuatorSuite reusedNativeSuite)
                {
                    if (string.IsNullOrWhiteSpace(ep.Interface)) ep.Interface = ServiceName;
                    reusedNativeSuite.RegisterService(ep);
                }
                else if (ep.InnerDubboActuatorSuite is TripleDubboActuatorSuite reusedTripleSuite)
                {
                    if (string.IsNullOrWhiteSpace(ep.Interface)) ep.Interface = ServiceName;
                    reusedTripleSuite.RegisterService(ep);
                }
                if(InnerActuatorSuites.TryAdd(ep.EndPoint, ep))
                {
                    _sourceDubboActuatorSuiteCollection[ep.EndPoint].ReferenceCount++;
                    //UpdateTotalWeight();
                    return true;
                }
            }
            else
            {
                IDubboActuatorSuite newDubboActuatorSuite = null;
                //选择IDubboActuatorSuite的协议类型
                if (string.Equals(ep.Scheme, "dubbo", StringComparison.OrdinalIgnoreCase))
                {
                    newDubboActuatorSuite = new NativeDubboActuatorSuite(ep.EndPoint, new DubboActuatorSuiteConf()
                    {
                        AssistConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteAssistConnectionAliveTime,
                        MasterConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteMasterConnectionAliveTime,
                        DubboRequestTimeout = _innerDubboServiceDriverConf.DubboRequestTimeout,
                        MaxConnections = _innerDubboServiceDriverConf.DubboActuatorSuiteMaxConnections
                    });
                    if (string.IsNullOrWhiteSpace(ep.Interface)) ep.Interface = ServiceName;
                    ((NativeDubboActuatorSuite)newDubboActuatorSuite).RegisterService(ep);
                }
                else if (string.Equals(ep.Scheme, "tri", StringComparison.OrdinalIgnoreCase))
                {
                    newDubboActuatorSuite = new TripleDubboActuatorSuite(ep.EndPoint, new DubboActuatorSuiteConf()
                    {
                        AssistConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteAssistConnectionAliveTime,
                        MasterConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteMasterConnectionAliveTime,
                        DubboRequestTimeout = _innerDubboServiceDriverConf.DubboRequestTimeout,
                        MaxConnections = _innerDubboServiceDriverConf.DubboActuatorSuiteMaxConnections
                    });
                    if (string.IsNullOrWhiteSpace(ep.Interface)) ep.Interface = ServiceName;
                    ((TripleDubboActuatorSuite)newDubboActuatorSuite).RegisterService(ep);
                }
                if(newDubboActuatorSuite == null)
                {
                    newDubboActuatorSuite = new TelnetDubboActuatorSuite(ep.EndPoint, new DubboActuatorSuiteConf()
                    {
                        AssistConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteAssistConnectionAliveTime,
                        MasterConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteMasterConnectionAliveTime,
                        DubboRequestTimeout = _innerDubboServiceDriverConf.DubboRequestTimeout,
                        MaxConnections = _innerDubboServiceDriverConf.DubboActuatorSuiteMaxConnections
                    });
                }
                DubboActuatorSuiteEndPintInfo dubboActuatorSuiteEndPintInfo = new DubboActuatorSuiteEndPintInfo()
                {
                    EndPoint = ep.EndPoint,
                    ActuatorSuite = newDubboActuatorSuite,
                    ReferenceCount = 0
                };
                ep.InnerDubboActuatorSuite = dubboActuatorSuiteEndPintInfo.ActuatorSuite;
                if (InnerActuatorSuites.TryAdd(ep.EndPoint, ep))
                {
                    dubboActuatorSuiteEndPintInfo.ReferenceCount++;
                    _sourceDubboActuatorSuiteCollection.Add(ep.EndPoint, dubboActuatorSuiteEndPintInfo);
                    //UpdateTotalWeight();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 更新DubboServiceDriver服务节点
        /// EN: Reconciles the service driver's provider endpoints with the latest registry snapshot.
        /// </summary>
        /// <param name="dbEpList">最新的 Provider 节点列表。EN: Latest provider endpoint list.</param>
        /// <returns>新增和移除的节点总数。EN: Total number of endpoints added or removed.</returns>
        /// <exception cref="ArgumentException"><paramref name="dbEpList"/> 为空或不包含节点。EN: <paramref name="dbEpList"/> is null or contains no endpoints.</exception>
        public int UpdateActuatorSuiteEndPoints(List<DubboServiceEndPointInfo> dbEpList)
        {
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            int changeCount=0;
            //移出多余节点
            foreach(var insItem in InnerActuatorSuites)
            {
                
                //if (!dbEpList.Contains(insItem.Key))
                if(dbEpList.FirstOrDefault((it) => it.EndPoint.Equals(insItem.Key)) == null)
                {
                    InnerActuatorSuites.Remove(insItem.Key);
                    changeCount++;
                    if(_sourceDubboActuatorSuiteCollection.ContainsKey(insItem.Key))
                    {
                        _sourceDubboActuatorSuiteCollection[insItem.Key].ReferenceCount--;
                        if(_sourceDubboActuatorSuiteCollection[insItem.Key].ReferenceCount<=0)
                        {
                            _sourceDubboActuatorSuiteCollection[insItem.Key].ActuatorSuite.Dispose();
                            _sourceDubboActuatorSuiteCollection.Remove(insItem.Key);
                        }
                    }
                    else
                    {
                        MyLogger.LogError($"[UpdateEqualIPEndPoints]_sourceDubboActuatorSuiteCollection not contain {insItem.Key}");
                    }
                }
            }
            //添加新增节点
            foreach(var epItem in dbEpList)
            {
                if(!InnerActuatorSuites.ContainsKey(epItem.EndPoint))
                {
                    if(AddActuatorSuite(epItem)) changeCount++;
                }
            }
            if(changeCount!=0)
            {
                UpdateTotalWeight();
                UpdateConsistentHash();
            }
            return changeCount;
        }

        /// <summary>
        /// 以指定负载策略返回可用DubboActuatorSuite
        /// EN: Selects an available actuator suite using the specified load-balancing strategy.
        /// </summary>
        /// <param name="loadBalanceMode">负载均衡策略。EN: Load-balancing strategy.</param>
        /// <param name="qurey">请求内容，仅用于一致性 Hash 计算。EN: Request content used only as the consistent-hash key.</param>
        /// <returns>选中的执行器套件；当前服务没有节点时为 <see langword="null"/>。EN: Selected actuator suite, or <see langword="null"/> when the service has no endpoints.</returns>
        /// <exception cref="Exception">负载策略不受支持，或无法选出端点。EN: The load-balancing mode is unsupported or no endpoint can be selected.</exception>
        public IDubboActuatorSuite GetDubboActuatorSuite(LoadBalanceMode loadBalanceMode ,string qurey = null)
        {
            LastActivateTime = DateTime.Now;
            if (InnerActuatorSuites.Count==0)
            {
                return null;
            }
            if (InnerActuatorSuites.Count == 1)
            {
                return InnerActuatorSuites.First().Value.InnerDubboActuatorSuite;
            }
            DubboServiceEndPointInfo selectedDubboServiceEndPointInfo = null;
            switch (loadBalanceMode)
            {
                case LoadBalanceMode.Random:
                    Random random = new Random();
                    int randomNumber = random.Next(0, _totalWeightForActuatorSuites) + 1;
                    foreach(var weightedItem in InnerActuatorSuites)
                    {
                        if(randomNumber <= weightedItem.Value.Weight)
                        {
                            selectedDubboServiceEndPointInfo = weightedItem.Value;
                            break;
                        }
                        randomNumber = randomNumber - weightedItem.Value.Weight;
                    }
                    break;
                case LoadBalanceMode.RoundRobin:
                    foreach (var weightedItem in InnerActuatorSuites)
                    {
                        weightedItem.Value.NowDispatchWeight += weightedItem.Value.Weight;
                    }
                    var maxDispatchWeightItem = InnerActuatorSuites.MaxBy<KeyValuePair<IPEndPoint,DubboServiceEndPointInfo>,int>(it=>it.Value.NowDispatchWeight);
                    maxDispatchWeightItem.Value.NowDispatchWeight -= _totalWeightForActuatorSuites;
                    selectedDubboServiceEndPointInfo = maxDispatchWeightItem.Value;
                    break;
                case LoadBalanceMode.ConsistentHash:
                    if(qurey==null)
                    {
                        return GetDubboActuatorSuite(LoadBalanceMode.Random);
                    }
                    IPEndPoint nowEp = _dubboSuiteConsistentHash.GetNode(qurey);
                    selectedDubboServiceEndPointInfo = InnerActuatorSuites[nowEp];
                    break;
                case LoadBalanceMode.ShortestResponse:
                    DateTime nowTime = DateTime.Now;
                    int minQueryElapsedItemValue = InnerActuatorSuites.Min<KeyValuePair<IPEndPoint, DubboServiceEndPointInfo>, int>(it => (nowTime - it.Value.InnerDubboActuatorSuite.LastActivateTime).TotalSeconds < 60 ? it.Value.InnerDubboActuatorSuite.ActuatorSuiteStatusInfo.LastQueryElapsed : 0);
                    var minQueryElapsedItems = InnerActuatorSuites.Where(element => minQueryElapsedItemValue == ((nowTime - element.Value.InnerDubboActuatorSuite.LastActivateTime).TotalSeconds < 60 ? element.Value.InnerDubboActuatorSuite.ActuatorSuiteStatusInfo.LastQueryElapsed : 0));
                    if (minQueryElapsedItems.Count()==0)
                    {
                        var minQueryElapsedItem = InnerActuatorSuites.MinBy<KeyValuePair<IPEndPoint, DubboServiceEndPointInfo>, int>(it => (DateTime.Now - it.Value.InnerDubboActuatorSuite.LastActivateTime).TotalSeconds < 60 ? it.Value.InnerDubboActuatorSuite.ActuatorSuiteStatusInfo.LastQueryElapsed : 0);
                        selectedDubboServiceEndPointInfo = minQueryElapsedItem.Value;
                    }
                    else if(minQueryElapsedItems.Count() == 1)
                    {
                        selectedDubboServiceEndPointInfo = minQueryElapsedItems.First().Value;
                    }
                    else
                    {
                        random = new Random();
                        randomNumber = random.Next(0, minQueryElapsedItems.Count());
                        selectedDubboServiceEndPointInfo = minQueryElapsedItems.Skip(randomNumber).First().Value;
                    }
                    break;
                case LoadBalanceMode.LeastActive:
                    //未实现
                    return GetDubboActuatorSuite(LoadBalanceMode.Random);
                case LoadBalanceMode.P2CLoadBalance:
                    IDubboActuatorSuite providerA = GetDubboActuatorSuite(LoadBalanceMode.Random);
                    IDubboActuatorSuite providerB = GetDubboActuatorSuite(LoadBalanceMode.Random);
                    if (providerA?.ActuatorSuiteStatusInfo?.StatusInfo?.Load == null
                        || providerB?.ActuatorSuiteStatusInfo?.StatusInfo?.Load == null)
                    {
                        MyLogger.LogWarning(
                            "[GetDubboActuatorSuite] provider load is unavailable; " +
                            "falling back to the first random provider");
                        return providerA;
                    }
                    else
                    {
                        return providerA.ActuatorSuiteStatusInfo.StatusInfo.Load.Load > providerB.ActuatorSuiteStatusInfo.StatusInfo.Load.Load ? providerB : providerA;
                    }
                default:
                    throw new Exception($"[GetDubboActuatorSuite] nonsupported LoadBalanceMode {loadBalanceMode}");
            }
            if (selectedDubboServiceEndPointInfo == null)
            {
                throw new Exception("[GetDubboActuatorSuite] fialed get DubboActuatorSuite ,that selectedDubboServiceEndPointInfo is null");
            }
            return selectedDubboServiceEndPointInfo.InnerDubboActuatorSuite;
        }

        /// <summary>
        /// 释放当前服务持有的端点引用，并在引用计数归零时释放共享执行器。
        /// EN: Releases endpoint references held by this service and disposes shared actuator suites whose reference count reaches zero.
        /// </summary>
        public void Dispose()
        {
            if (InnerActuatorSuites != null)
            {
                foreach(var actuatorSuiteItem in InnerActuatorSuites)
                {
                    if(_sourceDubboActuatorSuiteCollection.ContainsKey(actuatorSuiteItem.Key))
                    {
                        _sourceDubboActuatorSuiteCollection[actuatorSuiteItem.Key].ReferenceCount--;
                        if(_sourceDubboActuatorSuiteCollection[actuatorSuiteItem.Key].ReferenceCount<=0)
                        {
                            _sourceDubboActuatorSuiteCollection[actuatorSuiteItem.Key].ActuatorSuite.Dispose();
                            _sourceDubboActuatorSuiteCollection.Remove(actuatorSuiteItem.Key);
                        }
                    }
                }
            }
            InnerActuatorSuites.Clear();
            InnerActuatorSuites = null;
            //_sourceDubboActuatorSuiteCollection是外部传入的公用对象，不要在这里做Clear操作
            _sourceDubboActuatorSuiteCollection = null;
            _dubboSuiteConsistentHash = null;
        }
    }

}
