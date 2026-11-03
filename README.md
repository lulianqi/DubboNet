# DubboNet

**English** | [简体中文](readme_cn.md)

DubboNet is a Dubbo client library for .NET. It discovers Providers through ZooKeeper or Nacos, selects a transport actuator based on the Provider URL, and invokes Java interfaces through classic Dubbo TCP/Hessian2 or Dubbo 3 Triple HTTP/JSON. The caller does not need to reference a Java interface JAR.

> The current Apache import branch targets **.NET 10**: the library, examples, and tests all use `net10.0`. This change does not replace previously published NuGet packages from the personal project. Native Dubbo TCP invocation has been verified against a **Dubbo 2.7.3** Provider in a real environment; the first phase of Triple support has been verified against a **Dubbo 3.3.6** Java Interface Provider. Read [Compatibility](#compatibility) for the boundaries of support for other versions.

## Features

- Automatic Provider discovery and subscriptions through classic interface-level ZooKeeper directories or interface-level Nacos Naming services.
- Automatic actuator selection based on the Provider URL scheme:
  - `dubbo://`: `NativeDubboActuatorSuite`, using Dubbo2 TCP frames and Hessian2 generic invocation.
  - `tri://`: `TripleDubboActuatorSuite`, using Dubbo 3.3 Java Interface Unary HTTP/JSON invocation.
- Load-balancing strategies including weighted random, smooth weighted round robin, shortest response, and consistent hashing.
- Automatic Java method signature lookup through the Dubbo metadata center or Provider Telnet `ls -l`.
- Overload resolution based on argument count and CLR types, with cached method metadata.
- Explicit Java parameter types for missing metadata, `null`, wrapper types, and ambiguous overloads.
- Service versions, groups, custom attachments, POJO, collection, and Map parameters.
- Request IDs, Dubbo response status, serialization IDs, response attachments, and dynamic raw results.
- Reuse of ZooKeeper/Nacos clients for the same address, Provider connections, and service drivers across requests.
- Optional process-wide consumer registration: interfaces are registered when invoked, references are counted across clients, and registrations use ZooKeeper ephemeral nodes or Nacos ephemeral instances.
- Chinese and English XML documentation for the main public APIs, with `DubboNet.xml` included alongside the assembly and in the NuGet package.

## Architecture

```text
Application
   |
   v
DubboClient
   |-- Registry
   |       |-- ZooKeeper --> /dubbo/{interface}/providers + Watch
   |       +-- Nacos -----> providers:{interface}:{version}:{group} + Subscribe
   |
   |-- DubboMetadataManager
   |       |-- release >= 2.7.3 or unknown --> Metadata center FullServiceDefinition
   |       |                                      |
   |       |                                      +-- One-time Telnet ls -l on failure
   |       +-- release < 2.7.3 --------------> One-time Telnet ls -l
   |
   +-- DubboServiceDriver --> Load balancing --> Select actuator by scheme
           |-- dubbo:// --> NativeDubboActuatorSuite --> Dubbo TCP + Hessian2
           +-- tri://   --> TripleDubboActuatorSuite --> Triple Unary HTTP/JSON
```

`release` only determines the metadata lookup order; the Provider URL scheme determines the actuator. In a Provider URL, `dubbo=2.0.2` identifies the classic wire protocol version, while `version` and `group` identify service routing. These fields are not interchangeable.

## Installation

The project currently declares version `3.6.2` and the NuGet package ID `DubboNet`. If that version is available in your NuGet feed, run:

```bash
dotnet add package DubboNet --version 3.6.2
```

If the package has not been published, or you need the latest implementation in this repository, reference the source project directly:

```xml
<ItemGroup>
  <ProjectReference Include="../DubboNet/DubboNet.csproj" />
</ItemGroup>
```

Building the current source requires the .NET 10 SDK; the examples and tests use the .NET 10 Runtime. The library built from this branch only targets `net10.0` and cannot be referenced directly by .NET 6/7/8/9 applications. Those applications must upgrade or continue using a previously published compatible package. The main dependencies are `DistributedLock.ZooKeeper`, `nacos-sdk-csharp`, and `NHessian`.

## Quick Start

Create a long-lived `DubboClient` and dispose it when the application shuts down. Do not create a new instance for every request:

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

To use Nacos, change the registry address to the required `nacos://` format. This prefix also selects the registry implementation:

```csharp
using var client = new DubboClient(
    "nacos://127.0.0.1:8848",
    new DubboClient.DubboClientConf
    {
        // Naming group; defaults to DEFAULT_GROUP when omitted.
        DubboRootPath = "dubbo-prod",
        // Config metadata group is independent; defaults to dubbo when omitted.
        MetadataRootPath = "dubbo-metadata-prod"
    });
```

The service and method names can be separated by `#`, `.`, or `/`, for example:

```text
com.foo.JobService#getJob
com.foo.JobService.getJob
com.foo.JobService/getJob
```

Use `#` to make the boundary between the fully qualified Java interface name and the method name explicit.

### No-argument Invocation

```csharp
DubboRequestResult result = await client.QueryAsync(
    "com.foo.HealthService#check");
```

If neither metadata source is available, a no-argument method can still be invoked with an empty parameter signature.

### Automatic Type Inference

```csharp
DubboRequestResult result = await client.QueryAsync(
    "com.foo.CustomerService#getCustomer",
    "123",
    1234L);
```

Automatic inference does not guess Java types from JSON alone. The client first obtains service method signatures, then selects a unique overload based on the method name, argument count, and CLR argument types. If no unique overload can be determined, it throws `DubboMetadataException`.

In particular:

- C# `1234` is an `int`; `1234L` is a `long`.
- A value alone cannot reliably distinguish Java `int` from `java.lang.Integer`, or `long` from `java.lang.Long`.
- `null` provides no CLR type information.
- Multiple Java overloads with the same argument count and compatible types can remain ambiguous.

Prefer an explicit-type overload in these cases.

### Explicit Java Parameter Types

Explicit-type overloads do not require metadata lookup to succeed and provide the most deterministic invocation path for production. The client may still attempt a best-effort metadata lookup to assist protocol selection, but a lookup failure does not prevent sending a request with the supplied signature:

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

Each type name must be an exact Java type that the Provider's `$invoke` can load, for example:

- Primitives: `int`, `long`, `boolean`, `double`
- Wrappers: `java.lang.Integer`, `java.lang.Long`, `java.lang.Boolean`
- Collections and arrays: `java.util.List`, `java.util.Map`, `java.lang.String[]`
- POJO: `com.foo.api.dto.CustomerRequest`

### Typed Response

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

`ResultModle` uses `System.Text.Json` to deserialize `Result`. If local deserialization fails, details are written to `ErrorMeaasge`. This is separate from whether the Provider's business result indicates success.

### DubboInvocation

Use `DubboInvocation` to specify an exact signature, service version, group, and custom attachments:

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

For POJO parameters, the client automatically supplies the `class=<Java FQCN>` required for generic invocation based on `ParameterTypes`. If `ParameterTypes` is empty, `DubboClient` attempts to infer it from metadata.

### Unified Invocation API

All public request entry points on `DubboClient` are named `QueryAsync`. Common overloads include:

```csharp
// No arguments
await client.QueryAsync("com.foo.HealthService#check");

// A single argument; an array argument is not accidentally expanded
await client.QueryAsync("com.foo.UserService#batchGet", new long[] { 1, 2 });

// Two or more arguments
DubboRequestResult result = await client.QueryAsync(
    "com.foo.CustomerService#getCustomer",
    "123",
    1234L);

// Typed response
DubboRequestResult<MyResponse> typed = await client.QueryAsync<MyResponse>(
    "com.foo.JobService#getJob",
    1234L);
```

The release3.0 branch no longer exposes `QueryGenericAsync`, raw JSON text overloads, or the old overloads extended by the number of generic request types. To migrate, replace `QueryGenericAsync` with `QueryAsync`; explicit Java type calls require arguments in a separate `object[]`. A string now always represents an ordinary string argument and is no longer interpreted as comma-separated raw JSON requests.

### Direct Actuator Usage

If you already know the Provider IP and port and do not need a registry, create a native actuator directly. This path provides no service discovery, load balancing, or metadata inference, so always supply the exact Java signature:

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

Dubbo 3.3+ Java Interface services can also be called directly with the first-phase Triple actuator. Requests are sent to
`/{service}/{method}`; the body is always a JSON argument array, with service version/group routing headers:

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

If the Provider exposes Dubbo Telnet commands, you can explicitly use `DubboActuator` for diagnostics:

```csharp
using DubboNet.DubboService;

using var telnet = new DubboActuator("127.0.0.1", 20880);

var methods = await telnet.GetDubboServiceFuncAsync(
    "com.foo.JobService");
var status = await telnet.GetDubboStatusInfoAsync();
```

`DubboActuator.SendQuery` and `TelnetDubboActuatorSuite` also retain Telnet `invoke` business calls, but require Telnet to be enabled on the Provider. For new business calls, use `NativeDubboActuatorSuite` or `DubboClient`; Telnet is primarily for diagnostics such as `ls`, `status`, `ps`, and `trace`.

## Metadata Inspection

Inspect all method overloads before invoking a method:

```csharp
using DubboNet.Clients.DataModle;

IReadOnlyList<DubboMethodMetadata> overloads =
    await client.GetMethodMetadataAsync(
        "com.foo.JobService",
        "getJob");

// A full endpoint is also accepted.
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

// Read FullServiceDefinition.types from the metadata center only, for tools
// such as DubboTester to build POJO/collection/nested parameter editors; no Telnet fallback.
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

Key fields include:

- `ParameterTypes`, `ReturnType`: exact Java signatures.
- `MetadataSource`: `MetadataCenter` or `Telnet`.
- `Application`, `Version`, `Group`: information about the corresponding Provider.
- `MetadataPath`: a ZooKeeper node, `nacos-config://` configuration location, or `telnet://` source address.
- `TypeDefinitions`: POJO, collection, enum, and reference definitions from FullServiceDefinition; Telnet sources generally do not provide these.
- `MetadataVersion`, `LoadedAt`: metadata version and load time. Nacos Config currently has no equivalent node version, so its version is `-1`.

`GetServiceTypesMetadataAsync` first checks whether the full metadata location exists and installs a change listener, then reads the data only after confirming its existence. ZooKeeper uses `exists`/watchers; Nacos uses Config listeners. A missing location, unavailable connection, or invalid document results in a `DubboMetadataException` containing the expected location. Because Telnet `ls -l` does not include `types`, this API never falls back to Telnet. Successful results and stable "not found" conclusions are cached per service; subsequent metadata creation or modification refreshes the cache.

## Metadata Strategy and Cache

The client uses the Provider URL's `release` field to select metadata sources:

| Provider `release` | Lookup order |
| --- | --- |
| `>= 2.7.3` | Metadata center first, then Telnet `ls -l` on failure |
| Known and `< 2.7.3` | Try Telnet `ls -l` directly |
| Missing or unrecognized format | Metadata center first, then Telnet `ls -l` on failure |
| Mixed Provider versions | Prefer the metadata center if any Provider is `>= 2.7.3` or has an unknown version |

ZooKeeper metadata is read from the following node structure:

```text
{MetadataRootPath}/metadata/{interface}/{version?}/{group?}/provider/{application}
```

For example:

```text
/dubbo/metadata/com.foo.JobService/provider/job-service
```

For Nacos Config, `MetadataRootPath` with leading and trailing slashes removed is the group. The DataId follows the official Dubbo convention:

```text
Group:  {MetadataRootPath}
DataId: {interface}:{version}:{serviceGroup}:provider:{application}
```

For example, `MetadataRootPath=/dubbo-3.3.6` selects the Config group `dubbo-3.3.6`.
Nacos Naming defaults to `DEFAULT_GROUP`, while Config metadata independently defaults to `dubbo`.
If a Provider writes metadata to a custom group, set `MetadataRootPath` explicitly; the Naming group is not inherited automatically.

Behavior:

- If `MetadataCenterAddress` is omitted, the registry address and the same ZooKeeper or Nacos client are reused. The registry and metadata center may also use different registry types.
- A successful FullServiceDefinition load installs a ZooKeeper watch or Nacos Config listener.
- Metadata modification, deletion, subsequent creation, and changes to Provider application/version/group refresh the cache. ZooKeeper session recovery also reinstalls watches.
- Nacos checks the service directory for referenced interfaces every 30 seconds, discovering newly registered versions/groups without recreating the client. Directory scans for the same address are shared by Naming group. Instance changes within existing services still use SDK subscriptions.
- When the last available Provider goes offline, the service driver is cleared. Disabled and zero-weight nodes receive no requests and can rejoin after recovery.
- Telnet metadata is cached per service. A single probe may try multiple `dubbo://` Providers of that service in sequence until one succeeds.
- Both successful Telnet results and "Telnet unavailable" failures are cached, so the same service is not repeatedly connected to during one `DubboClient` lifetime.
- If a watched metadata node is created later, the cache switches back to the higher-priority FullServiceDefinition.
- Transient metadata center connection failures are not cached permanently. Later requests retry the metadata center while reusing the existing positive or negative Telnet cache.
- If a metadata center document exists but lacks the requested method or overload, the client supplements it with the same one-time Telnet result.
- Telnet is only used for method signatures and explicit diagnostics; native business invocation still uses Dubbo TCP.
- Telnet fallback only connects to the address and port of a registered `dubbo://` Provider. A separate QoS/Telnet port cannot be configured.

If Telnet is enabled for a service during runtime after a `DubboClient` has cached "Telnet unavailable", recreate that client. This avoids repeating failed connection attempts on every invocation.

## Configuration

```csharp
using var client = new DubboClient(
    "zk-1:2181,zk-2:2181",
    new DubboClient.DubboClientConf
    {
        DubboRootPath = "/dubbo/",
        MetadataCenterAddress = "metadata-zk:2181", // Optional
        MetadataRootPath = "/dubbo",                // Optional
        DubboRequestTimeout = 60_000,
        TelnetMetadataTimeout = 10_000,
        MaintainServiceNum = 20,
        NowLoadBalanceMode = DubboClient.LoadBalanceMode.Random
    });
```

ZooKeeper addresses accept multiple comma-separated nodes. An authentication string can be supplied as:

```text
zk-1:2181,zk-2:2181 [digest user:password]
```

Nacos addresses must start with `nacos://` and also accept multiple comma-separated nodes. Configure namespaces and authentication through query parameters:

```text
nacos://nacos-1:8848,nacos-2:8848?namespace=prod&username=nacos&password=nacos
```

Use the namespace ID for `namespace`; omission, an empty value, or `public` selects the default namespace. The `accessKey` and `secretKey` query parameters are also supported.
Special characters in query values, such as `+`, `&`, and `#`, must be URL-encoded; for example, encode a `+` in a password as `%2B`.
Hostnames, address order, and query parameter order are normalized; namespace, username, password, and key values are strictly case sensitive.
Multiple `DubboClient` instances with equivalent configurations share the underlying `MyNacos` instance, including clients created at different times. Each instance contains separate Naming and Config SDK clients; sharing does not mean using only one TCP connection.
Naming/Config subscriptions are isolated by caller, and listeners are removed when the last subscriber is disposed. Directory refresh stops when there are no interface subscriptions.

**Nacos SDK lifecycle limitation:** `nacos-sdk-csharp 1.3.10` does not stop its internal RPC worker threads when `ShutDown()` is called.
The shared pool therefore retains SDK instances for reuse and limits the total number of distinct connection configurations per process to **32**, including idle instances.
At the limit, a new configuration throws an explicit exception; existing configurations remain reusable. `Dispose()` removes this client's subscriptions and business connections,
but does not mean the SDK background connections have exited. Applications requiring more than 32 distinct address/namespace/credential combinations must use separate processes or restart the process.
The current implementation does not hide SDK thread leaks by evicting idle instances and creating replacements.

### Optional Consumer Registration

Consumer registration is disabled by default. When enabled, it makes the interfaces referenced by this application visible in the registry and compatible management platforms.
It is independent of Provider subscriptions and RPC calls and does not automatically add governance features such as dynamic routing or configuration overrides.

Configuration applies to all `DubboClient` instances in the process, not to `DubboClientConf`. Call
`ConfigureConsumerRegistration` before creating clients or during runtime; calling it again hot-switches registration without recreating clients.
Each call copies the options, so modifying the original options object alone has no effect. Omitted or blank `ApplicationName`, `Host`,
and `InstanceId` inherit their previous values. The first enable operation requires an application name; an automatically generated instance ID remains stable across switches.
`RetryInterval` and `ShutdownTimeout` are updated from each supplied configuration, using defaults when omitted.
All clients in one process use the same application identity; separate processes automatically generate different instance IDs.

```csharp
using DubboNet.Clients;
using DubboNet.Clients.DataModle;

DubboClient.ConfigureConsumerRegistration(new ConsumerRegistrationOptions
{
    Enabled = true,
    ApplicationName = "orders-dotnet",
    // Optional: explicitly set the advertised local IP for containers, multiple NICs, or NAT.
    // Host = "10.0.0.12",
    RetryInterval = TimeSpan.FromSeconds(10),
    ShutdownTimeout = TimeSpan.FromSeconds(3)
});

using (var first = new DubboClient("127.0.0.1:2181"))
using (var second = new DubboClient("127.0.0.1:2181"))
{
    await first.QueryAsync("com.foo.EchoService#echo", "hello");
    await second.QueryAsync("com.foo.EchoService#echo", "world");
    // Both clients' references to the same service produce one shared consumer record.

    // Disable at runtime: unregister in the background; RPC and Provider subscriptions are unaffected.
    DubboClient.ConfigureConsumerRegistration(new ConsumerRegistrationOptions { Enabled = false });

    // Enable at runtime: inherit the application identity and register services still referenced by live clients.
    DubboClient.ConfigureConsumerRegistration(new ConsumerRegistrationOptions { Enabled = true });
}

// Optional: wait for current background registration/unregistration attempts during host shutdown;
// network failures still produce warnings and retries.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await DubboClient.FlushConsumerRegistrationsAsync(timeout.Token);
```

Registration and disposal rules:

- Registration is asynchronous and only starts when `QueryAsync` prepares to invoke a service. Constructing a client, reading the service directory, or querying metadata alone does not register it.
- Service references are tracked locally even when registration is disabled by default or at runtime, without consumer registration network calls. Enabling registration later catches up; disposed clients are not revived.
- Configuration calls synchronously update the desired state. Network registration/unregistration converges serially in the background without waiting for registry confirmation. Rapid switches converge to the latest configuration; intermediate states are not guaranteed to be reported.
- Disabling registration cleans up both confirmed and possibly written records. If an in-flight operation cannot be canceled, cleanup resumes against the latest state after it completes. Network failures can delay unregistration, which continues retrying in the background.
- Changing the application name, IP, or instance ID at runtime first confirms cleanup of the old identity, then registers the new one. A failed cleanup does not create new records prematurely. Re-enabling is no longer accepted once process shutdown begins.
- Records are deduplicated by shared registry connection, root path/Nacos Naming group, interface, and service version/group. Different application instances do not overwrite each other.
- Records are not recreated for every request. A service reference remains until its client is disposed; Provider cache eviction does not mean the application no longer references that service.
- Disposing one client removes only its references. A service is unregistered when its last reference is released; other services and clients on the same connection are unaffected.
- `DubboInvocation.Version/Group` identifies the referenced service version and group. Unspecified fields remain empty in the consumer record rather than guessing a Provider's version.
- Registration/unregistration failures do not block RPC. Failed operations and possibly written records stay in the background retry queue, using exponential backoff from `RetryInterval` up to `max(RetryInterval, 60 seconds)`. Different services retry independently.
- `FlushConsumerRegistrationsAsync` can trigger a retry and wait for the current attempt. It is not a registry confirmation barrier and does not cancel background work. Canceling the wait only stops the caller from waiting.
- Warnings include the operation, connection identifier, interface, root path, version/group, consecutive failure count, and exception type chain. Recovery is logged at Info level; raw exception messages and connection credentials are not logged. Repeated failure warnings are rate-limited. For console output, set `MyCommonHelper.MyLogger.LogPrintLevel = MyCommonHelper.MyLogger.LogType.Warn` (the default is Error); use Info to see recovery logs.
- Enabling registration requires write permissions on consumer directories/instances. With authenticated ZooKeeper connections, new nodes use creator ACLs; existing directory ACLs are not modified.
- `Dispose()` waits at most `ShutdownTimeout`. After a timeout, cleanup continues retrying on the shared connection. Normal process shutdown also attempts best-effort unregistration.

ZooKeeper uses ephemeral nodes under `/{root}/{interface}/consumers/{URL-encoded consumer://URL}`
and reuses existing session heartbeats. The server deletes nodes when the session expires; a new session restores records that are still referenced.
Nacos uses ephemeral instances named `consumers:{interface}:{version}:{group}`, maintained by the existing Naming SDK's
connection checks and re-registration on reconnect. Consumer instances use a separate cluster identifier to avoid collisions between processes on the same host.
`RetryInterval` is the base interval for failure retries and session recovery checks, not an additional heartbeat.
A new ZooKeeper session only restores consumers that are enabled and still referenced; consumer recovery checks stop once registration is disabled and cleanup is complete.

Idle Nacos connections remain in the process-wide pool, so releasing the last service reference explicitly unregisters it rather than relying on SDK disconnection.
If the process crashes or is forcibly terminated, ZooKeeper/Nacos session or connection expiration eventually removes ephemeral records.
Cleanup after network disconnection is not instantaneous; its timing depends on server timeout settings.

If the process and shared connection remain healthy but the application forgets to call `Dispose`, heartbeats cannot determine that a client is no longer used,
and consumer records may remain. Use `using` or DI-managed lifetimes and dispose clients during host shutdown.
GC/finalizers and process-exit callbacks do not replace deterministic disposal.

### Direct Nacos Directory Access

`MyNacos` is a public Nacos wrapper whose public signatures contain only .NET and DubboNet-owned types. Applications such as DubboTester
do not need to reference or call `nacos-sdk-csharp` directly. In addition to internal DubboClient service discovery and metadata lookup, it provides
Naming/Config APIs corresponding to the directory access capabilities of `MyZookeeper`:

```csharp
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;

using var nacos = new MyNacos("nacos://127.0.0.1:8848");

// List all Naming groups containing Dubbo Providers in the current namespace.
IReadOnlyList<string> roots = await nacos.ListRegistryRootsAsync();

// List Java interface names in a group, without providers:, version, or service group encoding.
IReadOnlyList<string> services = await nacos.ListServicesAsync("dubbo-3.3.6");

// Get all Providers of an interface, including disabled/unhealthy nodes for a complete directory view.
IReadOnlyList<DubboServiceEndPointInfo> providers = await nacos.GetProvidersAsync(
    "com.example.dubbonet.api.EchoService",
    "dubbo-3.3.6");

// To retain raw Nacos instance metadata, list raw service names and read DubboNet-owned instance DTOs.
IReadOnlyList<string> rawServices = await nacos.ListNacosServicesAsync("dubbo-3.3.6");
IReadOnlyList<NacosServiceInstanceInfo> instances = await nacos.GetServiceInstancesAsync(
    rawServices[0],
    "dubbo-3.3.6");
```

Common public APIs:

- `ListCatalogServicesAsync`: reads the raw Naming catalog, groups, and instance statistics in the current namespace.
- `ListRegistryRootsAsync`: returns Naming groups containing Dubbo Providers.
- `ListServicesAsync`: returns Dubbo Java interface names in a specified group.
- `ListNacosServicesAsync` / `GetServiceInstancesAsync`: reads raw service and instance metadata without returning Nacos SDK types.
- `GetProvidersAsync`: returns `DubboServiceEndPointInfo` for direct mapping to a service tree.
- `SubscribeServiceAsync` / `SubscribeConfigAsync`: subscribes to instance or configuration changes; use `RemoveSubscriber` to unsubscribe.
- `GetConfigAsync` / `ConfigExistsAsync`: reads arbitrary Nacos Config configurations.
- `TestConnectionAsync` / `GetServerStatusAsync`: checks connectivity and Naming server status.

Automatic group discovery uses the Nacos Catalog API because the Naming SDK can only enumerate a known group. Services, instances, and subscriptions
within known groups still use the Naming SDK. The connection string's `username`/`password` are used for Catalog API login and cached access tokens;
multi-node addresses provide sequential failover. `accessKey`/`secretKey` remain in use for Naming/Config SDK requests. If a Nacos authentication plugin does not
support standard token login, call `ListServicesAsync(knownGroup)` directly or use an account compatible with the standard Catalog API.

Nacos Naming defaults to `DEFAULT_GROUP`, while Config metadata independently defaults to `dubbo`.
Equivalent addresses share clients, but namespaces and credentials are case sensitive. Catalogs refresh every 30 seconds.
The SDK cannot shut down RPC workers, so a bounded process-wide pool retains at most 32 distinct configurations;
disposing a DubboClient removes its subscriptions and business connections, not the retained SDK workers.

| Setting | Default | Description |
| --- | ---: | --- |
| `DubboRootPath` | Automatic | ZooKeeper: `/dubbo/`; Nacos Naming: `DEFAULT_GROUP` (leading/trailing `/` removed automatically) |
| `MetadataCenterAddress` | Registry address | ZooKeeper or `nacos://` Nacos metadata center; the same address reuses the client |
| `MetadataRootPath` | Automatic | Nacos Config: `dubbo`; ZooKeeper: the ZooKeeper registry root, or `/dubbo` for mixed registry/metadata center types |
| `DubboRequestTimeout` | `60000` ms | Maximum wait for a business request |
| `TelnetMetadataTimeout` | `10000` ms | Timeout for one Telnet metadata probe; non-positive values revert to the default |
| `MaintainServiceNum` | `20` | Number of reusable service drivers; `0` means unlimited |
| `NowLoadBalanceMode` | `Random` | Current load-balancing strategy |
| `DubboActuatorSuiteMaxConnections` | `20` | Maximum actuator connections; mainly affects the Telnet pool, while the native actuator multiplexes a single TCP connection |
| `DubboActuatorSuiteAssistConnectionAliveTime` | `300` seconds | Idle timeout for Telnet auxiliary connections; `0` disables proactive release |
| `DubboActuatorSuiteMasterConnectionAliveTime` | `1200` seconds | Idle timeout for the Telnet master connection; `0` disables proactive release |
| `DefaultServiceName` | `null` | Default interface when the service name is omitted |
| `DefaultFuncName` | `null` | Default method when the endpoint is empty |

Load-balancing strategies:

- `Random`: weighted random selection using a shared random source and 64-bit cumulative weights.
- `RoundRobin`: concurrency-safe smooth weighted round robin, with per-method state and Provider weight updates.
- `LeastActive`: selects the node with the fewest in-flight requests observed by this consumer; ties use weighted random selection.
- `P2CLoadBalance`: samples two distinct nodes by weight and compares their local in-flight request counts, breaking ties with weighted random selection. It does not depend on Telnet Provider load and works for Native/Triple. Weights affect candidate sampling and tie-breaking, not a guarantee of strictly proportional final traffic.
- `ShortestResponse`: estimates response time from the mean duration of successful requests over the last 30 seconds and the in-flight request count, breaking ties with weighted random selection. Failed calls are counted separately and incur a timeout-scale score penalty; fast failures are not treated as fast successful responses. Normal business data with a failing business `code` is not counted as a protocol failure.
- `ConsistentHash`: uses stable MurmurHash2, UTF-8 request content, and 160 virtual nodes per node. The same candidate set and request content map consistently across processes; compatibility with Java Dubbo's hash implementation is not guaranteed. With no hash key, selection falls back to weighted random.

Active counts represent in-flight requests observed by this `DubboClient`, not global server load or TCP connection counts; connections can be multiplexed.
Statistics are isolated by Provider, service, method signature, and invocation version/group, so fast and slow methods on the same node do not share them.
The unified send entry point reserves a count exactly once, releasing it on success, failure, timeout, cancellation, or exception. Node removal and service cache eviction do not prematurely close connections still held by in-flight requests. Explicitly disposing the entire client still closes its connections.
An expired statistics window does not make a node the fastest with `0ms`. Nodes without samples are probed at 5-second intervals, with at most one cold-start probe in flight.
If all nodes lack usable samples and are busy, selection falls back to least active; this is not a rate limiter. Statistics start with an empty window, suit long-lived clients, and do not represent other processes' load.
Method state is cleaned up when nodes are removed, and idle signature caches are bounded. Timing uses a monotonic clock. Node selection, round-robin state, and reservation updates happen in short critical sections that do not wait for network responses.
These optimizations add neither automatic RPC retries nor circuit breaking, and do not change the default `Random` strategy or public APIs.

**Consistent hash upgrade note:** the old implementation used process-randomized `HashCode.Combine` and ASCII request encoding.
Switching to a stable hash, UTF-8, and a new virtual-node count changes existing key-to-node mappings. Applications relying on session affinity or local caches must coordinate upgrades; do not assume consistent routing while old and new versions coexist.

## Result Semantics

Common `DubboRequestResult` fields:

| Field | Meaning |
| --- | --- |
| `QuerySuccess` | Success at the transport and Dubbo invocation layers, not business success |
| `Result` | Response value converted to a JSON string |
| `RawResult` | Dynamic .NET value before JSON conversion; provided only by the native Dubbo actuator |
| `ErrorMeaasge` | Details of connection, timeout, protocol, Provider exception, or local deserialization errors |
| `RequestElapsed` | Client request duration including network waits, in milliseconds |
| `ServiceElapsed` | Service duration when available in Telnet/HTTP modes; native Dubbo does not provide Provider execution time by default |
| `ServiceElapsedAvailable` | Whether `ServiceElapsed` is meaningful |
| `RequestId` | Native Dubbo Request ID |
| `ResponseStatus` | Native Dubbo response status; `20` means protocol success |
| `SerializationId` | Serialization ID in the Dubbo frame; Hessian2 is `2` |
| `ResponseAttachments` | Native Dubbo response attachments |

For a response such as the following, `QuerySuccess` can still be `true` even when the business field `success` is `false`, provided transport, protocol handling, and Provider method execution completed normally:

```json
{
  "success": false,
  "code": "JOB_NOT_FOUND",
  "message": "job does not exist"
}
```

Handle the two layers separately:

```csharp
if (!result.QuerySuccess)
{
    // Invocation-layer errors: connection, timeout, Dubbo status, Provider-thrown exceptions, etc.
    Console.WriteLine(result.ErrorMeaasge);
}
else
{
    // Then check code/success in Result or ResultModle for business success.
}
```

## Compatibility

The table distinguishes real-environment verification from conditional compatibility paths. It is not an unconditional support commitment for an entire Dubbo version line.

| Component or version | Support status | Notes |
| --- | --- | --- |
| .NET 10 | **Current target** | Library, examples, and tests target `net10.0`; CI uses the .NET 10 SDK |
| .NET 6/7/8/9 | **Cannot reference the current branch** | No lower TFM is provided by this branch; this change does not replace previously published personal-project packages |
| .NET Framework / .NET Standard | **Not supported by the current package** | No `netstandard2.0`, `netstandard2.1`, or .NET Framework target |
| Classic interface-level ZooKeeper registration | **Supported** | Reads `/dubbo/{interface}/providers` and installs watches |
| Nacos 2.5.4 interface-level registration | **Verified in a real environment** | Naming Provider discovery/subscriptions and Config remote metadata reads/listeners; verified with Dubbo 3.2.20 and 3.3.6 |
| Other registries such as Consul or Kubernetes | **Not implemented** | `DubboClient` currently implements ZooKeeper and Nacos |
| Dubbo 2.7.3 + `dubbo://` + Hessian2 | **Verified in a real environment** | No-argument, single-argument, two-argument calls, and metadata/Telnet fallback verified |
| Other Dubbo versions `>= 2.7.3` | **Conditionally compatible; not verified individually** | Requires classic interface-level ZooKeeper directories, `dubbo://`, Hessian2, and `$invoke` generic invocation |
| Dubbo `< 2.7.3` | **Conditionally compatible; not verified individually** | Automatic signatures depend on Provider Telnet; explicit Java types can bypass metadata entirely |
| Classic `dubbo://` compatibility mode in Dubbo 3.x | **Verified for specific versions** | Dubbo2/Hessian2 `$invoke` Providers in 3.2.20 and 3.3.6 verified with ZooKeeper/Nacos; other versions still require testing |
| Dubbo 3 application-level service discovery | **Not implemented** | The Dubbo 3 application-level service discovery metadata layout is not implemented |
| Dubbo 3.3.6 + `tri://` + Java Interface Unary JSON | **Verified in a real environment (first phase)** | HTTP/1.1, `application/json`, version/group/timeout headers, and attachments |
| Triple Protobuf/IDL, streaming, gRPC framing | **Not implemented** | The first phase only covers Java Interface Unary HTTP/JSON, not full Triple/gRPC capabilities |
| Dubbo Telnet/QoS | **Optional** | Metadata fallback and diagnostics only when the registered Provider address and port expose the required commands |
| Explicit version/group routing | **Strict matching (compatibility change)** | Non-empty `Version` / `Group` must match Provider identities exactly, including case, with no fallback to another version/group. Unspecified fields impose no constraint. No matching nodes results in no available Provider |

## Protocol and Serialization Limits

The native actuator currently has the following boundaries:

- Uses the classic 16-byte Dubbo2 TCP header; the flag for normal two-way Hessian2 requests is `0xc2`.
- The Dubbo protocol version in the request body is fixed at `2.0.2`, distinct from the Provider framework's `release`.
- Sends only `$invoke` generic invocations, not strongly typed Java interface invocations.
- **Only Hessian2 is supported, with serialization ID `2`.** Responses using other serialization IDs fail.
- Provider URL fields `serialization` and `prefer.serialization` do not currently switch the client encoder.
- Supports common scalars, strings, dates, binary data, arrays, collections, Maps, and POJO. Unsupported Hessian2 tags produce protocol errors.
- POJO parameters are encoded as generic Maps with `class`; field names must match Java DTO properties.
- Source-style generic parameters in Telnet output undergo Java type erasure; for example, `java.util.Map<String, Long>` becomes `java.util.Map`.
- A single native TCP connection multiplexes concurrent requests by Request ID. Writes are serialized; responses may arrive out of order.
- A received response body is limited to 8 MiB by default, with no current `DubboClientConf` setting to change it.
- Hessian2 response wire nesting and reference-expansion depth during JSON conversion are each limited to 64 levels. Even shallow wire data can be rejected if shared references form a deep expansion chain. Errors distinguish wire nesting from reference expansion. Chunked strings/binary are assembled iteratively; chunk counts do not count as nesting depth.
- To prevent small responses from amplifying memory through shared references, return values/exceptions and attachments share a budget of 100,000 expanded nodes (containers and values) and a conservative 32 MiB JSON size estimate. Each repeated reference expansion is charged again; strings are estimated using the worst-case escaped size of UTF-16 characters, so rejection can occur before actual JSON reaches 32 MiB. Exceeding limits produces a catchable protocol error rather than replacing valid shared references with placeholders. These safety limits are not configurable; paginate or simplify very large or deeply chained responses on the server.
- Responds to two-way event/heartbeat frames sent by Providers.
- Does not provide TLS transport or custom Dubbo serialization extensions.
- Failed request writes are not replayed automatically, avoiding duplicate business operations when the Provider may already have executed the request. Later requests reconnect.
- Hosts in ZooKeeper Provider URLs must currently be IP addresses; domains/hostnames are not automatically resolved.

See [Docs/NativeDubboProtocol.md](Docs/NativeDubboProtocol.md) for frame headers, request fields, and response result flags.

See [Docs/TripleProtocol.md](Docs/TripleProtocol.md) for the first-phase Triple request format, routing headers, and explicit limitations.

Each Triple request uses one deadline covering both response headers and the entire response body. The Provider URL's `timeout` overrides the client's `DubboRequestTimeout` and may be longer or shorter. If absent, the client default applies; the effective value is at least 1 ms. Internal `HttpClient.Timeout` is disabled to avoid competing timeouts.

When calling Native/Triple `SendQuery(invocation, cancellationToken)` directly, caller cancellation follows the existing convention of returning a failure result with `QuerySuccess == false`, rather than putting the Task into the Canceled state. Do not rely on `Task.WhenAll` to propagate cancellation automatically; the caller must check results/tokens and decide whether to stop the batch. Triple's `ErrorMeaasge` distinguishes `canceled by caller` from `timed out after N ms`; caller cancellation takes precedence when both cancellation and the deadline fire. Invalid arguments and calls to a disposed actuator can still throw directly.

## Recommendations

1. **Reuse `DubboClient`.** Long-lived instances make full use of shared ZooKeeper/Nacos clients, Provider TCP connections, service drivers, and metadata caches.
2. **Use `QueryAsync` consistently.** Let the client infer signatures for ordinary calls; use explicit Java type overloads when types are ambiguous.
3. **Specify types for critical interfaces.** Explicit Java types are more predictable than runtime inference for overloads, wrappers, `null`, collections, and POJO.
4. **Prefer a metadata center.** Telnet is an optional fallback and should not be the only metadata source for production calls. Do not expose diagnostic ports to untrusted networks.
5. **Ensure Provider URLs are complete.** Locating metadata requires `application`; service routing may also require `version` and `group`.
6. **Distinguish invocation success from business success.** Check `QuerySuccess` first, then business fields such as `code` and `success`.
7. **Retry non-idempotent requests cautiously.** A timeout or disconnection does not prove that the Provider did not execute the request. Use business idempotency keys and an appropriate retry policy.
8. **Size caches for your service count.** Raise `MaintainServiceNum` or set it to `0` when calling many interfaces, while monitoring connections and memory.
9. **Choose load balancing for the workload.** Default `Random` suits general use. `LeastActive`, P2C, and `ShortestResponse` use local consumer statistics without requiring Telnet diagnostic ports. `ConsistentHash` suits request affinity; account for mapping changes during upgrades.
10. **Test compatibility against real Providers before deployment.** This is particularly important for Dubbo versions not marked as verified in the compatibility matrix, complex POJO, and custom Hessian types.

## Troubleshooting

### Service or Provider Not Found

Check:

- Whether `DubboRootPath` matches the registry.
- Whether ZooKeeper's `/dubbo/{fully-qualified-interface}/providers` has valid child nodes, or the corresponding Nacos Naming group contains a `providers:{interface}:...` service.
- Whether Provider URLs start with `dubbo://` or `tri://`.
- Whether the Provider host is an IP address rather than a domain name.
- Whether the interface name, service `version`, and `group` are correct.

Service discovery failures generally return a `DubboRequestResult` with `QuerySuccess=false`; details are in `ErrorMeaasge`.

### `DubboMetadataException`: Method Signature Unavailable

The exception reports metadata center and Telnet failures separately. Check, in order:

1. Provider URL fields `release`, `application`, `version`, and `group`.
2. Whether `MetadataCenterAddress` and `MetadataRootPath` point to the actual metadata center.
3. Whether the expected FullServiceDefinition location exists. The ZooKeeper path is:

   ```text
   {root}/metadata/{interface}/{version?}/{group?}/provider/{application}
   ```

   For Nacos, check the `{MetadataRootPath}` Config group and DataId:

   ```text
   {interface}:{version}:{group}:provider:{application}
   ```

4. Whether the Provider has configured metadata-report and successfully published metadata. The presence of consumer nodes does not mean Provider method definitions exist.
5. Whether Telnet `ls -l` is exposed on the Provider's registered `dubbo://` address and port.

If the metadata sources cannot be fixed, follow the exception's suggestion to use an explicit-type overload:

```csharp
await client.QueryAsync(
    "com.foo.JobService#getJob",
    new[] { "java.lang.Integer" },
    new object[] { 1234 });
```

### Ambiguous Method Overloads

Inspect all candidate signatures:

```csharp
var methods = await client.GetMethodMetadataAsync(
    "com.foo.JobService",
    "getJob");
```

Then use explicit Java types. Do not merely change `1234` to `1234L` to distinguish primitives from wrappers; `long` and `java.lang.Long` still require an exact signature.

### Unsupported Serialization ID

The native actuator only supports Hessian2. Ensure the Provider accepts Hessian2 and the response frame's serialization ID is `2`. The client does not currently switch automatically to Fastjson2, Kryo, FST, Protobuf, or other encoders based on the Provider URL.

### Failed `tri://` Calls

Ensure the Provider uses Dubbo 3.3+ and exposes Java Interface HTTP/JSON access. The first-phase actuator sends
an HTTP/1.1 JSON POST to `http://{host}:{port}/{service}/{method}`. The body must be an argument array,
including `[value]` for a single argument. With multiple versions/groups, ensure both fields in the registered URL are accurate.

The default Triple HTTP/JSON mapping in Dubbo 3.3.6 cannot reliably distinguish all Java overloads.
When invoking through `DubboClient`, detected overloaded methods are restricted to native `dubbo://` nodes before load balancing;
if only `tri://` nodes exist, an explicit error is returned. To expose overloads through Triple,
declare a different REST route for each overload on the Provider.

The first phase does not support Protobuf/IDL, streaming, or gRPC framing; such services cannot be invoked through the current
`TripleDubboActuatorSuite`.

### Request Timeout or Disconnection

- Adjust `DubboRequestTimeout`.
- Check `ErrorMeaasge` and Provider logs.
- Native connections are re-established for later requests, but failed business requests are not replayed automatically.
- Do not blindly retry non-idempotent methods if you cannot determine whether the Provider executed them.

### Business Failure with `QuerySuccess=true`

This is expected. `QuerySuccess` only indicates invocation-layer success; parse the business status in `Result` or `ResultModle` separately.

## Build and Test

The .NET 10 SDK is required. All projects use `net10.0`; the .NET 6 Runtime is no longer needed:

```bash
dotnet restore DubboNet.sln
dotnet build DubboNet.sln --no-restore
dotnet test Tests/UnitTestForDubboNet/UnitTestForDubboNet.csproj --no-restore
```

`DubboClientIntegrationTests` and `QueryDemo` default to local addresses only: `localhost:2181` for ZooKeeper, `nacos://localhost:8848` for Nacos, and `localhost` for direct Provider access. Remote hosts must be supplied at runtime through command-line arguments or environment variables; no public test address is built in.

Use `--host` to set both the registry and direct Provider host:

```bash
dotnet run \
  --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -- --host localhost
```

For Nacos, use `--nacos` or the compatible `nacos` shorthand. If the registry and Provider are on different hosts, specify them separately:

```bash
dotnet run \
  --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -- --nacos --host localhost

dotnet run \
  --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -- --registry nacos://localhost:8848 --provider-host localhost
```

Environment variables `DUBBONET_TEST_HOST`, `DUBBONET_TEST_REGISTRY`, and `DUBBONET_TEST_PROVIDER_HOST` are also supported. Command-line `--host` overrides environment addresses; `--registry` / `--provider-host` can set the two endpoints independently. Without a separate Provider host, the explicitly supplied host or the registry's first host is used. The old first positional argument, such as `localhost:2181` or `nacos://localhost:8848`, remains supported.

`--nacos` only selects the protocol and default port; it does not overwrite `DUBBONET_TEST_REGISTRY`. A Nacos environment address is used as-is; a ZooKeeper address produces an explicit configuration conflict. To override environment addresses, use `--host` or an explicit `--registry`, avoiding silent requests to another environment.

`--print-config` only prints the effective configuration, with no DNS resolution, registry connection, or RPC. `--help` shows argument usage. Ordinary unit tests do not use these network integration arguments or depend on public services.

The ZooKeeper test matrix covers Dubbo 2.7.23, 3.1.11, 3.2.20, and 3.3.6; the Nacos matrix covers 3.2.20 and 3.3.6. Both exercise metadata caching,
automatic signature inference, explicit Java types, typed responses, `DubboInvocation`, Provider exceptions,
and explicit direct calls over Dubbo TCP/Hessian2 and Triple HTTP/JSON.
Tests require the prepared `com.example.dubbonet` services. When supplying another registry address, ensure the same groups, root paths, and test services exist. The program reports failures with a non-zero exit code for manual acceptance testing or CI.

`QueryDemo` uses sequential examples to demonstrate every `DubboClient.QueryAsync` overload, plus direct invocation
without a registry when the Provider IP, port, and protocol are known:

```bash
dotnet run --project Tests/QueryDemo/QueryDemo.csproj -- --host localhost
```

Personal VS Code shortcut tasks and root-level Compose debugging files are no longer provided. Command-line builds, tests, and runs are unaffected; the standalone container build entry point remains:

```bash
docker build -f Tests/DubboClientIntegrationTests/Dockerfile -t dubbonet-integration .
docker run --rm dubbonet-integration --host localhost --print-config
```

Inside a container, `localhost` means the container itself. To connect to a host machine or external Provider, explicitly supply a hostname or IP reachable from the container.

### Publishing

The pre-ASF automated publishing job inherited from the original repository has
been removed. Release publishing for the Apache project must be set up and
reviewed separately under ASF release policy (PMC-managed release process,
signed artifacts, ASF-owned publishing identity). No package is published from
this repository today.

This repository currently contains no automated publishing workflows. The Apache release process requires separate design and review;
the original personal project's nuget.org / GitHub Packages publishing identities must not be reused.

## Contributing

Read the [contribution guide](CONTRIBUTING.md) before opening a pull request. When updating documentation, keep the English and Chinese READMEs in sync, or explicitly identify any temporarily untranslated sections in the pull request.

## License

This project is licensed under the [Apache License 2.0](LICENSE).

See [NOTICE](NOTICE) for attribution information.
