﻿using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 可用的DubboActuatorSuite信息·(主要用于返回负载结果)
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

        public GetDubboActuatorSuiteResultType ResultType { get;internal set; } = GetDubboActuatorSuiteResultType.Unkonw;
        public string ErrorMes { get;internal set; } = null;
        public DubboServiceDriver NowDubboServiceDriver { get;internal set; }
        public IDubboActuatorSuite AvailableDubboActuatorSuite { get;internal set; }
    }

}
