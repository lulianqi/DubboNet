using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using MyCommonHelper;
using org.apache.zookeeper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.Clients
{
    /// <summary>
    /// 解析并缓存 Dubbo 方法元数据：Dubbo 2.7.3 及以上优先元数据中心并回退一次 Telnet，旧版本直接尝试 Telnet。
    /// <para>EN: Resolves and caches Dubbo method metadata. Dubbo 2.7.3+ prefers the metadata center with a one-time Telnet fallback; older providers use Telnet directly.</para>
    /// </summary>
    internal sealed class DubboMetadataManager : IDisposable
    {
        private sealed class ProviderMetadataDocument
        {
            [JsonPropertyName("canonicalName")]
            public string CanonicalName { get; set; }

            [JsonPropertyName("codeSource")]
            public string CodeSource { get; set; }

            [JsonPropertyName("methods")]
            public List<DubboMethodMetadata> Methods { get; set; } =
                new List<DubboMethodMetadata>();

            [JsonPropertyName("types")]
            public List<DubboTypeMetadata> Types { get; set; } =
                new List<DubboTypeMetadata>();

            [JsonPropertyName("parameters")]
            public Dictionary<string, string> Parameters { get; set; } =
                new Dictionary<string, string>();
        }

        private sealed class ParsedMetadataDocument
        {
            public IReadOnlyList<DubboMethodMetadata> Methods { get; init; } =
                Array.Empty<DubboMethodMetadata>();
            public IReadOnlyList<DubboTypeMetadata> Types { get; init; } =
                Array.Empty<DubboTypeMetadata>();
            public bool IsDocument { get; init; }
        }

        private sealed class ServiceMetadataSnapshot
        {
            public IReadOnlyList<DubboMethodMetadata> Methods { get; init; } =
                Array.Empty<DubboMethodMetadata>();
            public IReadOnlyList<string> ExpectedPaths { get; init; } = Array.Empty<string>();
            public string FailureReason { get; init; }
            public bool CanCache { get; init; } = true;
        }

        private sealed class MetadataCenterLoadResult
        {
            public IReadOnlyList<DubboMethodMetadata> Methods { get; init; } =
                Array.Empty<DubboMethodMetadata>();
            public IReadOnlyList<DubboTypeMetadata> Types { get; init; } =
                Array.Empty<DubboTypeMetadata>();
            public IReadOnlyList<string> ExpectedPaths { get; init; } = Array.Empty<string>();
            public string FailureReason { get; init; }
            public bool HasMetadataDocument { get; init; }
            public bool CanCacheFallback { get; init; } = true;
        }

        private enum TelnetProbeState
        {
            NotAttempted,
            Available,
            Unavailable
        }

        private sealed class ServiceCacheEntry : IDisposable
        {
            public SemaphoreSlim RefreshLock { get; } = new SemaphoreSlim(1, 1);
            public ServiceMetadataSnapshot Snapshot { get; set; }
            public MetadataCenterLoadResult MetadataCenterSnapshot { get; set; }
            public HashSet<string> WatchedPaths { get; set; } =
                new HashSet<string>(StringComparer.Ordinal);
            public TelnetProbeState TelnetState { get; set; } = TelnetProbeState.NotAttempted;
            public IReadOnlyList<DubboMethodMetadata> TelnetMethods { get; set; } =
                Array.Empty<DubboMethodMetadata>();
            public string TelnetFailureReason { get; set; }

            public void Dispose()
            {
                RefreshLock.Dispose();
            }
        }

        private sealed class MetadataWatcher : Watcher
        {
            private readonly DubboMetadataManager _owner;

            public MetadataWatcher(DubboMetadataManager owner)
            {
                _owner = owner;
            }

            public override Task process(WatchedEvent @event)
            {
                return _owner.ProcessWatcherEventAsync(@event);
            }
        }

        /// <summary>
        /// Dubbo 官方 FullServiceDefinition 使用字符串表示元素和属性类型引用，部分旧版本或第三方实现则输出展开对象；
        /// 此转换器将两种格式统一为 <see cref="DubboTypeMetadata"/>，使调用方无需感知元数据发布版本。
        /// <para>EN: Dubbo's official FullServiceDefinition stores item and property type references as strings,
        /// while some older or third-party publishers emit expanded objects. This converter normalizes both
        /// representations into <see cref="DubboTypeMetadata"/> so callers remain independent of the publisher version.</para>
        /// </summary>
        private sealed class DubboTypeMetadataJsonConverter : JsonConverter<DubboTypeMetadata>
        {
            public override DubboTypeMetadata Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options)
            {
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                return ReadType(document.RootElement);
            }

            public override void Write(
                Utf8JsonWriter writer,
                DubboTypeMetadata value,
                JsonSerializerOptions options)
            {
                if (value == null)
                {
                    writer.WriteNullValue();
                    return;
                }

                writer.WriteStartObject();
                WriteString(writer, "id", value.Id);
                WriteString(writer, "type", value.Type);
                if ((value.Items?.Count ?? 0) > 0)
                {
                    writer.WritePropertyName("items");
                    writer.WriteStartArray();
                    foreach (DubboTypeMetadata item in value.Items)
                    {
                        Write(writer, item, options);
                    }
                    writer.WriteEndArray();
                }
                if ((value.EnumValues?.Count ?? 0) > 0)
                {
                    writer.WritePropertyName("enums");
                    writer.WriteStartArray();
                    foreach (string enumValue in value.EnumValues)
                    {
                        writer.WriteStringValue(enumValue);
                    }
                    writer.WriteEndArray();
                }
                WriteString(writer, "$ref", value.Reference);
                if ((value.Properties?.Count ?? 0) > 0)
                {
                    writer.WritePropertyName("properties");
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, DubboTypeMetadata> property in value.Properties)
                    {
                        writer.WritePropertyName(property.Key);
                        Write(writer, property.Value, options);
                    }
                    writer.WriteEndObject();
                }
                WriteString(writer, "typeBuilderName", value.TypeBuilderName);
                writer.WriteEndObject();
            }

            private static DubboTypeMetadata ReadType(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    return new DubboTypeMetadata { Type = element.GetString() };
                }

                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return new DubboTypeMetadata();
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException(
                        $"Expected a Dubbo type definition string or object, but found {element.ValueKind}.");
                }

                DubboTypeMetadata result = new DubboTypeMetadata();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Id = ReadOptionalString(property.Value);
                    }
                    else if (property.Name.Equals("type", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Type = ReadOptionalString(property.Value);
                    }
                    else if (property.Name.Equals("$ref", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Reference = ReadOptionalString(property.Value);
                    }
                    else if (property.Name.Equals("typeBuilderName", StringComparison.OrdinalIgnoreCase))
                    {
                        result.TypeBuilderName = ReadOptionalString(property.Value);
                    }
                    else if (property.Name.Equals("items", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Items = ReadItems(property.Value);
                    }
                    else if (property.Name.Equals("properties", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Properties = ReadProperties(property.Value);
                    }
                    else if (property.Name.Equals("enum", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("enums", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string enumValue in ReadEnumValues(property.Value))
                        {
                            if (!result.EnumValues.Contains(enumValue, StringComparer.Ordinal))
                            {
                                result.EnumValues.Add(enumValue);
                            }
                        }
                    }
                }
                return result;
            }

            private static List<DubboTypeMetadata> ReadItems(JsonElement element)
            {
                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return new List<DubboTypeMetadata>();
                }
                if (element.ValueKind != JsonValueKind.Array)
                {
                    return new List<DubboTypeMetadata> { ReadType(element) };
                }
                return element.EnumerateArray().Select(ReadType).ToList();
            }

            private static Dictionary<string, DubboTypeMetadata> ReadProperties(JsonElement element)
            {
                Dictionary<string, DubboTypeMetadata> result =
                    new Dictionary<string, DubboTypeMetadata>(StringComparer.Ordinal);
                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return result;
                }
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException(
                        $"Expected Dubbo type properties to be an object, but found {element.ValueKind}.");
                }
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    result[property.Name] = ReadType(property.Value);
                }
                return result;
            }

            private static List<string> ReadEnumValues(JsonElement element)
            {
                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return new List<string>();
                }
                if (element.ValueKind == JsonValueKind.String)
                {
                    return new List<string> { element.GetString() };
                }
                if (element.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException(
                        $"Expected Dubbo enum values to be an array, but found {element.ValueKind}.");
                }
                return element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => item != null)
                    .ToList();
            }

            private static string ReadOptionalString(JsonElement element) =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : null;

            private static void WriteString(Utf8JsonWriter writer, string name, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    writer.WriteString(name, value);
                }
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new DubboTypeMetadataJsonConverter() }
        };

        private readonly MyZookeeper _metadataZookeeper;
        private readonly string _metadataRootPath;
        private readonly Func<string, Task<IReadOnlyList<DubboServiceEndPointInfo>>> _providerResolver;
        private readonly Func<
            string,
            IReadOnlyList<DubboServiceEndPointInfo>,
            Task<DubboTelnetMetadataResult>> _telnetMetadataLoader;
        private readonly Func<
            string,
            Watcher,
            Task<org.apache.zookeeper.data.Stat>> _metadataExists;
        private readonly Func<
            string,
            Watcher,
            Task<org.apache.zookeeper.DataResult>> _metadataDataLoader;
        private readonly ConcurrentDictionary<string, ServiceCacheEntry> _serviceCache =
            new ConcurrentDictionary<string, ServiceCacheEntry>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _pathToService =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly MetadataWatcher _watcher;
        private int _connectionNeedsReload;
        private bool _disposed;

        /// <summary>
        /// 创建元数据管理器并配置 Telnet 探测超时。
        /// <para>EN: Creates the metadata manager with a timeout for the Telnet probe.</para>
        /// </summary>
        /// <param name="metadataZookeeper">元数据中心 ZooKeeper 客户端。<para>EN: ZooKeeper client for the metadata center.</para></param>
        /// <param name="metadataRootPath">Dubbo 元数据根路径。<para>EN: Dubbo metadata root path.</para></param>
        /// <param name="providerResolver">服务提供者发现函数。<para>EN: Service-provider discovery function.</para></param>
        /// <param name="telnetRequestTimeout">Telnet 探测超时（毫秒）。<para>EN: Telnet probe timeout in milliseconds.</para></param>
        public DubboMetadataManager(
            MyZookeeper metadataZookeeper,
            string metadataRootPath,
            Func<string, Task<IReadOnlyList<DubboServiceEndPointInfo>>> providerResolver,
            int telnetRequestTimeout = 10_000)
            : this(
                metadataZookeeper,
                metadataRootPath,
                providerResolver,
                (serviceName, providers) => DubboTelnetMetadataResolver.LoadAsync(
                    serviceName,
                    providers,
                    telnetRequestTimeout))
        {
        }

        internal DubboMetadataManager(
            MyZookeeper metadataZookeeper,
            string metadataRootPath,
            Func<string, Task<IReadOnlyList<DubboServiceEndPointInfo>>> providerResolver,
            Func<
                string,
                IReadOnlyList<DubboServiceEndPointInfo>,
                Task<DubboTelnetMetadataResult>> telnetMetadataLoader,
            Func<
                string,
                Watcher,
                Task<org.apache.zookeeper.data.Stat>> metadataExists = null,
            Func<
                string,
                Watcher,
                Task<org.apache.zookeeper.DataResult>> metadataDataLoader = null)
        {
            _metadataZookeeper = metadataZookeeper
                ?? throw new ArgumentNullException(nameof(metadataZookeeper));
            _providerResolver = providerResolver
                ?? throw new ArgumentNullException(nameof(providerResolver));
            _telnetMetadataLoader = telnetMetadataLoader
                ?? throw new ArgumentNullException(nameof(telnetMetadataLoader));
            _metadataExists = metadataExists
                ?? ((path, watcher) => _metadataZookeeper.ExistsAsync(path, watcher));
            _metadataDataLoader = metadataDataLoader
                ?? ((path, watcher) => _metadataZookeeper.GetDataAsync(path, watcher));
            _metadataRootPath = NormalizeRootPath(metadataRootPath);
            _watcher = new MetadataWatcher(this);
        }

        /// <summary>
        /// 检查指定服务是否已经创建元数据缓存项。
        /// <para>EN: Checks whether a metadata cache entry exists for the service.</para>
        /// </summary>
        /// <param name="serviceName">Dubbo 接口全限定名。<para>EN: Fully qualified Dubbo interface name.</para></param>
        /// <returns>存在缓存项时为 <see langword="true"/>。<para>EN: <see langword="true"/> when a cache entry exists.</para></returns>
        public bool HasCachedService(string serviceName)
        {
            return !string.IsNullOrWhiteSpace(serviceName)
                && _serviceCache.ContainsKey(serviceName);
        }

        /// <summary>
        /// 获取服务的全部方法元数据，并按版本规则选择元数据中心或 Telnet。
        /// <para>EN: Gets all method metadata for a service and selects the metadata center or Telnet according to the provider version.</para>
        /// </summary>
        /// <param name="serviceName">Dubbo 接口全限定名。<para>EN: Fully qualified Dubbo interface name.</para></param>
        /// <param name="forceRefresh">是否忽略当前快照并重新加载。<para>EN: Whether to ignore the current snapshot and reload it.</para></param>
        /// <returns>方法元数据列表。<para>EN: Method metadata list.</para></returns>
        public async Task<IReadOnlyList<DubboMethodMetadata>> GetServiceMethodsAsync(
            string serviceName,
            bool forceRefresh = false)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DubboMetadataManager));
            }
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException("Dubbo service name cannot be empty.", nameof(serviceName));
            }

            ServiceCacheEntry cacheEntry = _serviceCache.GetOrAdd(
                serviceName,
                _ => new ServiceCacheEntry());

            if (!forceRefresh && cacheEntry.Snapshot != null)
            {
                return EnsureMetadataAvailable(serviceName, cacheEntry.Snapshot);
            }

            await cacheEntry.RefreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!forceRefresh && cacheEntry.Snapshot != null)
                {
                    return EnsureMetadataAvailable(serviceName, cacheEntry.Snapshot);
                }

                ServiceMetadataSnapshot snapshot = await LoadServiceMetadataAsync(
                    serviceName,
                    cacheEntry,
                    forceRefresh).ConfigureAwait(false);
                if (snapshot.CanCache)
                {
                    cacheEntry.Snapshot = snapshot;
                }
                else if (forceRefresh)
                {
                    // A forced refresh is normally caused by a provider/metadata watch. Do not
                    // keep serving the old definition when the refresh hit a transient failure;
                    // leave the entry uncached so the next request retries the recoverable source.
                    cacheEntry.Snapshot = null;
                }
                return EnsureMetadataAvailable(serviceName, snapshot);
            }
            finally
            {
                cacheEntry.RefreshLock.Release();
            }
        }

        /// <summary>
        /// 获取指定名称的所有方法重载；必要时用一次 Telnet 结果补充不完整的元数据中心结果。
        /// <para>EN: Gets all overloads of a method and, when needed, supplements an incomplete metadata-center result with the one-time Telnet result.</para>
        /// </summary>
        /// <param name="serviceName">Dubbo 接口全限定名。<para>EN: Fully qualified Dubbo interface name.</para></param>
        /// <param name="methodName">Java 方法名。<para>EN: Java method name.</para></param>
        /// <returns>匹配的方法元数据。<para>EN: Matching method metadata.</para></returns>
        public async Task<IReadOnlyList<DubboMethodMetadata>> GetMethodMetadataAsync(
            string serviceName,
            string methodName)
        {
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException("Dubbo method name cannot be empty.", nameof(methodName));
            }

            IReadOnlyList<DubboMethodMetadata> methods =
                await GetServiceMethodsAsync(serviceName).ConfigureAwait(false);
            DubboMethodMetadata[] matchingMethods = methods
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();
            if (matchingMethods.Length > 0
                || !methods.Any(method => method.MetadataSource == DubboMetadataSource.MetadataCenter))
            {
                return matchingMethods;
            }

            // A service can be published by several applications while only part of its metadata
            // documents are present. If the preferred center result does not contain this method,
            // give the service-level, one-time Telnet fallback a chance to supplement it.
            DubboTelnetMetadataResult telnet = await GetTelnetServiceMetadataAsync(serviceName)
                .ConfigureAwait(false);
            return telnet.Methods
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();
        }

        /// <summary>
        /// 仅从元数据中心获取指定服务的 <c>FullServiceDefinition.types</c> 类型定义，不使用 Telnet 回退。
        /// <para>EN: Gets <c>FullServiceDefinition.types</c> for a service exclusively from the metadata center, without a Telnet fallback.</para>
        /// </summary>
        /// <param name="serviceName">Dubbo 接口全限定名。<para>EN: Fully qualified Dubbo interface name.</para></param>
        /// <returns>按类型标识去重后的递归类型定义。<para>EN: Recursive type definitions deduplicated by type identifier.</para></returns>
        internal async Task<IReadOnlyList<DubboTypeMetadata>> GetServiceTypesAsync(
            string serviceName)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DubboMetadataManager));
            }
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException("Dubbo service name cannot be empty.", nameof(serviceName));
            }

            ServiceCacheEntry cacheEntry = _serviceCache.GetOrAdd(
                serviceName,
                _ => new ServiceCacheEntry());
            await cacheEntry.RefreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cacheEntry.MetadataCenterSnapshot != null)
                {
                    return EnsureTypeDefinitionsAvailable(
                        serviceName,
                        cacheEntry.MetadataCenterSnapshot);
                }

                IReadOnlyList<DubboServiceEndPointInfo> providers;
                try
                {
                    providers = await _providerResolver(serviceName).ConfigureAwait(false)
                        ?? Array.Empty<DubboServiceEndPointInfo>();
                }
                catch (Exception exception)
                {
                    throw new DubboMetadataException(
                        $"Unable to discover providers before reading metadata-center types for " +
                        $"Dubbo service '{serviceName}': {exception.Message}",
                        exception);
                }

                if (!DubboTelnetMetadataResolver.ShouldPreferMetadataCenter(providers))
                {
                    throw new DubboMetadataException(
                        $"Unable to obtain type definitions for Dubbo service '{serviceName}'. " +
                        "Every reported provider release is below 2.7.3, so no compatible " +
                        "FullServiceDefinition metadata path is expected. Telnet metadata does " +
                        "not contain the 'types' structure.");
                }

                MetadataCenterLoadResult metadataCenterResult =
                    await LoadFromMetadataCenterAsync(serviceName, providers, cacheEntry)
                        .ConfigureAwait(false);
                CacheMetadataCenterResult(cacheEntry, metadataCenterResult);
                return EnsureTypeDefinitionsAvailable(serviceName, metadataCenterResult);
            }
            finally
            {
                cacheEntry.RefreshLock.Release();
            }
        }

        internal async Task<DubboTelnetMetadataResult> GetTelnetServiceMetadataAsync(
            string serviceName)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DubboMetadataManager));
            }
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException("Dubbo service name cannot be empty.", nameof(serviceName));
            }

            ServiceCacheEntry cacheEntry = _serviceCache.GetOrAdd(
                serviceName,
                _ => new ServiceCacheEntry());
            await cacheEntry.RefreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Once the service-level Telnet result (including an unavailable result) is
                // known, it is self-contained. Do not make a cached lookup depend on another
                // registry read, especially while the registry is temporarily unavailable.
                if (cacheEntry.TelnetState == TelnetProbeState.Available)
                {
                    return DubboTelnetMetadataResult.Success(cacheEntry.TelnetMethods);
                }
                if (cacheEntry.TelnetState == TelnetProbeState.Unavailable)
                {
                    return DubboTelnetMetadataResult.Failure(
                        cacheEntry.TelnetFailureReason);
                }

                IReadOnlyList<DubboServiceEndPointInfo> providers;
                try
                {
                    providers = await _providerResolver(serviceName).ConfigureAwait(false)
                        ?? Array.Empty<DubboServiceEndPointInfo>();
                }
                catch (Exception exception)
                {
                    // Provider discovery is recoverable and is not evidence that the service has
                    // Telnet disabled. Do not poison the service-level negative Telnet cache.
                    return DubboTelnetMetadataResult.Failure(
                        $"Telnet could not be attempted because provider discovery failed: {exception.Message}");
                }

                return await GetOrLoadTelnetMetadataAsync(serviceName, providers, cacheEntry)
                    .ConfigureAwait(false);
            }
            finally
            {
                cacheEntry.RefreshLock.Release();
            }
        }

        /// <summary>
        /// 在已有缓存项存在时刷新服务元数据；该方法通常由 ZooKeeper watcher 调用。
        /// <para>EN: Refreshes service metadata when a cache entry exists; normally called by a ZooKeeper watcher.</para>
        /// </summary>
        /// <param name="serviceName">Dubbo 接口全限定名。<para>EN: Fully qualified Dubbo interface name.</para></param>
        /// <returns>表示刷新操作的任务。<para>EN: A task representing the refresh operation.</para></returns>
        public async Task RefreshCachedServiceAsync(string serviceName)
        {
            if (_disposed || !HasCachedService(serviceName))
            {
                return;
            }

            try
            {
                await GetServiceMethodsAsync(serviceName, true).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MyLogger.LogWarning(
                    $"[DubboMetadataManager] refresh metadata for {serviceName} failed: " +
                    exception.Message);
            }
        }

        internal static string BuildMetadataPath(
            string metadataRootPath,
            string serviceName,
            string version,
            string group,
            string application)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException("Dubbo service name cannot be empty.", nameof(serviceName));
            }
            if (string.IsNullOrWhiteSpace(application))
            {
                throw new ArgumentException(
                    "Provider application is required to locate Dubbo metadata.",
                    nameof(application));
            }

            List<string> parts = new List<string>
            {
                NormalizeRootPath(metadataRootPath),
                "metadata",
                Uri.EscapeDataString(serviceName)
            };
            if (!string.IsNullOrEmpty(version))
            {
                parts.Add(version);
            }
            if (!string.IsNullOrEmpty(group))
            {
                parts.Add(group);
            }
            parts.Add("provider");
            parts.Add(application);
            return string.Join("/", parts.Select((part, index) =>
                index == 0 ? part.TrimEnd('/') : part.Trim('/')));
        }

        internal static IReadOnlyList<DubboMethodMetadata> ParseMetadataDocument(
            byte[] data,
            string serviceName,
            DubboServiceEndPointInfo provider,
            string metadataPath,
            int metadataVersion,
            DateTimeOffset loadedAt)
        {
            return ParseMetadataDocumentDetails(
                data,
                serviceName,
                provider,
                metadataPath,
                metadataVersion,
                loadedAt).Methods;
        }

        private static ParsedMetadataDocument ParseMetadataDocumentDetails(
            byte[] data,
            string serviceName,
            DubboServiceEndPointInfo provider,
            string metadataPath,
            int metadataVersion,
            DateTimeOffset loadedAt)
        {
            if (data == null || data.Length == 0)
            {
                return new ParsedMetadataDocument();
            }

            ProviderMetadataDocument document;
            try
            {
                document = JsonSerializer.Deserialize<ProviderMetadataDocument>(data, JsonOptions);
            }
            catch (JsonException exception)
            {
                string preview = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256));
                throw new DubboMetadataException(
                    $"Dubbo metadata node '{metadataPath}' does not contain a valid " +
                    $"FullServiceDefinition JSON document. {exception.Message} " +
                    $"Data starts with: {preview}",
                    exception);
            }

            if (document == null)
            {
                return new ParsedMetadataDocument();
            }

            string effectiveServiceName = string.IsNullOrWhiteSpace(document.CanonicalName)
                ? serviceName
                : document.CanonicalName;
            IReadOnlyDictionary<string, string> parameters =
                document.Parameters ?? new Dictionary<string, string>();
            IReadOnlyList<DubboTypeMetadata> types =
                document.Types ?? new List<DubboTypeMetadata>();
            MaterializeTypeReferences(types);

            foreach (DubboMethodMetadata method in document.Methods
                ?? new List<DubboMethodMetadata>())
            {
                method.ServiceName = effectiveServiceName;
                method.ParameterTypes ??= Array.Empty<string>();
                method.Version = provider?.Version;
                method.Group = provider?.Group;
                method.Application = provider?.Application;
                method.MetadataPath = metadataPath;
                method.CodeSource = document.CodeSource;
                method.MetadataSource = DubboMetadataSource.MetadataCenter;
                method.ServiceParameters = parameters;
                method.TypeDefinitions = types;
                method.MetadataVersion = metadataVersion;
                method.LoadedAt = loadedAt;
            }

            return new ParsedMetadataDocument
            {
                Methods = document.Methods ?? new List<DubboMethodMetadata>(),
                Types = types,
                IsDocument = true
            };
        }

        private async Task<ServiceMetadataSnapshot> LoadServiceMetadataAsync(
            string serviceName,
            ServiceCacheEntry cacheEntry,
            bool forceRefresh)
        {
            IReadOnlyList<DubboServiceEndPointInfo> providers;
            try
            {
                providers = await _providerResolver(serviceName).ConfigureAwait(false)
                    ?? Array.Empty<DubboServiceEndPointInfo>();
            }
            catch (Exception exception)
            {
                if (forceRefresh)
                {
                    cacheEntry.MetadataCenterSnapshot = null;
                }
                UpdatePathMappings(
                    serviceName,
                    cacheEntry,
                    new HashSet<string>(StringComparer.Ordinal));
                return new ServiceMetadataSnapshot
                {
                    CanCache = false,
                    FailureReason = BuildFailureReason(
                        serviceName,
                        Array.Empty<DubboServiceEndPointInfo>(),
                        true,
                        $"Provider discovery failed: {exception.Message}",
                        "Telnet was not attempted because no provider endpoint could be discovered.")
                };
            }

            bool preferMetadataCenter =
                DubboTelnetMetadataResolver.ShouldPreferMetadataCenter(providers);
            MetadataCenterLoadResult metadataCenterResult = null;
            if (preferMetadataCenter)
            {
                metadataCenterResult = forceRefresh
                    ? null
                    : cacheEntry.MetadataCenterSnapshot;
                if (metadataCenterResult == null)
                {
                    metadataCenterResult = await LoadFromMetadataCenterAsync(
                        serviceName,
                        providers,
                        cacheEntry).ConfigureAwait(false);
                    CacheMetadataCenterResult(cacheEntry, metadataCenterResult);
                }
                if (metadataCenterResult.Methods.Count > 0)
                {
                    return new ServiceMetadataSnapshot
                    {
                        Methods = metadataCenterResult.Methods,
                        ExpectedPaths = metadataCenterResult.ExpectedPaths,
                        CanCache = metadataCenterResult.CanCacheFallback
                    };
                }
            }
            else
            {
                // Known providers below 2.7.3 do not use the 2.7.3 FullServiceDefinition path.
                cacheEntry.MetadataCenterSnapshot = null;
                UpdatePathMappings(
                    serviceName,
                    cacheEntry,
                    new HashSet<string>(StringComparer.Ordinal));
            }

            DubboTelnetMetadataResult telnetResult = await GetOrLoadTelnetMetadataAsync(
                serviceName,
                providers,
                cacheEntry).ConfigureAwait(false);
            if (telnetResult.Methods.Count > 0)
            {
                return new ServiceMetadataSnapshot
                {
                    Methods = telnetResult.Methods,
                    ExpectedPaths = metadataCenterResult?.ExpectedPaths
                        ?? Array.Empty<string>(),
                    CanCache = !preferMetadataCenter
                        || metadataCenterResult?.CanCacheFallback != false
                };
            }

            return new ServiceMetadataSnapshot
            {
                ExpectedPaths = metadataCenterResult?.ExpectedPaths
                    ?? Array.Empty<string>(),
                CanCache = !preferMetadataCenter
                    || metadataCenterResult?.CanCacheFallback != false,
                FailureReason = BuildFailureReason(
                    serviceName,
                    providers,
                    preferMetadataCenter,
                    metadataCenterResult?.FailureReason,
                    telnetResult.FailureReason)
            };
        }

        private async Task<MetadataCenterLoadResult> LoadFromMetadataCenterAsync(
            string serviceName,
            IReadOnlyList<DubboServiceEndPointInfo> providers,
            ServiceCacheEntry cacheEntry)
        {
            List<(string Path, DubboServiceEndPointInfo Provider)> metadataLocations = providers
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.Application))
                .Select(provider => (
                    BuildMetadataPath(
                        _metadataRootPath,
                        serviceName,
                        provider.Version,
                        provider.Group,
                        provider.Application),
                    provider))
                .GroupBy(location => location.Item1, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            HashSet<string> newPaths = new HashSet<string>(
                metadataLocations.Select(location => location.Path),
                StringComparer.Ordinal);
            UpdatePathMappings(serviceName, cacheEntry, newPaths);

            List<DubboMethodMetadata> methods = new List<DubboMethodMetadata>();
            List<DubboTypeMetadata> types = new List<DubboTypeMetadata>();
            List<string> failures = new List<string>();
            bool hasMetadataDocument = false;
            bool hasUnwatchedFailure = false;
            foreach ((string path, DubboServiceEndPointInfo provider) in metadataLocations)
            {
                org.apache.zookeeper.data.Stat metadataStat;
                try
                {
                    // 先检查完整的元数据叶子路径。节点不存在时 exists 会注册创建监听，
                    // 无需先执行 getData 并制造一次可预期的 NoNodeException。
                    // EN: Check the full metadata leaf path first. When it is absent, exists
                    // registers a creation watch without producing an expected NoNodeException.
                    metadataStat = await _metadataExists(path, _watcher).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    hasUnwatchedFailure = true;
                    failures.Add(
                        $"node '{path}' existence could not be checked or watched: " +
                        exception.Message);
                    continue;
                }

                if (metadataStat == null)
                {
                    failures.Add($"node '{path}' does not exist");
                    continue;
                }

                org.apache.zookeeper.DataResult dataResult;
                try
                {
                    dataResult = await _metadataDataLoader(path, _watcher)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // No data watch was installed. A cached Telnet answer may still be returned to
                    // this caller, but the next call must retry the preferred metadata center.
                    hasUnwatchedFailure = true;
                    failures.Add($"node '{path}' could not be read: {exception.Message}");
                    continue;
                }

                if (dataResult == null || dataResult.Data == null)
                {
                    try
                    {
                        // The node may have been deleted between exists and getData. Reinstall an
                        // exists watch so later publication replaces a cached Telnet fallback.
                        await _metadataExists(path, _watcher).ConfigureAwait(false);
                        failures.Add($"node '{path}' disappeared before its data could be read");
                    }
                    catch (Exception exception)
                    {
                        hasUnwatchedFailure = true;
                        failures.Add(
                            $"node '{path}' disappeared and its creation could not be watched: " +
                            exception.Message);
                    }
                    continue;
                }

                try
                {
                    int metadataVersion = dataResult.Stat?.getVersion() ?? -1;
                    ParsedMetadataDocument parsed = ParseMetadataDocumentDetails(
                        dataResult.Data,
                        serviceName,
                        provider,
                        path,
                        metadataVersion,
                        DateTimeOffset.UtcNow);
                    hasMetadataDocument |= parsed.IsDocument;
                    types.AddRange(parsed.Types);
                    if (parsed.Methods.Count == 0)
                    {
                        failures.Add($"node '{path}' contains no method definitions");
                        continue;
                    }
                    methods.AddRange(parsed.Methods);
                }
                catch (Exception exception)
                {
                    // getData succeeded, so its watch will invalidate this stable parse failure
                    // when the document changes. One damaged document must not hide another one.
                    failures.Add($"node '{path}' failed: {exception.Message}");
                }
            }

            string failureReason = null;
            if (methods.Count == 0)
            {
                failureReason = metadataLocations.Count == 0
                    ? "No metadata path could be derived because the provider URL does not report an application name."
                    : "No FullServiceDefinition could be loaded. " + string.Join("; ", failures);
            }
            return new MetadataCenterLoadResult
            {
                Methods = methods.ToArray(),
                Types = DeduplicateTypeDefinitions(types),
                ExpectedPaths = newPaths.ToArray(),
                FailureReason = failureReason,
                HasMetadataDocument = hasMetadataDocument,
                CanCacheFallback = !hasUnwatchedFailure
            };
        }

        private async Task<DubboTelnetMetadataResult> GetOrLoadTelnetMetadataAsync(
            string serviceName,
            IReadOnlyList<DubboServiceEndPointInfo> providers,
            ServiceCacheEntry cacheEntry)
        {
            if (cacheEntry.TelnetState == TelnetProbeState.Available)
            {
                return DubboTelnetMetadataResult.Success(cacheEntry.TelnetMethods);
            }
            if (cacheEntry.TelnetState == TelnetProbeState.Unavailable)
            {
                return DubboTelnetMetadataResult.Failure(cacheEntry.TelnetFailureReason);
            }

            DubboTelnetMetadataResult result;
            try
            {
                result = await _telnetMetadataLoader(serviceName, providers)
                    .ConfigureAwait(false)
                    ?? DubboTelnetMetadataResult.Failure(
                        "The Telnet metadata loader returned no result.");
            }
            catch (Exception exception)
            {
                result = DubboTelnetMetadataResult.Failure(
                    $"The Telnet metadata probe failed: {exception.Message}");
            }

            if (result.Methods.Count > 0)
            {
                cacheEntry.TelnetState = TelnetProbeState.Available;
                cacheEntry.TelnetMethods = result.Methods.ToArray();
                cacheEntry.TelnetFailureReason = null;
                return DubboTelnetMetadataResult.Success(cacheEntry.TelnetMethods);
            }

            // Negative caching is deliberate: Telnet is configured per service process, so a
            // service which rejected the first probe must not receive a connection on every call.
            cacheEntry.TelnetState = TelnetProbeState.Unavailable;
            cacheEntry.TelnetMethods = Array.Empty<DubboMethodMetadata>();
            cacheEntry.TelnetFailureReason = result.FailureReason;
            return DubboTelnetMetadataResult.Failure(cacheEntry.TelnetFailureReason);
        }

        private static void CacheMetadataCenterResult(
            ServiceCacheEntry cacheEntry,
            MetadataCenterLoadResult result)
        {
            cacheEntry.MetadataCenterSnapshot = result?.CanCacheFallback == true
                ? result
                : null;
        }

        private static IReadOnlyList<DubboTypeMetadata> EnsureTypeDefinitionsAvailable(
            string serviceName,
            MetadataCenterLoadResult result)
        {
            if (result?.HasMetadataDocument == true)
            {
                return result.Types ?? Array.Empty<DubboTypeMetadata>();
            }

            string expectedPaths = result?.ExpectedPaths?.Count > 0
                ? string.Join(", ", result.ExpectedPaths)
                : "none could be derived";
            throw new DubboMetadataException(
                $"Unable to obtain FullServiceDefinition.types for Dubbo service " +
                $"'{serviceName}' from the metadata center. " +
                $"Expected metadata path(s): {expectedPaths}. " +
                $"{result?.FailureReason ?? "No FullServiceDefinition document was found."} " +
                "Telnet metadata cannot provide POJO type structures.");
        }

        private static IReadOnlyList<DubboTypeMetadata> DeduplicateTypeDefinitions(
            IEnumerable<DubboTypeMetadata> types)
        {
            HashSet<string> namedTypes = new HashSet<string>(StringComparer.Ordinal);
            List<DubboTypeMetadata> result = new List<DubboTypeMetadata>();
            foreach (DubboTypeMetadata type in types ?? Array.Empty<DubboTypeMetadata>())
            {
                if (type == null)
                {
                    continue;
                }

                string key = !string.IsNullOrWhiteSpace(type.Id)
                    ? type.Id
                    : type.Type;
                if (string.IsNullOrWhiteSpace(key) || namedTypes.Add(key))
                {
                    // Keep anonymous/reference entries in their original positions. Dubbo 2.7
                    // emits JSONPath references such as $.types[3].properties.pageNo; moving all
                    // anonymous entries to the end changes those indices and corrupts the schema.
                    result.Add(type);
                }
            }
            return result;
        }

        private static void MaterializeTypeReferences(IReadOnlyList<DubboTypeMetadata> types)
        {
            Dictionary<string, DubboTypeMetadata> paths =
                new Dictionary<string, DubboTypeMetadata>(StringComparer.Ordinal);
            for (int index = 0; index < (types?.Count ?? 0); index++)
            {
                IndexTypeMetadata(
                    types[index],
                    $"$.types[{index}]",
                    $"#/types/{index}",
                    paths);
            }

            HashSet<DubboTypeMetadata> resolved = new HashSet<DubboTypeMetadata>();
            HashSet<DubboTypeMetadata> resolving = new HashSet<DubboTypeMetadata>();
            foreach (DubboTypeMetadata type in types ?? Array.Empty<DubboTypeMetadata>())
            {
                MaterializeTypeReference(type, paths, resolved, resolving);
            }
        }

        private static void IndexTypeMetadata(
            DubboTypeMetadata metadata,
            string jsonPath,
            string pointerPath,
            IDictionary<string, DubboTypeMetadata> paths)
        {
            if (metadata == null)
            {
                return;
            }

            paths[jsonPath] = metadata;
            paths[pointerPath] = metadata;
            for (int index = 0; index < (metadata.Items?.Count ?? 0); index++)
            {
                IndexTypeMetadata(
                    metadata.Items[index],
                    $"{jsonPath}.items[{index}]",
                    $"{pointerPath}/items/{index}",
                    paths);
            }
            foreach (KeyValuePair<string, DubboTypeMetadata> property in
                metadata.Properties ?? new Dictionary<string, DubboTypeMetadata>())
            {
                IndexTypeMetadata(
                    property.Value,
                    $"{jsonPath}.properties.{property.Key}",
                    $"{pointerPath}/properties/{EscapeJsonPointerSegment(property.Key)}",
                    paths);
            }
        }

        private static void MaterializeTypeReference(
            DubboTypeMetadata metadata,
            IReadOnlyDictionary<string, DubboTypeMetadata> paths,
            ISet<DubboTypeMetadata> resolved,
            ISet<DubboTypeMetadata> resolving)
        {
            if (metadata == null || resolved.Contains(metadata) || !resolving.Add(metadata))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Reference)
                && paths.TryGetValue(metadata.Reference, out DubboTypeMetadata target)
                && !ReferenceEquals(metadata, target))
            {
                MaterializeTypeReference(target, paths, resolved, resolving);
                string targetType = !string.IsNullOrWhiteSpace(target.Id)
                    && IsTypeCategory(target.Type)
                        ? target.Id
                        : target.Type ?? target.Id;
                if (string.IsNullOrWhiteSpace(metadata.Type)
                    && !string.IsNullOrWhiteSpace(targetType))
                {
                    metadata.Type = targetType;
                }
                if (string.IsNullOrWhiteSpace(metadata.TypeBuilderName))
                {
                    metadata.TypeBuilderName = target.TypeBuilderName;
                }
                if ((metadata.EnumValues?.Count ?? 0) == 0
                    && (target.EnumValues?.Count ?? 0) > 0)
                {
                    metadata.EnumValues = target.EnumValues.ToList();
                }
            }

            foreach (DubboTypeMetadata item in metadata.Items
                ?? new List<DubboTypeMetadata>())
            {
                MaterializeTypeReference(item, paths, resolved, resolving);
            }
            foreach (DubboTypeMetadata property in (metadata.Properties
                ?? new Dictionary<string, DubboTypeMetadata>()).Values)
            {
                MaterializeTypeReference(property, paths, resolved, resolving);
            }

            resolving.Remove(metadata);
            resolved.Add(metadata);
        }

        private static bool IsTypeCategory(string type)
        {
            return type != null && (type.Equals("object", StringComparison.OrdinalIgnoreCase)
                || type.Equals("array", StringComparison.OrdinalIgnoreCase)
                || type.Equals("collection", StringComparison.OrdinalIgnoreCase)
                || type.Equals("map", StringComparison.OrdinalIgnoreCase)
                || type.Equals("enum", StringComparison.OrdinalIgnoreCase)
                || type.Equals("string", StringComparison.OrdinalIgnoreCase)
                || type.Equals("number", StringComparison.OrdinalIgnoreCase)
                || type.Equals("boolean", StringComparison.OrdinalIgnoreCase));
        }

        private static string EscapeJsonPointerSegment(string value)
        {
            return value
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
        }

        private static string BuildFailureReason(
            string serviceName,
            IReadOnlyList<DubboServiceEndPointInfo> providers,
            bool metadataCenterWasPreferred,
            string metadataCenterFailure,
            string telnetFailure)
        {
            string releases = string.Join(
                ", ",
                (providers ?? Array.Empty<DubboServiceEndPointInfo>())
                    .Select(provider => string.IsNullOrWhiteSpace(provider?.Release)
                        ? "unknown"
                        : provider.Release)
                    .Distinct(StringComparer.Ordinal));
            if (string.IsNullOrWhiteSpace(releases))
            {
                releases = "unknown";
            }

            string metadataCenterPart = metadataCenterWasPreferred
                ? $"Metadata center: {metadataCenterFailure ?? "no usable method definitions were returned"}."
                : "Metadata center: skipped because every reported provider release is below 2.7.3.";
            string telnetPart =
                $"Telnet fallback: {telnetFailure ?? "no usable method definitions were returned"}.";
            return
                $"Unable to obtain method metadata for Dubbo service '{serviceName}' " +
                $"(provider release(s): {releases}). {metadataCenterPart} {telnetPart} " +
                "Automatic Java parameter type inference cannot continue. Retry with the explicit " +
                "type overload QueryGenericAsync(funcEndPoint, javaParameterTypes, arguments), " +
                "supplying the exact Java parameter type names.";
        }

        private static IReadOnlyList<DubboMethodMetadata> EnsureMetadataAvailable(
            string serviceName,
            ServiceMetadataSnapshot snapshot)
        {
            if (snapshot.Methods.Count > 0)
            {
                return snapshot.Methods;
            }

            throw new DubboMetadataException(
                snapshot.FailureReason
                ?? $"No Dubbo method metadata was found for service '{serviceName}'. " +
                   "Retry with QueryGenericAsync(funcEndPoint, javaParameterTypes, arguments) " +
                   "and supply the exact Java parameter type names.");
        }

        private void UpdatePathMappings(
            string serviceName,
            ServiceCacheEntry cacheEntry,
            HashSet<string> newPaths)
        {
            foreach (string oldPath in cacheEntry.WatchedPaths)
            {
                if (!newPaths.Contains(oldPath)
                    && _pathToService.TryGetValue(oldPath, out string owner)
                    && string.Equals(owner, serviceName, StringComparison.Ordinal))
                {
                    _pathToService.TryRemove(oldPath, out _);
                }
            }
            foreach (string path in newPaths)
            {
                _pathToService[path] = serviceName;
            }
            cacheEntry.WatchedPaths = newPaths;
        }

        private async Task ProcessWatcherEventAsync(WatchedEvent @event)
        {
            if (_disposed || @event == null)
            {
                return;
            }

            if (@event.get_Type() == Watcher.Event.EventType.None)
            {
                if (@event.getState() == Watcher.Event.KeeperState.Disconnected)
                {
                    Interlocked.Exchange(ref _connectionNeedsReload, 1);
                    return;
                }

                if (@event.getState() == Watcher.Event.KeeperState.Expired)
                {
                    // An expired session loses every registered watch. Reloading performs a
                    // reconnect through MyZookeeper and installs fresh data/exists watches.
                    Interlocked.Exchange(ref _connectionNeedsReload, 1);
                    await ReloadAllCachedServicesAsync().ConfigureAwait(false);
                    return;
                }

                if (@event.getState() == Watcher.Event.KeeperState.SyncConnected
                    && Interlocked.Exchange(ref _connectionNeedsReload, 0) == 1)
                {
                    await ReloadAllCachedServicesAsync().ConfigureAwait(false);
                }
                return;
            }

            string path = @event.getPath();
            if (string.IsNullOrEmpty(path)
                || !_pathToService.TryGetValue(path, out string serviceName))
            {
                return;
            }

            if (@event.get_Type() == Watcher.Event.EventType.NodeDataChanged
                || @event.get_Type() == Watcher.Event.EventType.NodeCreated
                || @event.get_Type() == Watcher.Event.EventType.NodeDeleted)
            {
                await RefreshCachedServiceAsync(serviceName).ConfigureAwait(false);
            }
        }

        private async Task ReloadAllCachedServicesAsync()
        {
            foreach (string serviceName in _serviceCache.Keys)
            {
                await RefreshCachedServiceAsync(serviceName).ConfigureAwait(false);
            }
        }

        private static string NormalizeRootPath(string rootPath)
        {
            string normalized = string.IsNullOrWhiteSpace(rootPath) ? "/dubbo" : rootPath.Trim();
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }
            return normalized.TrimEnd('/');
        }

        /// <summary>
        /// 释放缓存项及其同步资源。
        /// <para>EN: Releases cache entries and their synchronization resources.</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (ServiceCacheEntry entry in _serviceCache.Values)
            {
                entry.Dispose();
            }
            _serviceCache.Clear();
            _pathToService.Clear();
        }
    }
}
