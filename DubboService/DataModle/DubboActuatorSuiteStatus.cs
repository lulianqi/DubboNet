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
        private const int AVERAGE_QUERY_NUM = 20;
        private DateTime _lastElapsedUpdateTime = DateTime.Now;
        private int _lastQueryElapsed;
        private int _averageQueryElapsed;

        /// <summary>
        /// 当前Dubbo服务节点的状态信息（在HttpDubboActuatorSuite实现版本里可能为空）
        /// </summary>
        public DubboStatusInfo StatusInfo { get; internal set; }
        /// <summary>
        /// 当前服务节点提供的服务列表信息（在HttpDubboActuatorSuite实现版本里可能为空）
        /// </summary>
        public DubboLsInfo LsInfo { get; internal set; }
        /// <summary>
        /// 获取当前服务最后一次的请求的耗时
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
        /// 获取当前服务最后AVERAGE_QUERY_NUM次的请求平均耗时（5分钟内的）
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
