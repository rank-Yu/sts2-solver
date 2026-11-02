# 测试与验证

[返回文档索引](README.md)

本文说明仓库里有哪些检查、每一层证明什么、不证明什么，以及怎么运行。冻结快照的格式、来源和更新条件见 [冻结快照](golden-data.md)；差分宿主的完整命令清单见 [tests/Differential](../tests/Differential/README.md)。

## 1. 各层校验范围

| 层 | 校验内容 | 不覆盖 |
| --- | --- | --- |
| 构建与架构检查 | 编译、警告和依赖边界 | 运行时正确性 |
| 内核合同（K0） | 布局、随机数、编码、容量与注册表合同 | 完整搜索与实机部署 |
| 模拟回归 | 冻结输入的逐步重放与 K4 哈希 | 未捕获的状态、实机导入 |
| 搜索回归 | 冻结请求的完整确定性搜索结果 | 未捕获的请求、生产时间预算下的搜索质量 |
| manifest | 快照完整性与来源声明 | 语义正确性 |
| 差分宿主 | 选定输入的原生状态、规则、导入、请求、随机流与边界执行对照 | 界面、任意第三方内容 |
| Mod 无头捕获 | 实机捕获与冻结快照、离线重建的比较 | 路线执行、界面 |
| 实机确定性搜索 | 游戏与离线宿主的请求和结果比较 | 生产组合调度的搜索质量 |
| 实机整场 | 自动执行、逐动作核对、回合对账、选牌与取消清理 | 语料外内容、界面、未捕获的前缀与准备根 |

冻结回归、搜索质量评估和实机部署是不同的检查，一项通过不能替代其它项。冻结回归与基准一致只说明行为没变，不代表路线最优。

## 2. 环境

- .NET SDK 9.0.300 或更高的补丁版本，以 `global.json` 为准。
- 普通的 Linux 或 Windows .NET 控制台环境。内核合同、模拟回归、搜索回归都不需要游戏、Godot、Harmony、RitsuLib 或第三方 Mod。
- 所有命令在仓库根目录执行，`dotnet` 要在 `PATH` 上。

## 3. 日常验证

Linux/Bash 上的完整验证：

```bash
tools/testing/verify-core.sh --jobs 8
```

脚本先构建整个 `Sts2Solver.sln`，再依次运行 KernelChecks、Architecture、Golden K0 和完整的模拟回归、搜索回归，任一步失败就以非零退出。它会打印当前提交、工作区是否有未提交改动（`dirty` 时提交号不代表全部受测源码）、SDK、并行度和数据目录。默认并行度 8，不能跳过构建。审核候选快照时可以用 `--data-dir .local/<candidate>` 指定数据目录；默认始终验证 `tests/Golden/data`。

分步运行：

```bash
dotnet build Sts2Solver.sln -c Release
dotnet run --project tests/KernelChecks -c Release --no-build
dotnet run --project tests/Architecture -c Release --no-build
dotnet run --project tests/Golden -c Release --no-build -- k0
dotnet run --project tests/Golden -c Release --no-build -- verify tests/Golden/data
```

注意：`dotnet run --no-build` 用的是该测试项目产物目录里的 `Sts2Solver.Core.dll` 副本。只构建 `tests/Differential` 不会刷新 Golden 或 KernelChecks 的副本，所以改了 Core 之后必须重新构建整个 solution，再跑单项命令，不能把旧产物的通过结果当作当前源码的验证。

UI 文案键的静态检查单独运行：

```bash
python3 tools/testing/check-ui-keys.py
```

脚本按 C# 插值字符串规则解析 `SolverText.Get("…")` 和 `SolverText.Format($"…")`，同时读取 `SolverPanelText` 映射表的值；有键在用但 `English.json` 缺键，或目录键无人使用，都以非零退出。`ui-panel.json` 的运行时英文切换还会在缺键时记录 `LOCALE_KEY_MISSING`。

### 3.1 架构检查

```bash
dotnet run --project tests/Architecture -c Release --no-build
```

检查编译后 Core 的分层引用：Simulation 不依赖上层，Content 和 Ai 只依赖 Simulation，Autoplay 依赖 Simulation 和 Ai；同时检查 Core 不含局外模块。正反例确保允许的引用通过、禁止的引用会被报告。依赖方向见 [文档索引](README.md#分层)。构建 Mod 后还可以检查正式包和测试包的边界，命令见 [Mod 安装与构建](mod.md#两种构建)。

### 3.2 内核合同（K0）

```bash
dotnet run --project tests/KernelChecks -c Release --no-build
dotnet run --project tests/Golden -c Release --no-build -- k0
```

- `tests/KernelChecks` 输出 `"contracts": "Passed"`。它检查 `Battle`、`Card`、`Enemy`、`Power`、`Orb`、`RngState` 的字节尺寸与牌区偏移；随机数对照原版参考向量；洗牌排序；牌堆操作与容量失败；`CostState` 各位互不干扰；选择协议、注册表与编码合同；成长计数的打包；TheGambit 让玩家死亡后不再调用后续玩家监听、LizardTail 阻止死亡后监听照常执行；以及 `Exec` 字段覆盖合同（见 4.3）。
- `Golden k0` 输出形如 `K0 Passed fields=30 battleBytes=27320 formatVersion=3 magics=BATL,RULE,GOLD,SREQ,SRES`。它检查 `Rules` 的 30 个字段都进入编码、`decode(encode(x))` 字节不变、`Unsafe.SizeOf<Battle>()` 符合预期、五个魔数按小端字节解出的 ASCII 与格式标签一致。

K0 证明内核的布局、编码与纯函数合同在只有 BCL 的环境下成立；不证明搜索语义、冻结输入的求值路径或实机状态。

## 4. 模拟回归与搜索回归

Golden 数据是事先录好的标准答案，用来发现行为变化，不代表路线最优。它分两部分：

- **模拟回归**（数据目录 `d1`）：在固定的战斗状态上执行固定动作，检查得到的状态、合法动作、选择请求和评分哈希是否不变。
- **搜索回归**（数据目录 `d2`）：把固定的搜索请求去掉时间限制后重跑，检查结果是否逐字段不变。

```bash
# 完整校验：先 manifest，再模拟回归和搜索回归
dotnet run --project tests/Golden -c Release --no-build -- verify tests/Golden/data

# 只跑一层
dotnet run --project tests/Golden -c Release --no-build -- verify tests/Golden/data --only simulation --jobs 8
dotnet run --project tests/Golden -c Release --no-build -- verify tests/Golden/data --only search --jobs 8

# 分片
dotnet run --project tests/Golden -c Release --no-build -- verify tests/Golden/data --only search --jobs 8 --shard 0/4
```

`--jobs N` 控制并行度，默认是处理器数。`--search-threads N` 控制每个搜索请求内部同时展开的线程数，默认 1；它不改变确定性结果，只改展开速度。验证总并行度时两者相乘不要超过机器的可用核数。

`--workspace-buffer-bytes N` 把每个工作区的可变展开缓冲上限设成 N 字节（默认 8 MiB）。设成 1 会让所有父节点走直通模式，用于压测内存上界。`--workspace-stats` 收集每个父节点的缓冲峰值，打印分位数、超过 1/2/4/8/16 MiB 的个数和最大父节点的子节点构成；只在验证工具里使用。

`--shard i/n` 只跑第 i 片：

- 模拟回归按排好序的根目录做 `index % n == i`。
- 搜索回归按工作量均分。每个请求的权重是期望结果里记录的展开节点数（`ExpandedNodes`），这个数和机器无关。请求从重到轻依次分给当前最轻的分片，片内也是先跑最重的，所以各片耗时接近，分法也不受目录枚举顺序影响。平局时权重相同按请求哈希排序，分片总量相同取编号小的。
- 不指定 `--shard` 等于 `0/1`：全部请求，同样从重到轻处理。

完整 `verify` 的最后一行是 `GOLDEN_VERIFY ... mismatched=0`。解码失败或字段不一致输出 `GOLDEN_INVALID <原因>`，非预期异常输出 `GOLDEN_FAILED <类型>: <消息>`，两者都以非零退出。

### 4.1 模拟回归

输出 `SIMULATION_OK`，汇总文件、根、游走、状态、特征和差异的计数，失败时以非零退出。对每个根重放：

- `golden.br` 里的根状态、K1 单步动作与完整选择序列；
- K2 固定种子游走的每一步状态、合法动作顺序、选择请求与实际选择；
- 每个记录状态的 `scoreHash`、`boundStatus`、`boundHash`，每个转移的 `traitPresent`、`traitHash`；
- 规则对象 `objects/rules/*.br` 的编码一致性。

证明：冻结的输入在 Sts2Solver 上能逐字节重放，评分、下界和路线标签特征与录制时一致。不证明：未捕获的状态、实机导入、任意第三方内容。

### 4.2 搜索回归

对 `d2/requests/*.request.br` 的每个唯一请求：

1. 解码 `SREQ`（规则、战斗、搜索选项）；
2. 把软时间预算改为 `TimeSpan.MaxValue`，清空让帧器和追踪回调；
3. 运行 `BeamSolver.Solve`；
4. 与 `d2/expected/*.result.br` 逐字段比较：路线、分数的 double 位、展开/转移/去重计数、容量丢弃、结束回合池压缩、药水审计、`BestRootIndex` 和 `PotionPolicySatisfied`。

`SEARCH_OK` 还会输出 `playDeduped`、`playVerifiedExact/Approx`、`choiceByClassRequests`、`choiceVerifiedExact/Approx` 和未决计数；没开等价自检时 `*Verified*` 项为 0（见 4.3）。

每次搜索回归先打印一行 `SEARCH_SHARD`，列出当前分片的 `requests` 和 `weight`。

证明：冻结请求在去掉软时间后能复现完整搜索结果。不证明：请求集合之外的根、实机运行、界面与部署行为。

搜索回归通过 Core 的 `SearchBudgetPolicy.ApplyRegression` 注入固定参数：结束回合池保留宽度 × 8、触发宽度 × 16，状态池预算和搜索内存预算都是 2,576,980,377 字节。协调器用同一个 `SearchBudgetPolicy.Apply` 计算生产成员，回放和测试不另写公式。

### 4.3 搜索等价自检

搜索为了快，会共享结束回合的前缀、原地投影状态、跳过等价的出牌和选牌。自检用来确认这些优化没有改变结果。

**`Exec` 覆盖合同。** KernelChecks 的 `ExecCoverage` 用反射枚举 `Exec` 的全部实例字段（含私有字段），要求每个字段恰好归入一类：进入 `ExecContinuation`、捕获时断言为空或为零、在 `AdvanceRoundPlayerStart` 入口重置、单独处理（`B`、`R`、选择游标）。分类表只在 `tests/KernelChecks/Combat/ExecCoverage.cs` 一处，同时反向校验快照字段集合与 `ExecContinuation` 完全一致。出现未分类、重复归类或快照表不一致时，消息里写出字段名。

**运行时自检。** `BeamSolver` 默认使用共享的结束回合前缀。设置 `STS2_SOLVER_VERIFY_SEARCH_EQUIVALENCE=1` 后同时开启：

- 每个复用前缀的选择叶子同时执行完整 `Kernel.Step` 与前缀续跑，逐字节比较 `Battle`（不一致时用 `BattleDump.Diff` 输出字段差异），并比较 `StepResult`、`ChoiceRequest`（选项、来源、时机、生成卡字节）和 `HistoryRisk`；
- 每次 `Scorer.ProjectHpAfterThreat` 原地投影前保存 `Battle`，`finally` 恢复后逐字节比较前后；
- 每个被跳过的完全等价出牌，从父状态分别执行代表动作和被跳过的动作，按同一确定策略续完后续选择，逐字节比较 `Battle`；不同就抛 `同名出牌完全等价校验失败`；
- 每个选牌输出（类代表或补位的物理选择）取一个同类不同位置的物理选择比较；判定为完全等价的必须相同，判定为近似的只统计实际相同的比例。每个请求最多抽样 8 个输出，容量或选择深度失败的比较计为未决。

在副本上对全部搜索回归打开自检：

```bash
STS2_SOLVER_VERIFY_SEARCH_EQUIVALENCE=1 \
  dotnet run --project tests/Golden -c Release --no-build -- \
  verify .local/golden-copy --only search --jobs 6
```

完全等价的检查失败会直接报错；近似项和未决项只统计，用输出的计数判断实际覆盖范围。

## 5. 更新快照

快照默认冻结，什么情况下允许更新见 [冻结快照](golden-data.md#11-什么情况下允许更新快照)。下面的工具都只在副本上改数据，源目录只读；替换 `tests/Golden/data` 由维护者决定。工具具体改动哪些数据见 [冻结快照](golden-data.md#9-重录与重冻会改什么)。

### 5.1 `compare`：只报告，不判失败

```bash
dotnet run --project tests/Golden -c Release --no-build -- \
  compare tests/Golden/data --jobs 6 --config all --shard 0/4 \
  --out .local/compare-all-0.csv
```

改了会影响搜索结果的代码后，先用它看看哪些请求变好、变差。

- `--config none|play|choice|all|choice-fill|all-fill`：`none` 关闭两项去重；`play` 只开完全等价的出牌去重；`choice` 只开旧的选牌等价类（每个类向量一个代表）；`all` 是 `play + choice`；`choice-fill` 是类优先、再补位置变体；`all-fill` 是 `play + choice-fill`，也是正式默认。
- `--shard i/n`：与 `verify` 相同的权重分片；全量对照按 4 片运行。
- `--search-threads N`：每个请求内部的展开线程数，默认 1；结果应与单线程逐字节相同。
- `--out`：逐请求的 CSV，含质量分类、路线是否逐动作相同、展开/转移、分数、胜负、耗时和等价计数。
- `--verify`：对照时打开 `STS2_SOLVER_VERIFY_SEARCH_EQUIVALENCE` 的全部检查；精确等价断言失败仍以 `GOLDEN_FAILED` 退出，近似项只统计。
- 质量用 `BeamSolver.TryCompareResultQuality` 判断：把冻结路线和当前路线分别从同一根重放，按求解器自己的终局比较键（胜负、存活、死亡保护、战略失血、成长、回合、资源、可选药水、敌人生命、分数、动作数）分成更好、相同、更差；无法重放时计 `unknown`。
- 不修改 `tests/Golden/data`，也不更新 manifest。

### 5.2 `refreeze`：重算搜索回归的期望结果

```bash
dotnet run --project tests/Golden -c Release --no-build -- \
  refreeze tests/Golden/data .local/golden-refreeze --jobs 6
dotnet run --project tests/Golden -c Release --no-build -- \
  verify .local/golden-refreeze --jobs 6
```

`refreeze` 先拒绝源和目标互相包含或目标非空，再复制整棵数据目录，用当前代码重解全部搜索回归请求，最后重写 manifest。输出 `REFREEZE_OK`（源、目标、请求数和结果哈希）与 `MANIFEST_WRITTEN`。经用户同意后，再用副本替换仓库数据。

### 5.3 `rerecord`：重录模拟回归的根

语义修正后，旧的模拟回归录制会变成旧行为，需要用 Sts2Solver 自己重录。

```bash
# 1. 先在没改语义的代码上证明工具能复现旧录制
dotnet run --project tests/Golden -c Release --no-build -- \
  rerecord tests/Golden/data .local/rerecord-fidelity \
  --roots .local/rerecord-roots-all.txt --reason "fidelity check" --jobs 6
diff -r tests/Golden/data/d1 .local/rerecord-fidelity/d1

# 2. 语义修正后，只重录失配且能说清原因的根
dotnet run --project tests/Golden -c Release --no-build -- \
  rerecord tests/Golden/data .local/rerecord \
  --roots .local/rerecord-roots.txt --reason "<语义修正>" --jobs 6
dotnet run --project tests/Golden -c Release --no-build -- \
  verify .local/rerecord --only simulation --jobs 6
```

第 1 步是门槛：对全部根重录一遍，`golden.br` 必须和现有数据逐字节相同（计数没变时 `meta.json` 不会重写）。字节不同但解码后字段相同时，要说明是压缩层的原因。这一步不过，就不做语义修正后的重录。

第 2 步只对 `verify` 报出失配的根重录，其它根保持原样。每个根输出一行变化摘要：第一个失配位置（K1 下标，或种子与状态下标）、这一步的动作和卡牌/药水原生 id、变化的 K1 数、游走状态数与游走长度。摘要和归因表写进任务报告，清单由工具写入 `provenance.d1Rerecord`。

### 5.4 重新捕获根

`rerecord` 把根状态当作输入，只重算 K1、K2。如果语义修正改的正是“从原版状态导入或回放出的根”，旧的冻结根仍是旧行为，必须先用差分宿主重新捕获根和 `config.br`。适用条件：`--config-compare` 或 `--import-compare` 的差异落在根状态上，而且能归因到一次已确认的原版语义修正。不要用它掩盖 K1、K2 或搜索结果的偏差。

```bash
# 1. 差分宿主从原生状态重新导入或回放，写出载荷（roots.gold + config.br）
dotnet artifacts/bin/Differential/release/Differential.dll \
  --root qf-nightmare-detached --recapture-root \
  --data tests/Golden/data --corpora tests/corpora --out .local/recapture

# 2. 保真检查：全部根重新捕获，除已归因的根外，必须 rootBytes=same configBytes=same
dotnet artifacts/bin/Differential/release/Differential.dll \
  --all --jobs 4 --recapture-root \
  --data tests/Golden/data --corpora tests/corpora --out .local/recapture-all

# 3. Golden 用载荷替换根和 config.br，再按 M0 策略重算 K1、K2 与派生哈希
dotnet run --project tests/Golden -c Release --no-build -- \
  rerecord tests/Golden/data .local/rerecord-root \
  --roots .local/rerecord-root.txt \
  --recapture-root .local/recapture --reason "<语义修正>" --jobs 4
```

`--recapture-root` 必须同时给出 `--roots` 和 `--reason`。载荷目录按根名分子目录：`roots.gold` 是只含根段的 `GOLD`，`config.br` 是重新编译的 `SREQ`。Golden 不引用游戏程序集，只读取差分宿主产出的载荷，替换 `golden.br` 的根段与 `config.br`，其余输入映射、`rulesSha256` 和 K1/K2 规则不变。

## 6. manifest

```bash
dotnet run --project tests/Golden -c Release --no-build -- manifest tests/Golden/data
git diff --exit-code -- tests/Golden/data/manifest.json
```

`manifest` 按路径序数排序，逐文件写 `path`、`bytes`、`sha256`，并写格式版本、`battleSize`、`provenance`、压缩方式、计数和总字节，输出一行 `MANIFEST_WRITTEN` 汇总。它是确定性的：同一份数据重写后应逐字节相同，所以重写后 `git diff` 应为空。

证明：文件集合、大小、SHA-256、计数和来源声明自洽。不证明语义正确，它只防止冻结快照被悄悄改动。

## 7. 差分宿主

`tests/Differential` 是不启动 Godot 的控制台工程：它初始化游戏的静态状态，用游戏自己的 `RunManager`、`ModelDb` 建局，推进到玩家可操作，再拿原版的结果和 Sts2Solver 的导入、规则编译、评估配置和搜索请求逐字节比较。工程**不在** `Sts2Solver.sln` 里，CI 没有游戏文件，只保证 `dotnet build Sts2Solver.sln` 不受影响。

它需要本机的游戏目录（`sts2.dll`、`GodotSharp.dll`、`0Harmony.dll`），路径写在被 git 忽略的 `tests/Differential/local.props`（模板 `local.props.example`），不需要 RitsuLib。建局、注入与转储代码和 Mod 共用 `src/TestHost/`，不在两边复制。每个根一个子进程，避免游戏静态状态跨根污染。

下面每一项只写它证明什么；命令和参数见 [tests/Differential](../tests/Differential/README.md)。

| 检查 | 比较什么 | 结果 |
| --- | --- | --- |
| 建根（`--root`、`--all`）与 `--compare-only` | 原生 `CombatState` 的确定性转储（`NativeStateDump`）与参考转储 | 打印第一处差异 |
| 导入对照 `--import-compare` | 导入的 `Battle` 与 `golden.br` 里的 `BATL`；加 `--compiled-rules` 时改用规则编译器的输出 | 每根 `same`、`diff`（`BattleDump.Diff` 前 24 行）或 `reject`（稳定原因码） |
| 规则对照 `--rules-compare` | `RulesCodec.Encode` 的结果与冻结规则对象 | 每根 `same`、`diff`（`RulesDiff` 第一处表项及前 16 行）或 `reject` |
| 镜像覆盖率表 `--rules-table-check` | 静态表与 `ModelDb.All`、注册表 | 报告遗漏和多余的 id |
| 评估配置对照 `--config-compare` | 重组的 `SREQ` 与 `config.br`，依次比较规则、根、评分表、下界与其余选项；并用 `GoldenHash` 检查 `scoreHash`、`boundHash` | 打印第一处差异 |
| 负例 `--import-negative` | 在 `p8-c-rage-juggling-dark-embrace` 上制造三类问题：Power 内部数据置空、注入未认领的私有状态、挂上测试程序集里的 Power | 必须分别以 `missing-private-field`、`unclaimed-private-state`、`third-party-content` 拒绝，输出 `negative failures=0` |

`--all` 汇总 `same/diff/reject`，并按首个差异字段或拒绝原因分组计数。前缀根通过“导入前缀之前的状态 + 内核回放”比较；回合准备 fixture 按原生准备选项逐个重建，比较整份请求及全部准备根（见 [原生导入](import.md#8-前缀根与回合准备根)）。

`NativeStateDump` 还记录跑局和幕的信息：`run.ascension`、`run.act.index/count/id`、`run.act.boss/secondBoss`、`run.room.type/id`、`run.tutorial.discoveryOrder`、`run.unlock.*` 和 `rng.UpFront` 的计数与状态（`SecondBossEncounter` 缺失时写 `-`）。这些字段只用来核对建局路径和 `BossHpRelief` 用到的幕结构，不参与导入判定。

证明：不启动 Godot 时，冻结输入能建出与参考导出相同的原生战斗状态，导入后与冻结的 `BATL` 逐字节相同，编译出的 `Rules`、评分表与下界也与冻结数据相同；私有状态和第三方内容的负例会显式拒绝。不证明：实机部署、界面、准备阶段的多段选择，以及未捕获的第三方 Mod 内容。

### 7.1 离线整场自动战斗

离线工具从质量语料导入根，用与 Mod 相同的组合协调器和请求级时间、内存预算，在内核里执行搜索出的路线，用于质量评估。每根一个子进程，`--jobs` 控制并发，`--search-threads N` 控制每个成员内部的展开线程数（默认 1）；`--workspace-buffer-bytes N` 和 `--workspace-stats` 与 Golden 验证工具含义相同。

每根的 `full-combat.json` 记录胜负、重搜、执行失败，以及每次搜索的完整成员明细（执行或跳过、停止原因、展开数、路线质量、内存峰值）；父进程的 `summary.json` 汇总档位、四种宽度、时间和内存预算。墙钟控制的成员在不同机器上可能截断在不同位置，不能当确定性的回归来比较。默认质量集合和期望见 [tests/corpora/quality](../tests/corpora/quality/README.md)。

### 7.2 原版逐动作与终局对照

`--combat-pilot-compare` 检查内核预测和原版实际执行是否一致：

- 每个用例在独立子进程里，用与战斗捕获共用的 `ScenarioBuilder` 建立原版战斗；`CombatPilot` 低档生成路线，`NativePilotHost` 逐动作交给 `NativeActionExecutor`，用原版公开入口执行。
- 每个**可出牌**的动作边界，都用 `LiveCombatTracker.ImportForComparison` 读回 `Battle`，与预测的 `action.After` 逐字节比较。
- 终局另做独立取证：`TerminalBattleCapture` 在 `CombatManager.EndCombatInternal`（胜利）和 `ProcessPendingLoss`（失败）的 **prefix** 处捕获原版 `Battle`，再与预测终局比较。比较的字段是显式列出的：`Outcome`（由捕获来源的胜负路径判定，不由预测反推）、玩家 `CurrentHp`/`MaxHp`、`Gold`、药水槽、遗物的 `Id/Flags/Counter/Aux` 和 9 条战斗随机数。手牌和各牌堆、回合内计数、敌人和 Power 行在原版清理时会变化，不在合同里，所以不宣称终局 `Battle` 逐字节相同。合同内的字段都独立读取原版；导入器带的搜索专用计数不参与终局比较，也没有重新实现战后 Hook 或奖励生成。

结果：`status=same` 表示所有动作边界和终局合同都相同；`status=executor-diff` 表示内核预测与原版边界不同；`status=reject` 带稳定原因码。捕获缺失和等待超时报告为 `executor-diff`，原因分别是 `terminal_missing`、`boundary_timeout`；明确不支持的导入报告 `reject`，不计为 same。`EventDefeat` 目前以 `terminal_diff` 明确报告未覆盖，退出码为 1。单根（`--pilot-root`）有差异时退出码为 1。父进程先看子进程的退出码：非零的子进程即使打印了 `same` 或 `reject` 也不计入正常结果，有明确 `executor-diff` 的计为 diff，否则计为崩溃。`actions` 是实际部署的动作数，`turns` 是回合数。

负例：设置 `STS2_SOLVER_PILOT_TAMPER=hp`、`outcome` 或 `event` 篡改预测终局，检查分别报告 `player.hp`、`outcome`、`event-defeat is not covered`，退出码都为 1。

内核回放评测（7.1）通过不等于这项对照通过，两类报告要分开写。

## 8. 搜索内存与计时

搜索账本统计状态、路线节点、辅助数组和标签等逻辑存储；进程的托管堆与工作集峰值另行记录，只作诊断。极小预算的合同检查分配被拒绝、可回放的回退路线和复用；结束回合池的批次压缩合同单独对照启用与关闭。账本不是游戏进程的 RSS 限制。实现和档位见 [战斗搜索](combat-ai.md#7-内存与容量)；测量结果保存在本地 `.local/`。

## 9. 实机无头测试

先构建测试版 Mod 和 Differential。启动脚本默认使用 `artifacts/mod-testing/Sts2Solver`，把游戏、RitsuLib、Mod 和种子存档复制到 `.local/headless-instances/<实例>` 下的独立实例，源游戏目录不变。

```bash
dotnet build tests/Differential -c Release
dotnet build src/Mod -c Release -p:EnableModTesting=true
tools/testing/run-headless.sh --instance combat-smoke --root ROOT_NAME \
  --full-combat --results .local/combat-smoke --cleanup-instance-on-exit
```

测试模式读取请求 JSON，创建场景，捕获根，按请求执行单次搜索或整场战斗。测试专用补丁只处理进度隔离、无头教程、Discovery 展示顺序和回合准备快照。请求失败时写明原因，清理后再处理下一个请求。实机测试模式显式拒绝前缀动作，回合准备 fixture 只捕获第一个准备选项。

请求由差分宿主生成：`--emit-live-requests <目录>` 配合 `--root`、`--roots-file` 或 `--all`（`--all` 跳过前缀输入）。可选 `--search-tier Low|Medium|High|ExtraHigh`（默认 `Medium`）、`--search-normal`（关掉确定性，用正常的软时间）、`--search-cancel-after-ms N`（取消测试）和 `--full-combat`。

### 9.1 实机捕获对照

Mod 在无头游戏里把 `BATL`、`RULE`、`SREQ.capture`、`native.txt` 写进结果目录后，用 `--live-compare <结果目录>` 逐根比较：

- `BATL` 与 `golden.br` 里的期望根（回合准备 fixture 按 `setupSelections` 选根）；
- `RULE` 与冻结规则对象；
- `SREQ.capture` 与 `config.br`（回合准备 fixture 只比评分和下界子部件，`scope=subparts`）；
- `native.txt` 与离线宿主用同一请求重建的 `NativeStateDump`。

输出逐根的 `live ... status=same ... native=same`，最后汇总 `live roots=N same=... diff=... reject=... native=...`。

结果目录必须包含 `SREQ.capture`；缺失时该根按拒绝处理。

### 9.2 实机搜索请求与结果对照

单次捕获测试默认用中档、确定性的单成员请求求解一次（软时间关闭、宽度 300、节点上限 512,000），写出 `SREQ`、`SRES`、`search.json`。`--search-compare <结果目录>` 对每个根做两件事：

1. 解码游戏写出的 `SREQ`，用同一个 `BeamSolver` 离线重解，`SearchCodec.EncodeResult` 后与游戏的 `SRES` 逐字节比较；
2. 用离线宿主重建同一个原生根，导入并编译规则和评估配置，再按 `status.searchTier`、`status.deterministicSearch` 调用同一个 `SearchRequestCompiler`，与游戏的 `SREQ` 逐字节比较。

输出逐根的 `search=same sreq=same sres=same`，汇总 `search-compare roots=N same=... diff=... reject=... skipped=...`。非确定性的测量结果计为 `skipped reason=measure-only`，回合准备根计为 `skipped reason=turn-setup-root`。正常预算的搜索是性能与执行测试，不保证与其它机器的墙钟截断结果逐字节一致。

### 9.3 实机整场自动战斗

请求带 `fullCombatForTest`（`--emit-live-requests --full-combat` 会置位并关闭确定性）时，测试模式在进入战斗前把档位设为全自动，由 `CombatSessionController` 每回合搜索或沿用路线、逐动作核对、回合对账，直到胜利、失败、暂停或取消。结果写 `full-combat.json`，并在 `status.json` 里加 `fullCombatStatus/outcome/turns/playerHp/playerMaxHp/searches/reusedTurns/deviations/deploymentFailures`。

每个整场请求还会检查战斗面板的结构，结果写 `ui-panel.json`（32 项）：开战后面板存在且可见；经视口输入管线注入 `F8`、`Ctrl+F9` 后面板都不隐藏也不改变状态（没有快捷键）；收起后标题栏、求解器开关和模式占位按钮仍然可见、可点，收起状态写入设置；模式按钮能循环三档并恢复，求解器开关能关闭再开启；状态详情和设置页能来回切换，设置页不提供浅色主题；用游戏自带的英文覆盖把语言切到英文再切回，面板各重建一次；中文下面板控件有游戏替代字体覆盖，英文下没有字体覆盖，英文目录不缺键；动作栏每个可见按钮的可见性、可点性和执行按钮文字逐帧与快照的 `Can*`、阶段标志和呈现状态一致，按搜索/执行/空闲统计帧数；整场过程中逐帧比较面板已应用的快照与它的显示，路线行数、回合号和动作下标等于快照的 16 回合窗口，执行中高亮的胶囊覆盖快照的当前动作下标，已完成标记与完成数一致，阶段等于快照阶段；战斗结束后面板隐藏，同一进程内下一场战斗重新出现（第一场跳过）。断言只用阶段枚举名、回合号和全局动作下标，不比较本地化文本。任何一项失败，该请求记为失败。测试期间测试入口把 `headlessFastModeForTest` 覆盖为 `Instant`（默认），结束后恢复。

请求带 `controllerScenarioForTest` 时，测试模式在根捕获后运行 `CombatControllerChecks`，用 `ICombatUiCommands` 驱动控制器并写 `controller.json`（断言、命令序列和快照序列）。控制器场景覆盖手动开始/停止、自动搜索每回合、停止后下一回合/采用/重搜、采用、执行本回合的三种情况、模式切换、全自动开局、死亡回合暂停、重算战损增加暂停、求解器开关、战斗结束和多人门禁。手动开始、停止、采用和执行本回合通过面板按钮的 `Pressed` 信号触发，另有搜索/执行/完成/等待/停止/手动等待/暂停文案和线程设置、出牌速度、牌间停顿、停顿取消场景。其余场景仍用命令驱动。控制器场景与 `fullCombatForTest` 互斥。

```bash
tools/testing/run-headless.sh --instance full-combat \
  --roots-file .local/full-combat-roots.txt --full-combat \
  --results .local/full-combat-results \
  --cleanup-instance-on-exit --timeout-seconds 480
```

取消检查可以用 `--full-combat --search-cancel-after-ms 5000`，检查返回状态、worker 退出和主菜单恢复。

## 10. 适用边界

- **覆盖范围**：每次运行只覆盖命令选定的根与配置，不能从局部样本推断全部普通根、全部质量语料或整局都已验证。
- **界面**：无头测试只检查面板的结构和状态（见 9.3），不证明可见渲染、字体、排版、拖动与缩放手感；这些要在可见游戏里看。
- **非选牌模态**：选择驱动只覆盖 `CardSelectCmd` 的 `ICardSelector` 路径，没有验证未来或第三方 Mod 的其它模态请求。
- **前缀根**：实机测试模式显式拒绝前缀动作，前缀根由离线宿主的 `PrefixReplay` 覆盖。
- **回合准备多根**：实机只捕获第一个准备选项；完整 `config.br` 的多根排序只在差分宿主里验证。
- **第三方 Mod**：没有实机 Mod 根；导入层只显式拒绝非 `sts2` 程序集的模型，没有兼容层。
- **Windows/Steam**：当前的计时与验证都在 Linux 上，Windows 的 `.ps1` 启动脚本还没有。

没覆盖的内容，不要在文档或提交说明里写成已验证。
