#!/usr/bin/env bash
# 开发用文件夹构建（输出可运行的发布目录 + tar.gz）。
# 面向用户的发布产物是单一 AppImage：使用 scripts/build-appimage.sh。
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -n "${DOTNET:-}" ]]; then
    dotnet_cmd="$DOTNET"
elif [[ -x "$repo_dir/.tools/dotnet/dotnet" ]]; then
    dotnet_cmd="$repo_dir/.tools/dotnet/dotnet"
else
    dotnet_cmd="dotnet"
fi
output_dir="$repo_dir/artifacts/pcl-ce-linux-x64"

sdk_version="$("$dotnet_cmd" --version)"
if (( ${sdk_version%%.*} < 11 )); then
    echo "构建需要 .NET 11 SDK（C# 15）；当前版本：$sdk_version" >&2
    exit 1
fi

"$dotnet_cmd" publish "$repo_dir/PCL.Linux/PCL.Linux.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$output_dir"

{
    echo "PCL CE Linux 1.0.0"
    echo "Target: linux-x64, self-contained .NET 10"
    echo "SDK: $sdk_version"
    echo "Built: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo "Source: $(git -C "$repo_dir" describe --always --dirty)"
} > "$output_dir/BUILD-INFO.txt"

tar -czf "$repo_dir/artifacts/PCL-CE-linux-x64.tar.gz" -C "$repo_dir/artifacts" pcl-ce-linux-x64
(
    cd "$repo_dir/artifacts"
    sha256sum PCL-CE-linux-x64.tar.gz > PCL-CE-linux-x64.tar.gz.sha256
)
echo "开发用 Linux 发布目录：$repo_dir/artifacts/pcl-ce-linux-x64"
echo "开发用 tar.gz：$repo_dir/artifacts/PCL-CE-linux-x64.tar.gz（正式发布产物请用 scripts/build-appimage.sh）"
