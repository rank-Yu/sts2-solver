#!/usr/bin/env bash
# Linux headless launcher for Sts2Solver's Mod test mode.
#
# Creates .local/headless-instances/<instance>, copies the game, RitsuLib, the packaged
# Sts2Solver mod and the seed XDG_DATA_HOME into it, writes one or more OfflineRequest JSON
# files, starts the native headless game with --force-steam=off, and polls results/done.json.
#
# The instance never contains CombatSolver unless --coexist-combat-solver is given (used only
# for the coexistence probe). The source game directory is never modified.
#
# Trimmed from CombatSolver/tools/testing/run-unattended-test.sh and headless-runtime.sh at
# commit a62f1850 (MIT, Copyright (c) 2026 Torch1230 and contributors).
set -uo pipefail

die() { failure_reason="$*"; echo "run-headless: $*" >&2; exit 1; }
info() { echo "run-headless: $*"; }

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
instance=""
instance_root="${STS2_SOLVER_HEADLESS_ROOT:-$repo_root/.local/headless-instances}"
failure_root="${STS2_SOLVER_HEADLESS_FAILURE_ROOT:-$repo_root/.local/headless-failures}"
game_root="$HOME/games/sts2"
ritsu_root="$HOME/games/sts2/steamapps/workshop/content/2868840/3747602295"
mod_dir="$repo_root/artifacts/mod-testing/Sts2Solver"
seed_data="$HOME/ss/CombatSolver/.local/headless-seed/data"
offline_host="$repo_root/artifacts/bin/Differential/release/Differential.dll"
requests_dir=""
results_dir=""
log_dir=""
timeout_seconds=600
shutdown_timeout_seconds=30
cleanup_instance=0
coexist=0
probe_only=0
no_launch=0
full_combat=0
search_tier=""
combat_solver_dir="$HOME/games/sts2/mods/CombatSolver"
declare -a roots=()
declare -a request_files=()

usage() {
    cat <<'USAGE'
用法：
  tools/testing/run-headless.sh --instance NAME (--roots-file FILE | --root NAME ... |
      --requests DIR | --request FILE ...) [options]

选项：
  --instance NAME              实例名（必填；字母/数字/._-，最多 64 字符）
  --roots-file FILE            从 Frozen 根名列表生成请求（每行一个，# 注释）
  --root NAME                  单个 Frozen 根名（可重复）
  --requests DIR               直接复制目录里的 *.json 请求（文件名即结果根名）
  --request FILE               直接复制单个请求 JSON（文件名即结果根名，可重复）
  --results DIR                完成后把结果目录复制到 DIR
  --log-dir DIR                复制 Godot、启动器和 Mod 日志到 DIR（诊断用）
  --timeout-seconds N          等待 results/done.json 的秒数（默认 600）
  --shutdown-timeout-seconds N  结果写出后等待正常退出的秒数（默认 30）
  --cleanup-instance-on-exit   结束后删除整个实例目录
  --coexist-combat-solver      把 CombatSolver 也放进实例（只用于共存检查）
  --probe-only                 只等到 Mod 初始化/禁用日志，不要求 results/done.json
  --no-launch                  只准备实例和请求，不启动游戏
  --full-combat                用 --roots-file/--root 生成整场自动战斗请求
  --search-tier NAME           整场自动战斗的搜索档位（Low/Medium/High/ExtraHigh，默认 Medium）
  --instance-root DIR          实例根（默认 .local/headless-instances）
  --failure-root DIR           失败证据根（默认 .local/headless-failures）
  --game-root DIR              游戏目录（默认 ~/games/sts2）
  --ritsu-root DIR             RitsuLib 目录（默认 workshop 3747602295）
  --mod-dir DIR                Sts2Solver Mod 目录（默认 artifacts/mod-testing/Sts2Solver）
  --seed-data DIR              种子 XDG_DATA_HOME/data（默认 CombatSolver headless-seed/data）
  --offline-host-dll FILE      Differential.dll（用于 --roots-file/--root 生成请求）
  --combat-solver-dir DIR      CombatSolver Mod 目录（默认 ~/games/sts2/mods/CombatSolver）
  -h, --help                   显示本帮助

示例：
  tools/testing/run-headless.sh --instance m2b0-20 --roots-file .local/m2b0-20-roots.txt \
      --results .local/m2b0-20-results --cleanup-instance-on-exit
  tools/testing/run-headless.sh --instance m2b0-coexist --coexist-combat-solver \
      --probe-only --cleanup-instance-on-exit
USAGE
}

while (($#)); do
    case "$1" in
        --instance) instance="${2:-}"; shift 2 ;;
        --instance-root) instance_root="${2:-}"; shift 2 ;;
        --failure-root) failure_root="${2:-}"; shift 2 ;;
        --roots-file) roots_file="${2:-}"; shift 2 ;;
        --root) roots+=("${2:-}"); shift 2 ;;
        --requests) requests_dir="${2:-}"; shift 2 ;;
        --request) request_files+=("${2:-}"); shift 2 ;;
        --results) results_dir="${2:-}"; shift 2 ;;
        --log-dir) log_dir="${2:-}"; shift 2 ;;
        --timeout-seconds) timeout_seconds="${2:-}"; shift 2 ;;
        --shutdown-timeout-seconds) shutdown_timeout_seconds="${2:-}"; shift 2 ;;
        --cleanup-instance-on-exit) cleanup_instance=1; shift ;;
        --coexist-combat-solver) coexist=1; shift ;;
        --probe-only) probe_only=1; shift ;;
        --no-launch) no_launch=1; shift ;;
        --full-combat) full_combat=1; shift ;;
        --search-tier) search_tier="${2:-}"; shift 2 ;;
        --game-root) game_root="${2:-}"; shift 2 ;;
        --ritsu-root) ritsu_root="${2:-}"; shift 2 ;;
        --mod-dir) mod_dir="${2:-}"; shift 2 ;;
        --seed-data) seed_data="${2:-}"; shift 2 ;;
        --offline-host-dll) offline_host="${2:-}"; shift 2 ;;
        --combat-solver-dir) combat_solver_dir="${2:-}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) die "未知参数：$1" ;;
    esac
done

[[ -n $instance ]] || die "缺少 --instance。"
[[ $instance =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$ ]] || die "非法实例名：$instance"
[[ $timeout_seconds =~ ^[1-9][0-9]*$ ]] || die "--timeout-seconds 必须是正整数"
[[ $shutdown_timeout_seconds =~ ^[1-9][0-9]*$ ]] || die "--shutdown-timeout-seconds 必须是正整数"
[[ -x $game_root/SlayTheSpire2 ]] || die "找不到游戏：$game_root/SlayTheSpire2"
[[ -f $mod_dir/Sts2Solver.json && -f $mod_dir/Sts2Solver.dll ]] || die "找不到打包好的 Mod：$mod_dir"
[[ -d $seed_data/SlayTheSpire2/default/1 ]] || die "种子数据缺少 settings：$seed_data"
command -v jq >/dev/null || die "需要 jq"
command -v realpath >/dev/null || die "需要 realpath"

instance_root="$(realpath -m -- "$instance_root")"
failure_root="$(realpath -m -- "$failure_root")"
[[ -z $log_dir ]] || log_dir="$(realpath -m -- "$log_dir")"
instance_dir="$instance_root/$instance"
instance_dir="$(realpath -m -- "$instance_dir")"
for protected in / "$HOME" "$repo_root" "$game_root" "$instance_root"; do
    protected="$(realpath -m -- "$protected")"
    [[ $instance_dir != "$protected" && $protected != "$instance_dir/"* ]] \
        || die "拒绝使用受保护目录作为实例：$instance_dir"
done
[[ $instance_dir != "$repo_root/"* ]] || true

if [[ -e $instance_dir ]]; then
    if [[ -f $instance_dir/.sts2-solver-headless-owned ]]; then
        info "removing previous owned instance $instance_dir"
        rm -rf -- "$instance_dir" || die "无法删除旧实例：$instance_dir"
    else
        die "实例目录已存在且不是本脚本创建的：$instance_dir（请手动检查或换 --instance）"
    fi
fi

if [[ -n $requests_dir || ${#request_files[@]} -gt 0 || ${#roots[@]} -gt 0 || -n ${roots_file:-} ]]; then
    :
elif (( probe_only == 0 )); then
    die "需要 --roots-file/--root/--requests/--request 之一。"
fi

mkdir -p -- "$instance_dir"/{game,data,config,cache,run/requests,run/results} || die "无法创建实例目录"
printf '%s\n' "$instance_dir" >"$instance_dir/.sts2-solver-headless-owned" || die "无法写实例标记"

game_pid=""
log_path=""
launcher_log=""
game_exit_status=""
game_exit_description=""
game_exit_recorded=0
failure_reason=""
failure_artifacts=""

describe_game_status() {
    local status=$1
    if (( status > 128 )); then
        local signal=$((status - 128))
        local name
        name="$(kill -l "$signal" 2>/dev/null || true)"
        echo "code=$status signal=${name:-$signal} signal_number=$signal"
    else
        echo "code=$status"
    fi
}

record_game_exit() {
    (( game_exit_recorded == 0 )) || return 0
    [[ -n $game_pid ]] || return 0
    local status
    wait "$game_pid" 2>/dev/null
    status=$?
    game_exit_recorded=1
    game_exit_status=$status
    game_exit_description="$(describe_game_status "$status")"
    game_pid=""
    local invalid_task_ids=0
    local build_invalid_task_ids=0
    if [[ -f $log_path ]]; then
        invalid_task_ids="$(grep -c 'Invalid Task ID' -- "$log_path" 2>/dev/null || true)"
        [[ -n $invalid_task_ids ]] || invalid_task_ids=0
        build_invalid_task_ids="$(awk '
            /\[Sts2Solver\] BUILD_STARTED/ { in_build = 1 }
            /\[Sts2Solver\] BUILD_COMPLETED/ { in_build = 0 }
            in_build && /Invalid Task ID/ { count++ }
            END { print count + 0 }
        ' "$log_path" 2>/dev/null || true)"
        [[ -n $build_invalid_task_ids ]] || build_invalid_task_ids=0
    fi
    if [[ -d $instance_dir/run ]]; then
        printf 'status=%s description=%s\n' "$status" "$game_exit_description" \
            >"$instance_dir/run/game-exit.txt" 2>/dev/null || true
    fi
    echo "HEADLESS_GAME_EXIT $game_exit_description"
    echo "HEADLESS_EXIT $game_exit_description"
    echo "HEADLESS_GODOT_ERROR invalid_task_id=$invalid_task_ids build_invalid_task_id=$build_invalid_task_ids"
}

terminate_game() {
    [[ -n $game_pid ]] || return 0
    if kill -0 "$game_pid" 2>/dev/null; then
        kill -TERM -- "-$game_pid" 2>/dev/null || kill -TERM "$game_pid" 2>/dev/null || true
        for _ in $(seq 1 50); do
            kill -0 "$game_pid" 2>/dev/null || break
            sleep 0.1
        done
        if kill -0 "$game_pid" 2>/dev/null; then
            kill -KILL -- "-$game_pid" 2>/dev/null || kill -KILL "$game_pid" 2>/dev/null || true
        fi
    fi
    record_game_exit
}

preserve_failure_evidence() {
    local reason=$1
    local launcher_exit=$2
    [[ -z $failure_artifacts ]] || return 0
    local stamp
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    local destination="$failure_root/$instance-$stamp"
    local suffix=1
    while [[ -e $destination ]]; do
        destination="$failure_root/$instance-$stamp-$suffix"
        suffix=$((suffix + 1))
    done
    mkdir -p -- "$destination/run" || {
        echo "run-headless: 无法创建失败证据目录：$destination" >&2
        return 1
    }
    if [[ -d $instance_dir/run ]]; then
        cp -a -- "$instance_dir/run/." "$destination/run/" 2>/dev/null || true
    fi
    [[ ! -f $log_path ]] || cp -a -- "$log_path" "$destination/godot-headless.log" 2>/dev/null || true
    [[ ! -f $launcher_log ]] || cp -a -- "$launcher_log" "$destination/launcher.log" 2>/dev/null || true
    {
        printf 'instance=%s\n' "$instance"
        printf 'reason=%s\n' "$reason"
        printf 'launcher_exit=%s\n' "$launcher_exit"
        printf 'game_exit=%s\n' "${game_exit_description:-not_recorded}"
        printf 'instance_dir=%s\n' "$instance_dir"
    } >"$destination/failure.txt" 2>/dev/null || true
    failure_artifacts="$destination"
    echo "HEADLESS_FAILURE_ARTIFACTS path=$failure_artifacts"
}

copy_run_logs() {
    [[ -n $log_dir ]] || return 0
    mkdir -p -- "$log_dir" || {
        echo "run-headless: 无法创建日志目录：$log_dir" >&2
        return 1
    }
    [[ ! -f $log_path ]] || cp -a -- "$log_path" "$log_dir/godot-headless.log" 2>/dev/null || true
    [[ ! -f $launcher_log ]] || cp -a -- "$launcher_log" "$log_dir/launcher.log" 2>/dev/null || true
    [[ ! -f $instance_dir/run/mod.log ]] || cp -a -- "$instance_dir/run/mod.log" "$log_dir/mod.log" 2>/dev/null || true
    [[ ! -f $instance_dir/run/game-exit.txt ]] || cp -a -- "$instance_dir/run/game-exit.txt" "$log_dir/game-exit.txt" 2>/dev/null || true
    echo "HEADLESS_LOGS path=$log_dir"
}

print_failure_logs() {
    if [[ -f $launcher_log ]]; then
        echo "HEADLESS_LAUNCHER_LOG_TAIL path=$launcher_log lines=200" >&2
        tail -200 -- "$launcher_log" >&2 || true
    else
        echo "HEADLESS_LAUNCHER_LOG missing path=$launcher_log" >&2
    fi
    if [[ -f $log_path ]]; then
        echo "HEADLESS_GODOT_LOG_TAIL path=$log_path lines=100" >&2
        tail -100 -- "$log_path" >&2 || true
    else
        echo "HEADLESS_GODOT_LOG_TAIL missing path=$log_path" >&2
    fi
}

cleanup() {
    local code=$?
    trap - EXIT INT TERM
    if [[ -n $game_pid ]]; then
        [[ -n $failure_reason ]] || failure_reason="launcher_terminated"
        terminate_game
    fi
    if (( code != 0 )); then
        [[ -n $failure_reason ]] || failure_reason="launcher_exit_code_$code"
        print_failure_logs
        preserve_failure_evidence "$failure_reason" "$code"
    fi
    copy_run_logs
    if (( cleanup_instance == 1 )) && [[ -d $instance_dir ]]; then
        rm -rf -- "$instance_dir" || {
            echo "run-headless: failed to remove instance: $instance_dir" >&2
            code=1
        }
        [[ -d $instance_dir ]] || echo "INSTANCE_REMOVED path=$instance_dir"
    fi
    exit "$code"
}
trap cleanup EXIT
trap 'failure_reason=interrupted; exit 130' INT
trap 'failure_reason=terminated; exit 143' TERM

info "copying game to $instance_dir/game"
cp -aL --reflink=auto -- "$game_root/." "$instance_dir/game/" || die "复制游戏失败"
rm -rf -- "$instance_dir/game/mods" || die "无法清理实例 mods"
mkdir -p -- "$instance_dir/game/mods/STS2-RitsuLib" "$instance_dir/game/mods/Sts2Solver" \
    || die "无法创建实例 mods"

[[ -f $ritsu_root/STS2-RitsuLib.dll && -f $ritsu_root/mod_manifest.json ]] \
    || die "找不到 RitsuLib 捆绑：$ritsu_root"
cp -a -- "$ritsu_root/." "$instance_dir/game/mods/STS2-RitsuLib/" || die "复制 RitsuLib 失败"
mv -- "$instance_dir/game/mods/STS2-RitsuLib/mod_manifest.json" \
    "$instance_dir/game/mods/STS2-RitsuLib/STS2-RitsuLib.json" || die "写 RitsuLib 清单失败"
rm -f -- "$instance_dir/game/mods/STS2-RitsuLib/mod_manifest.json"

cp -a -- "$mod_dir/." "$instance_dir/game/mods/Sts2Solver/" || die "复制 Sts2Solver 失败"
[[ -f $instance_dir/game/mods/Sts2Solver/Sts2Solver.dll &&
   -f $instance_dir/game/mods/Sts2Solver/Sts2Solver.json ]] || die "Sts2Solver Mod 不完整"

if (( coexist == 1 )); then
    [[ -f $combat_solver_dir/CombatSolver.json && -f $combat_solver_dir/CombatSolver.dll ]] \
        || die "找不到 CombatSolver：$combat_solver_dir"
    mkdir -p -- "$instance_dir/game/mods/CombatSolver"
    cp -a -- "$combat_solver_dir/." "$instance_dir/game/mods/CombatSolver/" || die "复制 CombatSolver 失败"
fi

info "copying seed data"
cp -a -- "$seed_data/." "$instance_dir/data/" || die "复制种子数据失败"
settings="$instance_dir/data/SlayTheSpire2/default/1/settings.save"
[[ -f $settings ]] || die "种子数据缺少 settings.save：$settings"
settings_tmp="$(mktemp --tmpdir="$instance_dir" .settings.XXXXXX)" || die "mktemp 失败"
jq '.mod_settings = {mods_enabled: true, mod_list: []}
    | .volume_master = 0 | .volume_bgm = 0 | .volume_sfx = 0 | .volume_ambience = 0' \
    "$settings" >"$settings_tmp" || die "更新 settings.save 失败"
mv -f -- "$settings_tmp" "$settings"

requests_out="$instance_dir/run/requests"
if [[ -n ${roots_file:-} ]]; then
    [[ -f $roots_file ]] || die "找不到 --roots-file：$roots_file"
    while IFS= read -r line; do
        line="${line%%#*}"
        line="$(printf '%s' "$line" | tr -d '[:space:]')"
        [[ -z $line ]] || roots+=("$line")
    done <"$roots_file"
fi
if ((${#roots[@]} > 0)); then
    [[ -f $offline_host ]] || die "找不到 Differential.dll：$offline_host（先构建 tests/Differential）"
    roots_tmp="$instance_dir/run/roots.txt"
    printf '%s\n' "${roots[@]}" >"$roots_tmp" || die "写根列表失败"
    info "emitting ${#roots[@]} request(s)"
    emit_args=(--emit-live-requests "$requests_out" --roots-file "$roots_tmp")
    ((full_combat)) && emit_args+=(--full-combat)
    [[ -n $search_tier ]] && emit_args+=(--search-tier "$search_tier")
    dotnet "$offline_host" "${emit_args[@]}" \
        >"$instance_dir/run/emit-requests.log" 2>&1 || {
        cat "$instance_dir/run/emit-requests.log" >&2
        die "生成请求失败"
    }
    rm -f -- "$requests_out/manifest.json"
fi
if [[ -n $requests_dir ]]; then
    [[ -d $requests_dir ]] || die "找不到 --requests：$requests_dir"
    for file in "$requests_dir"/*.json; do
        [[ -e $file ]] || continue
        [[ $(basename -- "$file") == manifest.json ]] && continue
        cp -- "$file" "$requests_out/" || die "复制请求失败：$file"
    done
fi
for file in "${request_files[@]}"; do
    [[ -f $file ]] || die "找不到 --request：$file"
    cp -- "$file" "$requests_out/" || die "复制请求失败：$file"
done
shopt -s nullglob
prepared=("$requests_out"/*.json)
shopt -u nullglob
if (( probe_only == 0 )); then
    ((${#prepared[@]} > 0)) || die "没有请求文件"
fi
for file in "${prepared[@]}"; do
    base="$(basename -- "$file")"
    [[ $base =~ ^[A-Za-z0-9][A-Za-z0-9._-]*\.json$ ]] || die "请求文件名非法：$base"
done

info "instance ready: $instance_dir (requests=${#prepared[@]})"
if (( no_launch == 1 )); then
    echo "INSTANCE_PREPARED path=$instance_dir"
    exit 0
fi

log_path="$instance_dir/godot-headless.log"
launcher_log="$instance_dir/launcher.log"
(
    cd -- "$instance_dir/game" || exit 1
    exec setsid env \
        DISPLAY= WAYLAND_DISPLAY= \
        XDG_DATA_HOME="$instance_dir/data" \
        XDG_CONFIG_HOME="$instance_dir/config" \
        XDG_CACHE_HOME="$instance_dir/cache" \
        STS2_SOLVER_TEST_DIR="$instance_dir/run" \
        "$instance_dir/game/SlayTheSpire2" \
        --headless --disable-vsync --max-fps 0 --force-steam=off --log-file "$log_path"
) </dev/null >"$launcher_log" 2>&1 &
game_pid=$!
info "started headless game pid=$game_pid"

deadline=$((SECONDS + timeout_seconds))
if (( probe_only == 1 )); then
    ok=0
    while (( SECONDS < deadline )); do
        if ! kill -0 "$game_pid" 2>/dev/null; then break; fi
        if grep -q "\[Sts2Solver\] INIT id=Sts2Solver" "$log_path" 2>/dev/null \
            || grep -q "\[Sts2Solver\] DISABLED reason=CombatSolver" "$log_path" 2>/dev/null; then
            ok=1
            break
        fi
        sleep 0.2
    done
    if (( ok == 0 )); then
        die "等待 Mod 初始化日志超时"
    fi
    if (( coexist == 1 )); then
        grep -q "\[Sts2Solver\] DISABLED reason=CombatSolver" "$log_path" \
            || die "共存检查失败：没有 DISABLED 日志"
        if grep -q "\[Sts2Solver\] INIT id=Sts2Solver" "$log_path"; then
            die "共存检查失败：CombatSolver 存在时仍然 INIT"
        fi
        if grep -q "\[Sts2Solver\] TEST_STARTED" "$log_path"; then
            die "共存检查失败：CombatSolver 存在时启动了测试模式"
        fi
        [[ ! -f $instance_dir/run/mod.log ]] || die "共存检查失败：仍写了测试日志"
        grep -m1 "\[Sts2Solver\] DISABLED" "$log_path" || true
        echo "COEXISTENCE check=passed instance=$instance"
    else
        grep -q "\[Sts2Solver\] INIT id=Sts2Solver" "$log_path" \
            || die "Mod 初始化日志缺失"
        grep -m1 "\[Sts2Solver\] INIT" "$log_path" || true
        echo "MOD_LOAD check=passed instance=$instance"
    fi
    exit 0
fi

done_path="$instance_dir/run/results/done.json"
while (( SECONDS < deadline )); do
    if [[ -f $done_path ]] && jq -e '.schemaVersion == 1' "$done_path" >/dev/null 2>&1; then
        break
    fi
    if ! kill -0 "$game_pid" 2>/dev/null; then
        record_game_exit
        die "无头游戏在写 results/done.json 前退出"
    fi
    sleep 0.2
done
[[ -f $done_path ]] || die "等待 results/done.json 超时（${timeout_seconds}s）"

if ! grep -q "\[Sts2Solver\] INIT id=Sts2Solver" "$log_path"; then
    die "Mod 未初始化：日志中没有 INIT"
fi
if grep -q "Exception thrown while loading mod Sts2Solver" "$log_path"; then
    die "Mod 加载异常"
fi

failed="$(jq -r '.failed // 0' "$done_path")"
ok_count="$(jq -r '.ok // 0' "$done_path")"
total_ms="$(jq -r '.totalMs // 0' "$done_path")"
echo "HEADLESS_DONE instance=$instance ok=$ok_count failed=$failed total_ms=$total_ms"
jq -c '.results[] | {root,status,elapsedMs,error}' "$done_path"

if [[ -n $results_dir ]]; then
    mkdir -p -- "$results_dir" || die "无法创建 --results：$results_dir"
    cp -a -- "$instance_dir/run/results/." "$results_dir/" || die "复制结果失败"
    info "results copied to $results_dir"
fi
info "mod log: $instance_dir/run/mod.log"
info "game log: $log_path"
# done.json describes the tests; success also requires the game's own shutdown to finish.
shutdown_deadline=$((SECONDS + shutdown_timeout_seconds))
while kill -0 "$game_pid" 2>/dev/null && (( SECONDS < shutdown_deadline )); do
    sleep 0.1
done
if kill -0 "$game_pid" 2>/dev/null; then
    die "游戏写出结果后 ${shutdown_timeout_seconds} 秒仍未退出"
fi
record_game_exit
game_exit=$game_exit_status
(( game_exit == 0 )) || die "无头游戏异常退出（${game_exit_description}）"
(( failed == 0 )) || exit 1
exit 0
