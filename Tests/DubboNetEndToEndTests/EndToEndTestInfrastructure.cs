/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements. See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License. You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using DubboNet.Clients;
using DubboNet.DubboService;
using System.Collections.Concurrent;
using Xunit;

namespace DubboNetEndToEndTests;

/// <summary>
/// 仅当显式启用端到端测试时才运行，避免普通单元测试依赖外部注册中心。
/// EN: Runs only when end-to-end tests are explicitly enabled, keeping unit tests self-contained.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EndToEndTheoryAttribute : TheoryAttribute
{
    public EndToEndTheoryAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DUBBONET_E2E_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set DUBBONET_E2E_ENABLED=true and provide the temporary registry addresses.";
        }
    }
}

/// <summary>
/// 端到端测试集合；禁用并行以便复用客户端并验证有状态方法。
/// EN: End-to-end collection; parallelism is disabled for client reuse and stateful assertions.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EndToEndTestCollection : ICollectionFixture<EndToEndClientPool>
{
    public const string Name = "DubboNet end-to-end provider methods";
}

/// <summary>
/// 按注册中心地址和 Dubbo 根路径复用 DubboClient。
/// EN: Reuses DubboClient instances by registry address and Dubbo root path.
/// </summary>
public sealed class EndToEndClientPool : IDisposable
{
    private readonly ConcurrentDictionary<string, DubboClient> _clients = new();

    public DubboClient GetClient(string registryKind, string rootPath)
    {
        string registryAddress = EndToEndTestCatalog.GetRegistryAddress(registryKind);
        string key = registryAddress + "|" + rootPath;
        return _clients.GetOrAdd(key, _ => new DubboClient(
            registryAddress,
            new DubboClient.DubboClientConf
            {
                DubboRootPath = rootPath,
                MetadataRootPath = rootPath,
                DubboRequestTimeout = 10_000,
                TelnetMetadataTimeout = 5_000,
                MaintainServiceNum = 10,
                NowLoadBalanceMode = DubboClient.LoadBalanceMode.RoundRobin
            }));
    }

    public void Dispose()
    {
        foreach (DubboClient client in _clients.Values)
        {
            client.Dispose();
        }
        _clients.Clear();
    }
}

internal sealed record ProviderVersionScenario(
    string RegistryKind,
    string Version,
    string RootPath);

internal sealed record ProviderMethodCase(
    string Id,
    string Service,
    string Method,
    string[] ParameterTypes)
{
    public string Signature => Method + "(" + string.Join(",", ParameterTypes) + ")";
}

/// <summary>
/// 端到端环境的版本矩阵与完整方法目录。
/// EN: Version matrix and complete method catalog exposed by the end-to-end environment.
/// </summary>
internal static class EndToEndTestCatalog
{
    public const string ZooKeeper = "ZooKeeper";
    public const string Nacos = "Nacos";

    public const string DataTypeService = "com.example.dubbonet.api.DataTypeService";
    public const string EchoService = "com.example.dubbonet.api.EchoService";
    public const string OrderService = "com.example.dubbonet.api.OrderService";
    public const string OverloadService = "com.example.dubbonet.api.OverloadService";
    public const string UserService = "com.example.dubbonet.api.UserService";

    public static readonly IReadOnlyList<ProviderVersionScenario> Versions = new[]
    {
        new ProviderVersionScenario(ZooKeeper, "2.7.23", "/dubbo-2.7.23"),
        new ProviderVersionScenario(ZooKeeper, "3.1.11", "/dubbo-3.1.11"),
        new ProviderVersionScenario(ZooKeeper, "3.2.20", "/dubbo-3.2.20"),
        new ProviderVersionScenario(ZooKeeper, "3.3.6", "/dubbo-3.3.6"),
        new ProviderVersionScenario(Nacos, "3.2.20", "/dubbo-3.2.20"),
        new ProviderVersionScenario(Nacos, "3.3.6", "/dubbo-3.3.6")
    };

    public static readonly IReadOnlyList<ProviderMethodCase> Methods = new[]
    {
        Method(DataTypeService, "add", "int", "int"),
        Method(DataTypeService, "average", "double[]"),
        Method(DataTypeService, "negate", "boolean"),
        Method(DataTypeService, "serverTime"),
        Method(DataTypeService, "join", "java.lang.String", "java.lang.String[]"),

        Method(EchoService, "ping"),
        Method(EchoService, "echo", "java.lang.String"),
        Method(EchoService, "echoBytes", "byte[]"),
        Method(EchoService, "echoHeaders", "java.util.Map"),
        Method(EchoService, "alwaysFail", "java.lang.String"),

        Method(OrderService, "getOrder", "java.lang.String"),
        Method(OrderService, "listOrders", "long"),
        Method(OrderService, "searchOrders", "com.example.dubbonet.model.OrderQuery"),
        Method(OrderService, "calculateTotal", "java.util.List"),
        Method(OrderService, "updateStatus", "java.lang.String", "com.example.dubbonet.model.OrderStatus"),

        Method(OverloadService, "overload"),
        Method(OverloadService, "overload", "java.lang.String"),
        Method(OverloadService, "overload", "int"),
        Method(OverloadService, "overload", "java.lang.Integer"),
        Method(OverloadService, "overload", "java.lang.String", "int"),

        Method(UserService, "getUser", "long"),
        Method(UserService, "listUsers", "java.util.List"),
        Method(UserService, "createUser", "com.example.dubbonet.model.CreateUserRequest"),
        Method(UserService, "batchGet", "long[]"),
        Method(UserService, "updateProfile", "long", "java.util.Map")
    };

    public static IEnumerable<object[]> InvocationCases()
    {
        foreach (ProviderVersionScenario version in Versions)
        {
            foreach (ProviderMethodCase method in Methods)
            {
                yield return new object[]
                {
                    version.RegistryKind,
                    version.Version,
                    version.RootPath,
                    method.Id
                };
            }
        }
    }

    public static IEnumerable<object[]> MetadataCoverageCases()
    {
        foreach (ProviderVersionScenario version in Versions)
        {
            yield return new object[]
            {
                version.RegistryKind,
                version.Version,
                version.RootPath
            };
        }
    }

    public static ProviderMethodCase GetMethod(string caseId)
    {
        return Methods.Single(method => method.Id == caseId);
    }

    public static string GetRegistryAddress(string registryKind)
    {
        string variable = registryKind switch
        {
            ZooKeeper => "DUBBONET_E2E_ZOOKEEPER_ADDRESS",
            Nacos => "DUBBONET_E2E_NACOS_ADDRESS",
            _ => throw new ArgumentOutOfRangeException(nameof(registryKind), registryKind, null)
        };
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable {variable} is required.");
        }
        return value.Trim();
    }

    private static ProviderMethodCase Method(
        string service,
        string method,
        params string[] parameterTypes)
    {
        string shortService = service[(service.LastIndexOf('.') + 1)..];
        string id = shortService + "." + method + "(" + string.Join(",", parameterTypes) + ")";
        return new ProviderMethodCase(id, service, method, parameterTypes);
    }
}
