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

using DubboNet.Clients.DataModle;
using DubboNet.Testing;
using System.Net;

namespace UnitTestForDubboNet;

public sealed class EnvironmentReadinessProbeTest
{
    private const string Service = "com.example.dubbonet.api.EchoService";
    private static readonly IPAddress[] Addresses = { IPAddress.Parse("192.0.2.10") };

    [Theory]
    [InlineData("2.7.23", 20883)]
    [InlineData("3.1.11", 20882)]
    [InlineData("3.2.20", 20881)]
    [InlineData("3.3.6", 20880)]
    public void Recognizes_each_expected_fixture(string version, int port)
    {
        var provider = Provider(version, port);
        Assert.True(EnvironmentReadinessProbe.IsExpectedProvider(provider, Service, version, Addresses));
    }

    [Theory]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("protocol")]
    [InlineData("interface")]
    [InlineData("application")]
    [InlineData("version")]
    [InlineData("group")]
    [InlineData("disabled")]
    [InlineData("weight")]
    [InlineData("missing endpoint")]
    public void A_record_count_or_open_port_does_not_prove_publication(string mismatch)
    {
        var provider = Provider("3.3.6", 20880);
        switch (mismatch)
        {
            case "address": provider.EndPoint = new IPEndPoint(IPAddress.Loopback, 20880); break;
            case "port": provider.EndPoint = new IPEndPoint(Addresses[0], 1); break;
            case "protocol": provider.Scheme = "tri"; break;
            case "interface": provider.Interface = "other"; break;
            case "application": provider.Application = "other"; break;
            case "version": provider.Version = "2.0.0"; break;
            case "group": provider.Group = "other"; break;
            case "disabled": provider.Disabled = true; break;
            case "weight": provider.Weight = 0; break;
            case "missing endpoint": provider.EndPoint = null; break;
        }
        Assert.False(EnvironmentReadinessProbe.IsExpectedProvider(provider, Service, "3.3.6", Addresses));
    }

    [Fact]
    public void Empty_or_telnet_only_metadata_is_not_ready()
    {
        var remote = new DubboMethodMetadata { Name = "ping", MetadataSource = DubboMetadataSource.MetadataCenter };
        var telnet = new DubboMethodMetadata { Name = "ping", MetadataSource = DubboMetadataSource.Telnet };
        Assert.True(EnvironmentReadinessProbe.MetadataIsReady(1, new[] { remote }));
        Assert.False(EnvironmentReadinessProbe.MetadataIsReady(0, new[] { remote }));
        Assert.False(EnvironmentReadinessProbe.MetadataIsReady(1, Array.Empty<DubboMethodMetadata>()));
        Assert.False(EnvironmentReadinessProbe.MetadataIsReady(1, new[] { telnet }));
        Assert.False(EnvironmentReadinessProbe.MetadataIsReady(1, new[] { remote, telnet }));
    }

    [Fact]
    public async Task An_empty_matrix_does_not_report_ready_without_checks()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => EnvironmentReadinessProbe.CheckAsync(
            "localhost:2181", Addresses, Array.Empty<(string, string)>()));
    }

    private static DubboServiceEndPointInfo Provider(string version, int port) =>
        DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(new Uri(
            $"dubbo://192.0.2.10:{port}/{Service}?interface={Service}"
            + $"&application=dubbonet-provider-{version}&version=1.0.0&group=dubbonet-{version}"));
}
