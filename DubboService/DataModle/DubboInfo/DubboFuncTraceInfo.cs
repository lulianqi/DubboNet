using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle.DubboInfo
{

    /// <summary>
    /// 表示 Dubbo Telnet <c>trace</c> 返回的一次方法调用。
    /// EN: Represents one method invocation returned by Dubbo Telnet <c>trace</c>.
    /// </summary>
    public class DubboFuncTraceInfo : DubboInfoBase
    {
        public string FullName { get; set; }
        public string ServiceName { get; set; }
        public string MethodName { get; set; }
        public string FuncRequest { get; set; }
        public string FuncResponse { get; set; }


        /// <summary>
        /// 从trace的返回消息里提取DubboFuncTraceInfo元数据（仅填充FullName，FuncRequest，FuncResponse）(如果失败将返回null)
        /// EN: Extracts function, request, and response data from a trace message, or returns null when parsing fails.
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        public static DubboFuncTraceInfo GetTraceInfo(string source)
        {
            const string FUNCNAME_START = "-> ";
            const string FUNCREQUEST_START = "([";
            const string FUNCRESPONSE_START = "]) -> ";
            const string _dubboResultSpit_elapsed = "\nelapsed: ";


            DubboFuncTraceInfo dubboFuncTraceInfo = new DubboFuncTraceInfo();
            if (string.IsNullOrEmpty(source)) return null;
            int startIndex, endIndex = 0;
            //get func name
            startIndex = source.IndexOf(FUNCNAME_START);
            if (startIndex == -1) return null;
            endIndex = source.IndexOf(FUNCREQUEST_START, startIndex);
            if (startIndex == -1) return null;
            dubboFuncTraceInfo.FullName = source.Substring(startIndex + FUNCNAME_START.Length, endIndex - startIndex - FUNCNAME_START.Length);
            //get request
            startIndex = endIndex + FUNCREQUEST_START.Length;
            endIndex = source.IndexOf(FUNCRESPONSE_START, startIndex);
            if (endIndex == -1) return null;
            dubboFuncTraceInfo.FuncRequest = source.Substring(startIndex, endIndex - startIndex);
            //get response
            startIndex = endIndex + FUNCRESPONSE_START.Length;
            endIndex = source.IndexOf(_dubboResultSpit_elapsed, startIndex);
            dubboFuncTraceInfo.FuncResponse = source.Substring(startIndex, endIndex - startIndex);

            return dubboFuncTraceInfo;
        }
    }

}
