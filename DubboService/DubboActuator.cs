using DubboNet.DubboService.DataModle;
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
        /// 当前DubboTester是否处于连接状态
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
        /// 获取当前DubboTester状态
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
        /// 获取当前DubboTester是否出于请求发送中
        /// </summary>
        public bool IsQuerySending
        {
            get
            {
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
        /// 连接DubboTester
        /// </summary>
        /// <returns>是否连接成功（连接失败请通过NowErrorMes属性查看错误信息）</returns>
        public async Task<bool> Connect()
        {
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
        /// 断开连接
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
        /// <param name="req">请求参数</param>
        /// <returns></returns>
        public async Task<DubboRequestResult> DoRequestAsync(string endPoint, string req)
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
        /// 发送指定类型的结构化数据Query请求，并将返回指定类型的结构化数据[返回DubboRequestResult<T_Rsp>结果]
        /// </summary>
        /// <typeparam name="T_Rsp">响应类型</typeparam>
        /// <typeparam name="T_Req">请求类型</typeparam>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req"></param>
        /// <returns></returns>
        public async Task<DubboRequestResult<T_Rsp>> DoRequestAsync<T_Rsp,T_Req>(string endPoint, T_Req req)
        {
            DubboRequestResult<T_Rsp> dubboRequestResult = null;
            string requestStr = null;
            if (req == null)
            {
                requestStr = string.Empty;
            }
            try
            {
                //https://stackoverflow.com/questions/863881/how-do-i-tell-if-a-type-is-a-simple-type-i-e-holds-a-single-value
                Type typeReq = typeof(T_Req);
                if (typeReq.IsPrimitive)
                {
                    
                }
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
                MyLogger.LogError("DoRequestAsync<T_Rsp,T_Req> fail in JsonSerializer.Serialize", ex);
                return dubboRequestResult;
            }
            DubboRequestResult sourceDubboResult = await DoRequestAsync(endPoint , requestStr);
            dubboRequestResult = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }


        /// <summary>
        ///  发送Query请求[直接返回原始报文字符串]（弃用）
        /// </summary>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req">请求参数</param>
        /// <returns></returns>
        public async Task<string> SendQuery(string endPoint, string req)
        {
            TelnetRequestResult queryResult = await SendCommandAsync($"invoke {endPoint}({req})");
            MyLogger.LogDiagnostics($"[DoRequestAsync]: {queryResult.ElapsedMilliseconds} ms", "SendQuery");
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
        /// <param name="command"></param>
        /// <returns>返回结果，如果未null表示执行失败（错误请查看NowErrorMes）</returns>
        protected async Task<TelnetRequestResult> SendCommandAsync(string command)
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
                throw new ArgumentException("serviceName is empty");
            }
            TelnetRequestResult tempResult = await SendCommandAsync($"ls -l {serviceName}");
            if (tempResult == null || string.IsNullOrEmpty(tempResult.Result))
            {
                NowErrorMes = "tempResult or tempResult.Result is NullOrEmpty";
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "can not get all data";
                return null;
            }
            if (tempResult.Result.StartsWith("No such service"))
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
        public async Task<List<string>> GetAllDubboServiceAsync()
        {
            TelnetRequestResult tempResult = await SendCommandAsync($"ls");
            if (tempResult == null)
            {
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "can not get all data";
                return null;
            }
            return GetAllDubboServiceList(tempResult.Result);
        }

        /// <summary>
        /// 获取端口上的连接详细信息
        /// </summary>
        /// <returns></returns>
        public async Task<DubboPsInfo> GetDubboPsInfoAsync()
        {
            TelnetRequestResult tempResult = await SendCommandAsync($"ps -l {DubboPort}");
            if (tempResult == null)
            {
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "can not get all data";
                return null;
            }
            return DubboPsInfo.GetDubboPsInfo(tempResult.Result);
        }

        /// <summary>
        /// 获取状态信息
        /// </summary>
        /// <returns></returns>
        public async Task<DubboStatusInfo> GetDubboStatusInfoAsync()
        {
            TelnetRequestResult tempResult = await SendCommandAsync($"status -l");
            if (tempResult == null)
            {
                return null;
            }
            if (!tempResult.IsGetTargetIdentification)
            {
                NowErrorMes = "can not get all data";
                return null;
            }
            return DubboStatusInfo.GetDubboStatusInfo(tempResult.Result);
        }

        /// <summary>
        /// 获取指定服务方法的TraceInfo (失败返回null)(因为获取trace可能耗时比较长，会启一个独立的DubboTester，获取完成后自动释放)
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="methodName"></param>
        /// <param name="timeoutSecond"></param>
        /// <returns></returns>
        public async Task<DubboFuncTraceInfo> GetDubboFuncTraceInfo(string serviceName, string methodName, int timeoutSecond = 300)
        {
            DubboActuator innerDubboTester = new DubboActuator(dubboTelnet.TelnetEndPoint.Address.ToString(), dubboTelnet.TelnetEndPoint.Port, 120 * 1000);
            DubboFuncTraceInfo dubboFuncTraceInfo = null;
            if (await innerDubboTester.Connect())
            {
                DateTime endTime = DateTime.Now.AddSeconds(timeoutSecond);
                while (DateTime.Now < endTime)
                {
                    TelnetRequestResult requestResult = await innerDubboTester.SendCommandAsync($"trace {serviceName} {methodName} 1");
                    if (requestResult != null)
                    {
                        dubboFuncTraceInfo = DubboFuncTraceInfo.GetTraceInfo(requestResult.Result);
                    }
                    if (dubboFuncTraceInfo != null)
                    {
                        dubboFuncTraceInfo.ServiceName = serviceName;
                        dubboFuncTraceInfo.MethodName = methodName;
                        break;
                    }
                }
            }
            innerDubboTester.Dispose();
            return dubboFuncTraceInfo;
        }


        public async Task<DubboFuncTraceInfo> GetDubboFuncTraceInfo(string dubboEndPoint, int timeoutSecond = 180)
        {
            if (!(dubboEndPoint?.Contains('.') == true))
            {
                return null;
            }
            int spitIndex = dubboEndPoint.LastIndexOf(".");
            return await GetDubboFuncTraceInfo(dubboEndPoint.Substring(0, spitIndex), dubboEndPoint.Substring(spitIndex + 1), timeoutSecond);
        }



        

        /// <summary>
        /// 将ls 命令返回值结果解析为服务List（内部使用）
        /// </summary>
        /// <param name="lsStr"></param>
        /// <returns></returns>
        protected List<string> GetAllDubboServiceList(string lsStr)
        {
            List<string> result = new List<string>();
            if (!string.IsNullOrEmpty(lsStr) && lsStr.Contains(_dubboResponseNewline))
            {
                string[] tempLines = lsStr.Split(_dubboResponseNewline, StringSplitOptions.RemoveEmptyEntries);
                if (tempLines.Length > 1)
                {
                    bool isSatrtProvider = false;
                    for (int i = 0; i < tempLines.Length; i++)
                    {
                        if (isSatrtProvider)
                        {
                            if (tempLines[i].StartsWith("CONSUMER:"))
                            {
                                break;
                            }
                            result.Add(tempLines[i]);
                            continue;
                        }
                        else if (tempLines[i].StartsWith("PROVIDER:"))
                        {
                            isSatrtProvider = true;
                        }
                    }
                }
            }
            return result;
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

        public void Dispose()
        {
            DisConnect();
            dubboTelnet?.Dispose();
        }

        public object Clone()
        {
            return new DubboActuator(dubboTelnet.TelnetEndPoint.Address.ToString(), dubboTelnet.TelnetEndPoint.Port, dubboTelnet.DefaWaitTimeout);
        }
    }
}
