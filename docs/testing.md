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
- `UiLogicChecks` 在无头测试启动时运行，覆盖设置默认值/往返、schema 1–4 迁移到 5、药水逐瓶策略对账、面板文案和本地化目录。
- `JitChecks` 在同一个启动检查里对 Mod/Core 程序集的所有非泛型方法调用 `RuntimeHelpers.PrepareMethod`（含 async 状态机的 `MoveNext`；泛型方法跳过），任何 `InvalidProgramException` 或 `TypeLoadException` 都会列出方法名并让 `ui-checks.json` 失败。规则见 [Battle 状态布局](combat-state-layout.md#大结构体与-jit-限制)。
- `Golden k0` 输出形如 `K0 Passed fields=30 battleBytes=62744 formatVersion=5 magics=BATL,RULE,GOLD,SREQ,SRES`。它检查 `Rules` 的 30 个字段都进入编码、`decode(encode(x))` 字节不变、`Unsafe.SizeOf<Battle>()` 符合预期、五个魔数按小端字节解出的 ASCII 与格式标签一致。

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

输出 `SIMULATION_OK`，汇总文件、根、游走、状态、特征和差异的计数，失败时以非零退出。除下面列出的重放外，模拟回归还在每个根、K1 和 K2 转移上检查结束回合的 setup 暂停合同：凡是 EndTurn 进入下一回合且请求 `PlayerTurnStart` 选择的转移，`EndTurn.After` 必须逐字节等于 `PreparePlayerPlayPhase(TryAdvanceToPlayerSetupStart(EndTurn.Before), 同样的准备选择, takingExtraTurn)`；额外回合的转移和普通回合一样逐个比较，`takingExtraTurn` 由同一个 `ShouldTakeExtraTurn` 前导得到。每个根另跑一条确定的 EndTurn 路径覆盖记录里没有结束回合的根。对每个根重放：

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
  compare tests/Golden/data --jobs 6 --shard 0/4 \
  --out .local/compare-0.csv
```

改了会影响搜索结果的代码后，先用它看看哪些请求变好、变差。

- `--config none|play|choice|all|choice-fill|all-fill`：`none` 关闭两项去重；`play` 只开完全等价的出牌去重；`choice` 只开旧的选牌等价类（每个类向量一个代表）；`all` 是 `play + choice`；`choice-fill` 是类优先、再补位置变体；`all-fill` 是 `play + choice-fill`，是正式默认，也是不写 `--config` 时的默认。冻结的期望结果都是在 `all-fill` 下算的；用别的配置对照时，变化里混着配置本身的差别，不能当成代码改动的影响。
- `--shard i/n`：与 `verify` 相同的权重分片；全量对照按 4 片运行。
- `--search-threads N`：每个请求内部的展开线程数，默认 1；结果应与单线程逐字节相同。
- `--out`：逐请求的 CSV，含质量分类、路线是否逐动作相同、展开/转移、分数、胜负、耗时和等价计数；质量比较因冻结路线重放失败而记 `unknown` 时，末尾的 `qualityError` 列写具体异常类型和消息。
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
| 回合准备枚举 `--setup-enumerate <root> [--setup-turn 1\|2]` | 在 `SetupPlayerTurn` 入口导入 Start 根，用 `TurnSetupRootEnumerator` 枚举计划；对每个计划重建一次原生准备页，比较 Play 根和根集合；另比较 `golden.br` 根集合、`config.br` 的数据段、短搜与 `Battles[BestRootIndex]` 重放 | 每个计划 `PLAN status=same`、`SETUP_NATIVE status=same`；冻结 fixture 输出 `SETUP_CONFIG scope=frozen status=same`、`SETUP_GOLDEN status=same` |

`--all` 汇总 `same/diff/reject`，并按首个差异字段或拒绝原因分组计数。前缀根通过“导入前缀之前的状态 + 内核回放”比较；回合准备 fixture 按原生准备选项逐个重建，比较整份请求及全部准备根（见 [原生导入](import.md#8-前缀根与回合准备根)）。

回合准备枚举的单独入口（不启动 Godot，走同一个原生建局和准备页）：

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-a-toasty
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-a-toasty --setup-turn 2
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-b-toolbox
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate qf2-gradient-failure
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate qf-turn-start-choice-forge --setup-turn 2
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-d-autopreplay-choice --setup-turn 2
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-g-multi-choice --setup-turn 2
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-h-choice-then-listener
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-i-plasma-pause --setup-turn 2
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-j-crossbow-one-card
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-j-crossbow-many
dotnet artifacts/bin/Differential/release/Differential.dll --setup-enumerate ts-k-two-relic-choices
```

`ts-g-multi-choice` 覆盖 `AfterPlayerTurnStart` 两段选择 + 熔炉侧回合开始；`ts-h-choice-then-listener` 覆盖同一轮选择之后还有遗物监听器；`ts-i-plasma-pause` 覆盖 Plasma 在暂停处触发。三者都是测试专用 fixture。

逐计划重建原生准备页时还比较每一段页面的候选列表：牌的 id、升级、顺序，以及 `ICardSelector` 能表达的有效最少/最多选择数。不一致输出 `PLAN status=diff field=options` 并以非零退出；生成选项页来自 `FromChooseACardScreen` 时 `ICardSelector` 固定收到 0..1，`canSkip` 不经过这个接口，因此这类页面只比较候选，不比较边界。

`--setup-turn 2` 先建到第 1 回合 Play、结束回合，在第二次 `SetupPlayerTurn` 入口重新抓 Start 根；用于后续回合、AutoPrePlay 和多段选择。Start 根和逐计划 Play 根用生产口径（`LiveCombatTracker.ImportSetupRoot` / `ImportForComparison`，`Reconciliation=true`）比较；冻结 `golden.br`/`config.br` 仍用冻结口径。输出 `SETUP_ENUM`/`ENUM`/`PLAN`/`SETUP_NATIVE` 之外，还会输出 `SETUP_START`（两种 Start 导入口径逐字节相同或第一处差异）、`SETUP_BUDGET`（测试用的更低根预算走同一条丢弃路径）；冻结 fixture 还会输出 `SETUP_CONFIG`（把枚举根按 `config.br` 顺序重排后的数据段逐字节比较）、`SETUP_BOUND`（带/不带 Bound、Start/Play 根 Bound）、`SETUP_PROD`（生产请求与 `config.br` 的子部件差异）、`SETUP_SEARCH`（短搜后用 `Battles[BestRootIndex]` 重放）和 `SETUP_GOLDEN`。

`NativeStateDump` 还记录跑局和幕的信息：`run.ascension`、`run.act.index/count/id`、`run.act.boss/secondBoss`、`run.room.type/id`、`run.tutorial.discoveryOrder`、`run.unlock.*` 和 `rng.UpFront` 的计数与状态（`SecondBossEncounter` 缺失时写 `-`）。这些字段只用来核对建局路径和 `BossHpRelief` 用到的幕结构，不参与导入判定。

证明：不启动 Godot 时，冻结输入能建出与参考导出相同的原生战斗状态，导入后与冻结的 `BATL` 逐字节相同，编译出的 `Rules`、评分表与下界也与冻结数据相同；私有状态和第三方内容的负例会显式拒绝。不证明：实机部署、界面、实机选牌写回，以及未捕获的第三方 Mod 内容。

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

对一个目录里的请求逐个对照时用 `--pilot-dir <目录>`：目录里的每个 `*.json` 是一份 `OfflineRequest`，父进程仍为每份请求启动独立子进程，`--pilot-jobs`、`--pilot-shard i n` 和 `--pilot-results <TSV>` 的含义与按根名运行时相同。子进程入口是 `--pilot-request <文件>`。`--pilot-results` 逐用例写 `case/status/reason/outcome/actions/turns/encounter/child_exit/first_diff/action/diag`，便于汇总差异类别；没有该参数时只打印汇总。

内核回放评测（7.1）通过不等于这项对照通过，两类报告要分开写。

### 7.3 sts2-sim 整局导出与批量对照

`tools/sts2-sim-export/Sts2SimExport.csproj` 在 sts2-sim 里跑完整局，在每场战斗开始时导出一份 `OfflineRequest` 到本地目录。它不加入 `Sts2Solver.sln`，主构建不依赖 sts2-sim。

准备外部检出（不修改 `~/ss/sts2-sim`）：

```bash
git clone ~/ss/sts2-sim .local/sts2-sim
git -C .local/sts2-sim checkout 489c4b1
dotnet build tools/sts2-sim-export/Sts2SimExport.csproj -c Release -p:NuGetAudit=false
```

工程默认读 `.local/sts2-sim`；也可以用 MSBuild 属性 `Sts2SimRepo` 或环境变量 `STS2_SIM_REPO` 指向其他固定检出。

```bash
dotnet artifacts/bin/Sts2SimExport/release/Sts2SimExport.dll --out .local/sts2-sim-export \
  --runs 1000 --characters IRONCLAD,SILENT,DEFECT,REGENT,NECROBINDER --ascensions 0,10
```

`--force-win` 会在每场战斗照常导出开局后注入胜利（剩余生命保持进入战斗时的值，不消耗药水），
用于把整局推进到第二、三幕。注入只在 sts2-sim 的 imagination clone 中生效，导出器会从请求种子派生
clone 种子；事件强制战斗不能注入，会退回普通战斗策略并计入 `nestedRoomSkipped`。不加该参数时
行为不变。

每次运行输出：

- `<scenarioId>.json`：战斗开始时的角色、进阶、幕、遭遇、生命、整副牌组、遗物状态和药水；
- `combats.jsonl`：每个导出战斗的索引（角色、进阶、幕、房间类型、遭遇、种子、牌组/遗物/药水数量）；
- `runs.jsonl`：整局种子与结果；`--resume` 会跳过已经记录的种子；
- `summary.json`：按角色/进阶/幕/房间类型/遭遇的计数、跳过原因计数，以及 `--force-win` 的注入、
  缺少开局快照、事件强制战斗和注入后生命核对计数。

导出器只记录能由 `OfflineRequest` 表达的运行状态；无法表达的牌或遗物状态、缺失内容的 id、空牌组等按 `EXPORT-SKIP <原因>=<次数>` 计数并跳过，不会构造近似请求。战斗种子由整局种子、幕、楼层、遭遇和该局战斗序号派生，重跑同一批种子得到同一组请求。

导出的目录直接用 `--pilot-dir` 跑 7.2 的逐动作对照：

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare \
  --pilot-dir .local/sts2-sim-export --pilot-jobs 4 \
  --pilot-results .local/sts2-sim-diff/results.tsv
```

sts2-sim 的 `RunDriver` 只负责生成局面；判定仍由原版程序和本仓库内核完成，不和 sts2-sim 比较。该流程当前没有“在原版里随机走动作”的入口；随机动作对照的设计不在本文承诺范围内。

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

测试模式读取请求 JSON，创建场景，捕获根，按请求执行单次搜索或整场战斗。测试专用补丁只处理进度隔离、无头教程、Discovery 展示顺序和回合准备快照。请求失败时写明原因，清理后再处理下一个请求。实机测试模式显式拒绝前缀动作，回合准备 fixture 只捕获第一个准备选项。每个请求结束回主菜单后，TestHost 等到 `AssetSets.CommonAssets` 和 `AssetSets.MainMenuSet` 全部进入 `PreloadManager.Cache` 再建下一个根；`NGame.ReturnToMainMenu` 只等预加载会话建立，不等它完成，直接建局会让 Common 与 characters 两套预加载重叠。

启动器用 `HEADLESS_GAME_EXIT code=... [signal=...]` 记录游戏进程退出状态，末尾打印 `HEADLESS_EXIT ...`；探测成功但主动终止的进程同样会记录收到的信号。失败（提前退出、超时、加载异常或结果失败）时，启动器先打印 `launcher.log` 最后 200 行和 Godot 日志最后 100 行，再把 `godot-headless.log`、`launcher.log`、`run/`（请求、`mod.log`、`game-exit.txt` 和已有结果）复制到 `.local/headless-failures/<实例>-<UTC 时间>/`，打印 `HEADLESS_FAILURE_ARTIFACTS path=...`，最后才按 `--cleanup-instance-on-exit` 删除实例；`--failure-root DIR` 可改证据根。`--log-dir DIR` 在正常和失败收尾时都把 Godot 日志、`launcher.log`、`mod.log` 和 `game-exit.txt` 复制到指定目录并打印 `HEADLESS_LOGS path=...`。启动器还从 Godot 日志统计 `Invalid Task ID`，打印 `HEADLESS_GODOT_ERROR invalid_task_id=N build_invalid_task_id=M`；`M` 只统计每个 `BUILD_STARTED` 到对应 `BUILD_COMPLETED` 之间的部分，用来区分建局过程中的预加载重叠与建局前后的启动/收尾预加载。

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

每个整场请求还会检查战斗面板的结构，结果写 `ui-panel.json`（54 项）：开战后面板存在且可见；经视口输入管线注入 `F8`、`Ctrl+F9` 后面板都不隐藏也不改变状态（没有快捷键）；收起后标题栏和三段模式按钮仍然可见、可点，收起状态写入设置；点手动、自动搜索、全自动三段分别切换并恢复；状态详情能来回切换；标题栏右侧依次是“药水”“成长”“设置”“收起”，都没有按下；点“药水”打开策略侧栏的药水页并按下该按钮，侧栏停靠在面板旁、与面板同高、深色，药水行与快照的药栏一致（槽位、药水、模式文字、不可指定时禁用并带悬停）；点“成长”换到成长页，开关和十个额度与快照一致；点已按下的按钮、点“×”、收起面板都会关闭侧栏，收起时三个按钮隐藏，再展开时侧栏仍然关闭；点“设置”时关闭侧栏，面板主体换成设置页，按钮字变成“返回”且宽度不变，设置页深色、没有主题选择，八个控件都在，两项幕末 Boss 下拉与快照一致；“恢复默认”恢复本页各项（含两项幕末 Boss）且不改变模式；设置页显示时面板高度等于完整内容与 540 中较小的；收起保留设置页状态，展开后回到设置页；设置页开着时点“成长”先关设置页再打开侧栏；“返回”回到路线；盗贼提示条只在快照适用时显示；侧栏开着时切换语言，重建后仍开着，英文下“设置”和“返回”宽度相同；同一进程内下一场战斗开始时侧栏和设置页都是关的（第一场跳过）；用游戏自带的英文覆盖把语言切到英文再切回，面板各重建一次；中文下面板控件有游戏替代字体覆盖，英文下没有字体覆盖，英文目录不缺键；底栏两个槽按设计稿逐帧比较：左槽始终显示、文字按搜索/重新搜索/停止搜索切换，右槽按执行/停止执行/隐藏切换，禁用状态和悬停原因来自快照的 `CanStartSearch`、`SearchBlockReason`、`CurrentTurnRouteReady`、`DeployWhenReady`、`IsStoppingDeployment` 和搜索候选标志；期望值由快照独立推导，按搜索/执行/空闲统计帧数；整场过程中逐帧比较面板已应用的快照与它的显示，路线行数、回合号和动作下标等于快照的 16 回合窗口，执行中高亮的胶囊覆盖快照的当前动作下标，已完成标记与完成数一致，阶段等于快照阶段；战斗结束后面板隐藏，同一进程内下一场战斗重新出现（第一场跳过）。断言只用阶段枚举名、回合号和全局动作下标，不比较本地化文本。任何一项失败，该请求记为失败。测试期间测试入口把 `headlessFastModeForTest` 覆盖为 `Instant`（默认），结束后恢复。

请求带 `controllerScenarioForTest` 时，测试模式在根捕获后运行 `CombatControllerChecks`，用 `ICombatUiCommands` 驱动控制器并写 `controller.json`（断言、命令序列和快照序列）。控制器场景覆盖手动开始/停止、自动搜索每回合、停止搜索留下路线、停止后下一回合/重搜、搜索中执行本回合、无路线时执行被拒、模式切换、回合中途切到自动搜索和手动、手动档回合开始沿用路线、全自动开局和中途武装、预计死亡暂停、搜索失败累计暂停、停止执行、不能搜索的原因（敌方回合、动作结算中、回合开始结算中）、动作结果/发牌前局面/回合边界偏差立即停止且不重搜、部署失败立即停止、准备计划点完后实机戳不符、路线耗尽后重搜、搜索失败主面板（`search_failed` 与 `forced_potion_unusable`，重新搜索和下一回合清除）、战斗结束、多人门禁，以及策略命令：药水逐瓶/批量模式、成长额度、不考虑局外收益、Boss 血量策略和本场盗贼策略。策略场景同时断言命令不触发搜索、下一次请求带上新策略，盗贼策略在下一场战斗回到默认。`panel_strategy` 场景在手动档里只通过面板控件改策略：药水模式按钮按智能使用 → 禁用 / 保护 → 强制使用循环、四个批量按钮，成长页的两个开关、至亮之焰上限和额度输入（非法输入时侧栏不关闭），设置页的两项幕末 Boss 下拉和“恢复默认”，以及有盗贼的战斗里提示条的按钮；每一步断言命令生效、快照随之变化、页面显示与快照一致、状态行写“已保存，下次搜索生效”，全程不触发搜索，结束时恢复设置。药水用 `k3-basic-potions`，盗贼和空药栏用 `tw-gremlin-merc`。搜索、停止、执行和模式按钮通过面板按钮的 `Pressed` 信号触发；另有搜索/执行/完成/等待/停止/手动等待/暂停文案和线程设置、出牌速度、牌间停顿场景。控制器场景与 `fullCombatForTest` 互斥。`manual_switch_at_turn_start` 在单搜索线程下先投递回合开始事件再立刻切到手动，断言这个回合没有自动搜索、`Searches` 不变；`auto_turn_start_once` 在第一次自动搜索失败后重复投递同一回合的回合开始事件，断言不发出第二次自动搜索、失败状态保留。停止后继续的场景用 `continue_search_middle`（停在中间成员后从下一个成员继续，断言被停成员不重跑、后续成员都处理、继续期间面板保留路线）、`continue_search_settings_changed` 和 `continue_search_state_changed`（设置或局面变化后左槽变“重新搜索”并从头搜）、`continue_search_last_member`（停在最后一个成员后不提供继续）、`continue_search_twice`（继续中再停并再继续）和 `continue_search_request_mismatch`（便宜判断通过但精确请求不同，从头搜并记录请求差异）。偏差场景 `deviation_action_full_auto`、`deviation_action_auto_search`、`deviation_deployment_drift`、`deviation_boundary`、`deviation_boundary_setup` 和 `deviation_setup_stamp` 断言暂停码、不重搜、左槽可重搜、右槽隐藏和摘要里的具体原因；`deployment_drift_research` 断言部署失败 `deploy_failed` 停下且不重搜；`plan_exhausted` 断言路线耗尽仍重搜。失败原因场景 `import_capacity` 用超过 `Limits.Cards` 张的固定牌组断言“牌组共 N 张，超过求解器上限 M 张”原文，`import_failure` 断言导入失败标题和原因两行，`search_status_failures` 用测试钩子把协调器状态分别设成容量丢弃、达到内存上限和没有可执行路线并断言三条原因；中文和英文请求各跑一次。`capacity_dropped_route` 在自动搜索档下给一次真实成功搜索加上 `Cards=1` 的容量丢弃信息，断言结果不是失败、摘要显示容量提示行，左槽“重新搜索”和右槽“执行本回合”都可点。没有能稳定预测本回合死亡的实机根；控制器场景用 `ForceProjectedDeathTurn` / `ForceProjectedDeathTurnSetup` 覆盖出牌和回合准备两条死亡暂停路径，纯策略由 `UiLogicChecks` 检查。

回合准备接管场景用 `turnSetupTakeoverForTest` 显式打开测试构建里的正式接管，测试模式默认关闭；打开时测试入口不再安装 `TurnSetupCardSelector`，并让建局停在可见准备页或 `Play`。场景覆盖：自动搜索搜完后按“执行本回合”驱动页面并部署；全自动搜完直接驱动；Toolbox 三选一；Toasty 后监听器；停止搜索有候选（保留计划）与无候选（交还页面）；强制搜索失败显示 `turn_setup_failed` 且替换任务不 fault；全自动驱动中点“停止执行”；全自动在驱动前命中暂停条件；页面开着退出主菜单；手动档不接管；玩家在计划就绪后改选另一张牌使计划作废；纯隐式选择（`ts-j-toasty-one-card`，没有可见页面）不启动准备搜索、`Play` 后正常搜索。每个场景断言搜索次数、复用次数、偏差、错误码、遮挡是否释放和持久模式不变。现有 `turn_setup_wait` 仍作为“正式接管关闭”的回归。

后续回合和新页面的场景用 `turn_setup_turn2`（上一回合由求解器结束，第 2 回合沿用 EndTurn 计划，不再搜索）、`turn_setup_manual_end`（自动搜索档，玩家用原生结束回合动作结束上一回合，没有可沿用的计划，走新的准备搜索）、`turn_setup_autopreplay`（`ts-d-autopreplay-choice`，沿用计划包含 AutoPrePlay 选择，驱动消费完且只有一次搜索）、`turn_setup_plasma`（`ts-i-plasma-pause` 第 2 回合沿用）、`turn_setup_multi`（`ts-k-two-relic-choices` 的两段准备选择）和 `turn_setup_carry`（`ts-a-toasty`，初始生命 1/80、敌人 120，第 1 回合强制使用 `BLOOD_POTION` 使 `RecoveredHp=16`，断言第 2 回合准备 Start 根、选中的 Play 根和部署边界都等于该值，且预测 Start 根与实机一致）。`turn_setup_crossbow` / `turn_setup_crossbow_many` 用 `ts-j-crossbow-one-card` / `ts-j-crossbow-many` 验证暂停后加入手牌的十字弓牌进入原生页面候选（分别 2 个和 4 个选项）。

沿用计划的场景另加 `turn_setup_continuation_full`（`ts-a-toasty` 全自动至少 3 个玩家回合，只搜索一次，复用回合数增加，0 偏差）、`turn_setup_continuation_auto`（自动搜索档第 2 回合显示沿用计划，执行后驱动并部署）、`turn_setup_continuation_player`（玩家改选后计划作废，出牌阶段正常搜索）、`turn_setup_continuation_validation`（校验前篡改计划，改走新的枚举搜索）、`turn_setup_continuation_gap`（`ts-g-multi-choice` 的熔炉在暂停处多一个候选，内核 EndTurn 现在和准备前奏同样暂停，因此沿用计划通过校验并直接部署，只搜索一次、不改走新搜索）、`turn_setup_continuation_mismatch`（校验通过后、驱动前篡改计划，页面交还，全自动按 `choice_mismatch` 暂停）、`turn_setup_continuation_pause`（沿用计划命中全自动暂停条件）和 `turn_setup_continuation_probe`（只记录沿用计划被校验或续用比较接受还是拒绝，供研究）。胶囊来源用 `turn_setup_capsule_source` / `turn_setup_capsule_source_turn2` 在中文和英文下断言遗物、能力、卡牌来源的本地化标题；`ts-g` 的胶囊来自沿用计划。下一回合场景断言 `CardSelectCmd.Selector` 在 setup 前为 null、`SupersededByTurnSetup` 不产生 `action_deviation`，并要求出牌阶段的精确状态戳一致。

```bash
tools/testing/run-headless.sh --instance full-combat \
  --roots-file .local/full-combat-roots.txt --full-combat \
  --results .local/full-combat-results \
  --cleanup-instance-on-exit --timeout-seconds 480
```

取消检查可以用 `--full-combat --search-cancel-after-ms 5000`，检查返回状态、worker 退出和主菜单恢复。

### 9.4 场景回归入口

控制器场景、回合准备场景和整场回归的请求清单保存在 `tests/corpora/scenarios/manifest.json`，说明见 [场景请求清单](../tests/corpora/scenarios/README.md)。清单是唯一输入：生成脚本按条目从 fixtures / quality 语料生成请求，新增场景只改清单。先构建测试版 Mod 和 Differential，再运行：

```bash
dotnet build tests/Differential -c Release
dotnet build src/Mod -c Release -p:EnableModTesting=true
tools/testing/run-scenario-regression.py --jobs 4
```

默认跑全部四组：`controller`、`turn-setup`、`full-combat`、`panel-stress`。`--group` 可以只跑一组，可重复；`--scenario <id>` 只跑清单里的指定条目；`--generate-only` 只生成请求。脚本把请求生成到 `.local/scenario-regression/requests/`，结果默认写到 `.local/scenario-regression/results/`。

每个无头实例最多六个请求，并始终带 `--cleanup-instance-on-exit`。实例按清单声明的 `settingsThreadsForTest` 调度，同时运行实例的搜索线程总数不超过 8；`--jobs` 限制并发实例数，默认 4，`--timeout-seconds` 限制每个实例的总时长，结果写出后的退出等待为 120 秒。每次运行结束按组打印通过数和失败的场景名、第一条失败断言；每个实例的 `ui-checks.json` 也必须通过，`jit_prepare_methods` 在其中。整场组额外检查每个请求的 `ui-panel.json` 54 项。`SCENARIO_CHUNK` 和 `SCENARIO_SUMMARY` 带 `invalidTaskIds=` 与 `buildInvalidTaskIds=`（启动器从 Godot 日志统计的累计值；后者只算每个 `BUILD_STARTED` 到对应 `BUILD_COMPLETED` 之间的部分）。场景运行器把每个实例的日志放到 `.local/scenario-regression/logs/<chunk>/`；失败实例的 `HEADLESS_FAILURE_ARTIFACTS path=...` 同时写进 `SCENARIO_CHUNK failureArtifacts=...`、逐条 `SCENARIO_FAILURE_ARTIFACTS` 和最终的 `SCENARIO_SUMMARY failureArtifacts=...`。

## 10. 适用边界

- **覆盖范围**：每次运行只覆盖命令选定的根与配置，不能从局部样本推断全部普通根、全部质量语料或整局都已验证。
- **界面**：无头测试只检查面板的结构和状态（见 9.3），不证明可见渲染、字体、排版、拖动与缩放手感；这些要在可见游戏里看。
- **非选牌模态**：选择驱动只覆盖 `CardSelectCmd` 的 `ICardSelector` 路径，没有验证未来或第三方 Mod 的其它模态请求。
- **前缀根**：实机测试模式显式拒绝前缀动作，前缀根由离线宿主的 `PrefixReplay` 覆盖。
- **回合准备多根**：实机 Mod 在自动搜索/全自动档接管第一张可见准备页及其后的 AutoPrePlay 页，自动搜索只显示、全自动驱动；两者共用同一个页面会话和计划，只搜索一次。纯隐式选择不启动准备搜索。`ts-k-two-relic-choices` 在同一准备阶段产生两段可见选择，驱动按顺序消费；`ts-j-crossbow-one-card` / `ts-j-crossbow-many` 覆盖暂停后由侧回合开始加入手牌的候选。差分宿主验证 Start 根枚举、每个计划的 Play 根和 `config.br` 数据段。`--setup-enumerate` 默认只跑第 1 回合，`--setup-turn 2` 覆盖第 2 回合、额外回合和多段选择（`ts-ex-extra-turn` 用 PaelsEye + Toasty Mittens 在第 2 个玩家回合开始触发选择）。准备暂停时侧回合开始或球的触发如果自己请求选择，当前显式不支持；原版 v0.111.0 没有这样的内容（`AfterSideTurnStart` 不接收选择上下文且没有 `CardSelectCmd` 调用，球只有 PlasmaOrb 覆写回合开始触发且只获得能量）。
- **第三方 Mod**：没有实机 Mod 根；导入层只显式拒绝非 `sts2` 程序集的模型，没有兼容层。
- **Windows/Steam**：当前的计时与验证都在 Linux 上，Windows 的 `.ps1` 启动脚本还没有。

没覆盖的内容，不要在文档或提交说明里写成已验证。
