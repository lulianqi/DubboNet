using DubboNet.Clients.DataModle;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DubboNet.Clients
{
    /// <summary>
    /// 根据实参数量和 CLR/JSON 值兼容性，从元数据中选择唯一的 Java 方法重载。
    /// <para>EN: Selects a unique Java overload from metadata by argument count and CLR/JSON value compatibility.</para>
    /// </summary>
    internal static class DubboMethodMetadataResolver
    {
        /// <summary>
        /// 解析本次调用应使用的方法签名；无法唯一判断时要求调用方显式传入 Java 类型。
        /// <para>EN: Resolves the method signature for an invocation and requires explicit Java types when the overload is ambiguous.</para>
        /// </summary>
        internal static DubboMethodMetadata Resolve(
            string serviceName,
            string methodName,
            IReadOnlyList<DubboMethodMetadata> methods,
            IReadOnlyList<object> arguments)
        {
            arguments ??= Array.Empty<object>();
            DubboMethodMetadata[] namedCandidates = methods
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();
            if (namedCandidates.Length == 0)
            {
                throw new DubboMetadataException(
                    $"Method '{serviceName}.{methodName}' is not present in the provider metadata. " +
                    ExplicitTypesSuggestion(serviceName, methodName));
            }

            DubboMethodMetadata[] arityCandidates = namedCandidates
                .Where(method => (method.ParameterTypes?.Length ?? 0) == arguments.Count)
                .ToArray();
            if (arityCandidates.Length == 0)
            {
                throw new DubboMetadataException(
                    $"No overload of '{serviceName}.{methodName}' accepts {arguments.Count} argument(s). " +
                    $"Available signatures: {DescribeSignatures(namedCandidates)}. " +
                    ExplicitTypesSuggestion(serviceName, methodName));
            }

            // Multiple applications often publish the same service definition. Collapse those
            // copies before overload resolution; the invocation only needs the Java signature.
            DubboMethodMetadata[] signatureCandidates = arityCandidates
                .GroupBy(SignatureKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (signatureCandidates.Length == 1)
            {
                return signatureCandidates[0];
            }

            List<(DubboMethodMetadata Method, int Score)> compatible = new();
            foreach (DubboMethodMetadata candidate in signatureCandidates)
            {
                int score = 0;
                bool matches = true;
                for (int index = 0; index < arguments.Count; index++)
                {
                    int argumentScore = GetCompatibilityScore(
                        arguments[index],
                        candidate.ParameterTypes[index]);
                    if (argumentScore < 0)
                    {
                        matches = false;
                        break;
                    }
                    score += argumentScore;
                }
                if (matches)
                {
                    compatible.Add((candidate, score));
                }
            }

            if (compatible.Count == 1)
            {
                return compatible[0].Method;
            }
            if (compatible.Count > 1)
            {
                int bestScore = compatible.Max(item => item.Score);
                DubboMethodMetadata[] best = compatible
                    .Where(item => item.Score == bestScore)
                    .Select(item => item.Method)
                    .ToArray();
                if (best.Length == 1)
                {
                    return best[0];
                }
            }

            DubboMethodMetadata[] ambiguous = compatible.Count == 0
                ? signatureCandidates
                : compatible.Select(item => item.Method).ToArray();
            throw new DubboMetadataException(
                $"Arguments do not uniquely identify an overload of " +
                $"'{serviceName}.{methodName}'. Candidate signatures: " +
                $"{DescribeSignatures(ambiguous)}. Pass javaParameterTypes explicitly. " +
                ExplicitTypesSuggestion(serviceName, methodName));
        }

        /// <summary>
        /// 生成只包含方法名和参数类型的稳定签名键。
        /// <para>EN: Builds a stable signature key from the method name and parameter types.</para>
        /// </summary>
        internal static string SignatureKey(DubboMethodMetadata method)
        {
            return $"{method.Name}({string.Join("\u001f", method.ParameterTypes ?? Array.Empty<string>())})";
        }

        private static int GetCompatibilityScore(object argument, string javaType)
        {
            if (argument is JsonDocument document)
            {
                argument = document.RootElement;
            }
            if (argument is JsonElement element)
            {
                return GetJsonCompatibilityScore(element, javaType);
            }

            bool primitive = IsPrimitive(javaType);
            if (argument == null)
            {
                return primitive ? -1 : 1;
            }

            Type type = argument.GetType();
            if (type == typeof(string))
            {
                return javaType switch
                {
                    "java.lang.String" => 20,
                    "java.lang.CharSequence" => 18,
                    "char" or "java.lang.Character" when ((string)argument).Length == 1 => 12,
                    _ => IsPojoOrObject(javaType) ? 2 : -1
                };
            }
            if (type == typeof(char))
            {
                return javaType is "char" or "java.lang.Character" ? 20
                    : javaType == "java.lang.String" ? 10 : -1;
            }
            if (type == typeof(bool))
            {
                return javaType is "boolean" or "java.lang.Boolean" ? 20 : -1;
            }
            if (IsIntegral(type))
            {
                return ScoreIntegral(type, javaType);
            }
            if (type == typeof(float))
            {
                return javaType is "float" or "java.lang.Float" ? 20
                    : javaType is "double" or "java.lang.Double" ? 12 : -1;
            }
            if (type == typeof(double) || type == typeof(decimal))
            {
                return javaType is "double" or "java.lang.Double" ? 20
                    : javaType == "java.math.BigDecimal" ? 18 : -1;
            }
            if (argument is IDictionary)
            {
                return IsMap(javaType) ? 20 : IsPojoOrObject(javaType) ? 10 : -1;
            }
            if (argument is IEnumerable && argument is not string)
            {
                if (javaType.EndsWith("[]", StringComparison.Ordinal)) return 20;
                if (IsCollection(javaType)) return 18;
                return -1;
            }

            return IsPojoOrObject(javaType) ? 10 : -1;
        }

        private static int GetJsonCompatibilityScore(JsonElement argument, string javaType)
        {
            return argument.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => IsPrimitive(javaType) ? -1 : 1,
                JsonValueKind.String => javaType == "java.lang.String" ? 15
                    : IsPojoOrObject(javaType) ? 2 : -1,
                JsonValueKind.True or JsonValueKind.False =>
                    javaType is "boolean" or "java.lang.Boolean" ? 15 : -1,
                JsonValueKind.Number => IsNumeric(javaType) ? 8 : -1,
                JsonValueKind.Array => javaType.EndsWith("[]", StringComparison.Ordinal)
                    || IsCollection(javaType) ? 10 : -1,
                JsonValueKind.Object => IsMap(javaType) || IsPojoOrObject(javaType) ? 10 : -1,
                _ => -1
            };
        }

        private static int ScoreIntegral(Type clrType, string javaType)
        {
            string expected = clrType == typeof(byte) || clrType == typeof(sbyte)
                ? "byte"
                : clrType == typeof(short) || clrType == typeof(ushort)
                    ? "short"
                    : clrType == typeof(int) || clrType == typeof(uint)
                        ? "int"
                        : "long";
            string actual = NormalizeNumericType(javaType);
            if (actual == expected) return 20;
            if (expected == "byte" && actual is "short" or "int" or "long") return 12;
            if (expected == "short" && actual is "int" or "long") return 12;
            if (expected == "int" && actual == "long") return 12;
            if (actual is "float" or "double") return 5;
            return -1;
        }

        private static string NormalizeNumericType(string javaType)
        {
            return javaType switch
            {
                "java.lang.Byte" => "byte",
                "java.lang.Short" => "short",
                "java.lang.Integer" => "int",
                "java.lang.Long" => "long",
                "java.lang.Float" => "float",
                "java.lang.Double" => "double",
                _ => javaType
            };
        }

        private static bool IsIntegral(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong);
        }

        private static bool IsNumeric(string javaType)
        {
            return NormalizeNumericType(javaType) is
                "byte" or "short" or "int" or "long" or "float" or "double"
                || javaType is "java.math.BigDecimal" or "java.math.BigInteger";
        }

        private static bool IsPrimitive(string javaType)
        {
            return javaType is "boolean" or "byte" or "char" or "short"
                or "int" or "long" or "float" or "double";
        }

        private static bool IsCollection(string javaType)
        {
            return javaType is "java.util.Collection" or "java.util.List"
                or "java.util.Set" or "java.lang.Iterable"
                || javaType.StartsWith("java.util.List<", StringComparison.Ordinal)
                || javaType.StartsWith("java.util.Set<", StringComparison.Ordinal)
                || javaType.StartsWith("java.util.Collection<", StringComparison.Ordinal);
        }

        private static bool IsMap(string javaType)
        {
            return javaType == "java.util.Map"
                || javaType.StartsWith("java.util.Map<", StringComparison.Ordinal);
        }

        private static bool IsPojoOrObject(string javaType)
        {
            return !string.IsNullOrWhiteSpace(javaType)
                && !IsPrimitive(javaType)
                && !IsNumeric(javaType)
                && javaType != "java.lang.Boolean"
                && javaType != "java.lang.Character"
                && !javaType.EndsWith("[]", StringComparison.Ordinal)
                && !IsCollection(javaType);
        }

        private static string DescribeSignatures(IEnumerable<DubboMethodMetadata> methods)
        {
            return string.Join(", ", methods.Select(method =>
                $"{method.Name}({string.Join(", ", method.ParameterTypes ?? Array.Empty<string>())})"));
        }

        private static string ExplicitTypesSuggestion(string serviceName, string methodName)
        {
            return
                $"Retry with QueryGenericAsync(\"{serviceName}.{methodName}\", " +
                "new[] { \"<exact-java-type>\" }, arguments).";
        }
    }
}
