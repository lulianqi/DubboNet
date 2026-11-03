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
using DubboNet.Clients.DataModle;
using DubboNet.DubboService.DataModle;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace DubboNetEndToEndTests;

/// <summary>
/// 验证临时端到端环境中每个 Provider 的全部公开服务方法。
/// EN: Verifies every public service method exposed by each provider in the temporary environment.
/// </summary>
[Collection(EndToEndTestCollection.Name)]
public sealed class DubboProviderMethodsEndToEndTests
{
    private readonly EndToEndClientPool _clientPool;
    private readonly ITestOutputHelper _output;

    public DubboProviderMethodsEndToEndTests(
        EndToEndClientPool clientPool,
        ITestOutputHelper output)
    {
        _clientPool = clientPool;
        _output = output;
    }

    public static IEnumerable<object[]> InvocationCases() =>
        EndToEndTestCatalog.InvocationCases();

    public static IEnumerable<object[]> MetadataCoverageCases() =>
        EndToEndTestCatalog.MetadataCoverageCases();

    [EndToEndTheory]
    [MemberData(nameof(InvocationCases))]
    public async Task Every_provider_method_can_be_invoked(
        string registryKind,
        string version,
        string rootPath,
        string caseId)
    {
        DubboClient client = _clientPool.GetClient(registryKind, rootPath);
        ProviderMethodCase method = EndToEndTestCatalog.GetMethod(caseId);

        await InvokeAndVerifyAsync(client, method, registryKind, version);
    }

    [EndToEndTheory]
    [MemberData(nameof(MetadataCoverageCases))]
    public async Task Metadata_catalog_has_a_test_for_every_provider_method(
        string registryKind,
        string version,
        string rootPath)
    {
        DubboClient client = _clientPool.GetClient(registryKind, rootPath);

        foreach (IGrouping<string, ProviderMethodCase> serviceCases in
                 EndToEndTestCatalog.Methods.GroupBy(method => method.Service))
        {
            IReadOnlyList<DubboMethodMetadata> metadata =
                await client.GetServiceMethodsMetadataAsync(serviceCases.Key);
            string[] expected = serviceCases
                .Select(method => method.Signature)
                .OrderBy(signature => signature, StringComparer.Ordinal)
                .ToArray();
            string[] actual = metadata
                .Select(method => method.Name + "(" + string.Join(",",
                    method.ParameterTypes.Select(EraseGenericArguments)) + ")")
                .OrderBy(signature => signature, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
            _output.WriteLine(
                $"[PASS] {registryKind}/{version} {serviceCases.Key}: " +
                $"all {actual.Length} metadata methods have invocation tests.");
        }
    }

    private async Task InvokeAndVerifyAsync(
        DubboClient client,
        ProviderMethodCase method,
        string registryKind,
        string version)
    {
        switch (method.Id)
        {
            case "DataTypeService.add(int,int)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, 19, 23);
                    Assert.Equal(42, value.GetInt32());
                    break;
                }
            case "DataTypeService.average(double[])":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        new double[] { 1, 2, 3, 4 });
                    Assert.Equal(2.5D, value.GetDouble(), 6);
                    break;
                }
            case "DataTypeService.negate(boolean)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, false);
                    Assert.True(value.GetBoolean());
                    break;
                }
            case "DataTypeService.serverTime()":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method);
                    AssertValidJavaDate(value);
                    break;
                }
            case "DataTypeService.join(java.lang.String,java.lang.String[])":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        "|",
                        new[] { "A", "中文", "C" });
                    Assert.Equal("A|中文|C", value.GetString());
                    break;
                }
            case "EchoService.ping()":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method);
                    Assert.Equal("pong", value.GetString());
                    break;
                }
            case "EchoService.echo(java.lang.String)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, "hello-中文");
                    Assert.Equal("hello-中文", value.GetString());
                    break;
                }
            case "EchoService.echoBytes(byte[])":
                {
                    byte[] payload = { 0, 1, 2, 127, 128, 255 };
                    JsonElement value = await InvokeForJsonAsync(client, method, payload);
                    AssertJsonBytes(payload, value);
                    break;
                }
            case "EchoService.echoHeaders(java.util.Map)":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        new Dictionary<string, string>
                        {
                            ["traceId"] = $"e2e-{registryKind}-{version}",
                            ["language"] = "中文"
                        });
                    Assert.Equal($"e2e-{registryKind}-{version}", Property(value, "traceId").GetString());
                    Assert.Equal("中文", Property(value, "language").GetString());
                    break;
                }
            case "EchoService.alwaysFail(java.lang.String)":
                {
                    DubboRequestResult result = await InvokeAsync(client, method, "expected-e2e-error");
                    Assert.False(result.QuerySuccess);
                    string failure = (result.ErrorMeaasge ?? string.Empty) + " " + (result.Result ?? string.Empty);
                    Assert.True(
                        failure.Contains("expected-e2e-error", StringComparison.OrdinalIgnoreCase)
                    && failure.Contains("com.example.dubbonet.model.DemoServiceException", StringComparison.Ordinal),
                        "The provider exception did not include the expected error details: " + failure);
                    break;
                }
            case "OrderService.getOrder(java.lang.String)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, "ORDER-E2E-1001");
                    Assert.Equal("ORDER-E2E-1001", Property(value, "orderId").GetString());
                    Assert.Equal(358.80M, ReadDecimal(Property(value, "totalAmount")));
                    break;
                }
            case "OrderService.listOrders(long)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, 7001L);
                    Assert.Equal(JsonValueKind.Array, value.ValueKind);
                    Assert.Equal(3, value.GetArrayLength());
                    Assert.All(value.EnumerateArray(), order =>
                        Assert.Equal(7001L, Property(order, "userId").GetInt64()));
                    break;
                }
            case "OrderService.searchOrders(com.example.dubbonet.model.OrderQuery)":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        new Dictionary<string, object>
                        {
                            ["userId"] = 7002L,
                            ["pageNo"] = 1,
                            ["pageSize"] = 2
                        });
                    Assert.Equal(1, Property(value, "pageNo").GetInt32());
                    Assert.Equal(2, Property(value, "pageSize").GetInt32());
                    Assert.Equal(2, Property(value, "items").GetArrayLength());
                    break;
                }
            case "OrderService.calculateTotal(java.util.List)":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        new List<Dictionary<string, object>>
                        {
                        new()
                        {
                            ["sku"] = "SKU-E2E-1",
                            ["name"] = "first",
                            ["quantity"] = 2,
                            ["unitPrice"] = "10.25"
                        },
                        new()
                        {
                            ["sku"] = "SKU-E2E-2",
                            ["name"] = "second",
                            ["quantity"] = 1,
                            ["unitPrice"] = "4.50"
                        }
                        });
                    Assert.Equal(25.00M, ReadDecimal(value));
                    break;
                }
            case "OrderService.updateStatus(java.lang.String,com.example.dubbonet.model.OrderStatus)":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        "ORDER-E2E-STATUS",
                        "SHIPPED");
                    Assert.True(Property(value, "success").GetBoolean());
                    Assert.Equal("OK", Property(value, "code").GetString());
                    Assert.Contains("SHIPPED", Property(value, "data").GetRawText(), StringComparison.Ordinal);
                    break;
                }
            case "OverloadService.overload()":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method);
                    Assert.Equal("overload()", value.GetString());
                    break;
                }
            case "OverloadService.overload(java.lang.String)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, "text");
                    Assert.Equal("overload(String):text", value.GetString());
                    break;
                }
            case "OverloadService.overload(int)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, 7);
                    Assert.Equal("overload(int):7", value.GetString());
                    break;
                }
            case "OverloadService.overload(java.lang.Integer)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, 8);
                    Assert.Equal("overload(Integer):8", value.GetString());
                    break;
                }
            case "OverloadService.overload(java.lang.String,int)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, "repeat", 3);
                    Assert.Equal("overload(String,int):repeat|repeat|repeat", value.GetString());
                    break;
                }
            case "UserService.getUser(long)":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, 41L);
                    Assert.Equal(41L, Property(value, "id").GetInt64());
                    Assert.Equal("user-41", Property(value, "name").GetString());
                    Assert.True(Property(value, "active").GetBoolean());
                    break;
                }
            case "UserService.listUsers(java.util.List)":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        new List<long> { 41L, 42L, 43L });
                    Assert.Equal(JsonValueKind.Array, value.ValueKind);
                    Assert.Equal(3, value.GetArrayLength());
                    break;
                }
            case "UserService.createUser(com.example.dubbonet.model.CreateUserRequest)":
                {
                    JsonElement value = await InvokeForJsonAsync(
                        client,
                        method,
                        new Dictionary<string, object>
                        {
                            ["name"] = $"e2e-{registryKind}-{version}",
                            ["age"] = 28,
                            ["roles"] = new[] { "tester", "中文角色" }
                        });
                    Assert.True(Property(value, "success").GetBoolean());
                    Assert.Equal("OK", Property(value, "code").GetString());
                    Assert.Equal(
                        $"e2e-{registryKind}-{version}",
                        Property(Property(value, "data"), "name").GetString());
                    break;
                }
            case "UserService.batchGet(long[])":
                {
                    JsonElement value = await InvokeForJsonAsync(client, method, new long[] { 51, 52 });
                    Assert.Equal("user-51", Property(Property(value, "51"), "name").GetString());
                    Assert.Equal("user-52", Property(Property(value, "52"), "name").GetString());
                    break;
                }
            case "UserService.updateProfile(long,java.util.Map)":
                {
                    const long userId = 90901L;
                    string marker = $"profile-{registryKind}-{version}";
                    DubboRequestResult update = await InvokeAsync(
                        client,
                        method,
                        userId,
                        new Dictionary<string, string>
                        {
                            ["marker"] = marker,
                            ["language"] = "中文"
                        });
                    AssertSuccess(update);

                    ProviderMethodCase getUser = EndToEndTestCatalog.GetMethod("UserService.getUser(long)");
                    JsonElement user = await InvokeForJsonAsync(client, getUser, userId);
                    JsonElement attributes = Property(user, "attributes");
                    Assert.Equal(marker, Property(attributes, "marker").GetString());
                    Assert.Equal("中文", Property(attributes, "language").GetString());
                    break;
                }
            default:
                throw new InvalidOperationException("No invocation assertion exists for " + method.Id);
        }

        _output.WriteLine($"[PASS] {registryKind}/{version} {method.Id}");
    }

    private async Task<JsonElement> InvokeForJsonAsync(
        DubboClient client,
        ProviderMethodCase method,
        params object[] arguments)
    {
        DubboRequestResult result = await InvokeAsync(client, method, arguments);
        AssertSuccess(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Result));

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Result!);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"{method.Id} returned invalid JSON: {result.Result}. {exception.Message}");
        }
    }

    private async Task<DubboRequestResult> InvokeAsync(
        DubboClient client,
        ProviderMethodCase method,
        params object[] arguments)
    {
        DubboRequestResult result = await client.QueryAsync(
            method.Service + "." + method.Method,
            method.ParameterTypes,
            arguments);
        _output.WriteLine(
            $"{method.Id}: success={result.QuerySuccess}, elapsed={result.RequestElapsed}ms, " +
            $"result={Shorten(result.Result)}, error={Shorten(result.ErrorMeaasge)}");
        return result;
    }

    private static void AssertSuccess(DubboRequestResult result)
    {
        Assert.True(
            result.QuerySuccess && string.IsNullOrWhiteSpace(result.ErrorMeaasge),
            "Dubbo invocation failed: " + result.ErrorMeaasge + "; result=" + result.Result);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.True(element.TryGetProperty(name, out JsonElement value),
            $"Property '{name}' was not found in {element.GetRawText()}.");
        return value;
    }

    private static decimal ReadDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            JsonValueKind.Object => ReadDecimal(Property(value, "value")),
            _ => throw new Xunit.Sdk.XunitException(
                "Expected a decimal-compatible JSON value, but received " + value.GetRawText())
        };
    }

    private static void AssertJsonBytes(byte[] expected, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expected, Convert.FromBase64String(value.GetString()!));
            return;
        }
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal(expected, value.EnumerateArray().Select(item => item.GetByte()).ToArray());
    }

    private static void AssertValidJavaDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            Assert.True(value.GetInt64() > 0);
            return;
        }
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.True(DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _));
    }

    /// <summary>
    /// 按 Java 类型擦除规则移除元数据中的泛型参数，以便和实际 Dubbo 调用签名比较。
    /// EN: Removes metadata generic arguments using Java type erasure before comparing invocation signatures.
    /// </summary>
    private static string EraseGenericArguments(string typeName)
    {
        StringBuilder erased = new(typeName.Length);
        int genericDepth = 0;
        foreach (char character in typeName)
        {
            if (character == '<')
            {
                genericDepth++;
                continue;
            }
            if (character == '>')
            {
                genericDepth--;
                continue;
            }
            if (genericDepth == 0)
            {
                erased.Append(character);
            }
        }
        Assert.Equal(0, genericDepth);
        return erased.ToString().Trim();
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }
        string singleLine = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return singleLine.Length <= 180 ? singleLine : singleLine[..180] + "...";
    }
}
