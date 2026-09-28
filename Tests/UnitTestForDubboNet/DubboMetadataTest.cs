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
                      }
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
            Assert.Equal("java.lang.String", type.Properties["name"].Type);
            Assert.Equal(new[] { "ACTIVE", "INACTIVE" }, type.EnumValues);
            Assert.Equal("1.0.0", method.ServiceParameters["version"]);
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
