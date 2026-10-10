# DubboNet

DubboNet 是一个面向 .NET 的 Dubbo 客户端库。它可以从 ZooKeeper 发现 Provider，依据 Provider URL 选择传输执行器，并通过经典 Dubbo TCP/Hessian2 或 Dubbo 3 Triple HTTP/JSON 调用 Java 接口，因此调用方不需要引用 Java 接口 JAR。

> 当前项目目标框架为 **.NET 10**。原生 Dubbo TCP 调用已在 **Dubbo 2.7.3** Provider 上完成真实环境验证；Triple 第一期已在 **Dubbo 3.3.6** Java Interface Provider 上完成真实环境验证。其他版本的支持边界请先阅读[版本兼容性 / Compatibility](#版本兼容性--compatibility)。

## 功能概览 / Features

- 从 ZooKeeper 的经典接口级目录 `/dubbo/{interface}/providers` 自动发现和监听 Provider。
- 根据 Provider URL scheme 自动选择执行器：
  - `dubbo://`：`NativeDubboActuatorSuite`，使用 Dubbo2 TCP 帧和 Hessian2 泛化调用。
  - `tri://`：`TripleDubboActuatorSuite`，使用 Dubbo 3.3 的 Java Interface Unary HTTP/JSON 调用。
- 支持按权重随机、平滑加权轮询、最短响应、一致性 Hash 等负载策略。
- 自动从 Dubbo 元数据中心或 Provider Telnet `ls -l` 获取 Java 方法签名。
- 根据实参数量和 CLR 类型推断重载，并缓存方法元数据。
- 支持调用方显式指定 Java 参数类型，用于元数据缺失、`null`、包装类型和重载歧义等场景。
- 支持服务版本、分组、自定义 attachments、POJO、集合和 Map 参数。
- 暴露 Request ID、Dubbo response status、serialization id、响应 attachments 和动态原始结果。
- ZooKeeper、Provider 连接和服务驱动在多个请求之间复用。
- 主要公共 API 提供中英文 XML documentation，并随程序集/NuGet 包生成 `DubboNet.xml`。

## 调用架构 / Architecture

```text
应用调用
   |
   v
DubboClient
   |-- ZooKeeper 注册中心 --> /dubbo/{interface}/providers
   |                            |
   |                            +-- Provider URL / 节点变更 Watch
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

项目文件当前声明版本为 `2.0.0`、NuGet 包 ID 为 `DubboNet`。对应版本已经发布到你的 NuGet 源时，可以执行：

```bash
dotnet add package DubboNet --version 2.0.0
```

如果包尚未发布，或需要使用当前仓库中的最新实现，请直接引用源码项目：

```xml
<ItemGroup>
  <ProjectReference Include="../DubboNet/DubboNet.csproj" />
</ItemGroup>
```

运行和编译需要 .NET 10 SDK。项目的主要依赖是 `DistributedLock.ZooKeeper` 和 `NHessian`。

## 快速开始 / Quick Start

推荐创建一个长生命周期的 `DubboClient`，在应用关闭时释放，不要为每个请求创建新实例：

```csharp
using DubboNet.Clients;
using DubboNet.DubboService.DataModle;

using var client = new DubboClient("127.0.0.1:2181");

DubboRequestResult result = await client.QueryGenericAsync(
    "com.foo.JobService#getJob",
    1234);

if (!result.QuerySuccess)
{
    Console.WriteLine(result.ErrorMeaasge);
    return;
}

Console.WriteLine(result.Result);
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
DubboRequestResult result = await client.QueryGenericAsync(
    "com.foo.HealthService#check");
```

当两种元数据来源都不可用时，无参数方法仍可使用空参数签名发起调用。

### 自动推断参数类型 / Automatic Type Inference

```csharp
DubboRequestResult result = await client.QueryGenericAsync(
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

显式重载不查询元数据中心，也不连接 Telnet，是生产环境中最确定的调用方式：

```csharp
DubboRequestResult job = await client.QueryGenericAsync(
    "com.foo.JobService#getJob",
    new[] { "java.lang.Integer" },
    1234);

DubboRequestResult customer = await client.QueryGenericAsync(
    "com.foo.CustomerService#getCustomer",
    new[] { "java.lang.String", "java.lang.Long" },
    "123",
    1234L);
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
    await client.QueryGenericAsync<JobDto>(
        "com.foo.JobService#getJob",
        new[] { "java.lang.Long" },
        1234L);

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

DubboRequestResult result = await client.QueryGenericAsync(invocation);
```

对于 POJO 参数，客户端会根据 `ParameterTypes` 自动补充泛化调用需要的 `class=<Java FQCN>`。如果 `ParameterTypes` 为空，`DubboClient` 会尝试通过元数据自动推断。

### 兼容的 JSON 调用接口 / Legacy JSON API

原有 `QueryAsync` 接口仍可使用：

```csharp
DubboRequestResult result = await client.QueryAsync(
    "com.foo.JobService#getJob",
    "1234");

DubboRequestResult<MyResponse> typed =
    await client.QueryAsync<MyResponse, long>(
        "com.foo.JobService#getJob",
        1234L);
```

在 `dubbo://` Provider 上，`QueryAsync` 最终也会尝试用元数据解析准确签名。新代码建议使用 `QueryGenericAsync`；当类型存在歧义时使用显式 Java 类型重载。

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
- `MetadataPath`：ZooKeeper 节点或 `telnet://` 来源地址。
- `TypeDefinitions`：FullServiceDefinition 中的 POJO、集合、枚举和引用定义；Telnet 来源通常没有这部分信息。
- `MetadataVersion`、`LoadedAt`：ZooKeeper 数据版本和读取时间。

`GetServiceTypesMetadataAsync` 会先对完整元数据叶子路径执行 ZooKeeper `exists` 并安装 watcher，确认节点存在后才读取数据。节点不存在、连接不可用或文档无效时会抛出包含预期路径的 `DubboMetadataException`；因为 Telnet `ls -l` 不包含 `types`，该接口不会退回 Telnet。成功结果和稳定的“节点不存在”结论都会按服务缓存，节点后续创建或修改时 watcher 会刷新缓存。

## 元数据策略与缓存 / Metadata Strategy and Cache

客户端读取 Provider URL 的 `release` 字段来选择元数据来源：

| Provider `release` | 获取顺序 |
| --- | --- |
| `>= 2.7.3` | 元数据中心优先，失败后 Telnet `ls -l` |
| 已知且 `< 2.7.3` | 直接尝试 Telnet `ls -l` |
| 缺失或格式无法识别 | 元数据中心优先，失败后 Telnet `ls -l` |
| 多个 Provider 版本混合 | 只要存在 `>= 2.7.3` 或未知版本，就优先元数据中心 |

元数据中心节点按以下结构读取：

```text
{MetadataRootPath}/metadata/{interface}/{version?}/{group?}/provider/{application}
```

例如：

```text
/dubbo/metadata/com.foo.JobService/provider/job-service
```

行为说明：

- `MetadataCenterAddress` 不填写时复用注册中心地址和同一个 ZooKeeper 客户端。
- FullServiceDefinition 成功加载后，会对数据节点安装 ZooKeeper watch。
- 节点修改、删除、后续创建，Provider application/version/group 变化以及 ZooKeeper 会话恢复都会触发缓存刷新。
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

| 配置项 | 默认值 | 说明 |
| --- | ---: | --- |
| `DubboRootPath` | `/dubbo/` | 经典接口级注册目录根路径 |
| `MetadataCenterAddress` | 注册中心地址 | 元数据中心 ZooKeeper；相同地址会复用连接 |
| `MetadataRootPath` | `DubboRootPath` | metadata-report group 根路径，内部会规范为无结尾 `/` |
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

- `Random`：加权随机。
- `RoundRobin`：平滑加权轮询。
- `ShortestResponse`：优先选择近期响应较快的节点。
- `ConsistentHash`：根据请求内容计算一致性 Hash。
- `P2CLoadBalance`：比较两次随机候选的负载；无法获取 Provider load 时退化为随机候选。
- `LeastActive`：当前尚未实现，会退化为 `Random`。

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
| .NET 10 | **支持** | 当前库、示例和测试项目均只目标化 `net10.0` |
| .NET 6/7/8/9 | **当前包不支持** | 项目已从 `net6.0` 改为 `net10.0`，未提供多目标构建 |
| ZooKeeper 经典接口级注册 | **支持** | 读取 `/dubbo/{interface}/providers` 并注册 watch |
| Nacos、Consul、Kubernetes 等注册中心 | **未实现** | 当前 `DubboClient` 只实现 ZooKeeper 发现 |
| Dubbo 2.7.3 + `dubbo://` + Hessian2 | **真实验证** | 已验证无参数、单参数、双参数调用以及元数据/Telnet 回退 |
| Dubbo `>= 2.7.3` 的其他版本 | **条件兼容，未逐版本验证** | 需要经典 ZooKeeper 接口级目录、`dubbo://`、Hessian2 和 `$invoke` 泛化能力 |
| Dubbo `< 2.7.3` | **条件兼容，未逐版本验证** | 自动签名依赖 Provider Telnet；也可完全绕过元数据并显式传入 Java 类型 |
| Dubbo 3.x 的 classic `dubbo://` 兼容模式 | **条件兼容，未完整验证** | 仅限继续发布经典接口级 ZooKeeper URL 并接受 Dubbo2/Hessian2 `$invoke` 的 Provider |
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

1. **复用 `DubboClient`。** 长生命周期实例才能充分复用 ZooKeeper、Provider TCP 连接、服务驱动和元数据缓存。
2. **优先使用 `QueryGenericAsync`。** 它清楚地区分参数对象和 Java 签名；旧 `QueryAsync` 主要用于兼容已有代码。
3. **关键接口显式传类型。** 对重载、包装类型、`null`、集合和 POJO，显式 Java 类型比运行时推断更稳定。
4. **优先建设元数据中心。** Telnet 是可选的兜底能力，不应成为生产调用的唯一元数据来源；不要把诊断端口暴露到不可信网络。
5. **确认 Provider URL 完整。** 元数据中心定位依赖 `application`，服务路由还可能依赖 `version` 和 `group`。
6. **分别判断调用成功和业务成功。** 先检查 `QuerySuccess`，再检查业务响应中的 `code`、`success` 等字段。
7. **谨慎重试非幂等请求。** 超时或连接中断不代表 Provider 一定没有执行，请由业务幂等键和重试策略兜底。
8. **根据服务数量配置缓存。** 大量调用不同接口时提高 `MaintainServiceNum`，或设为 `0`；同时关注连接和内存占用。
9. **原生调用优先使用 `Random`、`RoundRobin` 或 `ConsistentHash`。** `LeastActive` 尚未实现，P2C 在没有 Provider load 时会退化。
10. **部署前用真实 Provider 做兼容测试。** 尤其是未在兼容矩阵中标记为“真实验证”的 Dubbo 版本、复杂 POJO 和自定义 Hessian 类型。

## 异常与排障 / Troubleshooting

### 找不到服务或 Provider

检查：

- `DubboRootPath` 是否与注册中心一致。
- `/dubbo/{完整接口名}/providers` 是否存在有效子节点。
- Provider URL 是否以 `dubbo://` 或 `tri://` 开头。
- Provider host 是否为 IP 地址，而不是域名。
- 接口名、服务 `version` 和 `group` 是否正确。

服务发现失败通常以 `QuerySuccess=false` 的 `DubboRequestResult` 返回，详情在 `ErrorMeaasge`。

### `DubboMetadataException`：无法获取方法签名

异常会分别说明元数据中心和 Telnet 的失败原因。依次检查：

1. Provider URL 的 `release`、`application`、`version` 和 `group`。
2. `MetadataCenterAddress` 和 `MetadataRootPath` 是否指向实际元数据中心。
3. 预期 FullServiceDefinition 节点是否存在：

   ```text
   {root}/metadata/{interface}/{version?}/{group?}/provider/{application}
   ```

4. Provider 是否配置并成功上报 metadata-report。只有 consumer 节点不代表存在 provider 方法定义。
5. Provider 注册的 `dubbo://` 地址和端口是否开放 Telnet `ls -l`。

无法修复元数据来源时，按异常建议改用显式类型重载：

```csharp
await client.QueryGenericAsync(
    "com.foo.JobService#getJob",
    new[] { "java.lang.Integer" },
    1234);
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
`http://{host}:{port}/{service}/{method}` 发送 HTTP/1.1 JSON POST，正文必须是参数数组，单参数也应为 `[value]`。有多个 version/group 时还要保证注册 URL 中这两个字段准确。

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

需要 .NET 10 SDK：

```bash
dotnet restore DubboNet.sln
dotnet build DubboNet.csproj --no-restore
dotnet test Tests/UnitTestForDubboNet/UnitTestForDubboNet.csproj --no-restore
```

真实环境集成测试接受 ZooKeeper 地址作为第一个参数：

```bash
dotnet run \
  --project Tests/NativeDubboIntegration/NativeDubboIntegration.csproj \
  -- 127.0.0.1:2181
```

集成测试程序会查询元数据并调用 `Program.cs` 中配置的真实服务。运行前请先替换为测试环境接口，并确认这些方法允许被实际调用；不要把它当作无副作用的单元测试。

## License

本项目使用 [MIT License](LICENSE)。
