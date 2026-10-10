# Triple 第一期实现说明

`TripleDubboActuatorSuite` 面向 Dubbo 3.3+ 的 Java Interface Unary HTTP/JSON
调用。注册中心返回 `tri://` Provider URL 时，`DubboServiceDriver` 会选择该执行器，
不再把 Triple 节点交给普通 `HttpDubboActuatorSuite`。

## 请求格式

- HTTP 方法：`POST`
- 路径：`/{完整 Java 接口名}/{方法名}`
- Content-Type：`application/json`
- 请求正文：按方法参数顺序组成的 JSON 数组；无参数为 `[]`，单参数也必须保留数组，
  例如 `[false]`
- 第一期固定使用 HTTP/1.1；Dubbo 3.3 Triple 同一端口可以接受 HTTP/1、HTTP/2
  和 HTTP/3，但后两者不属于当前实现目标

示例：

```http
POST /com.example.DataTypeService/negate HTTP/1.1
Content-Type: application/json
tri-service-version: 1.0.0
tri-service-group: demo
tri-service-timeout: 10000

[false]
```

## 服务路由和附件

执行器优先使用 `DubboInvocation.Version` 和 `DubboInvocation.Group`；未显式填写时，
使用注册中心 Provider URL 中的 `version`、`group` 和 `timeout`。对应的 Triple 请求头为：

- `tri-service-version`
- `tri-service-group`
- `tri-service-timeout`

`DubboInvocation.Attachments` 会作为 HTTP 请求头发送。协议保留头、伪头、
`Host`、`Content-Length` 和 `Content-Type` 不能由附件覆盖。

响应为 2xx 时保留原始 JSON 正文；非 2xx 时 `QuerySuccess=false`，HTTP 状态和
Provider 返回的错误正文同时写入 `ErrorMeaasge`。响应头会保存在
`ResponseAttachments` 中。

## 第一期边界

当前支持：

- Dubbo 3.3+ Java Interface
- Unary
- HTTP/1.1
- `application/json`
- ZooKeeper 经典接口级 `tri://` Provider 发现

当前不支持：

- Protobuf/IDL 服务
- Client/Server/Bidirectional Streaming
- gRPC/Triple 二进制 framing
- TLS、HTTP/2、HTTP/3 的客户端协商
- Dubbo 3 应用级服务发现

## 设计依据

- [Apache Dubbo：Java Interface + Triple](https://dubbo.apache.org/en/overview/mannual/java-sdk/tasks/protocols/triple/interface/)
- [Apache Dubbo：Triple Protocol Specification](https://dubbo.apache.org/en/overview/reference/protocols/triple-spec/)
- [Apache Dubbo：Triple 3.3 New Features](https://dubbo.apache.org/en/overview/mannual/java-sdk/reference-manual/protocol/triple-3.3/)

实现同时对照 Apache Dubbo 3.3.6 源码中的 HTTP/JSON 解码器、
`TripleHeaderEnum` 和 group/version 路由条件。
