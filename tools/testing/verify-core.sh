#!/usr/bin/env bash
# 始终先构建所有纯 Core 校验项目，避免 --no-build 读取旧的依赖副本。
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
jobs=8
data_dir=tests/Golden/data
usage() {
    echo "用法: $0 [--jobs N] [--data-dir DIR]" >&2
}
while (( $# > 0 )); do
    case "$1" in
        --jobs)
            if (( $# < 2 )) || [[ ! $2 =~ ^[1-9][0-9]*$ ]]; then
                usage
                exit 2
            fi
            jobs=$2
            shift 2
            ;;
        --data-dir)
            if (( $# < 2 )) || [[ -z $2 ]]; then
                usage
                exit 2
            fi
            data_dir=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

cd -- "$repo_root"
if [[ ! -d $data_dir ]]; then
    echo "数据目录不存在: $data_dir" >&2
    exit 2
fi
data_dir=$(cd -- "$data_dir" && pwd)
worktree=clean
if [[ -n $(git status --porcelain) ]]; then
    worktree=dirty
fi
printf "VERIFY_CORE sha=%s worktree=%s sdk=%s jobs=%s data=%s\n" \
    "$(git rev-parse HEAD)" "$worktree" "$(dotnet --version)" "$jobs" "$data_dir"

# 只构建 Differential 不会刷新 Golden/KernelChecks 目录中的 Core.dll。
dotnet build Sts2Solver.sln -c Release
dotnet run --project tests/KernelChecks -c Release --no-build
dotnet run --project tests/Architecture -c Release --no-build
dotnet run --project tests/Golden -c Release --no-build -- k0 "$data_dir"
dotnet run --project tests/Golden -c Release --no-build -- verify "$data_dir" --jobs "$jobs"
