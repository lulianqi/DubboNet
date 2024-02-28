using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;


namespace DubboNet.Clients
{
    public class DubboClient:IDisposable
    {

        /// <summary>
        /// 负载模式
        /// </summary>
        public enum LoadBalanceMode
        {
            Random,
            ShortestResponse,
            RoundRobin,
            LeastActive,
            ConsistentHash
        }

        /// <summary>
        /// 注册中心状态
        /// </summary>
        public enum RegistryState
        {
            Default, //默认状态（在不需要关注状态的时候，该状态是不更新的，会保持默认状态）
            DisConnect, //收到断开消息后，更新为DisConnect
            Connecting,
            Connected, //连接成功后
            TryConnect, //开始进入主动重新连接
            LostConnect //主动重连超时了
        }
        
        /// <summary>
        /// 服务端点信息(信息来源dubbo uri)
        /// </summary>
        public class DubboServiceEndPointInfo
        {
            public IPEndPoint EndPoint{ get;  set; } =null;
            public bool? Anyhost { get;  set; } = null;
            public string Application { get;  set; } = null;
            public string BeanName { get;  set; } = null;
            public bool? Deprecated { get;  set; } = null;
            public string Dubbo { get;  set; } = null;
            public bool? Dynamic { get;  set; } = null;
            public bool? Generic { get;  set; } = null;
            public string Interface { get;  set; } = null;
            public string Loadbalance { get;  set; } = null;
            public int? Pid { get;  set; } = null;
            public string Methods { get;  set; } = null;
            public bool? Register { get;  set; } = null;
            public string Revision { get;  set; } = null;
            public string Side { get;  set; } = null;
            public string Threads { get;  set; } = null;
            public int? Timeout { get;  set; } = null;
            public long? Timestamp { get;  set; } = null;
            public int Weight { get;  set; } = 100;

            public static DubboServiceEndPointInfo GetDubboServiceEndPointInfo(Uri dubboUri)
            {
                if(dubboUri == null)
                {
                    throw new ArgumentNullException(nameof(dubboUri));
                }
                DubboServiceEndPointInfo dubboServiceEndPointInfo = new DubboServiceEndPointInfo();
                dubboServiceEndPointInfo.EndPoint = new IPEndPoint(IPAddress.Parse(dubboUri.Host), dubboUri.Port);
                NameValueCollection queryParameters = System.Web.HttpUtility.ParseQueryString(dubboUri.Query);
                int tempIntValue = 0;
                bool tempBoolValue = false;
                long tempLongValue = 0;
                if(bool.TryParse(queryParameters["anyhost"], out tempBoolValue))
                {
                    dubboServiceEndPointInfo.Anyhost = tempBoolValue;
                }
                dubboServiceEndPointInfo.Application = queryParameters["application"];
                dubboServiceEndPointInfo.BeanName = queryParameters["bean.name"];
                if(bool.TryParse(queryParameters["dynamic"], out tempBoolValue))
                {
                    dubboServiceEndPointInfo.Dynamic = tempBoolValue;
                }
                dubboServiceEndPointInfo.Dubbo = queryParameters["dubbo"];
                if(bool.TryParse(queryParameters["generic"], out tempBoolValue))
                {
                    dubboServiceEndPointInfo.Generic = tempBoolValue;
                }
                dubboServiceEndPointInfo.Interface = queryParameters["interface"];
                dubboServiceEndPointInfo.Loadbalance = queryParameters["loadbalance"];
                 if(int.TryParse(queryParameters["pid"], out tempIntValue))
                {
                    dubboServiceEndPointInfo.Pid = tempIntValue;
                }
                dubboServiceEndPointInfo.Methods = queryParameters["methods"];
                if(bool.TryParse(queryParameters["register"], out tempBoolValue))
                {
                    dubboServiceEndPointInfo.Register = tempBoolValue;
                }
                dubboServiceEndPointInfo.Revision = queryParameters["revision"];
                dubboServiceEndPointInfo.Side = queryParameters["side"];
                dubboServiceEndPointInfo.Threads = queryParameters["threads"];
                if(int.TryParse(queryParameters["timeout"], out tempIntValue))
                {
                    dubboServiceEndPointInfo.Timeout = tempIntValue;
                }
                if(long.TryParse(queryParameters["timestamp"], out tempLongValue))
                {
                    dubboServiceEndPointInfo.Timestamp = tempLongValue;
                }
                if(int.TryParse(queryParameters["weight"], out tempIntValue))
                {
                    dubboServiceEndPointInfo.Weight = tempIntValue;
                }
                return dubboServiceEndPointInfo;
            }
        }

        /// <summary>
        /// 服务端点信息集合
        /// </summary>
        public class ServiceEndPointsInfo
        {
            public string ErrorInfo { get;  set; } = null;
            public List<DubboServiceEndPointInfo> EndPoints { get;  set; } = new List<DubboServiceEndPointInfo>();
        }

        public class DubboClientConf
        {
            public string DubboRootPath { get; set; } = "/dubbo/";
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
            public int MaintainServiceNum { get; set; } = 20;
            public LoadBalanceMode NowLoadBalanceMode { get; set; } = LoadBalanceMode.Random;
            public string DefaultFuncName { get; set; } = null;
            public string DefaultServiceName { get; set; } = null;
        }

        private static MultiMyZookeeperStorage DubboClientMultiMyZookeeperStorage = new MultiMyZookeeperStorage();

        private MyZookeeper _innerMyZookeeper;

        private DubboClientZookeeperWatcher _dubboClientZookeeperWatcher;

        private DubboDriverCollection _dubboDriverCollection;

        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _retainDubboActuatorSuiteCollection = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();

        private ConcurrentDictionary<string, Task<ServiceEndPointsInfo>> _concurrentGetProviderEndPointsTasks = new ConcurrentDictionary<string, Task<ServiceEndPointsInfo>>();

        private volatile bool _isInReLoadDubboDriverCollectionTask = false;

        internal bool IsDisposed { get;private set;}= false;

        /// <summary>
        /// 注册中心的状态（内部状态，对调用方隐藏。实际上DubboClient网络状态是自动维护的，对外接口使用上是无状态的）
        /// </summary>
        internal RegistryState InnerRegistryState { get; set; } = RegistryState.Default;

        /// <summary>
        /// 获取只读的DubboActuatorSuiteCollection
        /// </summary>
        internal ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> DubboActuatorSuiteCollection => new ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>(_retainDubboActuatorSuiteCollection);

        /// <summary>
        /// Zookeeper上默认的Dubbo跟路径，默认/dubbo/
        /// </summary>
        public string DubboRootPath { get; private set; } = "/dubbo/";

        /// <summary>
        /// 默认当前Dubbo方法名称
        /// </summary>
        public string DefaultFuncName { get; private set; } = null;

        /// <summary>
        /// 默认当前Dubbo服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; } = null;

        /// <summary>
        /// 获当前负载模式
        /// </summary>
        public LoadBalanceMode NowLoadBalanceMode { get; private set; } = LoadBalanceMode.Random;

        /// <summary>
        /// 单个Dubbo服务节点最多能开启的连接数量（默认情况会保持1个连接，当对单个节点出现并行请求时会自动开启更多连接）
        /// </summary>
        public int DubboActuatorSuiteMaxConnections { get;private set; } = 20;

        /// <summary>
        /// 服务节点辅助连接不再活跃超过此时间时释放辅助连接（单位秒,默认300s，0表示不进行主动释放）
        /// </summary>
        public int DubboActuatorSuiteAssistConnectionAliveTime { get; private set; } = 60 * 5;

        /// <summary>
        /// 服务节点主连接不再活跃超过此时间时释放辅助连接（单位秒,默认1200s，0表示不进行主动释放）
        /// </summary>
        public int DubboActuatorSuiteMasterConnectionAliveTime { get; private set; } = 60 * 20;

        /// <summary>
        /// Dubbo请求的最大超时时间
        /// </summary>
        public int DubboRequestTimeout { get; private set; } = 60 * 1000;

        /// <summary>
        /// DubboClient可最大缓存的复用服务数量,0表示无限制（如果当前DubboClient实例会大量请求不同服务，可以扩大该数值）
        /// </summary>
        public int MaintainServiceNum { get;private set; } = 20;



        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <exception cref="ArgumentException"></exception>
        public DubboClient(string zookeeperCoonectString) : this(zookeeperCoonectString,new DubboClientConf())
        {
        }

        public DubboClient(string zookeeperCoonectString , DubboClientConf dubboClientConf)
        {
            if (string.IsNullOrEmpty(zookeeperCoonectString))
            {
                throw new ArgumentException($"“{nameof(zookeeperCoonectString)}” can not be null or empty", nameof(zookeeperCoonectString));
            }
            if (dubboClientConf == null)
            {
                //throw new ArgumentNullException(nameof(dubboClientConf));
                dubboClientConf = new DubboClientConf();
            }
            DubboRootPath = dubboClientConf.DubboRootPath;
            DefaultFuncName = dubboClientConf.DefaultFuncName;
            DefaultServiceName = dubboClientConf.DefaultServiceName;
            NowLoadBalanceMode = dubboClientConf.NowLoadBalanceMode;
            DubboActuatorSuiteMaxConnections = dubboClientConf.DubboActuatorSuiteMaxConnections;
            DubboActuatorSuiteAssistConnectionAliveTime = dubboClientConf.DubboActuatorSuiteAssistConnectionAliveTime;
            DubboActuatorSuiteMasterConnectionAliveTime = dubboClientConf.DubboActuatorSuiteMasterConnectionAliveTime;
            DubboRequestTimeout = dubboClientConf.DubboRequestTimeout;
            MaintainServiceNum = dubboClientConf.MaintainServiceNum;

            _innerMyZookeeper = DubboClientMultiMyZookeeperStorage.GetMyZookeeper(zookeeperCoonectString);
            _dubboClientZookeeperWatcher = new DubboClientZookeeperWatcher(this);
            _dubboDriverCollection = new DubboDriverCollection(_retainDubboActuatorSuiteCollection, new DubboDriverCollection.DubboDriverCollectionConf()
            {
                DefaultServiceName = DefaultServiceName,
                DubboActuatorSuiteAssistConnectionAliveTime = DubboActuatorSuiteAssistConnectionAliveTime,
                DubboActuatorSuiteMasterConnectionAliveTime = DubboActuatorSuiteMasterConnectionAliveTime,
                DubboActuatorSuiteMaxConnections = DubboActuatorSuiteMaxConnections,
                DubboRequestTimeout = DubboRequestTimeout,
                MaintainServiceNum = MaintainServiceNum,
                NowLoadBalanceMode = NowLoadBalanceMode
            });

        }

        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <param name="funcEndPoint">默认方法入口（完整名称包括服务名称 eg:ServiceName.FuncName）</param>
        /// <exception cref="ArgumentException"></exception>
        public DubboClient(string zookeeperCoonectString, string funcEndPoint ) : this(zookeeperCoonectString)
        {
            if (funcEndPoint.Contains('#'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('#');
                DefaultFuncName = funcEndPoint.Substring(tempSpitIndex + 1);
                DefaultServiceName = funcEndPoint.Remove(tempSpitIndex);
            }
            else if (DefaultFuncName.Contains('.'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('.');
                DefaultFuncName = funcEndPoint.Substring(tempSpitIndex + 1);
                DefaultServiceName = funcEndPoint.Remove(tempSpitIndex);
            }
            else
            {
                throw new ArgumentException($"“{nameof(funcEndPoint)}” is error", nameof(funcEndPoint));
            }
        }


        public async Task<DubboRequestResult> SendRequestAsync(string funcEndPoint, string req)
        {
            if(IsDisposed)
            {
                throw new Exception("DubboClient is disposed");
            }
            Tuple<string, string> tuple = GetSeviceNameFormFuncEndPoint(funcEndPoint);
            string nowServiceName = tuple.Item1;
            string nowFuncName = tuple.Item2;
            if (string.IsNullOrEmpty(nowServiceName) || string.IsNullOrEmpty(nowFuncName))
            {
                throw new ArgumentException("can not find the ServiceName or FuncName");
            }
            AvailableDubboActuatorInfo availableDubboActuatorInfo = _dubboDriverCollection.GetDubboActuatorSuite(nowServiceName, NowLoadBalanceMode);
            //获取到可用的DubboActuatorSuite
            if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite)
            {
                return await availableDubboActuatorInfo.AvailableDubboActuatorSuite.SendQuery($"{nowServiceName}.{nowFuncName}", req);
            }
            //没有_dubboDriverCollection没有目标服务，尝试添加服务节点 （新的ServiceName都会通过这里将节点添加进来）
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoDubboServiceDriver)
            {
                ServiceEndPointsInfo serviceEndPointsInfo = await ConcurrentGetProviderEndPoints(nowServiceName);
                if (serviceEndPointsInfo.ErrorInfo != null)
                {
                    string tempErrorMes = $"[SendRequestAsync] GetSeviceProviderEndPoints fail {nowServiceName} -> {serviceEndPointsInfo.ErrorInfo}";
                    MyLogger.LogWarning(tempErrorMes);
                    return new DubboRequestResult()
                    {
                        ServiceElapsed = -1,
                        ErrorMeaasge = tempErrorMes
                    };
                }
                else
                {
                    _dubboDriverCollection.AddDubboServiceDriver(nowServiceName, serviceEndPointsInfo.EndPoints);
                    return await SendRequestAsync(funcEndPoint, req);
                }
            }
            //没有获取到可用的DubboActuatorSuite，因为服务里的节点信息为空
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoActuatorInService)
            {
                //实际上是有watch会自动更新，这里主动更新一次可以兼容watch异常的情况（这里会触发同一个路径注册2个watch，不过重复的watch只会触发一次）
                ServiceEndPointsInfo serviceEndPointsInfo = await ConcurrentGetProviderEndPoints(nowServiceName);
                string tempErrorMes = null;
                if (serviceEndPointsInfo.ErrorInfo != null)
                {
                    tempErrorMes = $"[SendRequestAsync] GetSeviceProviderEndPoints fail {nowServiceName} -> {serviceEndPointsInfo.ErrorInfo}";
                }
                else if (serviceEndPointsInfo.EndPoints.Count > 0)
                {
                    _dubboDriverCollection.AddDubboServiceDriver(nowServiceName, serviceEndPointsInfo.EndPoints);
                    return await SendRequestAsync(funcEndPoint, req);
                }
                else
                {
                    tempErrorMes = $"[SendRequestAsync] fail {nowServiceName} do not has any Provider";
                }
                MyLogger.LogWarning(tempErrorMes);
                return new DubboRequestResult()
                {
                    ServiceElapsed = -1,
                    ErrorMeaasge = tempErrorMes
                };
            }
            //没有获取到可用的DubboActuatorSuite，因为没有可用的DubboActuatorSuite（比如配置的资源耗尽）
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoAvailableActuator)
            {
                return new DubboRequestResult()
                {
                    ServiceElapsed = -1,
                    ErrorMeaasge = $"NoAvailableActuator in {nowServiceName}"
                };
            }
            else
            {
                return new DubboRequestResult()
                {
                    ServiceElapsed = -1,
                    ErrorMeaasge = "Unkonw error"
                };
            }
        }


        /// <summary>
        /// 是否已经存在ServiceDriver（内部调用，不用暴露）
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        internal bool HasServiceDriver(string serviceName)
        {
            return _dubboDriverCollection.HasServiceDriver(serviceName);
        }

        /// <summary>
        /// 尝试重新连接注册中心(用于注册中心断开后，开启一个重连任务)
        /// 实际上ZooKeeper客户端会自动进行反复的重连，直到最终成功连接上ZooKeeper集群中的一台机器。再次连接上服务端的客户端有可能会处于以下两种状态之一。
        /// CONNECTED：如果在会话超时时间内重新连接上了ZooKeeper集群中任意一台机器，那么被视为重连成功，EXPIRED：如果是在会话超时时间以外重新连接上，那么服务端其实已经对该会话进行了会话清理操作，因此再次连接上的会话将被视为非法会话。
        /// 大部分情况都将是EXPIRED（同一个watch即使注册了很多path，也指挥收到一次）这个时候ZooKeeper是要重新构建才能正常使用的
        /// </summary>
        /// <param name="delayTime"></param>
        /// <param name="timeOut"></param>
        /// <returns></returns>
        internal async Task<bool> TryDoConnectRegistryTaskAsync(int delayTime = 1000, int timeOut = 1000*60*30)
        {
            DateTime expiredTime = DateTime.Now.AddMilliseconds(timeOut);
            //如果已经有TryDoConnectRegistryTaskAsync任务在进行，直接取结果，不用真的开启任务(这个时候是不会更新InnerRegistryState的)
            if (InnerRegistryState == RegistryState.TryConnect)
            {
                while (DateTime.Now < expiredTime)
                {
                    await Task.Delay(delayTime);
                    if(InnerRegistryState == RegistryState.Connected)
                    {
                        return true;
                    }
                    else if(InnerRegistryState == RegistryState.TryConnect)
                    {
                        continue;
                    }
                    else
                    {
                        int remainTimeOut = (int)(expiredTime - DateTime.Now).TotalMilliseconds;
                        if (remainTimeOut > 10)
                        {
                            return await TryDoConnectRegistryTaskAsync(delayTime, remainTimeOut);
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
                return InnerRegistryState == RegistryState.Connected;
            }
            InnerRegistryState = RegistryState.TryConnect;
            //如果timeOut为0则直接执行一次ConnectZooKeeperAsync
            if (timeOut==0)
            {
                if(await _innerMyZookeeper.ConnectZooKeeperAsync())
                {
                    InnerRegistryState = RegistryState.Connected;
                    return true;
                }
                InnerRegistryState = RegistryState.LostConnect;
                return false;
            }
            //开始Retry任务
            while(DateTime.Now<expiredTime)
            {
                if(IsDisposed)
                {
                    break;
                }
                if (_innerMyZookeeper.IsConnected)
                {
                    InnerRegistryState = RegistryState.Connected;
                    return true;
                }
                if(await _innerMyZookeeper.ConnectZooKeeperAsync())
                {
                    InnerRegistryState = RegistryState.Connected;
                    return true;
                }
                await Task.Delay(delayTime);
            }
            InnerRegistryState = RegistryState.LostConnect;
            return false;
        }

        /// <summary>
        /// 重新刷新dubboDriverCollection服务节点信息（发生在注册中心异常断开恢复连接时，其一断开的时间节点信息可能会变化，其二重新连接后需要重新注册watch）
        /// </summary>
        /// <returns></returns>
        internal async Task ReLoadDubboDriverCollection()
        {
            if (_isInReLoadDubboDriverCollectionTask) return;
            _isInReLoadDubboDriverCollectionTask = true;
            foreach (DubboServiceDriver dubboServiceDriverItem in _dubboDriverCollection)
            {
                await ReflushProviderAsync(dubboServiceDriverItem.ServiceName);
            }
            _isInReLoadDubboDriverCollectionTask = false;
        }

        /// <summary>
        /// 刷新服务节点信息
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        internal async Task<bool> ReflushProviderAsync(string serviceName ,bool isFullPath = false)
        {
            ServiceEndPointsInfo serviceEndPointsInfo = await ConcurrentGetProviderEndPoints(serviceName,isFullPath);
            if (serviceEndPointsInfo.ErrorInfo != null)
            {
                MyLogger.LogError($"[ReflushProviderByPathAsync] fail by {serviceName} : {serviceEndPointsInfo.ErrorInfo}");
                return false;
            }
            else
            {
                string nowServiceName = serviceName;
                if(isFullPath)
                {
                    if (serviceName.StartsWith(DubboRootPath) && serviceName.EndsWith("/providers"))
                    {
                         nowServiceName = serviceName.Substring(DubboRootPath.Length, serviceName.Length- DubboRootPath.Length - "/providers".Length);
                    }
                    else
                    {
                        MyLogger.LogError($"[ReflushProviderByPathAsync] fail : {serviceName} is error path for Service");
                        return false;
                    }
                }
                return _dubboDriverCollection.AddDubboServiceDriver(nowServiceName, serviceEndPointsInfo.EndPoints);
            }
        }

        /// <summary>
        /// 对GetSeviceProviderEndPointsAsync重复并发的封装（用于应对短时间并行同时对同一个serviceName节点信息进行获取，同时重复获会耗费不必要的性能，这里会复用可复用的Task完成获取任务）
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        private async Task<ServiceEndPointsInfo> ConcurrentGetProviderEndPoints(string serviceName, bool isFullPath = false)
        {
            if(_concurrentGetProviderEndPointsTasks.ContainsKey(serviceName))
            {
                return await _concurrentGetProviderEndPointsTasks[serviceName];
            }
            else
            {
                Task<ServiceEndPointsInfo> getProviderEndPointsTask = GetSeviceProviderEndPointsAsync(serviceName, isFullPath);
                if(!_concurrentGetProviderEndPointsTasks.TryAdd(serviceName,getProviderEndPointsTask))
                {
                    MyLogger.LogError($"[ConcurrentGetProviderEndPoints] {serviceName} TryAdd fail");
                }
                ServiceEndPointsInfo serviceEndPointsInfo = await getProviderEndPointsTask;
                if(!_concurrentGetProviderEndPointsTasks.TryRemove(serviceName,out _))
                {
                    MyLogger.LogError($"[ConcurrentGetProviderEndPoints] {serviceName} TryRemove fail");
                }
                return serviceEndPointsInfo;
            }
        }

        /// <summary>
        /// 根据服务名，在注册中心查找服务节点信息(如果服务存在会默认注册_dubboClientZookeeperWatcher，以达到自动更新的目的)
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private async Task<ServiceEndPointsInfo> GetSeviceProviderEndPointsAsync(string serviceName ,bool isFullPath = false)
        {
            if(string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            string nowFullPath = isFullPath? serviceName: $"{DubboRootPath}{serviceName}/providers";
            ServiceEndPointsInfo serviceEndPointsInfo = new ServiceEndPointsInfo();
            Stat stat =await _innerMyZookeeper.ExistsAsync(nowFullPath);
            if (stat==null)
            {
                if(!_innerMyZookeeper.IsConnected)
                {
                    serviceEndPointsInfo.ErrorInfo = $"can not get [{serviceName}] that _innerMyZookeeper can not connect";
                }
                else if((await _innerMyZookeeper.ExistsAsync($"{DubboRootPath}{serviceName}"))==null)
                {
                    serviceEndPointsInfo.ErrorInfo = $"serviceName [{serviceName}] is error";
                }
                else
                {
                    serviceEndPointsInfo.ErrorInfo = $"no provider in [{serviceName}]";
                }
            }
            else if(stat.getNumChildren()<=0)
            {
                serviceEndPointsInfo.ErrorInfo = $"no provider endpoint in [{serviceName}]";
            }
            else
            {
                //注册中心之前已经断开了，现在连接上了，需要重新更新watch
                if(InnerRegistryState == RegistryState.LostConnect || InnerRegistryState == RegistryState.DisConnect)
                {
                    _ = ReLoadDubboDriverCollection();
                }

                ChildrenResult childrenResult = await _innerMyZookeeper.GetChildrenAsync(nowFullPath,_dubboClientZookeeperWatcher).ConfigureAwait(false);
                if (childrenResult == null)
                {
                    serviceEndPointsInfo.ErrorInfo = $"GetChildrenAsync error [{serviceName}]";
                }
                {
                    foreach(var child in childrenResult.Children)
                    {
                        if (child.StartsWith("dubbo%3A%2F%2F"))
                        {
                            string nowDubboPath = System.Web.HttpUtility.UrlDecode(child, System.Text.Encoding.UTF8);
                            Uri nowDubboUri;
                            if(Uri.TryCreate(nowDubboPath, UriKind.Absolute, out nowDubboUri))
                            {
                                if (IPAddress.TryParse(nowDubboUri.Host, out _))
                                {
                                    serviceEndPointsInfo.EndPoints.Add(DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(nowDubboUri));
                                }
                                else
                                {
                                    //这里如果有使用域名或主机名称的可能性，这里可以继续解析为IP
                                    MyLogger.LogWarning($"[GetSeviceProviderEndPoints] IPAddress.TryParse error {child}");
                                    continue;
                                }
                            }
                            else
                            {
                                MyLogger.LogWarning($"[GetSeviceProviderEndPoints] Uri.TryCreate error {child}");
                                continue;
                            }
                        }
                        else
                        {
                            MyLogger.LogWarning($"[GetSeviceProviderEndPoints] childrenResult.Children formate error {child}");
                            continue;
                        }
                    }
                }
            }
            return serviceEndPointsInfo;
        }

        /// <summary>
        /// 根据funcEndPoint获取服务名称与方法名称（用于参数解析，如果需要使用默认名称返回）
        /// </summary>
        /// <param name="funcEndPoint"></param>
        /// <returns></returns>
        private Tuple<string,string> GetSeviceNameFormFuncEndPoint(string funcEndPoint)
        {
            string serviceName, funcName = null;
            if(string.IsNullOrEmpty(funcEndPoint))
            {
                serviceName = DefaultServiceName;
                funcName = DefaultFuncName;
            }
            else if (funcEndPoint.Contains('#'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('#');
                funcName = funcEndPoint.Substring(tempSpitIndex + 1);
                serviceName = funcEndPoint.Remove(tempSpitIndex);
            }
            else if (funcEndPoint.Contains('.'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('.');
                funcName = funcEndPoint.Substring(tempSpitIndex + 1);
                serviceName = funcEndPoint.Remove(tempSpitIndex);
            }
            else
            {
                serviceName = DefaultServiceName;
                funcName = funcEndPoint;
            }
            return new Tuple<string,string>(serviceName, funcName);
        }


        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        private List<ZNode> GetDubboProvidersNode(ZNode zNode)
        {
            ZNode dubboNodes = zNode.FilterLeafNode(nd => nd.Path?.StartsWith("dubbo%3A%2F%2F") ?? false, "DubboNode");
            List<ZNode> resultZNodes = new List<ZNode>();
            foreach (var tn in dubboNodes.Children)
            {
                if (tn.Path == "providers")
                {
                    //resultZNodes.AddRange(tn.GetLeafNodeList());
                    resultZNodes.AddRange(tn.Children);
                }
            }
            return resultZNodes;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)
                }
                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                IsDisposed = true;
                DubboClientMultiMyZookeeperStorage.RemoveMyZookeeper(_innerMyZookeeper.ConnectionString);
                _dubboClientZookeeperWatcher = null;
                _dubboDriverCollection.Dispose();
                _dubboDriverCollection = null;
                foreach(var item in _retainDubboActuatorSuiteCollection)
                {
                    item.Value?.ActuatorSuite.Dispose();
                }
                _retainDubboActuatorSuiteCollection.Clear();
                _retainDubboActuatorSuiteCollection = null;
            }
        }

        // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        ~DubboClient()
        {
           //不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
           Dispose(disposing: false);
        }

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
