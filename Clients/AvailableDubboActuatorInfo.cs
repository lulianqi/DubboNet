using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients
{
    /// <summary>
    /// 可用的DubboActuatorSuite信息·
    /// </summary>
    internal class AvailableDubboActuatorInfo
    {
        public enum GetDubboActuatorSuiteResultType
        {
            Unkonw,
            GetDubboActuatorSuite,
            NoDubboServiceDriver,
            NoActuatorInService,
            NoAvailableActuator,
            NetworkError
        }

        public GetDubboActuatorSuiteResultType ResultType { get; set; } = GetDubboActuatorSuiteResultType.Unkonw;
        public string ErrorMes { get; set; } = null;
        public DubboServiceDriver NowDubboServiceDriver { get; set; }
        public DubboActuatorSuite AvailableDubboActuatorSuite { get; set; }
    }

}
