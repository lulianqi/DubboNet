using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 可用的DubboActuatorSuite信息·(主要用于返回负载结果)
    /// EN: Internal result describing actuator-suite selection and its load-balancing outcome.
    /// </summary>
    internal class AvailableDubboActuatorInfo
    {
        /// <summary>
        /// 获取执行器套件的结果类型。
        /// EN: Outcome of an actuator-suite lookup.
        /// </summary>
        public enum GetDubboActuatorSuiteResultType
        {
            /// <summary>未知或尚未设置结果。EN: Unknown or not-yet-assigned outcome.</summary>
            Unkonw,
            /// <summary>已成功获取执行器套件。EN: An actuator suite was selected successfully.</summary>
            GetDubboActuatorSuite,
            /// <summary>未找到服务驱动。EN: No service driver exists for the requested service.</summary>
            NoDubboServiceDriver,
            /// <summary>服务驱动中没有 Provider 节点。EN: The service driver contains no provider endpoints.</summary>
            NoActuatorInService,
            /// <summary>存在节点但没有可用执行器。EN: Provider endpoints exist but no actuator suite is available.</summary>
            NoAvailableActuator,
            /// <summary>获取过程中发生网络错误。EN: A network error occurred during lookup.</summary>
            NetworkError
        }

        /// <summary>执行器选择结果类型。EN: Actuator-selection outcome.</summary>
        public GetDubboActuatorSuiteResultType ResultType { get;internal set; } = GetDubboActuatorSuiteResultType.Unkonw;
        /// <summary>选择失败时的错误信息。EN: Error message when selection fails.</summary>
        public string ErrorMes { get;internal set; } = null;
        /// <summary>参与选择的服务驱动。EN: Service driver involved in selection.</summary>
        public DubboServiceDriver NowDubboServiceDriver { get;internal set; }
        /// <summary>选中的可用执行器套件。EN: Selected available actuator suite.</summary>
        public IDubboActuatorSuite AvailableDubboActuatorSuite { get;internal set; }
    }

}
