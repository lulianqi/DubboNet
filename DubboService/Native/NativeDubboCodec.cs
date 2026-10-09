using DubboNet.DubboService.DataModle;
using NHessian.IO;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace DubboNet.DubboService.Native
{
    /// <summary>
    /// 编解码 Dubbo2 16 字节帧头、Hessian2 泛化调用请求和响应。
    /// <para>EN: Encodes and decodes Dubbo2 16-byte frames and Hessian2 generic invocation payloads.</para>
    /// </summary>
    internal static class NativeDubboCodec
    {
        internal const int HeaderLength = 16;
        internal const int DefaultMaxPayloadLength = 8 * 1024 * 1024;
        internal const byte Hessian2SerializationId = 2;
        internal const byte OkStatus = 20;
        internal const string ProtocolVersion = "2.0.2";
        internal const string GenericParameterDescriptor =
            "Ljava/lang/String;[Ljava/lang/String;[Ljava/lang/Object;";

        private const byte RequestFlag = 0x80;
        private const byte TwoWayFlag = 0x40;
        private const byte EventFlag = 0x20;
        private const byte SerializationMask = 0x1f;

        private static readonly JsonSerializerOptions ResultJsonOptions =
            new JsonSerializerOptions
            {
                // 保留中文等 Unicode 字符的可读形式，同时继续转义 JSON/HTML 敏感字符。
                // EN: Keep Chinese and other Unicode characters readable while retaining
                // the encoder's escaping of JSON/HTML-sensitive characters.
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            };

        private static readonly JsonSerializerOptions ArgumentJsonOptions =
            new JsonSerializerOptions
            {
                IncludeFields = true
            };

        /// <summary>
        /// 将泛化调用编码为完整的双向 Dubbo2 请求帧。
        /// <para>EN: Encodes a generic invocation as a complete two-way Dubbo2 request frame.</para>
        /// </summary>
        internal static byte[] EncodeGenericRequest(
            long requestId,
            DubboInvocation invocation,
            string serviceVersion,
            string group,
            int timeoutMilliseconds)
        {
            invocation.Validate();
            byte[] body = EncodeGenericRequestBody(
                invocation,
                serviceVersion,
                group,
                timeoutMilliseconds);

            byte[] frame = new byte[HeaderLength + body.Length];
            frame[0] = 0xda;
            frame[1] = 0xbb;
            frame[2] = RequestFlag | TwoWayFlag | Hessian2SerializationId;
            frame[3] = 0;
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(4, 8), requestId);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12, 4), body.Length);
            body.CopyTo(frame, HeaderLength);
            return frame;
        }

        /// <summary>
        /// 编码客户端心跳请求帧。
        /// <para>EN: Encodes a client heartbeat request frame.</para>
        /// </summary>
        internal static byte[] EncodeHeartbeat(long requestId)
        {
            byte[] frame = new byte[HeaderLength + 1];
            frame[0] = 0xda;
            frame[1] = 0xbb;
            frame[2] = RequestFlag | TwoWayFlag | EventFlag | Hessian2SerializationId;
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(4, 8), requestId);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12, 4), 1);
            frame[HeaderLength] = (byte)'N';
            return frame;
        }

        /// <summary>
        /// 编码服务端双向事件所需的心跳响应帧。
        /// <para>EN: Encodes the heartbeat response required by a server two-way event.</para>
        /// </summary>
        internal static byte[] EncodeHeartbeatResponse(long requestId, byte serializationId)
        {
            byte[] frame = new byte[HeaderLength + 1];
            frame[0] = 0xda;
            frame[1] = 0xbb;
            frame[2] = (byte)(EventFlag | (serializationId & SerializationMask));
            frame[3] = OkStatus;
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(4, 8), requestId);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(12, 4), 1);
            frame[HeaderLength] = (byte)'N';
            return frame;
        }

        /// <summary>
        /// 按 <c>GenericService.$invoke</c> 参数顺序编码 Hessian2 请求体。
        /// <para>EN: Encodes a Hessian2 request body in <c>GenericService.$invoke</c> argument order.</para>
        /// </summary>
        internal static byte[] EncodeGenericRequestBody(
            DubboInvocation invocation,
            string serviceVersion,
            string group,
            int timeoutMilliseconds)
        {
            using MemoryStream stream = new MemoryStream();
            using HessianStreamWriter streamWriter = new HessianStreamWriter(stream, true);
            HessianOutputV2 output = new HessianOutputV2(streamWriter, TypeBindings.Java);

            string effectiveVersion = invocation.Version ?? serviceVersion;
            string effectiveGroup = invocation.Group ?? group;
            object[] arguments = new object[invocation.Arguments.Count];
            Dictionary<object, object> normalizedReferences =
                new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < arguments.Length; i++)
            {
                object prepared = PrepareArgument(
                    invocation.Arguments[i],
                    invocation.ParameterTypes[i]);
                arguments[i] = NormalizeHessianArgument(prepared, normalizedReferences);
            }

            output.WriteString(ProtocolVersion);
            output.WriteString(invocation.Service);
            output.WriteString(effectiveVersion);
            output.WriteString("$invoke");
            output.WriteString(GenericParameterDescriptor);
            output.WriteString(invocation.Method);
            output.WriteObject(invocation.ParameterTypes.ToArray());
            output.WriteObject(arguments);
            output.WriteObject(BuildAttachments(
                invocation,
                effectiveVersion,
                effectiveGroup,
                timeoutMilliseconds));

            return stream.ToArray();
        }

        /// <summary>
        /// 解码 Hessian2 响应标志、返回值、异常和 attachments。
        /// <para>EN: Decodes Hessian2 response flags, values, exceptions, and attachments.</para>
        /// </summary>
        internal static NativeDubboResponse DecodeResponse(NativeDubboFrame frame)
        {
            NativeDubboResponse response = new NativeDubboResponse
            {
                RequestId = frame.RequestId,
                Status = frame.Status,
                SerializationId = frame.SerializationId,
                IsEvent = frame.IsEvent
            };

            if (frame.SerializationId != Hessian2SerializationId)
            {
                throw new NotSupportedException(
                    $"Dubbo response serialization id {frame.SerializationId} is not supported. " +
                    "NativeDubboActuatorSuite currently supports Hessian2 (id 2) only.");
            }

            DubboHessian2Reader input = new DubboHessian2Reader(frame.Body);

            if (frame.Status != OkStatus)
            {
                response.ErrorMessage = input.ReadString();
                return response;
            }

            if (frame.IsEvent)
            {
                response.Value = input.ReadValue();
                return response;
            }

            response.ResultKind = input.ReadInt();
            switch (response.ResultKind)
            {
                case 0:
                    response.Exception = ToJsonCompatible(input.ReadValue());
                    break;
                case 1:
                    response.Value = ToJsonCompatible(input.ReadValue());
                    break;
                case 2:
                    break;
                case 3:
                    response.Exception = ToJsonCompatible(input.ReadValue());
                    response.Attachments = ReadAttachments(input);
                    break;
                case 4:
                    response.Value = ToJsonCompatible(input.ReadValue());
                    response.Attachments = ReadAttachments(input);
                    break;
                case 5:
                    response.Attachments = ReadAttachments(input);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unknown Dubbo response result kind {response.ResultKind}; expected 0 through 5.");
            }
            return response;
        }

        /// <summary>
        /// 校验并解析 Dubbo2 帧头。
        /// <para>EN: Validates and parses a Dubbo2 frame header.</para>
        /// </summary>
        internal static NativeDubboFrame DecodeFrameHeader(byte[] header, byte[] body)
        {
            if (header == null || header.Length != HeaderLength)
            {
                throw new InvalidDataException($"Dubbo header must contain exactly {HeaderLength} bytes.");
            }
            if (header[0] != 0xda || header[1] != 0xbb)
            {
                throw new InvalidDataException(
                    $"Invalid Dubbo magic 0x{header[0]:x2}{header[1]:x2}; expected 0xdabb.");
            }

            int declaredLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
            if (body == null || declaredLength != body.Length)
            {
                throw new InvalidDataException(
                    $"Dubbo body length mismatch. Header declares {declaredLength}, received {body?.Length ?? 0}.");
            }

            byte flags = header[2];
            return new NativeDubboFrame
            {
                Flags = flags,
                Status = header[3],
                RequestId = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(4, 8)),
                SerializationId = (byte)(flags & SerializationMask),
                IsRequest = (flags & RequestFlag) != 0,
                IsTwoWay = (flags & TwoWayFlag) != 0,
                IsEvent = (flags & EventFlag) != 0,
                Body = body
            };
        }

        /// <summary>
        /// 将旧式 endpoint 与 JSON 请求转换为泛化调用；类型推断仅用于兼容入口。
        /// <para>EN: Converts a legacy endpoint and JSON request into a generic invocation; inference here exists for compatibility only.</para>
        /// </summary>
        internal static DubboInvocation ParseInvocation(string endPoint, string request)
        {
            (string service, string method) = SplitEndPoint(endPoint);
            JsonElement[] jsonArguments = ParseJsonArguments(request);
            string[] parameterTypes = new string[jsonArguments.Length];
            object[] arguments = new object[jsonArguments.Length];

            for (int i = 0; i < jsonArguments.Length; i++)
            {
                parameterTypes[i] = InferJavaType(jsonArguments[i]);
                arguments[i] = jsonArguments[i].Clone();
            }

            return new DubboInvocation(service, method, parameterTypes, arguments);
        }

        internal static string SerializeJson(object value)
        {
            return JsonSerializer.Serialize(ToJsonCompatible(value), ResultJsonOptions);
        }

        internal static string DescribeException(object exception)
        {
            if (exception is IDictionary<string, object> stringMap)
            {
                foreach (string key in new[] { "detailMessage", "message", "exceptionMessage" })
                {
                    if (stringMap.TryGetValue(key, out object value) && value != null)
                    {
                        return value.ToString();
                    }
                }
            }
            return exception?.ToString() ?? "The Dubbo provider returned an exception.";
        }

        private static Dictionary<string, object> BuildAttachments(
            DubboInvocation invocation,
            string serviceVersion,
            string group,
            int timeoutMilliseconds)
        {
            Dictionary<string, object> attachments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = invocation.Service,
                ["interface"] = invocation.Service,
                ["dubbo"] = ProtocolVersion,
                ["generic"] = "true",
                ["timeout"] = Math.Max(1, timeoutMilliseconds).ToString()
            };

            if (!string.IsNullOrEmpty(serviceVersion))
            {
                attachments["version"] = serviceVersion;
            }
            if (!string.IsNullOrEmpty(group))
            {
                attachments["group"] = group;
            }
            if (invocation.Attachments != null)
            {
                foreach (KeyValuePair<string, string> attachment in invocation.Attachments)
                {
                    attachments[attachment.Key] = attachment.Value;
                }
            }
            return attachments;
        }

        private static IReadOnlyDictionary<string, object> ReadAttachments(DubboHessian2Reader input)
        {
            object value = ToJsonCompatible(input.ReadValue());
            return value as IReadOnlyDictionary<string, object>
                ?? new Dictionary<string, object>();
        }

        private static object PrepareArgument(object value, string javaType)
        {
            if (value is JsonDocument document)
            {
                value = document.RootElement;
            }
            if (value is JsonElement element)
            {
                value = FromJsonElement(element, javaType);
            }

            switch (javaType)
            {
                case "byte":
                case "java.lang.Byte":
                case "short":
                case "java.lang.Short":
                case "int":
                case "java.lang.Integer":
                    return value == null ? null : Convert.ToInt32(value);
                case "long":
                case "java.lang.Long":
                    return value == null ? null : Convert.ToInt64(value);
                case "float":
                case "java.lang.Float":
                case "double":
                case "java.lang.Double":
                    return value == null ? null : Convert.ToDouble(value);
                case "boolean":
                case "java.lang.Boolean":
                    return value == null ? null : Convert.ToBoolean(value);
                case "char":
                case "java.lang.Character":
                case "java.lang.String":
                    return value?.ToString();
                default:
                    if (value != null && value is not string && IsPojoType(javaType))
                    {
                        JsonElement serialized = JsonSerializer.SerializeToElement(
                            value,
                            ArgumentJsonOptions);
                        if (serialized.ValueKind == JsonValueKind.Object)
                        {
                            return FromJsonElement(serialized, javaType);
                        }
                    }
                    return AddPojoClass(value, javaType);
            }
        }

        private static object FromJsonElement(JsonElement element, string javaType = null)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    if (IsLongType(javaType) && element.TryGetInt64(out long longValue))
                    {
                        return longValue;
                    }
                    if (IsFloatingPointType(javaType))
                    {
                        return element.GetDouble();
                    }
                    if (element.TryGetInt32(out int intValue))
                    {
                        return intValue;
                    }
                    if (element.TryGetInt64(out long inferredLong))
                    {
                        return inferredLong;
                    }
                    return element.GetDouble();
                case JsonValueKind.Array:
                    return element.EnumerateArray()
                        .Select(item => FromJsonElement(item))
                        .ToArray();
                case JsonValueKind.Object:
                    Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        map[property.Name] = FromJsonElement(property.Value);
                    }
                    return AddPojoClass(map, javaType);
                default:
                    throw new NotSupportedException($"Unsupported JSON value kind {element.ValueKind}.");
            }
        }

        private static object NormalizeHessianArgument(
            object value,
            Dictionary<object, object> normalizedReferences)
        {
            if (value == null
                || value is string
                || value is bool
                || value is int
                || value is long
                || value is float
                || value is double
                || value is DateTime
                || value is byte[]
                || value is Enum)
            {
                return value;
            }

            // NHessian 0.4.3 only writes int, long, float and double as native
            // Hessian numbers. Unsupported CLR numeric structs are otherwise
            // reflected as POJOs; decimal/UInt64 can then recurse through their
            // own backing fields until a StackOverflowException occurs.
            if (value is decimal decimalValue)
            {
                return Convert.ToDouble(decimalValue);
            }
            if (value is byte
                || value is sbyte
                || value is short
                || value is ushort)
            {
                return Convert.ToInt32(value);
            }
            if (value is uint uintValue)
            {
                return Convert.ToInt64(uintValue);
            }
            if (value is ulong ulongValue)
            {
                if (ulongValue > long.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        ulongValue,
                        "UInt64 values greater than Int64.MaxValue cannot be encoded by Hessian2.");
                }
                return Convert.ToInt64(ulongValue);
            }
            if (value is char character)
            {
                return character.ToString();
            }
            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.UtcDateTime;
            }
            if (value is Guid guid)
            {
                return guid.ToString();
            }
            if (value is JsonDocument document)
            {
                return NormalizeHessianArgument(
                    FromJsonElement(document.RootElement),
                    normalizedReferences);
            }
            if (value is JsonElement element)
            {
                return NormalizeHessianArgument(
                    FromJsonElement(element),
                    normalizedReferences);
            }

            if (normalizedReferences.TryGetValue(value, out object existing))
            {
                return existing;
            }

            if (value is IDictionary dictionary)
            {
                Dictionary<object, object> normalized =
                    new Dictionary<object, object>();
                normalizedReferences[value] = normalized;
                foreach (DictionaryEntry entry in dictionary)
                {
                    object key = NormalizeHessianArgument(entry.Key, normalizedReferences);
                    if (key == null)
                    {
                        throw new ArgumentException(
                            "Hessian request maps cannot contain a null key.",
                            nameof(value));
                    }
                    normalized[key] = NormalizeHessianArgument(
                        entry.Value,
                        normalizedReferences);
                }
                return normalized;
            }

            if (value is IEnumerable enumerable)
            {
                List<object> normalized = new List<object>();
                normalizedReferences[value] = normalized;
                foreach (object item in enumerable)
                {
                    normalized.Add(NormalizeHessianArgument(item, normalizedReferences));
                }
                return normalized;
            }

            JsonElement serialized = JsonSerializer.SerializeToElement(
                value,
                ArgumentJsonOptions);
            object normalizedPojo = FromJsonElement(serialized);
            object normalizedValue = NormalizeHessianArgument(
                normalizedPojo,
                normalizedReferences);
            normalizedReferences[value] = normalizedValue;
            return normalizedValue;
        }

        private static object AddPojoClass(object value, string javaType)
        {
            if (value is IDictionary<string, object> map && IsPojoType(javaType))
            {
                if (!map.ContainsKey("class"))
                {
                    map["class"] = javaType;
                }
            }
            return value;
        }

        private static bool IsPojoType(string javaType)
        {
            if (string.IsNullOrWhiteSpace(javaType) || javaType.StartsWith("[", StringComparison.Ordinal))
            {
                return false;
            }
            return javaType != "java.lang.Object"
                && javaType != "java.util.Map"
                && javaType != "java.util.HashMap"
                && javaType != "java.util.List"
                && javaType != "java.util.Collection";
        }

        private static bool IsLongType(string javaType)
        {
            return javaType == "long" || javaType == "java.lang.Long";
        }

        private static bool IsFloatingPointType(string javaType)
        {
            return javaType == "float"
                || javaType == "java.lang.Float"
                || javaType == "double"
                || javaType == "java.lang.Double";
        }

        private static JsonElement[] ParseJsonArguments(string request)
        {
            if (string.IsNullOrWhiteSpace(request))
            {
                return Array.Empty<JsonElement>();
            }

            string trimmed = request.Trim();
            string json = trimmed.StartsWith("[", StringComparison.Ordinal)
                && trimmed.EndsWith("]", StringComparison.Ordinal)
                    ? trimmed
                    : $"[{trimmed}]";

            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray()
                .Select(item => item.Clone())
                .ToArray();
        }

        private static string InferJavaType(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return "java.lang.String";
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return "boolean";
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out _))
                    {
                        return "int";
                    }
                    if (element.TryGetInt64(out _))
                    {
                        return "long";
                    }
                    return "double";
                case JsonValueKind.Object:
                    if (element.TryGetProperty("class", out JsonElement classElement)
                        && classElement.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(classElement.GetString()))
                    {
                        return classElement.GetString();
                    }
                    return "java.util.Map";
                case JsonValueKind.Array:
                    return "java.util.List";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return "java.lang.Object";
                default:
                    throw new NotSupportedException(
                        $"Cannot infer a Java parameter type from JSON kind {element.ValueKind}.");
            }
        }

        private static (string Service, string Method) SplitEndPoint(string endPoint)
        {
            if (string.IsNullOrWhiteSpace(endPoint))
            {
                throw new ArgumentException("Dubbo endpoint cannot be empty.", nameof(endPoint));
            }

            int separator = Math.Max(endPoint.LastIndexOf('#'), endPoint.LastIndexOf('/'));
            if (separator < 0)
            {
                separator = endPoint.LastIndexOf('.');
            }
            if (separator <= 0 || separator == endPoint.Length - 1)
            {
                throw new ArgumentException(
                    $"Dubbo endpoint '{endPoint}' must contain both service and method names.",
                    nameof(endPoint));
            }
            return (endPoint.Substring(0, separator), endPoint.Substring(separator + 1));
        }

        private static object ToJsonCompatible(object value)
        {
            return ToJsonCompatible(
                value,
                new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static object ToJsonCompatible(object value, HashSet<object> activeReferences)
        {
            if (value == null
                || value is string
                || value is bool
                || value is byte
                || value is sbyte
                || value is short
                || value is ushort
                || value is int
                || value is uint
                || value is long
                || value is ulong
                || value is float
                || value is double
                || value is decimal
                || value is DateTime
                || value is byte[])
            {
                return value;
            }

            if (value is IDictionary dictionary)
            {
                if (!activeReferences.Add(value))
                {
                    return "[Circular reference]";
                }
                Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
                try
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        normalized[entry.Key?.ToString() ?? "null"] =
                            ToJsonCompatible(entry.Value, activeReferences);
                    }
                }
                finally
                {
                    activeReferences.Remove(value);
                }
                return normalized;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                if (!activeReferences.Add(value))
                {
                    return "[Circular reference]";
                }
                List<object> normalized = new List<object>();
                try
                {
                    foreach (object item in enumerable)
                    {
                        normalized.Add(ToJsonCompatible(item, activeReferences));
                    }
                }
                finally
                {
                    activeReferences.Remove(value);
                }
                return normalized;
            }
            return value.ToString();
        }
    }

    internal sealed class NativeDubboFrame
    {
        public byte Flags { get; set; }
        public byte Status { get; set; }
        public long RequestId { get; set; }
        public byte SerializationId { get; set; }
        public bool IsRequest { get; set; }
        public bool IsTwoWay { get; set; }
        public bool IsEvent { get; set; }
        public byte[] Body { get; set; }
    }

    internal sealed class NativeDubboResponse
    {
        public long RequestId { get; set; }
        public byte Status { get; set; }
        public byte SerializationId { get; set; }
        public bool IsEvent { get; set; }
        public int ResultKind { get; set; } = -1;
        public object Value { get; set; }
        public object Exception { get; set; }
        public IReadOnlyDictionary<string, object> Attachments { get; set; }
        public string ErrorMessage { get; set; }
    }
}
