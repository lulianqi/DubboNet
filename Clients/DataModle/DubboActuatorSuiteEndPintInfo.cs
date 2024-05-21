using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.DataModle
{
    internal class DubboActuatorSuiteEndPintInfo
    {
        public IPEndPoint EndPoint { get;internal set; }
        public TelnetDubboActuatorSuite ActuatorSuite { get;internal set; }
        public int ReferenceCount { get; internal set; } = 0;
    }
}
