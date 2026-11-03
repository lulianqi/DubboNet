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
provider_root="$script_dir/JavaProviders"
log_dir=${1:-"$script_dir/logs"}
pid_file="$log_dir/provider-pids"

mkdir -p "$log_dir"
if [[ -f "$pid_file" ]]; then
  while IFS= read -r provider_pid; do
    if [[ "$provider_pid" =~ ^[0-9]+$ ]] && kill -0 "$provider_pid" 2>/dev/null; then
      echo "A provider from this log directory is still running (PID $provider_pid). Stop it first." >&2
      exit 1
    fi
  done < "$pid_file"
fi
: > "$pid_file"

# 使用 DUBBONET_ 前缀，避免 Dubbo 3.3 把 DUBBO_REGISTRY_ADDRESS 当作全局覆盖项。
# EN: Avoid DUBBO_REGISTRY_ADDRESS because Dubbo 3.3 applies it to every registry.
export DUBBONET_ZOOKEEPER_REGISTRY_ADDRESS=${DUBBONET_ZOOKEEPER_REGISTRY_ADDRESS:-zookeeper://127.0.0.1:2181}
export DUBBONET_NACOS_REGISTRY_ADDRESS=${DUBBONET_NACOS_REGISTRY_ADDRESS:-nacos://127.0.0.1:8848}

if [[ -z "${DUBBONET_PROVIDER_HOST:-}" ]] && command -v ip >/dev/null 2>&1; then
  DUBBONET_PROVIDER_HOST=$(ip -4 route get 1.1.1.1 \
    | awk '{ for (field = 1; field <= NF; field++) if ($field == "src") { print $(field + 1); exit } }')
fi
if [[ -z "${DUBBONET_PROVIDER_HOST:-}" ]]; then
  echo "DUBBONET_PROVIDER_HOST must be a non-loopback address reachable from this machine." >&2
  exit 1
fi
if [[ "$DUBBONET_PROVIDER_HOST" == 127.* || "$DUBBONET_PROVIDER_HOST" == "0.0.0.0" ]]; then
  echo "Dubbo does not allow the registry address $DUBBONET_PROVIDER_HOST for a provider." >&2
  exit 1
fi
export DUBBONET_PROVIDER_HOST
echo "$DUBBONET_PROVIDER_HOST" > "$log_dir/provider-host"

modules=(
  provider-2.7.23
  provider-3.1.11
  provider-3.2.20
  provider-3.3.6
)

for module in "${modules[@]}"; do
  jar_file=$(find "$provider_root/$module/target" -maxdepth 1 \
    -name 'dubbonet-example-*.jar' ! -name '*-sources.jar' ! -name '*-javadoc.jar' \
    -print -quit)
  if [[ -z "$jar_file" ]]; then
    echo "Cannot find the packaged provider JAR for $module." >&2
    exit 1
  fi

  java -Xms64m -Xmx256m -jar "$jar_file" > "$log_dir/$module.log" 2>&1 &
  provider_pid=$!
  echo "$provider_pid" >> "$pid_file"
  echo "Started $module (PID $provider_pid)."
done
