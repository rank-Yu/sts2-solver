# sts2-sim 整局局面导出

用 [sts2-sim](https://github.com/iRyougi/sts2-sim)（MIT）跑完整局，在每场战斗开始时写出本仓库
`src/TestHost/Combat/OfflineRequest.cs` 能读取的 `OfflineRequest` JSON。sts2-sim 只负责生成真实
分布的局面；对照时由原版程序建立同一场战斗，再由 `tests/Differential` 比较内核预测和原版执行。

本工程不加入 `Sts2Solver.sln`，主构建和发布都不依赖 sts2-sim。

## 依赖与检出

- .NET 9 SDK。
- 一份固定提交的 sts2-sim 检出。默认是工作树里的 `.local/sts2-sim`：

```bash
git clone ~/ss/sts2-sim .local/sts2-sim
git -C .local/sts2-sim checkout 489c4b1
```

不要修改 `~/ss/sts2-sim`。也可以用 MSBuild 属性 `Sts2SimRepo` 或环境变量 `STS2_SIM_REPO`
指向另一份固定提交的检出：

```bash
dotnet build tools/sts2-sim-export/Sts2SimExport.csproj -c Release -p:NuGetAudit=false \
  -p:Sts2SimRepo=/path/to/sts2-sim
```

## 跑整局并导出

```bash
dotnet artifacts/bin/Sts2SimExport/release/Sts2SimExport.dll --out .local/sts2-sim-export \
  --runs 1000 --characters IRONCLAD,SILENT,DEFECT,REGENT,NECROBINDER --ascensions 0,10
```

加 `--force-win` 时，每场战斗仍然先走正常的房间进入和 `BeginCombat`（导出开局），然后由
`ICombatOutcomeProvider` 注入按胜利结算的结果。强制胜利把剩余生命固定为进入战斗时的值，且不消耗
药水，因此整局能继续推进到第二、三幕；不加该参数时行为不变。当前提供者只用于分布导出，后续可用
同一个接口替换成求解器在原版里真实打出的剩余生命和药水消耗。注入结果只在 sts2-sim 的 imagination
clone 中生效，因此 `--force-win` 会从请求种子派生一个 `run-clone-<hex>` 种子再跑整局；请求和结果
都可以用同一请求种子稳定重放。

参数：

- `--out <目录>`：输出目录，默认 `.local/sts2-sim-export`。
- `--runs N`：尝试的整局数，默认 20。角色与进阶按轮转分配，种子为
  `<seed-prefix>-<角色>-A<进阶>-<轮次>`。
- `--target-combats N`：导出到 N 场后停止启动新局；默认 0（跑满 `--runs`）。
- `--max-floors N`：单局最多访问楼层，默认 60。
- `--seed-prefix <前缀>`：整局种子前缀，默认 `FS1`。
- `--characters <逗号分隔>`：默认五个角色。
- `--ascensions <逗号分隔>`：默认 `0,10`。
- `--resume`：跳过 `runs.jsonl` 里已有种子的整局。
- `--force-win`：每场战斗先导出开局，再按胜利结算；剩余生命保持进入战斗时的值，不消耗药水。

## 决策策略

导出器使用确定性的启发式策略，不使用随机策略：

- 战斗：先处理致死/低血量喝药，然后对每个可出的牌做一回合 rollout（克隆战斗、打完本回合、
  结束回合、按原版结算后的血量/敌人血量/格挡/能量/手牌评分），取最高分；卡牌选择统一取前
  `MinCount` 张。
- 地图：Boss 必选；生命低于 70% 优先休息处；生命高于 80% 时以 1/3 概率选精英；否则普通战斗。
- 休息处：生命不高于 65% 回血，否则升级优先度最高的未升级牌。
- 奖励：金币、药水、遗物照常拿；卡牌奖励在可选项中按稀有度和类型取最高分。
- 商店：优先买便宜遗物，其次删一张基础打击/防御，最后在有空药水槽时买药水。

这些选择只影响导出的分布，不参与对照判定。

## 战斗结果提供者

牌组、遗物和药水仍在 `EnterFloor` 快照；`CombatOutcomeCoordinator` 在 `BeginCombat`
记录导出请求真正使用的初始生命和 `scenarioId`，随后由
`ICombatOutcomeProvider.Resolve(CombatOutcomeContext)` 决定注入结果。上下文包含整局状态、战斗房间、
`scenarioId`（未导出时为 null）、遭遇、幕和进入战斗时的生命。当前实现
`ForceWinCombatOutcomeProvider` 返回 `InjectedCombatOutcome(Died: false, RemainingHp: 进入战斗生命)`。
后续批次可以实现同一个接口，按 `scenarioId` 查询求解器在原版中真实打出的结果，并在
`ConsumedPotionSlots` 里给出用掉的药水槽。

sts2-sim 不接受事件强制战斗的结果注入（`RunDriver` 会拒绝 `forcedEnemies` 路径），这类战斗会退回
原有战斗策略并计入 `FORCE-WIN nestedRoomSkipped`；事件本身造成的非战斗伤害也不由注入控制，因此
强制胜利模式下仍可能出现整局提前结束。`summary.json` 的 `injectedFinalHpChecked` /
`injectedFinalHpMismatch` 记录注入后 `EndCombat` 时实际生命与注入值的核对结果。

## 输出

- `<scenarioId>.json`：一场战斗开局的 `OfflineRequest`。`scenarioId` 包含角色、进阶、整局种子
  短哈希、幕、楼层、房间类型、遭遇和该局战斗序号，因此不同整局不会互相覆盖。
- `combats.jsonl`：每场导出战斗一行索引，字段包括角色、进阶、幕名、房间类型、遭遇、种子、
  牌组/遗物/药水数量和整局种子。
- `runs.jsonl`：每局一行结果，供 `--resume` 和报告使用。
- `summary.json`：运行、导出、跳过和分布计数。

导出器只写 `OfflineRequest` 能表达的字段。以下情况按原因计数并跳过，不构造近似请求：

- 牌组里的卡、附魔、苦难、遗物、药水或遭遇不在本仓库内容注册表里；
- 牌的持久累积值没有对应字段（例如 Genetic Algorithm 的累计格挡、The Scythe 的累计伤害）；
- 遗物状态字段类型不是整数/布尔（例如 Beating Remnant 的每回合伤害是 Decimal）；
- 牌组为空、牌数超过内核容量、玩家已死亡、没有战斗开局快照等。

## 对照

```bash
dotnet artifacts/bin/Differential/release/Differential.dll --combat-pilot-compare \
  --pilot-dir .local/sts2-sim-export --pilot-jobs 4 \
  --pilot-shard 0 2 --pilot-results .local/sts2-sim-diff/results.tsv
```

目录里的每个 `*.json` 由父进程 Fork 一个独立子进程；`--pilot-shard i n` 可按文件名顺序分片，
`--pilot-results` 写逐用例 TSV。完整语义见 [`docs/testing.md`](../../docs/testing.md#73-sts2-sim-整局导出与批量对照)。

## 来源与许可

sts2-sim 是 iRyougi/sts2-sim 的 MIT 项目（Copyright (c) 2026 iRyougi and contributors）。
本工程只以项目引用的方式使用它，构建产物不包含其源码；本仓库不复制或修改 sts2-sim 的内容。
