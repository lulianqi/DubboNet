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
    /// </summary>
    internal class DubboActuatorSuiteEndPintInfo
    {
        public IPEndPoint EndPoint { get;internal set; }
        public IDubboActuatorSuite ActuatorSuite { get;internal set; }
        public int ReferenceCount { get; internal set; } = 0;
    }
}
