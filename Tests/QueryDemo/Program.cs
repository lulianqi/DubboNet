using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;

const string DefaultZooKeeper = "110.40.136.183:2181";
const string DubboRootPath = "/dubbo-3.3.6";
const string ProviderHost = "110.40.136.183";
const string EchoService = "com.example.dubbonet.api.EchoService";
const string DataTypeService = "com.example.dubbonet.api.DataTypeService";

string zooKeeper = args.Length > 0 ? args[0] : DefaultZooKeeper;

Console.WriteLine($"ZooKeeper: {zooKeeper}");
Console.WriteLine($"Dubbo root: {DubboRootPath}");
Console.WriteLine();

// DubboClient 应作为长生命周期对象复用。它会复用 ZooKeeper、Provider 连接、
// 服务发现结果和方法元数据缓存。应用关闭时再 Dispose。
// EN: Reuse DubboClient as a long-lived object so registry/provider connections,
// discovery results, and metadata caches can be shared. Dispose it at shutdown.
using DubboClient client = new(zooKeeper, new DubboClient.DubboClientConf
{
    DubboRootPath = DubboRootPath,
    DubboRequestTimeout = 10_000
});

// 1/10 QueryAsync(string funcEndPoint)
// funcEndPoint：完整 Java 接口名 + 方法名。本重载用于无参数方法；客户端会发送空参数列表。
// EN: funcEndPoint is the Java interface plus method name. Use this overload for a no-argument method.
DubboRequestResult noArgumentResult = await client.QueryAsync(
    $"{EchoService}#ping");
Console.WriteLine($"1. 无参数调用: {noArgumentResult.Result}");

// 2/10 QueryAsync(string funcEndPoint, object argument)
// argument：唯一的业务参数。本重载专门保证数组、集合等对象被当成一个参数，而不会被 params 展开。
// EN: argument is the single business argument. Arrays and collections remain one argument.
DubboRequestResult oneArgumentResult = await client.QueryAsync(
    $"{DataTypeService}#negate",
    false);
Console.WriteLine($"2. 单参数调用: {oneArgumentResult.Result}");

// 3/10 QueryAsync(string funcEndPoint, object firstArgument,
//                 object secondArgument, params object[] additionalArguments)
// firstArgument/secondArgument：前两个业务参数；additionalArguments：第三个及后续参数。
// 该重载用于两个或更多参数，调用方可以像普通 C# 方法一样依次传值。
// EN: Use firstArgument and secondArgument for the first two values; params carries any remaining values.
DubboRequestResult multipleArgumentsResult = await client.QueryAsync(
    $"{DataTypeService}#add",
    19,
    23);
Console.WriteLine($"3. 多参数调用: {multipleArgumentsResult.Result}");

// 4/10 QueryAsync<TResponse>(string funcEndPoint)
// TResponse：期望的 CLR 响应类型；funcEndPoint：无参数方法入口。
// 当希望直接从 ResultModle 获取强类型结果时使用，原始 JSON 仍保留在 Result 中。
// EN: TResponse is the expected CLR response type. Use this overload for a typed no-argument call.
DubboRequestResult<string> typedNoArgumentResult = await client.QueryAsync<string>(
    $"{EchoService}#ping");
Console.WriteLine($"4. 强类型无参数调用: {typedNoArgumentResult.ResultModle}");

// 5/10 QueryAsync<TResponse>(string funcEndPoint, object argument)
// TResponse：响应 CLR 类型；argument：唯一业务参数。
// 用于需要强类型响应的单参数方法，同样保证数组参数不会被拆开。
// EN: Use this overload for a typed response from a one-argument method; array arguments stay intact.
DubboRequestResult<string> typedOneArgumentResult = await client.QueryAsync<string>(
    $"{EchoService}#echo",
    "hello-query-demo");
Console.WriteLine($"5. 强类型单参数调用: {typedOneArgumentResult.ResultModle}");

// 6/10 QueryAsync<TResponse>(string funcEndPoint, object firstArgument,
//                            object secondArgument, params object[] additionalArguments)
// TResponse：响应 CLR 类型；其余参数是按 Java 方法声明顺序排列的两个或更多业务参数。
// EN: Use this overload when a method has two or more arguments and its response should be deserialized.
DubboRequestResult<int> typedMultipleArgumentsResult = await client.QueryAsync<int>(
    $"{DataTypeService}#add",
    19,
    23);
Console.WriteLine($"6. 强类型多参数调用: {typedMultipleArgumentsResult.ResultModle}");

// 7/10 QueryAsync(string funcEndPoint,
//                 IReadOnlyList<string> javaParameterTypes,
//                 IReadOnlyList<object> arguments)
// javaParameterTypes：准确的 Java 参数类型；arguments：与类型列表一一对应的实参。
// 元数据缺失、存在重载、参数为 null、包装类型/数组/POJO 难以推断时使用显式签名。
// EN: Supply exact Java types and matching arguments when metadata is unavailable or inference is ambiguous.
DubboRequestResult explicitTypesResult = await client.QueryAsync(
    $"{DataTypeService}#average",
    new[] { "double[]" },
    new object[] { new double[] { 1, 2, 3, 4 } });
Console.WriteLine($"7. 显式 Java 类型调用: {explicitTypesResult.Result}");

// 8/10 QueryAsync<TResponse>(string funcEndPoint,
//                            IReadOnlyList<string> javaParameterTypes,
//                            IReadOnlyList<object> arguments)
// 与上一个重载相同，但 TResponse 会把响应反序列化为指定 CLR 类型。
// EN: This is the typed-response counterpart of the explicit Java-signature overload.
DubboRequestResult<double> typedExplicitTypesResult = await client.QueryAsync<double>(
    $"{DataTypeService}#average",
    new[] { "double[]" },
    new object[] { new double[] { 1, 2, 3, 4 } });
Console.WriteLine($"8. 强类型 + 显式 Java 类型调用: {typedExplicitTypesResult.ResultModle}");

// 9/10 QueryAsync(DubboInvocation invocation)
// invocation：完整调用描述，可以同时指定接口、方法、Java 类型、实参、version、group 和 attachments。
// 当普通重载无法表达路由信息或自定义附件时使用。
// EN: DubboInvocation carries the full signature, routing fields, and attachments for advanced calls.
DubboInvocation invocation = new(
    EchoService,
    "echoHeaders",
    new[] { "java.util.Map" },
    new object[]
    {
        new Dictionary<string, string>
        {
            ["traceId"] = "query-demo",
            ["language"] = "中文"
        }
    })
{
    Version = "1.0.0",
    Group = "dubbonet-3.3.6",
    Attachments = new Dictionary<string, string>
    {
        ["demo-source"] = "QueryDemo"
    }
};
DubboRequestResult invocationResult = await client.QueryAsync(invocation);
Console.WriteLine($"9. DubboInvocation 调用: {invocationResult.Result}");

// 10/10 QueryAsync<TResponse>(DubboInvocation invocation)
// invocation 的含义不变；TResponse 额外指定 CLR 响应类型。
// 当高级调用同时需要 version/group/attachments 和强类型响应时使用。
// EN: Use this overload when an advanced DubboInvocation also needs a typed CLR response.
DubboInvocation typedInvocation = new(
    EchoService,
    "echo",
    new[] { "java.lang.String" },
    new object[] { "typed-invocation" })
{
    Version = "1.0.0",
    Group = "dubbonet-3.3.6"
};
DubboRequestResult<string> typedInvocationResult =
    await client.QueryAsync<string>(typedInvocation);
Console.WriteLine($"10. 强类型 DubboInvocation 调用: {typedInvocationResult.ResultModle}");

Console.WriteLine();
Console.WriteLine("=== 不使用注册中心，直接调用已知 Provider ===");

// 直连不会创建 DubboClient，也不会提供服务发现、负载均衡或元数据推断。
// 因此必须明确知道 Provider 的 IP、端口、协议，以及完整 Java 方法签名。
// EN: Direct invocation skips DubboClient, discovery, load balancing, and metadata inference.
// The provider address, protocol, and exact Java signature must be known in advance.
DubboInvocation directInvocation = new(
    EchoService,
    "echo",
    new[] { "java.lang.String" },
    new object[] { "direct-query" })
{
    Version = "1.0.0",
    Group = "dubbonet-3.3.6"
};

// 已知节点使用经典 dubbo:// 协议时，直接创建 NativeDubboActuatorSuite。
// 参数依次是 Provider IP、Dubbo TCP 端口和可选执行器配置。
// EN: Create NativeDubboActuatorSuite for a known dubbo:// provider IP and TCP port.
using NativeDubboActuatorSuite nativeSuite = new(
    ProviderHost,
    20880,
    new DubboActuatorSuiteConf { DubboRequestTimeout = 10_000 });
DubboRequestResult directNativeResult = await nativeSuite.SendQuery(directInvocation);
Console.WriteLine($"直连 Dubbo TCP: {directNativeResult.Result}");

// 已知节点使用 tri:// Java Interface HTTP/JSON 协议时，直接创建 TripleDubboActuatorSuite。
// 参数依次是 Provider IP、Triple 端口和可选执行器配置。
// EN: Create TripleDubboActuatorSuite for a known tri:// Java-interface provider IP and port.
using TripleDubboActuatorSuite tripleSuite = new(
    ProviderHost,
    50051,
    new DubboActuatorSuiteConf { DubboRequestTimeout = 10_000 });
DubboRequestResult directTripleResult = await tripleSuite.SendQuery(directInvocation);
Console.WriteLine($"直连 Triple HTTP/JSON: {directTripleResult.Result}");
