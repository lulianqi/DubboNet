#!/usr/bin/env bash
# Licensed to the Apache Software Foundation (ASF) under one or more
# contributor license agreements. See the NOTICE file distributed with
# this work for additional information regarding copyright ownership.
# The ASF licenses this file to You under the Apache License, Version 2.0
# (the "License"); you may not use this file except in compliance with
# the License. You may obtain a copy of the License at
#
#   http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
cd "$repo_root"

for required in docker java mvn dotnet timeout; do
  if ! command -v "$required" >/dev/null 2>&1; then
    echo "Required tool is missing: $required. See $script_dir/README.md." >&2
    exit 1
  fi
done
docker compose version >/dev/null
docker info >/dev/null

log_dir=${1:-$(mktemp -d "${TMPDIR:-/tmp}/dubbonet-e2e.XXXXXX")}
mkdir -p "$log_dir"
log_dir=$(CDPATH= cd -- "$log_dir" && pwd)
export COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-dubbonet-e2e-local-$$}
echo "Compose project: $COMPOSE_PROJECT_NAME"
echo "Manual cleanup: docker compose -p $COMPOSE_PROJECT_NAME -f $script_dir/compose.yaml down --volumes --remove-orphans"

cleanup() {
  local status=$?
  trap - EXIT
  docker compose -f "$script_dir/compose.yaml" logs --no-color > "$log_dir/registries.log" 2>&1 || true
  "$script_dir/stop-providers.sh" "$log_dir" || status=1
  docker compose -f "$script_dir/compose.yaml" down --volumes --remove-orphans || status=1
  echo "End-to-end logs: $log_dir"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

run_logged() {
  local name=$1
  shift
  "$@" 2>&1 | tee "$log_dir/$name.log"
}

docker compose -f "$script_dir/compose.yaml" up -d
run_logged java-build mvn -B -ntp -f "$script_dir/JavaProviders/pom.xml" package -DskipTests
"$script_dir/start-providers.sh" "$log_dir"
DUBBONET_PROVIDER_HOST=$(<"$log_dir/provider-host")
if [[ -z "$DUBBONET_PROVIDER_HOST" ]]; then
  echo "The Provider host file is empty: $log_dir/provider-host" >&2
  exit 1
fi
export DUBBONET_PROVIDER_HOST
dotnet restore DubboNet.sln
run_logged readiness-build dotnet build Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
  --configuration Release --no-restore -p:GeneratePackageOnBuild=false
"$script_dir/wait-for-environment.sh" "$log_dir"

export DUBBONET_E2E_ENABLED=true
export DUBBONET_E2E_ZOOKEEPER_ADDRESS=127.0.0.1:2181
export DUBBONET_E2E_NACOS_ADDRESS=nacos://127.0.0.1:8848
run_logged end-to-end-tests dotnet test Tests/DubboNetEndToEndTests/DubboNetEndToEndTests.csproj \
  --configuration Release --no-restore -p:GeneratePackageOnBuild=false \
  --logger "console;verbosity=detailed" --logger "trx;LogFileName=dubbonet-end-to-end.trx" \
  --results-directory "$log_dir/test-results"

for registry in "$DUBBONET_E2E_ZOOKEEPER_ADDRESS" "$DUBBONET_E2E_NACOS_ADDRESS"; do
  registry_kind=zookeeper
  if [[ "$registry" == nacos://* ]]; then registry_kind=nacos; fi
  run_logged "integration-$registry_kind" dotnet run \
    --project Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj \
    --configuration Release --no-restore -p:GeneratePackageOnBuild=false \
    -- --registry "$registry" --provider-host "$DUBBONET_PROVIDER_HOST"
  run_logged "query-demo-$registry_kind" dotnet run \
    --project Tests/QueryDemo/QueryDemo.csproj \
    --configuration Release --no-restore -p:GeneratePackageOnBuild=false \
    -- --registry "$registry" --provider-host "$DUBBONET_PROVIDER_HOST"
done
