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

## 回合准备枚举

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate <root> [--setup-turn 1|2]
```

在 `SetupPlayerTurn` 入口导入 Start 根，枚举全部准备计划，再按计划逐一重建原生准备页比较 Play 根；输出 `SETUP_ENUM`/`ENUM`/`PLAN`/`SETUP_NATIVE`。冻结 fixture 还会比较 `config.br` 的数据段、Bound 取舍、生产请求子部件、短搜重放与 `golden.br` 根集合。`--setup-turn 2` 先结束第 1 回合，用于后续回合、BeforeHandDraw/AfterPlayerTurnStart 多段和 AutoPrePlay 选择。

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

质量目录本身就是集合：`--list-quality-roots` 枚举 `tests/corpora/quality/` 下带 `request.json` 的子目录，共 108 个，不启动战斗。`--offline-combat` 不写 `--root`/`--roots-file` 时使用同一组根；自定义 `--corpora` 目录同样按其中的 `request.json` 枚举。

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --list-quality-roots
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat --corpora tests/corpora --jobs 4 \
  --out .local/offline-combat/default
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat --root quality-DEFECT-000 \
  --search-tier Medium --out .local/offline-combat/one
# 计时研究：恢复生产墙钟截止（结果会随机器负载变化）
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat --root quality-DEFECT-000 \
  --wall-clock --out .local/offline-combat/one-wall
```

不启动 Godot，用与 Mod 相同的 `CombatPilot` 在内核里打完一个根。每根写 `<out>/<root>/full-combat.json`，父进程写 `<out>/summary.json`。`--search-tier` 默认 `Medium`；`--memory-budget-mb` 或 `--memory-budget-bytes` 覆盖搜索内存预算（默认按 16 GiB 机器的 15% 计）。实验结果写进 `--out` 指定的本地目录，不提交。

默认成员预算是可复现的固定节点上限：每个成员最多展开 `宽度 × 512` 个节点，协调器的成员截止和校验截止读一个不前进的时钟，因此搜索不看墙钟，两个并发进程在同一台满载机器上也得到相同的展开数和路线；成员内部固定单线程，`--search-threads` 只能为 1。`--wall-clock` 改用生产预算（每宽度 50 ms 的软时间和墙钟截止，加上 `宽度 × 1024` 的节点上限，先到先停），只用于计时研究，该模式可用 `--search-threads N`（默认 1）。`summary.json` 和每局 `full-combat.json` 的 `searchBudget` 记录本次预算：可复现模式为 `mode=reproducible`、`maxExpandedNodesPerWidth=512`、`searchThreads=1`，墙钟模式为 `mode=wall-clock`、`millisecondsPerWidth=50`、`maxExpandedNodesPerWidth=1024`。

可复现模式的节点预算按 Medium 墙钟基线标定：整场加权展开速度约为 11.3 节点/ms，50 ms/宽度约合 565 节点/宽度；取 512 后，基线上所有在墙钟截止前自然结束的成员都低于该预算。固定预算是可跨机器比较的确定性近似，不声明与任意机器上的生产计时逐局相同。

`--requests-file <file>` 直接运行 sts2-sim 导出的 `OfflineRequest` JSON，不走质量语料的目录/spec 查找。文件每行是 `<request.json>` 或 `<结果目录名>=<request.json>`；相对路径按列表文件所在目录解析。名称必须唯一、是合法目录名，默认取文件名（不含扩展名）。父进程为每个请求启动一个子进程，`--jobs` 控制并发；每行的名称就是 `<out>/<名称>/full-combat.json` 的目录名。单个请求也可以只给 `--root <名称>`，但必须同时给同一份列表。

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat \
  --requests-file .local/sts2-sim-sample.txt --jobs 8 --out .local/sts2-sim-eval
```

`tools/testing/sample-sts2-sim.py` 按牌组中的卡牌 id 从 sts2-sim 导出树抽样（默认每张最多 25 局、按 `scenarioId` 去重并尽量覆盖不同幕和遭遇），输出上述列表格式；`tools/testing/compare-offline-results.py` 比较两次离线整场结果目录的胜负和失血，并按角色/牌汇总。两个脚本的完整用法见 [测试与验证](../../docs/testing.md#71-离线整场自动战斗)。

## 补录 D2 成员请求

`--capture-d2 <目录>` 在给定根上建出生产开局，只跑第 1 回合的一次生产搜索，把 `CombatSearchCoordinator` 实际发出的每个成员 `SearchCase` 按当前 `SREQ` 编码写成 `<目录>/<root>/requests/<sha256>.request.br`，同时写 `<目录>/<root>/capture.json` 和汇总 `<目录>/capture-summary.json`。成员搜索使用 D2 回归预算（去掉墙钟、固定节点/内存上限），所以同一输入重复录入得到同样的请求；协调器的组合、药水阶段、女王与准备补搜成员仍走生产计划。

```bash
# 单个根
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat \
  --capture-d2 .local/d2-capture --root tw-thieving-hopper --search-tier Medium

# 多个根；每个根一个子进程，输出目录可复用，capture-summary.json 汇总已有根
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat \
  --capture-d2 .local/d2-capture --roots-file .local/d2-capture-roots.txt --jobs 1
```

不同根需要不同生产设置时分次运行，后一次不会删除前一次的根目录。入口只处理第 1 回合的普通 Play 根；开局就带回合准备选择的根会显式失败。当前补录使用的根和设置：

| 分支 | 根 | 设置来源 |
| --- | --- | --- |
| `TheftPolicy.PreserveResources` | `tw-thieving-hopper`（`THIEVING_HOPPER_WEAK`） | 根上自动判定：`SearchPolicyCapture.ResolveTheftPolicy` 对 ThievingHopper / GremlinMerc 返回 `PreserveResources`；生产控制器 `CombatSessionController.BeginCombat` 对 `IsTheftApplicable` 的根也初始化为 `PreserveResources`。 |
| `PreserveReplayAllocatorOpening` | `tw-throwing-axe` | 根上有未使用的 `THROWING_AXE`；`SearchRequestCompiler.HasUnusedCardReplayAllocator` 为真时写入。 |
| `BrightestFlameMaxHpLossLimit` | `ec-c-ancient` | `--brightest-flame-max-hp-loss-limit 2` 映射生产设置 `SolverSettingsData.Search.BrightestFlameMaxHpLossLimit`（默认 `null`，范围 0..1000）。 |
| `SearchEvaluation.Setup` | `a3-queen-boss`、`tw-queen-setup-stages` | 生产计划对首领/第三幕精英打开 `SetupStrategies`；两个根都是 Queen 首领开局。 |
| `SearchEvaluation.ContinuousSetup` | `tw-queen-setup-stages` | 只在一个用药阶段加入；该根带一瓶药水，并用 1 点生命让第一遍搜索以失败路线结束后进入用药阶段。 |
| `QueenSearchPolicy` | `a3-queen-boss`、`tw-queen-setup-stages` | `QueenSearchPolicy.Capture` 要求第 1 回合、`QUEEN` + `TORCH_HEAD_AMALGAM` 两个敌人；两个根都满足。 |

`tw-throwing-axe` 和 `tw-queen-setup-stages` 是按现有 fixture 写法新增的最小开局；前者只注入未使用的投掷斧，后者只保留 1 点生命、一瓶 `FIRE_POTION` 与一张 `INFLAME`，让协调器完整走到两个用药阶段，搜索本身仍然很小。

把捕获目录并入 D2 时，用 `Golden refreeze` 的 `--add-requests`：

```bash
dotnet run --project tests/Golden -c Release --no-build -- \
  refreeze tests/Golden/data .local/golden-refreeze --jobs 8 \
  --add-requests .local/d2-capture --reason "<补录原因>"
```

`--add-requests` 会按 `capture.json` 累计捕获调用数，把新请求按编码后的 SHA-256 去重写入 `d2/requests`，补上 `rules-index`，再重算全部期望结果并重写 manifest。已存在的请求和期望结果不改写；旧请求的期望结果逐字节不变。

## 原版逐动作与终局对照

```bash
# 当前质量语料，CombatPilot 低档；默认并行 8 个子进程
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare

# 单根；终局有差异时退出码为 1
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare --pilot-root quality-IRONCLAD-001

# 对一个目录里的 OfflineRequest 逐个对照，并写逐用例 TSV
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare \
  --pilot-dir .local/sts2-sim-export --pilot-jobs 4 --pilot-shard 0 2 \
  --pilot-results .local/sts2-sim-diff/results.tsv

# 单请求默认使用请求里的 searchTier；根 fixture/pilot-dir 仍默认 Low。可用 --pilot-tier 覆盖
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare \
  --pilot-request .local/evidence/requests/FS1-REGENT-A10-D413F4C2-Act2-F049-Boss-QUEEN_BOSS-31.json
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare \
  --pilot-request .local/evidence/requests/FS1-REGENT-A10-D413F4C2-Act2-F049-Boss-QUEEN_BOSS-31.json \
  --pilot-tier Medium
```

用原版创建战斗，低档组合搜索后通过原版动作执行，核对每个稳定边界与终局合同。`--pilot-dir` 目录里的每个 `*.json` 是一份请求，仍然一请求一子进程；`--pilot-shard i n` 和 `--pilot-results` 与按根名运行时含义相同。子进程入口是 `--pilot-request <文件>`。它和 `--offline-combat` 的内核回放评测是两回事。

## 输入

- fixture：`tests/corpora/fixtures/<root>.json`；
- 质量语料：`tests/corpora/quality/<name>/spec.json` 与 `request.json`，根名为 `quality-<name>`。

请求 DTO 只覆盖冻结输入、生成场景规格与评估政策实际用到的字段，其中包括会影响政策的覆盖项（`growthBudgets`、`ignoreGrowthRewards`、两个 Boss 策略、`theftPolicyForTest`、`potionDefaultModeForTest`、`potionDirectivesForTest`、`act3BossStrategyForTest`）。未知字段和不支持的搜索或部署取值都显式失败。
