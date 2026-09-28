using DubboNet.DubboService.DataModle;
using DubboNet.DubboService.DataModle.DubboInfo;
using Microsoft.VisualBasic;
using MyCommonHelper;
using NetService.Telnet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static NetService.Telnet.ExTelnet;

namespace DubboNet.DubboService
{
    /// <summary>
    /// 通过 Dubbo Telnet 命令连接 Provider、发送调用并读取诊断信息的执行器。
    /// EN: An actuator that connects to a provider through Dubbo Telnet commands, sends invocations, and reads diagnostics.
    /// </summary>
    public class DubboActuator : IDisposable, ICloneable
    {
        /// <summary>
        /// 表示 Dubbo Telnet 执行器的连接状态。
        /// EN: Represents the connection state of a Dubbo Telnet actuator.
        /// </summary>
        public enum DubboActuatorStatus
        {
            /// <summary>
            /// 未连接。
            /// EN: The actuator is disconnected.
            /// </summary>
            DisConnect = -1,
            /// <summary>
            /// 正在连接。
            /// EN: The actuator is connecting.
            /// </summary>
            Connecting = 0,
            /// <summary>
            /// 已连接。
            /// EN: The actuator is connected.
            /// </summary>
            Connected = 1
        }


        /// <summary>
        /// 获取保存历史结果文本的共享列表。
        /// EN: Gets the shared list used to retain historical result text.
        /// </summary>
        public static List<string> ResultList;

        static DubboActuator()
        {
            ResultList = new List<string>();
        }

        private const int _dubboTelnetKeepAliveTime = 10000; //TCP 保活计时器 (TCP 自己的心跳维持，默认2h)
        private const int _dubboTelnetTelnetAlivePeriod = 30000; //telnet 业务保护间隔 （发送一个业务命令，防止服务器主动断开）
        private const int _dubboTelnetConnectTimeOut = 5000; //应用侧连接主机时等待的最大超时时间，默认为0表示默认值不设置默认超时将会是2MSL （MSL根据操作系统不同实现会有差距普遍会超过30s，使用不设置该项超时等待时间会超过1min）
        private const int _dubboTelnetReceiveBuffLength = 1024 * 8; //telnet内部接收缓存大小
        private const int _dubboTelnetMaxMaintainDataLength = 1024 * 1024 * 8; //telnet命令返回接收缓存大小, dubbo 响应默认最大返回为8MB
        private const string _dubboTelnetDefautExpectPattern = "dubbo>";


        private ExTelnet dubboTelnet;
        private AutoResetEvent sendQueryAutoResetEvent = null;
        private bool _isConnecting = false;

        /// <summary>
        /// 获取最近的错误信息(仅用于同步调用)
        /// EN: Gets the most recent error message; intended for synchronous status inspection.
        /// </summary>
        public string NowErrorMes { get; private set; }

        /// <summary>
        /// 是否在外部调用队列中（内部属性，目前仅用在派生类DubboActuatorSuite中以精确标记DubboActuator被使用的状态）
        /// 不完全依靠IsQuerySending是因为在短时高并的场景下不适用（不同task以IsQuerySending确认后并不会锁定资源）
        /// EN: Tracks whether this actuator is reserved by the derived pool; IsQuerySending alone cannot reserve it atomically under bursts of concurrency.
        /// </summary>
        internal bool IsInUsedQueue{ get;  set; }=false;

        /// <summary>
        /// 获取DubboActuator唯一GUID
        /// EN: Gets the unique identifier of this DubboActuator instance.
        /// </summary>
        public Guid DubboActuatorGUID { get; private set; } = Guid.NewGuid();

        /// <summary>
        /// 当前DubboActuator备注名称（非必要信息，主要用于多DubboActuator场景下的区分）
        /// EN: Gets or sets an optional display name used to distinguish actuator instances.
        /// </summary>
        public string RemarkName { get; set; } = "DubboActuator";

        /// <summary>
        /// 当前Dubbo服务Host地址
        /// EN: Gets the host address of the current Dubbo provider.
        /// </summary>
        public string DubboHost { get; private set; }

        /// <summary>
        /// 当前Dubbo服务Port端口
        /// EN: Gets the port of the current Dubbo provider.
        /// </summary>
        public int DubboPort { get; private set; }

        /// <summary>
        /// DubboRequest的最大等待时间（Dubbo服务默认的超时时间是 1000 毫秒，这个值建议设置大于等于Dubbo服务默认的超时时间）
        /// EN: Gets the maximum request wait time in milliseconds; it should normally be no shorter than the provider timeout.
        /// </summary>
        public int DubboRequestTimeout { get; private set; }


        /// <summary>
        /// 当前Dubbo服务默认服务名称
        /// EN: Gets the default service name for the current Dubbo provider.
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 最后激活时间，标记最后一次向dubbo服务发送业务请求的时间（连接、关闭连接不属于业务请求不更新LastActivateTime）
        /// EN: Gets the time of the last business request; connecting and disconnecting do not update this value.
        /// </summary>
        public DateTime LastActivateTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 当前DubboActuator是否处于连接状态
        /// EN: Gets whether the actuator is currently connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (dubboTelnet == null)
                {
                    return false;
                }
                return dubboTelnet.IsConnected;
            }
        }

        /// <summary>
        /// 获取当前DubboActuator状态
        /// EN: Gets the current actuator connection state.
        /// </summary>
        public DubboActuatorStatus State
        {
            get
            {
                if (IsConnected)
                {
                    return DubboActuatorStatus.Connected;
                }
                else
                {
                    return _isConnecting ? DubboActuatorStatus.Connecting : DubboActuatorStatus.DisConnect;
                }
            }
        }

        /// <summary>
        /// 获取当前DubboActuator是否处于请求发送中状态
        /// EN: Gets whether the actuator is currently connecting or sending a request.
        /// </summary>
        public bool IsQuerySending
        {
            get
            {
                //正在连接（在高并发下可能会有正在连接的节点被查询到）
                if(_isConnecting)
                {
                    return true;
                }
                //从未连接过，直接返回
                if (!IsConnected || sendQueryAutoResetEvent == null)
                {
                    return false;
                }
                bool getSignal = sendQueryAutoResetEvent.WaitOne(0);
                if (getSignal)
                {
                    sendQueryAutoResetEvent.Set();
                }
                return !getSignal;
            }
        }

        /// <summary>
        /// DubboActuator构造函数
        /// EN: Initializes a Dubbo Telnet actuator with a provider address, port, timeout, and optional default service.
        /// </summary>
        /// <param name="address">节点地址（IP 地址）。EN: The provider IP address.</param>
        /// <param name="port">节点端口号。EN: The provider port.</param>
        /// <param name="commandTimeout">请求超时时间（毫秒）。EN: The request timeout in milliseconds.</param>
        /// <param name="defaultServiceName">默认服务名称（默认为 null）。EN: The optional default service name.</param>
        public DubboActuator(string address, int port, int commandTimeout = 10 * 1000, string defaultServiceName = null)
        {
            DubboHost = address;
            DubboPort = port;
            DubboRequestTimeout = commandTimeout;

            dubboTelnet = new ExTelnet(address, port, commandTimeout);
            dubboTelnet.DefautExpectPattern = _dubboTelnetDefautExpectPattern;
            dubboTelnet.ReceiveBuffLength = _dubboTelnetReceiveBuffLength;
            dubboTelnet.IsSaveTerminalData = false;
            dubboTelnet.MaxMaintainDataLength = _dubboTelnetMaxMaintainDataLength; //dubbo 默认最大返回为8MB
        }

        /// <summary>
        /// 使用网络端点、超时时间和可选默认服务初始化 Dubbo Telnet 执行器。
        /// EN: Initializes a Dubbo Telnet actuator with a network endpoint, timeout, and optional default service.
        /// </summary>
        /// <param name="iPEndPoint">Provider 网络端点。EN: The provider network endpoint.</param>
        /// <param name="commandTimeout">请求超时时间（毫秒）。EN: The request timeout in milliseconds.</param>
        /// <param name="defaultServiceName">默认服务名称（默认为 null）。EN: The optional default service name.</param>
        public DubboActuator(IPEndPoint iPEndPoint, int commandTimeout = 10 * 1000, string defaultServiceName = null):this(iPEndPoint.Address.ToString(), iPEndPoint.Port, commandTimeout, defaultServiceName)
        {
        }


        /// <summary>
        /// 连接DubboActuator (使用时可以不用调用，会在需要的时候自动连接)
        /// EN: Connects the actuator; explicit connection is optional because requests connect automatically when needed.
        /// </summary>
        /// <returns>是否连接成功（失败时可查看 NowErrorMes）。EN: True when connected; inspect NowErrorMes on failure.</returns>
        public async Task<bool> Connect()
        {
            if(_isConnecting)
            {
                NowErrorMes = "another task is connecting";
                return false;
            }
            //return telnet.Connect();
            _isConnecting = true;
            if (await dubboTelnet.ConnectAsync(_dubboTelnetKeepAliveTime, _dubboTelnetTelnetAlivePeriod, _dubboTelnetConnectTimeOut))
            {
                _isConnecting = false;
                //重置上一个dubboTelnet的发送等待信号(如果信号器没有销毁)
                if (sendQueryAutoResetEvent != null)
                {
                    while (!sendQueryAutoResetEvent.WaitOne(0))
                    {
                        sendQueryAutoResetEvent.Set();
                    }
                }
                sendQueryAutoResetEvent = new AutoResetEvent(true);
                return true;
            }
            NowErrorMes = dubboTelnet.NowErrorMes;
            _isConnecting = false;
            return false;
        }

        /// <summary>
        /// 断开连接(不用手动调用除非有场景需要暂时临时断开，在未来将再次连接，Dispose释放时自动调用，如果想主动退出可以先调用ExitAsync以完成更平滑的退出)
        /// EN: Disconnects the actuator; Dispose disconnects automatically, while ExitAsync performs a graceful exit first.
        /// </summary>
        public void DisConnect()
        {
            dubboTelnet?.DisConnect();
            //重置上一个dubboTelnet的发送等待信号
            if (sendQueryAutoResetEvent != null)
            {
                while (!sendQueryAutoResetEvent.WaitOne(0))
                {
                    sendQueryAutoResetEvent.Set();
                }
            }
            sendQueryAutoResetEvent = null;
        }

        /// <summary>
        ///  发送Query请求[返回DubboRequestResult结果](返回不会为null，dubboRequestResult.ServiceElapsed 为 -1 时即代表错误，通过dubboRequestResult.ErrorMeaasge获取错误详情)
        /// EN: Sends a Telnet invoke request and returns a non-null DubboRequestResult; ServiceElapsed -1 indicates failure.
        /// </summary>
        /// <param name="endPoint">服务入口。EN: The service and method endpoint.</param>
        /// <param name="req">以逗号分隔的 JSON 参数文本；无参数时使用空字符串。EN: Comma-separated JSON argument text, or an empty string for no arguments.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public async Task<DubboRequestResult> SendQuery(string endPoint, string req)
        {
            DubboRequestResult dubboRequestResult = new DubboRequestResult();
            TelnetRequestResult queryResult = await SendCommandAsync($"invoke {endPoint}({req})");
            if (queryResult != null)
            {
                if (queryResult.IsGetTargetIdentification)
                {
                    dubboRequestResult = DubboRequestResult.GetRequestResultFormStr(queryResult.Result);
                }
                else
                {
                    dubboRequestResult.Result = queryResult.Result;
                    dubboRequestResult.ServiceElapsed = -1;
                    dubboRequestResult.ErrorMeaasge = $"can not get the end flag of the request,it may has more data for this request\r\n{queryResult.Result}";
                }
                dubboRequestResult.RequestElapsed = (int)queryResult.ElapsedMilliseconds;
            }
            else
            {
                dubboRequestResult.Result = string.Empty;
                dubboRequestResult.ServiceElapsed = -1;
                dubboRequestResult.RequestElapsed = -1;
                dubboRequestResult.ErrorMeaasge = $"queryResult is null \r\nlast error:{NowErrorMes}";
            }
            return dubboRequestResult;
        }

        /// <summary>
        ///  发送Query请求[返回DubboRequestResult结果](返回不会为null，dubboRequestResult.ServiceElapsed 为 -1 时即代表错误，通过dubboRequestResult.ErrorMeaasge获取错误详情)
        /// EN: Sends an argument-free Telnet invoke request and returns a non-null DubboRequestResult.
        /// </summary>
        /// <param name="endPoint">服务入口。EN: The service and method endpoint.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public async Task<DubboRequestResult> SendQuery(string endPoint)
        {
            return await SendQuery(endPoint, "");
        }

        /// <summary>
        /// 发送Query请求，并将返回指定类型的结构化数据[返回 DubboRequestResult&lt;T_Rsp&gt; 结果]
        /// EN: Sends a Telnet invoke request and deserializes the response into the specified model type.
        /// </summary>
        /// <typeparam name="T_Rsp">响应模型类型。EN: The response model type.</typeparam>
        /// <param name="endPoint">服务入口。EN: The service and method endpoint.</param>
        /// <param name="req">以逗号分隔的 JSON 参数文本；无参数时使用空字符串。EN: Comma-separated JSON argument text, or an empty string for no arguments.</param>
        /// <returns>包含响应模型的请求结果。EN: The invocation result containing the response model.</returns>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req)
        {
            DubboRequestResult sourceDubboResult = await SendQuery(endPoint, req);
            DubboRequestResult<T_Rsp> dubboRequestResult  = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }

        /// <summary>
        /// 发送Query请求，并将返回指定类型的结构化数据[返回 DubboRequestResult&lt;T_Rsp&gt; 结果](请求为无请求参数的版本)
        /// EN: Sends an argument-free request and deserializes the response into the specified model type.
        /// </summary>
        /// <typeparam name="T_Rsp">响应模型类型。EN: The response model type.</typeparam>
        /// <param name="endPoint">服务入口。EN: The service and method endpoint.</param>
        /// <returns>包含响应模型的请求结果。EN: The invocation result containing the response model.</returns>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            return await SendQuery<T_Rsp>(endPoint, "");
        }

        /// <summary>
        /// 发送指定类型的结构化数据Query请求，并将返回指定类型的结构化数据[返回 DubboRequestResult&lt;T_Rsp&gt; 结果]
        /// EN: Serializes one strongly typed argument, sends the request, and deserializes the response model.
        /// </summary>
        /// <typeparam name="T_Rsp">响应类型。EN: The response type.</typeparam>
        /// <typeparam name="T_Req">请求类型。EN: The request argument type.</typeparam>
        /// <param name="endPoint">服务入口。EN: The service and method endpoint.</param>
        /// <param name="req">请求参数。EN: The request argument.</param>
        /// <returns>包含响应模型的请求结果。EN: The invocation result containing the response model.</returns>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp,T_Req>(string endPoint, T_Req req)
        {
            DubboRequestResult<T_Rsp> dubboRequestResult = null;
            string requestStr = null;
            try
            {
                //单独的基础类型也是json，可以被正常序列化
                //if (IsSimple(typeof(T_Req)))
                //{
                //    requestStr = req.ToString();
                //}
                requestStr = JsonSerializer.Serialize<T_Req>(req);
            }
            catch (Exception ex)
            {
                dubboRequestResult = new DubboRequestResult<T_Rsp>()
                {
                    ErrorMeaasge = ex.Message,
                    ServiceElapsed = -1,
                    RequestElapsed = -1,
                };
                MyLogger.LogError("DoRequestAsync fail in T_Req JsonSerializer.Serialize", ex);
                return dubboRequestResult;
            }
            
            DubboRequestResult sourceDubboResult = await SendQuery(endPoint , requestStr);
            dubboRequestResult = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }

        /// <summary>
        /// 发送两个强类型参数并反序列化响应。
        /// EN: Sends two strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp,T_Req1, T_Req2  >(string endPoint, T_Req1 req1, T_Req2 req2)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)}");
        }
        /// <summary>
        /// 发送三个强类型参数并反序列化响应。
        /// EN: Sends three strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp,T_Req1, T_Req2, T_Req3 >(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)}");
        }
        /// <summary>
        /// 发送四个强类型参数并反序列化响应。
        /// EN: Sends four strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)}");
        }
        /// <summary>
        /// 发送五个强类型参数并反序列化响应。
        /// EN: Sends five strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)}");
        }
        /// <summary>
        /// 发送六个强类型参数并反序列化响应。
        /// EN: Sends six strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)}");
        }
        /// <summary>
        /// 发送七个强类型参数并反序列化响应。
        /// EN: Sends seven strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)}");
        }
        /// <summary>
        /// 发送八个强类型参数并反序列化响应。
        /// EN: Sends eight strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7,T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)}");
        }
        /// <summary>
        /// 发送九个强类型参数并反序列化响应。
        /// EN: Sends nine strongly typed arguments and deserializes the response.
        /// </summary>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)},{JsonSerializer.Serialize<T_Req9>(req9)}");
        }

        /// <summary>
        ///  发送Query请求[直接返回原始报文字符串]（弃用）
        /// EN: Sends a query and returns the raw response text; this API is deprecated.
        /// </summary>
        /// <param name="endPoint">服务入口。EN: The service and method endpoint.</param>
        /// <param name="req">请求参数文本。EN: The request argument text.</param>
        /// <returns>原始响应文本。EN: The raw response text.</returns>
        public async Task<string> SendRawQuery(string endPoint, string req)
        {
            TelnetRequestResult queryResult = await SendCommandAsync($"invoke {endPoint}({req})");
            MyLogger.LogDiagnostics($"[DoRequestAsync]: {queryResult?.ElapsedMilliseconds} ms", "SendQuery");
            if (queryResult == null)
            {
                return $"[error:{NowErrorMes}]";
            }
            else
            {
                if (queryResult.IsGetTargetIdentification)
                {
                    return queryResult.Result;
                }
                else
                {
                    return $"可能存在未能接收的数据\r\n{queryResult.Result}";
                }
            }
        }

        /// <summary>
        /// 执行telnet DoRequestAsync （原数据请求，所有请求，包括诊断类型请求最终都会使用该入口发送网络数据）
        /// EN: Sends a raw Telnet command; both invocation and diagnostic requests ultimately use this transport entry point.
        /// </summary>
        /// <param name="command">命令内容</param>
        /// <param name="isDiagnosisCommand">是否为诊断命令，默认false（内部包装好的的非invoke控制命令）</param>
        /// <returns>返回结果，如果为null表示执行失败（错误请查看NowErrorMes）</returns>
        internal virtual async Task<TelnetRequestResult> SendCommandAsync(string command ,bool isDiagnosisCommand = false)
        {
            if (!IsConnected && !await Connect())
            {
                NowErrorMes = $"Connected Fail :{dubboTelnet.NowErrorMes}";
                return null;
            }
            TelnetRequestResult requestResult = null;
            try
            {
                sendQueryAutoResetEvent.WaitOne();
                LastActivateTime = DateTime.Now;
                requestResult = await dubboTelnet.DoRequestAsync(command);
            }
            catch (Exception ex)
            {
                requestResult = null;
                NowErrorMes = $"数据请求异常\r\n{ex.Message}";
            }
            finally
            {
                sendQueryAutoResetEvent.Set();
            }
            return requestResult;
        }

        /// <summary>
        ///  获取指定服务的Func列表详情（失败返回null，通过查看NowErrorMes可获取错误详情）
        /// EN: Gets method details for a service, or null on failure; inspect NowErrorMes for details.
        /// </summary>
        /// <param name="serviceName">服务名称，不能为空。EN: The non-empty service name.</param>
        /// <returns>方法列表详情。EN: The method details keyed by method signature.</returns>
        public async Task<Dictionary<string, DubboFuncInfo>> GetDubboServiceFuncAsync(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentException("[GetDubboServiceFuncAsync] serviceName is empty");
            }
            TelnetRequestResult tempResult = await SendCommandAsync($"ls -l {serviceName}",true);
            if (tempResult == null || string.IsNullOrEmpty(tempResult.Result))
            {
                NowErrorMes = "[GetDubboServiceFuncAsync] tempResult or tempResult.Result is NullOrEmpty";
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "[GetDubboServiceFuncAsync] can not get all data";
                return null;
            }
            if (tempResult.Result.StartsWith("[GetDubboServiceFuncAsync] No such service"))
            {
                NowErrorMes = tempResult.Result;
                return null;
            }
            return DubboFuncInfo.GetDubboFuncListIntro(tempResult.Result, serviceName);
        }

        /// <summary>
        /// 获取当前服务PROVIDER列表
        /// EN: Gets the provider list exposed by the current Dubbo Telnet endpoint.
        /// </summary>
        /// <returns>Provider 列表信息。EN: The provider list information.</returns>
        public async Task<DubboLsInfo> GetDubboLsInfoAsync()
        {
            TelnetRequestResult tempResult = await SendCommandAsync($"ls",true);
            if (tempResult == null)
            {
                NowErrorMes = "[GetDubboLsInfoAsync] SendCommandAsync fail";
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "[GetDubboLsInfoAsync] can not get all data";
                return null;
            }
            DubboLsInfo dubboLsInfo = DubboLsInfo.GetDubboLsInfo(tempResult.Result);
            dubboLsInfo.SetDubboActuatorInfo(this);
            return dubboLsInfo;
        }

        /// <summary>
        /// 获取端口上的连接详细信息
        /// EN: Gets detailed connection information for the current provider port.
        /// </summary>
        /// <returns>连接信息。EN: The connection information.</returns>
        public async Task<DubboPsInfo> GetDubboPsInfoAsync()
        {
            TelnetRequestResult tempResult = await SendCommandAsync($"ps -l {DubboPort}",true);
            if (tempResult == null)
            {
                NowErrorMes = "[GetDubboPsInfoAsync] SendCommandAsync fail";
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "[GetDubboPsInfoAsync] can not get all data";
                return null;
            }
            DubboPsInfo dubboPsInfo = DubboPsInfo.GetDubboPsInfo(tempResult.Result);
            dubboPsInfo.SetDubboActuatorInfo(this);
            return dubboPsInfo;
        }

        /// <summary>
        /// 获取状态信息
        /// EN: Gets detailed status information from the current provider.
        /// </summary>
        /// <returns>Provider 状态信息。EN: The provider status information.</returns>
        public async Task<DubboStatusInfo> GetDubboStatusInfoAsync()
        {
            TelnetRequestResult tempResult = await SendCommandAsync($"status -l",true);
            if (tempResult == null)
            {
                NowErrorMes = "[GetDubboStatusInfoAsync] SendCommandAsync fail";
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "[GetDubboStatusInfoAsync] can not get all data";
                return null;
            }
            DubboStatusInfo dubboStatusInfo = DubboStatusInfo.GetDubboStatusInfo(tempResult.Result);
            dubboStatusInfo.SetDubboActuatorInfo(this);
            return dubboStatusInfo;
        }

        /// <summary>
        /// 获取指定服务方法的TraceInfo (失败返回null)(因为获取trace可能耗时比较长，会启一个独立的DubboActuator，获取完成后自动释放)
        /// EN: Traces a service method using a temporary actuator and returns null on failure or timeout.
        /// </summary>
        /// <param name="serviceName">服务名称。EN: The service name.</param>
        /// <param name="methodName">方法名称。EN: The method name.</param>
        /// <param name="timeoutSecond">最长跟踪时间（秒）。EN: The maximum trace duration in seconds.</param>
        /// <returns>方法跟踪信息，失败时为 null。EN: The method trace information, or null on failure.</returns>
        public async Task<DubboFuncTraceInfo> GetDubboFuncTraceInfoAsync(string serviceName, string methodName, int timeoutSecond = 300)
        {
            DubboActuator innerDubboActuator = new DubboActuator(dubboTelnet.TelnetEndPoint.Address.ToString(), dubboTelnet.TelnetEndPoint.Port, 120 * 1000);
            DubboFuncTraceInfo dubboFuncTraceInfo = null;
            if (await innerDubboActuator.Connect())
            {
                DateTime endTime = DateTime.Now.AddSeconds(timeoutSecond);
                while (DateTime.Now < endTime)
                {
                    TelnetRequestResult requestResult = await innerDubboActuator.SendCommandAsync($"trace {serviceName} {methodName} 1",true);
                    if (requestResult != null)
                    {
                        dubboFuncTraceInfo = DubboFuncTraceInfo.GetTraceInfo(requestResult.Result);
                        dubboFuncTraceInfo.SetDubboActuatorInfo(this);
                    }
                    if (dubboFuncTraceInfo != null)
                    {
                        dubboFuncTraceInfo.ServiceName = serviceName;
                        dubboFuncTraceInfo.MethodName = methodName;
                        break;
                    }
                }
            }
            innerDubboActuator.Dispose();
            return dubboFuncTraceInfo;
        }

        /// <summary>
        /// 获取指定服务方法的TraceInfo (失败返回null)(因为获取trace可能耗时比较长，会启一个独立的DubboActuator，获取完成后自动释放)
        /// EN: Traces a fully qualified service-method endpoint using a temporary actuator.
        /// </summary>
        /// <param name="dubboEndPoint">完整服务方法入口。EN: The fully qualified service and method endpoint.</param>
        /// <param name="timeoutSecond">最长跟踪时间（秒）。EN: The maximum trace duration in seconds.</param>
        /// <returns>方法跟踪信息，失败时为 null。EN: The method trace information, or null on failure.</returns>
        public async Task<DubboFuncTraceInfo> GetDubboFuncTraceInfoAsync(string dubboEndPoint, int timeoutSecond = 180)
        {
            if (!(dubboEndPoint?.Contains('.') == true))
            {
                return null;
            }
            int spitIndex = dubboEndPoint.LastIndexOf(".");
            return await GetDubboFuncTraceInfoAsync(dubboEndPoint.Substring(0, spitIndex), dubboEndPoint.Substring(spitIndex + 1), timeoutSecond);
        }

        /// <summary>
        /// 判断当前类型是否为简单类型（简单类型可以不用json序列化，直接ToString就可以了）
        /// EN: Determines whether a type is a simple scalar type that can be represented directly as text.
        /// </summary>
        /// <param name="type">待检查的类型。EN: The type to inspect.</param>
        /// <returns>若为简单类型则返回 true。EN: True when the type is considered simple.</returns>
        public static bool IsSimple(Type type)
        {
            return type.IsPrimitive 
                || type.IsEnum
                || type.Equals(typeof(string))
                || type.Equals(typeof(decimal))
                || type.Equals(typeof(DateTime))
                || type.Equals(typeof(TimeSpan));
        }
        

        /// <summary>
        /// 主动关闭 Dubbo Telnet
        /// EN: Gracefully exits and closes the Dubbo Telnet connection.
        /// </summary>
        /// <returns>表示异步退出操作的任务。EN: A task representing the asynchronous exit operation.</returns>
        public async ValueTask ExitAsync()
        {
            if (IsConnected)
            {
                await dubboTelnet.WriteLineAsync("exit");
            }
            DisConnect();
        }

        /// <summary>
        /// 返回包含备注名称及 Provider 地址的可读文本。
        /// EN: Returns readable text containing the display name and provider address.
        /// </summary>
        /// <returns>执行器摘要。EN: An actuator summary.</returns>
        public override string ToString()
        {
            return $"[{RemarkName}]-{DubboHost}:{DubboPort}";
        }

        /// <summary>
        /// 断开连接并释放底层 Telnet 资源。
        /// EN: Disconnects and releases the underlying Telnet resources.
        /// </summary>
        public void Dispose()
        {
            
            DisConnect();
            dubboTelnet?.Dispose();
        }

        /// <summary>
        /// deep clone （使用当前配置返回一个新的DubboActuator）
        /// EN: Creates a new DubboActuator using the current endpoint and timeout configuration.
        /// </summary>
        /// <returns>新的执行器实例。EN: A new actuator instance.</returns>
        public object Clone()
        {
            return new DubboActuator(dubboTelnet.TelnetEndPoint.Address.ToString(), dubboTelnet.TelnetEndPoint.Port, dubboTelnet.DefaWaitTimeout);
        }
    }
}
