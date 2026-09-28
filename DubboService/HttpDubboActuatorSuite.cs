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
using static DubboNet.DubboService.TelnetDubboActuatorSuite;

namespace DubboNet.DubboService
{
    /// <summary>
    /// 通过 HTTP POST 和 JSON 请求体调用 HTTP 暴露的服务端点；它不是原生 Triple/gRPC 客户端。
    /// <para>EN: Invokes HTTP-exposed service endpoints with JSON POST bodies; this is not a native Triple/gRPC client.</para>
    /// </summary>
    public class HttpDubboActuatorSuite : IDubboActuatorSuite
    {
        private bool disposedValue;

        private HttpClient actuatorSuiteHttpClient = new HttpClient(new SocketsHttpHandler
        {
            //UseProxy = true,
            //Proxy = new System.Net.WebProxy("localhost", 8888),
            MaxConnectionsPerServer = 10000
        });

        /// <inheritdoc/>
        public DubboActuatorProtocolType ProtocolType => DubboActuatorProtocolType.Http;

        /// <inheritdoc/>
        public string ServiceFuncSpit => "/";

        /// <inheritdoc/>
        public DubboActuatorSuiteStatus ActuatorSuiteStatusInfo { get; private set; } = new DubboActuatorSuiteStatus();

        /// <inheritdoc/>
        public DateTime LastActivateTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 获取默认服务名称。
        /// <para>EN: Gets the default service name.</para>
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 创建 HTTP/JSON 执行器。
        /// <para>EN: Creates an HTTP/JSON actuator.</para>
        /// </summary>
        /// <param name="Address">服务地址。<para>EN: Service address.</para></param>
        /// <param name="Port">服务端口。<para>EN: Service port.</para></param>
        /// <param name="dubboActuatorSuiteConf">执行器配置。<para>EN: Actuator configuration.</para></param>
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
        /// 使用网络端点创建 HTTP/JSON 执行器。
        /// <para>EN: Creates an HTTP/JSON actuator from a network endpoint.</para>
        /// </summary>
        /// <param name="iPEndPoint">服务端点。<para>EN: Service endpoint.</para></param>
        /// <param name="dubboActuatorSuiteConf">执行器配置。<para>EN: Actuator configuration.</para></param>
        public HttpDubboActuatorSuite(IPEndPoint iPEndPoint, DubboActuatorSuiteConf dubboActuatorSuiteConf = null) : this(iPEndPoint.Address.ToString(), iPEndPoint.Port, dubboActuatorSuiteConf)
        {
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult> SendQuery(string endPoint)
        {
            return await SendQuery(endPoint, "");
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req)
        {
            DubboRequestResult sourceDubboResult = await SendQuery(endPoint, req);
            DubboRequestResult<T_Rsp> dubboRequestResult = new DubboRequestResult<T_Rsp>(sourceDubboResult);
            return dubboRequestResult;
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            return await SendQuery<T_Rsp>(endPoint, "");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(string endPoint, T_Req req)
        {
            return await SendQuery<T_Rsp>(endPoint, $"{JsonSerializer.Serialize<T_Req>(req)}");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2>(string endPoint, T_Req1 req1, T_Req2 req2)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)}]");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)}]");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)}]");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)}]");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)}]");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)}]");
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            return await SendQuery<T_Rsp>(endPoint, $"[{JsonSerializer.Serialize<T_Req1>(req1)},{JsonSerializer.Serialize<T_Req2>(req2)},{JsonSerializer.Serialize<T_Req3>(req3)},{JsonSerializer.Serialize<T_Req4>(req4)},{JsonSerializer.Serialize<T_Req5>(req5)},{JsonSerializer.Serialize<T_Req6>(req6)},{JsonSerializer.Serialize<T_Req7>(req7)},{JsonSerializer.Serialize<T_Req8>(req8)}]");
        }

        /// <inheritdoc/>
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

        /// <summary>
        /// 释放执行器持有的资源。
        /// <para>EN: Releases resources held by the actuator.</para>
        /// </summary>
        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
