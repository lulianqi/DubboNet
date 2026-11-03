# Java providers used by end-to-end tests

This Maven reactor builds four short-lived providers for Dubbo 2.7.23, 3.1.11,
3.2.20, and 3.3.6. The service interfaces, models, and implementations live in
`common`; each provider module contains only the bootstrapping code and the
dependencies required by its Dubbo version.

The providers are test fixtures, not published artifacts. Their registry and
advertised host are overridden by `Tests/EndToEndTestEnvironment/start-providers.sh`, so GitHub
Actions always uses the local ZooKeeper and Nacos containers.
