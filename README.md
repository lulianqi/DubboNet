# DubboNet

DubboNet 是一个面向 .NET 的 Dubbo 客户端库。它可以从 ZooKeeper 或 Nacos 发现 Provider，依据 Provider URL 选择传输执行器，并通过经典 Dubbo TCP/Hessian2 或 Dubbo 3 Triple HTTP/JSON 调用 Java 接口，因此调用方不需要引用 Java 接口 JAR。

> 当前项目目标框架为 **.NET 6**，可被 .NET 6 及以上版本的应用引用。原生 Dubbo TCP 调用已在 **Dubbo 2.7.3** Provider 上完成真实环境验证；Triple 第一期已在 **Dubbo 3.3.6** Java Interface Provider 上完成真实环境验证。其他版本的支持边界请先阅读[版本兼容性 / Compatibility](#版本兼容性--compatibility)。

## 功能概览 / Features

- 从 ZooKeeper 的经典接口级目录或 Nacos 接口级 Naming 服务自动发现和订阅 Provider。
- 根据 Provider URL scheme 自动选择执行器：
  - `dubbo://`：`NativeDubboActuatorSuite`，使用 Dubbo2 TCP 帧和 Hessian2 泛化调用。
  - `tri://`：`TripleDubboActuatorSuite`，使用 Dubbo 3.3 的 Java Interface Unary HTTP/JSON 调用。
- 支持按权重随机、平滑加权轮询、最短响应、一致性 Hash 等负载策略。
- 自动从 Dubbo 元数据中心或 Provider Telnet `ls -l` 获取 Java 方法签名。
- 根据实参数量和 CLR 类型推断重载，并缓存方法元数据。
- 支持调用方显式指定 Java 参数类型，用于元数据缺失、`null`、包装类型和重载歧义等场景。
- 支持服务版本、分组、自定义 attachments、POJO、集合和 Map 参数。
- 暴露 Request ID、Dubbo response status、serialization id、响应 attachments 和动态原始结果。
- 同地址的 ZooKeeper/Nacos 客户端、Provider 连接和服务驱动在多个请求之间复用。
- 可选的进程级消费者注册：按实际调用登记接口，跨客户端引用计数，并使用 ZooKeeper 临时节点或 Nacos 临时实例。
- 主要公共 API 提供中英文 XML documentation，并随程序集/NuGet 包生成 `DubboNet.xml`。

## 调用架构 / Architecture

```text
应用调用
   |
   v
DubboClient
   |-- 注册中心
   |       |-- ZooKeeper --> /dubbo/{interface}/providers + Watch
   |       +-- Nacos -----> providers:{interface}:{version}:{group} + Subscribe
   |
   |-- DubboMetadataManager
   |       |-- release >= 2.7.3 或未知 --> 元数据中心 FullServiceDefinition
   |       |                                  |
   |       |                                  +-- 失败时一次性 Telnet ls -l
   |       +-- release < 2.7.3 -----------> 一次性 Telnet ls -l
   |
   +-- DubboServiceDriver --> 负载均衡 --> 按 scheme 选择执行器
           |-- dubbo:// --> NativeDubboActuatorSuite --> Dubbo TCP + Hessian2
           +-- tri://   --> TripleDubboActuatorSuite --> Triple Unary HTTP/JSON
```

`release` 只决定元数据获取顺序；执行器由 Provider URL 的 scheme 决定。Provider URL 中的 `dubbo=2.0.2` 是经典线协议版本，`version` 和 `group` 则是服务路由信息，这几个字段不要混用。

## 安装 / Installation

项目文件当前声明版本为 `3.6.2`、NuGet 包 ID 为 `DubboNet`。对应版本已经发布到你的 NuGet 源时，可以执行：

```bash
dotnet add package DubboNet --version 3.6.2
```

如果包尚未发布，或需要使用当前仓库中的最新实现，请直接引用源码项目：

```xml
<ItemGroup>
  <ProjectReference Include="../DubboNet/DubboNet.csproj" />
</ItemGroup>
```

编译需要 .NET 6 SDK 或更新版本；示例和测试默认运行在 .NET 6 Runtime 上。NuGet 类库可由 .NET 6 及以上版本的应用引用，并使用调用方的运行时。项目的主要依赖是 `DistributedLock.ZooKeeper`、`nacos-sdk-csharp` 和 `NHessian`。

## 快速开始 / Quick Start

推荐创建一个长生命周期的 `DubboClient`，在应用关闭时释放，不要为每个请求创建新实例：

```csharp
using DubboNet.Clients;
using DubboNet.DubboService.DataModle;

using var client = new DubboClient("127.0.0.1:2181");

DubboRequestResult result = await client.QueryAsync(
    "com.foo.JobService#getJob",
    1234);

if (!result.QuerySuccess)
{
    Console.WriteLine(result.ErrorMeaasge);
    return;
}

Console.WriteLine(result.Result);
```

使用 Nacos 时只需把注册中心地址改为固定的 `nacos://` 格式；该前缀也是客户端选择注册中心实现的依据：

```csharp
using var client = new DubboClient(
    "nacos://127.0.0.1:8848",
    new DubboClient.DubboClientConf
    {
        // Naming 分组；不填写时为 DEFAULT_GROUP。
        DubboRootPath = "dubbo-prod",
        // Config 元数据分组独立配置；不填写时为 dubbo。
        MetadataRootPath = "dubbo-metadata-prod"
    });
```

服务和方法之间支持 `#`、`.` 或 `/`，例如：

```text
com.foo.JobService#getJob
com.foo.JobService.getJob
com.foo.JobService/getJob
```

建议使用 `#`，它能让完整 Java 接口名和方法名的边界最清楚。

### 无参数调用 / No-argument Invocation

```csharp
DubboRequestResult result = await client.QueryAsync(
    "com.foo.HealthService#check");
```

当两种元数据来源都不可用时，无参数方法仍可使用空参数签名发起调用。

### 自动推断参数类型 / Automatic Type Inference

```csharp
DubboRequestResult result = await client.QueryAsync(
    "com.foo.CustomerService#getCustomer",
    "123",
    1234L);
```

自动推断不会只根据 JSON 武断地猜测 Java 类型。客户端先取得服务方法签名，再根据方法名、参数数量和 CLR 实参类型选择唯一重载。无法唯一确定时会抛出 `DubboMetadataException`。

尤其需要注意：

- C# `1234` 是 `int`，`1234L` 是 `long`。
- 仅从值无法可靠地区分 Java `int` 与 `java.lang.Integer`，或 `long` 与 `java.lang.Long`。
- `null` 无法提供 CLR 类型信息。
- 同参数数量且类型兼容的多个 Java 重载可能仍然有歧义。

以上情况优先使用显式类型重载。

### 显式 Java 参数类型 / Explicit Java Parameter Types

显式重载不依赖元数据获取成功，是生产环境中最确定的调用方式。客户端仍可能尽力读取元数据以辅助协议选择，但读取失败不会阻止使用用户提供的签名发送请求：

```csharp
DubboRequestResult job = await client.QueryAsync(
    "com.foo.JobService#getJob",
    new[] { "java.lang.Integer" },
    new object[] { 1234 });

DubboRequestResult customer = await client.QueryAsync(
    "com.foo.CustomerService#getCustomer",
    new[] { "java.lang.String", "java.lang.Long" },
    new object[] { "123", 1234L });
```

数组中的类型必须是 Provider `$invoke` 能加载的准确 Java 类型名，例如：

- 基础类型：`int`、`long`、`boolean`、`double`
- 包装类型：`java.lang.Integer`、`java.lang.Long`、`java.lang.Boolean`
- 集合和数组：`java.util.List`、`java.util.Map`、`java.lang.String[]`
- POJO：`com.foo.api.dto.CustomerRequest`

### 强类型响应 / Typed Response

```csharp
public sealed class JobDto
{
    public long Id { get; set; }
    public string? Name { get; set; }
}

DubboRequestResult<JobDto> result =
    await client.QueryAsync<JobDto>(
        "com.foo.JobService#getJob",
        new[] { "java.lang.Long" },
        new object[] { 1234L });

if (result.QuerySuccess)
{
    JobDto? job = result.ResultModle;
}
```

`ResultModle` 使用 `System.Text.Json` 从 `Result` 反序列化。如果本地反序列化失败，详细信息写入 `ErrorMeaasge`；它与 Provider 业务返回是否成功是不同概念。

### 高级调用对象 / DubboInvocation

`DubboInvocation` 适合设置准确签名、服务版本、分组和自定义 attachments：

```csharp
using DubboNet.DubboService.DataModle;

var invocation = new DubboInvocation(
    "com.foo.UserService",
    "save",
    new[] { "com.foo.api.dto.UserRequest" },
    new object[]
    {
        new
        {
            name = "demo",
            age = 18
        }
    })
{
    Version = "1.0.0",
    Group = "prod",
    Attachments = new Dictionary<string, string>
    {
        ["traceId"] = "example-trace-id"
    }
};

DubboRequestResult result = await client.QueryAsync(invocation);
```

对于 POJO 参数，客户端会根据 `ParameterTypes` 自动补充泛化调用需要的 `class=<Java FQCN>`。如果 `ParameterTypes` 为空，`DubboClient` 会尝试通过元数据自动推断。

### 统一调用 API / Unified Invocation API

所有通过 `DubboClient` 发送请求的公共入口统一命名为 `QueryAsync`。常用重载如下：

```csharp
// 无参数
await client.QueryAsync("com.foo.HealthService#check");

// 单参数；即使参数本身是数组，也不会被错误展开
await client.QueryAsync("com.foo.UserService#batchGet", new long[] { 1, 2 });

// 两个或更多参数
DubboRequestResult result = await client.QueryAsync(
    "com.foo.CustomerService#getCustomer",
    "123",
    1234L);

// 强类型响应
DubboRequestResult<MyResponse> typed = await client.QueryAsync<MyResponse>(
    "com.foo.JobService#getJob",
    1234L);
```

release3.0 分支不再公开 `QueryGenericAsync`、原始 JSON 文本重载，以及按请求类型泛型数量扩展的旧重载。迁移时将 `QueryGenericAsync` 改为 `QueryAsync`；显式 Java 类型调用需要把实参放入独立的 `object[]`。字符串现在始终表示一个普通字符串参数，不再被解释为逗号分隔的原始 JSON 请求。

### 直接使用执行器 / Direct Actuator Usage

不需要注册中心、已经知道 Provider IP 和端口时，可以直接创建原生执行器。此方式不会提供服务发现、负载均衡或元数据推断，因此应始终传入准确 Java 签名：

```csharp
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;

using var suite = new NativeDubboActuatorSuite(
    "127.0.0.1",
    20880,
    new DubboActuatorSuiteConf
    {
        DubboRequestTimeout = 10_000
    });

var invocation = new DubboInvocation(
    "com.foo.JobService",
    "getJob",
    new[] { "java.lang.Integer" },
    new object[] { 1234 });

DubboRequestResult result = await suite.SendQuery(invocation);
```

Dubbo 3.3+ Java Interface 服务也可以直接使用 Triple 第一期执行器。请求会发送到
`/{service}/{method}`，正文始终是 JSON 参数数组，并携带服务 version/group 路由头：

```csharp
using var triple = new TripleDubboActuatorSuite(
    "127.0.0.1",
    50051,
    new DubboActuatorSuiteConf { DubboRequestTimeout = 10_000 });

var invocation = new DubboInvocation(
    "com.foo.DataTypeService",
    "negate",
    new[] { "boolean" },
    new object[] { false })
{
    Version = "1.0.0",
    Group = "default"
};

DubboRequestResult result = await triple.SendQuery(invocation);
```

Provider 开放 Dubbo Telnet 命令时，也可以显式使用 `DubboActuator` 做诊断：

```csharp
using DubboNet.DubboService;

using var telnet = new DubboActuator("127.0.0.1", 20880);

var methods = await telnet.GetDubboServiceFuncAsync(
    "com.foo.JobService");
var status = await telnet.GetDubboStatusInfoAsync();
```

`DubboActuator.SendQuery` 和 `TelnetDubboActuatorSuite` 也保留了 Telnet `invoke` 业务调用能力，但依赖 Provider 开启 Telnet。新业务调用建议使用 `NativeDubboActuatorSuite` 或 `DubboClient`，Telnet 主要用于 `ls`、`status`、`ps`、`trace` 等诊断。

## 查询方法元数据 / Metadata Inspection

可以在调用前查看方法的所有重载：

```csharp
using DubboNet.Clients.DataModle;

IReadOnlyList<DubboMethodMetadata> overloads =
    await client.GetMethodMetadataAsync(
        "com.foo.JobService",
        "getJob");

// 也可以传完整 endpoint。
IReadOnlyList<DubboMethodMetadata> sameOverloads =
    await client.GetMethodMetadataAsync(
        "com.foo.JobService#getJob");

IReadOnlyList<DubboMethodMetadata> allMethods =
    await client.GetServiceMethodsMetadataAsync(
        "com.foo.JobService");

foreach (DubboMethodMetadata method in overloads)
{
    Console.WriteLine(
        $"[{method.MetadataSource}] {method.ReturnType} " +
        $"{method.Name}({string.Join(", ", method.ParameterTypes)})");
}

// 仅从元数据中心读取 FullServiceDefinition.types，适合 DubboTester
// 生成 POJO/集合/嵌套参数编辑器；该接口不会使用 Telnet 回退。
IReadOnlyList<DubboTypeMetadata> types =
    await client.GetServiceTypesMetadataAsync(
        "com.foo.JobService");

foreach (DubboTypeMetadata type in types)
{
    Console.WriteLine(type.Type);
    foreach (var property in type.Properties)
    {
        Console.WriteLine($"  {property.Key}: {property.Value.Type}");
    }
}
```

主要字段包括：

- `ParameterTypes`、`ReturnType`：准确 Java 签名。
- `MetadataSource`：`MetadataCenter` 或 `Telnet`。
- `Application`、`Version`、`Group`：对应 Provider 信息。
- `MetadataPath`：ZooKeeper 节点、`nacos-config://` 配置位置或 `telnet://` 来源地址。
- `TypeDefinitions`：FullServiceDefinition 中的 POJO、集合、枚举和引用定义；Telnet 来源通常没有这部分信息。
- `MetadataVersion`、`LoadedAt`：元数据版本和读取时间；Nacos Config 当前不提供等价节点版本，因此版本为 `-1`。

`GetServiceTypesMetadataAsync` 会先检查完整元数据位置是否存在并安装变化监听，确认存在后才读取数据。ZooKeeper 使用 `exists`/watcher，Nacos 使用 Config listener。位置不存在、连接不可用或文档无效时会抛出包含预期位置的 `DubboMetadataException`；因为 Telnet `ls -l` 不包含 `types`，该接口不会退回 Telnet。成功结果和稳定的“不存在”结论都会按服务缓存，元数据后续创建或修改时会刷新缓存。

## 元数据策略与缓存 / Metadata Strategy and Cache

客户端读取 Provider URL 的 `release` 字段来选择元数据来源：

| Provider `release` | 获取顺序 |
| --- | --- |
| `>= 2.7.3` | 元数据中心优先，失败后 Telnet `ls -l` |
| 已知且 `< 2.7.3` | 直接尝试 Telnet `ls -l` |
| 缺失或格式无法识别 | 元数据中心优先，失败后 Telnet `ls -l` |
| 多个 Provider 版本混合 | 只要存在 `>= 2.7.3` 或未知版本，就优先元数据中心 |

ZooKeeper 元数据按以下节点结构读取：

```text
{MetadataRootPath}/metadata/{interface}/{version?}/{group?}/provider/{application}
```

例如：

```text
/dubbo/metadata/com.foo.JobService/provider/job-service
```

Nacos Config 使用 `MetadataRootPath` 去除首尾斜杠后的值作为分组，并按 Dubbo 官方规则生成 DataId：

```text
Group:  {MetadataRootPath}
DataId: {interface}:{version}:{serviceGroup}:provider:{application}
```

例如 `MetadataRootPath=/dubbo-3.3.6` 时，实际 Config 分组为 `dubbo-3.3.6`。
Nacos Naming 默认分组为 `DEFAULT_GROUP`，Config 元数据默认分组为 `dubbo`；两者独立。
如果 Provider 将元数据写入自定义分组，需要显式填写 `MetadataRootPath`，不会自动沿用 Naming 分组。

行为说明：

- `MetadataCenterAddress` 不填写时复用注册中心地址及同一个 ZooKeeper 或 Nacos 客户端；也允许注册中心和元数据中心使用不同类型。
- FullServiceDefinition 成功加载后，会安装 ZooKeeper watch 或 Nacos Config listener。
- 元数据修改、删除、后续创建以及 Provider application/version/group 变化都会触发缓存刷新；ZooKeeper 会话恢复还会重建 watch。
- Nacos 为已使用接口每 30 秒检查一次服务目录，新注册的版本/分组无需重建客户端即可发现；同一地址的目录扫描按 Naming 分组复用。已有服务的实例变化仍使用 SDK 订阅。
- 最后一个可用 Provider 下线后会清空服务驱动；禁用节点和零权重节点不会接收请求，节点恢复后可重新加入。
- Telnet 元数据按服务缓存；一次探测可能依次尝试该服务的多个 `dubbo://` Provider，直到成功。
- Telnet 成功结果和“Telnet 不可用”的失败结论都会缓存，一个 `DubboClient` 生命周期内不会对同一服务反复连接。
- 如果之后创建了已被 watch 的元数据节点，缓存会切回优先级更高的 FullServiceDefinition。
- 元数据中心瞬时连接失败不会永久缓存；后续请求会重试元数据中心，同时复用已有的 Telnet 正缓存或负缓存。
- 元数据中心文档存在但缺少目标方法或重载时，客户端会再用同一份一次性 Telnet 结果补充。
- Telnet 只用于获取方法签名和显式诊断，原生业务调用仍使用 Dubbo TCP。
- Telnet 回退只能连接注册中心中 `dubbo://` Provider 的地址和端口，不支持另行配置独立 QoS/Telnet 端口。

如果运行期间刚刚为某个服务开启 Telnet，而该 `DubboClient` 已缓存“Telnet 不可用”，请重新创建客户端实例；这是避免每次调用重复建立失败连接的设计选择。

## 客户端配置 / Configuration

```csharp
using var client = new DubboClient(
    "zk-1:2181,zk-2:2181",
    new DubboClient.DubboClientConf
    {
        DubboRootPath = "/dubbo/",
        MetadataCenterAddress = "metadata-zk:2181", // 可选
        MetadataRootPath = "/dubbo",                // 可选
        DubboRequestTimeout = 60_000,
        TelnetMetadataTimeout = 10_000,
        MaintainServiceNum = 20,
        NowLoadBalanceMode = DubboClient.LoadBalanceMode.Random
    });
```

ZooKeeper 地址支持多个节点，以逗号分隔。认证字符串可以写成：

```text
zk-1:2181,zk-2:2181 [digest user:password]
```

Nacos 地址必须以 `nacos://` 开头，同样支持逗号分隔的多个节点。可通过查询参数配置命名空间和认证：

```text
nacos://nacos-1:8848,nacos-2:8848?namespace=prod&username=nacos&password=nacos
```

`namespace` 应填写命名空间 ID，省略、空值或 `public` 表示默认命名空间。还支持 `accessKey` 和 `secretKey` 查询参数。
查询参数值中的 `+`、`&`、`#` 等特殊字符需要 URL 编码，例如密码中的 `+` 写成 `%2B`。
主机名、地址顺序和查询参数顺序会规范化；namespace、用户名、密码及密钥的值严格区分大小写。
等价配置创建的多个 `DubboClient` 共享底层 `MyNacos` 实例（包括先后创建的客户端），一个实例内部包含 Naming 和 Config 两个 SDK 客户端，不表示只使用一条 TCP 连接。
Naming/Config 订阅按调用方隔离，最后一个订阅者释放时注销监听；没有接口订阅后停止目录刷新。

**Nacos SDK 生命周期限制：** `nacos-sdk-csharp 1.3.10` 的 `ShutDown()` 不会停止内部 RPC 后台线程。
因此共享池保留 SDK 实例供后续复用，并将进程内不同连接配置的总数限制为 **32**（包含空闲实例）。
达到上限时，新配置会明确抛出异常；已有配置仍可继续复用。`Dispose()` 释放本客户端的订阅和业务连接，
不代表 SDK 的后台连接已退出。需要使用超过 32 个不同地址/namespace/认证组合的应用，应分进程管理或重启进程；
当前不通过驱逐空闲实例再创建来掩盖 SDK 线程泄漏。

### 可选消费者注册 / Optional Consumer Registration

消费者注册默认关闭。启用后，可让注册中心和兼容的管理平台看到本应用引用的接口。
它独立于 Provider 订阅及 RPC 调用，不会自动提供动态路由、配置覆盖等治理能力。

配置作用于整个进程中的 `DubboClient`，不属于 `DubboClientConf`。可在创建 client
之前或运行期间调用 `ConfigureConsumerRegistration`，再次调用即可热切换，无需重建 client。
每次调用复制配置，单独修改原配置对象不会生效。省略或留空的 `ApplicationName`、`Host`、
`InstanceId` 继承上次配置，首次启用必须提供应用名；自动生成的实例标识不会因切换而改变。
`RetryInterval`、`ShutdownTimeout` 每次按传入值（未指定时为默认值）更新。
同一进程使用同一应用身份；独立进程自动生成不同的实例标识。

```csharp
using DubboNet.Clients;
using DubboNet.Clients.DataModle;

DubboClient.ConfigureConsumerRegistration(new ConsumerRegistrationOptions
{
    Enabled = true,
    ApplicationName = "orders-dotnet",
    // 可选：容器、多网卡或 NAT 环境建议明确指定上报的本机 IP。
    // Host = "10.0.0.12",
    RetryInterval = TimeSpan.FromSeconds(10),
    ShutdownTimeout = TimeSpan.FromSeconds(3)
});

using (var first = new DubboClient("127.0.0.1:2181"))
using (var second = new DubboClient("127.0.0.1:2181"))
{
    await first.QueryAsync("com.foo.EchoService#echo", "hello");
    await second.QueryAsync("com.foo.EchoService#echo", "world");
    // 两个 client 的同一服务引用只产生一条共享消费者记录。

    // 运行中关闭：后台注销消费者，RPC 和 Provider 订阅不受影响。
    DubboClient.ConfigureConsumerRegistration(new ConsumerRegistrationOptions { Enabled = false });

    // 运行中开启：继承原应用身份，补注册仍存活 client 已引用的服务。
    DubboClient.ConfigureConsumerRegistration(new ConsumerRegistrationOptions { Enabled = true });
}

// 可选：在宿主关闭过程中等待当前后台注册/注销尝试；网络失败仍会告警并重试。
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await DubboClient.FlushConsumerRegistrationsAsync(timeout.Token);
```

注册与释放规则：

- 仅在 `QueryAsync` 准备调用服务时异步登记。构造 client、读取服务目录或单独查询元数据不会注册。
- 默认关闭或运行中关闭时也在本地记录服务引用，不进行消费者网络注册；之后启用会补注册，已释放 client 不会复活。
- 配置调用同步更新期望状态，网络注册/注销在后台串行收敛，不等待注册中心确认。快速连续切换按最新配置收敛，中间状态不保证都上报。
- 关闭会清理已确认和可能已写入的记录；在途请求不能取消时，等待其返回后继续按最新状态清理。网络故障时注销可能延迟，后台继续重试。
- 运行中更改应用名、IP 或实例标识时，先确认旧身份注销再注册新身份；旧身份清理失败时不会冒进创建新记录。进程退出开始后不再接受重新启用。
- 按共享注册中心连接、根路径/Nacos Naming 分组、接口、服务 version/group 去重；不同应用实例不会互相覆盖。
- 不会针对每次请求重复创建记录。服务引用保留到对应 client 释放；Provider 缓存的淘汰不表示应用不再引用该服务。
- 一个 client 释放时，仅移除它的引用。该服务的最后一个引用释放时注销；同一连接上的其他服务及 client 不受影响。
- `DubboInvocation.Version/Group` 指定的是引用服务的版本与分组；未指定时消费者记录保留为空，不臆测某个 Provider 的版本。
- 注册/注销失败不会阻断 RPC。失败结果和可能已写入的记录保留在后台重试队列；按 `RetryInterval` 起步指数退避，上限为 `max(RetryInterval, 60 秒)`，不同服务独立重试。
- `FlushConsumerRegistrationsAsync` 可主动触发一次重试并等待当前尝试完成；它不是注册中心确认屏障，也不取消后台工作。等待被取消只结束调用方等待。
- 告警包含操作、连接标识、接口、根路径、版本/分组、连续失败次数及异常类型链，恢复成功会记录 Info；不输出原始异常消息和连接凭据。连续失败告警会限频。需要控制台输出时设置 `MyCommonHelper.MyLogger.LogPrintLevel = MyCommonHelper.MyLogger.LogType.Warn`（默认 Error）；查看恢复信息可设为 Info。
- 启用时需要消费者目录/实例的写权限；ZooKeeper 使用认证连接时，新建节点采用创建者 ACL，已有目录 ACL 不会被修改。
- `Dispose()` 至多等待 `ShutdownTimeout`；超时后的清理继续在共享连接上重试。正常进程退出也会尽力注销。

ZooKeeper 使用 `/{root}/{interface}/consumers/{URL编码的consumer://URL}` 下的临时节点，
复用现有会话心跳；会话过期时由服务端删除，新会话建立后恢复仍被引用的记录。
Nacos 使用 `consumers:{interface}:{version}:{group}` 临时实例，通过现有 Naming SDK
的连接检查及重连重注册维护；消费者实例使用独立的 cluster 标识，避免同机多进程覆盖。
`RetryInterval` 用于失败重试的基础间隔及会话恢复检查，不是额外的心跳。
ZooKeeper 新会话仅恢复当前启用、仍被引用的消费者；关闭且清理完成后停止消费者恢复检查。

Nacos 的空闲连接仍保留在进程级池中，因此最后一个服务引用释放后会显式注销，不能依赖
SDK 连接断开。进程崩溃或被强制终止时，ZooKeeper/Nacos 的会话或连接失效机制最终清理
临时记录；网络断开后的清理不是瞬时发生，时间由服务端超时配置决定。

如果进程仍在运行、共享连接也健康，但业务忘记 `Dispose`，心跳无法判断该 client
已经不再使用；消费者记录可能继续存在。应使用 `using` 或由 DI 容器管理生命周期，
在宿主关闭时释放 client。GC/finalizer 和进程退出回调都不能替代确定性的释放。

### 直接读取 Nacos 服务目录

`MyNacos` 是公开的 Nacos 包装器。它的公共签名只包含 .NET 和 DubboNet 自有类型，应用（例如 DubboTester）
无需直接引用或调用 `nacos-sdk-csharp`。除 DubboClient 内部的服务发现和元数据读取外，它还提供与
`MyZookeeper` 目录读取能力对应的 Naming/Config API：

```csharp
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;

using var nacos = new MyNacos("nacos://127.0.0.1:8848");

// 枚举当前 namespace 中包含 Dubbo Provider 的所有 Naming 分组。
IReadOnlyList<string> roots = await nacos.ListRegistryRootsAsync();

// 枚举指定分组中的 Java 接口名（已去除 providers:、version、service group 等 Nacos 编码信息）。
IReadOnlyList<string> services = await nacos.ListServicesAsync("dubbo-3.3.6");

// 获取接口下的全部 Provider；包含禁用/不健康节点，方便测试或管理工具完整展示目录。
IReadOnlyList<DubboServiceEndPointInfo> providers = await nacos.GetProvidersAsync(
    "com.example.dubbonet.api.EchoService",
    "dubbo-3.3.6");

// 需要保留 Nacos 原始实例元数据时，先枚举原始服务名，再读取 DubboNet 自有实例 DTO。
IReadOnlyList<string> rawServices = await nacos.ListNacosServicesAsync("dubbo-3.3.6");
IReadOnlyList<NacosServiceInstanceInfo> instances = await nacos.GetServiceInstancesAsync(
    rawServices[0],
    "dubbo-3.3.6");
```

常用公开能力：

- `ListCatalogServicesAsync`：读取当前 namespace 的原始 Naming 目录及分组、实例统计。
- `ListRegistryRootsAsync`：返回含 Dubbo Provider 的 Naming 分组。
- `ListServicesAsync`：返回指定分组中的 Dubbo Java 接口名。
- `ListNacosServicesAsync` / `GetServiceInstancesAsync`：读取原始服务与实例元数据，返回值不含 Nacos SDK 类型。
- `GetProvidersAsync`：返回可直接映射到服务树的 `DubboServiceEndPointInfo`。
- `SubscribeServiceAsync` / `SubscribeConfigAsync`：监听实例或配置变化；通过 `RemoveSubscriber` 统一取消。
- `GetConfigAsync` / `ConfigExistsAsync`：读取任意 Nacos Config 配置。
- `TestConnectionAsync` / `GetServerStatusAsync`：连接检测和 Naming 服务状态。

分组自动发现使用 Nacos Catalog API，因为 Naming SDK 只能枚举一个已知分组；已知分组内的服务、实例和订阅
仍由 Naming SDK 处理。连接字符串中的 `username`/`password` 会用于 Catalog API 登录并缓存访问令牌，
多节点地址会按顺序容错。`accessKey`/`secretKey` 继续用于 Naming/Config SDK 请求；不同 Nacos 认证插件若不支持
标准 Token 登录，应直接调用 `ListServicesAsync(已知分组)`，或改用兼容标准 Catalog API 的账号。

EN: Nacos Naming defaults to `DEFAULT_GROUP`, while Config metadata independently defaults to `dubbo`.
Equivalent addresses share clients, but namespaces and credentials are case sensitive. Catalogs refresh every 30 seconds.
The SDK cannot shut down RPC workers, so a bounded process-wide pool retains at most 32 distinct configurations;
disposing a DubboClient removes its subscriptions and business connections, not the retained SDK workers.

| 配置项 | 默认值 | 说明 |
| --- | ---: | --- |
| `DubboRootPath` | 自动 | ZooKeeper 默认 `/dubbo/`；Nacos Naming 默认 `DEFAULT_GROUP`（自动去除首尾 `/`） |
| `MetadataCenterAddress` | 注册中心地址 | ZooKeeper 或 `nacos://` Nacos 元数据中心；相同地址会复用客户端 |
| `MetadataRootPath` | 自动 | Nacos Config 默认 `dubbo`；ZooKeeper 沿用 ZooKeeper 注册根路径，混合中心时默认 `/dubbo` |
| `DubboRequestTimeout` | `60000` ms | 业务请求最大等待时间 |
| `TelnetMetadataTimeout` | `10000` ms | 单次 Telnet 元数据探测超时；非正数会恢复默认值 |
| `MaintainServiceNum` | `20` | 可复用服务驱动数量；`0` 表示不限制 |
| `NowLoadBalanceMode` | `Random` | 当前负载策略 |
| `DubboActuatorSuiteMaxConnections` | `20` | 执行器最大连接数；主要影响 Telnet 连接池，原生执行器使用单 TCP 连接多路复用 |
| `DubboActuatorSuiteAssistConnectionAliveTime` | `300` 秒 | Telnet 辅助连接空闲释放时间；`0` 表示不主动释放 |
| `DubboActuatorSuiteMasterConnectionAliveTime` | `1200` 秒 | Telnet 主连接空闲释放时间；`0` 表示不主动释放 |
| `DefaultServiceName` | `null` | 省略服务名时使用的默认接口名 |
| `DefaultFuncName` | `null` | endpoint 为空时使用的默认方法名 |

负载策略说明：

- `Random`：加权随机，使用共享随机源及 64 位权重累计。
- `RoundRobin`：并发安全的平滑加权轮询，状态按方法隔离，支持 Provider 权重更新。
- `LeastActive`：选择当前消费者在途请求数最少的节点；活跃数相同时加权随机。
- `P2CLoadBalance`：按权重抽取两个不同节点，比较本地在途请求数，同分加权随机；不依赖 Telnet 的 Provider load，Native/Triple 同样有效。这里的权重影响候选抽样和平局选择，不保证最终流量严格按权重分配。
- `ShortestResponse`：基于最近 30 秒成功请求的平均耗时与在途请求数计算估算响应时间，同分加权随机。失败调用单独计数并加入超时量级的评分惩罚，不会把快速失败视为快速成功响应。返回正常业务数据但业务 `code` 失败的结果不会被当作协议失败。
- `ConsistentHash`：使用稳定 MurmurHash2、UTF-8 请求内容和每节点 160 个虚拟节点；相同候选集和相同请求内容在不同进程中映射一致，不保证与 Java Dubbo 的 Hash 实现互通。未提供 Hash key 时使用加权随机。

活跃数是本 `DubboClient` 观察到的在途请求数，不是服务端全局负载或 TCP 连接数；连接可以多路复用。
统计按 Provider、服务、方法签名及调用指定的 version/group 隔离，不混用同节点上的快慢方法。
在统一发送入口中仅登记一次，成功、失败、超时、取消或异常均释放预占计数；节点移除或服务缓存淘汰不会提前关闭仍被在途请求持有的连接。显式释放整个 client 仍会关闭它持有的连接。
旧统计窗口到期后不把节点直接当作 `0ms` 最快；无样本节点按 5 秒探测间隔、最多一个冷启动探测在途调用进行探测。
如果全部节点都没有可用样本且均忙碌，则回退到最少活跃选择；该机制不是限流器。统计从空窗口开始，适合长生命周期 client，不能代表其他进程的负载。
方法状态会随节点移除清理，并限制空闲签名缓存；计时使用单调时钟。选择节点、轮询状态及预占计数的更新在短临界区内完成，不在其中等待网络响应。
本次优化不增加 RPC 自动重试或熔断，不改变默认 `Random` 和公开接口。

**一致性 Hash 升级注意：** 旧实现使用进程随机化的 `HashCode.Combine` 和 ASCII 请求编码；
切换为稳定 Hash、UTF-8 及新虚拟节点数后，已有 key 的节点映射会改变。依赖会话亲和或本地缓存的应用应协调升级，不能假定新旧版本混用期间路由一致。

## 返回结果 / Result Semantics

`DubboRequestResult` 的常用字段：

| 字段 | 含义 |
| --- | --- |
| `QuerySuccess` | 传输和 Dubbo 调用层是否成功，不代表业务成功 |
| `Result` | 响应值转换后的 JSON 字符串 |
| `RawResult` | 转换为 JSON 前的动态 .NET 值，仅原生 Dubbo 执行器提供 |
| `ErrorMeaasge` | 连接、超时、协议、Provider 异常或本地反序列化错误详情 |
| `RequestElapsed` | 包含网络等待的客户端请求耗时，毫秒 |
| `ServiceElapsed` | Telnet/HTTP 模式可用的服务耗时；原生 Dubbo 默认不提供 Provider 执行耗时 |
| `ServiceElapsedAvailable` | `ServiceElapsed` 是否有实际意义 |
| `RequestId` | 原生 Dubbo Request ID |
| `ResponseStatus` | 原生 Dubbo response status；`20` 表示协议成功 |
| `SerializationId` | Dubbo 帧中的序列化 ID；Hessian2 为 `2` |
| `ResponseAttachments` | 原生 Dubbo 响应 attachments |

下面这种响应中，即使业务字段 `success` 为 `false`，只要传输、协议和 Provider 方法执行正常，`QuerySuccess` 仍可能是 `true`：

```json
{
  "success": false,
  "code": "JOB_NOT_FOUND",
  "message": "job does not exist"
}
```

因此应分别处理两层状态：

```csharp
if (!result.QuerySuccess)
{
    // 连接、超时、Dubbo status、Provider 抛出的异常等调用层错误。
    Console.WriteLine(result.ErrorMeaasge);
}
else
{
    // 再根据 Result 或 ResultModle 中的 code/success 判断业务是否成功。
}
```

## 版本兼容性 / Compatibility

下表区分“已经真实验证”和“满足条件时的兼容路径”，不表示对整个 Dubbo 版本线作无条件承诺。

| 组件或版本 | 支持状态 | 说明 |
| --- | --- | --- |
| .NET 6 | **支持** | 当前库、示例和测试项目均目标化 `net6.0`；可使用 .NET 6 SDK 构建 |
| .NET 7/8/9/10 | **兼容引用** | 应用可引用包内的 `net6.0` 类库，并使用自身运行时；无需额外安装 .NET 6 Runtime |
| .NET Framework / .NET Standard | **当前包不支持** | 未提供 `netstandard2.0`、`netstandard2.1` 或 .NET Framework 目标 |
| ZooKeeper 经典接口级注册 | **支持** | 读取 `/dubbo/{interface}/providers` 并注册 watch |
| Nacos 2.5.4 接口级注册 | **真实验证** | 使用 Naming 服务发现/订阅 Provider，并从 Config 读取/监听远程元数据；已验证 Dubbo 3.2.20、3.3.6 |
| Consul、Kubernetes 等其他注册中心 | **未实现** | 当前 `DubboClient` 实现 ZooKeeper 与 Nacos |
| Dubbo 2.7.3 + `dubbo://` + Hessian2 | **真实验证** | 已验证无参数、单参数、双参数调用以及元数据/Telnet 回退 |
| Dubbo `>= 2.7.3` 的其他版本 | **条件兼容，未逐版本验证** | 需要经典 ZooKeeper 接口级目录、`dubbo://`、Hessian2 和 `$invoke` 泛化能力 |
| Dubbo `< 2.7.3` | **条件兼容，未逐版本验证** | 自动签名依赖 Provider Telnet；也可完全绕过元数据并显式传入 Java 类型 |
| Dubbo 3.x 的 classic `dubbo://` 兼容模式 | **真实验证（指定版本）** | ZooKeeper/Nacos 下已验证 3.2.20、3.3.6 的 Dubbo2/Hessian2 `$invoke` Provider；其他版本仍需实测 |
| Dubbo 3 应用级服务发现 | **未实现** | 未实现 Dubbo 3 application-level service discovery 元数据布局 |
| Dubbo 3.3.6 + `tri://` + Java Interface Unary JSON | **真实验证（第一期）** | 支持 HTTP/1.1、`application/json`、version/group/timeout 头和 attachments |
| Triple Protobuf/IDL、流式调用、gRPC framing | **未实现** | 第一期仅覆盖 Java Interface Unary HTTP/JSON，不宣称完整 Triple/gRPC 能力 |
| Dubbo Telnet/QoS | **可选** | 仅在 Provider 地址和端口开放相关命令时用于元数据兜底和诊断 |

## 协议与序列化限制 / Protocol and Serialization Limits

原生执行器当前有以下边界：

- 使用 16 字节经典 Dubbo2 TCP 帧头，普通双向 Hessian2 请求 flag 为 `0xc2`。
- 请求体中的 Dubbo protocol version 固定为 `2.0.2`，它与 Provider 框架 `release` 不是同一个版本。
- 只发送 `$invoke` 泛化调用，不发送强类型 Java 接口调用。
- **仅支持 Hessian2，serialization id 为 `2`**。Provider 返回其他序列化 ID 时会失败。
- Provider URL 中的 `serialization` 和 `prefer.serialization` 当前不会切换客户端编码器。
- 支持常用标量、字符串、日期、二进制、数组、集合、Map 和 POJO；未覆盖的 Hessian2 tag 会作为协议错误返回。
- POJO 通过带 `class` 的泛化 Map 编码；字段名和 Java DTO 属性必须匹配。
- Telnet 输出中的源码式泛型参数会先做 Java 类型擦除，例如 `java.util.Map<String, Long>` 会转换成 `java.util.Map`。
- 单个原生 TCP 连接按 Request ID 多路复用并发请求；写入串行，响应可以乱序返回。
- 单个收到的 response body 默认上限为 8 MiB，当前没有 `DubboClientConf` 配置项修改该限制。
- 支持应答 Provider 发来的双向 event/heartbeat 帧。
- 不提供 TLS 传输或 Dubbo 自定义序列化扩展。
- 请求写入失败后不会自动重放，以避免 Provider 已执行但客户端误判失败时产生重复业务操作；后续请求会重新建连。
- ZooKeeper Provider URL 的 host 当前必须是 IP 地址，域名/主机名不会自动解析。

更详细的帧头、请求体字段和响应结果 flag 请阅读 [Docs/NativeDubboProtocol.md](Docs/NativeDubboProtocol.md)。

Triple 第一期的请求格式、路由头和明确边界请阅读 [Docs/TripleProtocol.md](Docs/TripleProtocol.md)。

## 使用建议 / Recommendations

1. **复用 `DubboClient`。** 长生命周期实例才能充分复用 ZooKeeper/Nacos、Provider TCP 连接、服务驱动和元数据缓存。
2. **统一使用 `QueryAsync`。** 普通调用让客户端自动推断签名；类型有歧义时使用显式 Java 类型重载。
3. **关键接口显式传类型。** 对重载、包装类型、`null`、集合和 POJO，显式 Java 类型比运行时推断更稳定。
4. **优先建设元数据中心。** Telnet 是可选的兜底能力，不应成为生产调用的唯一元数据来源；不要把诊断端口暴露到不可信网络。
5. **确认 Provider URL 完整。** 元数据中心定位依赖 `application`，服务路由还可能依赖 `version` 和 `group`。
6. **分别判断调用成功和业务成功。** 先检查 `QuerySuccess`，再检查业务响应中的 `code`、`success` 等字段。
7. **谨慎重试非幂等请求。** 超时或连接中断不代表 Provider 一定没有执行，请由业务幂等键和重试策略兜底。
8. **根据服务数量配置缓存。** 大量调用不同接口时提高 `MaintainServiceNum`，或设为 `0`；同时关注连接和内存占用。
9. **按业务选择负载模式。** 默认 `Random` 适合通用场景；`LeastActive`、P2C 和 `ShortestResponse` 基于消费者本地统计，不需要开放 Telnet 诊断端口；`ConsistentHash` 适合需要请求亲和的场景，升级时注意映射变化。
10. **部署前用真实 Provider 做兼容测试。** 尤其是未在兼容矩阵中标记为“真实验证”的 Dubbo 版本、复杂 POJO 和自定义 Hessian 类型。

## 异常与排障 / Troubleshooting

### 找不到服务或 Provider

检查：

- `DubboRootPath` 是否与注册中心一致。
- ZooKeeper 的 `/dubbo/{完整接口名}/providers` 是否存在有效子节点，或 Nacos 对应 Naming 分组中是否存在 `providers:{interface}:...` 服务。
- Provider URL 是否以 `dubbo://` 或 `tri://` 开头。
- Provider host 是否为 IP 地址，而不是域名。
- 接口名、服务 `version` 和 `group` 是否正确。

服务发现失败通常以 `QuerySuccess=false` 的 `DubboRequestResult` 返回，详情在 `ErrorMeaasge`。

### `DubboMetadataException`：无法获取方法签名

异常会分别说明元数据中心和 Telnet 的失败原因。依次检查：

1. Provider URL 的 `release`、`application`、`version` 和 `group`。
2. `MetadataCenterAddress` 和 `MetadataRootPath` 是否指向实际元数据中心。
3. 预期 FullServiceDefinition 位置是否存在。ZooKeeper 路径为：

   ```text
   {root}/metadata/{interface}/{version?}/{group?}/provider/{application}
   ```

   Nacos 中检查 `{MetadataRootPath}` Config 分组及 DataId：

   ```text
   {interface}:{version}:{group}:provider:{application}
   ```

4. Provider 是否配置并成功上报 metadata-report。只有 consumer 节点不代表存在 provider 方法定义。
5. Provider 注册的 `dubbo://` 地址和端口是否开放 Telnet `ls -l`。

无法修复元数据来源时，按异常建议改用显式类型重载：

```csharp
await client.QueryAsync(
    "com.foo.JobService#getJob",
    new[] { "java.lang.Integer" },
    new object[] { 1234 });
```

### 方法重载无法唯一确定

查看所有候选签名：

```csharp
var methods = await client.GetMethodMetadataAsync(
    "com.foo.JobService",
    "getJob");
```

然后使用显式 Java 类型。不要仅通过把 `1234` 改成 `1234L` 来区分 primitive 和 wrapper；`long` 与 `java.lang.Long` 仍需要准确签名。

### serialization id 不受支持

原生执行器只支持 Hessian2。确认 Provider 接受 Hessian2，并且响应帧 serialization id 为 `2`。当前不会根据 Provider URL 自动切换到 Fastjson2、Kryo、FST、Protobuf 等编码器。

### `tri://` 调用失败

确认 Provider 使用 Dubbo 3.3+ 并开放 Java Interface 的 HTTP/JSON 访问。第一期执行器会向
`http://{host}:{port}/{service}/{method}` 发送 HTTP/1.1 JSON POST，正文必须是参数数组，
单参数也应为 `[value]`。有多个 version/group 时还要保证注册 URL 中这两个字段准确。

Dubbo 3.3.6 的默认 Triple HTTP/JSON 映射不能可靠区分全部 Java 重载。通过
`DubboClient` 调用时，检测到重载方法会在负载均衡前限定使用 `dubbo://` 原生协议节点；
若只有 `tri://` 节点则返回明确错误。需要通过 Triple 暴露重载时，建议在 Provider 端为
各重载声明不同的 REST 路由。

第一期不支持 Protobuf/IDL、流式调用或 gRPC framing；这些服务不能通过当前的
`TripleDubboActuatorSuite` 调用。

### 请求超时或连接中断

- 调整 `DubboRequestTimeout`。
- 检查 `ErrorMeaasge` 和 Provider 日志。
- 原生连接会在后续请求时重新建立，但失败中的业务请求不会自动重放。
- 对非幂等方法，不要在无法确认 Provider 是否执行的情况下盲目重试。

### `QuerySuccess=true` 但业务失败

这是预期行为。`QuerySuccess` 只表示调用层成功；继续解析 `Result` 或 `ResultModle` 中的业务状态。

## 构建与测试 / Build and Test

需要 .NET 6 SDK 或更新版本。运行 `net6.0` 示例和测试时还需安装 .NET 6 Runtime；仅引用类库的高版本应用不需要安装它：

```bash
dotnet restore DubboNet.sln
dotnet build DubboNet.sln --no-restore
dotnet test Tests/UnitTestForDubboNet/UnitTestForDubboNet.csproj --no-restore
```

`DubboClientIntegrationTests` 默认连接公开 ZooKeeper 测试注册中心 `110.40.136.183:2181`，也可以把其他注册中心地址作为第一个参数传入：

```bash
dotnet run \
  --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -- 110.40.136.183:2181
```

Nacos 测试环境可直接使用简写 `nacos`，或传入完整地址：

```bash
dotnet run \
  --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -- nacos

dotnet run \
  --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -- nacos://110.40.136.183:8848
```

ZooKeeper 测试矩阵覆盖 Dubbo 2.7.23、3.1.11、3.2.20 和 3.3.6；Nacos 测试矩阵覆盖 3.2.20 和 3.3.6。两者均验证元数据缓存、
自动签名推断、显式 Java 类型、强类型返回、`DubboInvocation`、Provider 异常，
以及 Dubbo TCP/Hessian2 和 Triple HTTP/JSON 的明确直连。
测试使用专门准备的 `com.example.dubbonet` 服务；如传入其他注册中心地址，需确保存在相同分组、根路径和测试服务。程序以非零退出码报告失败，可直接用于手工验收或 CI。

`QueryDemo` 使用顺序代码展示 `DubboClient.QueryAsync` 的全部重载，以及已知 Provider
IP、端口和协议时如何绕过注册中心直接调用：

```bash
dotnet run --project Tests/QueryDemo/QueryDemo.csproj
```

### 自动发布 NuGet 包 / Automated NuGet publishing

仓库通过 `.github/workflows/publish-nuget.yml` 自动发布正式版本。创建并发布 GitHub
Release 后，工作流会把 Release Tag（支持 `3.6.2` 或 `v3.6.2`）作为包版本，依次运行
单元测试、打包，并将同一个 `.nupkg` 发布到 GitHub Packages 和 nuget.org。

nuget.org 使用 Trusted Publishing/OIDC 颁发的临时密钥，不在仓库中保存长期 API Key。
首次启用时，需要在 nuget.org 为包所有者创建以下 Trusted Publishing Policy：

- Repository Owner：`lulianqi`
- Repository：`DubboNet`
- Workflow File：`publish-nuget.yml`
- Environment：`release`

GitHub Packages 使用工作流自带的 `GITHUB_TOKEN`；首次发布后可在 Package settings 中
确认包的可见性。包通过项目中的 `RepositoryUrl` 自动关联到本仓库。

## License

本项目使用 [MIT License](LICENSE)。
