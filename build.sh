#!/usr/bin/env bash
# Builds MountScaler. The workload-resolver variable works around a broken
# .NET workload set on this machine ("dotnet workload repair" would also fix it).
set -euo pipefail
cd "$(dirname "$0")/MountScaler"
MSBuildEnableWorkloadResolver=false dotnet build -c Release "$@"
