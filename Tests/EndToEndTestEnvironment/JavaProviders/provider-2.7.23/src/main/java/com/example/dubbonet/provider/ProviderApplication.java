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

public final class ProviderApplication {
    private ProviderApplication() {
    }

    public static void main(String[] args) {
        DemoConfig config = DemoConfig.load();
        configureCompatibilityProperties(config);

        ApplicationConfig application = new ApplicationConfig(config.providerApplicationName());
        application.setQosEnable(false);
        application.setMetadataType("remote");

        RegistryConfig registry = new RegistryConfig(config.registryAddress());
        registry.setGroup(config.registryGroup());
        registry.setCheck(false);
        registry.setUseAsMetadataCenter(true);

        ProtocolConfig protocol = new ProtocolConfig("dubbo", config.protocolPort());
        protocol.setSerialization("hessian2");

        DubboBootstrap bootstrap = DubboBootstrap.getInstance()
                .application(application);
        bootstrap
                .registry(registry)
                .protocol(protocol)
                .service(service(com.example.dubbonet.api.UserService.class,
                        new UserServiceImpl(), config))
                .service(service(com.example.dubbonet.api.OrderService.class,
                        new OrderServiceImpl(), config))
                .service(service(com.example.dubbonet.api.EchoService.class,
                        new EchoServiceImpl(), config))
                .service(service(com.example.dubbonet.api.DataTypeService.class,
                        new DataTypeServiceImpl(), config))
                .service(service(com.example.dubbonet.api.OverloadService.class,
                        new OverloadServiceImpl(), config));

        System.out.println("Starting DubboNet provider");
        System.out.println("  Dubbo version : " + config.dubboVersion());
        System.out.println("  Registry      : " + config.registryAddress());
        System.out.println("  Registry root : /" + config.registryGroup());
        System.out.println("  Protocol port : " + config.protocolPort());
        System.out.println("  Metadata type : remote");
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

    private static void setIfAbsent(String key, String value) {
        if (System.getProperty(key) == null) {
            System.setProperty(key, value);
        }
    }

    private static <T> ServiceConfig<T> service(Class<T> interfaceClass, T implementation,
                                                 DemoConfig config) {
        ServiceConfig<T> service = new ServiceConfig<T>();
        service.setId(interfaceClass.getName());
        service.setInterface(interfaceClass);
        service.setRef(implementation);
        service.setGroup(config.serviceGroup());
        service.setVersion(config.serviceVersion());
        service.setTimeout(5000);
        service.setRetries(0);
        return service;
    }
}
