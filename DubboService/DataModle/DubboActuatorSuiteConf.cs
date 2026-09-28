using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    /// <summary>
    /// 配置单个服务节点执行器的连接、超时与诊断行为。
    /// <para>EN: Configures connection, timeout, and diagnostic behavior for one provider actuator.</para>
    /// </summary>
    public class DubboActuatorSuiteConf
    {
        /// <summary>
        /// 获取或设置 Telnet 执行器的最大连接数；默认 20。
        /// <para>EN: Gets or sets the maximum Telnet actuator connection count; the default is 20.</para>
        /// </summary>
        public int MaxConnections { get; set; } = 20;

        /// <summary>
        /// 获取或设置辅助连接的空闲存活时间（秒）；0 表示不因空闲关闭。
        /// <para>EN: Gets or sets the idle lifetime of auxiliary connections in seconds; zero disables idle closing.</para>
        /// </summary>
        public int AssistConnectionAliveTime { get; set; } = 60 * 5;

        /// <summary>
        /// 获取或设置主连接的空闲存活时间（秒）；0 表示不因空闲关闭。
        /// <para>EN: Gets or sets the idle lifetime of the primary connection in seconds; zero disables idle closing.</para>
        /// </summary>
        public int MasterConnectionAliveTime { get; set; } = 60 * 20;

        /// <summary>
        /// 获取或设置请求超时时间（毫秒）；默认 10 秒。
        /// <para>EN: Gets or sets the request timeout in milliseconds; the default is 10 seconds.</para>
        /// </summary>
        public int DubboRequestTimeout { get; set; } = 10 * 1000;

        /// <summary>
        /// 获取或设置省略服务名时使用的默认 Dubbo 接口名。
        /// <para>EN: Gets or sets the default Dubbo interface name used when the service name is omitted.</para>
        /// </summary>
        public string DefaultServiceName { get; set; } = null;

        /// <summary>
        /// 获取或设置 Telnet 执行器是否自动刷新节点状态信息。
        /// <para>EN: Gets or sets whether the Telnet actuator automatically refreshes provider status information.</para>
        /// </summary>
        public bool IsAutoUpdateStatusInfo { get; set; } = true;
    }
}
