using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients
{
    internal class DubboActuatorSuiteEndPintInfo
    {
        public IPEndPoint EndPoint { get; set; }
        public DubboActuatorSuite ActuatorSuite { get; set; }
        public int ReferenceCount { get; internal set; } = 0;
    }
}
