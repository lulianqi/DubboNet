using DubboNet.Clients.DataModle;
using DubboNet.DubboService;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DubboNet.Clients
{
    /// <summary>
    /// 表示一次 Telnet 元数据探测结果。
    /// <para>EN: Represents the result of a Telnet metadata probe.</para>
    /// </summary>
    internal sealed class DubboTelnetMetadataResult
    {
        public IReadOnlyList<DubboMethodMetadata> Methods { get; init; } =
            Array.Empty<DubboMethodMetadata>();

        public string FailureReason { get; init; }

        public static DubboTelnetMetadataResult Success(
            IReadOnlyList<DubboMethodMetadata> methods)
        {
            return new DubboTelnetMetadataResult
            {
                Methods = methods ?? Array.Empty<DubboMethodMetadata>()
            };
        }

        public static DubboTelnetMetadataResult Failure(string reason)
        {
            return new DubboTelnetMetadataResult
            {
                FailureReason = string.IsNullOrWhiteSpace(reason)
                    ? "Telnet metadata is unavailable."
                    : reason
            };
        }
    }

    /// <summary>
    /// 通过 Dubbo 可选的 Telnet <c>ls -l</c> 命令加载方法签名；调用方负责服务级缓存，本类只执行一次有界探测。
    /// <para>EN: Loads method signatures through Dubbo's optional Telnet <c>ls -l</c> command. The caller owns service-level caching; this resolver performs one bounded probe.</para>
    /// </summary>
    internal static class DubboTelnetMetadataResolver
    {
        private static readonly Regex ReleasePattern = new Regex(
            @"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:\.(?<revision>\d+))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Version MetadataCenterMinimumVersion = new Version(2, 7, 3);

        /// <summary>
        /// 根据 provider 的 <c>release</c> 参数判断是否应优先查询 2.7.3+ 元数据中心。
        /// <para>EN: Determines from provider <c>release</c> values whether the 2.7.3+ metadata center should be tried first.</para>
        /// </summary>
        internal static bool ShouldPreferMetadataCenter(
            IEnumerable<DubboServiceEndPointInfo> providers)
        {
            DubboServiceEndPointInfo[] providerArray = providers?
                .Where(provider => provider != null)
                .ToArray() ?? Array.Empty<DubboServiceEndPointInfo>();
            if (providerArray.Length == 0)
            {
                return true;
            }

            bool containsUnknownRelease = false;
            foreach (DubboServiceEndPointInfo provider in providerArray)
            {
                if (!TryParseRelease(provider.Release, out Version release))
                {
                    containsUnknownRelease = true;
                    continue;
                }
                if (release >= MetadataCenterMinimumVersion)
                {
                    return true;
                }
            }

            // An absent/malformed release is not evidence that the provider is older than 2.7.3.
            // Prefer the non-invasive metadata-center read before opening a Telnet socket.
            return containsUnknownRelease;
        }

        /// <summary>
        /// 从可能带后缀的 Dubbo <c>release</c> 文本中解析版本号。
        /// <para>EN: Parses a version from a Dubbo <c>release</c> string that may contain suffixes.</para>
        /// </summary>
        internal static bool TryParseRelease(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Match match = ReleasePattern.Match(value);
            if (!match.Success
                || !int.TryParse(match.Groups["major"].Value, out int major)
                || !int.TryParse(match.Groups["minor"].Value, out int minor))
            {
                return false;
            }

            int patch = match.Groups["patch"].Success
                && int.TryParse(match.Groups["patch"].Value, out int parsedPatch)
                    ? parsedPatch
                    : 0;
            int revision = match.Groups["revision"].Success
                && int.TryParse(match.Groups["revision"].Value, out int parsedRevision)
                    ? parsedRevision
                    : 0;
            version = new Version(major, minor, patch, revision);
            return true;
        }

        /// <summary>
        /// 依次探测可用的 <c>dubbo://</c> provider，返回第一份可解析的方法签名。
        /// <para>EN: Probes eligible <c>dubbo://</c> providers and returns the first parseable method-signature set.</para>
        /// </summary>
        internal static async Task<DubboTelnetMetadataResult> LoadAsync(
            string serviceName,
            IReadOnlyList<DubboServiceEndPointInfo> providers,
            int requestTimeout)
        {
            DubboServiceEndPointInfo[] candidates = (providers
                    ?? Array.Empty<DubboServiceEndPointInfo>())
                .Where(provider => provider?.EndPoint != null
                    && provider.Disabled != true
                    && string.Equals(provider.Scheme, "dubbo", StringComparison.OrdinalIgnoreCase))
                .GroupBy(provider => provider.EndPoint)
                .Select(group => group.First())
                .ToArray();

            if (candidates.Length == 0)
            {
                return DubboTelnetMetadataResult.Failure(
                    "No active dubbo:// provider endpoint is available for a Telnet probe.");
            }

            List<string> failures = new List<string>();
            foreach (DubboServiceEndPointInfo provider in candidates)
            {
                using DubboActuator actuator = new DubboActuator(
                    provider.EndPoint,
                    requestTimeout,
                    serviceName);
                try
                {
                    if (!await actuator.Connect().ConfigureAwait(false))
                    {
                        failures.Add(
                            $"{provider.EndPoint}: connection failed ({actuator.NowErrorMes ?? "unknown error"})");
                        continue;
                    }

                    NetService.Telnet.ExTelnet.TelnetRequestResult response =
                        await actuator.SendCommandAsync(
                            $"ls -l {serviceName}",
                            true).ConfigureAwait(false);
                    if (response == null)
                    {
                        failures.Add(
                            $"{provider.EndPoint}: command failed ({actuator.NowErrorMes ?? "no response"})");
                        continue;
                    }
                    if (!response.IsGetTargetIdentification)
                    {
                        failures.Add(
                            $"{provider.EndPoint}: the Telnet response was incomplete or did not contain the dubbo prompt");
                        continue;
                    }

                    IReadOnlyList<DubboMethodMetadata> methods = ParseMethodSignatures(
                        response.Result,
                        serviceName,
                        provider,
                        DateTimeOffset.UtcNow);
                    if (methods.Count == 0)
                    {
                        failures.Add(
                            $"{provider.EndPoint}: 'ls -l {serviceName}' returned no parseable method signatures");
                        continue;
                    }

                    return DubboTelnetMetadataResult.Success(methods);
                }
                catch (Exception exception)
                {
                    failures.Add($"{provider.EndPoint}: {exception.Message}");
                }
            }

            string reason =
                "None of the provider endpoints exposed usable Dubbo Telnet metadata. " +
                string.Join("; ", failures);
            MyLogger.LogWarning(
                $"[DubboTelnetMetadataResolver] {serviceName}: {reason}");
            return DubboTelnetMetadataResult.Failure(reason);
        }

        /// <summary>
        /// 将 <c>ls -l</c> 响应解析为已擦除泛型参数的方法元数据。
        /// <para>EN: Parses an <c>ls -l</c> response into method metadata with generic type arguments erased.</para>
        /// </summary>
        internal static IReadOnlyList<DubboMethodMetadata> ParseMethodSignatures(
            string response,
            string serviceName,
            DubboServiceEndPointInfo provider,
            DateTimeOffset loadedAt)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return Array.Empty<DubboMethodMetadata>();
            }

            List<DubboMethodMetadata> methods = new List<DubboMethodMetadata>();
            string[] lines = response.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                int openParenthesis = line.IndexOf('(');
                int closeParenthesis = line.LastIndexOf(')');
                if (openParenthesis <= 0 || closeParenthesis < openParenthesis)
                {
                    continue;
                }

                int separator = line.LastIndexOf(' ', openParenthesis - 1);
                if (separator <= 0)
                {
                    continue;
                }

                string returnDeclaration = line.Substring(0, separator).Trim();
                string methodName = line.Substring(
                    separator + 1,
                    openParenthesis - separator - 1).Trim();
                if (string.IsNullOrWhiteSpace(returnDeclaration)
                    || string.IsNullOrWhiteSpace(methodName)
                    || methodName.Contains("->", StringComparison.Ordinal))
                {
                    continue;
                }

                // Some Telnet extensions include a modifier (for example "public") while the
                // stock Dubbo handler prints only the return type. Extract the last declaration
                // token without treating whitespace inside generic arguments as a separator.
                string returnType = EraseGenericArguments(
                    ExtractDeclaredType(returnDeclaration));
                if (string.IsNullOrWhiteSpace(returnType))
                {
                    continue;
                }

                string parameterDeclaration = line.Substring(
                    openParenthesis + 1,
                    closeParenthesis - openParenthesis - 1);
                methods.Add(new DubboMethodMetadata
                {
                    ServiceName = serviceName,
                    Name = methodName,
                    ParameterTypes = SplitParameterTypes(parameterDeclaration),
                    ReturnType = returnType,
                    Version = provider?.Version,
                    Group = provider?.Group,
                    Application = provider?.Application,
                    MetadataPath = provider?.EndPoint == null
                        ? null
                        : $"telnet://{provider.EndPoint}/{serviceName}",
                    MetadataSource = DubboMetadataSource.Telnet,
                    ServiceParameters = BuildServiceParameters(provider),
                    TypeDefinitions = Array.Empty<DubboTypeMetadata>(),
                    MetadataVersion = -1,
                    LoadedAt = loadedAt
                });
            }

            return methods
                .GroupBy(DubboMethodMetadataResolver.SignatureKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }

        private static string[] SplitParameterTypes(string declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration))
            {
                return Array.Empty<string>();
            }

            List<string> parameters = new List<string>();
            int genericDepth = 0;
            int start = 0;
            for (int index = 0; index < declaration.Length; index++)
            {
                switch (declaration[index])
                {
                    case '<':
                        genericDepth++;
                        break;
                    case '>':
                        genericDepth = Math.Max(0, genericDepth - 1);
                        break;
                    case ',' when genericDepth == 0:
                        AddParameter(parameters, declaration.Substring(start, index - start));
                        start = index + 1;
                        break;
                }
            }
            AddParameter(parameters, declaration.Substring(start));
            return parameters.ToArray();
        }

        private static void AddParameter(List<string> parameters, string value)
        {
            string parameter = EraseGenericArguments(value);
            if (!string.IsNullOrWhiteSpace(parameter))
            {
                parameters.Add(parameter.EndsWith("...", StringComparison.Ordinal)
                    ? parameter.Substring(0, parameter.Length - 3) + "[]"
                    : parameter);
            }
        }

        private static string ExtractDeclaredType(string declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration))
            {
                return null;
            }

            int genericDepth = 0;
            int lastSeparator = -1;
            for (int index = 0; index < declaration.Length; index++)
            {
                switch (declaration[index])
                {
                    case '<':
                        genericDepth++;
                        break;
                    case '>':
                        genericDepth = Math.Max(0, genericDepth - 1);
                        break;
                    default:
                        if (genericDepth == 0 && char.IsWhiteSpace(declaration[index]))
                        {
                            lastSeparator = index;
                        }
                        break;
                }
            }

            return declaration.Substring(lastSeparator + 1).Trim();
        }

        /// <summary>
        /// <c>GenericService.$invoke</c> 需要 JVM 可加载的擦除类名，因此移除 Telnet 输出中的源码风格泛型参数。
        /// <para>EN: <c>GenericService.$invoke</c> requires JVM-loadable erased class names, so source-style generic arguments from Telnet output are removed.</para>
        /// </summary>
        private static string EraseGenericArguments(string declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration))
            {
                return null;
            }

            StringBuilder erased = new StringBuilder(declaration.Length);
            int genericDepth = 0;
            foreach (char character in declaration.Trim())
            {
                if (character == '<')
                {
                    genericDepth++;
                    continue;
                }
                if (character == '>')
                {
                    genericDepth = Math.Max(0, genericDepth - 1);
                    continue;
                }
                if (genericDepth == 0)
                {
                    erased.Append(character);
                }
            }
            return erased.ToString().Trim();
        }

        private static IReadOnlyDictionary<string, string> BuildServiceParameters(
            DubboServiceEndPointInfo provider)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>(
                StringComparer.Ordinal);
            AddParameter(parameters, "application", provider?.Application);
            AddParameter(parameters, "release", provider?.Release);
            AddParameter(parameters, "version", provider?.Version);
            AddParameter(parameters, "group", provider?.Group);
            return parameters;
        }

        private static void AddParameter(
            IDictionary<string, string> parameters,
            string key,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[key] = value;
            }
        }
    }
}
