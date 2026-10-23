using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;


namespace DubboNet.Clients
{
    /// <summary>
    /// Dubbo 客户端，负责服务发现、负载均衡、元数据解析以及请求发送。
    /// EN: Dubbo client responsible for service discovery, load balancing, metadata resolution, and request dispatch.
    /// </summary>
    public class DubboClient:IDisposable
    {

        /// <summary>
        /// 负载模式
        /// EN: Load-balancing mode.
        /// </summary>
        public enum LoadBalanceMode
        {
            /// <summary>
            /// 加权随机，按权重设置随机概率。
            /// EN: Weighted random selection using provider weights as probabilities.
            /// </summary>
            Random,
            /// <summary>
            /// 加权最短响应优先，在最近一个滑动窗口中，响应时间越短，越优先调用。相同响应时间的进行加权随机。
            /// EN: Prefer the shortest recent response time, using weighted random selection for ties.
            /// </summary>
            ShortestResponse,
            /// <summary>
            /// 加权轮询，按公约后的权重设置轮询比率，循环调用节点(实现平滑加权轮询算法)
            /// EN: Smooth weighted round-robin selection.
            /// </summary>
            RoundRobin,
            /// <summary>
            /// 未实现
            /// 加权最少活跃调用优先，活跃数越低，越优先调用，相同活跃数的进行加权随机。活跃数指调用前后计数差（针对特定提供者：请求发送数 - 响应返回数），表示特定提供者的任务堆积量，活跃数越低，代表该提供者处理能力越强。
            /// EN: Reserved for least-active selection; this mode is not currently implemented.
            /// </summary>
            LeastActive,
            /// <summary>
            /// 一致性 Hash，相同参数的请求总是发到同一提供者。
            /// EN: Consistent hashing that routes equal request parameters to the same provider.
            /// </summary>
            ConsistentHash,
            /// <summary>
            /// 从可用的provider列表中做两次随机选择，选出两个节点providerA和providerB,比较providerA和providerB两个节点，选择其“当前正在处理的连接数”较小的那个节点。
            /// EN: Power-of-two-choices selection using the less busy of two random providers.
            /// </summary>
            P2CLoadBalance
        }

        /// <summary>
        /// 注册中心状态
        /// EN: Registry connection state.
        /// </summary>
        public enum RegistryState
        {
            /// <summary>默认状态。EN: Default state before active registry-state tracking.</summary>
            Default, //默认状态（在不需要关注状态的时候，该状态是不更新的，会保持默认状态）
            /// <summary>已收到断开事件。EN: A disconnection event has been received.</summary>
            DisConnect, //收到断开消息后，更新为DisConnect
            /// <summary>正在建立连接。EN: A connection is being established.</summary>
            Connecting,
            /// <summary>连接成功。EN: The registry is connected.</summary>
            Connected, //连接成功后
            /// <summary>正在主动重连。EN: An active reconnection attempt is in progress.</summary>
            TryConnect, //开始进入主动重新连接
            /// <summary>主动重连已超时。EN: Active reconnection timed out.</summary>
            LostConnect //主动重连超时了
        }


        /// <summary>
        /// DubboClient配置
        /// EN: Configuration options for <see cref="DubboClient"/>.
        /// </summary>
        public class DubboClientConf
        {
            /// <summary>
            /// 注册位置；不填写时 ZooKeeper 使用 <c>/dubbo/</c>，Nacos Naming 使用 <c>DEFAULT_GROUP</c>。
            /// EN: Registry location; when omitted, ZooKeeper uses <c>/dubbo/</c> and Nacos Naming uses <c>DEFAULT_GROUP</c>.
            /// </summary>
            public string DubboRootPath { get; set; } = null;
            /// <summary>
            /// Dubbo 元数据中心地址；支持 ZooKeeper 与以 <c>nacos://</c> 开头的 Nacos 地址。未填写时复用服务注册中心连接。
            /// EN: Dubbo metadata-center address. ZooKeeper and <c>nacos://</c> Nacos addresses are supported; when omitted, the service registry is reused.
            /// </summary>
            public string MetadataCenterAddress { get; set; } = null;
            /// <summary>
            /// 元数据中心位置；Nacos Config 默认 <c>dubbo</c>（独立于 Naming 分组）。ZooKeeper 默认沿用 ZooKeeper 注册根路径，混合中心时默认 <c>/dubbo</c>。
            /// EN: Metadata location; Nacos Config defaults to <c>dubbo</c>, independently of Naming. ZooKeeper inherits a ZooKeeper registry root, or uses <c>/dubbo</c> for mixed centers.
            /// </summary>
            public string MetadataRootPath { get; set; } = null;
            /// <summary>
            /// 单个 Provider 节点允许的最大连接数；仅对 <see cref="TripleDubboActuatorSuite"/> 和
            /// <see cref="TelnetDubboActuatorSuite"/> 生效。<see cref="NativeDubboActuatorSuite"/> 使用单条可并发复用的
            /// TCP 长连接，不使用此配置。
            /// EN: Maximum connections allowed for one provider endpoint. This setting applies only to
            /// <see cref="TripleDubboActuatorSuite"/> and <see cref="TelnetDubboActuatorSuite"/>.
            /// <see cref="NativeDubboActuatorSuite"/> multiplexes requests over one reusable TCP connection and ignores it.
            /// </summary>
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            /// <summary>
            /// Telnet 执行器辅助连接的空闲存活时间（秒，默认 300 秒）；0 表示不主动释放。
            /// 仅对 <see cref="TelnetDubboActuatorSuite"/> 生效。
            /// EN: Idle lifetime, in seconds, for auxiliary Telnet connections; the default is 300 seconds and zero
            /// disables proactive release. This setting applies only to <see cref="TelnetDubboActuatorSuite"/>.
            /// </summary>
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            /// <summary>
            /// Provider 主连接的空闲存活时间（秒，默认 1200 秒）；0 表示不主动释放。
            /// 对 <see cref="TelnetDubboActuatorSuite"/> 表示主 TCP 连接的空闲释放时间；对
            /// <see cref="TripleDubboActuatorSuite"/> 表示 HTTP 连接池的空闲连接超时；不影响
            /// <see cref="NativeDubboActuatorSuite"/>。
            /// EN: Idle lifetime, in seconds, for the provider's primary connection; the default is 1200 seconds and
            /// zero disables proactive release. It controls the primary TCP connection in <see cref="TelnetDubboActuatorSuite"/>
            /// and the pooled HTTP connection idle timeout in <see cref="TripleDubboActuatorSuite"/>; it does not affect
            /// <see cref="NativeDubboActuatorSuite"/>.
            /// </summary>
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            /// <summary>
            /// Dubbo 请求的最大超时时间（毫秒）；对客户端创建的 <see cref="NativeDubboActuatorSuite"/>、
            /// <see cref="TripleDubboActuatorSuite"/> 和 <see cref="TelnetDubboActuatorSuite"/> 均生效。
            /// EN: Maximum Dubbo request timeout in milliseconds. This setting applies to
            /// <see cref="NativeDubboActuatorSuite"/>, <see cref="TripleDubboActuatorSuite"/>, and
            /// <see cref="TelnetDubboActuatorSuite"/> instances created by the client.
            /// </summary>
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
            /// <summary>
            /// 单次 Telnet 元数据回退探测的超时时间（毫秒）。仅用于无法从元数据中心获得方法签名时的
            /// Telnet 元数据探测，不控制 <see cref="TelnetDubboActuatorSuite"/> 的业务请求超时。
            /// EN: Timeout in milliseconds for one Telnet metadata fallback probe. It is used only when method metadata
            /// cannot be obtained from the metadata center and does not control business requests sent by
            /// <see cref="TelnetDubboActuatorSuite"/>.
            /// </summary>
            public int TelnetMetadataTimeout { get; set; } = 10 * 1000;
            /// <summary>
            /// DubboClient可最大缓存的复用服务数量,0表示无限制（如果当前DubboClient实例会大量请求不同服务，可以扩大该数值）
            /// EN: Maximum number of reusable services cached by the client; zero means unlimited.
            /// </summary>
            public int MaintainServiceNum { get; set; } = 20;
            /// <summary>
            /// 当前负载模式
            /// EN: Load-balancing mode used for provider selection.
            /// </summary>
            public LoadBalanceMode NowLoadBalanceMode { get; set; } = LoadBalanceMode.Random;
            /// <summary>
            /// 未在调用入口中指定方法时使用的默认方法名。
            /// EN: Default method name used when an invocation endpoint omits it.
            /// </summary>
            public string DefaultFuncName { get; set; } = null;
            /// <summary>
            /// 未在调用入口中指定服务时使用的默认服务接口名。
            /// EN: Default service interface name used when an invocation endpoint omits it.
            /// </summary>
            public string DefaultServiceName { get; set; } = null;
        }

        private static MultiMyZookeeperStorage DubboClientMultiMyZookeeperStorage = new MultiMyZookeeperStorage();

        private static MultiMyNacosStorage DubboClientMultiMyNacosStorage = new MultiMyNacosStorage();

        private MyZookeeper _innerMyZookeeper;

        private MyNacos _innerMyNacos;

        private MyZookeeper _metadataMyZookeeper;

        private MyNacos _metadataMyNacos;

        private bool _metadataRegistryHasSeparateReference;

        private bool _registryUsesNacos;

        private bool _metadataUsesNacos;

        private DubboMetadataManager _metadataManager;

        private DubboClientZookeeperWatcher _dubboClientZookeeperWatcher;

        private DubboDriverCollection _dubboDriverCollection;

        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _retainDubboActuatorSuiteCollection = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();

        private ConcurrentDictionary<string, Task<DubboServiceEndPointInfos>> _concurrentGetProviderEndPointsTasks = new ConcurrentDictionary<string, Task<DubboServiceEndPointInfos>>();

        private volatile bool _isInReLoadDubboDriverCollectionTask = false;

        internal bool IsDisposed { get;private set;}= false;

        /// <summary>
        /// 注册中心的状态（内部状态，对调用方隐藏。实际上DubboClient网络状态是自动维护的，对外接口使用上是无状态的）
        /// EN: Internal registry state; public calls are connection-state agnostic because the client maintains reconnection automatically.
        /// </summary>
        internal RegistryState InnerRegistryState { get; set; } = RegistryState.Default;

        /// <summary>
        /// 获取只读的DubboActuatorSuiteCollection
        /// EN: Gets the read-only collection of shared actuator suites.
        /// </summary>
        internal ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> DubboActuatorSuiteCollection => new ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>(_retainDubboActuatorSuiteCollection);

        /// <summary>
        /// 注册数据根路径；ZooKeeper 中为节点路径，Nacos 中为 Naming 分组。
        /// EN: Effective registry root; a node path in ZooKeeper and a Naming group in Nacos.
        /// </summary>
        public string DubboRootPath { get; private set; } = "/dubbo/";

        /// <summary>
        /// 当前用于读取 Dubbo 元数据的注册中心地址；默认复用服务注册中心。
        /// EN: Effective metadata-center registry address; defaults to the service registry.
        /// </summary>
        public string MetadataCenterAddress { get; private set; }

        /// <summary>
        /// 元数据实际根位置；ZooKeeper 中为节点根路径，Nacos 中为 Config 分组。
        /// EN: Effective metadata root; a node root in ZooKeeper and a Config group in Nacos.
        /// </summary>
        public string MetadataRootPath { get; private set; }

        /// <summary>
        /// 默认当前Dubbo方法名称
        /// EN: Default Dubbo method name.
        /// </summary>
        public string DefaultFuncName { get; private set; } = null;

        /// <summary>
        /// 默认当前Dubbo服务名称
        /// EN: Default Dubbo service interface name.
        /// </summary>
        public string DefaultServiceName { get; private set; } = null;

        /// <summary>
        /// 获当前负载模式
        /// EN: Effective provider load-balancing mode.
        /// </summary>
        public LoadBalanceMode NowLoadBalanceMode { get; private set; } = LoadBalanceMode.Random;

        /// <summary>
        /// 当前对 Triple 和 Telnet Provider 生效的最大连接数；原生 Dubbo TCP 执行器不使用此值。
        /// EN: Effective maximum connection count for Triple and Telnet providers; the native Dubbo TCP actuator ignores it.
        /// </summary>
        public int DubboActuatorSuiteMaxConnections { get;private set; } = 20;

        /// <summary>
        /// 当前 Telnet 辅助连接的空闲存活时间（秒）；仅对 Telnet 执行器生效。
        /// EN: Effective auxiliary-connection idle lifetime in seconds; applies only to the Telnet actuator.
        /// </summary>
        public int DubboActuatorSuiteAssistConnectionAliveTime { get; private set; } = 60 * 5;

        /// <summary>
        /// 当前主连接空闲存活时间（秒）；对 Telnet 主连接和 Triple HTTP 连接池生效，原生 Dubbo TCP 执行器不使用此值。
        /// EN: Effective primary-connection idle lifetime in seconds for Telnet and the Triple HTTP connection pool;
        /// the native Dubbo TCP actuator ignores it.
        /// </summary>
        public int DubboActuatorSuiteMasterConnectionAliveTime { get; private set; } = 60 * 20;

        /// <summary>
        /// 当前业务请求最大超时时间（毫秒）；对 Native、Triple 和 Telnet 执行器均生效。
        /// EN: Effective business-request timeout in milliseconds for Native, Triple, and Telnet actuators.
        /// </summary>
        public int DubboRequestTimeout { get; private set; } = 60 * 1000;

        /// <summary>
        /// 当前单次 Telnet 元数据回退探测的超时时间（毫秒）；不影响 Telnet 业务请求。
        /// EN: Effective timeout in milliseconds for one Telnet metadata fallback probe; it does not affect Telnet business requests.
        /// </summary>
        public int TelnetMetadataTimeout { get; private set; } = 10 * 1000;

        /// <summary>
        /// DubboClient可最大缓存的复用服务数量,0表示无限制（如果当前DubboClient实例会大量请求不同服务，可以扩大该数值）
        /// EN: Effective maximum number of reusable services cached by this client; zero means unlimited.
        /// </summary>
        public int MaintainServiceNum { get;private set; } = 20;



        /// <summary>
        /// 初始化DubboClient
        /// EN: Initializes a Dubbo client with default options.
        /// </summary>
        /// <param name="registryCoonectString">注册中心地址。ZooKeeper 示例：<c>127.0.0.1:2181</c>，多个地址以逗号分隔，认证格式为 <c>[Scheme Auth]</c>；Nacos 示例：<c>nacos://127.0.0.1:8848</c>，以 <c>nacos://</c> 前缀选择 Nacos。EN: Registry address. ZooKeeper example: <c>127.0.0.1:2181</c>; separate multiple addresses with commas and append authentication as <c>[Scheme Auth]</c>. Nacos example: <c>nacos://127.0.0.1:8848</c>; the <c>nacos://</c> prefix selects Nacos.</param>
        /// <exception cref="ArgumentException">连接字符串为空。EN: The connection string is empty.</exception>
        public DubboClient(string registryCoonectString) : this(registryCoonectString,new DubboClientConf())
        {
        }

        /// <summary>
        /// 初始化DubboClient
        /// EN: Initializes a Dubbo client with explicit options.
        /// </summary>
        /// <param name="registryCoonectString">注册中心地址。ZooKeeper 使用 <c>host:2181</c>；Nacos 使用 <c>nacos://host:8848</c>，并可附加 <c>?namespace=...&amp;username=...&amp;password=...</c>。EN: Registry address. Use <c>host:2181</c> for ZooKeeper or <c>nacos://host:8848</c> for Nacos; Nacos also accepts <c>?namespace=...&amp;username=...&amp;password=...</c>.</param>
        /// <param name="dubboClientConf">客户端配置；传入 <see langword="null"/> 时使用默认配置。EN: Client options; defaults are used when <see langword="null"/> is supplied.</param>
        /// <exception cref="ArgumentException">连接字符串为空。EN: The connection string is empty.</exception>
        public DubboClient(string registryCoonectString , DubboClientConf dubboClientConf)
        {
            registryCoonectString = registryCoonectString?.Trim();
            if (string.IsNullOrEmpty(registryCoonectString))
            {
                throw new ArgumentException($"“{nameof(registryCoonectString)}” can not be null or empty", nameof(registryCoonectString));
            }
            if (dubboClientConf == null)
            {
                //throw new ArgumentNullException(nameof(dubboClientConf));
                dubboClientConf = new DubboClientConf();
            }
            MetadataCenterAddress = string.IsNullOrWhiteSpace(dubboClientConf.MetadataCenterAddress)
                ? registryCoonectString
                : dubboClientConf.MetadataCenterAddress.Trim();
            _registryUsesNacos = MyNacos.IsNacosConnectionString(registryCoonectString);
            _metadataUsesNacos = MyNacos.IsNacosConnectionString(MetadataCenterAddress);
            (DubboRootPath, MetadataRootPath) = ResolveRegistryPaths(
                _registryUsesNacos, _metadataUsesNacos, dubboClientConf);
            DefaultFuncName = dubboClientConf.DefaultFuncName;
            DefaultServiceName = dubboClientConf.DefaultServiceName;
            NowLoadBalanceMode = dubboClientConf.NowLoadBalanceMode;
            DubboActuatorSuiteMaxConnections = dubboClientConf.DubboActuatorSuiteMaxConnections;
            DubboActuatorSuiteAssistConnectionAliveTime = dubboClientConf.DubboActuatorSuiteAssistConnectionAliveTime;
            DubboActuatorSuiteMasterConnectionAliveTime = dubboClientConf.DubboActuatorSuiteMasterConnectionAliveTime;
            DubboRequestTimeout = dubboClientConf.DubboRequestTimeout;
            TelnetMetadataTimeout = dubboClientConf.TelnetMetadataTimeout > 0
                ? dubboClientConf.TelnetMetadataTimeout
                : 10 * 1000;
            MaintainServiceNum = dubboClientConf.MaintainServiceNum;

            if (_registryUsesNacos)
            {
                _innerMyNacos = DubboClientMultiMyNacosStorage.GetMyNacos(registryCoonectString);
            }
            else
            {
                _innerMyZookeeper = DubboClientMultiMyZookeeperStorage.GetMyZookeeper(registryCoonectString);
            }

            bool metadataReusesRegistry = string.Equals(
                NormalizeRegistryAddress(MetadataCenterAddress),
                NormalizeRegistryAddress(registryCoonectString),
                StringComparison.Ordinal);
            _metadataRegistryHasSeparateReference = !metadataReusesRegistry;
            if (_metadataUsesNacos)
            {
                _metadataMyNacos = metadataReusesRegistry && _registryUsesNacos
                    ? _innerMyNacos
                    : DubboClientMultiMyNacosStorage.GetMyNacos(MetadataCenterAddress);
                _metadataManager = new DubboMetadataManager(
                    _metadataMyNacos,
                    MetadataRootPath,
                    GetProviderEndPointsForMetadataAsync,
                    TelnetMetadataTimeout);
            }
            else
            {
                _metadataMyZookeeper = metadataReusesRegistry && !_registryUsesNacos
                    ? _innerMyZookeeper
                    : DubboClientMultiMyZookeeperStorage.GetMyZookeeper(MetadataCenterAddress);
                _metadataManager = new DubboMetadataManager(
                    _metadataMyZookeeper,
                    MetadataRootPath,
                    GetProviderEndPointsForMetadataAsync,
                    TelnetMetadataTimeout);
            }
            if (!_registryUsesNacos)
            {
                _dubboClientZookeeperWatcher = new DubboClientZookeeperWatcher(this);
            }
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
        /// EN: Initializes a Dubbo client and configures a default service and method endpoint.
        /// </summary>
        /// <param name="registryCoonectString">ZooKeeper 或 Nacos 注册中心地址；Nacos 地址必须以 <c>nacos://</c> 开头。EN: ZooKeeper or Nacos registry address; a Nacos address must start with <c>nacos://</c>.</param>
        /// <param name="funcEndPoint">默认调用入口，例如 <c>ServiceName.FuncName</c> 或 <c>ServiceName#FuncName</c>。EN: Default invocation endpoint, such as <c>ServiceName.FuncName</c> or <c>ServiceName#FuncName</c>.</param>
        /// <exception cref="ArgumentException">连接字符串或调用入口格式无效。EN: The connection string or endpoint format is invalid.</exception>
        public DubboClient(string registryCoonectString, string funcEndPoint ) : this(registryCoonectString)
        {
            if (funcEndPoint.Contains('#'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('#');
                DefaultFuncName = funcEndPoint.Substring(tempSpitIndex + 1);
                DefaultServiceName = funcEndPoint.Remove(tempSpitIndex);
            }
            else if (funcEndPoint.Contains('.'))
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


        #region QueryAsync
        /// <summary>
        /// 调用 Dubbo 方法；Java 参数类型从元数据中心或 Provider Telnet 自动推断。
        /// EN: Invokes a Dubbo method with Java parameter types inferred from the metadata center or Provider Telnet.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口，例如 <c>com.foo.DemoService.find</c>。EN: Service and method endpoint, for example <c>com.foo.DemoService.find</c>.</param>
        /// <returns>包含调用结果、耗时和错误信息的请求结果。EN: A request result containing the response, timing, and error information.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定方法重载。EN: Metadata is unavailable or a method overload cannot be selected uniquely.</exception>
        public Task<DubboRequestResult> QueryAsync(string funcEndPoint)
        {
            return QueryWithInferredTypesAsync(funcEndPoint, Array.Empty<object>());
        }

        /// <summary>
        /// 使用单个参数调用 Dubbo 方法；数组和集合始终被视为一个参数。
        /// EN: Invokes a Dubbo method with one argument; arrays and collections are always treated as one argument.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="argument">单个调用参数。EN: The single invocation argument.</param>
        /// <returns>包含调用结果、耗时和错误信息的请求结果。EN: A request result containing the response, timing, and error information.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定方法重载。EN: Metadata is unavailable or a method overload cannot be selected uniquely.</exception>
        public Task<DubboRequestResult> QueryAsync(
            string funcEndPoint,
            object argument)
        {
            return QueryWithInferredTypesAsync(
                funcEndPoint,
                new[] { argument });
        }

        /// <summary>
        /// 使用两个或更多参数调用 Dubbo 方法；额外参数通过 <c>params</c> 传入。
        /// EN: Invokes a Dubbo method with two or more arguments; additional arguments are supplied through <c>params</c>.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="firstArgument">第一个调用参数。EN: The first invocation argument.</param>
        /// <param name="secondArgument">第二个调用参数。EN: The second invocation argument.</param>
        /// <param name="additionalArguments">其余调用参数。EN: Remaining invocation arguments.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定方法重载。EN: Metadata is unavailable or a method overload cannot be selected uniquely.</exception>
        public Task<DubboRequestResult> QueryAsync(
            string funcEndPoint,
            object firstArgument,
            object secondArgument,
            params object[] additionalArguments)
        {
            return QueryWithInferredTypesAsync(
                funcEndPoint,
                CombineArguments(firstArgument, secondArgument, additionalArguments));
        }

        /// <summary>
        /// 调用 Dubbo 方法并将响应反序列化为指定类型；Java 参数类型自动推断。
        /// EN: Invokes a Dubbo method, deserializes the response, and infers Java parameter types automatically.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: The typed Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定方法重载。EN: Metadata is unavailable or a method overload cannot be selected uniquely.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(
            string funcEndPoint)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryWithInferredTypesAsync(
                    funcEndPoint,
                    Array.Empty<object>()).ConfigureAwait(false));
        }

        /// <summary>
        /// 使用单个参数调用 Dubbo 方法并将响应反序列化为指定类型；数组和集合保持为一个参数。
        /// EN: Invokes a Dubbo method with one argument and deserializes the response; arrays and collections remain one argument.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="argument">单个调用参数。EN: The single invocation argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定方法重载。EN: Metadata is unavailable or a method overload cannot be selected uniquely.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(
            string funcEndPoint,
            object argument)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryWithInferredTypesAsync(
                    funcEndPoint,
                    new[] { argument }).ConfigureAwait(false));
        }

        /// <summary>
        /// 使用两个或更多参数调用 Dubbo 方法并将响应反序列化为指定类型。
        /// EN: Invokes a Dubbo method with two or more arguments and deserializes the response to the specified type.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="firstArgument">第一个调用参数。EN: The first invocation argument.</param>
        /// <param name="secondArgument">第二个调用参数。EN: The second invocation argument.</param>
        /// <param name="additionalArguments">其余调用参数。EN: Remaining invocation arguments.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定方法重载。EN: Metadata is unavailable or a method overload cannot be selected uniquely.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(
            string funcEndPoint,
            object firstArgument,
            object secondArgument,
            params object[] additionalArguments)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryWithInferredTypesAsync(
                    funcEndPoint,
                    CombineArguments(
                        firstArgument,
                        secondArgument,
                        additionalArguments)).ConfigureAwait(false));
        }

        /// <summary>
        /// 使用明确的 Java 参数类型调用 Dubbo 方法；重载、<see langword="null"/>、集合和 POJO 参数建议使用此重载。
        /// EN: Invokes a Dubbo method with explicit Java parameter types; use this overload for overloads, <see langword="null"/>, collections, and POJOs.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="javaParameterTypes">按声明顺序排列的精确 Java 类型名。EN: Exact Java type names in declaration order.</param>
        /// <param name="arguments">与类型列表一一对应的参数列表。EN: Arguments corresponding one-to-one with the type list.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        /// <exception cref="ArgumentException">类型数量与参数数量不一致，或入口无效。EN: Type and argument counts differ, or the endpoint is invalid.</exception>
        public Task<DubboRequestResult> QueryAsync(
            string funcEndPoint,
            IReadOnlyList<string> javaParameterTypes,
            IReadOnlyList<object> arguments)
        {
            Tuple<string, string> endpoint = GetSeviceNameFormFuncEndPoint(funcEndPoint);
            return QueryAsync(new DubboInvocation(
                endpoint.Item1,
                endpoint.Item2,
                javaParameterTypes,
                arguments));
        }

        /// <summary>
        /// 使用明确的 Java 参数类型调用 Dubbo 方法并将响应反序列化为指定类型。
        /// EN: Invokes a Dubbo method with explicit Java parameter types and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="javaParameterTypes">按声明顺序排列的精确 Java 类型名。EN: Exact Java type names in declaration order.</param>
        /// <param name="arguments">与类型列表一一对应的参数列表。EN: Arguments corresponding one-to-one with the type list.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: The typed Dubbo request result.</returns>
        /// <exception cref="ArgumentException">类型数量与参数数量不一致，或入口无效。EN: Type and argument counts differ, or the endpoint is invalid.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(
            string funcEndPoint,
            IReadOnlyList<string> javaParameterTypes,
            IReadOnlyList<object> arguments)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryAsync(
                    funcEndPoint,
                    javaParameterTypes,
                    arguments).ConfigureAwait(false));
        }

        /// <summary>
        /// 根据 <see cref="DubboInvocation"/> 调用 Dubbo 方法；参数类型为空时自动推断，并保留版本、分组和 attachments。
        /// EN: Invokes a Dubbo method from a <see cref="DubboInvocation"/>; empty parameter types are inferred while version, group, and attachments are preserved.
        /// </summary>
        /// <param name="invocation">完整调用描述。EN: The complete invocation descriptor.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="invocation"/> 为空。EN: <paramref name="invocation"/> is null.</exception>
        /// <exception cref="DubboMetadataException">需要自动推断但元数据不可用或存在歧义。EN: Inference is required but metadata is unavailable or ambiguous.</exception>
        public async Task<DubboRequestResult> QueryAsync(DubboInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            DubboInvocation effectiveInvocation = invocation;
            if (invocation.ParameterTypes == null || invocation.ParameterTypes.Count == 0)
            {
                effectiveInvocation = await CreateMetadataInvocationAsync(
                    invocation.Service,
                    invocation.Method,
                    invocation.Arguments ?? Array.Empty<object>(),
                    invocation).ConfigureAwait(false);
            }
            effectiveInvocation.Validate();
            return await QueryInternalAsync(
                $"{invocation.Service}#{invocation.Method}",
                JsonSerializer.Serialize(effectiveInvocation.Arguments),
                effectiveInvocation).ConfigureAwait(false);
        }

        /// <summary>
        /// 根据 <see cref="DubboInvocation"/> 调用 Dubbo 方法并将响应反序列化为指定类型。
        /// EN: Invokes a Dubbo method from a <see cref="DubboInvocation"/> and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="invocation">调用描述。EN: Invocation descriptor.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="invocation"/> 为空。EN: <paramref name="invocation"/> is null.</exception>
        /// <exception cref="DubboMetadataException">需要自动推断但元数据不可用或不明确。EN: Inference is required but metadata is unavailable or ambiguous.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(
            DubboInvocation invocation)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryAsync(invocation).ConfigureAwait(false));
        }

        /// <summary>
        /// 解析方法元数据并以结构化调用描述发送请求。
        /// EN: Resolves method metadata and sends the request with a structured invocation descriptor.
        /// </summary>
        private async Task<DubboRequestResult> QueryWithInferredTypesAsync(
            string funcEndPoint,
            IReadOnlyList<object> arguments)
        {
            Tuple<string, string> endpoint = GetSeviceNameFormFuncEndPoint(funcEndPoint);
            DubboInvocation invocation = await CreateMetadataInvocationAsync(
                endpoint.Item1,
                endpoint.Item2,
                arguments ?? Array.Empty<object>()).ConfigureAwait(false);
            return await QueryInternalAsync(
                funcEndPoint,
                JsonSerializer.Serialize(invocation.Arguments),
                invocation).ConfigureAwait(false);
        }

        /// <summary>
        /// 合并“两个及以上参数”重载收到的实参，同时避免单个数组被 <c>params</c> 展开。
        /// EN: Combines arguments received by the two-or-more overload while preventing a single array from being expanded by <c>params</c>.
        /// </summary>
        private static IReadOnlyList<object> CombineArguments(
            object firstArgument,
            object secondArgument,
            object[] additionalArguments)
        {
            int additionalCount = additionalArguments?.Length ?? 0;
            object[] arguments = new object[additionalCount + 2];
            arguments[0] = firstArgument;
            arguments[1] = secondArgument;
            if (additionalCount > 0)
            {
                Array.Copy(additionalArguments, 0, arguments, 2, additionalCount);
            }
            return arguments;
        }

        /// <summary>
        /// 从元数据中心或 Telnet 回退获取指定方法的全部重载；结果按服务缓存，元数据中心节点变化时自动刷新。
        /// EN: Gets all overloads of a method from the metadata center or Telnet fallback; results are cached per service and refreshed when watched metadata changes.
        /// </summary>
        /// <param name="serviceName">完整 Java 服务接口名。EN: Fully qualified Java service interface name.</param>
        /// <param name="methodName">Java 方法名。EN: Java method name.</param>
        /// <returns>方法的全部已发现签名；方法不存在时为空集合。EN: All discovered signatures for the method, or an empty collection when the method is absent.</returns>
        /// <exception cref="DubboMetadataException">两个元数据来源均不可用。EN: Neither metadata source is available.</exception>
        public async Task<IReadOnlyList<DubboMethodMetadata>> GetMethodMetadataAsync(
            string serviceName,
            string methodName)
        {
            ThrowIfDisposed();
            IReadOnlyList<DubboMethodMetadata> methods =
                await _metadataManager.GetMethodMetadataAsync(serviceName, methodName)
                    .ConfigureAwait(false);
            return methods.Select(method => method.CloneForCaller()).ToArray();
        }

        /// <summary>
        /// 使用完整入口获取指定方法的全部重载，例如 <c>com.foo.DemoService.find</c> 或 <c>com.foo.DemoService#find</c>。
        /// EN: Gets all overloads of a method from a full endpoint such as <c>com.foo.DemoService.find</c> or <c>com.foo.DemoService#find</c>.
        /// </summary>
        /// <param name="funcEndPoint">包含服务和方法名的完整入口。EN: Full endpoint containing the service and method names.</param>
        /// <returns>方法的全部已发现签名。EN: All discovered signatures for the method.</returns>
        /// <exception cref="ArgumentException">入口格式无效。EN: The endpoint format is invalid.</exception>
        /// <exception cref="DubboMetadataException">两个元数据来源均不可用。EN: Neither metadata source is available.</exception>
        public async Task<IReadOnlyList<DubboMethodMetadata>> GetMethodMetadataAsync(
            string funcEndPoint)
        {
            ThrowIfDisposed();
            Tuple<string, string> endpoint = GetSeviceNameFormFuncEndPoint(funcEndPoint);
            IReadOnlyList<DubboMethodMetadata> methods =
                await _metadataManager.GetMethodMetadataAsync(endpoint.Item1, endpoint.Item2)
                    .ConfigureAwait(false);
            return methods.Select(method => method.CloneForCaller()).ToArray();
        }

        /// <summary>
        /// 从元数据中心或 Telnet 回退获取服务的全部方法签名。
        /// EN: Gets all method signatures for a service from the metadata center or Telnet fallback.
        /// </summary>
        /// <param name="serviceName">完整 Java 服务接口名。EN: Fully qualified Java service interface name.</param>
        /// <returns>服务的全部已发现方法签名。EN: All discovered method signatures for the service.</returns>
        /// <exception cref="DubboMetadataException">两个元数据来源均不可用。EN: Neither metadata source is available.</exception>
        public async Task<IReadOnlyList<DubboMethodMetadata>> GetServiceMethodsMetadataAsync(
            string serviceName)
        {
            ThrowIfDisposed();
            IReadOnlyList<DubboMethodMetadata> methods =
                await _metadataManager.GetServiceMethodsAsync(serviceName).ConfigureAwait(false);
            return methods.Select(method => method.CloneForCaller()).ToArray();
        }

        /// <summary>
        /// 从元数据中心获取指定服务的 <c>FullServiceDefinition.types</c>，用于构造 POJO、集合及嵌套请求参数。
        /// <para>EN: Gets <c>FullServiceDefinition.types</c> from the metadata center for constructing POJO, collection, and nested request arguments.</para>
        /// </summary>
        /// <param name="serviceName">完整 Java 服务接口名。EN: Fully qualified Java service interface name.</param>
        /// <returns>按类型标识去重后的递归类型定义；元数据文档存在但未声明类型时为空集合。EN: Recursive type definitions deduplicated by identifier, or an empty collection when the document declares no types.</returns>
        /// <exception cref="ArgumentException">服务名为空。EN: The service name is empty.</exception>
        /// <exception cref="DubboMetadataException">元数据中心路径不存在、不可访问或没有 FullServiceDefinition；Telnet 无法提供 types。EN: The metadata-center path is missing, unavailable, or has no FullServiceDefinition; Telnet cannot provide types.</exception>
        public async Task<IReadOnlyList<DubboTypeMetadata>> GetServiceTypesMetadataAsync(
            string serviceName)
        {
            ThrowIfDisposed();
            IReadOnlyList<DubboTypeMetadata> types =
                await _metadataManager.GetServiceTypesAsync(serviceName).ConfigureAwait(false);
            return types.Select(type => type.CloneForCaller()).ToArray();
        }

        private async Task<DubboInvocation> CreateMetadataInvocationAsync(
            string serviceName,
            string methodName,
            IReadOnlyList<object> arguments,
            DubboInvocation source = null)
        {
            if (string.IsNullOrWhiteSpace(serviceName)
                || string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException("Can not find the Dubbo service or method name.");
            }

            IReadOnlyList<DubboMethodMetadata> methods;
            try
            {
                methods = await _metadataManager.GetServiceMethodsAsync(serviceName)
                    .ConfigureAwait(false);
            }
            catch (DubboMetadataException) when (arguments.Count == 0)
            {
                // A no-argument invocation has one exact parameter signature: the empty array.
                // Keep the missing-node watch registered by the metadata manager, but do not
                // require metadata merely to send a signature that cannot be ambiguous by type.
                return new DubboInvocation(
                    serviceName,
                    methodName,
                    Array.Empty<string>(),
                    arguments)
                {
                    Version = source?.Version,
                    Group = source?.Group,
                    Attachments = source?.Attachments
                };
            }
            catch (DubboMetadataException exception)
            {
                throw new DubboMetadataException(
                    $"{exception.Message} Requested endpoint: '{serviceName}.{methodName}'. " +
                    $"Use QueryAsync(\"{serviceName}.{methodName}\", " +
                    "new[] { \"<exact-java-type>\" }, new object[] { argument }) to retry explicitly.",
                    exception);
            }
            DubboMethodMetadata resolved;
            try
            {
                resolved = DubboMethodMetadataResolver.Resolve(
                    serviceName,
                    methodName,
                    methods,
                    arguments);
            }
            catch (DubboMetadataException centerResolutionException)
                when (methods.Any(method =>
                    method.MetadataSource == DubboMetadataSource.MetadataCenter))
            {
                // A metadata center can be only partially populated during rolling publication or
                // when several provider applications expose the same interface. Supplement an
                // unresolved center result with the same one-time, service-level Telnet probe.
                DubboTelnetMetadataResult telnet =
                    await _metadataManager.GetTelnetServiceMetadataAsync(serviceName)
                        .ConfigureAwait(false);
                if (telnet.Methods.Count == 0)
                {
                    throw new DubboMetadataException(
                        $"{centerResolutionException.Message} The metadata-center definition " +
                        $"could not resolve this invocation, and Telnet fallback could not " +
                        $"supplement it: {telnet.FailureReason}. " +
                        $"Use QueryAsync(\"{serviceName}.{methodName}\", " +
                        "new[] { \"<exact-java-type>\" }, new object[] { argument }) to retry explicitly.",
                        centerResolutionException);
                }

                IReadOnlyList<DubboMethodMetadata> combined = methods
                    .Concat(telnet.Methods)
                    .GroupBy(DubboMethodMetadataResolver.SignatureKey, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                resolved = DubboMethodMetadataResolver.Resolve(
                    serviceName,
                    methodName,
                    combined,
                    arguments);
            }

            return new DubboInvocation(
                serviceName,
                methodName,
                resolved.ParameterTypes,
                arguments)
            {
                Version = source?.Version,
                Group = source?.Group,
                Attachments = source?.Attachments
            };
        }

        private async Task<DubboRequestResult> QueryInternalAsync(
            string funcEndPoint,
            string req,
            DubboInvocation invocation)
        {
            if (IsDisposed)
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
            DubboActuatorProtocolType? requiredProtocol =
                await GetRequiredProtocolAsync(
                    nowServiceName,
                    nowFuncName,
                    invocation).ConfigureAwait(false);
            AvailableDubboActuatorInfo availableDubboActuatorInfo =
                _dubboDriverCollection.GetDubboActuatorSuite(
                    nowServiceName,
                    NowLoadBalanceMode,
                    req,
                    requiredProtocol);
            //获取到可用的DubboActuatorSuite
            if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite)
            {
                if (availableDubboActuatorInfo.AvailableDubboActuatorSuite
                    is IDubboGenericInvocationActuator genericInvocationSuite)
                {
                    return await genericInvocationSuite.SendQuery(invocation)
                        .ConfigureAwait(false);
                }

                return await availableDubboActuatorInfo.AvailableDubboActuatorSuite.SendQuery(
                    $"{nowServiceName}" +
                    $"{availableDubboActuatorInfo.AvailableDubboActuatorSuite.ServiceFuncSpit}" +
                    nowFuncName,
                    SerializeLegacyArguments(
                        invocation.Arguments,
                        availableDubboActuatorInfo.AvailableDubboActuatorSuite.ProtocolType))
                    .ConfigureAwait(false);
            }
            //没有_dubboDriverCollection没有目标服务，尝试添加服务节点 （新的ServiceName都会通过这里将节点添加进来）
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoDubboServiceDriver)
            {
                DubboServiceEndPointInfos serviceEndPointsInfo = await ConcurrentGetProviderEndPoints(nowServiceName);
                if (serviceEndPointsInfo.ErrorInfo != null)
                {
                    string tempErrorMes = $"[SendRequestAsync] GetSeviceProviderEndPoints fail {nowServiceName} -> {serviceEndPointsInfo.ErrorInfo}";
                    MyLogger.LogWarning(tempErrorMes);
                    return new DubboRequestResult()
                    {
                        QuerySuccess = false,
                        ServiceElapsed = -1,
                        ErrorMeaasge = tempErrorMes
                    };
                }
                else
                {
                    _dubboDriverCollection.AddDubboServiceDriver(nowServiceName, serviceEndPointsInfo.EndPoints);
                    return await QueryInternalAsync(funcEndPoint, req, invocation);
                }
            }
            //没有获取到可用的DubboActuatorSuite，因为服务里的节点信息为空
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoActuatorInService)
            {
                //实际上是有watch会自动更新，这里主动更新一次可以兼容watch异常的情况（这里会触发同一个路径注册2个watch，不过重复的watch只会触发一次）
                DubboServiceEndPointInfos serviceEndPointsInfo = await ConcurrentGetProviderEndPoints(nowServiceName);
                string tempErrorMes = null;
                if (serviceEndPointsInfo.ErrorInfo != null)
                {
                    tempErrorMes = $"[SendRequestAsync] GetSeviceProviderEndPoints fail {nowServiceName} -> {serviceEndPointsInfo.ErrorInfo}";
                }
                else if (serviceEndPointsInfo.EndPoints.Count > 0)
                {
                    _dubboDriverCollection.AddDubboServiceDriver(nowServiceName, serviceEndPointsInfo.EndPoints);
                    return await QueryInternalAsync(funcEndPoint, req, invocation);
                }
                else
                {
                    tempErrorMes = $"[SendRequestAsync] fail {nowServiceName} do not has any Provider";
                }
                MyLogger.LogWarning(tempErrorMes);
                return new DubboRequestResult()
                {
                    QuerySuccess = false,
                    ServiceElapsed = -1,
                    ErrorMeaasge = tempErrorMes
                };
            }
            //没有获取到可用的DubboActuatorSuite，因为没有可用的DubboActuatorSuite（比如配置的资源耗尽）
            else if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoAvailableActuator)
            {
                if (requiredProtocol == DubboActuatorProtocolType.NativeDubbo)
                {
                    return new DubboRequestResult()
                    {
                        QuerySuccess = false,
                        ServiceElapsed = -1,
                        ErrorMeaasge =
                            $"[SendRequestAsync] overloaded method {nowServiceName}.{nowFuncName} " +
                            "requires a dubbo:// provider. Dubbo 3.3 Triple HTTP/JSON cannot " +
                            "reliably distinguish every Java overload; register a native Dubbo " +
                            "provider or expose distinct REST mappings."
                    };
                }
                return new DubboRequestResult()
                {
                    QuerySuccess = false,
                    ServiceElapsed = -1,
                    ErrorMeaasge = $"[SendRequestAsync] fail NoAvailableActuator in {nowServiceName}"
                };
            }
            else
            {
                return new DubboRequestResult()
                {
                    QuerySuccess = false,
                    ServiceElapsed = -1,
                    ErrorMeaasge = "Unkonw error"
                };
            }
        }

        private async Task<DubboActuatorProtocolType?> GetRequiredProtocolAsync(
            string serviceName,
            string methodName,
            DubboInvocation invocation)
        {
            try
            {
                IReadOnlyList<DubboMethodMetadata> methods =
                    await _metadataManager.GetMethodMetadataAsync(serviceName, methodName)
                        .ConfigureAwait(false);
                if (methods.Count > 1)
                {
                    return DubboActuatorProtocolType.NativeDubbo;
                }
            }
            catch (DubboMetadataException exception)
            {
                // Explicit invocations are intentionally allowed when metadata is unavailable.
                // Keep the historical selection behavior rather than turning a best-effort
                // compatibility check into a new hard dependency on metadata discovery.
                MyLogger.LogWarning(
                    $"[GetRequiredProtocolAsync] unable to inspect overload metadata for " +
                    $"{serviceName}.{methodName}: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// 按旧 HTTP 或 Telnet 执行器要求的格式序列化结构化参数。
        /// EN: Serializes structured arguments in the format expected by legacy HTTP or Telnet actuators.
        /// </summary>
        private static string SerializeLegacyArguments(
            IReadOnlyList<object> arguments,
            DubboActuatorProtocolType protocolType)
        {
            if (arguments == null || arguments.Count == 0)
            {
                return string.Empty;
            }

            if (protocolType == DubboActuatorProtocolType.Http)
            {
                return arguments.Count == 1
                    ? JsonSerializer.Serialize(arguments[0])
                    : JsonSerializer.Serialize(arguments);
            }

            return string.Join(",", arguments.Select(argument =>
                JsonSerializer.Serialize(argument)));
        }

        #endregion


        /// <summary>
        /// 是否已经存在ServiceDriver（内部调用，不用暴露）
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        internal bool HasServiceDriver(string serviceName)
        {
            return _dubboDriverCollection.HasServiceDriver(serviceName);
        }

        internal bool HasCachedMetadata(string serviceName)
        {
            return _metadataManager?.HasCachedService(serviceName) == true;
        }

        internal Task RefreshCachedMetadataAsync(string serviceName)
        {
            return _metadataManager?.RefreshCachedServiceAsync(serviceName)
                ?? Task.CompletedTask;
        }

        /// <summary>
        /// 尝试重新连接注册中心(用于注册中心断开后，开启一个重连任务)
        /// 实际上ZooKeeper客户端会自动进行反复的重连，直到最终成功连接上ZooKeeper集群中的一台机器。再次连接上服务端的客户端有可能会处于以下两种状态之一。
        /// CONNECTED：如果在会话超时时间内重新连接上了ZooKeeper集群中任意一台机器，那么被视为重连成功，EXPIRED：如果是在会话超时时间以外重新连接上，那么服务端其实已经对该会话进行了会话清理操作，因此再次连接上的会话将被视为非法会话。
        /// 大部分情况都将是EXPIRED（同一个watch即使注册了很多path，也指挥收到一次）这个时候ZooKeeper是要重新构建才能正常使用的
        /// EN: Attempts active registry reconnection and rebuilds the session when ZooKeeper reports it as expired.
        /// </summary>
        /// <param name="delayTime">连接失败后重试间隔</param>
        /// <param name="timeOut">尝试连接多久</param>
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
        /// EN: Reloads provider drivers after registry recovery and reinstalls provider watches.
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
        /// EN: Refreshes provider endpoints for a service.
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        internal async Task<bool> ReflushProviderAsync(string serviceName ,bool isFullPath = false)
        {
            DubboServiceEndPointInfos serviceEndPointsInfo = await ConcurrentGetProviderEndPoints(serviceName,isFullPath);
            if (serviceEndPointsInfo.ErrorInfo != null)
            {
                string unavailableServiceName = serviceName;
                if (isFullPath
                    && TryGetServiceNameFromProviderPath(serviceName, out string parsedServiceName))
                {
                    unavailableServiceName = parsedServiceName;
                }
                if (_dubboDriverCollection.HasServiceDriver(unavailableServiceName)
                    && IsNoProviderError(serviceEndPointsInfo.ErrorInfo))
                {
                    bool changed = _dubboDriverCollection.AddDubboServiceDriver(
                        unavailableServiceName,
                        new List<DubboServiceEndPointInfo>());
                    await _metadataManager.RefreshCachedServiceAsync(unavailableServiceName)
                        .ConfigureAwait(false);
                    return changed;
                }
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
                bool changed = _dubboDriverCollection.AddDubboServiceDriver(
                    nowServiceName,
                    serviceEndPointsInfo.EndPoints);
                await _metadataManager.RefreshCachedServiceAsync(nowServiceName).ConfigureAwait(false);
                return changed;
            }
        }

        private static bool IsNoProviderError(string errorInfo) =>
            !string.IsNullOrWhiteSpace(errorInfo)
            && (errorInfo.Contains("no provider", StringComparison.OrdinalIgnoreCase)
                || errorInfo.Contains("no healthy provider", StringComparison.OrdinalIgnoreCase));

        internal bool TryGetServiceNameFromProviderPath(
            string providerPath,
            out string serviceName)
        {
            serviceName = null;
            if (string.IsNullOrEmpty(providerPath)
                || !providerPath.StartsWith(DubboRootPath, StringComparison.Ordinal)
                || !providerPath.EndsWith("/providers", StringComparison.Ordinal))
            {
                return false;
            }

            serviceName = providerPath.Substring(
                DubboRootPath.Length,
                providerPath.Length - DubboRootPath.Length - "/providers".Length);
            return !string.IsNullOrWhiteSpace(serviceName);
        }

        /// <summary>
        /// 对GetSeviceProviderEndPointsAsync重复并发的封装（用于应对短时间并行同时对同一个serviceName节点信息进行获取，同时重复获会耗费不必要的性能，这里会复用可复用的Task完成获取任务）
        /// EN: Coalesces concurrent provider-discovery calls for the same service into one reusable task.
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        private async Task<DubboServiceEndPointInfos> ConcurrentGetProviderEndPoints(string serviceName, bool isFullPath = false)
        {
            Task<DubboServiceEndPointInfos> getProviderEndPointsTask =
                _concurrentGetProviderEndPointsTasks.GetOrAdd(
                    serviceName,
                    _ => GetSeviceProviderEndPointsAsync(serviceName, isFullPath));
            try
            {
                return await getProviderEndPointsTask.ConfigureAwait(false);
            }
            finally
            {
                _concurrentGetProviderEndPointsTasks.TryRemove(
                    new KeyValuePair<string, Task<DubboServiceEndPointInfos>>(
                        serviceName,
                        getProviderEndPointsTask));
            }
        }

        private async Task<IReadOnlyList<DubboServiceEndPointInfo>>
            GetProviderEndPointsForMetadataAsync(string serviceName)
        {
            DubboServiceEndPointInfos result =
                await ConcurrentGetProviderEndPoints(serviceName).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(result.ErrorInfo))
            {
                throw new DubboMetadataException(
                    $"Can not discover providers for '{serviceName}': {result.ErrorInfo}");
            }
            return result.EndPoints;
        }

        /// <summary>
        /// 根据服务名，在注册中心查找服务节点信息(如果服务存在会默认注册_dubboClientZookeeperWatcher，以达到自动更新的目的)
        /// EN: Discovers provider endpoints in the registry and installs the provider watcher when the service exists.
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="isFullPath"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private async Task<DubboServiceEndPointInfos> GetSeviceProviderEndPointsAsync(string serviceName ,bool isFullPath = false)
        {
            if(string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            if (_registryUsesNacos)
            {
                DubboServiceEndPointInfos nacosResult = new DubboServiceEndPointInfos();
                if (isFullPath)
                {
                    nacosResult.ErrorInfo =
                        $"Nacos discovery expects a Dubbo interface name instead of a ZooKeeper path: {serviceName}";
                    return nacosResult;
                }
                try
                {
                    IReadOnlyList<DubboServiceEndPointInfo> endpoints =
                        await _innerMyNacos.GetProviderEndPointsAsync(
                            serviceName,
                            DubboRootPath,
                            this,
                            HandleNacosProviderChangedAsync).ConfigureAwait(false);
                    nacosResult.EndPoints.AddRange(endpoints);
                    if (nacosResult.EndPoints.Count == 0)
                    {
                        nacosResult.ErrorInfo =
                            $"no healthy provider endpoint for [{serviceName}] in Nacos group " +
                            $"[{MyNacos.NormalizeGroup(DubboRootPath)}]";
                    }
                }
                catch (Exception exception)
                {
                    nacosResult.ErrorInfo =
                        $"can not get [{serviceName}] from Nacos: {exception.Message}";
                }
                return nacosResult;
            }
            string nowFullPath = isFullPath? serviceName: $"{DubboRootPath}{serviceName}/providers";
            DubboServiceEndPointInfos serviceEndPointsInfo = new DubboServiceEndPointInfos();
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
                else
                {
                    foreach(var child in childrenResult.Children)
                    {
                        if (child.StartsWith("dubbo%3A%2F%2F") || child.StartsWith("tri%3A%2F%2F"))
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

        private async Task HandleNacosProviderChangedAsync(string serviceName)
        {
            if (IsDisposed || string.IsNullOrWhiteSpace(serviceName))
            {
                return;
            }
            if (HasServiceDriver(serviceName))
            {
                await ReflushProviderAsync(serviceName).ConfigureAwait(false);
            }
            else if (HasCachedMetadata(serviceName))
            {
                await RefreshCachedMetadataAsync(serviceName).ConfigureAwait(false);
            }
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(DubboClient));
            }
        }

        private static string NormalizeDubboRootPath(string rootPath)
        {
            string normalized = string.IsNullOrWhiteSpace(rootPath) ? "/dubbo" : rootPath.Trim();
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }
            return normalized.TrimEnd('/') + "/";
        }

        private static string NormalizeMetadataRootPath(string rootPath)
        {
            string normalized = string.IsNullOrWhiteSpace(rootPath) ? "/dubbo" : rootPath.Trim();
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }
            return normalized.TrimEnd('/');
        }

        internal static (string RegistryRoot, string MetadataRoot) ResolveRegistryPaths(
            bool registryUsesNacos, bool metadataUsesNacos, DubboClientConf configuration)
        {
            string registryRoot = registryUsesNacos
                ? MyNacos.NormalizeGroup(configuration.DubboRootPath, "DEFAULT_GROUP")
                : NormalizeDubboRootPath(configuration.DubboRootPath);
            string metadataRoot = metadataUsesNacos
                ? MyNacos.NormalizeGroup(configuration.MetadataRootPath, "dubbo")
                : string.IsNullOrWhiteSpace(configuration.MetadataRootPath)
                    ? (registryUsesNacos ? "/dubbo" : registryRoot.TrimEnd('/'))
                    : NormalizeMetadataRootPath(configuration.MetadataRootPath);
            return (registryRoot, metadataRoot);
        }

        private static string NormalizeRegistryAddress(string registryAddress)
        {
            if (MyNacos.IsNacosConnectionString(registryAddress))
            {
                return MyNacos.NormalizeConnectionString(registryAddress);
            }
            return registryAddress?.Trim();
        }

        /// <summary>
        /// 根据funcEndPoint获取服务名称与方法名称（用于参数解析，如果需要使用默认名称返回）
        /// 支持的格式有 seviceName#FuncName seviceName/FuncName seviceName.FuncName 
        /// EN: Splits an endpoint into service and method names, accepting <c>#</c>, <c>/</c>, or the final <c>.</c> as the separator.
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
            else if (funcEndPoint.Contains('/'))
            {
                int tempSpitIndex = funcEndPoint.LastIndexOf('/');
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
                _metadataManager?.Dispose();
                _metadataManager = null;
                _innerMyNacos?.RemoveSubscriber(this);
                if (_metadataRegistryHasSeparateReference)
                {
                    if (_metadataMyNacos != null)
                    {
                        DubboClientMultiMyNacosStorage.RemoveMyNacos(_metadataMyNacos);
                    }
                    if (_metadataMyZookeeper != null)
                    {
                        DubboClientMultiMyZookeeperStorage.RemoveMyZookeeper(
                            _metadataMyZookeeper);
                    }
                }
                _metadataMyNacos = null;
                _metadataMyZookeeper = null;
                if (_innerMyNacos != null)
                {
                    DubboClientMultiMyNacosStorage.RemoveMyNacos(_innerMyNacos);
                }
                if (_innerMyZookeeper != null)
                {
                    DubboClientMultiMyZookeeperStorage.RemoveMyZookeeper(_innerMyZookeeper);
                }
                _innerMyNacos = null;
                _innerMyZookeeper = null;
                _dubboClientZookeeperWatcher = null;
                _dubboDriverCollection?.Dispose();
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

        /// <summary>
        /// 释放客户端持有的注册中心订阅、连接驱动和执行器资源。
        /// EN: Releases registry subscriptions, connection drivers, and actuator resources owned by the client.
        /// </summary>
        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
