using DubboNet.Clients.DataModle;
using DubboNet.DubboService.DataModle;
using DubboNet.DubboService.Native;
using NHessian.IO;
using System.Buffers.Binary;
using System.Collections;

namespace UnitTestForDubboNet
{
    public class NativeDubboCodecTest
    {
        [Fact]
        public void SerializeJson_PreservesChineseCharacters()
        {
            Dictionary<string, object> value = new Dictionary<string, object>
            {
                ["message"] = "未知错误"
            };

            string json = NativeDubboCodec.SerializeJson(value);

            Assert.Equal("{\"message\":\"未知错误\"}", json);
        }

        [Fact]
        public void EncodeGenericRequest_WritesDubboHeaderAndGenericBody()
        {
            DubboInvocation invocation = new DubboInvocation(
                "com.foo.DemoService",
                "find",
                new[] { "java.lang.String", "int" },
                new object[] { "A001", 1234 });

            byte[] frame = NativeDubboCodec.EncodeGenericRequest(
                42,
                invocation,
                "1.0.0",
                "prod",
                5000);

            Assert.Equal(0xda, frame[0]);
            Assert.Equal(0xbb, frame[1]);
            Assert.Equal(0xc2, frame[2]);
            Assert.Equal(42, BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(4, 8)));
            Assert.Equal(frame.Length - 16, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(12, 4)));

            using MemoryStream body = new MemoryStream(frame, 16, frame.Length - 16, false);
            using HessianStreamReader reader = new HessianStreamReader(body, true);
            HessianInputV2 input = new HessianInputV2(reader, TypeBindings.Java);

            Assert.Equal("2.0.2", input.ReadString());
            Assert.Equal("com.foo.DemoService", input.ReadString());
            Assert.Equal("1.0.0", input.ReadString());
            Assert.Equal("$invoke", input.ReadString());
            Assert.Equal(
                "Ljava/lang/String;[Ljava/lang/String;[Ljava/lang/Object;",
                input.ReadString());
            Assert.Equal("find", input.ReadString());
            Assert.Equal(new[] { "java.lang.String", "int" }, (string[])input.ReadObject());
            Assert.Equal(new object[] { "A001", 1234 }, (object[])input.ReadObject());

            IDictionary attachments = Assert.IsAssignableFrom<IDictionary>(input.ReadObject());
            Assert.Equal("com.foo.DemoService", attachments["path"]);
            Assert.Equal("true", attachments["generic"]);
            Assert.Equal("prod", attachments["group"]);
            Assert.Equal("5000", attachments["timeout"]);
        }

        [Fact]
        public void DecodeResponse_ReadsValueAndAttachments()
        {
            byte[] body;
            using (MemoryStream stream = new MemoryStream())
            {
                using HessianStreamWriter writer = new HessianStreamWriter(stream, true);
                HessianOutputV2 output = new HessianOutputV2(writer, TypeBindings.Java);
                output.WriteInt(4);
                output.WriteObject(new Dictionary<string, object>
                {
                    ["id"] = 1234,
                    ["name"] = "demo",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["city"] = "杭州",
                        ["items"] = new object[]
                        {
                            new Dictionary<string, object> { ["value"] = 7 }
                        }
                    }
                });
                output.WriteObject(new Dictionary<string, object>
                {
                    ["dubbo"] = "2.0.2"
                });
                body = stream.ToArray();
            }

            byte[] header = new byte[16];
            header[0] = 0xda;
            header[1] = 0xbb;
            header[2] = 2;
            header[3] = 20;
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(4, 8), 99);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), body.Length);

            NativeDubboResponse response = NativeDubboCodec.DecodeResponse(
                NativeDubboCodec.DecodeFrameHeader(header, body));

            Assert.Equal(99, response.RequestId);
            Assert.Equal(4, response.ResultKind);
            Dictionary<string, object> value = Assert.IsType<Dictionary<string, object>>(response.Value);
            Assert.Equal(1234, value["id"]);
            Assert.Equal("demo", value["name"]);
            Dictionary<string, object> data = Assert.IsType<Dictionary<string, object>>(value["data"]);
            Assert.Equal("杭州", data["city"]);
            List<object> items = Assert.IsType<List<object>>(data["items"]);
            Dictionary<string, object> item = Assert.IsType<Dictionary<string, object>>(items[0]);
            Assert.Equal(7, item["value"]);
            Assert.Equal("2.0.2", response.Attachments["dubbo"]);
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("1234", 1)]
        [InlineData("\"123\",1234", 2)]
        public void ParseInvocation_InfersPrimitiveArgumentCount(string request, int count)
        {
            DubboInvocation invocation = NativeDubboCodec.ParseInvocation(
                "com.foo.DemoService.test",
                request);

            Assert.Equal("com.foo.DemoService", invocation.Service);
            Assert.Equal("test", invocation.Method);
            Assert.Equal(count, invocation.Arguments.Count);
            Assert.Equal(count, invocation.ParameterTypes.Count);
        }

        [Fact]
        public void EndpointInfo_ParsesServiceAndSerializationMetadata()
        {
            Uri uri = new Uri(
                "dubbo://127.0.0.1:20880/com.foo.DemoService" +
                "?interface=com.foo.DemoService&version=1.0.0&group=prod" +
                "&prefer.serialization=hessian2,fastjson2&serialization=hessian2");

            DubboServiceEndPointInfo endpoint =
                DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(uri);

            Assert.Equal("dubbo", endpoint.Scheme);
            Assert.Equal("1.0.0", endpoint.Version);
            Assert.Equal("prod", endpoint.Group);
            Assert.Equal("hessian2,fastjson2", endpoint.PreferSerialization);
            Assert.Equal("hessian2", endpoint.Serialization);
        }

        [Fact]
        public void EncodeGenericRequest_ConvertsClrPojoToGenericMapWithClassName()
        {
            DubboInvocation invocation = new DubboInvocation(
                "com.foo.UserService",
                "save",
                new[] { "com.foo.User" },
                new object[] { new { name = "demo", age = 18 } });

            byte[] body = NativeDubboCodec.EncodeGenericRequestBody(
                invocation,
                null,
                null,
                1000);

            using MemoryStream stream = new MemoryStream(body, false);
            using HessianStreamReader reader = new HessianStreamReader(stream, true);
            HessianInputV2 input = new HessianInputV2(reader, TypeBindings.Java);
            for (int i = 0; i < 7; i++)
            {
                _ = input.ReadObject();
            }
            Dictionary<object, object>[] arguments = Assert.IsType<Dictionary<object, object>[]>(
                input.ReadObject(typeof(Dictionary<object, object>[])));
            IDictionary pojo = Assert.IsAssignableFrom<IDictionary>(arguments[0]);

            Assert.Equal("com.foo.User", pojo["class"]);
            Assert.Equal("demo", pojo["name"]);
            Assert.Equal(18, pojo["age"]);
        }

        [Fact]
        public void DecodeResponse_ReadsUnicodeErrorBody()
        {
            byte[] body;
            using (MemoryStream stream = new MemoryStream())
            {
                using HessianStreamWriter writer = new HessianStreamWriter(stream, true);
                HessianOutputV2 output = new HessianOutputV2(writer, TypeBindings.Java);
                output.WriteString("查询失败");
                body = stream.ToArray();
            }

            byte[] header = new byte[16];
            header[0] = 0xda;
            header[1] = 0xbb;
            header[2] = 2;
            header[3] = 70;
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(4, 8), 100);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), body.Length);

            NativeDubboResponse response = NativeDubboCodec.DecodeResponse(
                NativeDubboCodec.DecodeFrameHeader(header, body));

            Assert.Equal("查询失败", response.ErrorMessage);
        }

        [Fact]
        public void EncodeHeartbeatResponse_WritesEventResponseHeader()
        {
            byte[] frame = NativeDubboCodec.EncodeHeartbeatResponse(101, 2);

            Assert.Equal(0xda, frame[0]);
            Assert.Equal(0xbb, frame[1]);
            Assert.Equal(0x22, frame[2]);
            Assert.Equal(20, frame[3]);
            Assert.Equal(101, BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(4, 8)));
            Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(12, 4)));
            Assert.Equal((byte)'N', frame[16]);
        }

        [Fact]
        public void DecodeResponse_ReadsCompactHessianObject()
        {
            byte[] body;
            using (MemoryStream stream = new MemoryStream())
            {
                using HessianStreamWriter writer = new HessianStreamWriter(stream, true);
                HessianOutputV2 output = new HessianOutputV2(writer, TypeBindings.Java);
                output.WriteInt(1);
                output.WriteObject(new CodecPojo { Name = "demo", Count = 3 });
                body = stream.ToArray();
            }

            byte[] header = new byte[16];
            header[0] = 0xda;
            header[1] = 0xbb;
            header[2] = 2;
            header[3] = 20;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), body.Length);

            NativeDubboResponse response = NativeDubboCodec.DecodeResponse(
                NativeDubboCodec.DecodeFrameHeader(header, body));
            Dictionary<string, object> value = Assert.IsType<Dictionary<string, object>>(response.Value);

            Assert.EndsWith("NativeDubboCodecTest+CodecPojo", Assert.IsType<string>(value["class"]));
            Assert.Equal("demo", value["Name"]);
            Assert.Equal(3, value["Count"]);
        }

        [Fact]
        public void DecodeResponse_HandlesCircularHessianReference()
        {
            Hashtable source = new Hashtable();
            source["self"] = source;

            byte[] body;
            using (MemoryStream stream = new MemoryStream())
            {
                using HessianStreamWriter writer = new HessianStreamWriter(stream, true);
                HessianOutputV2 output = new HessianOutputV2(writer, TypeBindings.Java);
                output.WriteInt(1);
                output.WriteObject(source);
                body = stream.ToArray();
            }

            byte[] header = new byte[16];
            header[0] = 0xda;
            header[1] = 0xbb;
            header[2] = 2;
            header[3] = 20;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), body.Length);

            NativeDubboResponse response = NativeDubboCodec.DecodeResponse(
                NativeDubboCodec.DecodeFrameHeader(header, body));
            Dictionary<string, object> value = Assert.IsType<Dictionary<string, object>>(response.Value);

            Assert.Equal("[Circular reference]", value["self"]);
        }

        private sealed class CodecPojo
        {
            public string Name = string.Empty;
            public int Count;
        }
    }
}
