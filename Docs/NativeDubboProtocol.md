# Dubbo2 原生 TCP 协议实现说明

本项目的 `NativeDubboActuatorSuite` 使用 Dubbo2 TCP 帧、Hessian2 和 `$invoke` 泛化调用，不依赖 Provider 的 Telnet `invoke` 命令。

## 版本字段

ZooKeeper Provider URL 中几个容易混淆的字段含义不同：

- `release=2.7.3`：Provider 使用的 Dubbo 框架发行版本，不用于选择线协议。
- `dubbo=2.0.2`：Dubbo 经典协议版本。原生请求体的首字段也写入 `2.0.2`。
- `version=...`：服务版本，会写入请求体的 service version 和 attachments。
- `group=...`：服务分组，会写入 attachments。
- URL scheme `dubbo://`：选择原生 Dubbo2 TCP 执行器。
- URL scheme `tri://`：当前选择 `HttpDubboActuatorSuite`，以 HTTP POST 发送 JSON；这只是 HTTP JSON 适配器，不是标准 Dubbo Triple/gRPC 客户端。

因此执行器选择依据是 Provider URL 的 scheme，而不是 `release` 的大小。

## 16 字节帧头

| 偏移 | 长度 | 含义 |
| --- | ---: | --- |
| 0 | 2 | Magic，固定为 `0xda 0xbb` |
| 2 | 1 | Flag：bit 7=request、bit 6=two-way、bit 5=event、低 5 位=serialization id |
| 3 | 1 | Response status；请求帧为 `0`，成功响应为 `20` |
| 4 | 8 | Request ID，大端序 `int64` |
| 12 | 4 | Body 长度，大端序 `int32` |

普通 Hessian2 双向请求的 flag 是 `0xc2`。普通 Hessian2 响应的低 5 位为 `2`。

## 泛化请求体

Body 是连续的 Hessian2 值，不是一个 JSON 对象。当前实现依次编码：

1. Dubbo protocol version：`"2.0.2"`
2. Service path：完整接口名
3. Service version：没有版本时为 `null`
4. Method：`"$invoke"`
5. Parameter descriptor：`"Ljava/lang/String;[Ljava/lang/String;[Ljava/lang/Object;"`
6. 实际方法名
7. Java 参数类型名数组，例如 `["java.lang.String", "java.lang.Long"]`
8. 参数值数组
9. Attachments Map

默认 attachments 包含：

- `path`
- `interface`
- `dubbo=2.0.2`
- `generic=true`
- `timeout`
- 可选的 `version`、`group` 和调用方自定义字段

复杂 POJO 参数以泛化 Map 发送，并包含 `class=<Java FQCN>`。例如参数类型为 `com.foo.User` 时，匿名对象 `{ name = "demo" }` 会转换为：

```json
{
  "class": "com.foo.User",
  "name": "demo"
}
```

## 响应体

当 status 不是 `20` 时，Body 是 Hessian2 字符串，内容通常是 Dubbo/RpcException 错误详情。

当 status 为 `20` 时，第一个 Hessian2 int 是结果类型：

| 值 | 结构 |
| ---: | --- |
| 0 | Exception |
| 1 | Value |
| 2 | Null value |
| 3 | Exception + attachments |
| 4 | Value + attachments |
| 5 | Null value + attachments |

解码后的值会递归转换成 .NET 标量、`List<object>` 和 `Dictionary<string, object>`；同时保留 POJO 的 `class`、嵌套集合、二进制值和响应 attachments。循环引用会表示为 `"[Circular reference]"`，避免 JSON 序列化递归溢出。

`DubboRequestResult` 可取得：

- `Result`：JSON 字符串
- `RawResult`：JSON 化之前的动态 .NET 值
- `RequestId`
- `ResponseStatus`
- `SerializationId`
- `ResponseAttachments`
- `RequestElapsed`

经典 Dubbo 响应默认不包含 Provider 方法执行耗时，因此原生结果的 `ServiceElapsedAvailable` 为 `false`。

## 当前边界

- 只实现 Hessian2（serialization id `2`）。
- 使用 `$invoke` 泛化调用，不要求 .NET 持有 Java 接口 jar。
- `DubboClient.QueryGenericAsync(endpoint, arguments)` 会读取并缓存精确签名：Provider `release >= 2.7.3` 时优先使用元数据中心，失败后回退到 Telnet `ls -l`；已知低版本直接使用 Telnet；无法识别版本时先尝试元数据中心。
- 元数据中心地址默认复用注册中心，也可以通过 `MetadataCenterAddress` 和 `MetadataRootPath` 单独配置。
- Telnet 元数据和“不支持 Telnet”的失败结论都按服务缓存，一个 `DubboClient` 生命周期内只探测一次；若后续创建元数据节点，ZooKeeper watch 会切换回优先级更高的元数据中心结果。
- 注册中心或元数据中心的瞬时连接错误不会被永久缓存；后续调用会重试高优先级来源，但会复用已有的 Telnet 正/负缓存，不会重复连接。元数据中心只返回部分方法/重载时，解析失败的目标方法会再用 Telnet 结果补全一次。
- Telnet 扩展若输出带泛型参数的源码式类型名，写入 `$invoke` 的 `parameterTypes` 前会按 Java 类型擦除为可加载的原始类名。
- Provider 两种来源都无法提供元数据时，零参数方法仍可直接调用；有参数的方法会抛出包含两路失败原因的 `DubboMetadataException`，并提示使用显式签名重载，避免把 `int`、`Integer`、`long`、`Long` 猜错。
- 单个 TCP 连接通过 Request ID 复用并发请求；写入串行化，响应可以乱序返回。
- Body 默认限制为 8 MiB。
- 会应答 Provider 发来的双向 event/heartbeat 帧。
- 连接写入失败后不会自动重发业务请求，以避免不确定状态下的重复调用；后续请求会重新建连。

Telnet 实现仍不承载原生业务调用；它只在上述规则要求时，以一次性的 `ls -l` 连接作为方法签名兜底来源，也继续支持 `ls`、`status`、`trace` 等显式诊断。
