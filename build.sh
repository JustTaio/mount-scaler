#!/usr/bin/env bash
# MSBuildEnableWorkloadResolver=false works around a broken .NET workload set.
set -euo pipefail
cd "$(dirname "$0")/MountScaler"
MSBuildEnableWorkloadResolver=false dotnet build -c Release "$@"
