using DubboNet.DubboService.DataModle.DubboInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    public class DubboActuatorSuiteStatus
    {
        public DubboStatusInfo StatusInfo { get; internal set; }
        public DubboLsInfo LsInfo { get; internal set; }
        public int LastQueryElapsed { get; internal set; }
    }
}
