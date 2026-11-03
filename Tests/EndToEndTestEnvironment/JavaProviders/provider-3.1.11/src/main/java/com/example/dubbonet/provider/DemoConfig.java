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

package com.example.dubbonet.provider;

import java.io.IOException;
import java.io.InputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.util.Enumeration;
import java.util.Properties;

public final class DemoConfig {
    private static final String CONFIG_RESOURCE = "/dubbonet-demo.properties";

    private final Properties properties;

    private DemoConfig(Properties properties) {
        this.properties = properties;
    }

    public static DemoConfig load() {
        Properties properties = new Properties();
        InputStream input = DemoConfig.class.getResourceAsStream(CONFIG_RESOURCE);
        if (input == null) {
            throw new IllegalStateException("Missing resource: " + CONFIG_RESOURCE);
        }
        try {
            properties.load(input);
        } catch (IOException exception) {
            throw new IllegalStateException("Cannot read " + CONFIG_RESOURCE, exception);
        } finally {
            try {
                input.close();
            } catch (IOException ignored) {
                // Nothing else to release.
            }
        }
        return new DemoConfig(properties);
    }

    public String dubboVersion() {
        return required("dubbo.version");
    }

    public String registryAddress() {
        String configured = System.getenv("DUBBONET_ZOOKEEPER_REGISTRY_ADDRESS");
        return systemOrDefault("dubbonet.zookeeper.registry.address",
                configured == null || configured.trim().isEmpty()
                        ? required("dubbo.registry.address") : configured.trim());
    }

    public String registryGroup() {
        String configured = System.getenv("DUBBO_REGISTRY_GROUP");
        return systemOrDefault("dubbo.registry.group",
                configured == null || configured.trim().isEmpty()
                        ? required("dubbo.registry.group") : configured.trim());
    }

    public int protocolPort() {
        return Integer.parseInt(systemOrDefault("dubbo.protocol.port", required("dubbo.protocol.port")));
    }

    public String providerHost() {
        String configured = System.getProperty("dubbo.provider.host");
        if (configured == null || configured.trim().isEmpty()) {
            configured = System.getenv("DUBBONET_PROVIDER_HOST");
        }
        if (configured != null && !configured.trim().isEmpty()) {
            return configured.trim();
        }
        return findSiteLocalAddress();
    }

    public String serviceVersion() {
        return systemOrDefault("dubbo.service.version", required("dubbo.service.version"));
    }

    public String serviceGroup() {
        return systemOrDefault("dubbo.service.group", "dubbonet-" + dubboVersion());
    }

    public String providerApplicationName() {
        return "dubbonet-provider-" + dubboVersion();
    }

    public String consumerApplicationName() {
        return "dubbonet-consumer-" + dubboVersion();
    }

    private String systemOrDefault(String key, String defaultValue) {
        String override = System.getProperty(key);
        return override == null || override.trim().isEmpty() ? defaultValue : override.trim();
    }

    private String required(String key) {
        String value = properties.getProperty(key);
        if (value == null || value.trim().isEmpty()) {
            throw new IllegalStateException("Missing config key: " + key);
        }
        return value.trim();
    }

    private String findSiteLocalAddress() {
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface networkInterface = interfaces.nextElement();
                if (!networkInterface.isUp() || networkInterface.isLoopback()) {
                    continue;
                }
                Enumeration<InetAddress> addresses = networkInterface.getInetAddresses();
                while (addresses.hasMoreElements()) {
                    InetAddress address = addresses.nextElement();
                    if (address instanceof Inet4Address && address.isSiteLocalAddress()) {
                        return address.getHostAddress();
                    }
                }
            }
        } catch (SocketException ignored) {
            // Let Dubbo choose an address when network interfaces cannot be inspected.
        }
        return null;
    }
}
