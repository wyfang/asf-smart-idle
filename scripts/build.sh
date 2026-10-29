#!/usr/bin/env bash
set -euo pipefail

script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
project_dir="$(CDPATH= cd -- "$script_dir/.." && pwd)"

if [[ -n "${DOTNET:-}" ]]; then
    dotnet_cmd="$DOTNET"
elif command -v dotnet >/dev/null 2>&1; then
    dotnet_cmd="$(command -v dotnet)"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_cmd="$HOME/.dotnet/dotnet"
else
    printf '%s\n' '未找到 .NET 10 SDK；请安装 SDK，或通过 DOTNET 指定 dotnet 可执行文件。' >&2
    exit 1
fi

sdk_version="$("$dotnet_cmd" --version)"
if [[ "$sdk_version" != 10.* ]]; then
    printf '需要 .NET 10 SDK，当前为 %s。请通过 DOTNET 指定正确版本。\n' "$sdk_version" >&2
    exit 1
fi

reference_dir="$("$script_dir/prepare-asf.sh")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
cd -- "$project_dir"

"$dotnet_cmd" build src/SmartIdle.Plugin/SmartIdle.Plugin.csproj \
    --configuration Release --nologo \
    -p:AsfReferenceDir="$reference_dir" -p:ContinuousIntegrationBuild=true
"$dotnet_cmd" build tests/SmartIdle.HostProbe/SmartIdle.HostProbe.csproj \
    --configuration Release --nologo \
    -p:AsfReferenceDir="$reference_dir" -p:ContinuousIntegrationBuild=true
"$dotnet_cmd" run --project tests/SmartIdle.Tests --configuration Release \
    -p:ContinuousIntegrationBuild=true
"$dotnet_cmd" run --project tests/SmartIdle.RatingTests --configuration Release \
    -p:ContinuousIntegrationBuild=true

python3 "$script_dir/package.py"
