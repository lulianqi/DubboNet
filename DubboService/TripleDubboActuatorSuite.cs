using DubboNet.Clients.DataModle;
using DubboNet.DubboService.DataModle;
using DubboNet.DubboService.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.DubboService
{
    /// <summary>
    /// 通过 Dubbo 3.3 引入的 HTTP/JSON 兼容端点调用 Dubbo Triple Java 接口一元方法。
    /// EN: Invokes Dubbo Triple Java-interface unary methods through the HTTP/JSON
    /// compatibility endpoint introduced by Dubbo 3.3.
    /// </summary>
    /// <remarks>
    /// 第一期实现仅面向使用 <c>application/json</c> 的 Java 接口一元调用，不包含
    /// Protobuf IDL、流式调用和带帧的 gRPC 线协议。
    /// EN: This first implementation intentionally targets unary Java-interface calls
    /// with <c>application/json</c>. Protobuf IDL, streaming and the framed gRPC
    /// wire format are outside its scope.
    /// </remarks>
    public sealed class TripleDubboActuatorSuite :
        IDubboActuatorSuite,
        IDubboGenericInvocationActuator
    {
        private const string ServiceVersionHeader = "tri-service-version";
        private const string ServiceGroupHeader = "tri-service-group";
        private const string ServiceTimeoutHeader = "tri-service-timeout";

        private static readonly HashSet<string> ReservedRequestHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "host",
                "content-length",
                "content-type",
                "grpc-timeout",
                ServiceVersionHeader,
                ServiceGroupHeader,
                ServiceTimeoutHeader
            };

        private sealed class ServiceMetadata
        {
            public string Version { get; set; }
            public string Group { get; set; }
            public int? Timeout { get; set; }
        }

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly int _requestTimeoutMilliseconds;
        private readonly ConcurrentDictionary<string, ServiceMetadata> _services =
            new ConcurrentDictionary<string, ServiceMetadata>(StringComparer.Ordinal);
        private bool _disposed;

        /// <inheritdoc/>
        public DubboActuatorProtocolType ProtocolType => DubboActuatorProtocolType.Triple;

        /// <inheritdoc/>
        public string ServiceFuncSpit => "/";

        /// <inheritdoc/>
        public DubboActuatorSuiteStatus ActuatorSuiteStatusInfo { get; private set; } =
            new DubboActuatorSuiteStatus();

        /// <inheritdoc/>
        public DateTime LastActivateTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 获取默认的 Java 服务接口名。
        /// EN: Gets the default Java service interface name.
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 使用指定地址和端口创建 Triple HTTP/JSON 执行器。
        /// EN: Creates a Triple HTTP/JSON actuator from an address and port.
        /// </summary>
        /// <param name="address">Provider 地址。EN: Provider address.</param>
        /// <param name="port">Provider 端口。EN: Provider port.</param>
        /// <param name="dubboActuatorSuiteConf">执行器配置。EN: Actuator configuration.</param>
        public TripleDubboActuatorSuite(
            string address,
            int port,
            DubboActuatorSuiteConf dubboActuatorSuiteConf = null)
            : this(
                CreateHttpClient(address, port, dubboActuatorSuiteConf),
                dubboActuatorSuiteConf,
                true)
        {
        }

        /// <summary>
        /// 使用网络端点创建 Triple HTTP/JSON 执行器。
        /// EN: Creates a Triple HTTP/JSON actuator from a network endpoint.
        /// </summary>
        /// <param name="endPoint">Provider 网络端点。EN: Provider network endpoint.</param>
        /// <param name="dubboActuatorSuiteConf">执行器配置。EN: Actuator configuration.</param>
        public TripleDubboActuatorSuite(
            IPEndPoint endPoint,
            DubboActuatorSuiteConf dubboActuatorSuiteConf = null)
            : this(
                endPoint?.Address.ToString()
                    ?? throw new ArgumentNullException(nameof(endPoint)),
                endPoint.Port,
                dubboActuatorSuiteConf)
        {
        }

        internal TripleDubboActuatorSuite(
            HttpClient httpClient,
            DubboActuatorSuiteConf dubboActuatorSuiteConf = null)
            : this(httpClient, dubboActuatorSuiteConf, false)
        {
        }

        private TripleDubboActuatorSuite(
            HttpClient httpClient,
            DubboActuatorSuiteConf dubboActuatorSuiteConf,
            bool ownsHttpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (_httpClient.BaseAddress == null)
            {
                throw new ArgumentException(
                    "Triple HttpClient must define BaseAddress.",
                    nameof(httpClient));
            }

            DubboActuatorSuiteConf configuration =
                dubboActuatorSuiteConf ?? new DubboActuatorSuiteConf();
            DefaultServiceName = configuration.DefaultServiceName;
            _requestTimeoutMilliseconds = Math.Max(1, configuration.DubboRequestTimeout);
            _ownsHttpClient = ownsHttpClient;
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
                Timeout = endpoint.Timeout
            };
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult> SendQuery(string endPoint)
        {
            return SendQuery(endPoint, string.Empty);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public Task<DubboRequestResult> SendQuery(DubboInvocation invocation)
        {
            return SendQuery(invocation, CancellationToken.None);
        }

        /// <inheritdoc/>
        public async Task<DubboRequestResult> SendQuery(
            DubboInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TripleDubboActuatorSuite));
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
            string version = FirstNonBlank(invocation.Version, metadata?.Version);
            string group = FirstNonBlank(invocation.Group, metadata?.Group);
            int timeoutMilliseconds = Math.Max(
                1,
                metadata?.Timeout ?? _requestTimeoutMilliseconds);

            string requestPath = $"/{Uri.EscapeDataString(invocation.Service)}" +
                $"/{Uri.EscapeDataString(invocation.Method)}";
            string requestBody = JsonSerializer.Serialize(invocation.Arguments);

            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestPath)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };

                AddHeader(request, ServiceVersionHeader, version);
                AddHeader(request, ServiceGroupHeader, group);
                AddHeader(request, ServiceTimeoutHeader, timeoutMilliseconds.ToString());
                AddAttachments(request, invocation.Attachments);

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                string responseBody = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                result.RequestElapsed = (int)stopwatch.ElapsedMilliseconds;
                result.Result = responseBody;
                result.RawResult = ParseResponseValue(responseBody);
                result.ResponseAttachments = ReadResponseHeaders(response);

                if (!response.IsSuccessStatusCode)
                {
                    result.UpdateQueryFailed();
                    result.ErrorMeaasge = BuildHttpError(response, responseBody);
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
        public async Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(
            string endPoint,
            string req)
        {
            return new DubboRequestResult<T_Rsp>(
                await SendQuery(endPoint, req).ConfigureAwait(false));
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            return SendQuery<T_Rsp>(endPoint, string.Empty);
        }

        /// <inheritdoc/>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(
            string endPoint,
            T_Req req)
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
            string request = JsonSerializer.Serialize(arguments);
            return new DubboRequestResult<T_Rsp>(
                await SendQuery(endPoint, request).ConfigureAwait(false));
        }

        private static HttpClient CreateHttpClient(
            string address,
            int port,
            DubboActuatorSuiteConf configuration)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Provider address cannot be empty.", nameof(address));
            }

            int maxConnections = Math.Max(1, configuration?.MaxConnections ?? 20);
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = maxConnections
            };
            if ((configuration?.MasterConnectionAliveTime ?? 0) > 0)
            {
                handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(
                    configuration.MasterConnectionAliveTime);
            }

            HttpClient client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{address}:{port}"),
                Timeout = TimeSpan.FromMilliseconds(Math.Max(
                    1,
                    configuration?.DubboRequestTimeout ?? 10_000))
            };
            return client;
        }

        private static void AddHeader(
            HttpRequestMessage request,
            string name,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        private static void AddAttachments(
            HttpRequestMessage request,
            IReadOnlyDictionary<string, string> attachments)
        {
            if (attachments == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> attachment in attachments)
            {
                if (string.IsNullOrWhiteSpace(attachment.Key)
                    || attachment.Key.StartsWith(":", StringComparison.Ordinal)
                    || ReservedRequestHeaders.Contains(attachment.Key))
                {
                    continue;
                }
                request.Headers.TryAddWithoutValidation(attachment.Key, attachment.Value);
            }
        }

        private static IReadOnlyDictionary<string, object> ReadResponseHeaders(
            HttpResponseMessage response)
        {
            Dictionary<string, object> headers =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            return headers;
        }

        private static object ParseResponseValue(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return responseBody;
            }
        }

        private static string BuildHttpError(
            HttpResponseMessage response,
            string responseBody)
        {
            string status = $"Triple provider returned HTTP {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}.";
            return string.IsNullOrWhiteSpace(responseBody)
                ? status
                : $"{status} {responseBody}";
        }

        private static string FirstNonBlank(string first, string second)
        {
            return !string.IsNullOrWhiteSpace(first) ? first : second;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
