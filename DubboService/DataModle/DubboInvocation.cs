using System;
using System.Collections.Generic;

namespace DubboNet.DubboService.DataModle
{
    /// <summary>
    /// 描述一次原生 Dubbo 泛化调用；ParameterTypes 使用 Java 源码类型名，例如
    /// "java.lang.String"、"int" 或 "com.foo.User"。
    /// EN: Describes one native Dubbo generic invocation. ParameterTypes contains Java
    /// source type names such as "java.lang.String", "int", or "com.foo.User".
    /// </summary>
    public sealed class DubboInvocation
    {
        /// <summary>
        /// 获取或设置 Java 服务接口的全限定名。
        /// EN: Gets or sets the fully qualified Java service interface name.
        /// </summary>
        public string Service { get; set; }

        /// <summary>
        /// 获取或设置要调用的方法名。
        /// EN: Gets or sets the method name to invoke.
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// 获取或设置与参数顺序一致的精确 Java 类型名。
        /// EN: Gets or sets the exact Java type names in argument order.
        /// </summary>
        public IReadOnlyList<string> ParameterTypes { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 获取或设置调用参数。
        /// EN: Gets or sets the invocation arguments.
        /// </summary>
        public IReadOnlyList<object> Arguments { get; set; } = Array.Empty<object>();

        /// <summary>
        /// 获取或设置服务版本；为空时使用注册中心提供的版本。
        /// EN: Gets or sets the service version; when null, the registry-provided version is used.
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// 获取或设置服务分组；为空时使用注册中心提供的分组。
        /// EN: Gets or sets the service group; when null, the registry-provided group is used.
        /// </summary>
        public string Group { get; set; }

        /// <summary>
        /// 获取或设置附加到 Dubbo 请求的自定义字符串参数。
        /// EN: Gets or sets custom string attachments added to the Dubbo request.
        /// </summary>
        public IReadOnlyDictionary<string, string> Attachments { get; set; }

        /// <summary>
        /// 初始化一个空的 Dubbo 泛化调用描述。
        /// EN: Initializes an empty Dubbo generic invocation descriptor.
        /// </summary>
        public DubboInvocation()
        {
        }

        /// <summary>
        /// 使用服务、方法、精确 Java 参数类型和参数值初始化调用描述。
        /// EN: Initializes an invocation with its service, method, exact Java parameter types, and argument values.
        /// </summary>
        /// <param name="service">Java 服务接口全限定名。EN: The fully qualified Java service interface name.</param>
        /// <param name="method">方法名。EN: The method name.</param>
        /// <param name="parameterTypes">精确 Java 参数类型。EN: The exact Java parameter types.</param>
        /// <param name="arguments">参数值。EN: The argument values.</param>
        public DubboInvocation(
            string service,
            string method,
            IReadOnlyList<string> parameterTypes,
            IReadOnlyList<object> arguments)
        {
            Service = service;
            Method = method;
            ParameterTypes = parameterTypes ?? Array.Empty<string>();
            Arguments = arguments ?? Array.Empty<object>();
        }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Service))
            {
                throw new ArgumentException("Dubbo service name cannot be empty.", nameof(Service));
            }
            if (string.IsNullOrWhiteSpace(Method))
            {
                throw new ArgumentException("Dubbo method name cannot be empty.", nameof(Method));
            }
            if (ParameterTypes == null)
            {
                throw new ArgumentNullException(nameof(ParameterTypes));
            }
            if (Arguments == null)
            {
                throw new ArgumentNullException(nameof(Arguments));
            }
            if (ParameterTypes.Count != Arguments.Count)
            {
                throw new ArgumentException(
                    $"Parameter type count ({ParameterTypes.Count}) does not match argument count ({Arguments.Count}).");
            }
        }
    }
}
