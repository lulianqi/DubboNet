using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using System.Net;
using System.Text;
using System.Threading;

namespace UnitTestForDubboNet
{
    public class DubboMetadataTest
    {
        [Fact]
        public void BuildMetadataPath_UsesDubbo27IdentifierLayout()
        {
            Assert.Equal(
                "/dubbo/metadata/com.foo.DemoService/provider/demo-provider",
                DubboMetadataManager.BuildMetadataPath(
                    "/dubbo/",
                    "com.foo.DemoService",
                    null,
                    null,
                    "demo-provider"));

            Assert.Equal(
                "/custom/metadata/com.foo.DemoService/1.2.0/prod/provider/demo-provider",
                DubboMetadataManager.BuildMetadataPath(
                    "custom",
                    "com.foo.DemoService",
                    "1.2.0",
                    "prod",
                    "demo-provider"));
        }

        [Fact]
        public void ParseMetadataDocument_ReturnsMethodAndPojoDetails()
        {
            const string json = """
                {
                  "canonicalName": "com.foo.DemoService",
                  "codeSource": "demo-api.jar",
                  "methods": [
                    {
                      "name": "save",
                      "parameterTypes": ["com.foo.User", "java.lang.Long"],
                      "returnType": "java.lang.Boolean"
                    }
                  ],
                  "types": [
                    {
                      "id": "com.foo.User",
                      "type": "object",
                      "enums": ["ACTIVE", "INACTIVE"],
                      "properties": {
                        "name": { "type": "java.lang.String" }
                      },
                      "typeBuilderName": "org.apache.dubbo.metadata.definition.builder.EnumTypeBuilder"
                    }
                  ],
                  "parameters": {
                    "application": "demo-provider",
                    "version": "1.0.0"
                  }
                }
                """;
            DubboServiceEndPointInfo provider = new DubboServiceEndPointInfo
            {
                Application = "demo-provider",
                Version = "1.0.0",
                Group = "prod"
            };

            IReadOnlyList<DubboMethodMetadata> methods =
                DubboMetadataManager.ParseMetadataDocument(
                    Encoding.UTF8.GetBytes(json),
                    "com.foo.DemoService",
                    provider,
                    "/dubbo/metadata/com.foo.DemoService/1.0.0/prod/provider/demo-provider",
                    7,
                    DateTimeOffset.UnixEpoch);

            DubboMethodMetadata method = Assert.Single(methods);
            Assert.Equal("save", method.Name);
            Assert.Equal(
                new[] { "com.foo.User", "java.lang.Long" },
                method.ParameterTypes);
            Assert.Equal("java.lang.Boolean", method.ReturnType);
            Assert.Equal("demo-provider", method.Application);
            Assert.Equal("prod", method.Group);
            Assert.Equal(7, method.MetadataVersion);
            Assert.Equal("demo-api.jar", method.CodeSource);
            Assert.Equal(DubboMetadataSource.MetadataCenter, method.MetadataSource);
            DubboTypeMetadata type = Assert.Single(method.TypeDefinitions);
            Assert.Equal("com.foo.User", type.Id);
            Assert.Equal("object", type.Type);
            Assert.Equal("java.lang.String", type.Properties["name"].Type);
            Assert.Equal(new[] { "ACTIVE", "INACTIVE" }, type.EnumValues);
            Assert.Equal(
                "org.apache.dubbo.metadata.definition.builder.EnumTypeBuilder",
                type.TypeBuilderName);
            Assert.Equal("1.0.0", method.ServiceParameters["version"]);
        }

        [Fact]
        public void ParseMetadataDocument_MapsAllDubbo273TypeDefinitionFields()
        {
            const string json = """
                {
                  "canonicalName": "com.foo.DemoService",
                  "methods": [
                    {
                      "name": "save",
                      "parameterTypes": ["com.foo.Container"],
                      "returnType": "void"
                    }
                  ],
                  "types": [
                    {
                      "id": "container-id",
                      "type": "com.foo.Container",
                      "items": [
                        {
                          "type": "com.foo.Status",
                          "enum": ["ACTIVE", "INACTIVE"],
                          "typeBuilderName": "org.apache.dubbo.metadata.definition.builder.EnumTypeBuilder"
                        }
                      ],
                      "$ref": "com.foo.Container",
                      "properties": {
                        "status": {
                          "type": "com.foo.Status",
                          "$ref": "com.foo.Status"
                        }
                      },
                      "typeBuilderName": "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder"
                    }
                  ]
                }
                """;

            IReadOnlyList<DubboMethodMetadata> methods =
                DubboMetadataManager.ParseMetadataDocument(
                    Encoding.UTF8.GetBytes(json),
                    "com.foo.DemoService",
                    Provider("2.7.3"),
                    "/dubbo/metadata/com.foo.DemoService/provider/demo-provider",
                    1,
                    DateTimeOffset.UnixEpoch);

            DubboTypeMetadata type = Assert.Single(Assert.Single(methods).TypeDefinitions);
            Assert.Equal("container-id", type.Id);
            Assert.Equal("com.foo.Container", type.Type);
            Assert.Equal("com.foo.Container", type.Reference);
            Assert.Equal(
                "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder",
                type.TypeBuilderName);

            DubboTypeMetadata item = Assert.Single(type.Items);
            Assert.Equal("com.foo.Status", item.Type);
            Assert.Equal(new[] { "ACTIVE", "INACTIVE" }, item.EnumValues);
            Assert.Equal(
                "org.apache.dubbo.metadata.definition.builder.EnumTypeBuilder",
                item.TypeBuilderName);

            DubboTypeMetadata property = type.Properties["status"];
            Assert.Equal("com.foo.Status", property.Type);
            Assert.Equal("com.foo.Status", property.Reference);
        }

        [Fact]
        public void ParseMetadataDocument_MaterializesDubboJsonPathPropertyReferences()
        {
            const string json = """
                {
                  "canonicalName": "com.example.dubbonet.api.OrderService",
                  "methods": [
                    {
                      "name": "calculateTotal",
                      "parameterTypes": ["java.util.List<com.example.dubbonet.model.OrderItem>"],
                      "returnType": "java.math.BigDecimal"
                    }
                  ],
                  "types": [
                    {
                      "type": "com.example.dubbonet.model.Order",
                      "properties": {
                        "orderId": { "type": "java.lang.String" },
                        "totalAmount": { "type": "java.math.BigDecimal" }
                      }
                    },
                    {
                      "type": "com.example.dubbonet.model.OrderQuery",
                      "properties": {
                        "pageNo": { "type": "int" }
                      }
                    },
                    {
                      "type": "com.example.dubbonet.model.OrderItem",
                      "properties": {
                        "name": { "$ref": "$.types[0].properties.orderId" },
                        "quantity": { "$ref": "$.types[1].properties.pageNo" },
                        "sku": { "$ref": "#/types/0/properties/orderId" },
                        "unitPrice": { "$ref": "$.types[0].properties.totalAmount" }
                      }
                    }
                  ]
                }
                """;

            DubboMethodMetadata method = Assert.Single(
                DubboMetadataManager.ParseMetadataDocument(
                    Encoding.UTF8.GetBytes(json),
                    "com.example.dubbonet.api.OrderService",
                    Provider("2.7.3"),
                    "/dubbo/metadata/com.example.dubbonet.api.OrderService/provider/demo-provider",
                    1,
                    DateTimeOffset.UnixEpoch));
            DubboTypeMetadata item = Assert.Single(method.TypeDefinitions.Where(type =>
                type.Type == "com.example.dubbonet.model.OrderItem"));

            Assert.Equal("java.lang.String", item.Properties["name"].Type);
            Assert.Equal("int", item.Properties["quantity"].Type);
            Assert.Equal("java.lang.String", item.Properties["sku"].Type);
            Assert.Equal("java.math.BigDecimal", item.Properties["unitPrice"].Type);
            Assert.Equal("$.types[0].properties.orderId", item.Properties["name"].Reference);
        }

        [Fact]
        public void Resolve_SelectsIntegerOverloadForClrInt()
        {
            DubboMethodMetadata resolved = DubboMethodMetadataResolver.Resolve(
                "com.foo.DemoService",
                "find",
                new[]
                {
                    Method("find", "java.lang.Long"),
                    Method("find", "java.lang.Integer")
                },
                new object[] { 1234 });

            Assert.Equal("java.lang.Integer", Assert.Single(resolved.ParameterTypes));
        }

        [Fact]
        public void Resolve_CollapsesSameSignaturePublishedByMultipleApplications()
        {
            DubboMethodMetadata resolved = DubboMethodMetadataResolver.Resolve(
                "com.foo.DemoService",
                "find",
                new[]
                {
                    Method("find", "java.lang.Integer"),
                    Method("find", "java.lang.Integer")
                },
                new object[] { 1234 });

            Assert.Equal("java.lang.Integer", Assert.Single(resolved.ParameterTypes));
        }

        [Fact]
        public void Resolve_RejectsAmbiguousPrimitiveAndWrapperOverloads()
        {
            DubboMetadataException exception = Assert.Throws<DubboMetadataException>(() =>
                DubboMethodMetadataResolver.Resolve(
                    "com.foo.DemoService",
                    "find",
                    new[]
                    {
                        Method("find", "int"),
                        Method("find", "java.lang.Integer")
                    },
                    new object[] { 1234 }));

            Assert.Contains("Pass javaParameterTypes explicitly", exception.Message);
        }

        [Theory]
        [InlineData("2.7.2", false)]
        [InlineData("2.7.3", true)]
        [InlineData("2.7.3-SNAPSHOT", true)]
        [InlineData("2.7.3.1", true)]
        [InlineData("3.2.20", true)]
        [InlineData(null, true)]
        [InlineData("unknown", true)]
        public void VersionPolicy_UsesReleaseAndTreatsUnknownAsMetadataCapable(
            string release,
            bool expected)
        {
            bool actual = DubboTelnetMetadataResolver.ShouldPreferMetadataCenter(
                new[]
                {
                    new DubboServiceEndPointInfo { Release = release }
                });

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void VersionPolicy_MixedProvidersPreferMetadataCenter()
        {
            bool actual = DubboTelnetMetadataResolver.ShouldPreferMetadataCenter(
                new[]
                {
                    new DubboServiceEndPointInfo { Release = "2.7.2" },
                    new DubboServiceEndPointInfo { Release = "2.7.23" }
                });

            Assert.True(actual);
        }

        [Fact]
        public void ParseTelnetMetadata_HandlesOverloadsEmptyParametersAndErasesGenerics()
        {
            const string response =
                "PROVIDER:\n" +
                "\tjava.lang.String find(java.lang.Integer)\n" +
                "\tjava.lang.String find(java.util.Map<java.lang.String, java.lang.Integer>, java.lang.Long)\n" +
                "\tvoid ping()\n" +
                "\tpublic java.util.List<java.lang.String> list()\n" +
                "dubbo>";
            DubboServiceEndPointInfo provider = Provider("2.7.2");

            IReadOnlyList<DubboMethodMetadata> methods =
                DubboTelnetMetadataResolver.ParseMethodSignatures(
                    response,
                    "com.foo.DemoService",
                    provider,
                    DateTimeOffset.UnixEpoch);

            Assert.Equal(4, methods.Count);
            Assert.Equal(
                new[]
                {
                    "java.util.Map",
                    "java.lang.Long"
                },
                methods.Single(method => method.ParameterTypes.Length == 2).ParameterTypes);
            Assert.Empty(methods.Single(method => method.Name == "ping").ParameterTypes);
            Assert.Equal(
                "java.util.List",
                methods.Single(method => method.Name == "list").ReturnType);
            Assert.All(methods, method =>
                Assert.Equal(DubboMetadataSource.Telnet, method.MetadataSource));
        }

        [Fact]
        public async Task ProviderDiscoveryFailure_IsNotPermanentlyCached()
        {
            int providerDiscoveryCount = 0;
            int telnetProbeCount = 0;
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ =>
                {
                    if (Interlocked.Increment(ref providerDiscoveryCount) == 1)
                    {
                        throw new InvalidOperationException("temporary registry outage");
                    }
                    return Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                        new[] { Provider("2.7.2") });
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref telnetProbeCount);
                    return Task.FromResult(DubboTelnetMetadataResult.Success(
                        new[] { Method("find", "java.lang.Integer") }));
                });

            DubboMetadataException first = await Assert.ThrowsAsync<DubboMetadataException>(() =>
                manager.GetServiceMethodsAsync("com.foo.DemoService"));
            IReadOnlyList<DubboMethodMetadata> recovered =
                await manager.GetServiceMethodsAsync("com.foo.DemoService");

            Assert.Contains("temporary registry outage", first.Message);
            Assert.Equal(2, providerDiscoveryCount);
            Assert.Equal(1, telnetProbeCount);
            Assert.Single(recovered);
        }

        [Fact]
        public async Task TelnetMetadata_SuccessIsProbedOnceAndCachedAcrossConcurrentCalls()
        {
            int probeCount = 0;
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                    new[] { Provider("2.7.2") }),
                async (_, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    await Task.Delay(20);
                    return DubboTelnetMetadataResult.Success(
                        new[] { Method("find", "java.lang.Integer") });
                });

            Task<IReadOnlyList<DubboMethodMetadata>>[] calls = Enumerable.Range(0, 20)
                .Select(_ => manager.GetServiceMethodsAsync("com.foo.DemoService"))
                .ToArray();
            await Task.WhenAll(calls);
            IReadOnlyList<DubboMethodMetadata> cached =
                await manager.GetServiceMethodsAsync("com.foo.DemoService");

            Assert.Equal(1, probeCount);
            Assert.Single(cached);
        }

        [Fact]
        public async Task TelnetMetadata_CachedResultDoesNotRediscoverProviders()
        {
            int providerDiscoveryCount = 0;
            int probeCount = 0;
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ =>
                {
                    if (Interlocked.Increment(ref providerDiscoveryCount) > 1)
                    {
                        throw new InvalidOperationException("registry unavailable after cache fill");
                    }
                    return Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                        new[] { Provider("2.7.2") });
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(DubboTelnetMetadataResult.Success(
                        new[] { Method("find", "java.lang.Integer") }));
                });

            DubboTelnetMetadataResult first =
                await manager.GetTelnetServiceMetadataAsync("com.foo.DemoService");
            DubboTelnetMetadataResult second =
                await manager.GetTelnetServiceMetadataAsync("com.foo.DemoService");

            Assert.Single(first.Methods);
            Assert.Single(second.Methods);
            Assert.Equal(1, providerDiscoveryCount);
            Assert.Equal(1, probeCount);
        }

        [Fact]
        public async Task TelnetMetadata_FailureIsProbedOnceAndThrowsActionableException()
        {
            int probeCount = 0;
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                    new[] { Provider("2.7.2") }),
                (_, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(
                        DubboTelnetMetadataResult.Failure("connection refused"));
                });

            DubboMetadataException first = await Assert.ThrowsAsync<DubboMetadataException>(() =>
                manager.GetServiceMethodsAsync("com.foo.DemoService"));
            DubboMetadataException second = await Assert.ThrowsAsync<DubboMetadataException>(() =>
                manager.GetServiceMethodsAsync("com.foo.DemoService"));

            Assert.Equal(1, probeCount);
            Assert.Contains("below 2.7.3", first.Message);
            Assert.Contains("connection refused", first.Message);
            Assert.Contains("QueryGenericAsync", first.Message);
            Assert.Equal(first.Message, second.Message);
        }

        [Fact]
        public async Task ModernProviderWithoutCenterDocumentFallsBackToCachedTelnet()
        {
            int probeCount = 0;
            DubboServiceEndPointInfo provider = Provider("2.7.3");
            provider.Application = null; // No metadata path can be derived; no live ZK is needed.
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                    new[] { provider }),
                (_, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(DubboTelnetMetadataResult.Success(
                        new[] { Method("find", "java.lang.Long") }));
                });

            IReadOnlyList<DubboMethodMetadata> first =
                await manager.GetServiceMethodsAsync("com.foo.DemoService");
            IReadOnlyList<DubboMethodMetadata> second =
                await manager.GetServiceMethodsAsync("com.foo.DemoService");

            Assert.Equal(1, probeCount);
            Assert.Single(first);
            Assert.Single(second);
        }

        [Fact]
        public async Task ModernProviderChecksMetadataPathBeforeReadingNodeData()
        {
            const string serviceName = "com.foo.DemoService";
            const string expectedPath =
                "/dubbo/metadata/com.foo.DemoService/provider/demo-provider";
            int existsCount = 0;
            int dataReadCount = 0;
            int telnetProbeCount = 0;
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                    new[] { Provider("2.7.3") }),
                (_, _) =>
                {
                    Interlocked.Increment(ref telnetProbeCount);
                    DubboMethodMetadata telnetMethod = Method("find", "java.lang.Long");
                    telnetMethod.MetadataSource = DubboMetadataSource.Telnet;
                    return Task.FromResult(DubboTelnetMetadataResult.Success(
                        new[] { telnetMethod }));
                },
                (path, _) =>
                {
                    Assert.Equal(expectedPath, path);
                    Interlocked.Increment(ref existsCount);
                    return Task.FromResult<org.apache.zookeeper.data.Stat>(null!);
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref dataReadCount);
                    return Task.FromResult<org.apache.zookeeper.DataResult>(null!);
                });

            IReadOnlyList<DubboMethodMetadata> first =
                await manager.GetServiceMethodsAsync(serviceName);
            IReadOnlyList<DubboMethodMetadata> cached =
                await manager.GetServiceMethodsAsync(serviceName);

            Assert.Equal(1, existsCount);
            Assert.Equal(0, dataReadCount);
            Assert.Equal(1, telnetProbeCount);
            Assert.Single(first);
            Assert.Single(cached);
            Assert.All(first, method =>
                Assert.Equal(DubboMetadataSource.Telnet, method.MetadataSource));
        }

        [Fact]
        public async Task ServiceTypes_LoadFromMetadataCenterAfterExistsCheckAndAreCached()
        {
            const string serviceName = "com.foo.DemoService";
            const string metadataJson = """
                {
                  "canonicalName": "com.foo.DemoService",
                  "methods": [],
                  "types": [
                    {
                      "type": "com.foo.CreateRequest",
                      "properties": {
                        "name": { "type": "java.lang.String" },
                        "children": {
                          "type": "java.util.List<com.foo.ChildRequest>",
                          "typeBuilderName": "CollectionTypeBuilder"
                        }
                      },
                      "typeBuilderName": "DefaultTypeBuilder"
                    },
                    {
                      "$ref": "$.types[0].properties.name"
                    },
                    {
                      "type": "com.foo.EnvironmentTypeEnum",
                      "enum": ["DEV", "PROD"],
                      "typeBuilderName": "EnumTypeBuilder"
                    }
                  ]
                }
                """;
            int existsCount = 0;
            int dataReadCount = 0;
            int telnetProbeCount = 0;
            org.apache.zookeeper.data.Stat stat =
                new org.apache.zookeeper.data.Stat(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                    new[] { Provider("2.7.3") }),
                (_, _) =>
                {
                    Interlocked.Increment(ref telnetProbeCount);
                    return Task.FromResult(
                        DubboTelnetMetadataResult.Failure("must not be called"));
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref existsCount);
                    return Task.FromResult(stat);
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref dataReadCount);
                    return Task.FromResult(CreateDataResult(
                        Encoding.UTF8.GetBytes(metadataJson),
                        stat));
                });

            IReadOnlyList<DubboTypeMetadata> first =
                await manager.GetServiceTypesAsync(serviceName);
            IReadOnlyList<DubboTypeMetadata> cached =
                await manager.GetServiceTypesAsync(serviceName);

            DubboTypeMetadata request = Assert.Single(
                first.Where(type => type.Type == "com.foo.CreateRequest"));
            Assert.Equal("com.foo.CreateRequest", request.Type);
            Assert.Equal("java.lang.String", request.Properties["name"].Type);
            Assert.Equal(
                "java.util.List<com.foo.ChildRequest>",
                request.Properties["children"].Type);
            DubboTypeMetadata environment = Assert.Single(
                first.Where(type => type.Type == "com.foo.EnvironmentTypeEnum"));
            Assert.Equal(new[] { "DEV", "PROD" }, environment.EnumValues);
            Assert.Equal("$.types[0].properties.name", first[1].Reference);
            Assert.Equal("java.lang.String", first[1].Type);
            Assert.Equal(3, cached.Count);
            Assert.Equal(1, existsCount);
            Assert.Equal(1, dataReadCount);
            Assert.Equal(0, telnetProbeCount);
        }

        [Fact]
        public async Task ServiceTypes_MissingMetadataPathDoesNotReadNodeOrUseTelnet()
        {
            int existsCount = 0;
            int dataReadCount = 0;
            int telnetProbeCount = 0;
            using MyZookeeper zookeeper = new MyZookeeper("127.0.0.1:2181");
            using DubboMetadataManager manager = new DubboMetadataManager(
                zookeeper,
                "/dubbo",
                _ => Task.FromResult<IReadOnlyList<DubboServiceEndPointInfo>>(
                    new[] { Provider("2.7.3") }),
                (_, _) =>
                {
                    Interlocked.Increment(ref telnetProbeCount);
                    return Task.FromResult(
                        DubboTelnetMetadataResult.Failure("must not be called"));
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref existsCount);
                    return Task.FromResult<org.apache.zookeeper.data.Stat>(null!);
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref dataReadCount);
                    return Task.FromResult<org.apache.zookeeper.DataResult>(null!);
                });

            DubboMetadataException first = await Assert.ThrowsAsync<DubboMetadataException>(() =>
                manager.GetServiceTypesAsync("com.foo.DemoService"));
            DubboMetadataException cached = await Assert.ThrowsAsync<DubboMetadataException>(() =>
                manager.GetServiceTypesAsync("com.foo.DemoService"));

            Assert.Contains(
                "/dubbo/metadata/com.foo.DemoService/provider/demo-provider",
                first.Message);
            Assert.Contains("does not exist", first.Message);
            Assert.Equal(first.Message, cached.Message);
            Assert.Equal(1, existsCount);
            Assert.Equal(0, dataReadCount);
            Assert.Equal(0, telnetProbeCount);
        }

        [Fact]
        public void ZookeeperStorage_ReusesOneClientForTheSameConnectionString()
        {
            using MultiMyZookeeperStorage storage = new MultiMyZookeeperStorage();

            MyZookeeper first = storage.GetMyZookeeper("127.0.0.1:2181");
            MyZookeeper second = storage.GetMyZookeeper("127.0.0.1:2181");

            Assert.Same(first, second);
            storage.RemoveMyZookeeper(first);
            storage.RemoveMyZookeeper(second);
        }

        private static DubboMethodMetadata Method(string name, params string[] parameterTypes)
        {
            return new DubboMethodMetadata
            {
                ServiceName = "com.foo.DemoService",
                Name = name,
                ParameterTypes = parameterTypes,
                ReturnType = "java.lang.Object"
            };
        }

        private static org.apache.zookeeper.DataResult CreateDataResult(
            byte[] data,
            org.apache.zookeeper.data.Stat stat)
        {
            org.apache.zookeeper.DataResult result =
                (org.apache.zookeeper.DataResult)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(org.apache.zookeeper.DataResult));
            typeof(org.apache.zookeeper.DataResult)
                .GetField(nameof(org.apache.zookeeper.DataResult.Data))!
                .SetValue(result, data);
            typeof(org.apache.zookeeper.NodeResult)
                .GetField(nameof(org.apache.zookeeper.NodeResult.Stat))!
                .SetValue(result, stat);
            return result;
        }

        private static DubboServiceEndPointInfo Provider(string release)
        {
            return new DubboServiceEndPointInfo
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 20880),
                Scheme = "dubbo",
                Interface = "com.foo.DemoService",
                Application = "demo-provider",
                Release = release
            };
        }
    }
}
