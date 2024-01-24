using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using static DubboNet.Clients.DubboClient;
using static DubboNet.Clients.DubboDriverCollection;
using static DubboNet.DubboService.DubboActuator;

namespace DubboNet.Clients
{
    public class DubboClient
    {

        public enum LoadBalanceMode
        {
            Random,
            ShortestResponse,
            RoundRobin,
            LeastActive,
            ConsistentHash
        }

        internal class DubboClientZookeeperWatcher : Watcher
        {

            public string Name { get; private set; }
            public DubboClient InnerDubboClient { get; private set; }

            public DubboClientZookeeperWatcher(DubboClient dubboClient , string name = null)
            {
                Name = name?? "DubboClientZookeeperWatcher";
                InnerDubboClient= dubboClient;
            }

            public override async Task process(WatchedEvent @event)
            {
                MyLogger.LogInfo($"{Name} recieve: Path-{@event.getPath()}     State-{@event.getState()}    Type-{@event.get_Type()}");
                if (@event.get_Type() == Event.EventType.NodeChildrenChanged)
                {
                    await InnerDubboClient.ReflushProviderByPathAsync(@event.getPath());
                }
                //return Task.FromResult(0);
            }
        }
        public class ServiceEndPointsInfo
        {
            public string ErrorInfo { get;  set; } = null;
            public List<IPEndPoint> EndPoints { get;  set; } = new List<IPEndPoint>();
        }

        private MyZookeeper _innerMyZookeeper;

        private DubboClientZookeeperWatcher _dubboClientZookeeperWatcher;

        private DubboDriverCollection _dubboDriverCollection;

        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _retainDubboActuatorSuiteCollection = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();


        /// <summary>
        /// 获取只读的DubboActuatorSuiteCollection
        /// </summary>
        internal ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> DubboActuatorSuiteCollection => new ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>(_retainDubboActuatorSuiteCollection);

        /// <summary>
        /// Zookeeper上默认的Dubbo跟路径，默认/dubbo/
        /// </summary>
        public string DubboRootPath { get; set; } = "/dubbo/";

        /// <summary>
        /// 默认当前Dubbo方法名称
        /// </summary>
        public string DefaultFuncName { get; private set; }

        /// <summary>
        /// 默认当前Dubbo服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 获当前负载模式
        /// </summary>
        public LoadBalanceMode NowLoadBalanceMode { get; private set; } = LoadBalanceMode.Random;


        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <exception cref="ArgumentException"></exception>
        public DubboClient(string zookeeperCoonectString)
        {
            if (string.IsNullOrEmpty(zookeeperCoonectString))
            {
                throw new ArgumentException($"“{nameof(zookeeperCoonectString)}”不能为 null 或空。", nameof(zookeeperCoonectString));
            }
            _innerMyZookeeper = new MyZookeeper(zookeeperCoonectString);
            _dubboClientZookeeperWatcher = new DubboClientZookeeperWatcher(this);
            _dubboDriverCollection = new DubboDriverCollection(_retainDubboActuatorSuiteCollection);
            DefaultFuncName = null;
            DefaultServiceName = null;
        }

        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <param name="defaultServiceName">默认服务名称</param>
        /// <param name="defaultFuncName">默认方法名称</param>
        /// <exception cref="Exception"></exception>
        public DubboClient(string zookeeperCoonectString, string defaultServiceName, string defaultFuncName) : this(zookeeperCoonectString)
        {
            DefaultServiceName = defaultServiceName;
            DefaultFuncName = defaultFuncName;
            if (string.IsNullOrEmpty(DefaultServiceName) && !string.IsNullOrEmpty(DefaultFuncName))
            {
                throw new Exception("defaultServiceName can not be null unless defaultFuncName is null");
            }
        }

        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <param name="funcEndPoint">默认方法入口（完整名称包括服务名称 eg:ServiceName.FuncName）</param>
        /// <exception cref="ArgumentException"></exception>
        public DubboClient(string zookeeperCoonectString, string funcEndPoint) : this(zookeeperCoonectString)
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


        public async Task<DubboRequestResult> SendRequestAsync(string req)
        {
            throw new NotImplementedException();
        }

        public async Task<DubboRequestResult> SendRequestAsync(string funcEndPoint ,string req)
        {
            Tuple<string, string> tuple = GetSeviceNameFormFuncEndPoint(funcEndPoint);
            string nowServiceName = tuple.Item1;
            string nowFuncName = tuple.Item2;
            if(string.IsNullOrEmpty(nowServiceName)|| string.IsNullOrEmpty(nowFuncName)) 
            {
                throw new ArgumentException("can not find the ServiceName or FuncName");
            }
            AvailableDubboActuatorInfo availableDubboActuatorInfo = _dubboDriverCollection.GetDubboActuatorSuite(nowServiceName, NowLoadBalanceMode);
            //获取到可用的DubboActuatorSuite
            if (availableDubboActuatorInfo.ResultType== AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite)
            {
                return await availableDubboActuatorInfo.AvailableDubboActuatorSuite.SendQuery($"{nowServiceName}.{nowFuncName}", req);
            }
            //没有_dubboDriverCollection没有目标服务，尝试添加服务节点 （新的ServiceName都会通过这里将节点添加进来）
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoDubboServiceDriver)
            {
                ServiceEndPointsInfo serviceEndPointsInfo = await GetSeviceProviderEndPoints(nowServiceName);
                if(serviceEndPointsInfo.ErrorInfo!=null)
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

            }
            //没有获取到可用的DubboActuatorSuite，因为没有可用的DubboActuatorSuite
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoAvailableActuator)
            {

            }
            else
            {

            }
            throw new NotImplementedException();
        }

        private async Task ReflushProviderByPathAsync(string nowFullPath)
        {
            ServiceEndPointsInfo serviceEndPointsInfo = await GetSeviceProviderEndPoints(nowFullPath,true);
            if (serviceEndPointsInfo.ErrorInfo != null)
            {
                MyLogger.LogWarning($"[ReflushProviderByPathAsync] fail by {nowFullPath} : serviceEndPointsInfo.ErrorInfo");
            }
            else
            {
                if (nowFullPath.StartsWith(DubboRootPath) && nowFullPath.EndsWith("/providers"))
                {
                    string nowServiceName = nowFullPath.Substring(DubboRootPath.Length, nowFullPath.Length- DubboRootPath.Length - "/providers".Length);
                    _dubboDriverCollection.AddDubboServiceDriver(nowServiceName, serviceEndPointsInfo.EndPoints);
                }
                else
                {
                    MyLogger.LogWarning($"[ReflushProviderByPathAsync] fail : {nowFullPath} is error path for Service");
                }
            }
        }

        /// <summary>
        /// 根据服务名，在注册中心查找服务节点信息
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private async Task<ServiceEndPointsInfo> GetSeviceProviderEndPoints(string serviceName ,bool isFullPath = false)
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
                if((await _innerMyZookeeper.ExistsAsync($"{DubboRootPath}{serviceName}"))==null)
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
                                IPAddress nowIp;
                                if (IPAddress.TryParse(nowDubboUri.Host, out nowIp))
                                {
                                    serviceEndPointsInfo.EndPoints.Add(new IPEndPoint(nowIp, nowDubboUri.Port));
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
            string serviceName, funName = null;
            if(string.IsNullOrEmpty(funcEndPoint))
            {
                serviceName = DefaultServiceName;
                funName = DefaultFuncName;
            }
            else if (funcEndPoint.Contains('#'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('#');
                serviceName = funcEndPoint.Substring(tempSpitIndex + 1);
                funName = funcEndPoint.Remove(tempSpitIndex);
            }
            else if (DefaultFuncName.Contains('.'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('.');
                serviceName = funcEndPoint.Substring(tempSpitIndex + 1);
                funName = funcEndPoint.Remove(tempSpitIndex);
            }
            else
            {
                serviceName = DefaultServiceName;
                funName = funcEndPoint;
            }
            return new Tuple<string,string>(serviceName, funName);
        }

        private async Task InitServiceHost()
        {
            string nowFuncPath = $"{DubboRootPath}{DefaultServiceName}";
            ZNode tempZNode = await _innerMyZookeeper.GetZNodeTree(nowFuncPath);
            if (tempZNode == null)
            {
                throw new Exception($"[GetServiceHost] error : can not GetZNodeTree from {DefaultServiceName} ");
            }
            List<ZNode> providersNodes = GetDubboProvidersNode(tempZNode);
            //删除已经无效的节点
            if (_dubboDriverCollection.Count > 0)
            {
                Func<DubboActuator, bool> filterFunc = new Func<DubboActuator, bool>((dt) =>
                {
                    bool isSholdAlive = false;
                    foreach (ZNode providerNode in providersNodes)
                    {
                        string path = System.Web.HttpUtility.UrlDecode(providerNode.Path, System.Text.Encoding.UTF8);
                        Uri uri = new Uri(path);
                        if (dt.DubboHost == uri.Host && dt.DubboPort == uri.Port)
                        {
                            isSholdAlive = true;
                            break;
                        }
                    }
                    return !isSholdAlive;
                });
                //_dubboDriverCollection.RemoveByFilter(filterFunc);
            }
            //添加新节点
            foreach (ZNode providerNode in providersNodes)
            {
                string path = System.Web.HttpUtility.UrlDecode(providerNode.Path, System.Text.Encoding.UTF8);
                Uri uri = new Uri(path);
                //if (!_dubboDriverCollection.IsInclude(uri.Host, uri.Port))
                //{
                //    _dubboDriverCollection.AddDubboMan(uri.Host, uri.Port);
                //}
            }

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
    }
}
