using DubboNet.DubboService.DataModle.DubboInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    /// <summary>
    /// 汇总执行器的节点诊断数据和近期请求耗时。
    /// <para>EN: Aggregates provider diagnostics and recent request latency for an actuator.</para>
    /// </summary>
    public class DubboActuatorSuiteStatus
    {
        private const int AVERAGE_QUERY_NUM = 20;
        private DateTime _lastElapsedUpdateTime = DateTime.Now;
        private int _lastQueryElapsed;
        private int _averageQueryElapsed;

        /// <summary>
        /// 获取当前 Dubbo 节点的状态信息；非 Telnet 执行器可能不提供。
        /// <para>EN: Gets the current provider status; non-Telnet actuators may not expose it.</para>
        /// </summary>
        public DubboStatusInfo StatusInfo { get; internal set; }
        /// <summary>
        /// 获取当前节点发布的服务列表；非 Telnet 执行器可能不提供。
        /// <para>EN: Gets the services published by the provider; non-Telnet actuators may not expose this information.</para>
        /// </summary>
        public DubboLsInfo LsInfo { get; internal set; }
        /// <summary>
        /// 获取最后一次成功传输请求的耗时（毫秒）。
        /// <para>EN: Gets the latency of the last successfully transported request in milliseconds.</para>
        /// </summary>
        public int LastQueryElapsed {
            get
            {
                return _lastQueryElapsed;
            } 
            internal set 
            {
                _lastQueryElapsed = value;
                if(_averageQueryElapsed ==0 || (DateTime.Now - _lastElapsedUpdateTime).TotalSeconds>5*60)
                {
                    _averageQueryElapsed = _lastQueryElapsed;
                }
                else
                {
                    _averageQueryElapsed = (_averageQueryElapsed * (AVERAGE_QUERY_NUM - 1) + _lastQueryElapsed) / AVERAGE_QUERY_NUM;
                }
                _lastElapsedUpdateTime = DateTime.Now;
            } 
        }
        /// <summary>
        /// 获取最近请求的平滑平均耗时（毫秒）；超过 5 分钟无更新时返回 0。
        /// <para>EN: Gets the smoothed recent request latency in milliseconds; returns zero after five minutes without an update.</para>
        /// </summary>
        public int AverageQueryElapsed {
            get 
            {
                if ((DateTime.Now - _lastElapsedUpdateTime).TotalSeconds > 5 * 60)
                {
                    return 0;
                }
                return _averageQueryElapsed;
            } 
        }
    }
}
