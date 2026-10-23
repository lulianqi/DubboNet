using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using System.Text.Json;

const string DefaultZooKeeper = "110.40.136.183:2181";
const string DefaultNacos = "nacos://110.40.136.183:8848";
const string ProviderHost = "110.40.136.183";
const string DataTypeService = "com.example.dubbonet.api.DataTypeService";
const string EchoService = "com.example.dubbonet.api.EchoService";
const string OrderService = "com.example.dubbonet.api.OrderService";
const string UserService = "com.example.dubbonet.api.UserService";

string registryAddress = args.Length > 0
    ? (string.Equals(args[0], "nacos", StringComparison.OrdinalIgnoreCase)
        ? DefaultNacos
        : args[0])
    : DefaultZooKeeper;
bool useNacos = registryAddress.StartsWith("nacos://", StringComparison.OrdinalIgnoreCase);
VersionScenario[] versions = useNacos
    ? new[]
    {
        new VersionScenario("3.2.20", "/dubbo-3.2.20"),
        new VersionScenario("3.3.6", "/dubbo-3.3.6")
    }
    : new[]
    {
        new VersionScenario("2.7.23", "/dubbo-2.7.23"),
        new VersionScenario("3.1.11", "/dubbo-3.1.11"),
        new VersionScenario("3.2.20", "/dubbo-3.2.20"),
        new VersionScenario("3.3.6", "/dubbo-3.3.6")
    };

TestSummary summary = new();
Console.WriteLine($"Registry: {registryAddress} ({(useNacos ? "Nacos" : "ZooKeeper")})");
Console.WriteLine("Dubbo versions: " + string.Join(", ", versions.Select(version => version.Version)));
Console.WriteLine();

foreach (VersionScenario version in versions)
{
    await RunVersionAsync(version, registryAddress, summary);
}

await RunDirectProtocolCasesAsync(summary);

Console.WriteLine();
Console.WriteLine(
    $"Integration summary: {summary.Passed} passed, {summary.Failed} failed, " +
    $"{summary.Passed + summary.Failed} total.");

if (summary.Failed > 0)
{
    Console.WriteLine("Failures:");
    foreach (string failure in summary.Failures)
    {
        Console.WriteLine($"  - {failure}");
    }
    Environment.ExitCode = 1;
}

static async Task RunVersionAsync(
    VersionScenario version,
    string registryAddress,
    TestSummary summary)
{
    Console.WriteLine($"=== Dubbo {version.Version} ({version.RootPath}) ===");
    using DubboClient client = new(registryAddress, new DubboClient.DubboClientConf
    {
        DubboRootPath = version.RootPath,
        // 测试环境显式把元数据存放在版本分组；Nacos Config 的默认分组独立为 dubbo。
        // EN: This environment stores metadata per version; the independent Nacos Config default is dubbo.
        MetadataRootPath = version.RootPath,
        DubboRequestTimeout = 10_000,
        TelnetMetadataTimeout = 5_000,
        MaintainServiceNum = 10,
        NowLoadBalanceMode = DubboClient.LoadBalanceMode.RoundRobin
    });

    await RunMetadataCaseAsync(version, client, summary);

    await RunCaseAsync(
        version.Version,
        "inferred/no-argument",
        () => client.QueryAsync($"{EchoService}.ping"),
        Success,
        summary);

    await RunCaseAsync(
        version.Version,
        "explicit/string",
        () => client.QueryAsync(
            $"{EchoService}.echo",
            new[] { "java.lang.String" },
            new object[] { $"hello-{version.Version}" }),
        result => Success(result) && JsonStringEquals(result, $"hello-{version.Version}"),
        summary);

    await RunCaseAsync(
        version.Version,
        "inferred/typed-int-result",
        async () => await client.QueryAsync<int>($"{DataTypeService}.add", 19, 23),
        result => Success(result)
            && result is DubboRequestResult<int> typed
            && typed.ResultModle == 42,
        summary);

    await RunCaseAsync(
        version.Version,
        "inferred/boolean",
        () => client.QueryAsync($"{DataTypeService}.negate", false),
        result => Success(result) && JsonBooleanEquals(result, true),
        summary);

    await RunCaseAsync(
        version.Version,
        "explicit/primitive-array",
        () => client.QueryAsync(
            $"{DataTypeService}.average",
            new[] { "double[]" },
            new object[] { new double[] { 1, 2, 3, 4 } }),
        result => Success(result) && JsonNumberEquals(result, 2.5),
        summary);

    await RunCaseAsync(
        version.Version,
        "explicit/string-and-array",
        () => client.QueryAsync(
            $"{DataTypeService}.join",
            new[] { "java.lang.String", "java.lang.String[]" },
            new object[] { "|", new[] { "A", "B", "C" } }),
        result => Success(result) && JsonStringEquals(result, "A|B|C"),
        summary);

    await RunCaseAsync(
        version.Version,
        "invocation/map-and-attachments",
        () => client.QueryAsync(new DubboInvocation(
            EchoService,
            "echoHeaders",
            new[] { "java.util.Map" },
            new object[]
            {
                new Dictionary<string, string>
                {
                    ["traceId"] = $"integration-{version.Version}",
                    ["language"] = "中文"
                }
            })
        {
            Attachments = new Dictionary<string, string>
            {
                ["integration-version"] = version.Version
            }
        }),
        Success,
        summary);

    await RunCaseAsync(
        version.Version,
        "inferred/pojo-result",
        () => client.QueryAsync($"{OrderService}.getOrder", "ORDER-1001"),
        Success,
        summary);

    await RunCaseAsync(
        version.Version,
        "explicit/pojo-argument",
        () => client.QueryAsync(
            $"{OrderService}.searchOrders",
            new[] { "com.example.dubbonet.model.OrderQuery" },
            new object[]
            {
                new Dictionary<string, object>
                {
                    ["pageNo"] = 1,
                    ["pageSize"] = 2
                }
            }),
        Success,
        summary);

    await RunCaseAsync(
        version.Version,
        "explicit/long-array",
        () => client.QueryAsync(
            $"{UserService}.batchGet",
            new[] { "long[]" },
            new object[] { new long[] { 1, 2 } }),
        Success,
        summary);

    await RunCaseAsync(
        version.Version,
        "unified/string-argument",
        () => client.QueryAsync($"{EchoService}.echo", "unified-call"),
        result => Success(result) && JsonStringEquals(result, "unified-call"),
        summary);

    await RunCaseAsync(
        version.Version,
        "provider-exception",
        () => client.QueryAsync($"{EchoService}.alwaysFail", "expected-error"),
        result => !result.QuerySuccess
            && !string.IsNullOrWhiteSpace(result.ErrorMeaasge),
        summary);

    Console.WriteLine();
}

static async Task RunMetadataCaseAsync(
    VersionScenario version,
    DubboClient client,
    TestSummary summary)
{
    const string caseName = "metadata-center/cache";
    try
    {
        IReadOnlyList<DubboMethodMetadata> first =
            await client.GetServiceMethodsMetadataAsync(DataTypeService);
        IReadOnlyList<DubboMethodMetadata> second =
            await client.GetServiceMethodsMetadataAsync(DataTypeService);
        IReadOnlyList<DubboTypeMetadata> types =
            await client.GetServiceTypesMetadataAsync(DataTypeService);

        bool passed = first.Count == 5
            && second.Count == first.Count
            && types.Count > 0
            && types.Any(type => type.Type == "int")
            && first.All(method => method.MetadataSource == DubboMetadataSource.MetadataCenter)
            && first.Any(method => method.Name == "add"
                && method.ParameterTypes.SequenceEqual(new[] { "int", "int" }));

        Record(
            version.Version,
            caseName,
            passed,
            passed
                ? $"{first.Count} methods, {types.Count} types, source={first[0].MetadataSource}"
                : $"unexpected metadata: first={first.Count}, second={second.Count}, types={types.Count}",
            summary);
    }
    catch (Exception exception)
    {
        Record(version.Version, caseName, false, exception.Message, summary);
    }
}

static async Task RunDirectProtocolCasesAsync(TestSummary summary)
{
    Console.WriteLine("=== Dubbo 3.3.6 direct protocol selection ===");
    DubboInvocation invocation = new(
        EchoService,
        "echo",
        new[] { "java.lang.String" },
        new object[] { "direct-protocol" })
    {
        Version = "1.0.0",
        Group = "dubbonet-3.3.6"
    };
    DubboActuatorSuiteConf configuration = new()
    {
        DubboRequestTimeout = 10_000
    };

    using (NativeDubboActuatorSuite native = new(ProviderHost, 20880, configuration))
    {
        await RunCaseAsync(
            "3.3.6",
            "direct/dubbo-tcp-hessian2",
            () => native.SendQuery(invocation),
            result => Success(result)
                && result.ResponseStatus == 20
                && result.SerializationId == 2
                && JsonStringEquals(result, "direct-protocol"),
            summary);
    }

    using (TripleDubboActuatorSuite triple = new(ProviderHost, 50051, configuration))
    {
        await RunCaseAsync(
            "3.3.6",
            "direct/triple-http-json",
            () => triple.SendQuery(invocation),
            result => Success(result) && JsonStringEquals(result, "direct-protocol"),
            summary);
    }
}

static async Task RunCaseAsync(
    string version,
    string caseName,
    Func<Task<DubboRequestResult>> action,
    Func<DubboRequestResult, bool> verify,
    TestSummary summary)
{
    try
    {
        DubboRequestResult result = await action();
        bool passed = verify(result);
        string detail = passed
            ? $"{result.RequestElapsed} ms, result={Shorten(result.Result)}"
            : $"success={result.QuerySuccess}, result={Shorten(result.Result)}, " +
              $"error={Shorten(result.ErrorMeaasge)}";
        Record(version, caseName, passed, detail, summary);
    }
    catch (Exception exception)
    {
        Record(version, caseName, false, exception.Message, summary);
    }
}

static bool Success(DubboRequestResult result)
{
    return result.QuerySuccess && string.IsNullOrWhiteSpace(result.ErrorMeaasge);
}

static bool JsonStringEquals(DubboRequestResult result, string expected)
{
    return TryReadJson(result.Result, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == expected;
}

static bool JsonBooleanEquals(DubboRequestResult result, bool expected)
{
    return TryReadJson(result.Result, out JsonElement value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        && value.GetBoolean() == expected;
}

static bool JsonNumberEquals(DubboRequestResult result, double expected)
{
    return TryReadJson(result.Result, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && Math.Abs(value.GetDouble() - expected) < 0.000001;
}

static bool TryReadJson(string? json, out JsonElement value)
{
    value = default;
    if (string.IsNullOrWhiteSpace(json))
    {
        return false;
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(json);
        value = document.RootElement.Clone();
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static void Record(
    string version,
    string caseName,
    bool passed,
    string detail,
    TestSummary summary)
{
    string status = passed ? "PASS" : "FAIL";
    Console.WriteLine($"[{status}] {version,-6} {caseName,-32} {detail}");
    if (passed)
    {
        summary.Passed++;
        return;
    }

    summary.Failed++;
    summary.Failures.Add($"Dubbo {version} / {caseName}: {detail}");
}

static string Shorten(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "<empty>";
    }

    string singleLine = value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
    return singleLine.Length <= 160
        ? singleLine
        : singleLine[..160] + "...";
}

internal sealed record VersionScenario(
    string Version,
    string RootPath);

internal sealed class TestSummary
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public List<string> Failures { get; } = new();
}
