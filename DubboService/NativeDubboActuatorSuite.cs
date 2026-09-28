using DubboNet.Clients.DataModle;
using DubboNet.DubboService.DataModle;
using DubboNet.DubboService.Native;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.DubboService
{
    /// <summary>
    /// 使用 Hessian2 泛化调用的原生 Dubbo2 TCP 执行器；单个 TCP 连接可按请求 ID 复用并发请求。
    /// EN: A native Dubbo2 TCP actuator using Hessian2 generic invocation. A single TCP
    /// connection can multiplex concurrent requests by request id.
    /// </summary>
    public sealed class NativeDubboActuatorSuite : IDubboActuatorSuite
    {
        private sealed class ServiceMetadata
        {
            public string Version { get; set; }
            public string Group { get; set; }
            public string Serialization { get; set; }
            public string PreferSerialization { get; set; }
        }

        private readonly NativeDubboConnection _connection;
        private readonly ConcurrentDictionary<string, ServiceMetadata> _services =
            new ConcurrentDictionary<string, ServiceMetadata>(StringComparer.Ordinal);
        private bool _disposed;

        /// <inheritdoc/>
        public DubboActuatorProtocolType ProtocolType => DubboActuatorProtocolType.NativeDubbo;
        /// <inheritdoc/>
        public string ServiceFuncSpit => ".";
        /// <inheritdoc/>
        public DubboActuatorSuiteStatus ActuatorSuiteStatusInfo { get; private set; } =
            new DubboActuatorSuiteStatus();
        /// <inheritdoc/>
        public DateTime LastActivateTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 获取默认服务接口名。
        /// EN: Gets the default service interface name.
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 使用地址、端口和可选配置初始化原生 Dubbo2 TCP 执行器。
        /// EN: Initializes a native Dubbo2 TCP actuator with an address, port, and optional configuration.
        /// </summary>
        /// <param name="address">Provider IP 地址。EN: The provider IP address.</param>
        /// <param name="port">Provider 端口。EN: The provider port.</param>
        /// <param name="dubboActuatorSuiteConf">执行器配置。EN: The actuator configuration.</param>
        public NativeDubboActuatorSuite(
            string address,
            int port,
            DubboActuatorSuiteConf dubboActuatorSuiteConf = null)
            : this(new IPEndPoint(IPAddress.Parse(address), port), dubboActuatorSuiteConf)
        {
        }

        /// <summary>
        /// 使用网络端点和可选配置初始化原生 Dubbo2 TCP 执行器。
        /// EN: Initializes a native Dubbo2 TCP actuator with a network endpoint and optional configuration.
        /// </summary>
        /// <param name="endPoint">Provider 网络端点。EN: The provider network endpoint.</param>
        /// <param name="dubboActuatorSuiteConf">执行器配置。EN: The actuator configuration.</param>
        public NativeDubboActuatorSuite(
            IPEndPoint endPoint,
            DubboActuatorSuiteConf dubboActuatorSuiteConf = null)
        {
            DubboActuatorSuiteConf configuration =
                dubboActuatorSuiteConf ?? new DubboActuatorSuiteConf();
            DefaultServiceName = configuration.DefaultServiceName;
            _connection = new NativeDubboConnection(
                endPoint,
                configuration.DubboRequestTimeout);
        }

        internal void RegisterService(DubboServiceEndPointInfo endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }
            if (string.IsNullOrWhiteSpace(endpoint.Interface))
            {
                return;
            }

            _services[endpoint.Interface] = new ServiceMetadata
            {
                Version = endpoint.Version,
                Group = endpoint.Group,
                Serialization = endpoint.Serialization,
                PreferSerialization = endpoint.PreferSerialization
            };
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult> SendQuery(string endPoint)
        {
            return SendQuery(endPoint, string.Empty);
        }

        /// <summary>
        /// 使用 JSON 参数文本发送泛化调用；直接使用此执行器时 Java 类型由 JSON 启发式推断。
        /// EN: Sends a generic invocation from JSON argument text; when this suite is used directly,
        /// Java parameter types are inferred heuristically from JSON.
        /// </summary>
        /// <param name="endPoint">服务与方法入口。EN: The service and method endpoint.</param>
        /// <param name="req">以逗号分隔的 JSON 参数文本。EN: Comma-separated JSON argument text.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public Task<DubboRequestResult> SendQuery(string endPoint, string req)
        {
            if (!string.IsNullOrWhiteSpace(DefaultServiceName)
                && !string.IsNullOrWhiteSpace(endPoint)
                && !endPoint.Contains("#")
                && !endPoint.Contains("/")
                && !endPoint.Contains("."))
            {
                endPoint = $"{DefaultServiceName}.{endPoint}";
            }
            DubboInvocation invocation = NativeDubboCodec.ParseInvocation(endPoint, req);
            return SendQuery(invocation);
        }

        /// <summary>
        /// 使用包含精确 Java 参数类型的调用描述发送泛化请求。
        /// EN: Sends a generic request using an invocation descriptor containing exact Java parameter types.
        /// </summary>
        /// <param name="invocation">调用描述。EN: The invocation descriptor.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public Task<DubboRequestResult> SendQuery(DubboInvocation invocation)
        {
            return SendQuery(invocation, CancellationToken.None);
        }

        /// <summary>
        /// 使用精确调用描述和取消标记发送泛化请求；传输异常会记录为失败结果。
        /// EN: Sends a generic request with an exact invocation descriptor and cancellation token;
        /// transport exceptions are recorded in a failed result.
        /// </summary>
        /// <param name="invocation">调用描述。EN: The invocation descriptor.</param>
        /// <param name="cancellationToken">取消标记。EN: The cancellation token.</param>
        /// <returns>请求结果。EN: The invocation result.</returns>
        public async Task<DubboRequestResult> SendQuery(
            DubboInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeDubboActuatorSuite));
            }

            invocation.Validate();
            LastActivateTime = DateTime.Now;
            Stopwatch stopwatch = Stopwatch.StartNew();
            DubboRequestResult result = new DubboRequestResult
            {
                ServiceElapsed = 0,
                ServiceElapsedAvailable = false
            };

            _services.TryGetValue(invocation.Service, out ServiceMetadata metadata);
            try
            {
                NativeDubboResponse response = await _connection.InvokeAsync(
                    invocation,
                    metadata?.Version,
                    metadata?.Group,
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                result.RequestElapsed = (int)stopwatch.ElapsedMilliseconds;
                result.RequestId = response.RequestId;
                result.ResponseStatus = response.Status;
                result.SerializationId = response.SerializationId;
                result.ResponseAttachments = response.Attachments;

                if (response.Status != NativeDubboCodec.OkStatus)
                {
                    result.UpdateQueryFailed();
                    result.ErrorMeaasge = response.ErrorMessage
                        ?? $"Dubbo provider returned response status {response.Status}.";
                    result.Result = NativeDubboCodec.SerializeJson(result.ErrorMeaasge);
                }
                else if (response.Exception != null)
                {
                    result.UpdateQueryFailed();
                    result.RawResult = response.Exception;
                    result.Result = NativeDubboCodec.SerializeJson(response.Exception);
                    result.ErrorMeaasge = NativeDubboCodec.DescribeException(response.Exception);
                }
                else
                {
                    result.RawResult = response.Value;
                    result.Result = NativeDubboCodec.SerializeJson(response.Value);
                }
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                result.RequestElapsed = (int)stopwatch.ElapsedMilliseconds;
                result.UpdateQueryFailed();
                result.ErrorMeaasge = exception.Message;
            }

            ActuatorSuiteStatusInfo.LastQueryElapsed = result.RequestElapsed;
            return result;
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req)
        {
            return new DubboRequestResult<T_Rsp>(await SendQuery(endPoint, req).ConfigureAwait(false));
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            return SendQuery<T_Rsp>(endPoint, string.Empty);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(string endPoint, T_Req req)
        {
            return SendObjects<T_Rsp>(endPoint, req);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2>(
            string endPoint, T_Req1 req1, T_Req2 req2)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3, req4);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3, req4, req5);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3, req4, req5, req6);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3, req4, req5, req6, req7);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3, req4, req5, req6, req7, req8);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(
            string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9)
        {
            return SendObjects<T_Rsp>(endPoint, req1, req2, req3, req4, req5, req6, req7, req8, req9);
        }

        private async Task<DubboRequestResult<T_Rsp>> SendObjects<T_Rsp>(
            string endPoint,
            params object[] arguments)
        {
            string request = string.Join(",", arguments.Select(argument => JsonSerializer.Serialize(argument)));
            return new DubboRequestResult<T_Rsp>(
                await SendQuery(endPoint, request).ConfigureAwait(false));
        }

        /// <summary>
        /// 释放原生 Dubbo 连接及其等待中的请求。
        /// EN: Releases the native Dubbo connection and its pending requests.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _connection.Dispose();
        }
    }
}
