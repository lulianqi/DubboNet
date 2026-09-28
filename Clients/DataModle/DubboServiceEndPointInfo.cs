using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 服务端点信息(信息来源dubbo uri)
    /// EN: Provider endpoint information parsed from a Dubbo registry URL.
    /// </summary>
    public class DubboServiceEndPointInfo
    {
        /// <summary>Provider 网络端点。EN: Provider network endpoint.</summary>
        public IPEndPoint EndPoint { get;internal set; } = null;
        /// <summary>该端点关联的执行器套件。EN: Actuator suite associated with this endpoint.</summary>
        public IDubboActuatorSuite InnerDubboActuatorSuite { get;internal set; }
        /// <summary>平滑加权轮询使用的当前调度权重。EN: Current dispatch weight used by smooth weighted round robin.</summary>
        public int NowDispatchWeight { get;internal set; } = 0;
        /// <summary>注册 URL 的协议，例如 <c>dubbo</c> 或 <c>tri</c>。EN: Registry URL scheme, such as <c>dubbo</c> or <c>tri</c>.</summary>
        public string Scheme { get; set; } = null;

        /// <summary>是否接受任意主机地址。EN: Whether the provider accepts any host address.</summary>
        public bool? Anyhost { get; set; } = null;
        /// <summary>Provider 应用名。EN: Provider application name.</summary>
        public string Application { get; set; } = null;
        /// <summary>Provider Bean 名称。EN: Provider bean name.</summary>
        public string BeanName { get; set; } = null;
        /// <summary>服务是否已废弃。EN: Whether the service is deprecated.</summary>
        public bool? Deprecated { get; set; } = null;
        /// <summary>端点是否已禁用。EN: Whether the endpoint is disabled.</summary>
        public bool? Disabled { get; set; } = null;
        /// <summary>Dubbo 协议版本，通常为 <c>2.0.2</c>；它不是框架发布版本。EN: Dubbo protocol version, commonly <c>2.0.2</c>; this is not the framework release version.</summary>
        public string Dubbo { get; set; } = null;
        /// <summary>注册节点是否为动态节点。EN: Whether the registry entry is dynamic.</summary>
        public bool? Dynamic { get; set; } = null;
        /// <summary>服务是否以泛化方式导出。EN: Whether the service is exported as a generic service.</summary>
        public bool? Generic { get; set; } = null;
        /// <summary>完整 Java 服务接口名。EN: Fully qualified Java service interface name.</summary>
        public string Interface { get; set; } = null;
        /// <summary>Provider 声明的负载均衡策略。EN: Load-balancing strategy declared by the provider.</summary>
        public string Loadbalance { get; set; } = null;
        /// <summary>Provider 进程标识。EN: Provider process identifier.</summary>
        public int? Pid { get; set; } = null;
        /// <summary>Provider 声明的方法名列表。EN: Method-name list declared by the provider.</summary>
        public string Methods { get; set; } = null;
        /// <summary>服务是否注册到注册中心。EN: Whether the service is registered with the registry.</summary>
        public bool? Register { get; set; } = null;
        /// <summary>服务接口或构件修订版本；语义由 Provider 决定。EN: Service interface or artifact revision as reported by the provider.</summary>
        public string Revision { get; set; } = null;
        /// <summary>服务角色，通常为 <c>provider</c>。EN: Service side, normally <c>provider</c>.</summary>
        public string Side { get; set; } = null;
        /// <summary>Provider 工作线程配置。EN: Provider worker-thread configuration.</summary>
        public string Threads { get; set; } = null;
        /// <summary>Dubbo 框架发布版本；元数据中心优先级判断使用该字段。EN: Dubbo framework release version used to determine metadata-center lookup priority.</summary>
        public string Release { get; set; } = null;
        /// <summary>Dubbo 服务版本，用于服务分组和元数据路径；它不是框架版本。EN: Dubbo service version used for service identity and metadata paths; this is not the framework version.</summary>
        public string Version { get; set; } = null;
        /// <summary>Dubbo 服务分组。EN: Dubbo service group.</summary>
        public string Group { get; set; } = null;
        /// <summary>Provider 首选序列化方式列表。EN: Provider-preferred serialization methods.</summary>
        public string PreferSerialization { get; set; } = null;
        /// <summary>Provider 配置的序列化方式。EN: Serialization method configured by the provider.</summary>
        public string Serialization { get; set; } = null;
        /// <summary>Provider 声明的调用超时时间，单位毫秒。EN: Invocation timeout declared by the provider, in milliseconds.</summary>
        public int? Timeout { get; set; } = null;
        /// <summary>注册 URL 的时间戳。EN: Timestamp carried by the registry URL.</summary>
        public long? Timestamp { get; set; } = null;
        /// <summary>负载均衡权重，默认值为 100。EN: Load-balancing weight; defaults to 100.</summary>
        public int Weight { get; set; } = 100;


        /// <summary>
        /// 从 Dubbo 注册 URL 解析服务端点信息。
        /// EN: Parses provider endpoint information from a Dubbo registry URL.
        /// </summary>
        /// <param name="dubboUri">包含协议、主机、端口和查询参数的绝对 URL。EN: Absolute URL containing the scheme, host, port, and query parameters.</param>
        /// <returns>解析后的服务端点信息。EN: Parsed provider endpoint information.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="dubboUri"/> 为空。EN: <paramref name="dubboUri"/> is null.</exception>
        /// <exception cref="FormatException">主机不是有效 IP 地址。EN: The host is not a valid IP address.</exception>
        public static DubboServiceEndPointInfo GetDubboServiceEndPointInfo(Uri dubboUri)
        {
            if (dubboUri == null)
            {
                throw new ArgumentNullException(nameof(dubboUri));
            }
            DubboServiceEndPointInfo dubboServiceEndPointInfo = new DubboServiceEndPointInfo();
            dubboServiceEndPointInfo.EndPoint = new IPEndPoint(IPAddress.Parse(dubboUri.Host), dubboUri.Port);
            dubboServiceEndPointInfo.Scheme = dubboUri.Scheme;
            NameValueCollection queryParameters = System.Web.HttpUtility.ParseQueryString(dubboUri.Query);
            int tempIntValue = 0;
            bool tempBoolValue = false;
            long tempLongValue = 0;
            if (bool.TryParse(queryParameters["anyhost"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Anyhost = tempBoolValue;
            }
            dubboServiceEndPointInfo.Application = queryParameters["application"];
            if (bool.TryParse(queryParameters["deprecated"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Deprecated = tempBoolValue;
            }
            if (bool.TryParse(queryParameters["disabled"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Disabled = tempBoolValue;
            }
            dubboServiceEndPointInfo.BeanName = queryParameters["bean.name"];
            if (bool.TryParse(queryParameters["dynamic"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Dynamic = tempBoolValue;
            }
            dubboServiceEndPointInfo.Dubbo = queryParameters["dubbo"];
            if (bool.TryParse(queryParameters["generic"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Generic = tempBoolValue;
            }
            dubboServiceEndPointInfo.Interface = queryParameters["interface"];
            dubboServiceEndPointInfo.Loadbalance = queryParameters["loadbalance"];
            if (int.TryParse(queryParameters["pid"], out tempIntValue))
            {
                dubboServiceEndPointInfo.Pid = tempIntValue;
            }
            //兼容pid=29001®ister=true格式数据
            else if(!string.IsNullOrEmpty(queryParameters["pid"]))
            {
                if(queryParameters["pid"].Contains("@"))
                {
                    if(int.TryParse(queryParameters["pid"].Split('@')[0], out tempIntValue))
                    {
                        dubboServiceEndPointInfo.Pid = tempIntValue;
                    }
                }
                if(dubboServiceEndPointInfo.Pid == null && queryParameters["pid"].Contains("®"))
                {
                    if(int.TryParse(queryParameters["pid"].Split('®')[0], out tempIntValue))
                    {
                        dubboServiceEndPointInfo.Pid = tempIntValue;
                    }
                }
            }
            dubboServiceEndPointInfo.Methods = queryParameters["methods"];
            if (bool.TryParse(queryParameters["register"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Register = tempBoolValue;
            }
            dubboServiceEndPointInfo.Revision = queryParameters["revision"];
            dubboServiceEndPointInfo.Side = queryParameters["side"];
            dubboServiceEndPointInfo.Threads = queryParameters["threads"];
            dubboServiceEndPointInfo.Release = queryParameters["release"];
            dubboServiceEndPointInfo.Version = queryParameters["version"];
            dubboServiceEndPointInfo.Group = queryParameters["group"];
            dubboServiceEndPointInfo.PreferSerialization = queryParameters["prefer.serialization"];
            dubboServiceEndPointInfo.Serialization = queryParameters["serialization"];
            if (int.TryParse(queryParameters["timeout"], out tempIntValue))
            {
                dubboServiceEndPointInfo.Timeout = tempIntValue;
            }
            //兼容timeout=6000×tamp=1710423915011格式数据
            else if(!string.IsNullOrEmpty(queryParameters["timeout"])&&queryParameters["timeout"].Contains("×tamp="))
            {
                string[] tempArray = queryParameters["timeout"].Split("×tamp=");
                if (int.TryParse(tempArray[0], out tempIntValue))
                {
                    dubboServiceEndPointInfo.Timeout = tempIntValue;
                }
                if (int.TryParse(tempArray[1], out tempIntValue))
                {
                    dubboServiceEndPointInfo.Timestamp = tempIntValue;
                }
            }
            if (long.TryParse(queryParameters["timestamp"], out tempLongValue))
            {
                dubboServiceEndPointInfo.Timestamp = tempLongValue;
            }
            if (int.TryParse(queryParameters["weight"], out tempIntValue))
            {
                dubboServiceEndPointInfo.Weight = tempIntValue;
            }
            return dubboServiceEndPointInfo;
        }
    }


    /// <summary>
    /// 服务端点信息集合
    /// EN: Result containing discovered provider endpoints and any discovery error.
    /// </summary>
    public class DubboServiceEndPointInfos
    {
        /// <summary>服务发现失败信息；成功时为空。EN: Service-discovery error message; null on success.</summary>
        public string ErrorInfo { get; set; } = null;
        /// <summary>已发现的 Provider 端点列表。EN: Discovered provider endpoints.</summary>
        public List<DubboServiceEndPointInfo> EndPoints { get; set; } = new List<DubboServiceEndPointInfo>();
    }
}
