#!/usr/bin/env bash
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

wait_for_tcp() {
  local host=$1
  local port=$2
  local label=$3

  for _ in $(seq 1 90); do
    if (echo > "/dev/tcp/$host/$port") >/dev/null 2>&1; then
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

if [[ -f "$log_dir/provider-pids" ]]; then
  while IFS= read -r provider_pid; do
    if ! kill -0 "$provider_pid" 2>/dev/null; then
      echo "Provider process $provider_pid exited during startup." >&2
      exit 1
    fi
  done < "$log_dir/provider-pids"
fi

# Ports open before registry metadata is always visible. Give both registries a
# short, deterministic settling window before the first discovery request.
sleep 10
