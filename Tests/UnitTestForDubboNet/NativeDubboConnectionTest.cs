using DubboNet.DubboService.DataModle;
using DubboNet.DubboService.Native;
using NHessian.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace UnitTestForDubboNet
{
    public class NativeDubboConnectionTest
    {
        [Fact]
        public async Task InvokeAsync_MatchesOutOfOrderResponsesByRequestId()
        {
            using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            IPEndPoint endpoint = (IPEndPoint)listener.LocalEndpoint;

            Task server = ServeTwoOutOfOrderResponses(listener);
            using NativeDubboConnection connection = new NativeDubboConnection(endpoint, 5_000);

            Task<NativeDubboResponse> first = connection.InvokeAsync(
                Invocation("first"), null, null);
            Task<NativeDubboResponse> second = connection.InvokeAsync(
                Invocation("second"), null, null);

            NativeDubboResponse[] responses = await Task.WhenAll(first, second);
            await server;

            Assert.Equal("first", responses[0].Value);
            Assert.Equal("second", responses[1].Value);
            Assert.NotEqual(responses[0].RequestId, responses[1].RequestId);
        }

        private static DubboInvocation Invocation(string method)
        {
            return new DubboInvocation(
                "com.foo.DemoService",
                method,
                Array.Empty<string>(),
                Array.Empty<object>());
        }

        private static async Task ServeTwoOutOfOrderResponses(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();

            (long FirstId, string FirstMethod) first = await ReadRequest(stream);
            (long SecondId, string SecondMethod) second = await ReadRequest(stream);

            await WriteFragmented(stream, EncodeResponse(second.SecondId, second.SecondMethod));
            await WriteFragmented(stream, EncodeResponse(first.FirstId, first.FirstMethod));
        }

        private static async Task<(long RequestId, string Method)> ReadRequest(NetworkStream stream)
        {
            byte[] header = new byte[NativeDubboCodec.HeaderLength];
            await ReadExactly(stream, header);
            int bodyLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
            byte[] body = new byte[bodyLength];
            await ReadExactly(stream, body);

            using MemoryStream bodyStream = new MemoryStream(body, false);
            using HessianStreamReader reader = new HessianStreamReader(bodyStream, true);
            HessianInputV2 input = new HessianInputV2(reader, TypeBindings.Java);
            _ = input.ReadString();
            _ = input.ReadString();
            _ = input.ReadString();
            _ = input.ReadString();
            _ = input.ReadString();
            string method = input.ReadString();

            return (BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(4, 8)), method);
        }

        private static byte[] EncodeResponse(long requestId, string value)
        {
            byte[] body;
            using (MemoryStream stream = new MemoryStream())
            {
                using HessianStreamWriter writer = new HessianStreamWriter(stream, true);
                HessianOutputV2 output = new HessianOutputV2(writer, TypeBindings.Java);
                output.WriteInt(1);
                output.WriteObject(value);
                body = stream.ToArray();
            }

            byte[] frame = new byte[NativeDubboCodec.HeaderLength + body.Length];
            frame[0] = 0xda;
            frame[1] = 0xbb;
            frame[2] = NativeDubboCodec.Hessian2SerializationId;
            frame[3] = NativeDubboCodec.OkStatus;
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(4, 8), requestId);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12, 4), body.Length);
            body.CopyTo(frame, NativeDubboCodec.HeaderLength);
            return frame;
        }

        private static async Task WriteFragmented(NetworkStream stream, byte[] frame)
        {
            await stream.WriteAsync(frame.AsMemory(0, 3));
            await stream.FlushAsync();
            await stream.WriteAsync(frame.AsMemory(3));
            await stream.FlushAsync();
        }

        private static async Task ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(offset));
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += count;
            }
        }
    }
}
