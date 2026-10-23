using System.Net;
using System.Reflection;
using System.Text;
using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using Nacos.V2;
using Nacos.V2.Naming;
using Nacos.V2.Naming.Dtos;
using Nacos.V2.Remote;

namespace UnitTestForDubboNet;

/// <summary>离线覆盖 Nacos 动态发现、缓存及共享订阅。EN: Offline tests for dynamic discovery, metadata, and shared subscriptions.</summary>
public class MyNacosTest
{
    private const string Service = "com.example.Service";
    private const string Group = "DEFAULT_GROUP";
    private const string Address = "nacos://localhost:8848";

    [Fact]
    public void ConnectionIdentity_NormalizesServersAndKeysButPreservesSensitiveValues()
    {
        Assert.Equal(MyNacos.NormalizeConnectionString("NACOS://HOST:8848,backup:8848/?password=A%2Bb&namespaceId=Prod&username=alice"),
            MyNacos.NormalizeConnectionString("nacos://backup,host?username=alice&namespace=Prod&password=A%2Bb"));
        Assert.Equal(MyNacos.NormalizeConnectionString(Address), MyNacos.NormalizeConnectionString(Address + "?namespace=public"));
        Assert.NotEqual(MyNacos.NormalizeConnectionString(Address + "?namespace=Prod"), MyNacos.NormalizeConnectionString(Address + "?namespace=prod"));
        Assert.NotEqual(MyNacos.NormalizeConnectionString(Address + "?password=ABC"), MyNacos.NormalizeConnectionString(Address + "?password=abc"));
        Assert.Contains(":80", MyNacos.NormalizeConnectionString("nacos://http://host:80"));
        Assert.Throws<ArgumentException>(() => MyNacos.NormalizeConnectionString("nacos://"));
        Assert.Throws<ArgumentException>(() => MyNacos.NormalizeConnectionString(Address + "?namespace=a&namespaceId=b"));
    }

    [Theory]
    [InlineData(false, false, null, null, "/dubbo/", "/dubbo")]
    [InlineData(true, true, null, null, "DEFAULT_GROUP", "dubbo")]
    [InlineData(true, true, "/naming/", null, "naming", "dubbo")]
    [InlineData(true, true, "/naming/", "/metadata/", "naming", "metadata")]
    [InlineData(false, false, "/custom/", null, "/custom/", "/custom")]
    [InlineData(true, false, "naming", null, "naming", "/dubbo")]
    [InlineData(false, true, "/custom/", null, "/custom/", "dubbo")]
    public void RegistryDefaults_AreIndependent(bool namingNacos, bool metadataNacos,
        string? root, string? metadataRoot, string expectedRoot, string expectedMetadataRoot)
    {
        Assert.Equal((expectedRoot, expectedMetadataRoot), DubboClient.ResolveRegistryPaths(namingNacos, metadataNacos,
            new DubboClient.DubboClientConf { DubboRootPath = root, MetadataRootPath = metadataRoot }));
    }

    [Theory]
    [InlineData("new")]
    [InlineData(null)]
    public async Task InitialMetadataRead_DoesNotOverwriteAnEarlierNotification(string? pushed)
    {
        var server = new FakeNacos();
        server.ConfigContent = "old";
        server.DuringInitialConfig = listener => listener.ReceiveConfigInfo(pushed!);
        using MyNacos nacos = server.CreateClient();
        int notifications = 0;
        string location = Location();
        bool exists = await nacos.MetadataExistsAsync(location, new object(), () => { notifications++; return Task.CompletedTask; });
        Assert.Equal(pushed != null, exists);
        byte[]? data = await nacos.GetMetadataAsync(location);
        Assert.Equal(pushed, data == null ? null : Encoding.UTF8.GetString(data));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task MetadataLifecycle_CachesReadsAndIsolatesOwners()
    {
        var server = new FakeNacos();
        using MyNacos nacos = server.CreateClient();
        var ownerA = new object();
        var ownerB = new object();
        int callsA = 0, callsB = 0;
        string location = Location();
        Assert.False(await nacos.MetadataExistsAsync(location, ownerA, () => { callsA++; return Task.CompletedTask; }));
        Assert.False(await nacos.MetadataExistsAsync(location, ownerB, () => { callsB++; return Task.CompletedTask; }));
        Assert.Equal(1, server.ConfigReads);
        server.PushConfig("v1");
        Assert.Equal("v1", Encoding.UTF8.GetString(await nacos.GetMetadataAsync(location)));
        Assert.True(await nacos.MetadataExistsAsync(location, ownerB, () => { callsB++; return Task.CompletedTask; }));
        Assert.Equal(1, server.ConfigReads);
        nacos.RemoveSubscriber(ownerA);
        server.PushConfig("v2");
        Assert.Equal(1, callsA);
        Assert.Equal(2, callsB);
        server.PushConfig(null);
        Assert.Null(await nacos.GetMetadataAsync(location));
        Assert.Equal(0, server.ConfigRemoves);
        nacos.RemoveSubscriber(ownerB);
        Assert.Equal(1, server.ConfigRemoves);
        server.PushConfig("late");
        Assert.Equal(3, callsB);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => nacos.MetadataExistsAsync(location, ownerB, () => Task.CompletedTask));
    }

    [Fact]
    public async Task FailedSubscription_CanBeRetriedWithoutKeepingBrokenCache()
    {
        var server = new FakeNacos { FailConfigRead = true };
        using MyNacos nacos = server.CreateClient();
        var owner = new object();
        await Assert.ThrowsAsync<IOException>(() => nacos.MetadataExistsAsync(Location(), owner, () => Task.CompletedTask));
        server.FailConfigRead = false;
        server.ConfigContent = "recovered";
        Assert.True(await nacos.MetadataExistsAsync(Location(), owner, () => Task.CompletedTask));
        Assert.Equal(2, server.ConfigReads);
    }

    [Fact]
    public async Task CatalogRefresh_DiscoversNewVersionsAndKeepsSharedSubscriptions()
    {
        var server = new FakeNacos();
        server.Add("1", 20880);
        using MyNacos nacos = server.CreateClient();
        var ownerA = new object();
        var ownerB = new object();
        IReadOnlyList<DubboServiceEndPointInfo> snapshot = Array.Empty<DubboServiceEndPointInfo>();
        int callsA = 0, callsB = 0;
        Func<string, Task>? callbackB = null;
        callbackB = async _ =>
        {
            callsB++;
            snapshot = await nacos.GetProviderEndPointsAsync(Service, Group, ownerB, callbackB!);
        };
        await nacos.GetProviderEndPointsAsync(Service, Group, ownerA, _ => { callsA++; return Task.CompletedTask; });
        await nacos.GetProviderEndPointsAsync(Service, Group, ownerB, callbackB);
        Assert.Equal(1, server.NamingSubscribes);
        server.Add("2", 20881); // v1 无变化事件。EN: No event is raised for v1.
        await nacos.RefreshServiceCatalogAsync();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, server.NamingSubscribes);
        nacos.RemoveSubscriber(ownerA);
        int callsBeforeRemoval = callsA;
        server.Instances.Clear();
        await server.PushNaming();
        Assert.Empty(snapshot);
        Assert.Equal(callsBeforeRemoval, callsA);
        Assert.True(callsB > 0);
        nacos.RemoveSubscriber(ownerB);
        int pages = server.CatalogReads;
        await nacos.RefreshServiceCatalogAsync();
        Assert.Equal(pages, server.CatalogReads);
        Assert.Empty(server.NamingListeners);
    }

    [Fact]
    public async Task EmptyInitialCatalog_RecoversAndTransientErrorsPreserveSubscriptions()
    {
        var server = new FakeNacos();
        using MyNacos nacos = server.CreateClient();
        var owner = new object();
        int calls = 0;
        Assert.Empty(await nacos.GetProviderEndPointsAsync(Service, Group, owner, _ => { calls++; return Task.CompletedTask; }));
        server.Add("1", 20880);
        await nacos.RefreshServiceCatalogAsync();
        Assert.Equal(1, server.NamingSubscribes);
        Assert.Equal(1, calls);
        server.FailCatalog = true;
        await nacos.RefreshServiceCatalogAsync();
        Assert.Single(server.NamingListeners);
        server.FailCatalog = false;
        server.Services.Clear();
        await nacos.RefreshServiceCatalogAsync();
        Assert.Empty(server.NamingListeners);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ProviderConversion_PreservesDisabledAndZeroWeight()
    {
        Instance instance = FakeNacos.Provider(20880);
        instance.Metadata["disabled"] = "true";
        instance.Weight = 0;
        DubboServiceEndPointInfo endpoint = MyNacos.ConvertInstance(instance, Service);
        Assert.True(endpoint.Disabled);
        Assert.Equal(0, endpoint.Weight);
        instance.Metadata["weight"] = "100";
        Assert.Equal(0, MyNacos.ConvertInstance(instance, Service).Weight);
        instance.Metadata["weight"] = "0";
        instance.Weight = 1;
        Assert.Equal(0, MyNacos.ConvertInstance(instance, Service).Weight);
    }

    [Fact]
    public void SharedPool_ReusesEquivalentAddressesAndBoundsDistinctConfigurations()
    {
        var server = new FakeNacos();
        int creations = 0;
        using var pool = new MultiMyNacosStorage(2, address => { creations++; return server.CreateClient(address); });
        MyNacos first = pool.GetMyNacos("nacos://HOST?namespace=Prod&password=ABC");
        MyNacos second = pool.GetMyNacos("nacos://host:8848?password=ABC&namespaceId=Prod");
        Assert.Same(first, second);
        pool.RemoveMyNacos(first);
        pool.RemoveMyNacos(second);
        Assert.Same(first, pool.GetMyNacos("nacos://host:8848?namespace=Prod&password=ABC"));
        Assert.NotSame(first, pool.GetMyNacos("nacos://host:8848?namespace=prod&password=ABC"));
        Assert.Throws<InvalidOperationException>(() => pool.GetMyNacos("nacos://other:8848"));
        Assert.Equal(2, creations);
    }

    [Fact]
    public async Task MetadataManager_RefreshesCachedMethodsAndTypesOnConfigChanges()
    {
        var server = new FakeNacos { ConfigContent = Definition("before", "int") };
        using MyNacos nacos = server.CreateClient();
        var endpoint = MyNacos.ConvertInstance(FakeNacos.Provider(20880), Service);
        using var manager = new DubboMetadataManager(nacos, "dubbo",
            _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(new[] { endpoint }));
        Assert.Equal("before", Assert.Single(await manager.GetServiceMethodsAsync(Service)).Name);
        server.PushConfig(Definition("after", "long"));
        Assert.Equal("after", Assert.Single(await manager.GetServiceMethodsAsync(Service)).Name);
        Assert.Contains(await manager.GetServiceTypesAsync(Service), type => type.Type == "long");
        Assert.Equal(1, server.ConfigReads);
    }

    private static string Definition(string method, string type) =>
        $$"""{"canonicalName":"{{Service}}","methods":[{"name":"{{method}}","parameterTypes":["{{type}}"],"returnType":"{{type}}"}],"types":[{"type":"{{type}}"}]}""";
    private static string Location() => MyNacos.BuildMetadataLocation("dubbo", Service, "1", "g", "app");

    private sealed class FakeNacos
    {
        public List<string> Services { get; } = new();
        public Dictionary<string, List<Instance>> Instances { get; } = new();
        public Dictionary<string, IEventListener> NamingListeners { get; } = new();
        public IListener? ConfigListener;
        public string? ConfigContent;
        public Action<IListener>? DuringInitialConfig;
        public bool FailCatalog, FailConfigRead;
        public int CatalogReads, NamingSubscribes, ConfigReads, ConfigRemoves;

        public MyNacos CreateClient(string address = Address)
        {
            var naming = DispatchProxy.Create<INacosNamingService, SdkProxy>();
            ((SdkProxy)(object)naming).Handler = (method, args) => method.Name switch
            {
                "GetServicesOfServer" => Catalog(),
                "GetAllInstances" => Task.FromResult(Instances.GetValueOrDefault((string)args[0]!) ?? new List<Instance>()),
                "Subscribe" => Subscribe((string)args[0]!, (IEventListener)args[2]!),
                "Unsubscribe" => Unsubscribe((string)args[0]!),
                "ShutDown" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name)
            };
            var config = DispatchProxy.Create<INacosConfigService, SdkProxy>();
            ((SdkProxy)(object)config).Handler = (method, args) => method.Name switch
            {
                "GetConfigAndSignListener" => ReadConfig((IListener)args[3]!),
                "GetConfig" => Task.FromResult(ConfigContent),
                "RemoveListener" => RemoveConfig(),
                "ShutDown" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name)
            };
            return new MyNacos(address, naming, config, Timeout.InfiniteTimeSpan);
        }

        public void Add(string version, int port)
        {
            string name = $"providers:{Service}:{version}:g";
            Services.Add(name);
            Instances[name] = new() { Provider(port) };
        }

        public static Instance Provider(int port) => new()
        {
            Ip = "127.0.0.1", Port = port, Healthy = true, Enabled = true, Weight = 1,
            Metadata = new() { ["protocol"] = "dubbo", ["interface"] = Service,
                ["release"] = "3.3.6", ["version"] = "1", ["group"] = "g", ["application"] = "app" }
        };

        public void PushConfig(string? value) => ConfigListener?.ReceiveConfigInfo(value!);
        public async Task PushNaming()
        {
            foreach (IEventListener listener in NamingListeners.Values.ToArray()) await listener.OnEvent(null!);
        }
        private Task<ListView<string>> Catalog()
        {
            CatalogReads++;
            return FailCatalog ? Task.FromException<ListView<string>>(new IOException("offline"))
                : Task.FromResult(new ListView<string>(Services.Count, Services.ToList()));
        }
        private Task Subscribe(string name, IEventListener listener)
        {
            NamingSubscribes++;
            NamingListeners[name] = listener;
            return Task.CompletedTask;
        }
        private Task Unsubscribe(string name) { NamingListeners.Remove(name); return Task.CompletedTask; }
        private Task<string?> ReadConfig(IListener listener)
        {
            ConfigReads++;
            ConfigListener = listener;
            if (FailConfigRead) return Task.FromException<string?>(new IOException("offline"));
            DuringInitialConfig?.Invoke(listener);
            return Task.FromResult(ConfigContent);
        }
        private Task RemoveConfig() { ConfigRemoves++; return Task.CompletedTask; }
    }

    public class SdkProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args!);
    }
}
