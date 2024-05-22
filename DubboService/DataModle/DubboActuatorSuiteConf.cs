using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    public class DubboActuatorSuiteConf
    {
        public int MaxConnections { get; set; } = 20;
        public int AssistConnectionAliveTime { get; set; } = 60 * 5;
        public int MasterConnectionAliveTime { get; set; } = 60 * 20;
        public int DubboRequestTimeout { get; set; } = 10 * 1000;
        public string DefaultServiceName { get; set; } = null;
        public bool IsAutoUpdateStatusInfo { get; set; } = true;
    }
}
