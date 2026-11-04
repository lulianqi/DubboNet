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
using DubboNet.Clients.RegistryClient;
using System.Net;

namespace DubboNet.Testing;

// A single read-only snapshot. The shell owns polling and the shared startup deadline.
// Kept out of the client library and reused by the integration executable/unit tests.
internal static class EnvironmentReadinessProbe
{
    internal static readonly string[] Services =
    {
        "com.example.dubbonet.api.DataTypeService",
        "com.example.dubbonet.api.EchoService",
        "com.example.dubbonet.api.OrderService",
        "com.example.dubbonet.api.OverloadService",
        "com.example.dubbonet.api.UserService"
    };

    public static async Task CheckAsync(
        string registryAddress,
        IReadOnlyCollection<IPAddress> providerAddresses,
        IEnumerable<(string Version, string RootPath)> versions)
    {
        bool useNacos = registryAddress.StartsWith("nacos://", StringComparison.OrdinalIgnoreCase);
        string kind = useNacos ? "Nacos" : "ZooKeeper";
        var scenarios = versions.ToArray();
        if (scenarios.Length == 0 || providerAddresses.Count == 0)
            throw new ArgumentException("Readiness requires fixture versions and a resolved Provider address.");
        using MyNacos? nacos = useNacos ? new MyNacos(registryAddress) : null;
        using MyZookeeper? zookeeper = useNacos ? null : new MyZookeeper(registryAddress);
        if (zookeeper != null && !await zookeeper.ConnectZooKeeperAsync())
            throw new InvalidOperationException("ZooKeeper connection is not ready.");

        List<string> failures = new();
        int readyServices = 0;
        foreach ((string version, string root) in scenarios)
        {
            using var client = new DubboClient(registryAddress, new DubboClient.DubboClientConf
            {
                DubboRootPath = root,
                MetadataRootPath = root,
                TelnetMetadataTimeout = 500,
                DubboRequestTimeout = 2_000
            });
            foreach (string service in Services)
            {
                string label = $"{kind}/{version}/{service}";
                try
                {
                    IReadOnlyList<DubboServiceEndPointInfo> providers;
                    if (nacos != null)
                    {
                        providers = await nacos.GetProvidersAsync(service, root);
                    }
                    else
                    {
                        string location = root.TrimEnd('/') + "/" + service + "/providers";
                        var children = await zookeeper!.GetChildrenAsync(location, retryTime: 0);
                        providers = (children?.Children ?? new List<string>())
                            .Select(node => Uri.UnescapeDataString(node))
                            .Select(url => DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(new Uri(url)))
                            .ToArray();
                    }
                    if (!providers.Any(provider => IsExpectedProvider(provider, service, version, providerAddresses)))
                        throw new InvalidOperationException($"Expected active dubbo:// Provider is absent under {root}.");

                    // Unlike method lookup, types cannot be supplied by a Telnet fallback.
                    var types = await client.GetServiceTypesMetadataAsync(service);
                    var methods = await client.GetServiceMethodsMetadataAsync(service);
                    if (!MetadataIsReady(types.Count, methods))
                        throw new InvalidOperationException("FullServiceDefinition types/methods are not ready in the metadata center.");

                    readyServices++;
                    Console.WriteLine($"[READY] {label}: Provider and metadata published.");
                }
                catch (Exception exception)
                {
                    failures.Add($"{label}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        Console.WriteLine($"Readiness summary: {kind}, {readyServices} services ready.");
    }

    internal static bool IsExpectedProvider(
        DubboServiceEndPointInfo provider,
        string service,
        string version,
        IReadOnlyCollection<IPAddress> addresses)
    {
        int port = version switch
        {
            "2.7.23" => 20883,
            "3.1.11" => 20882,
            "3.2.20" => 20881,
            "3.3.6" => 20880,
            _ => throw new ArgumentException("Unknown fixture version: " + version)
        };
        return provider.EndPoint != null
            && addresses.Contains(provider.EndPoint.Address)
            && provider.EndPoint.Port == port
            && provider.Scheme == "dubbo"
            && provider.Interface == service
            && provider.Application == "dubbonet-provider-" + version
            && provider.Version == "1.0.0"
            && provider.Group == "dubbonet-" + version
            && provider.Disabled != true
            && provider.Weight > 0;
    }

    internal static bool MetadataIsReady(int typeCount, IReadOnlyList<DubboMethodMetadata> methods) =>
        typeCount > 0 && methods.Count > 0
        && methods.All(method => method.MetadataSource == DubboMetadataSource.MetadataCenter);
}
