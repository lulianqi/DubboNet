using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService.DataModle;
using System.Diagnostics;
using System.Web;

string zooKeeper = args.Length > 0 ? args[0] : "10.90.0.234:2181";
using DubboClient client = new DubboClient(zooKeeper, new DubboClient.DubboClientConf
{
    DubboRequestTimeout = 10_000,
    TelnetMetadataTimeout = 10_000,
    MaintainServiceNum = 10
});

string[] endpoints =
{
    "com.byai.tiandun.api.strategy.TiandunCheckRemoteService.checkTest",
    "com.byai.saas.callservice.api.CallJobRemoteService.getCallJob",
    "com.byai.transform.api.JdCustomerFacade.jdCustomerGet"
};

foreach (string endpoint in endpoints)
{
    string service = endpoint[..endpoint.LastIndexOf('.')];
    await PrintProviders(service);
    await PrintMethodMetadata(endpoint, "first lookup");
    await PrintMethodMetadata(endpoint, "cached lookup");
}

await Invoke(
    "com.byai.tiandun.api.strategy.TiandunCheckRemoteService.checkTest");
await Invoke(
    "com.byai.saas.callservice.api.CallJobRemoteService.getCallJob",
    1234);
await Invoke(
    "com.byai.transform.api.JdCustomerFacade.jdCustomerGet",
    "123",
    1234L);

async Task PrintMethodMetadata(string endpoint, string label)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    Console.WriteLine($"metadata {label}: {endpoint}");
    try
    {
        IReadOnlyList<DubboMethodMetadata> methods =
            await client.GetMethodMetadataAsync(endpoint);
        stopwatch.Stop();
        Console.WriteLine($"elapsed={stopwatch.ElapsedMilliseconds}ms, overloads={methods.Count}");
        foreach (DubboMethodMetadata method in methods)
        {
            Console.WriteLine(
                $"  [{method.MetadataSource}] {method.ReturnType} {method.Name}(" +
                $"{string.Join(", ", method.ParameterTypes)}) " +
                $"application={method.Application}, path={method.MetadataPath}");
        }
    }
    catch (DubboMetadataException exception)
    {
        stopwatch.Stop();
        Console.WriteLine($"elapsed={stopwatch.ElapsedMilliseconds}ms, unavailable: {exception.Message}");
    }
    Console.WriteLine();
}

async Task Invoke(string endpoint, params object[] arguments)
{
    try
    {
        DubboRequestResult result = await client.QueryGenericAsync(endpoint, arguments);
        string body = result.QuerySuccess ? result.Result : result.ErrorMeaasge;
        if (body?.Length > 500)
        {
            body = body[..500] + "...";
        }
        Console.WriteLine(
            $"invoke {endpoint}: success={result.QuerySuccess}, status={result.ResponseStatus}, " +
            $"serialization={result.SerializationId}, elapsed={result.RequestElapsed}ms");
        Console.WriteLine(body);
    }
    catch (DubboMetadataException exception)
    {
        Console.WriteLine($"invoke {endpoint}: metadata unavailable: {exception.Message}");
    }
    Console.WriteLine();
}

async Task PrintProviders(string service)
{
    using MyZookeeper registry = new MyZookeeper(zooKeeper);
    await registry.ConnectZooKeeperAsync();
    var children = await registry.GetChildrenAsync($"/dubbo/{service}/providers");
    Console.WriteLine($"providers: {service}");
    foreach (string child in children?.Children ?? new List<string>())
    {
        string decoded = HttpUtility.UrlDecode(child);
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out Uri? uri)
            || uri == null
            || !string.Equals(uri.Scheme, "dubbo", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        DubboServiceEndPointInfo provider =
            DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(uri);
        Console.WriteLine(
            $"  endpoint={provider.EndPoint}, release={provider.Release ?? "unknown"}, " +
            $"application={provider.Application ?? "unknown"}, version={provider.Version ?? "<empty>"}, " +
            $"group={provider.Group ?? "<empty>"}");
    }
    Console.WriteLine();
}
