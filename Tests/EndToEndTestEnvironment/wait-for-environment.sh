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
log_dir=${1:-"$script_dir/logs"}
provider_host=${DUBBONET_PROVIDER_HOST:-}
if [[ -z "$provider_host" && -f "$log_dir/provider-host" ]]; then
  provider_host=$(<"$log_dir/provider-host")
fi
if [[ -z "$provider_host" ]]; then
  echo "Cannot determine the provider host." >&2
  exit 1
fi

ready_timeout=${DUBBONET_E2E_READY_TIMEOUT:-180}
if [[ ! "$ready_timeout" =~ ^[1-9][0-9]*$ ]]; then
  echo "DUBBONET_E2E_READY_TIMEOUT must be a positive number of seconds." >&2
  exit 1
fi
ready_deadline=$((SECONDS + ready_timeout))

check_provider_processes() {
  if [[ ! -s "$log_dir/provider-pids" ]]; then
    echo "No provider PID file found; start the providers first." >&2
    return 1
  fi
  while IFS= read -r provider_pid; do
    if [[ ! "$provider_pid" =~ ^[0-9]+$ ]] || ! kill -0 "$provider_pid" 2>/dev/null; then
      echo "Provider process $provider_pid exited during startup. Inspect $log_dir." >&2
      return 1
    fi
  done < "$log_dir/provider-pids"
}

wait_for_tcp() {
  local host=$1
  local port=$2
  local label=$3

  while (( SECONDS < ready_deadline )); do
    check_provider_processes
    if timeout 2 bash -c 'echo > "/dev/tcp/$1/$2"' _ "$host" "$port" >/dev/null 2>&1; then
      echo "$label is ready on $host:$port."
      return 0
    fi
    sleep 2
  done

  echo "Timed out waiting for $label on $host:$port." >&2
  return 1
}

wait_for_tcp 127.0.0.1 2181 ZooKeeper
wait_for_tcp 127.0.0.1 8848 Nacos
wait_for_tcp 127.0.0.1 9848 "Nacos gRPC"
wait_for_tcp "$provider_host" 20883 "Dubbo 2.7.23 provider"
wait_for_tcp "$provider_host" 20882 "Dubbo 3.1.11 provider"
wait_for_tcp "$provider_host" 20881 "Dubbo 3.2.20 provider"
wait_for_tcp "$provider_host" 20880 "Dubbo 3.3.6 provider"
wait_for_tcp "$provider_host" 50051 "Dubbo 3.3.6 Triple provider"

check_provider_processes

probe="$script_dir/../DubboClientIntegrationTests/bin/Release/net10.0/DubboClientIntegrationTests.dll"
if [[ ! -f "$probe" ]]; then
  echo "Build DubboClientIntegrationTests in Release before waiting for publication." >&2
  exit 1
fi
mkdir -p "$log_dir"
readiness_log="$log_dir/readiness.log"
: > "$readiness_log"

wait_for_publication() {
  local registry=$1 label=$2 remaining
  while (( SECONDS < ready_deadline )); do
    check_provider_processes
    remaining=$((ready_deadline - SECONDS))
    if (( remaining <= 0 )); then break; fi
    if timeout --kill-after=2 "$remaining" dotnet "$probe" --check-readiness \
      --registry "$registry" --provider-host "$provider_host" >> "$readiness_log" 2>&1; then
      echo "$label Providers and metadata are ready."
      return 0
    fi
    echo "Waiting for $label Provider/metadata publication (details: $readiness_log)."
    if (( SECONDS < ready_deadline )); then sleep 1; fi
  done
  echo "Timed out waiting for $label Provider/metadata publication. Inspect $readiness_log." >&2
  tail -n 40 "$readiness_log" >&2
  return 1
}

wait_for_publication 127.0.0.1:2181 ZooKeeper
wait_for_publication nacos://127.0.0.1:8848 Nacos
