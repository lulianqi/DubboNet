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
pid_file="$log_dir/provider-pids"
provider_root="$script_dir/JavaProviders"

if [[ ! -f "$pid_file" ]]; then
  exit 0
fi

owned_pids=()
while IFS= read -r provider_pid; do
  if [[ "$provider_pid" =~ ^[0-9]+$ ]] && kill -0 "$provider_pid" 2>/dev/null; then
    provider_command=$(ps -p "$provider_pid" -o command= 2>/dev/null || true)
    # A stale/reused PID must never terminate an unrelated process.
    if [[ "$provider_command" == *"-jar $provider_root/"* ]]; then
      owned_pids+=("$provider_pid")
      kill "$provider_pid" 2>/dev/null || true
    fi
  fi
done < "$pid_file"

shutdown_deadline=$((SECONDS + 10))
for provider_pid in "${owned_pids[@]}"; do
  while kill -0 "$provider_pid" 2>/dev/null && (( SECONDS < shutdown_deadline )); do
    sleep 1
  done
  if kill -0 "$provider_pid" 2>/dev/null; then
    provider_command=$(ps -p "$provider_pid" -o command= 2>/dev/null || true)
    if [[ "$provider_command" == *"-jar $provider_root/"* ]]; then
      kill -KILL "$provider_pid" 2>/dev/null || true
    fi
  fi
done
