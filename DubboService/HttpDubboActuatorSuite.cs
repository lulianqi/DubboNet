using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static baiyinTest0801.WebService.MyWebTool;
using static DubboNet.DubboService.TelnetDubboActuatorSuite;

namespace DubboNet.DubboService
{
    public class HttpDubboActuatorSuite : IDubboActuatorSuite
    {
        private bool disposedValue;

        private HttpClient actuatorSuiteHttpClient = new HttpClient(new SocketsHttpHandler
        {
            //UseProxy = true,
            //Proxy = new System.Net.WebProxy("localhost", 8888),
            MaxConnectionsPerServer = 10000
        });

        public DubboActuatorProtocolType ProtocolType => DubboActuatorProtocolType.Http;

        public string ServiceFuncSpit => "/";

        public DubboActuatorSuiteStatus ActuatorSuiteStatusInfo { get; private set; } = new DubboActuatorSuiteStatus();

        public DateTime LastActivateTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 获取默认服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 初始化DubboActuatorSuite
        /// </summary>
        /// <param name="Address">地址（ip）</param>
        /// <param name="Port">端口</param>
        /// <param name="CommandTimeout">客户端请求命令的超时时间（毫秒为单位，默认10秒）</param>
        /// <param name="dubboActuatorSuiteConf">DubboActuatorSuiteConf配置</param>
        public HttpDubboActuatorSuite(string Address, int Port, DubboActuatorSuiteConf dubboActuatorSuiteConf = null) 
        {
            actuatorSuiteHttpClient.BaseAddress = new Uri($"http://{Address}:{Port}");
            if (dubboActuatorSuiteConf != null)
            {
                DefaultServiceName = dubboActuatorSuiteConf.DefaultServiceName;
                if (dubboActuatorSuiteConf.DubboRequestTimeout > 0)
                {
                    actuatorSuiteHttpClient.Timeout =  TimeSpan.FromMilliseconds(dubboActuatorSuiteConf.DubboRequestTimeout);
                }
            }
        }

        /// <summary>
        /// 初始化DubboActuatorSuite
        /// </summary>
        /// <param name="iPEndPoint"></param>
        /// <param name="CommandTimeout">客户端请求命令的超时时间（毫秒为单位，默认10秒）</param>
        /// <param name="dubboActuatorSuiteConf">DubboActuatorSuiteConf配置</param>
        public HttpDubboActuatorSuite(IPEndPoint iPEndPoint, DubboActuatorSuiteConf dubboActuatorSuiteConf = null) : this(iPEndPoint.Address.ToString(), iPEndPoint.Port, dubboActuatorSuiteConf)
        {
        }

        public async Task<DubboRequestResult> SendQuery(string endPoint)
        {
            return await SendQuery(endPoint, "");
        }

        public async Task<DubboRequestResult> SendQuery(string endPoint, string req)
        {
            LastActivateTime = DateTime.Now;
            DubboRequestResult dubboRequestResult = new DubboRequestResult();
            HttpContent requestContent = new StringContent(req, Encoding.UTF8, "application/json");
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endPoint)
            {
                Content = requestContent
            };
            try
            {
                DateTime sendQueryTime = DateTime.Now;
                HttpResponseMessage httpResponse = await actuatorSuiteHttpClient.SendAsync(request);
                dubboRequestResult.UpdateServiceElapsed();
                string responseStr = await httpResponse.Content.ReadAsStringAsync();
                dubboRequestResult.UpdateRequestElapsed();
                dubboRequestResult.Result = responseStr;
                //EnsureSuccessStatusCode放在后面，为了让HttpRequestException发生时，依然可以读取responseStr
                httpResponse.EnsureSuccessStatusCode();
            }
            //catch(HttpRequestException ex)
            catch(Exception ex)
            {
                dubboRequestResult.UpdateQueryFailed();
                if (!(ex is HttpRequestException))
                {
                    dubboRequestResult.UpdateRequestElapsed();
                }
                dubboRequestResult.ErrorMeaasge = ex.Message;
            }
            //todo LastQueryElapsed是否应该只算成功的
            if (dubboRequestResult.QuerySuccess)
            {
                ActuatorSuiteStatusInfo.LastQueryElapsed = dubboRequestResult.RequestElapsed;
            }
            return dubboRequestResult;
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req)
        {
            DubboRequestResult sourceDubboResult = await SendQuery(endPoint, req);
            DubboRequestResult<T_Rsp> dubboRequestResult = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            return await SendQuery<T_Rsp>(endPoint, "");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(string endPoint, T_Req req)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req>(req)}");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2>(string endPoint, T_Req1 req1, T_Req2 req2)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)}]");
        }

        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)},{JsonSerializer.Serialize<T_Req9>(req9)}]");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)
                }

                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                disposedValue = true;
            }
        }

        // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        // ~HttpDubboActuatorSuite()
        // {
        //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
