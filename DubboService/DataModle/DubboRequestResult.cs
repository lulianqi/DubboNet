using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MyCommonHelper;

namespace DubboNet.DubboService.DataModle
{
    /*invoke XxxService.xxxMethod(1234, "abcd", {"prop" : "value"})
    Use default service com.account.api.account.AccountInfoRemoteSerevice.
    result: { "code":200,"data":57,"message":"successful","success":true}
    elapsed: 7 ms.
    */
    /// <summary>
    /// 表示一次 Dubbo 调用的协议级结果、耗时及错误信息。
    /// EN: Represents the protocol-level result, timing, and error information of a Dubbo invocation.
    /// </summary>
    public class DubboRequestResult
    {
        private const string _dubboResultSpit_result = "result: ";
        private const string _dubboResultSpit_elapsed = "\nelapsed: ";
        private const string _dubboResultSpit_ms = " ms.";

        /// <summary>
        /// DubboRequestResult对象创建时间
        /// EN: Gets the time at which this result object was created.
        /// </summary>
        public DateTime CreatTime { get;protected set; } = DateTime.Now;
        /// <summary>
        /// 请求是否成功（非业务含义，仅在协议上表示请求成功）
        /// EN: Gets whether the transport and Dubbo protocol invocation succeeded; this does not represent business success.
        /// </summary>
        public bool QuerySuccess { get; internal set; } = true;
        /// <summary>
        /// 请求结果
        /// EN: Gets or sets the response payload represented as JSON or raw response text.
        /// </summary>
        public string Result { get; set; }
        /// <summary>
        /// 服务处理时间，-1表示解析失败（毫秒）
        /// EN: Gets or sets the provider-reported processing time in milliseconds; -1 indicates failure or an unavailable value.
        /// </summary>
        public int ServiceElapsed { get; set; }
        /// <summary>
        /// 请求时间，包含网络时间（毫秒）
        /// EN: Gets or sets the end-to-end request time, including network time, in milliseconds.
        /// </summary>
        public int RequestElapsed { get; set; }
        /// <summary>
        /// 请求的异常错误信息（请求已经有了返回数据，返回数据非法或反序列化异常时等异常情况，此处记录错误详情）
        /// DubboRequestResult是否正常应以ServiceElapsed是否为-1为准，因为可能存在请求正常返回，但是反序列化失败的场景
        /// EN: Gets or sets detailed transport, protocol, response parsing, or deserialization error information.
        /// </summary>
        public string ErrorMeaasge { get; set; } = null;

        /// <summary>
        /// 原生 Dubbo 请求 ID；不公开该值的传输协议返回 null。
        /// EN: Gets the native Dubbo request id, or null for transports that do not expose one.
        /// </summary>
        public long? RequestId { get; internal set; }

        /// <summary>
        /// 原生 Dubbo 响应状态码；20 表示协议成功。
        /// EN: Gets the native Dubbo response status; 20 indicates protocol success.
        /// </summary>
        public byte? ResponseStatus { get; internal set; }

        /// <summary>
        /// Dubbo 帧携带的序列化 ID；Hessian2 为 2。
        /// EN: Gets the serialization id carried by the Dubbo frame; Hessian2 uses id 2.
        /// </summary>
        public byte? SerializationId { get; internal set; }

        /// <summary>
        /// 获取响应值转换为 Result JSON 前的动态反序列化对象。
        /// EN: Gets the deserialized response value before it is converted to Result JSON.
        /// </summary>
        public object RawResult { get; internal set; }

        /// <summary>
        /// 获取原生 Dubbo 响应附件。
        /// EN: Gets the native Dubbo response attachments.
        /// </summary>
        public IReadOnlyDictionary<string, object> ResponseAttachments { get; internal set; }

        /// <summary>
        /// 获取 ServiceElapsed 是否包含可用的服务端执行时间；原生 Dubbo 默认不返回该时间。
        /// EN: Gets whether ServiceElapsed contains a provider execution time; native Dubbo does not report one by default.
        /// </summary>
        public bool ServiceElapsedAvailable { get; internal set; } = true;

        /// <summary>
        /// 更新QuerySuccess为失败 (成功不用设置，默认成功)
        /// EN: Marks the transport-level query as failed; successful results keep the default state.
        /// </summary>
        internal void UpdateQueryFailed()
        {
            ServiceElapsed = -1;
            QuerySuccess = false;
        }

        /// <summary>
        /// 使用当前事件更新ServiceElapsed
        /// EN: Updates the elapsed time recorded at the service-response boundary.
        /// </summary>
        internal void UpdateServiceElapsed()
        {
            ServiceElapsed = (int)(DateTime.Now - CreatTime).TotalMilliseconds;
        }

        /// <summary>
        /// 使用当前时间更新 RequestElapsed。
        /// EN: Updates RequestElapsed using the current time.
        /// </summary>
        internal void UpdateRequestElapsed()
        {
            RequestElapsed = (int)(DateTime.Now - CreatTime).TotalMilliseconds;
        }

        /// <summary>
        /// 通过dubbo telnet原始返回 获取DubboRequestResult (仅适用于TelnetDubboActuatorSuite的原始RAW数据处理)
        /// EN: Parses a raw Dubbo Telnet response into a DubboRequestResult; used only by TelnetDubboActuatorSuite.
        /// </summary>
        /// <param name="queryResultStr"></param>
        /// <returns></returns>
        internal static DubboRequestResult GetRequestResultFormStr(string queryResultStr)
        {
            DubboRequestResult dubboRequestResult = new DubboRequestResult();
            int nowStartFlag = 0;
            int nowEndFlag = 0;
            if (queryResultStr.Contains(_dubboResultSpit_result))
            {
                //get Result
                if (!queryResultStr.StartsWith(_dubboResultSpit_result))
                {
                    nowStartFlag = queryResultStr.IndexOf(_dubboResultSpit_result);
                }
                nowStartFlag = nowStartFlag + _dubboResultSpit_result.Length;
                //get Elapsed
                //需要指定StringComparison.Ordinal，不然\n在\r\n中将不能被找到，详见 https://learn.microsoft.com/zh-cn/dotnet/core/extensions/globalization-icu
                nowEndFlag = queryResultStr.IndexOf(_dubboResultSpit_elapsed, nowStartFlag, StringComparison.Ordinal);
                if (nowEndFlag > 0)
                {
                    dubboRequestResult.Result = queryResultStr.Substring(nowStartFlag, nowEndFlag - nowStartFlag);
                    nowStartFlag = nowEndFlag + _dubboResultSpit_elapsed.Length;
                    nowEndFlag = queryResultStr.IndexOf(_dubboResultSpit_ms, nowStartFlag);
                    int tempElapsed = -1;
                    if (nowEndFlag > 0)
                    {
                        if (!int.TryParse(queryResultStr.Substring(nowStartFlag, nowEndFlag - nowStartFlag), out tempElapsed))
                        {
                            //TryParse 失败 out 值会赋写为0
                            tempElapsed = -1;
                            dubboRequestResult.UpdateQueryFailed();
                            dubboRequestResult.ErrorMeaasge = $"can not find [_dubboResultSpit_ms]:{_dubboResultSpit_ms} in queryResultStr";
                        }
                    }
                    dubboRequestResult.ServiceElapsed = tempElapsed;
                }
                else
                {
                    dubboRequestResult.Result = queryResultStr.Substring(nowStartFlag);
                    dubboRequestResult.UpdateQueryFailed();
                    dubboRequestResult.ErrorMeaasge = $"can not find [_dubboResultSpit_elapsed]:{_dubboResultSpit_elapsed} in queryResultStr";
                }

            }
            else
            {
                dubboRequestResult.Result = queryResultStr;
                dubboRequestResult.UpdateQueryFailed();
                dubboRequestResult.ErrorMeaasge = $"can not find [_dubboResultSpit_elapsed]:{_dubboResultSpit_result} in queryResultStr";
            }
            return dubboRequestResult;
        }

        /// <summary>
        /// 返回包含成功状态、结果、耗时和错误信息的可读文本。
        /// EN: Returns readable text containing status, result, timing, and error information.
        /// </summary>
        /// <returns>结果摘要。EN: A result summary.</returns>
        public override string ToString()
        {
            return $"QuerySuccess:{QuerySuccess}{System.Environment.NewLine}CreatTime:{CreatTime.ToString("yyyy-MM-dd HH:mm:ss.fff")}{System.Environment.NewLine}Result:{Result}{System.Environment.NewLine}ServiceElapsed:{ServiceElapsed}{System.Environment.NewLine}RequestElapsed:{RequestElapsed}{System.Environment.NewLine}ErrorMeaasge:{ErrorMeaasge??""}";
        }
    }

    /// <summary>
    /// 表示包含指定响应模型的一次 Dubbo 调用结果。
    /// EN: Represents a Dubbo invocation result containing a response model of the specified type.
    /// </summary>
    /// <typeparam name="T">响应模型类型。EN: The response model type.</typeparam>
    public class DubboRequestResult<T> : DubboRequestResult  //where T :class
    {
        private T _resultModle = default;

        private bool hasSetResultModle = false;

        /// <summary>
        /// T Modle 类型数据
        /// EN: Gets the response payload deserialized as the requested model type.
        /// </summary>
        public T ResultModle
        {
            get
            {
                if (!hasSetResultModle)
                {
                    if (!string.IsNullOrEmpty(Result))
                    {
                        try
                        {
                            _resultModle = JsonSerializer.Deserialize<T>(Result);
                        }
                        catch (Exception ex)
                        {
                            _resultModle = default;
                            ErrorMeaasge = ex.Message;
                            MyLogger.LogError("Get ResultModle Error", ex);
                        }
                    }
                    hasSetResultModle = true;
                }
                return _resultModle;
            }
        }

        /// <summary>
        /// 初始化一个空的强类型 Dubbo 调用结果。
        /// EN: Initializes an empty strongly typed Dubbo invocation result.
        /// </summary>
        public DubboRequestResult()
        {
        }

        /// <summary>
        /// 从非泛型调用结果初始化强类型结果并尝试反序列化响应模型。
        /// EN: Initializes a strongly typed result from an untyped result and attempts to deserialize the response model.
        /// </summary>
        /// <param name="sourceDubboRequestResult">源调用结果。EN: The source invocation result.</param>
        public DubboRequestResult(DubboRequestResult sourceDubboRequestResult)
        {
            CreatTime = sourceDubboRequestResult.CreatTime;
            QuerySuccess = sourceDubboRequestResult.QuerySuccess;
            Result = sourceDubboRequestResult.Result;
            ServiceElapsed = sourceDubboRequestResult.ServiceElapsed;
            RequestElapsed = sourceDubboRequestResult.RequestElapsed;
            ErrorMeaasge = sourceDubboRequestResult?.ErrorMeaasge; 
            RequestId = sourceDubboRequestResult?.RequestId;
            ResponseStatus = sourceDubboRequestResult?.ResponseStatus;
            SerializationId = sourceDubboRequestResult?.SerializationId;
            RawResult = sourceDubboRequestResult?.RawResult;
            ResponseAttachments = sourceDubboRequestResult?.ResponseAttachments;
            ServiceElapsedAvailable = sourceDubboRequestResult?.ServiceElapsedAvailable ?? true;
            //如果原始数据有问题，即放弃反序列化
            if (QuerySuccess)
            {
                _ = ResultModle;
            }
            else
            {
                hasSetResultModle = true;
            }
        }

        /// <summary>
        /// 清除已缓存的响应模型，使下次访问 ResultModle 时重新反序列化。
        /// EN: Clears the cached response model so the next ResultModle access deserializes it again.
        /// </summary>
        public void ResetResultModle()
        {
            _resultModle = default;
            hasSetResultModle = false;
        }
    }

}
