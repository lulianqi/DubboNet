﻿using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 服务端点信息(信息来源dubbo uri)
    /// </summary>
    public class DubboServiceEndPointInfo
    {
        public IPEndPoint EndPoint { get; set; } = null;

        public DubboActuatorSuite InnerDubboActuatorSuite { get; set; }

        public bool? Anyhost { get; set; } = null;
        public string Application { get; set; } = null;
        public string BeanName { get; set; } = null;
        public bool? Deprecated { get; set; } = null;
        public string Dubbo { get; set; } = null;
        public bool? Dynamic { get; set; } = null;
        public bool? Generic { get; set; } = null;
        public string Interface { get; set; } = null;
        public string Loadbalance { get; set; } = null;
        public int? Pid { get; set; } = null;
        public string Methods { get; set; } = null;
        public bool? Register { get; set; } = null;
        public string Revision { get; set; } = null;
        public string Side { get; set; } = null;
        public string Threads { get; set; } = null;
        public int? Timeout { get; set; } = null;
        public long? Timestamp { get; set; } = null;
        public int Weight { get; set; } = 100;

        public static DubboServiceEndPointInfo GetDubboServiceEndPointInfo(Uri dubboUri)
        {
            if (dubboUri == null)
            {
                throw new ArgumentNullException(nameof(dubboUri));
            }
            DubboServiceEndPointInfo dubboServiceEndPointInfo = new DubboServiceEndPointInfo();
            dubboServiceEndPointInfo.EndPoint = new IPEndPoint(IPAddress.Parse(dubboUri.Host), dubboUri.Port);
            NameValueCollection queryParameters = System.Web.HttpUtility.ParseQueryString(dubboUri.Query);
            int tempIntValue = 0;
            bool tempBoolValue = false;
            long tempLongValue = 0;
            if (bool.TryParse(queryParameters["anyhost"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Anyhost = tempBoolValue;
            }
            dubboServiceEndPointInfo.Application = queryParameters["application"];
            dubboServiceEndPointInfo.BeanName = queryParameters["bean.name"];
            if (bool.TryParse(queryParameters["dynamic"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Dynamic = tempBoolValue;
            }
            dubboServiceEndPointInfo.Dubbo = queryParameters["dubbo"];
            if (bool.TryParse(queryParameters["generic"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Generic = tempBoolValue;
            }
            dubboServiceEndPointInfo.Interface = queryParameters["interface"];
            dubboServiceEndPointInfo.Loadbalance = queryParameters["loadbalance"];
            if (int.TryParse(queryParameters["pid"], out tempIntValue))
            {
                dubboServiceEndPointInfo.Pid = tempIntValue;
            }
            dubboServiceEndPointInfo.Methods = queryParameters["methods"];
            if (bool.TryParse(queryParameters["register"], out tempBoolValue))
            {
                dubboServiceEndPointInfo.Register = tempBoolValue;
            }
            dubboServiceEndPointInfo.Revision = queryParameters["revision"];
            dubboServiceEndPointInfo.Side = queryParameters["side"];
            dubboServiceEndPointInfo.Threads = queryParameters["threads"];
            if (int.TryParse(queryParameters["timeout"], out tempIntValue))
            {
                dubboServiceEndPointInfo.Timeout = tempIntValue;
            }
            if (long.TryParse(queryParameters["timestamp"], out tempLongValue))
            {
                dubboServiceEndPointInfo.Timestamp = tempLongValue;
            }
            if (int.TryParse(queryParameters["weight"], out tempIntValue))
            {
                dubboServiceEndPointInfo.Weight = tempIntValue;
            }
            return dubboServiceEndPointInfo;
        }
    }


    /// <summary>
    /// 服务端点信息集合
    /// </summary>
    public class DubboServiceEndPointInfos
    {
        public string ErrorInfo { get; set; } = null;
        public List<DubboServiceEndPointInfo> EndPoints { get; set; } = new List<DubboServiceEndPointInfo>();
    }
}
