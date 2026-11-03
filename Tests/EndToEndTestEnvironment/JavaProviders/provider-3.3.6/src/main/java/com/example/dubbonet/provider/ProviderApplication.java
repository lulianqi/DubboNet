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

import org.apache.dubbo.config.ApplicationConfig;
import org.apache.dubbo.config.ProtocolConfig;
import org.apache.dubbo.config.RegistryConfig;
import org.apache.dubbo.config.ServiceConfig;
import org.apache.dubbo.config.bootstrap.DubboBootstrap;

import java.util.Arrays;
import java.util.List;

public final class ProviderApplication {
    private ProviderApplication() {
    }

    public static void main(String[] args) {
        DemoConfig config = DemoConfig.load();
        configureCompatibilityProperties(config);

        ApplicationConfig application = new ApplicationConfig(config.providerApplicationName());
        application.setQosEnable(false);
        application.setMetadataType("remote");

        RegistryConfig zookeeperRegistry = registry(
                "zookeeper-registry", config.registryAddress(), config);
        RegistryConfig nacosRegistry = registry(
                "nacos-registry", config.nacosRegistryAddress(), config);

        ProtocolConfig dubboProtocol = new ProtocolConfig("dubbo", config.protocolPort());
        dubboProtocol.setSerialization("hessian2");
        ProtocolConfig triProtocol = new ProtocolConfig("tri", config.triProtocolPort());
        List<ProtocolConfig> protocols = Arrays.asList(dubboProtocol, triProtocol);

        DubboBootstrap bootstrap = DubboBootstrap.getInstance()
                .application(application);
        bootstrap
                .registry(zookeeperRegistry)
                .registry(nacosRegistry)
                .protocol(dubboProtocol)
                .protocol(triProtocol)
                .service(service(com.example.dubbonet.api.UserService.class,
                        new UserServiceImpl(), config, protocols))
                .service(service(com.example.dubbonet.api.OrderService.class,
                        new OrderServiceImpl(), config, protocols))
                .service(service(com.example.dubbonet.api.EchoService.class,
                        new EchoServiceImpl(), config, protocols))
                .service(service(com.example.dubbonet.api.DataTypeService.class,
                        new DataTypeServiceImpl(), config, protocols))
                .service(service(com.example.dubbonet.api.OverloadService.class,
                        new OverloadServiceImpl(), config, protocols));

        System.out.println("Starting DubboNet provider");
        System.out.println("  Dubbo version : " + config.dubboVersion());
        System.out.println("  ZooKeeper     : " + config.registryAddress());
        System.out.println("  Nacos         : " + config.nacosRegistryAddress());
        System.out.println("  Registry group: " + config.registryGroup());
        System.out.println("  Dubbo protocol: dubbo://:" + config.protocolPort());
        System.out.println("  Triple protocol: tri://:" + config.triProtocolPort());
        System.out.println("  Metadata      : remote (ZooKeeper + Nacos)");
        System.out.println("  Provider host : " + displayHost(config.providerHost()));
        System.out.println("  Service group : " + config.serviceGroup());
        System.out.println("  Service count : 5 (25 methods)");

        bootstrap.start().await();
    }

    private static void configureCompatibilityProperties(DemoConfig config) {
        // Dubbo 3 otherwise prefers application-level discovery. Interface mode keeps
        // interface-level /{registry-group}/{interface}/providers nodes for DubboNet tests.
        setIfAbsent("dubbo.application.register-mode", "interface");
        setIfAbsent("dubbo.application.qos.enable", "false");
        setIfAbsent("dubbo.registry.use-as-config-center", "false");
        setIfAbsent("dubbo.registry.use-as-metadata-center", "true");
        if (config.providerHost() != null) {
            setIfAbsent("DUBBO_IP_TO_REGISTRY", config.providerHost());
        }
    }

    private static String displayHost(String host) {
        return host == null ? "Dubbo auto-detect" : host;
    }

    private static RegistryConfig registry(String id, String address, DemoConfig config) {
        RegistryConfig registry = new RegistryConfig(address);
        registry.setId(id);
        registry.setDefault(true);
        registry.setGroup(config.registryGroup());
        registry.setCheck(false);
        registry.setRegisterMode("interface");
        registry.setUseAsConfigCenter(false);
        registry.setUseAsMetadataCenter(true);
        return registry;
    }

    private static void setIfAbsent(String key, String value) {
        if (System.getProperty(key) == null) {
            System.setProperty(key, value);
        }
    }

    private static <T> ServiceConfig<T> service(Class<T> interfaceClass, T implementation,
                                                 DemoConfig config,
                                                 List<ProtocolConfig> protocols) {
        ServiceConfig<T> service = new ServiceConfig<T>();
        service.setId(interfaceClass.getName());
        service.setInterface(interfaceClass);
        service.setRef(implementation);
        service.setGroup(config.serviceGroup());
        service.setVersion(config.serviceVersion());
        service.setProtocols(protocols);
        service.setTimeout(5000);
        service.setRetries(0);
        return service;
    }
}
