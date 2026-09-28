using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 存储ActuatorSuite（IDubboActuatorSuite）的内部对象（主要用于保持ActuatorSuite的复用）
    /// EN: Internal holder used to share an actuator suite and track its references across services.
    /// </summary>
    internal class DubboActuatorSuiteEndPintInfo
    {
        /// <summary>执行器连接的 Provider 网络端点。EN: Provider network endpoint used by the actuator suite.</summary>
        public IPEndPoint EndPoint { get;internal set; }
        /// <summary>可在多个服务间复用的执行器套件。EN: Actuator suite shared across services.</summary>
        public IDubboActuatorSuite ActuatorSuite { get;internal set; }
        /// <summary>当前引用该执行器套件的服务驱动数量。EN: Number of service drivers currently referencing the actuator suite.</summary>
        public int ReferenceCount { get; internal set; } = 0;
    }
}
