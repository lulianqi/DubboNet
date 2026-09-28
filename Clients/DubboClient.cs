using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using DubboNet.DubboService.Native;
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
            /// Zookeeper上默认的Dubbo跟路径，默认/dubbo/
            /// EN: Root path of Dubbo data in ZooKeeper; defaults to <c>/dubbo/</c>.
            /// </summary>
            public string DubboRootPath { get; set; } = "/dubbo/";
            /// <summary>
            /// Dubbo 元数据中心的 ZooKeeper 连接字符串；未填写时复用服务注册中心连接，不创建额外连接。
            /// EN: ZooKeeper connection string for the metadata center; when omitted, the service registry connection is reused.
            /// </summary>
            public string MetadataCenterAddress { get; set; } = null;
            /// <summary>
            /// 元数据中心的根路径（metadata-report 分组）；未填写时使用 <see cref="DubboRootPath"/>，Dubbo 2.7 默认使用 <c>/dubbo</c>。
            /// EN: Metadata-report root path; defaults to <see cref="DubboRootPath"/> (normally <c>/dubbo</c>).
            /// </summary>
            public string MetadataRootPath { get; set; } = null;
            /// <summary>
            /// 单个Dubbo服务节点最多能开启的连接数量（默认情况会保持1个连接，当对单个节点出现并行请求时会自动开启更多连接）
            /// EN: Maximum connections opened for one provider endpoint; extra connections are created for concurrent requests.
            /// </summary>
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            /// <summary>
            /// 服务节点辅助连接不再活跃超过此时间时释放辅助连接（单位秒,默认300s，0表示不进行主动释放）
            /// EN: Idle lifetime in seconds for auxiliary connections; zero disables proactive release.
            /// </summary>
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            /// <summary>
            /// 服务节点主连接不再活跃超过此时间时释放辅助连接（单位秒,默认1200s，0表示不进行主动释放）
            /// EN: Idle lifetime in seconds for the primary connection; zero disables proactive release.
            /// </summary>
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            /// <summary>
            /// Dubbo请求的最大超时时间
            /// EN: Maximum Dubbo request timeout in milliseconds.
            /// </summary>
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
            /// <summary>
            /// 单次 Telnet 元数据探测的超时时间（毫秒）。
            /// EN: Timeout in milliseconds for the one-time Telnet metadata probe.
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

        private MyZookeeper _innerMyZookeeper;

        private MyZookeeper _metadataMyZookeeper;

        private bool _metadataZookeeperHasSeparateReference;

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
        /// Zookeeper上默认的Dubbo跟路径，默认/dubbo/
        /// EN: Effective root path of Dubbo registry data in ZooKeeper.
        /// </summary>
        public string DubboRootPath { get; private set; } = "/dubbo/";

        /// <summary>
        /// 当前用于读取 Dubbo 元数据的 ZooKeeper 连接字符串；默认复用服务注册中心。
        /// EN: Effective metadata-center ZooKeeper connection string; defaults to the service registry.
        /// </summary>
        public string MetadataCenterAddress { get; private set; }

        /// <summary>
        /// 当前用于读取 Dubbo 元数据文档的根路径。
        /// EN: Effective root path used to read Dubbo metadata documents.
        /// </summary>
        public string MetadataRootPath { get; private set; } = "/dubbo";

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
        /// 单个Dubbo服务节点最多能开启的连接数量（默认情况会保持1个连接，当对单个节点出现并行请求时会自动开启更多连接）
        /// EN: Effective maximum connection count for one provider endpoint.
        /// </summary>
        public int DubboActuatorSuiteMaxConnections { get;private set; } = 20;

        /// <summary>
        /// 服务节点辅助连接不再活跃超过此时间时释放辅助连接（单位秒,默认300s，0表示不进行主动释放）
        /// EN: Effective auxiliary-connection idle lifetime in seconds.
        /// </summary>
        public int DubboActuatorSuiteAssistConnectionAliveTime { get; private set; } = 60 * 5;

        /// <summary>
        /// 服务节点主连接不再活跃超过此时间时释放辅助连接（单位秒,默认1200s，0表示不进行主动释放）
        /// EN: Effective primary-connection idle lifetime in seconds.
        /// </summary>
        public int DubboActuatorSuiteMasterConnectionAliveTime { get; private set; } = 60 * 20;

        /// <summary>
        /// Dubbo请求的最大超时时间
        /// EN: Effective Dubbo request timeout in milliseconds.
        /// </summary>
        public int DubboRequestTimeout { get; private set; } = 60 * 1000;

        /// <summary>
        /// 当前单次 Telnet 元数据探测的超时时间（毫秒）。
        /// EN: Effective timeout in milliseconds for the one-time Telnet metadata probe.
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
        /// <param name="zookeeperCoonectString">ZooKeeper 连接字符串；多个地址以逗号分隔，认证格式为 <c>[Scheme Auth]</c>。EN: ZooKeeper connection string; separate multiple addresses with commas and append authentication as <c>[Scheme Auth]</c>.</param>
        /// <exception cref="ArgumentException">连接字符串为空。EN: The connection string is empty.</exception>
        public DubboClient(string zookeeperCoonectString) : this(zookeeperCoonectString,new DubboClientConf())
        {
        }

        /// <summary>
        /// 初始化DubboClient
        /// EN: Initializes a Dubbo client with explicit options.
        /// </summary>
        /// <param name="zookeeperCoonectString">ZooKeeper 连接字符串；多个地址以逗号分隔，认证格式为 <c>[Scheme Auth]</c>。EN: ZooKeeper connection string; separate multiple addresses with commas and append authentication as <c>[Scheme Auth]</c>.</param>
        /// <param name="dubboClientConf">客户端配置；传入 <see langword="null"/> 时使用默认配置。EN: Client options; defaults are used when <see langword="null"/> is supplied.</param>
        /// <exception cref="ArgumentException">连接字符串为空。EN: The connection string is empty.</exception>
        public DubboClient(string zookeeperCoonectString , DubboClientConf dubboClientConf)
        {
            zookeeperCoonectString = zookeeperCoonectString?.Trim();
            if (string.IsNullOrEmpty(zookeeperCoonectString))
            {
                throw new ArgumentException($"“{nameof(zookeeperCoonectString)}” can not be null or empty", nameof(zookeeperCoonectString));
            }
            if (dubboClientConf == null)
            {
                //throw new ArgumentNullException(nameof(dubboClientConf));
                dubboClientConf = new DubboClientConf();
            }
            DubboRootPath = NormalizeDubboRootPath(dubboClientConf.DubboRootPath);
            MetadataCenterAddress = string.IsNullOrWhiteSpace(dubboClientConf.MetadataCenterAddress)
                ? zookeeperCoonectString
                : dubboClientConf.MetadataCenterAddress.Trim();
            MetadataRootPath = string.IsNullOrWhiteSpace(dubboClientConf.MetadataRootPath)
                ? DubboRootPath.TrimEnd('/')
                : NormalizeMetadataRootPath(dubboClientConf.MetadataRootPath);
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
            
            _innerMyZookeeper = DubboClientMultiMyZookeeperStorage.GetMyZookeeper(zookeeperCoonectString);
            if (string.Equals(
                MetadataCenterAddress,
                zookeeperCoonectString,
                StringComparison.Ordinal))
            {
                _metadataMyZookeeper = _innerMyZookeeper;
                _metadataZookeeperHasSeparateReference = false;
            }
            else
            {
                _metadataMyZookeeper =
                    DubboClientMultiMyZookeeperStorage.GetMyZookeeper(MetadataCenterAddress);
                _metadataZookeeperHasSeparateReference = true;
            }
            _dubboClientZookeeperWatcher = new DubboClientZookeeperWatcher(this);
            _metadataManager = new DubboMetadataManager(
                _metadataMyZookeeper,
                MetadataRootPath,
                GetProviderEndPointsForMetadataAsync,
                TelnetMetadataTimeout);
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
        /// <param name="zookeeperCoonectString">ZooKeeper 连接字符串，多个地址以逗号分隔。EN: ZooKeeper connection string with multiple addresses separated by commas.</param>
        /// <param name="funcEndPoint">默认调用入口，例如 <c>ServiceName.FuncName</c> 或 <c>ServiceName#FuncName</c>。EN: Default invocation endpoint, such as <c>ServiceName.FuncName</c> or <c>ServiceName#FuncName</c>.</param>
        /// <exception cref="ArgumentException">连接字符串或调用入口格式无效。EN: The connection string or endpoint format is invalid.</exception>
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


        #region QueryAsync
        /// <summary>
        /// 使用字符串形式的参数调用 Dubbo 方法；原生 Dubbo 协议会通过元数据推断精确 Java 参数类型。
        /// EN: Invokes a Dubbo method with string-form arguments; the native Dubbo protocol resolves exact Java parameter types from metadata.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口，例如 <c>com.foo.DemoService.find</c>。EN: Service and method endpoint, for example <c>com.foo.DemoService.find</c>.</param>
        /// <param name="req">逗号分隔的 JSON 参数内容；无参数时传空字符串。EN: Comma-separated JSON argument content; use an empty string for no arguments.</param>
        /// <returns>包含调用结果、耗时和错误信息的请求结果。EN: A request result containing the response, timing, and error information.</returns>
        /// <exception cref="DubboMetadataException">无法获取或唯一确定 Java 参数类型。EN: Java parameter types cannot be obtained or uniquely resolved.</exception>
        public Task<DubboRequestResult> QueryAsync(string funcEndPoint, string req)
        {
            return QueryInternalAsync(funcEndPoint, req, null);
        }

        /// <summary>
        /// 使用明确的 Java 参数类型执行原生 Dubbo 泛化调用；重载方法、null、集合和 POJO 参数建议使用此重载。
        /// EN: Performs a native Dubbo generic invocation with explicit Java parameter types; use this overload for overloaded methods, nulls, collections, and POJO arguments.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="javaParameterTypes">按声明顺序排列的精确 Java 参数类型。EN: Exact Java parameter type names in declaration order.</param>
        /// <param name="arguments">调用参数。EN: Invocation arguments.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        /// <exception cref="ArgumentException">参数类型数量与参数数量不一致，或入口无效。EN: Type and argument counts differ, or the endpoint is invalid.</exception>
        public Task<DubboRequestResult> QueryGenericAsync(
            string funcEndPoint,
            IReadOnlyList<string> javaParameterTypes,
            params object[] arguments)
        {
            Tuple<string, string> endpoint = GetSeviceNameFormFuncEndPoint(funcEndPoint);
            DubboInvocation invocation = new DubboInvocation(
                endpoint.Item1,
                endpoint.Item2,
                javaParameterTypes,
                arguments);
            return QueryInternalAsync(
                funcEndPoint,
                JsonSerializer.Serialize(arguments),
                invocation);
        }

        /// <summary>
        /// 执行原生 Dubbo 泛化调用；精确 Java 参数类型从元数据中心或服务 Telnet 端点解析并按服务缓存。
        /// EN: Performs a native Dubbo generic invocation; exact Java parameter types are resolved from the metadata center or service Telnet endpoint and cached per service.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="arguments">用于选择方法签名的调用参数。EN: Invocation arguments used to select the method signature.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定重载；可改用明确类型重载。EN: Metadata is unavailable or an overload cannot be uniquely selected; use the explicit-type overload.</exception>
        public async Task<DubboRequestResult> QueryGenericAsync(
            string funcEndPoint,
            params object[] arguments)
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
        /// 使用明确的 Java 参数类型执行原生 Dubbo 泛化调用，并将响应反序列化为指定类型。
        /// EN: Performs a native Dubbo generic invocation with explicit Java parameter types and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="javaParameterTypes">精确 Java 参数类型。EN: Exact Java parameter type names.</param>
        /// <param name="arguments">调用参数。EN: Invocation arguments.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="ArgumentException">参数类型数量与实参数量不一致，或调用描述无效。EN: The parameter-type count differs from the argument count, or the invocation descriptor is invalid.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryGenericAsync<T_Rsp>(
            string funcEndPoint,
            IReadOnlyList<string> javaParameterTypes,
            params object[] arguments)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryGenericAsync(
                    funcEndPoint,
                    javaParameterTypes,
                    arguments).ConfigureAwait(false));
        }

        /// <summary>
        /// 执行原生 Dubbo 泛化调用并反序列化响应；Java 参数类型从元数据中心或 Telnet 回退解析。
        /// EN: Performs a native Dubbo generic invocation and deserializes the response; Java parameter types are resolved from the metadata center or Telnet fallback.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="arguments">调用参数。EN: Invocation arguments.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">元数据不可用或无法唯一确定重载。EN: Metadata is unavailable or an overload cannot be uniquely selected.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryGenericAsync<T_Rsp>(
            string funcEndPoint,
            params object[] arguments)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryGenericAsync(funcEndPoint, arguments).ConfigureAwait(false));
        }

        /// <summary>
        /// 根据 <see cref="DubboInvocation"/> 执行原生 Dubbo 泛化调用；参数类型为空时自动解析元数据。
        /// EN: Performs a native Dubbo generic invocation described by <see cref="DubboInvocation"/>; metadata is resolved when parameter types are empty.
        /// </summary>
        /// <param name="invocation">调用描述，包括服务、方法、参数类型、参数和附件。EN: Invocation descriptor containing the service, method, parameter types, arguments, and attachments.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="invocation"/> 为空。EN: <paramref name="invocation"/> is null.</exception>
        /// <exception cref="DubboMetadataException">需要自动推断但元数据不可用或不明确。EN: Inference is required but metadata is unavailable or ambiguous.</exception>
        public async Task<DubboRequestResult> QueryGenericAsync(DubboInvocation invocation)
        {
            if (invocation == null)
            {
                throw new ArgumentNullException(nameof(invocation));
            }

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
        /// 根据调用描述执行原生 Dubbo 泛化调用，并将响应反序列化为指定类型。
        /// EN: Performs a native Dubbo generic invocation from a descriptor and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="invocation">调用描述。EN: Invocation descriptor.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="invocation"/> 为空。EN: <paramref name="invocation"/> is null.</exception>
        /// <exception cref="DubboMetadataException">需要自动推断但元数据不可用或不明确。EN: Inference is required but metadata is unavailable or ambiguous.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryGenericAsync<T_Rsp>(
            DubboInvocation invocation)
        {
            return new DubboRequestResult<T_Rsp>(
                await QueryGenericAsync(invocation).ConfigureAwait(false));
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
                    $"Use QueryGenericAsync(\"{serviceName}.{methodName}\", " +
                    "new[] { \"<exact-java-type>\" }, arguments) to retry explicitly.",
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
                        $"Use QueryGenericAsync(\"{serviceName}.{methodName}\", " +
                        "new[] { \"<exact-java-type>\" }, arguments) to retry explicitly.",
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
            DubboInvocation nativeInvocation)
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
            AvailableDubboActuatorInfo availableDubboActuatorInfo = _dubboDriverCollection.GetDubboActuatorSuite(nowServiceName, NowLoadBalanceMode, req);
            //获取到可用的DubboActuatorSuite
            if (availableDubboActuatorInfo.ResultType == AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite)
            {
                if (nativeInvocation != null)
                {
                    if (availableDubboActuatorInfo.AvailableDubboActuatorSuite is NativeDubboActuatorSuite nativeSuite)
                    {
                        return await nativeSuite.SendQuery(nativeInvocation).ConfigureAwait(false);
                    }

                    return new DubboRequestResult
                    {
                        QuerySuccess = false,
                        ServiceElapsed = -1,
                        ErrorMeaasge =
                            $"Explicit Dubbo generic invocation requires a dubbo:// provider, but " +
                            $"{availableDubboActuatorInfo.AvailableDubboActuatorSuite.ProtocolType} was selected."
                    };
                }
                if (availableDubboActuatorInfo.AvailableDubboActuatorSuite
                    is NativeDubboActuatorSuite metadataAwareNativeSuite)
                {
                    // Preserve the original QueryAsync API while improving it with an exact
                    // metadata signature. If neither metadata source can supply a signature,
                    // CreateMetadataInvocationAsync throws with the explicit-overload remedy.
                    DubboInvocation parsedInvocation = NativeDubboCodec.ParseInvocation(
                        $"{nowServiceName}#{nowFuncName}",
                        req);
                    DubboInvocation resolvedInvocation = await CreateMetadataInvocationAsync(
                        nowServiceName,
                        nowFuncName,
                        parsedInvocation.Arguments).ConfigureAwait(false);
                    return await metadataAwareNativeSuite.SendQuery(resolvedInvocation)
                        .ConfigureAwait(false);
                }
                return await availableDubboActuatorInfo.AvailableDubboActuatorSuite.SendQuery($"{nowServiceName}{availableDubboActuatorInfo.AvailableDubboActuatorSuite.ServiceFuncSpit}{nowFuncName}", req);
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
                    return await QueryInternalAsync(funcEndPoint, req, nativeInvocation);
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
                    return await QueryInternalAsync(funcEndPoint, req, nativeInvocation);
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

        /// <summary>
        /// 调用无参数 Dubbo 方法。
        /// EN: Invokes a Dubbo method without arguments.
        /// </summary>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <returns>Dubbo 请求结果。EN: The Dubbo request result.</returns>
        public async Task<DubboRequestResult> QueryAsync(string funcEndPoint)
        {
            return await QueryAsync(funcEndPoint, "");
        }

        /// <summary>
        /// 使用字符串形式的参数调用 Dubbo 方法并反序列化响应。
        /// EN: Invokes a Dubbo method with string-form arguments and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req">逗号分隔的 JSON 参数内容。EN: Comma-separated JSON argument content.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">无法解析精确 Java 参数类型。EN: Exact Java parameter types cannot be resolved.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(string funcEndPoint, string req)
        {
            DubboRequestResult sourceDubboResult = await QueryAsync(funcEndPoint, req);
            DubboRequestResult<T_Rsp> dubboRequestResult = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }

        /// <summary>
        /// 调用无参数 Dubbo 方法并反序列化响应。
        /// EN: Invokes a Dubbo method without arguments and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp>(string funcEndPoint)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, "");
        }


        /// <summary>
        /// 使用一个参数调用 Dubbo 方法并反序列化响应。
        /// EN: Invokes a Dubbo method with one argument and deserializes the response.
        /// </summary>
        /// <typeparam name="T_Rsp">响应数据的 CLR 类型。EN: CLR type of the response value.</typeparam>
        /// <typeparam name="T_Req">请求参数的 CLR 类型。EN: CLR type of the request argument.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req">请求参数。EN: Request argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        /// <exception cref="DubboMetadataException">无法解析精确 Java 参数类型。EN: Exact Java parameter types cannot be resolved.</exception>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req>(string funcEndPoint, T_Req req)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, JsonSerializer.Serialize<T_Req>(req));
        }
        /// <summary>使用两个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with two arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2>(string funcEndPoint, T_Req1 req1, T_Req2 req2)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)}");
        }
        /// <summary>使用三个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with three arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)}");
        }
        /// <summary>使用四个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with four arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <typeparam name="T_Req4">第四个参数类型。EN: Fourth argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <param name="req4">第四个参数。EN: Fourth argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)}");
        }
        /// <summary>使用五个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with five arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <typeparam name="T_Req4">第四个参数类型。EN: Fourth argument type.</typeparam>
        /// <typeparam name="T_Req5">第五个参数类型。EN: Fifth argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <param name="req4">第四个参数。EN: Fourth argument.</param>
        /// <param name="req5">第五个参数。EN: Fifth argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)}");
        }
        /// <summary>使用六个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with six arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <typeparam name="T_Req4">第四个参数类型。EN: Fourth argument type.</typeparam>
        /// <typeparam name="T_Req5">第五个参数类型。EN: Fifth argument type.</typeparam>
        /// <typeparam name="T_Req6">第六个参数类型。EN: Sixth argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <param name="req4">第四个参数。EN: Fourth argument.</param>
        /// <param name="req5">第五个参数。EN: Fifth argument.</param>
        /// <param name="req6">第六个参数。EN: Sixth argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)}");
        }
        /// <summary>使用七个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with seven arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <typeparam name="T_Req4">第四个参数类型。EN: Fourth argument type.</typeparam>
        /// <typeparam name="T_Req5">第五个参数类型。EN: Fifth argument type.</typeparam>
        /// <typeparam name="T_Req6">第六个参数类型。EN: Sixth argument type.</typeparam>
        /// <typeparam name="T_Req7">第七个参数类型。EN: Seventh argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <param name="req4">第四个参数。EN: Fourth argument.</param>
        /// <param name="req5">第五个参数。EN: Fifth argument.</param>
        /// <param name="req6">第六个参数。EN: Sixth argument.</param>
        /// <param name="req7">第七个参数。EN: Seventh argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)}");
        }
        /// <summary>使用八个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with eight arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <typeparam name="T_Req4">第四个参数类型。EN: Fourth argument type.</typeparam>
        /// <typeparam name="T_Req5">第五个参数类型。EN: Fifth argument type.</typeparam>
        /// <typeparam name="T_Req6">第六个参数类型。EN: Sixth argument type.</typeparam>
        /// <typeparam name="T_Req7">第七个参数类型。EN: Seventh argument type.</typeparam>
        /// <typeparam name="T_Req8">第八个参数类型。EN: Eighth argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <param name="req4">第四个参数。EN: Fourth argument.</param>
        /// <param name="req5">第五个参数。EN: Fifth argument.</param>
        /// <param name="req6">第六个参数。EN: Sixth argument.</param>
        /// <param name="req7">第七个参数。EN: Seventh argument.</param>
        /// <param name="req8">第八个参数。EN: Eighth argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)}");
        }
        /// <summary>使用九个参数调用 Dubbo 方法并反序列化响应。EN: Invokes a Dubbo method with nine arguments and deserializes the response.</summary>
        /// <typeparam name="T_Rsp">响应类型。EN: Response type.</typeparam>
        /// <typeparam name="T_Req1">第一个参数类型。EN: First argument type.</typeparam>
        /// <typeparam name="T_Req2">第二个参数类型。EN: Second argument type.</typeparam>
        /// <typeparam name="T_Req3">第三个参数类型。EN: Third argument type.</typeparam>
        /// <typeparam name="T_Req4">第四个参数类型。EN: Fourth argument type.</typeparam>
        /// <typeparam name="T_Req5">第五个参数类型。EN: Fifth argument type.</typeparam>
        /// <typeparam name="T_Req6">第六个参数类型。EN: Sixth argument type.</typeparam>
        /// <typeparam name="T_Req7">第七个参数类型。EN: Seventh argument type.</typeparam>
        /// <typeparam name="T_Req8">第八个参数类型。EN: Eighth argument type.</typeparam>
        /// <typeparam name="T_Req9">第九个参数类型。EN: Ninth argument type.</typeparam>
        /// <param name="funcEndPoint">服务和方法入口。EN: Service and method endpoint.</param>
        /// <param name="req1">第一个参数。EN: First argument.</param>
        /// <param name="req2">第二个参数。EN: Second argument.</param>
        /// <param name="req3">第三个参数。EN: Third argument.</param>
        /// <param name="req4">第四个参数。EN: Fourth argument.</param>
        /// <param name="req5">第五个参数。EN: Fifth argument.</param>
        /// <param name="req6">第六个参数。EN: Sixth argument.</param>
        /// <param name="req7">第七个参数。EN: Seventh argument.</param>
        /// <param name="req8">第八个参数。EN: Eighth argument.</param>
        /// <param name="req9">第九个参数。EN: Ninth argument.</param>
        /// <returns>强类型 Dubbo 请求结果。EN: A strongly typed Dubbo request result.</returns>
        public async Task<DubboRequestResult<T_Rsp>> QueryAsync<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string funcEndPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9)
        {
            return await QueryAsync<T_Rsp>(funcEndPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)},{JsonSerializer.Serialize<T_Req9>(req9)}");
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
                if (_metadataZookeeperHasSeparateReference && _metadataMyZookeeper != null)
                {
                    DubboClientMultiMyZookeeperStorage.RemoveMyZookeeper(
                        _metadataMyZookeeper);
                }
                _metadataMyZookeeper = null;
                DubboClientMultiMyZookeeperStorage.RemoveMyZookeeper(_innerMyZookeeper);
                _innerMyZookeeper = null;
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

        /// <summary>
        /// 释放客户端持有的 ZooKeeper 观察器、连接驱动和执行器资源。
        /// EN: Releases ZooKeeper watchers, connection drivers, and actuator resources owned by the client.
        /// </summary>
        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
