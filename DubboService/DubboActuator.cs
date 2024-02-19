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
    public class DubboActuator : IDisposable, ICloneable
    {
        public enum DubboActuatorStatus
        {
            DisConnect = -1,
            Connecting = 0,
            Connected = 1
        }


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
        private string _dubboResponseNewline = "\r\n";

        /// <summary>
        /// 获取最近的错误信息(仅用于同步调用)
        /// </summary>
        public string NowErrorMes { get; private set; }

        /// <summary>
        /// 是否在外部调用队列中（内部属性，目前仅用在派生类DubboActuatorSuite中以精确标记DubboActuator被使用的状态）
        /// 不完全依靠IsQuerySending是因为在短时高并的场景下不适用（不同task以IsQuerySending确认后并不会锁定资源）
        /// </summary>
        internal bool IsInUsedQueue{ get;  set; }=false;

        /// <summary>
        /// 获取DubboActuator唯一GUID
        /// </summary>
        public Guid DubboActuatorGUID { get; private set; } = Guid.NewGuid();

        /// <summary>
        /// 当前DubboActuator备注名称（非必要信息，主要用于多DubboActuator场景下的区分）
        /// </summary>
        public string RemarkName { get; set; } = "DubboActuator";

        /// <summary>
        /// 当前Dubbo服务Host地址
        /// </summary>
        public string DubboHost { get; private set; }

        /// <summary>
        /// 当前Dubbo服务Port端口
        /// </summary>
        public int DubboPort { get; private set; }

        /// <summary>
        /// DubboRequest的最大等待时间（Dubbo服务默认的超时时间是 1000 毫秒，这个值建议设置大于等于Dubbo服务默认的超时时间）
        /// </summary>
        public int DubboRequestTimeout { get; private set; }


        /// <summary>
        /// 当前Dubbo服务默认服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 最后激活时间，标记最后一次向dubbo服务发送业务请求的时间（连接、关闭连接不属于业务请求不更新LastActivateTime）
        /// </summary>
        public DateTime LastActivateTime { get; private set; }=default(DateTime);

        /// <summary>
        /// 当前DubboActuator是否处于连接状态
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
        /// </summary>
        /// <param name="address">节点地址（ip地址）</param>
        /// <param name="port">节点端口号</param>
        /// <param name="commandTimeout">请求超时时间</param>
        /// <param name="defaultServiceName">默认服务名称（默认为null）</param>
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

        public DubboActuator(IPEndPoint iPEndPoint, int commandTimeout = 10 * 1000, string defaultServiceName = null):this(iPEndPoint.Address.ToString(), iPEndPoint.Port, commandTimeout, defaultServiceName)
        {
        }


        /// <summary>
        /// 连接DubboActuator (使用时可以不用调用，会在需要的时候自动连接)
        /// </summary>
        /// <returns>是否连接成功（连接失败请通过NowErrorMes属性查看错误信息）</returns>
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
        /// </summary>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req">请求参数，如果有多个参数参数间用,隔开（这里是request的原始数据，实际是[par1,par2,par3]的数组形式[]不用包括中req里，里面的par是json对象）（null也是一种参数对象，没有任何参数填空""即可）</param>
        /// <returns></returns>
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
        /// </summary>
        /// <param name="endPoint">服务人口</param>
        /// <returns></returns>
        public async Task<DubboRequestResult> SendQuery(string endPoint)
        {
            return await SendQuery(endPoint, "");
        }

        /// <summary>
        /// 发送Query请求，并将返回指定类型的结构化数据[返回DubboRequestResult<T_Rsp>结果]
        /// </summary>
        /// <typeparam name="T_Rsp"></typeparam>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req">请求参数，如果有多个参数参数间用,隔开（实际是[par1,par2,par3]的数组形式[]不用包括中req里）（null也是一种参数对象，没有任何参数填空""即可）</param>
        /// <returns></returns>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req)
        {
            DubboRequestResult sourceDubboResult = await SendQuery(endPoint, req);
            DubboRequestResult<T_Rsp> dubboRequestResult  = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            return await SendQuery<T_Rsp>(endPoint, "");
        }

        /// <summary>
        /// 发送指定类型的结构化数据Query请求，并将返回指定类型的结构化数据[返回DubboRequestResult<T_Rsp>结果]
        /// </summary>
        /// <typeparam name="T_Rsp">响应类型</typeparam>
        /// <typeparam name="T_Req">请求类型</typeparam>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req"></param>
        /// <returns></returns>
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

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp,T_Req1, T_Req2  >(string endPoint, T_Req1 req1, T_Req2 req2)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp,T_Req1, T_Req2, T_Req3 >(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7,T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)}");
        }
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)},{JsonSerializer.Serialize<T_Req9>(req9)}");
        }

        /// <summary>
        ///  发送Query请求[直接返回原始报文字符串]（弃用）
        /// </summary>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req">请求参数</param>
        /// <returns></returns>
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
        /// </summary>
        /// <param name="serviceName">服务名称(不能为空，通过GetAllDubboServiceAsync可以获取可用服务名列表)</param>
        /// <returns>Func列表详情</returns>
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
        /// </summary>
        /// <returns>PROVIDER列表</returns>
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
        /// </summary>
        /// <returns></returns>
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
        /// </summary>
        /// <returns></returns>
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
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="methodName"></param>
        /// <param name="timeoutSecond"></param>
        /// <returns></returns>
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
        /// </summary>
        /// <param name="dubboEndPoint"></param>
        /// <param name="timeoutSecond"></param>
        /// <returns></returns>
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
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
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
        /// </summary>
        /// <returns></returns>
        public async ValueTask ExitAsync()
        {
            if (IsConnected)
            {
                await dubboTelnet.WriteLineAsync("exit");
            }
            DisConnect();
        }

        public override string ToString()
        {
            return $"[{RemarkName}]-{DubboHost}:{DubboPort}";
        }

        public void Dispose()
        {
            DisConnect();
            dubboTelnet?.Dispose();
        }

        /// <summary>
        /// deep clone （使用当前配置返回一个新的DubboActuator）
        /// </summary>
        /// <returns></returns>
        public object Clone()
        {
            return new DubboActuator(dubboTelnet.TelnetEndPoint.Address.ToString(), dubboTelnet.TelnetEndPoint.Port, dubboTelnet.DefaWaitTimeout);
        }
    }
}
