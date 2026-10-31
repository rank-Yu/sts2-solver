# 原版战斗差分宿主

在普通 .NET 进程里初始化原版战斗，核对导入、规则、搜索配置与动作结果。不包含局外的地图、奖励或商店执行。每项检查证明什么、不证明什么，见 [测试与验证](../../docs/testing.md#7-差分宿主)；本文是命令清单。

## 准备

需要本机游戏安装目录，至少包含 `sts2.dll`、`GodotSharp.dll`、`0Harmony.dll`。路径写在本目录下被 git 忽略的 `local.props` 里，模板是 `local.props.example`；`local.props` 不要提交。

```bash
dotnet build tests/Differential/Differential.csproj -c Release
```

产物在 `artifacts/bin/Differential/release/Differential.dll`。构建不会把游戏文件复制进仓库。

## 建根与参考转储

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --root kg2-b-stomp --out .local/differential/out
dotnet artifacts/bin/Differential/release/Differential.dll --all --jobs 8 --out .local/differential/out
dotnet artifacts/bin/Differential/release/Differential.dll --compare-only .local/differential/out <参考转储目录>
```

每个根一个子进程，避免游戏静态状态跨根污染；`--jobs` 取 1 到 8。`--compare-only <new> <ref>` 逐根比较转储并打印第一处差异。参考转储不在仓库里，由 CombatSolver `flash/m0` 的 `fe428fca`（给 M0 离线宿主加了同一份 `NativeStateDump`）生成。

## 导入、规则与评估配置

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --root kg2-b-stomp --import-compare
dotnet artifacts/bin/Differential/release/Differential.dll --all --jobs 8 --import-compare
dotnet artifacts/bin/Differential/release/Differential.dll --all --jobs 8 --import-compare --compiled-rules
dotnet artifacts/bin/Differential/release/Differential.dll --root kg2-b-stomp --rules-compare
dotnet artifacts/bin/Differential/release/Differential.dll --all --jobs 8 --rules-compare
dotnet artifacts/bin/Differential/release/Differential.dll --rules-table-check
dotnet artifacts/bin/Differential/release/Differential.dll --root kg2-b-stomp --config-compare
dotnet artifacts/bin/Differential/release/Differential.dll --all --jobs 8 --config-compare
dotnet artifacts/bin/Differential/release/Differential.dll --import-negative
```

- `--import-compare`：建原生状态、导入 `Battle`，与 `d1/<root>/golden.br` 的期望 `BATL` 逐字节比较；`--compiled-rules` 改用 `RulesCompiler.Compile` 的输出，而不是解码冻结的规则对象。
- `--rules-compare`：比较 `RulesCodec.Encode` 的结果与冻结规则对象。
- `--rules-table-check`：检查镜像覆盖率静态表。
- `--config-compare`：比较重组的请求与 `config.br`，并检查 `scoreHash`、`boundHash`。
- `--import-negative`：验证私有字段缺失、未认领状态、第三方内容三类拒绝。

## 重新捕获根

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --root qf-nightmare-detached --recapture-root --out .local/recapture
dotnet artifacts/bin/Differential/release/Differential.dll --all --jobs 4 --recapture-root --out .local/recapture-all
```

按与 `--config-compare` 相同的导入、前缀回放、回合准备枚举路径重建根和 `config.br`，写出 `<out>/<root>/roots.gold` 与 `<out>/<root>/config.br`，交给 `Golden rerecord --recapture-root` 使用（见 [测试与验证](../../docs/testing.md#54-重新捕获根)）。

## 实机请求与对照

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --emit-live-requests .local/live-requests --all
dotnet artifacts/bin/Differential/release/Differential.dll --emit-live-requests .local/live-requests --roots-file .local/live-roots.txt
dotnet artifacts/bin/Differential/release/Differential.dll --live-compare .local/live-results
dotnet artifacts/bin/Differential/release/Differential.dll --search-compare .local/live-results
```

- `--emit-live-requests`：为 Mod 无头测试生成请求；需要 `--root`、`--roots-file` 或 `--all`。可加 `--search-tier`、`--search-normal`、`--search-cancel-after-ms N`、`--full-combat`。
- `--live-compare`：比较游戏写出的 `BATL`、`RULE`、`SREQ.capture`、`native.txt`。
- `--search-compare`：重解游戏的 `SREQ` 并比较 `SRES`，同时核对离线组装的请求。

启动脚本是 [`tools/testing/run-headless.sh`](../../tools/testing/run-headless.sh)。

## 离线整场自动战斗

默认质量集合由 `tests/corpora/quality/default-roots.txt` 指定；`--list-quality-roots` 只列出名单，不启动战斗。

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --list-quality-roots
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat --corpora tests/corpora --jobs 4 \
  --out .local/offline-combat/default
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat --root quality-DEFECT-000 \
  --search-tier Medium --out .local/offline-combat/one
```

不启动 Godot，用与 Mod 相同的 `CombatPilot` 在内核里打完一个根。每根写 `<out>/<root>/full-combat.json`，父进程写 `<out>/summary.json`。`--search-tier` 默认 `Medium`；`--memory-budget-mb` 或 `--memory-budget-bytes` 覆盖搜索内存预算（默认按 16 GiB 机器的 15% 计）。实验结果写进 `--out` 指定的本地目录，不提交。

## 原版逐动作与终局对照

```bash
# 当前质量语料，CombatPilot 低档；默认并行 8 个子进程
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare

# 单根；终局有差异时退出码为 1
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare --pilot-root quality-IRONCLAD-001
```

用原版创建战斗，低档组合搜索后通过原版动作执行，核对每个稳定边界与终局合同。它和 `--offline-combat` 的内核回放评测是两回事。

## 输入

- fixture：`tests/corpora/fixtures/<root>.json`；
- 质量语料：`tests/corpora/quality/<name>/spec.json` 与 `request.json`，根名为 `quality-<name>`。

请求 DTO 只覆盖冻结输入、生成场景规格与评估政策实际用到的字段，其中包括会影响政策的覆盖项（`growthBudgets`、`relicCounterRules`、`ignoreLongTermRewards`、两个 Boss 策略、`theftPolicyForTest`、`act3BossStrategyForTest`、`baseScoreOnlyForTest`、`continuousThreatRankingForTest`、`beamWeightTermForTest`/`beamWeightScaleForTest`）。未知字段、非空的 `checkpointArchivePath`/`replayPolicyOverridePath`，以及不支持的搜索或部署取值都显式失败。

## 兼容旧选项（是否保留待代码清理决定）

- `--offline-combat` 的 `--potion-schedule one-allowed|one-two-three|one-two-allowed`：改用旧的默认用药调度跑实验（见 [战斗搜索](../../docs/combat-ai.md#10-兼容旧请求的路径是否保留待代码清理决定)）；不指定时用现行默认计划。`--potion-schedule-checks` 单独运行这些调度的自检。
- `--offline-combat` 的 `--pool-state-budget-mb`：覆盖旧的单池状态预算。
- 旧的加宽与节点上限参数 `--beam-width`、`--max-width`、`--max-nodes`、`--nodes-per-width` 仍会被识别，但只为报错“已移除，请使用 `--search-tier`”。
