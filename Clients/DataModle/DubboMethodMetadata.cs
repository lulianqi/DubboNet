using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// Dubbo 方法签名的元数据来源。
    /// EN: Source from which a Dubbo method signature was discovered.
    /// </summary>
    public enum DubboMetadataSource
    {
        /// <summary>
        /// 来自 Dubbo 元数据中心的 FullServiceDefinition。
        /// EN: Loaded from a FullServiceDefinition in the Dubbo metadata center.
        /// </summary>
        MetadataCenter,
        /// <summary>
        /// 来自服务端 Telnet <c>ls -l</c> 命令。
        /// EN: Loaded from the provider's Telnet <c>ls -l</c> command.
        /// </summary>
        Telnet
    }

    /// <summary>
    /// 单个 Java 方法签名的 Dubbo 元数据；重载方法会对应多个同名实例。
    /// EN: Dubbo metadata for one Java method signature; overloaded methods produce multiple entries with the same name.
    /// </summary>
    public sealed class DubboMethodMetadata
    {
        /// <summary>
        /// 完整 Java 服务接口名。
        /// EN: Fully qualified Java service interface name.
        /// </summary>
        public string ServiceName { get; internal set; }

        /// <summary>
        /// Java 方法名。
        /// EN: Java method name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// 按声明顺序排列的精确 Java 参数类型名。
        /// EN: Exact Java parameter type names in declaration order.
        /// </summary>
        [JsonPropertyName("parameterTypes")]
        public string[] ParameterTypes { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 精确 Java 返回类型名。
        /// EN: Exact Java return type name.
        /// </summary>
        [JsonPropertyName("returnType")]
        public string ReturnType { get; set; }

        /// <summary>
        /// 服务版本；用于定位元数据文档，可能为空。
        /// EN: Service version used to locate the metadata document; may be null.
        /// </summary>
        public string Version { get; internal set; }

        /// <summary>
        /// 服务分组；用于定位元数据文档，可能为空。
        /// EN: Service group used to locate the metadata document; may be null.
        /// </summary>
        public string Group { get; internal set; }

        /// <summary>
        /// 发布该元数据的 Provider 应用名，可能为空。
        /// EN: Provider application that published the metadata; may be null.
        /// </summary>
        public string Application { get; internal set; }

        /// <summary>
        /// 来源位置：ZooKeeper 元数据节点或 <c>telnet://</c> Provider 端点。
        /// EN: Source location, either a ZooKeeper metadata node or a <c>telnet://</c> provider endpoint.
        /// </summary>
        public string MetadataPath { get; internal set; }

        /// <summary>
        /// Dubbo 上报的 Provider 代码来源；Telnet 来源通常为空。
        /// EN: Provider code source reported by Dubbo; usually null for Telnet metadata.
        /// </summary>
        public string CodeSource { get; internal set; }

        /// <summary>
        /// 当前方法签名的元数据来源。
        /// EN: Metadata source from which this method signature was loaded.
        /// </summary>
        public DubboMetadataSource MetadataSource { get; internal set; } =
            DubboMetadataSource.MetadataCenter;

        /// <summary>
        /// Dubbo 上报的服务级参数；Telnet 来源仅包含可从 Provider URL 获取的参数。
        /// EN: Service-level parameters reported by Dubbo; Telnet metadata contains only values available from the provider URL.
        /// </summary>
        public IReadOnlyDictionary<string, string> ServiceParameters { get; internal set; } =
            new Dictionary<string, string>();

        /// <summary>
        /// 同一元数据文档中的类型定义，描述 POJO 属性、集合元素、枚举和引用；Telnet 来源为空集合。
        /// EN: Type definitions from the same metadata document, describing POJO properties, collection items, enums, and references; empty for Telnet metadata.
        /// </summary>
        public IReadOnlyList<DubboTypeMetadata> TypeDefinitions { get; internal set; } =
            Array.Empty<DubboTypeMetadata>();

        /// <summary>
        /// 元数据文档的 ZooKeeper 数据版本；Telnet 来源为 <c>-1</c>。
        /// EN: ZooKeeper data version of the metadata document; <c>-1</c> for Telnet metadata.
        /// </summary>
        public int MetadataVersion { get; internal set; }

        /// <summary>
        /// DubboClient 读取该元数据的 UTC 时间。
        /// EN: UTC time at which DubboClient loaded this metadata.
        /// </summary>
        public DateTimeOffset LoadedAt { get; internal set; }

        /// <summary>
        /// 返回便于阅读的 Java 方法签名。
        /// EN: Returns a human-readable Java method signature.
        /// </summary>
        /// <returns>包含返回类型、服务名、方法名和参数类型的签名。EN: Signature containing the return type, service, method, and parameter types.</returns>
        public override string ToString()
        {
            return $"{ReturnType} {ServiceName}.{Name}({string.Join(", ", ParameterTypes)})";
        }

        internal DubboMethodMetadata CloneForCaller()
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>(
                ServiceParameters ?? new Dictionary<string, string>(),
                StringComparer.Ordinal);
            return new DubboMethodMetadata
            {
                ServiceName = ServiceName,
                Name = Name,
                ParameterTypes = ParameterTypes?.ToArray() ?? Array.Empty<string>(),
                ReturnType = ReturnType,
                Version = Version,
                Group = Group,
                Application = Application,
                MetadataPath = MetadataPath,
                CodeSource = CodeSource,
                MetadataSource = MetadataSource,
                ServiceParameters = new ReadOnlyDictionary<string, string>(parameters),
                TypeDefinitions = (TypeDefinitions ?? Array.Empty<DubboTypeMetadata>())
                    .Select(type => type.CloneForCaller())
                    .ToArray(),
                MetadataVersion = MetadataVersion,
                LoadedAt = LoadedAt
            };
        }
    }

    /// <summary>
    /// <c>FullServiceDefinition.types</c> 中的递归 Dubbo 类型定义。
    /// EN: Recursive Dubbo type definition from <c>FullServiceDefinition.types</c>.
    /// </summary>
    public sealed class DubboTypeMetadata
    {
        /// <summary>
        /// 类型定义标识符，可能为空。
        /// EN: Type-definition identifier; may be null.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// Java 类型名或 Dubbo 类型类别。
        /// EN: Java type name or Dubbo type category.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>
        /// 数组或集合的元素类型定义。
        /// EN: Item type definitions for arrays or collections.
        /// </summary>
        [JsonPropertyName("items")]
        public List<DubboTypeMetadata> Items { get; set; } = new List<DubboTypeMetadata>();

        /// <summary>
        /// 枚举类型的可选常量值。
        /// EN: Declared constants of an enum type.
        /// </summary>
        [JsonPropertyName("enum")]
        public List<string> EnumValues { get; set; } = new List<string>();

        /// <summary>
        /// 兼容 Dubbo 3.2+ 使用的复数字段名。
        /// EN: Alias for the plural field name used by Dubbo 3.2+.
        /// </summary>
        [JsonPropertyName("enums")]
        public List<string> Enums
        {
            get => EnumValues;
            set => EnumValues = value ?? new List<string>();
        }

        /// <summary>
        /// 指向另一类型定义的引用。
        /// EN: Reference to another type definition.
        /// </summary>
        [JsonPropertyName("$ref")]
        public string Reference { get; set; }

        /// <summary>
        /// POJO 属性名到属性类型定义的映射。
        /// EN: Mapping from POJO property names to their type definitions.
        /// </summary>
        [JsonPropertyName("properties")]
        public Dictionary<string, DubboTypeMetadata> Properties { get; set; } =
            new Dictionary<string, DubboTypeMetadata>();

        /// <summary>
        /// Dubbo 生成该定义时使用的类型构建器名称。
        /// EN: Name of the Dubbo type builder that produced this definition.
        /// </summary>
        [JsonPropertyName("typeBuilderName")]
        public string TypeBuilderName { get; set; }

        internal DubboTypeMetadata CloneForCaller()
        {
            return new DubboTypeMetadata
            {
                Id = Id,
                Type = Type,
                Items = (Items ?? new List<DubboTypeMetadata>())
                    .Select(item => item.CloneForCaller())
                    .ToList(),
                EnumValues = EnumValues?.ToList() ?? new List<string>(),
                Reference = Reference,
                Properties = (Properties ?? new Dictionary<string, DubboTypeMetadata>())
                    .ToDictionary(
                        item => item.Key,
                        item => item.Value?.CloneForCaller(),
                        StringComparer.Ordinal),
                TypeBuilderName = TypeBuilderName
            };
        }
    }
}
