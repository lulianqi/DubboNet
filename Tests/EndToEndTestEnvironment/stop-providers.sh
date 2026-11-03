#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
log_dir=${1:-"$script_dir/logs"}
pid_file="$log_dir/provider-pids"

if [[ ! -f "$pid_file" ]]; then
  exit 0
fi

while IFS= read -r provider_pid; do
  if [[ "$provider_pid" =~ ^[0-9]+$ ]] && kill -0 "$provider_pid" 2>/dev/null; then
    kill "$provider_pid" 2>/dev/null || true
  fi
done < "$pid_file"
