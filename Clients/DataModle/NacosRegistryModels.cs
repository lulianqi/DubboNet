using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// Nacos Naming 目录中的服务摘要；该类型不依赖 Nacos SDK，可由应用直接用于构建服务目录。
    /// <para>EN: Service summary from the Nacos Naming catalog. This SDK-independent type can be used directly by applications to build a service tree.</para>
    /// </summary>
    public sealed class NacosServiceInfo
    {
        /// <summary>服务名称。<para>EN: Service name.</para></summary>
        public string Name { get; init; }

        /// <summary>服务所属 Naming 分组。<para>EN: Naming group that owns the service.</para></summary>
        public string GroupName { get; init; }

        /// <summary>集群数量；服务端未返回时为 <c>0</c>。<para>EN: Cluster count, or <c>0</c> when omitted by the server.</para></summary>
        public int ClusterCount { get; init; }

        /// <summary>实例数量；服务端未返回时为 <c>0</c>。<para>EN: Instance count, or <c>0</c> when omitted by the server.</para></summary>
        public int InstanceCount { get; init; }

        /// <summary>健康实例数量；服务端未返回时为 <c>0</c>。<para>EN: Healthy instance count, or <c>0</c> when omitted by the server.</para></summary>
        public int HealthyInstanceCount { get; init; }
    }

    /// <summary>
    /// Nacos Naming 服务实例的只读快照；公开字段由 DubboNet 自有类型表示，不会向调用方泄露 Nacos SDK 类型。
    /// <para>EN: Read-only snapshot of a Nacos Naming instance, represented entirely by DubboNet-owned types so Nacos SDK types do not leak to callers.</para>
    /// </summary>
    public sealed class NacosServiceInstanceInfo
    {
        /// <summary>Nacos 实例标识；服务端未提供时为空。<para>EN: Nacos instance identifier, or null when not supplied.</para></summary>
        public string InstanceId { get; init; }

        /// <summary>实例所属的原始 Nacos 服务名。<para>EN: Raw Nacos service name that owns this instance.</para></summary>
        public string ServiceName { get; init; }

        /// <summary>实例所属 Naming 分组。<para>EN: Naming group that owns this instance.</para></summary>
        public string GroupName { get; init; }

        /// <summary>实例 IP 地址。<para>EN: Instance IP address.</para></summary>
        public string Ip { get; init; }

        /// <summary>实例端口。<para>EN: Instance port.</para></summary>
        public int Port { get; init; }

        /// <summary>Nacos 集群名称。<para>EN: Nacos cluster name.</para></summary>
        public string ClusterName { get; init; }

        /// <summary>Nacos 原始权重。<para>EN: Raw Nacos weight.</para></summary>
        public double Weight { get; init; }

        /// <summary>实例是否健康。<para>EN: Whether the instance is healthy.</para></summary>
        public bool Healthy { get; init; }

        /// <summary>实例是否启用。<para>EN: Whether the instance is enabled.</para></summary>
        public bool Enabled { get; init; }

        /// <summary>实例是否为临时实例。<para>EN: Whether the instance is ephemeral.</para></summary>
        public bool Ephemeral { get; init; }

        /// <summary>实例元数据的独立只读副本。<para>EN: Independent read-only copy of the instance metadata.</para></summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
