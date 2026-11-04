# Reproducible end-to-end test environment

This directory retains the environment layout validated in the original DubboNet repository:
`Tests/EndToEndTestEnvironment/JavaProviders`. The independent .NET test project is
`Tests/DubboNetEndToEndTests`; it is included in `DubboNet.sln`.

ZooKeeper and Nacos run in temporary Docker Compose containers. The four Java Providers
are built from the Maven source here and run as short-lived JDK 8 processes on the test host.
No private JARs, personal server addresses, or registry secrets are needed.
Java/Maven are test-only tools, not dependencies of the client NuGet package.

## Requirements and isolation

Use a Linux host with Bash, Docker Engine and Compose v2, JDK 8, Maven, the .NET 10 SDK,
GNU coreutils (`timeout`), and iproute2 (`ip`). Normal dependency and public image downloads
require internet access; no external Dubbo test server is contacted.

The registries use pinned image versions `zookeeper:3.8.4` and
`nacos/nacos-server:v2.5.4`. Nacos authentication is disabled only for this temporary fixture.
Their published ports bind to `127.0.0.1`. Provider ports use a non-loopback IPv4 on the test
host because Dubbo rejects loopback advertised addresses. Run this environment only on a
trusted, disposable machine/network; these Java fixture versions and diagnostic ports are
not intended for production or public exposure.

The scripts auto-detect the advertised IPv4 on Linux. Set `DUBBONET_PROVIDER_HOST` explicitly
if needed; it must belong to the test host and be reachable by the .NET processes.
The Java processes and .NET clients must run on that same host; this layout does not
advertise Docker-only container addresses to host clients.

## Coverage

| Registry | Dubbo Provider versions | Native TCP ports |
| --- | --- | --- |
| ZooKeeper | 2.7.23 / 3.1.11 / 3.2.20 / 3.3.6 | 20883 / 20882 / 20881 / 20880 |
| Nacos Naming + Config | 3.2.20 / 3.3.6 | 20881 / 20880 |

Dubbo 3.3.6 also exposes Triple Java Interface Unary HTTP/JSON on port `50051`.
The registries require host ports `2181`, `8848`, `9848`, and `9849`.
Each CI job has its own runner and Compose project. Fixed ports mean local runs must not
overlap on the same machine or conflict with existing registries/Providers.

The workflow performs all of the following, without silently omitting versions:

- 150 xUnit method-invocation cases: all 25 signatures from DataTypeService, EchoService,
  OrderService, OverloadService, and UserService across the six registry/version combinations.
  Cases include arrays, maps, nested POJO, enums, decimals, overloads, stateful updates, and
  expected Provider exceptions. These are logical test cases; a case may send multiple RPCs.
- Six metadata coverage cases compare all exposed signatures against the test catalog,
  applying Java generic type erasure. Adding a Provider method requires a matching test.
- `DubboClientIntegrationTests` in ZooKeeper and Nacos modes, including metadata reads,
  directory APIs, inference, typed responses, and explicit Native/Triple direct calls.
- `QueryDemo` in both modes, checking actual fixture values for all ten QueryAsync
  overloads and both direct transports. A failed call, deserialization error, or unexpected
  value gives a non-zero exit code. Expected Provider failures must contain the test marker;
  a timeout/disconnection is not accepted as an expected business exception.

The separate xUnit project skips network tests unless `DUBBONET_E2E_ENABLED=true`.
Once enabled, missing registry settings or unavailable services fail the tests rather than
skipping them. This opt-in behavior keeps ordinary offline unit tests independent.

## Run locally

From the repository root:

```bash
Tests/EndToEndTestEnvironment/run-local.sh
```

An optional first argument selects a dedicated log directory. Otherwise a temporary directory
is created and its path printed. The script builds the Java Providers, starts the registries
and Providers, restores/builds the .NET readiness executable, waits for publication, and runs the full suite.
It installs EXIT/INT/TERM cleanup traps, preserves a failing exit code, and removes its
Compose containers and volumes even when a test fails. Do not share its log directory or
Compose project name with another run. Like any process, it cannot trap SIGKILL or power loss.

Readiness uses one shared 180-second deadline for TCP ports **and** actual publication;
`DUBBONET_E2E_READY_TIMEOUT` can override it. Dead Provider processes fail immediately.
There is no fixed settling sleep. The integration executable's read-only
`--check-readiness` mode verifies all five interfaces in each expected version/root:
an enabled, positive-weight native Provider at the expected advertised address/port and
service identity, plus nonempty FullServiceDefinition types and metadata-center method
definitions. Telnet-only metadata cannot satisfy readiness. Each retry uses a fresh
process/snapshot to avoid retaining a missing-node cache. No business RPC is sent by the probe.
Missing publication is retried within the remaining deadline, then fails with the affected
registry/version/interface in `readiness.log`; the normal RPC tests are never retried to hide regressions.

The probe must first be built in Release (the workflow and local runner do this). To inspect
an existing environment once, without polling or business calls:

```bash
dotnet Tests/DubboClientIntegrationTests/bin/Release/net10.0/DubboClientIntegrationTests.dll \
  --check-readiness --registry 127.0.0.1:2181 --provider-host "$DUBBONET_PROVIDER_HOST"
```

The deadline bounds the probe process; a stuck process is force-killed after a two-second
termination grace period. TCP checks have their own two-second per-attempt cap.

## Reuse an already running fixture

After starting the environment with the commands used in `run-local.sh`, additional tests
can reuse the same registries and Providers:

```bash
export DUBBONET_E2E_ENABLED=true
export DUBBONET_E2E_ZOOKEEPER_ADDRESS=127.0.0.1:2181
export DUBBONET_E2E_NACOS_ADDRESS=nacos://127.0.0.1:8848

dotnet test Tests/DubboNetEndToEndTests/DubboNetEndToEndTests.csproj \
  -p:GeneratePackageOnBuild=false --logger "console;verbosity=detailed"

dotnet run --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  -p:GeneratePackageOnBuild=false \
  -- --registry nacos://127.0.0.1:8848 --provider-host "$DUBBONET_PROVIDER_HOST"

dotnet run --project Tests/QueryDemo/QueryDemo.csproj \
  -p:GeneratePackageOnBuild=false \
  -- --registry 127.0.0.1:2181 --provider-host "$DUBBONET_PROVIDER_HOST"
```

Registry roots/metadata groups are `dubbo-{version}`; service version is `1.0.0`,
and service group is `dubbonet-{version}`. Provider defaults are local. To override
registry locations, use `DUBBONET_ZOOKEEPER_REGISTRY_ADDRESS` and
`DUBBONET_NACOS_REGISTRY_ADDRESS`, not Dubbo's global `DUBBO_REGISTRY_ADDRESS`, which
would collapse the two registry settings in Dubbo 3.3.

If manually managing lifecycle, always use the same log directory and Compose project:

```bash
Tests/EndToEndTestEnvironment/stop-providers.sh /path/to/run-logs
docker compose -f Tests/EndToEndTestEnvironment/compose.yaml down --volumes --remove-orphans
```

The Compose file's `name: dubbonet-e2e` is only a stable fallback for direct/manual commands.
CI overrides it per run/attempt; `run-local.sh` uses a per-process project name and prints
the exact project/cleanup command. Use that name with `-p` when inspecting or cleaning up from
another shell; deleting the fallback would not remove this requirement.

## GitHub Actions and diagnostics

`.github/workflows/end-to-end.yml` uses exactly the same trigger configuration as
`build.yml`: pushes to `main`, pull requests targeting `main`, and manual dispatch.
There are no path filters; documentation-only changes trigger it too.
It is separate from the fast build/unit workflow and has a 30-minute job timeout.
This layer detects real registry-path/discovery and protocol interoperability regressions
that unit tests cannot establish against Java Providers. It is not redundant slow unit testing.
PR runs share a cancellation group; each main push/manual run uses its own run-ID group, so
updates to main do not cancel in-progress or pending checks of previous pushes.

Java dependencies and Action revisions are pinned; Actions use read-only repository
permissions and checkout does not retain credentials. No package or image is published.

Every test's output is streamed to the job log and saved under the run's log directory.
The always-run final steps collect registry logs and upload Provider/client logs and xUnit TRX
artifacts (seven-day retention). Two independent always-run cleanup steps then stop Providers
and remove Compose resources, with one-minute and two-minute limits respectively.
A cleanup failure emits a warning and cannot skip the other cleanup or override the RPC test
result on the disposable CI runner. Local cleanup remains strict and returns a failing status
if resources cannot be removed.
Inspect `end-to-end-logs-*` and `end-to-end-test-results-*` on failures. The runner is
disposable; cleanup is best-effort if GitHub forcibly terminates the job.

To extend coverage, update the Java service/source in `JavaProviders/common`, add its
signature to `EndToEndTestCatalog.Methods`, and implement the corresponding assertions
in `DubboProviderMethodsEndToEndTests`. Rebuild and run this environment; no personal
remote test cluster is needed.
