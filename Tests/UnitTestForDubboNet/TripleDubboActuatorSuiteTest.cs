using DubboNet.Clients.DataModle;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UnitTestForDubboNet
{
    public class TripleDubboActuatorSuiteTest
    {
        [Fact]
        public async Task StructuredInvocation_UsesTripleHttpJsonShapeAndHeaders()
        {
            RecordingHandler handler = new RecordingHandler(
                HttpStatusCode.OK,
                "true");
            using HttpClient client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:50051")
            };
            using TripleDubboActuatorSuite suite = new TripleDubboActuatorSuite(
                client,
                new DubboActuatorSuiteConf { DubboRequestTimeout = 4321 });

            DubboInvocation invocation = new DubboInvocation(
                "com.example.dubbonet.api.DataTypeService",
                "negate",
                new[] { "boolean" },
                new object[] { false })
            {
                Version = "1.0.0",
                Group = "dubbonet-3.3.6",
                Attachments = new Dictionary<string, string>
                {
                    ["traceId"] = "test-trace",
                    ["content-type"] = "text/plain"
                }
            };

            DubboRequestResult result = await suite.SendQuery(invocation);

            Assert.True(result.QuerySuccess);
            Assert.Equal("true", result.Result);
            Assert.Equal(DubboActuatorProtocolType.Triple, suite.ProtocolType);
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.NotNull(handler.RequestUri);
            Assert.Equal(
                "/com.example.dubbonet.api.DataTypeService/negate",
                handler.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", handler.ContentType);
            Assert.Equal("[false]", handler.Body);
            Assert.Equal("1.0.0", handler.Headers["tri-service-version"]);
            Assert.Equal("dubbonet-3.3.6", handler.Headers["tri-service-group"]);
            Assert.Equal("4321", handler.Headers["tri-service-timeout"]);
            Assert.Equal("test-trace", handler.Headers["traceId"]);
            Assert.False(handler.Headers.ContainsKey("content-type"));
            Assert.IsType<JsonElement>(result.RawResult);
        }

        [Fact]
        public async Task RegisteredServiceMetadata_IsUsedWhenInvocationDoesNotOverrideIt()
        {
            RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "42");
            using HttpClient client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:50051")
            };
            using TripleDubboActuatorSuite suite = new TripleDubboActuatorSuite(client);
            suite.RegisterService(DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(
                new Uri(
                    "tri://127.0.0.1:50051/com.example.dubbonet.api.DataTypeService" +
                    "?interface=com.example.dubbonet.api.DataTypeService" +
                    "&version=1.0.0&group=dubbonet-3.3.6&timeout=2500")));

            DubboRequestResult result = await suite.SendQuery(new DubboInvocation(
                "com.example.dubbonet.api.DataTypeService",
                "add",
                new[] { "int", "int" },
                new object[] { 7, 35 }));

            Assert.True(result.QuerySuccess);
            Assert.Equal("[7,35]", handler.Body);
            Assert.Equal("1.0.0", handler.Headers["tri-service-version"]);
            Assert.Equal("dubbonet-3.3.6", handler.Headers["tri-service-group"]);
            Assert.Equal("2500", handler.Headers["tri-service-timeout"]);
        }

        [Fact]
        public async Task NonSuccessResponse_PreservesProviderErrorBody()
        {
            RecordingHandler handler = new RecordingHandler(
                HttpStatusCode.InternalServerError,
                "{\"message\":\"boom\",\"status\":\"500\"}");
            using HttpClient client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:50051")
            };
            using TripleDubboActuatorSuite suite = new TripleDubboActuatorSuite(client);

            DubboRequestResult result = await suite.SendQuery(
                "com.example.dubbonet.api.EchoService#alwaysFail",
                "\"boom\"");

            Assert.False(result.QuerySuccess);
            Assert.Equal(-1, result.ServiceElapsed);
            Assert.Contains("HTTP 500", result.ErrorMeaasge);
            Assert.Contains("boom", result.ErrorMeaasge);
            Assert.Contains("boom", result.Result);
        }

        [Fact]
        public async Task StronglyTypedSingleArgument_IsStillSentAsParameterArray()
        {
            RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "true");
            using HttpClient client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:50051")
            };
            using TripleDubboActuatorSuite suite = new TripleDubboActuatorSuite(client);

            DubboRequestResult<bool> result =
                await suite.SendQuery<bool, bool>(
                    "com.example.dubbonet.api.DataTypeService#negate",
                    false);

            Assert.True(result.QuerySuccess);
            Assert.True(result.ResultModle);
            Assert.Equal("[false]", handler.Body);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _responseBody;

            public RecordingHandler(HttpStatusCode statusCode, string responseBody)
            {
                _statusCode = statusCode;
                _responseBody = responseBody;
            }

            public HttpMethod? Method { get; private set; }
            public Uri? RequestUri { get; private set; }
            public string? ContentType { get; private set; }
            public string? Body { get; private set; }
            public Dictionary<string, string> Headers { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Method = request.Method;
                RequestUri = request.RequestUri;
                ContentType = request.Content?.Headers.ContentType?.MediaType;
                Body = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
                {
                    Headers[header.Key] = string.Join(",", header.Value);
                }

                return new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(
                        _responseBody,
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }
    }
}
