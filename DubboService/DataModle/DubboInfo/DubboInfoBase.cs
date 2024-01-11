using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle.DubboInfo
{
    public class DubboInfoBase
    {
        /// <summary>
        /// 信息创建时间
        /// </summary>
        public DateTime InfoCreatTime { get; } = DateTime.Now;
        /// <summary>
        /// 服务Host地址
        /// </summary>
        public string DubboHost { get; protected set; }
        /// <summary>
        /// 服务Port端口
        /// </summary>
        public int DubboPort { get; protected set; }

        public void SetDubboActuatorInfo(DubboActuator dubboActuator)
        {
            DubboHost = dubboActuator.DubboHost;
            DubboPort = dubboActuator.DubboPort;
        }
    }
}
