using DubboNet.Clients.DataModle;
using Microsoft.Extensions.DependencyInjection;
using MyCommonHelper;
using Nacos.V2;
using Nacos.V2.DependencyInjection;
using Nacos.V2.Naming.Dtos;
using Nacos.V2.Remote;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DubboNet.Clients.RegistryClient
{
    /// <summary>
    /// 封装 DubboNet 使用的 Nacos Naming 与 Config 操作，包括服务发现、订阅和远程元数据读取。
    /// <para>EN: Wraps the Nacos Naming and Config operations used by DubboNet, including discovery, subscriptions, and remote metadata reads.</para>
    /// </summary>
    internal sealed class MyNacos : IDisposable
    {
        private sealed class NamingListener : IEventListener
        {
            private readonly NamingSubscription _subscription;

            public NamingListener(NamingSubscription subscription)
            {
                _subscription = subscription;
            }

            public Task OnEvent(IEvent @event) => _subscription.NotifyAsync();
        }

        private sealed class NamingSubscription
        {
            private readonly object _syncRoot = new object();
            private readonly Dictionary<object, Func<string, Task>> _callbacks =
                new Dictionary<object, Func<string, Task>>();

            public string ServiceName { get; init; }
            public string GroupName { get; init; }
            public string InterfaceName { get; init; }
            public IEventListener Listener { get; set; }
            public HashSet<string> ServiceNames { get; set; } = new HashSet<string>(StringComparer.Ordinal);

            public KeyValuePair<object, Func<string, Task>>[] Callbacks
            {
                get { lock (_syncRoot) return _callbacks.ToArray(); }
            }

            public void Stop()
            {
                lock (_syncRoot) _callbacks.Clear();
            }

            public void Add(object subscriber, Func<string, Task> callback)
            {
                lock (_syncRoot)
                {
                    _callbacks[subscriber] = callback;
                }
            }

            public bool Remove(object subscriber)
            {
                lock (_syncRoot)
                {
                    _callbacks.Remove(subscriber);
                    return _callbacks.Count == 0;
                }
            }

            public async Task NotifyAsync()
            {
                Func<string, Task>[] callbacks;
                lock (_syncRoot)
                {
                    callbacks = _callbacks.Values.ToArray();
                }
                foreach (Func<string, Task> callback in callbacks)
                {
                    try
                    {
                        await callback(InterfaceName).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        MyLogger.LogWarning(
                            $"[MyNacos] provider-change callback for {InterfaceName} failed: {exception.Message}");
                    }
                }
            }
        }

        private sealed class ConfigListener : IListener
        {
            private readonly ConfigSubscription _subscription;

            public ConfigListener(ConfigSubscription subscription)
            {
                _subscription = subscription;
            }

            public void ReceiveConfigInfo(string configInfo)
            {
                _subscription.Receive(configInfo);
            }
        }

        private sealed class ConfigSubscription
        {
            private readonly object _syncRoot = new object();
            private readonly Dictionary<object, Func<Task>> _callbacks =
                new Dictionary<object, Func<Task>>();

            public string DataId { get; init; }
            public string GroupName { get; init; }
            private string _content;
            private bool _receivedNotification;
            private bool _initialized;
            private bool _stopped;
            public string Content { get { lock (_syncRoot) return _content; } }
            public IListener Listener { get; set; }

            // 初始化读不能覆盖已到达的推送（包括配置删除的 null）。
            // EN: The initial read must not overwrite an earlier push, including a null deletion.
            public void CompleteInitialization(string content)
            {
                bool changed;
                lock (_syncRoot)
                {
                    if (!_receivedNotification) _content = content;
                    _initialized = true;
                    changed = _receivedNotification && !_stopped;
                }
                if (changed) _ = NotifyAsync();
            }

            public void Receive(string content)
            {
                bool notify;
                lock (_syncRoot)
                {
                    if (_stopped) return;
                    _content = content;
                    _receivedNotification = true;
                    notify = _initialized;
                }
                if (notify) _ = NotifyAsync();
            }

            public void Stop()
            {
                lock (_syncRoot)
                {
                    _stopped = true;
                    _callbacks.Clear();
                }
            }

            public void Add(object subscriber, Func<Task> callback)
            {
                lock (_syncRoot)
                {
                    _callbacks[subscriber] = callback;
                }
            }

            public bool Remove(object subscriber)
            {
                lock (_syncRoot)
                {
                    _callbacks.Remove(subscriber);
                    return _callbacks.Count == 0;
                }
            }

            public async Task NotifyAsync()
            {
                Func<Task>[] callbacks;
                lock (_syncRoot)
                {
                    callbacks = _callbacks.Values.ToArray();
                }
                foreach (Func<Task> callback in callbacks)
                {
                    try
                    {
                        await callback().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        MyLogger.LogWarning(
                            $"[MyNacos] metadata-change callback for {DataId} failed: {exception.Message}");
                    }
                }
            }
        }

        private sealed class NacosConnectionOptions
        {
            public List<string> ServerAddresses { get; init; }
            public string Namespace { get; init; }
            public string UserName { get; init; }
            public string Password { get; init; }
            public string AccessKey { get; init; }
            public string SecretKey { get; init; }
        }

        private const string NacosPrefix = "nacos://";
        private const string MetadataLocationPrefix = "nacos-config://";
        private const int NacosPageSize = 500;
        private const long ConfigTimeoutMilliseconds = 10_000;

        private readonly IServiceProvider _serviceProvider;
        private readonly INacosNamingService _namingService;
        private readonly INacosConfigService _configService;
        private readonly ConcurrentDictionary<string, NamingSubscription> _namingSubscriptions =
            new ConcurrentDictionary<string, NamingSubscription>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConfigSubscription> _configSubscriptions =
            new ConcurrentDictionary<string, ConfigSubscription>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _subscriptionLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _catalogRefreshLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, NamingSubscription> _interfaces = new(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<object, object> _removedSubscribers = new();
        private readonly Timer _catalogTimer;
        private readonly TimeSpan _catalogRefreshInterval;
        private volatile bool _disposed;

        /// <summary>
        /// 使用 <c>nacos://</c> 连接字符串创建 Naming 与 Config 客户端。
        /// <para>EN: Creates Naming and Config clients from a <c>nacos://</c> connection string.</para>
        /// </summary>
        public MyNacos(string connectionString)
        {
            ConnectionString = NormalizeConnectionString(connectionString);
            NacosConnectionOptions parsed = ParseConnectionString(ConnectionString);
            ServiceCollection services = new ServiceCollection();
            services.AddLogging();
            services.AddNacosV2Naming(options => ApplyOptions(options, parsed));
            services.AddNacosV2Config(options => ApplyOptions(options, parsed));
            _serviceProvider = services.BuildServiceProvider();
            _namingService = _serviceProvider.GetRequiredService<INacosNamingService>();
            _configService = _serviceProvider.GetRequiredService<INacosConfigService>();
            _catalogRefreshInterval = TimeSpan.FromSeconds(30);
            _catalogTimer = new Timer(_ => _ = RefreshServiceCatalogAsync(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // 注入 SDK 接口用于离线验证订阅竞争及多调用方隔离。
        // EN: Inject SDK interfaces for deterministic subscription and ownership tests.
        internal MyNacos(string connectionString, INacosNamingService namingService,
            INacosConfigService configService, TimeSpan refreshInterval)
        {
            ConnectionString = NormalizeConnectionString(connectionString);
            _namingService = namingService;
            _configService = configService;
            _catalogRefreshInterval = refreshInterval;
            _catalogTimer = new Timer(_ => _ = RefreshServiceCatalogAsync(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public string ConnectionString { get; }

        /// <summary>
        /// 判断连接字符串是否选择 Nacos 注册中心。
        /// <para>EN: Determines whether a connection string selects the Nacos registry.</para>
        /// </summary>
        public static bool IsNacosConnectionString(string connectionString) =>
            !string.IsNullOrWhiteSpace(connectionString)
            && connectionString.TrimStart().StartsWith(NacosPrefix, StringComparison.OrdinalIgnoreCase);

        public static string NormalizeConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("Nacos connection string cannot be empty.", nameof(connectionString));
            }
            string normalized = connectionString.Trim();
            if (!IsNacosConnectionString(normalized))
            {
                throw new ArgumentException(
                    "Nacos connection string must start with 'nacos://'.",
                    nameof(connectionString));
            }
            string remainder = normalized.Substring(NacosPrefix.Length);
            int queryIndex = remainder.IndexOf('?');
            string serverPart = queryIndex < 0 ? remainder : remainder.Substring(0, queryIndex);
            string query = queryIndex < 0 ? string.Empty : remainder.Substring(queryIndex + 1);
            string canonicalServers = string.Join(",", serverPart
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(CanonicalizeServer)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(address => address, StringComparer.Ordinal));
            if (canonicalServers.Length == 0)
                throw new ArgumentException("Nacos connection string does not contain a server address.", nameof(connectionString));
            var values = HttpUtility.ParseQueryString(query);
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in values.AllKeys)
            {
                if (key == null) throw new ArgumentException("Invalid Nacos query parameter.", nameof(connectionString));
                string canonicalKey = key.Equals("namespaceId", StringComparison.OrdinalIgnoreCase)
                    ? "namespace" : key.ToLowerInvariant();
                if (values.GetValues(key)?.Length != 1 || parameters.ContainsKey(canonicalKey))
                    throw new ArgumentException("Duplicate Nacos query parameter: " + key, nameof(connectionString));
                string value = values[key];
                // public 与未填写表示同一个命名空间；其他值及凭据保留大小写。
                // EN: public and an omitted namespace are equivalent; all other values remain case sensitive.
                if (canonicalKey == "namespace" && (string.IsNullOrEmpty(value) || value == "public")) continue;
                parameters[canonicalKey] = value;
            }
            query = string.Join("&", parameters.Select(item =>
                Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value ?? string.Empty)));
            return NacosPrefix + canonicalServers
                + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query);
        }

        private static string CanonicalizeServer(string address)
        {
            address = address.Trim().TrimEnd('/');
            if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0
                || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("Invalid Nacos server address.", nameof(address));
            string host = uri.Host.ToLowerInvariant();
            if (uri.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
            string authority = address.Substring(address.IndexOf("://", StringComparison.Ordinal) + 3);
            bool hasPort = authority.StartsWith("[", StringComparison.Ordinal)
                ? authority.Substring(authority.IndexOf(']') + 1).StartsWith(":", StringComparison.Ordinal)
                : authority.Contains(':');
            return uri.Scheme + "://" + host + ":" + (hasPort ? uri.Port : 8848);
        }

        /// <summary>
        /// 发现 Dubbo 接口的健康 Provider，并为调用方注册服务变化订阅。
        /// <para>EN: Discovers healthy providers for a Dubbo interface and registers a service-change subscription for the caller.</para>
        /// </summary>
        public async Task<IReadOnlyList<DubboServiceEndPointInfo>> GetProviderEndPointsAsync(
            string interfaceName,
            string registryGroup,
            object subscriber,
            Func<string, Task> changed)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(interfaceName))
            {
                throw new ArgumentException("Dubbo interface name cannot be empty.", nameof(interfaceName));
            }
            string effectiveGroup = NormalizeGroup(registryGroup, "DEFAULT_GROUP");
            List<string> matchingServices = await FindProviderServicesAsync(
                interfaceName,
                effectiveGroup).ConfigureAwait(false);
            await ReconcileNamingSubscriptionsAsync(interfaceName, effectiveGroup,
                matchingServices, subscriber, changed).ConfigureAwait(false);
            List<DubboServiceEndPointInfo> endpoints = new List<DubboServiceEndPointInfo>();
            foreach (string nacosServiceName in matchingServices)
            {
                List<Instance> instances = await _namingService.GetAllInstances(
                    nacosServiceName,
                    effectiveGroup,
                    false).ConfigureAwait(false);
                foreach (Instance instance in instances ?? new List<Instance>())
                {
                    if (instance?.Healthy == true && instance.Enabled)
                    {
                        DubboServiceEndPointInfo endpoint = ConvertInstance(instance, interfaceName);
                        if (endpoint != null)
                        {
                            endpoints.Add(endpoint);
                        }
                    }
                }
            }
            return endpoints
                .GroupBy(item => $"{item.Scheme}://{item.EndPoint}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        /// <summary>
        /// 构造 Dubbo 远程元数据在 Nacos Config 中的位置标识。
        /// <para>EN: Builds the location identifier of a Dubbo remote-metadata document in Nacos Config.</para>
        /// </summary>
        public static string BuildMetadataLocation(
            string metadataGroup,
            string serviceName,
            string version,
            string serviceGroup,
            string application)
        {
            string dataId = string.Join(":", new[]
            {
                serviceName,
                version ?? string.Empty,
                serviceGroup ?? string.Empty,
                "provider",
                application
            });
            return MetadataLocationPrefix
                + Uri.EscapeDataString(NormalizeGroup(metadataGroup))
                + "/"
                + Uri.EscapeDataString(dataId);
        }

        /// <summary>
        /// 检查元数据配置是否存在，并确保已注册变化监听。
        /// <para>EN: Checks whether a metadata configuration exists and ensures that a change listener is registered.</para>
        /// </summary>
        public async Task<bool> MetadataExistsAsync(
            string location,
            object subscriber,
            Func<Task> changed)
        {
            ConfigSubscription subscription = await EnsureConfigSubscriptionAsync(
                location,
                subscriber,
                changed).ConfigureAwait(false);
            return subscription.Content != null;
        }

        /// <summary>
        /// 读取已定位的 Nacos Config 元数据正文。
        /// <para>EN: Reads the metadata content identified in Nacos Config.</para>
        /// </summary>
        public async Task<byte[]> GetMetadataAsync(string location)
        {
            ThrowIfDisposed();
            ParseMetadataLocation(location, out string group, out string dataId);
            string key = GetConfigKey(group, dataId);
            string content;
            if (_configSubscriptions.TryGetValue(key, out ConfigSubscription subscription))
            {
                content = subscription.Content;
            }
            else
            {
                content = await _configService.GetConfig(
                    dataId,
                    group,
                    ConfigTimeoutMilliseconds).ConfigureAwait(false);
            }
            return content == null ? null : Encoding.UTF8.GetBytes(content);
        }

        /// <summary>
        /// 删除某个 DubboClient 或元数据管理器在共享 Nacos 客户端上的全部订阅。
        /// <para>EN: Removes all subscriptions owned by one DubboClient or metadata manager from the shared Nacos client.</para>
        /// </summary>
        public void RemoveSubscriber(object subscriber)
        {
            if (subscriber == null || _disposed)
            {
                return;
            }
            _subscriptionLock.Wait();
            try
            {
                _removedSubscribers.GetValue(subscriber, _ => new object());
                foreach (var item in _interfaces.ToArray())
                {
                    if (item.Value.Remove(subscriber)) _interfaces.TryRemove(item.Key, out _);
                }
                if (_interfaces.IsEmpty) _catalogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                foreach (KeyValuePair<string, NamingSubscription> item in _namingSubscriptions.ToArray())
                {
                    if (item.Value.Remove(subscriber)
                        && _namingSubscriptions.TryRemove(item.Key, out NamingSubscription removed))
                    {
                        removed.Stop();
                        TryWait(_namingService.Unsubscribe(
                            removed.ServiceName,
                            removed.GroupName,
                            removed.Listener));
                    }
                }
                foreach (KeyValuePair<string, ConfigSubscription> item in _configSubscriptions.ToArray())
                {
                    if (item.Value.Remove(subscriber)
                        && _configSubscriptions.TryRemove(item.Key, out ConfigSubscription removed))
                    {
                        removed.Stop();
                        TryWait(_configService.RemoveListener(
                            removed.DataId,
                            removed.GroupName,
                            removed.Listener));
                    }
                }
            }
            finally
            {
                _subscriptionLock.Release();
            }
        }

        public static string NormalizeGroup(string rootPath, string defaultGroup = "dubbo")
        {
            string normalized = string.IsNullOrWhiteSpace(rootPath) ? defaultGroup : rootPath.Trim();
            normalized = normalized.Trim('/');
            return string.IsNullOrWhiteSpace(normalized) ? defaultGroup : normalized;
        }

        private async Task<List<string>> FindProviderServicesAsync(
            string interfaceName,
            string groupName)
        {
            return (await GetServiceNamesAsync(groupName).ConfigureAwait(false))
                .Where(name => IsProviderServiceName(name, interfaceName)).ToList();
        }

        private async Task<List<string>> GetServiceNamesAsync(string groupName)
        {
            List<string> services = new List<string>();
            int pageNumber = 1;
            while (true)
            {
                ListView<string> page = await _namingService.GetServicesOfServer(
                    pageNumber,
                    NacosPageSize,
                    groupName).ConfigureAwait(false);
                if (page?.Data == null || page.Data.Count == 0)
                {
                    break;
                }
                services.AddRange(page.Data);
                if (pageNumber * NacosPageSize >= page.Count)
                {
                    break;
                }
                pageNumber++;
            }
            return services.Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool IsProviderServiceName(string nacosServiceName, string interfaceName)
        {
            if (string.IsNullOrWhiteSpace(nacosServiceName))
            {
                return false;
            }
            string prefix = "providers:" + interfaceName;
            return string.Equals(nacosServiceName, prefix, StringComparison.Ordinal)
                || nacosServiceName.StartsWith(prefix + ":", StringComparison.Ordinal)
                || string.Equals(nacosServiceName, interfaceName, StringComparison.Ordinal);
        }

        private async Task<bool> ReconcileNamingSubscriptionsAsync(string interfaceName,
            string groupName, List<string> names, object subscriber = null,
            Func<string, Task> changed = null)
        {
            string interfaceKey = groupName + "\n" + interfaceName;
            await _subscriptionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return false;
                if (subscriber != null && _removedSubscribers.TryGetValue(subscriber, out _)) return false;
                if (!_interfaces.TryGetValue(interfaceKey, out NamingSubscription interest))
                {
                    if (subscriber == null || changed == null) return false;
                    interest = new NamingSubscription { InterfaceName = interfaceName, GroupName = groupName };
                    _interfaces[interfaceKey] = interest;
                    _catalogTimer.Change(_catalogRefreshInterval, _catalogRefreshInterval);
                }
                if (subscriber != null && changed != null) interest.Add(subscriber, changed);
                bool changedNames = !interest.ServiceNames.SetEquals(names);
                foreach (string serviceName in names)
                {
                    string key = groupName + "\n" + serviceName;
                    if (!_namingSubscriptions.TryGetValue(key, out NamingSubscription subscription))
                    {
                        subscription = new NamingSubscription
                        {
                            ServiceName = serviceName, GroupName = groupName, InterfaceName = interfaceName
                        };
                        subscription.Listener = new NamingListener(subscription);
                        foreach (var callback in interest.Callbacks) subscription.Add(callback.Key, callback.Value);
                        try
                        {
                            await _namingService.Subscribe(serviceName, groupName, subscription.Listener).ConfigureAwait(false);
                            _namingSubscriptions[key] = subscription;
                        }
                        catch
                        {
                            subscription.Stop();
                            TryWait(_namingService.Unsubscribe(serviceName, groupName, subscription.Listener));
                            throw;
                        }
                    }
                    foreach (var callback in interest.Callbacks) subscription.Add(callback.Key, callback.Value);
                }
                foreach (var item in _namingSubscriptions.ToArray())
                {
                    if (item.Value.GroupName == groupName && item.Value.InterfaceName == interfaceName
                        && !names.Contains(item.Value.ServiceName, StringComparer.Ordinal)
                        && _namingSubscriptions.TryRemove(item.Key, out NamingSubscription removed))
                    {
                        removed.Stop();
                        TryWait(_namingService.Unsubscribe(removed.ServiceName, groupName, removed.Listener));
                    }
                }
                interest.ServiceNames = new HashSet<string>(names, StringComparer.Ordinal);
                return changedNames;
            }
            finally
            {
                _subscriptionLock.Release();
            }
        }

        /// <summary>
        /// 同地址共享的低频目录刷新；覆盖新版本/分组以及首次发现为空的接口。
        /// EN: Shared periodic catalog refresh discovers new versions/groups and previously absent interfaces.
        /// </summary>
        internal async Task RefreshServiceCatalogAsync()
        {
            if (_disposed || !await _catalogRefreshLock.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                foreach (var group in _interfaces.Values.ToArray().GroupBy(item => item.GroupName))
                {
                    try
                    {
                        List<string> names = await GetServiceNamesAsync(group.Key).ConfigureAwait(false);
                        foreach (NamingSubscription interest in group)
                        {
                            List<string> matching = names.Where(name => IsProviderServiceName(name, interest.InterfaceName)).ToList();
                            await ReconcileNamingSubscriptionsAsync(interest.InterfaceName, group.Key, matching).ConfigureAwait(false);
                            // 周期刷新同时补偿丢失或失败的节点事件，临时故障不清空订阅。
                            // EN: Also reconcile missed/failed instance events; transient failures retain subscriptions.
                            if (!_disposed) await interest.NotifyAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        MyLogger.LogWarning($"[MyNacos] catalog refresh failed for {group.Key}: {exception.Message}");
                    }
                }
            }
            finally { _catalogRefreshLock.Release(); }
        }

        private async Task<ConfigSubscription> EnsureConfigSubscriptionAsync(
            string location,
            object subscriber,
            Func<Task> changed)
        {
            ThrowIfDisposed();
            ParseMetadataLocation(location, out string group, out string dataId);
            string key = GetConfigKey(group, dataId);
            await _subscriptionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (subscriber != null && _removedSubscribers.TryGetValue(subscriber, out _))
                    throw new ObjectDisposedException("Nacos metadata subscriber");
                if (!_configSubscriptions.TryGetValue(key, out ConfigSubscription subscription))
                {
                    subscription = new ConfigSubscription
                    {
                        DataId = dataId,
                        GroupName = group
                    };
                    subscription.Listener = new ConfigListener(subscription);
                    if (subscriber != null && changed != null) subscription.Add(subscriber, changed);
                    _configSubscriptions[key] = subscription;
                    try
                    {
                        string content = await _configService.GetConfigAndSignListener(
                            dataId, group, ConfigTimeoutMilliseconds, subscription.Listener).ConfigureAwait(false);
                        subscription.CompleteInitialization(content);
                    }
                    catch
                    {
                        _configSubscriptions.TryRemove(key, out _);
                        subscription.Stop();
                        TryWait(_configService.RemoveListener(dataId, group, subscription.Listener));
                        throw;
                    }
                }
                if (subscriber != null && changed != null)
                {
                    subscription.Add(subscriber, changed);
                }
                return subscription;
            }
            finally
            {
                _subscriptionLock.Release();
            }
        }

        internal static DubboServiceEndPointInfo ConvertInstance(
            Instance instance,
            string fallbackInterfaceName)
        {
            if (!IPAddress.TryParse(instance.Ip, out _))
            {
                MyLogger.LogWarning($"[MyNacos] provider address is not an IP: {instance.Ip}");
                return null;
            }
            Dictionary<string, string> metadata = instance.Metadata
                ?? new Dictionary<string, string>();
            string protocol = metadata.TryGetValue("protocol", out string protocolValue)
                && !string.IsNullOrWhiteSpace(protocolValue)
                ? protocolValue
                : "dubbo";
            if (!string.Equals(protocol, "dubbo", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(protocol, "tri", StringComparison.OrdinalIgnoreCase))
            {
                MyLogger.LogWarning(
                    $"[MyNacos] unsupported Dubbo provider protocol '{protocol}' at " +
                    $"{instance.Ip}:{instance.Port}");
                return null;
            }
            string interfaceName = metadata.TryGetValue("interface", out string interfaceValue)
                && !string.IsNullOrWhiteSpace(interfaceValue)
                ? interfaceValue
                : fallbackInterfaceName;
            Dictionary<string, string> parameters = new Dictionary<string, string>(
                metadata,
                StringComparer.OrdinalIgnoreCase)
            {
                ["interface"] = interfaceName,
                ["disabled"] = (!instance.Enabled
                    || (metadata.TryGetValue("disabled", out string disabled)
                        && bool.TryParse(disabled, out bool isDisabled) && isDisabled)).ToString().ToLowerInvariant()
            };
            if (!parameters.ContainsKey("dynamic"))
            {
                parameters["dynamic"] = instance.Ephemeral.ToString().ToLowerInvariant();
            }
            if (instance.Weight <= 0)
            {
                parameters["weight"] = "0";
            }
            else if (!parameters.ContainsKey("weight"))
            {
                parameters["weight"] = Math.Max(0, (int)Math.Round(instance.Weight * 100D)).ToString();
            }
            string query = string.Join("&", parameters.Select(item =>
                Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value ?? string.Empty)));
            UriBuilder builder = new UriBuilder(protocol, instance.Ip, instance.Port, interfaceName)
            {
                Query = query
            };
            return DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(builder.Uri);
        }

        private static NacosConnectionOptions ParseConnectionString(string connectionString)
        {
            string remainder = connectionString.Substring(NacosPrefix.Length);
            int queryIndex = remainder.IndexOf('?');
            string serverPart = queryIndex < 0 ? remainder : remainder.Substring(0, queryIndex);
            string query = queryIndex < 0 ? string.Empty : remainder.Substring(queryIndex + 1);
            List<string> serverAddresses = serverPart.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(address => address.Trim())
                .Where(address => address.Length > 0)
                .Select(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? address.TrimEnd('/') + "/"
                    : "http://" + address.TrimEnd('/') + "/")
                .ToList();
            if (serverAddresses.Count == 0)
            {
                throw new ArgumentException(
                    "Nacos connection string does not contain a server address.",
                    nameof(connectionString));
            }
            var values = HttpUtility.ParseQueryString(query);
            return new NacosConnectionOptions
            {
                ServerAddresses = serverAddresses,
                Namespace = values["namespace"] ?? values["namespaceId"] ?? string.Empty,
                UserName = values["username"],
                Password = values["password"],
                AccessKey = values["accessKey"],
                SecretKey = values["secretKey"]
            };
        }

        private static void ApplyOptions(NacosSdkOptions options, NacosConnectionOptions parsed)
        {
            options.ServerAddresses = parsed.ServerAddresses;
            options.Namespace = parsed.Namespace;
            options.UserName = parsed.UserName;
            options.Password = parsed.Password;
            options.AccessKey = parsed.AccessKey;
            options.SecretKey = parsed.SecretKey;
            options.NamingUseRpc = true;
            options.ConfigUseRpc = true;
        }

        private static string GetConfigKey(string group, string dataId) => group + "\n" + dataId;

        private static void ParseMetadataLocation(
            string location,
            out string group,
            out string dataId)
        {
            if (string.IsNullOrWhiteSpace(location)
                || !location.StartsWith(MetadataLocationPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid Nacos metadata location.", nameof(location));
            }
            string remainder = location.Substring(MetadataLocationPrefix.Length);
            int separator = remainder.IndexOf('/');
            if (separator <= 0 || separator == remainder.Length - 1)
            {
                throw new ArgumentException("Invalid Nacos metadata location.", nameof(location));
            }
            group = Uri.UnescapeDataString(remainder.Substring(0, separator));
            dataId = Uri.UnescapeDataString(remainder.Substring(separator + 1));
        }

        private static void TryWait(Task task)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                MyLogger.LogWarning($"[MyNacos] unsubscribe failed: {exception.Message}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MyNacos));
            }
        }

        /// <summary>
        /// 取消 Naming/Config 订阅并释放 SDK 服务容器。
        /// <para>EN: Cancels Naming/Config subscriptions and disposes the SDK service container.</para>
        /// </summary>
        public void Dispose()
        {
            _subscriptionLock.Wait();
            try
            {
                if (_disposed) return;
                _disposed = true;
                _catalogTimer.Dispose();
                foreach (NamingSubscription interest in _interfaces.Values) interest.Stop();
                _interfaces.Clear();
                foreach (NamingSubscription subscription in _namingSubscriptions.Values)
                {
                    subscription.Stop();
                    TryWait(_namingService.Unsubscribe(subscription.ServiceName, subscription.GroupName, subscription.Listener));
                }
                foreach (ConfigSubscription subscription in _configSubscriptions.Values)
                {
                    subscription.Stop();
                    TryWait(_configService.RemoveListener(subscription.DataId, subscription.GroupName, subscription.Listener));
                }
                _namingSubscriptions.Clear();
                _configSubscriptions.Clear();
                TryWait(_namingService.ShutDown());
                TryWait(_configService.ShutDown());
                (_serviceProvider as IDisposable)?.Dispose();
            }
            finally { _subscriptionLock.Release(); }
        }
    }
}
