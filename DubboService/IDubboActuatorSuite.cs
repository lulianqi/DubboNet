using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DubboNet.DubboService
{
    /// <summary>
    /// Dubbo 执行器使用的传输协议类型。
    /// EN: Identifies the transport protocol used by a Dubbo actuator suite.
    /// </summary>
    public enum DubboActuatorProtocolType
    {
        /// <summary>
        /// Dubbo Telnet 命令协议。
        /// EN: The Dubbo Telnet command protocol.
        /// </summary>
        Telnet,
        /// <summary>
        /// HTTP JSON 请求协议。
        /// EN: The HTTP JSON request protocol.
        /// </summary>
        Http,
        /// <summary>
        /// 预留的 gRPC 协议类型；当前没有对应执行器实现。
        /// EN: Reserved for gRPC; no corresponding actuator is currently implemented.
        /// </summary>
        Grpc,
        /// <summary>
        /// 原生 Dubbo2 TCP 协议。
        /// EN: The native Dubbo2 TCP protocol.
        /// </summary>
        NativeDubbo
    }

    /// <summary>
    /// 定义 Dubbo 服务节点执行器的统一调用契约。
    /// EN: Defines the common invocation contract for a Dubbo provider actuator suite.
    /// </summary>
    public interface IDubboActuatorSuite : IDisposable
    {
        /// <summary>
        /// 获取当前执行器使用的协议类型。
        /// EN: Gets the protocol used by this actuator suite.
        /// </summary>
        public DubboActuatorProtocolType ProtocolType { get; }

        /// <summary>
        /// 获取服务名与方法名之间的分隔符。
        /// EN: Gets the separator used between service and method names.
        /// </summary>
        public string ServiceFuncSpit { get; }

        /// <summary>
        /// 获取当前服务节点的状态信息。
        /// EN: Gets the current status information for the provider node.
        /// </summary>
        public DubboActuatorSuiteStatus ActuatorSuiteStatusInfo { get;  }

        /// <summary>
        /// 获取最后一次发送请求的时间。
        /// EN: Gets the time at which the suite last sent a request.
        /// </summary>
        public DateTime LastActivateTime { get; }

        /// <summary>
        /// 发送无参数请求并返回调用结果。
        /// EN: Sends an argument-free request and returns its invocation result.
        /// </summary>
        /// <param name="endPoint">服务与方法入口。EN: The service and method endpoint.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public Task<DubboRequestResult> SendQuery(string endPoint);

        /// <summary>
        /// 使用由具体传输协议解释的原始请求文本发送请求。
        /// EN: Sends a request using raw request text interpreted by the selected transport.
        /// </summary>
        /// <param name="endPoint">服务与方法入口。EN: The service and method endpoint.</param>
        /// <param name="req">原始请求文本。EN: The raw request text.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public Task<DubboRequestResult> SendQuery(string endPoint, string req);

        /// <summary>
        /// 发送请求并将结果反序列化为指定类型。
        /// EN: Sends a request and deserializes its result as the specified response type.
        /// </summary>
        /// <typeparam name="T_Rsp">响应模型类型。EN: The response model type.</typeparam>
        /// <param name="endPoint">服务与方法入口。EN: The service and method endpoint.</param>
        /// <param name="req">原始请求文本。EN: The raw request text.</param>
        /// <returns>包含响应模型的请求结果。EN: The invocation result containing the response model.</returns>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req);

        /// <summary>
        /// 发送无参数请求并将结果反序列化为指定类型。
        /// EN: Sends an argument-free request and deserializes its result as the specified response type.
        /// </summary>
        /// <typeparam name="T_Rsp">响应模型类型。EN: The response model type.</typeparam>
        /// <param name="endPoint">服务与方法入口。EN: The service and method endpoint.</param>
        /// <returns>包含响应模型的请求结果。EN: The invocation result containing the response model.</returns>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint);

        /// <summary>
        /// 发送一个强类型请求参数，并将结果反序列化为指定类型。
        /// EN: Sends one strongly typed argument and deserializes the result as the specified response type.
        /// </summary>
        /// <typeparam name="T_Rsp">响应类型。EN: The response type.</typeparam>
        /// <typeparam name="T_Req">请求参数类型。EN: The request argument type.</typeparam>
        /// <param name="endPoint">服务与方法入口。EN: The service and method endpoint.</param>
        /// <param name="req">请求参数。EN: The request argument.</param>
        /// <returns>包含响应模型的请求结果。EN: The invocation result containing the response model.</returns>
        public  Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(string endPoint, T_Req req);

        /// <summary>
        /// 发送两个强类型参数。
        /// EN: Sends two strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2>(string endPoint, T_Req1 req1, T_Req2 req2);
        /// <summary>
        /// 发送三个强类型参数。
        /// EN: Sends three strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3);
        /// <summary>
        /// 发送四个强类型参数。
        /// EN: Sends four strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4);
        /// <summary>
        /// 发送五个强类型参数。
        /// EN: Sends five strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5);
        /// <summary>
        /// 发送六个强类型参数。
        /// EN: Sends six strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6);
        /// <summary>
        /// 发送七个强类型参数。
        /// EN: Sends seven strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7);
        /// <summary>
        /// 发送八个强类型参数。
        /// EN: Sends eight strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8);
        /// <summary>
        /// 发送九个强类型参数。
        /// EN: Sends nine strongly typed arguments.
        /// </summary>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9);
    }
}
