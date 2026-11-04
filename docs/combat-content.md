# 内容专属机制

[返回文档索引](README.md)

本文记录具体卡牌、Power、遗物、药水和怪物里不显而易见的结算细节：哪些地方的顺序或口径和直觉不同、原引擎是怎么做的、Sts2Solver 怎么表示。通用的执行上下文、Hook 派发、出牌、伤害和回合流程见 [内核语义](combat-semantics.md)，字段编码见 [状态布局](combat-state-layout.md)。

每节开头的“来源”指原引擎（CombatSolver `a62f1850`）的源码位置，路径缩写见 [内核语义](combat-semantics.md) 开头；条目末尾的“对照”同理。这些语义已经冻结在 `tests/Golden/data`，以后以快照和实机证据为准。

## 1. 静默猎手：弃牌与 Sly

来源：`S/CombatPredictionSimulator.Card.cs` 的 `Discard`、`S/CombatPredictionSimulator.DiscardContinuation.cs`、`Q/SimulatedCombatState.ActionChoices.cs`、`Q/CardChoiceResolution.cs`。

**弃一张。** `Kernel.Discard` 对应 `CardCmd.Discard(choiceContext, CardModel)`：战斗强制结束或持有者已死时直接返回；否则把牌移到弃牌堆底，`CardsDiscardedThisTurn` 加一，派发 `AfterCardDiscarded`；如果这张牌本回合是 Sly（有 Sly 关键字或单回合标记 `IsSlyThisTurn`），立即自动打出。

**弃多张。** 一次弃多张时（`Kernel.DiscardAndDraw`，对应多张 `CardCmd.Discard` 和 `DiscardAndDraw`），按选择顺序逐张弃、逐张派发 `AfterCardDiscarded`，然后抽牌，最后把弃置时带 Sly 的牌按弃置顺序自动打出。

难点是弃完到自动打出之间可能抽牌、洗牌，牌的位置全变了。原引擎拿着 `CardModel` 对象，不怕牌移动；Sts2Solver 的牌没有对象身份，所以弃牌时给每张 Sly 牌写一个一次性标记 `Card.TransientMark`，打出时按标记找牌。两张字节完全相同的 Sly 牌也不能互换，因为选哪张会改变手牌和弃牌堆的分配。标记的分配区间、嵌套规则和找不到牌时的处理见 [状态布局](combat-state-layout.md#36-卡牌)。

**Scrape 的抽牌弃置。** 原版 `Scrape.OnPlay` 先 `CardPileCmd.Draw` 收集抽到的 `CardModel` 对象，再交给 `CardCmd.Discard`；抽牌钩子把牌自动打出、放回抽牌堆或放进弃牌堆都不影响对象身份。Sts2Solver 在每张牌进入手牌、`AfterCardDrawn` 之前写入本批的 `TransientMark`（`markStart + 抽牌序号`），`Kernel.DiscardDrawn` 再按标记在当前所有战斗牌堆中定位并逐张弃置；全部弃置完成后，才按弃置顺序自动打出其中的 Sly 牌。手中字节相同的旧副本不会被误弃；同一张牌在一批里被自动打出、洗回抽牌堆后再次抽到，按出现次数各弃一次。标记在 `finally` 中恢复外层批次的标记并释放 `TransientMarkBase`。标记丢失或牌完全离开战斗牌堆时显式抛 `UnsupportedException`。

**回合末清手牌不算弃牌。** 回合末手牌清理（`CorePowerSupport.FlushPlayerHandAtTurnEnd`）直接用 `AddToPile` 移入弃牌堆，不派发 `AfterCardDiscarded`，也不触发 Sly。Sts2Solver 保持相同行为。

**空选择。** `BuildRequiredEmptyChoice` 列出的牌（HiddenDaggers、Brand）在手牌为空时：手动打出会提出一个 0..0 的空选择；在自动打出里，嵌套选择由 `ResolveNestedCardChoice` 直接结算这个空选择，不提问。Sts2Solver 用 `Exec.CurrentPlayIsAutoPlay` 区分两种情形，之后照常执行 `ApplyPostChoiceEffects`。

**手牌和弃牌触发。** `AfterCardDrawn` 里的 `CorrosiveWavePower`、`SpeedsterPower` 等，以及弃牌触发，都在上面的位置派发。多张弃置的 `AfterCardDiscarded` 之间可能冒出待处理选择，这时 Sts2Solver 丢弃半成品状态，从父状态带上选择重新执行。

### 1.1 其它卡牌

- **Scrape**：攻击后抽 `Cards` 张，弃掉其中费用不为 0 的牌（X 费也算；不可打出的 -1 费会保留负值，因此会被弃掉）。抽牌钩子移动或自动打出抽到的牌后仍按对象身份弃置。对照 `CardDrawCardMirrors.ScrapeOnPlay` 与 `CardCmd.Discard`。
- **KnifeTrap**：按消耗堆顺序取出 Shiv（按“值 + 同值第几张”定位），升级版先把该 Shiv 升一级，再自动打出到同一目标；Shiv 带消耗关键字，打出后回到消耗堆。位置假设不成立（抽牌钩子移动了牌）时抛 `UnsupportedException`。对照 `CardOnPlaySupport.Batch043.ApplyKnifeTrap`。
- **EchoingSlash**：`Kernel.CardAttackRepeating` 在一个攻击上下文里分波打全体敌人，`BeforeAttack`、`AfterAttack` 各只派发一次。每波对当前全部存活敌人造成一次伤害，随后用**当前名单**结算死亡，每击杀一名敌人追加一波。每波开始前从 `Exec.PlayPosition` 重新读取当前打出的牌和伤害变量（原引擎每波读 `base.DynamicVars.Damage`）：Dampen 的最后一位施法者在本波死亡时会恢复卡牌升级，后续波次必须用恢复后的伤害。因为死亡结算只遍历当前名单，击杀后已离场的 Infested 尸体不会在下一波伤害前生成 Wriggler。对照 `CardOnPlaySupport.Batch042.ApplyEchoingSlash`。
- **Burst**：`BurstPower.ModifyCardPlayCount` 给技能牌 +1 次重放，`AfterModifyingCardPlayCount` 减一层，`EndTurnRegular` 移除。对照 `ModifyCardPlayCountMirrors` 与 `CorePowerSupport` 的 Burst 分支。
- **Shadowmeld**：`ShadowmeldPower.ModifyBlockMultiplicative` 按 2^层数放大其主人的格挡（不少于 96 层时取 `decimal.MaxValue`，与原引擎的溢出阀一致），侧回合末移除。对照 `ModifyBlockMultiplicativeMirrors.HandleShadowmeldPower`。
- **ToolsOfTheTrade**：`ModifyHandDraw` 加层数；玩家回合开始按层数弃牌。对照 `TurnStartPowerSupport` 的 ToolsOfTheTrade 分支、`PlanChoiceEffect.Discard`。
- **Mirage**：格挡 = `CalculationBase + CalculationExtra × 所有存活敌人的毒层数`，按 `Move` 卡牌格挡结算。对照 `CalculatedVarSpecRegistry` 的 Mirage。
- **TheHunt**：攻击前记录目标是否允许致命击杀，击杀后施加 `TheHuntPower` 并记 30 长线资源。战斗内额外的卡牌奖励只存在于 `CombatRoom`，两个模拟状态都不表示它，评估按原引擎的 `missedTheHuntRewards` 扣分。对照 `CorePowerSupport` 的 TheHunt 分支。

### 1.2 遗物

- **Tingsha**：玩家侧弃牌后随机打 3 点 Unpowered 伤害。
- **HelicalDart**：Shiv 打出后施加 1 层临时敏捷（`HelicalDartPower`）。对照 `AfterCardPlayedMirrors.HandleHelicalDart`。
- **SneckoSkull**：通过 `ModifyPowerAmountGiven` 查询，给玩家施加的毒 +1。对照 `PersistentRelicSupport.ModifyPowerAmountGiven`。
- **PaperKrane**：在 `WeakPower` 的乘数里，对玩家目标减 0.15。对照 `ModifyDamageMirrors.HandleWeakPower`。
- **BeltBuckle**：战前没有药水时，根快照里已经给了 2 敏捷；`AfterPotionUsed` 在最后一瓶药用掉、且本场还没补过时再给 2 敏捷，并置遗物计数（对应 `TriggerRelicsAfterPotionUsed` 里 `StatefulRelicState.Current` 的门控）。
- 弃牌相关的遗物派发对照 `AfterCardDiscardedMirrors`。Pen Nib 见 [7.2](#72-history_course-与-pen-nib)。

## 2. 故障机器人：充能球

来源：`S/CombatPredictionSimulator.Orb.cs`、`S/SimOrbQueue.cs`、`M/Orbs/**`、`M/Hooks/Orb/**`、`P/OrbLifecycleSupport.cs`、`P/EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs`，以及回合阶段中调用球的位置。

**状态与导入。** 球按原引擎 `SimOrbQueue.Orbs` 的顺序存在 `Battle.Orbs`，私有值放在 `Orb.Aux`（见 [状态布局](combat-state-layout.md#35-球)）。导入时，容量超过 10、球数超过容量、没有 Sts2Solver 定义的球、私有值不是 `int` 能表示的整数，都按 `ImportException` 拒绝。角色基础槽数 `BaseOrbSlotCount` 进 `Rules`。两个追加计数器：`LightningChannels` 导入 `CalculatedVarSpecRegistry.CountLightningChannels`（原版历史 + 预测历史）；`OrbChannels` 只统计预测历史里的充能，对应原引擎的 1000 次充能安全上限。

**充能。** `Kernel.ChannelOrb` 对应 `CombatPredictionSimulator.OrbChannel`：

1. 战斗正在结束或有待处理选择时不做。
2. 基础槽数为 0 且容量为 0 时，先加 1 槽（`OrbCmd.AddSlots`）。
3. 容量已满时，先 `EvokeNext` 激发最左侧的球，再入队。如果激发的副作用又同步充能、把槽填满，原引擎会记录 `MethodMirrorIncomplete` 风险并放弃；Sts2Solver 抛 `UnsupportedException`，不自行追加激发（K0 合同覆盖了满槽先激发）。
4. 容量仍为 0 时返回 false。成功入队后记录 `OrbChannels`，闪电球另记 `LightningChannels`，再派发 `AfterOrbChanneled`。预测历史达到 1000 次充能时显式拒绝。

`AddOrbSlots` 按 `min(10 - capacity, amount)` 增长；`RemoveOrbSlots` 对应 `SimOrbQueue.RemoveCapacity`，从队尾删球，不触发任何球的 Hook。

**激发。** `EvokeNext` 对应 `OrbEvokeNext`、`OrbCmd.EvokeNext`：取最左侧的球，按 `repeat` 重复，只有最后一次出队（Dualcast、MultiCast、Shatter 的 `dequeue:false` 语义），每次激发后执行 `ApplyEnemyDeathPowers`。`EvokeOrb` 对应 `OrbEvoke`：按需要出队，调用球的 `Evoke`，把返回值写进目标 owner 列表后派发 `AfterOrbEvoked`；战斗正在结束或有待处理选择时不执行。MultiCast 这类 X 次激发由卡牌读取 `ResolveEnergyXValue` 后传入。Darkness、TeslaCoil、Loop 的额外被动走 `OrbPassiveAt`，不查询触发次数 Hook。

**被动。** `TriggerOrbPassive` 对应 `OrbModel.TriggerPassive`：先用纯查询 `ModifyOrbPassiveTriggerCounts` 算次数（`GoldPlatedCables` 对首个球 +1；`AfterModifyingOrbPassiveTriggerCount` 只有展示用的监听者，Sts2Solver 不派发），再逐次 `Passive`，每次后执行 `ApplyEnemyDeathPowers`。`OrbPassiveAt` 对应 `OrbCmd.Passive`，跳过触发次数 Hook。两者在战斗正在结束或有待处理选择时都不执行。`ModifyOrbValue` 是纯查询，按战斗监听顺序调用：`FocusPower` 加集中并钳制到 ≥ 0，只修正需要修正的球值。

**回合中的位置。**

- 玩家回合末：在 `BeforeSideTurnEnd` 与胜负判定之后、虚无与回合末在手效果之前，按队列快照逐个调用 `BeforeTurnEndOrbTrigger`（等离子球的这一触发为空）。每次触发和被动后的敌人死亡效果立即结算；战斗进入结束后，后续触发无效果。
- 玩家回合开始：在敌人死亡效果与苦难归一化之后、自动预打出之前，按队列快照调用 `AfterTurnStartOrbTrigger`。只有等离子球重写了它，触发一次被动（仍经 `TriggerOrbPassive`，所以金缆的次数修正生效）。
- 玩家死亡清空球队列时，容量一起归零（`OrbQueue.Clear`）。

**球值。**

| 球 | 被动 | 激发 |
| --- | --- | --- |
| 闪电 | 3 点伤害，随机目标 | 8 点伤害 |
| 冰霜 | 给所有者 2 格挡 | 5 格挡 |
| 暗 | 把 `Modify(6)` 累加进 `Aux` | 对当前生命最低的可命中敌人造成**原始 `Aux`**：原版 `EvokeVal` 不再经过 `ModifyOrbValue`，集中只影响累积 |
| 玻璃 | 取 `Modify(Aux)`，大于 0 时造成伤害并把 `Aux` 减 1 | `Modify(Aux) × 2`，伤害所有可命中敌人 |
| 等离子 | +1 能量 | +2 能量 |

闪电和玻璃的随机目标各取一次 `CombatTargets` 流的 `NextInt(0, count)`，候选为空时不消耗；闪电的显式目标（TeslaCoil）不消耗该流。Chaos 与 TrashToTreasure 的随机球取 `CombatOrbGeneration` 流，候选顺序是原版 `_validOrbs`：闪电、冰霜、暗、等离子、玻璃；TrashToTreasure 的每层都先取随机球再尝试充能，充能被拒绝不会停止后续层。

### 2.1 遗物

- **CrackedCore**：第一回合玩家侧 `BeforeSideTurnStart` 充能一个闪电球。
- **RunicCapacitor**、**SymbioticVirus**：第一回合玩家侧 `AfterSideTurnStart` 分别增加 3 槽、充能一个暗球。
- **GoldPlatedCables**：实现触发次数 Hook。
- **PowerCell**：与原引擎的 `RegisterIgnored<PowerCell>` 一致，是准入需要的空定义。
- **ReptileTrinket**：`AfterPotionUsed` 里按遗物变量施加临时力量（`ReptileTrinketPower`），对应 `TriggerRelicsAfterPotionUsed`。

### 2.2 卡牌与药水

- **Leap**：通用格挡。
- **BoostAway**：格挡后生成一张 Dazed 到弃牌堆（`CardEffectSpecRegistry` 的 BoostAway 分支）。
- **MeteorStrike**：单体攻击后充能 3 个等离子球（`OrbCardMirrors.MeteorStrikeOnPlay`）。
- **MomentumStrike**：攻击后给当前打出的牌加一条本场绝对 0 费修饰（`CardEffectSpecRegistry` 的 MomentumStrike 分支；`CurrentPlayCard` 取当前打出区的槽位）。
- **Quadcast**：`EvokeNext(Repeat)`，只有最后一次激发出队（`OrbCardMirrors.QuadcastOnPlay`）。
- **EssenceOfDarkness**（药水）：按当前球槽容量逐个充能暗球（`OrbPotionMirrors.EssenceOfDarknessOnUse`）。
- 多人限定卡（EnergySurge、Ignition、ImitationLearning、Hibernate、OneForAll）在单人 `FilterForCombatAndPlayerCount` 中被排除，按原版不定义。

## 3. 储君：星能与锻造

来源：`S/CombatPredictionSimulator.Energy.cs`、`S/CombatPredictionSimulator.Card.cs`、`E/CombatPredictedCardExtensions.cs`、`P/PowerLifecycleSupport.cs`、`M/Hooks/Resources/AfterStarsGainedMirrors.cs`、`M/Hooks/Card/ModifyStarCostMirrors.cs`、`P/PersistentPowerSupport.Forge`、`Q/SimulatedCombatState.CardLifecycle.cs`、`Q/SimulatedCombatState.AutoPlay.cs`。

**状态。** 星能是 `Battle.Stars`，导入直接取 `playerState.Stars`。卡牌的 `BaseStarCost` 来自原版同名字段（没有星费为 -1），临时星费取 `_temporaryStarCosts` 最后一条（多条不一致时按容量拒绝）。原版的 `LastStarsSpent` 是出牌期瞬时值，放在栈上的 `Exec.StarValue`，不进入稳定状态。

**费用。** `StarCostWithModifiers` 对应 `GetStarCostWithModifiers`：X 星费卡返回当前全部星能；否则基础或临时星费只在非负时交给 `TryModifyStarCost`（BrilliantScarf、VoidFormPower），最后 `(int)` 截断并钳制到 ≥ 0；X 星费不派发费用 Hook。`ShouldPayExcessEnergyCostWithStars` 没有原版监听者，Sts2Solver 不做能量超额折算。`ResolveStarXValue` 读 `StarValue`，再经过 `ModifyXValue`（Stardust）。

**获得。** `Kernel.GainStars` 对应 `PlayerCmd.GainStars`：战斗正在结束时不获得；`ShouldGainStars` 没有原版监听者；星能钳制到 [0, 999999999]；正数获得同时累加 `StarsGainedThisTurn`（Radiate 的命中次数乘数，玩家回合开始清零）；随后派发 `AfterStarsGained`（BlackHolePower）。各卡在原引擎的同一位置调用它：Venerate、HiddenCache、Convergence 在 `CardOnPlaySupport` 的位置，SolarStrike、Glow、ShiningStrike 在 `CardEffectSpecRegistry` 的位置，BigBang 按 能量 → 星能 → 锻造。

**消耗。** `ManualPlay` 在卡进入打出区之前重算星费，写入 `StarValue`，扣星能后派发 `AfterStarsSpent`。原引擎这里的顺序不是监听者顺序：先 `TriggerRelicsAfterStarsSpent`（GalacticDust、MiniRegent，按遗物表顺序），再按 `EffectivePowers()` 顺序跑 `ChildOfTheStarsPower`。Sts2Solver 因此分“先遗物、后 Power”两遍派发。自动打出不支付星能，但在 `skipXCapture` 为假时同样记下 `StarValue`。

**锻造。** `Kernel.Forge` 对应 `PersistentPowerSupport.Forge`：

1. 有待处理选择时不执行。
2. 手牌、抽牌堆、弃牌堆、打出区都没有未消耗的 `SOVEREIGN_BLADE` 时，创建一张规范 0 级君王之刃到手中，走 `AddGeneratedCardToCombat` 的进战斗与生成卡 Hook。
3. 给**所有**牌堆里非复制的君王之刃的伤害变量（`Var0`）加 amount。

`CreatedThroughForge` 只驱动原版视觉，不进入状态。君王之刃的攻击次数来自 `Repeat` 变量（恒为 1，没有原版效果调用 `SetRepeats`）；`SeekingEdgePower` 把它改为全体目标，`ParryPower` 提供格挡。锻造卡和 `FurnacePower` 在 `GeneratedCards` 里声明君王之刃，规则闭包因此冻结它两个等级的数值。

`SwordSagePower` 的重放加成按原引擎 `NormalizeSwordSageReplays` 归一化：根快照里的刀带着根上冻结的层数；根之后锻造出的非克隆刀从 0 起算，再按当前层数补差；克隆从当前层数起算。`Card.Private` 记录已应用的加成。

**自动预打出。** 玩家回合开始、回合开始球触发之后，`Phase = AutoPrePlay`，依次：

1. 按消耗堆顺序自动打出阶段开始时就在消耗堆里的 `BOMBARDMENT`（每次打出后回到消耗堆；记下的数量与当前数量必须一致，否则显式拒绝）；
2. 按 `MayhemPower` 层数把抽牌堆顶的牌移到打出区，依次自动打出（对应 `MoveCardsForAutoPlay` 与 `TriggerScheduledAutoPlays`）；
3. 每个未熔化的 `HISTORY_COURSE` 复制上一玩家回合最后一张攻击牌并自动打出（见 [7.2](#72-history_course-与-pen-nib)）；
4. Imbued 附魔的自动打出。

完成后 `Phase = Play` 并做胜负判定。

**选择与回合开始选择。**

| 来源 | 请求 | 结算 |
| --- | --- | --- |
| DecisionsDecisions | `AutoPlayRepeated`：手牌中可打出的技能 | 按 `Repeat` 次数自动打出所选牌。第一次按所选的手牌位置找牌，之后跟着上一次打出后的落点（`MoveCard` 的插入下标）；打出过程中值变了取尾部钩子后的值，位置失效才退回按值定位 |
| HeirloomHammer | `Duplicate`：无色手牌 | `CloneCard` 生成 `Repeat` 份到手牌 |
| Charge、Guards、Begone | `Transform` | `Kernel.TransformSelectedTo` 按“值 + 同值第几张”定位，在 `CardChoiceSupport.TransformSelectedCards` 的同一位置原位替换为 1 级或 0 级的 Minion 令牌 |
| CosmicIndifference、PhotonCut、Glimmer | `MoveToDrawTop` | — |
| TyrannyPower | 玩家回合开始（`AfterPlayerTurnStart`）请求 `Exhaust` | 选择由结束回合动作携带 |
| ForegoneConclusionPower | `BeforeHandDraw` 时需要的话先洗牌，再请求 `MoveToHand`，然后移除自身 | 同上 |

一个动作最多携带 `Limits.MaxChoices`（当前 8）个选择。DecisionsDecisions 的嵌套自动打出可能需要更多，超出时按 `CapacityException` 丢弃这个分支。

**历史计数与卡牌钩子。**

- `CardsGeneratedThisCombat` 在原引擎 `History.CardGenerated` 的同一位置加一：`AddGeneratedCard`（含战斗结束时被拒绝加入的生成卡）以及 `TransformSelectedTo`、`ReplaceCard` 的插入。Supermassive 按 `CalculationBase + ExtraDamage × 生成数` 计算伤害。
- `Enemy.PoweredAttackHitsThisTurn` 在 `RecordDamageReceived` 里对“玩家打敌人的强化攻击”加一（完全格挡也计，非强化伤害不计），玩家回合开始清零，字节溢出抛 `UnsupportedException`。`BeatIntoShape` 用攻击前的命中数计算锻造量：攻击自身的命中在 `CalculatedForge - Results.Count × CalculationExtra` 里抵消。
- `KinglyKick`、`KinglyPunch` 在 `AfterCardDrawn` 用 `TakeIntrinsicDraw` 认领自身，分别追加 `AddThisCombat(-1)`，以及 `Damage`、`Private` 累加。
- `MakeItSo` 在 `AfterCardPlayedLate` 按本回合已完成的技能数取模，满足时回手。
- `TheSealedThronePower` 在 `BeforeCardPlayed` 给星能。
- `CollisionCourse` 生成 `DEBRIS`；`MeteorShower` 依次对全体施加 Weak、Vulnerable。

**VoidForm。** 打出 `VoidForm` 时，先按 `PrepareVoidFormApplication` 把已有 `VoidFormPower` 的 `Aux0`（原引擎 `VoidFormPredictionState.CardsPlayedThisTurn`）置为 999999999，再施加 `VoidFormPower`，最后请求结束玩家回合。`VoidFormPower` 的普通能量 Late 钩子和星能钩子只在牌位于手牌或打出区、且 `Aux0 < Amount` 时返回 0；`AfterCardPlayed` 只对非自动打出的最后一段加一；`BeforeSideTurnStart` 在玩家参与时清零。导入优先取 `VoidFormPredictionState`，没有时读模型内部计数。评估照原引擎 `VoidFormOpportunityValue` 计算剩余免费次数的手牌节省价值。

**遗物与 Power。**

- GalacticDust、MiniRegent 的计数取 `StatefulRelicState.Current`；Regalite 取 `RegalitePredictionState.UsedThisTurn`；BrilliantScarf 取 `CounterPredictionState`（非自动出牌递增、玩家侧回合开始清零，计数达到 `Cards - 1` 时能量和星能费用都变 0）。
- ChildOfTheStarsPower 在 `AfterStarsSpent` 给格挡；GenesisPower、StarNextTurnPower 在 `AfterEnergyReset` 获得星能（后者随后移除）。
- ArsenalPower、PillarOfCreationPower 在 `AfterCardGeneratedForCombat` 分别给力量、格挡；FurnacePower 在 `OwnerAfterSideTurnStart` 锻造。
- ConquerorPower 让君王之刃对持有者造成双倍伤害，并在侧回合末减层；ReflectPower 反弹被格挡的强化攻击伤害，并在侧回合开始减层。
- DyingStarPower 与 MonarchsGazeStrengthDownPower 是负向临时力量，在持有者侧回合末归还。

## 4. 亡灵契约师：Osty、Doom 与灵魂

来源：`S/CombatPredictionSimulator.Damage.cs`、`Attack.cs`，`Q/SimulatedCombatState.CardLifecycle.cs`、`Q/SimulatedCombatState.Doom.cs`、`P/MonsterMoveSemantics.cs`、`P/BeforeSideTurnEndMirrors`、`P/EndTurnPowerSupport`、`P/PowerLifecycleSupport`，以及各卡牌的镜像与补偿位置。

**状态。** `Battle.Osty` 是玩家侧宠物。`OstyPresent` 表示宠物存在（死亡后仍在），`OstyEverPresent` 表示原生 `Player.Osty` 已非空（根捕获时存在，或搜索中首次召唤后；`DieForYouPower` 让死亡的宠物仍留在这个字段里，所以它一旦置位就不再清除）。Osty 的生命和格挡用 `Creature`，Power 的 owner 是 `Limits.OstyOwner`；监听顺序排在玩家的 Power、遗物、药水之后，敌人之前。

**召唤与治疗。** `SummonOsty` 对应原引擎对 `OstyCmd.Summon` 的实现：不存在时创建（消耗一个 `NextCombatId`，以 amount 初始化生命，施加 1 层没有 applier 的 `DieForYouPower`）；存活时提高最大生命并等量治疗；已死亡时保留对象，按 amount 重置。`HealOsty` 只加当前生命，不超上限。Osty 死亡不触发玩家死亡、不移出战斗、不清 Power；玩家死亡时，在清空球之后以 `force` 击杀仍存活的 Osty。

**代受伤害。** 格挡按宠物的主人取（Osty 被打时消耗玩家的格挡）。先跑 Osty 之前的 `ModifyHpLost`，再由 `ModifyUnblockedDamageTarget` 链式选接收者（`DieForYouPower` 在 Osty 存活、目标是主人、强化攻击时返回 Osty），然后对接收者跑 Osty 之后的修正。接收者变了时产生两个 `DamageResult`：Osty 先承受，溢出的 `OverkillDamage` 经 Osty 之后的修正再打给原目标；两者的格挡字段都取自原目标。怪物行动的攻击在 `Damage` 之前临时清零“已死 Osty 的 DieForYou”，之后恢复（等价原引擎 `MonsterMoveSemantics.DamagePlayer`）；非攻击伤害不重定向。

**Osty 攻击。** 带 `CardTag.OstyAttack` 的牌以 `OstyOwner` 为攻击者调用 `CardAttack`，力量、虚弱和攻击历史都归宠物；宠物已死时直接返回。攻击结束后 `OstyAttacksThisTurn` 加一；这个计数在 `OstyEverPresent` 时于玩家回合开始清零（原引擎 `BeginSideTurn(player)` 走原生 `ownerPlayer.Osty`，搜索中召唤的宠物同样会出现在该字段）。

**伤害来源归属。** 原版判断“是不是玩家造成的”时并不总是比较 `dealer == 玩家`：部分钩子还把 `dealer.PetOwner`（Osty）算进来，或只看卡牌来源的持有者。内核按同一条条件实现：SkittishPower 在攻击上下文的 `AfterAttack` 里读该敌人的第一条伤害结果（Osty 攻击也触发）；PenNib、TheBoot、HandDrill 的 dealer/breaker 条件接受 `Owner.Creature || Owner.Osty`；StrikeDummy/FakeStrikeDummy/MiniatureCannon 只看卡来源是否属于玩家；GigantificationPower 按卡来源的 owner 加伤并消耗，Osty 攻击同样命中；VulnerablePower 的 Cruelty 修正回退到 dealer 的 PetOwner。PaperPhrog 的原版查询走 `dealer.Player`，因此 Osty 攻击不吃 PaperPhrog，内核保持这一差别。

**Doom。**

- 敌方侧回合末：对每个存活参与者求该侧的 Doom 总和，第一个满足 `CurrentHp <= Doom` 的监听者触发一次成批的 `DoomKill`，位置在 `TriggerEnemySideTurnEndEffects` 开头、Regen 与 Plating 之前。
- 玩家侧回合末：走 `EndTurnRegular` 的单监听者 `Kill`。
- 原版 `DoomPower.DoomKill` 逐个 `CreatureCmd.Kill`（每次击杀立即完成 `AfterDeath` 和 Power 移除），全部击杀后才派发 `AfterDiedToDoom`。Sts2Solver 与原引擎都在每次 `Kill` 后结算这一批的敌人死亡效果，最后按 `ShouldOwnerDeathTriggerFatal` 统计每个玩家的致命击杀数（排除玩家本身），再按已定义的 `BookRepairKnife` 治疗；没有定义时导入阶段拒绝该根。
- `DoomAppliedThisTurn` 记录玩家本回合是否施加过正层 Doom（DeathsDoor），玩家回合开始清零。已经死亡的生物不接收 Power：有 Osty 时 `DieForYouPower.ShouldAllowHitting` 对任何死亡生物返回 false，即使死亡清理还没跑完也一样。

**其它计数。** `EtherealPlays` 是整场计数：导入根历史后，每次打出完成时，按这张牌本地的虚无关键字加一（BansheesCry、PullFromBelow）。

**Melancholy。** `AfterDeath` 用 `State.FindCard(card)` 找到战斗副本，对每个战斗副本按 `Energy` 变量追加本场 −费用；牌组里的局外副本找不到战斗副本时不产生效果。Sts2Solver 只导入战斗副本，所以这类牌组副本按惰性放过（`DeckCopyOutsideCombatIsInert`）。

**Power 的结算时机。** 本角色的 Power 逐条对应原引擎 `HookMirrors`、`PowerLifecycleSupport`、`TurnStartPowerSupport`、`PersistentPowerSupport` 里的位置。值得注意的几个：

- Oblivion 在 `BeforeCardPlayed` 把当次层数放进栈上的 `CardPlayPairs`（`OblivionDoom`），`AfterCardPlayed` 取出并施加等量 Doom；稳定边界为空。
- Debilitate 的易伤、虚弱倍率并入 Vulnerable、Weak 的计算。
- SleightOfFlesh 在 `AfterPowerAmountChanged` 对“玩家施加给敌人的非临时减益”造成等于自身层数的 Unpowered 伤害。

**AfterHandEmptied。** 原引擎没有通用的遗物 Hook，`TriggerRelicsAfterHandEmptied` 只特判 `UnceasingTop`。Sts2Solver 在卡牌移到结果牌堆之后（最外层效果、允许的回合阶段内）做同样的特判：手牌为空、抽牌堆或弃牌堆非空时抽一张。

**卡牌修正。** Flatten：任意一次 Osty 攻击后，给战斗区内每张 Flatten 追加一条 `SetThisTurn(0)`（Absolute、EndOfTurn，按次数累积）；进入战斗的 Flatten 在本回合已有 Osty 攻击时同样追加。RightHandHand 的 `AfterCardPlayedLate` 不检查打出的是不是自己：任意一次能量值达到其 `Energy` 变量的打出，都会把弃牌堆里的每张 RightHandHand 收回手牌（`HandleRightHandHand` 的同一条件）。

### 4.1 其它卡牌与遗物

- **SentryMode**：`SentryModePower.BeforeHandDraw` 按层数生成 `SweepingGaze` 令牌入手；SweepingGaze 是 0 费、虚无、消耗的 Osty 随机目标攻击。对照 `TurnStartPowerSupport` 的 SentryMode 分支。
- **CallOfTheVoid**：`CallOfTheVoidPower.BeforeHandDraw` 每次从角色池（`CharacterAll`，已排除 Basic、Ancient、Event 与不可生成的卡）做一次 `TakeRandom(1)`，给生成的卡补上虚无后入手。
- **Misery**：给锁定目标的每个减益（层数、applier、`Target`）拍快照，攻击后按可命中敌人顺序克隆到其他敌人，每次克隆后排空 Power 数量变化队列；目标带临时 Power 时显式抛 `UnsupportedException`（缺少实例内部属性的合并）。对照 `BespokeCardMirrors.MiseryOnPlay`。
- **Pagestorm**：`AfterCardDrawn` 只检查卡牌本地的虚无关键字，命中后抽层数张。对照 `AfterCardDrawnMirrors.HandlePagestormPower`。
- **DrainPower**：单体攻击后，从弃牌堆的**可升级**卡里等概率取 `Cards` 张并升级（`CardSelectionCardMirrors.DrainPowerOnPlay` 的过滤 + 完整 Fisher-Yates + `CombatCardSelection` 流）。原引擎的 `Upgrade` 在战斗正在结束时什么也不做，所以击杀时的 DrainPower 仍然消耗随机数、但不升级任何牌；Sts2Solver 选完目标后按同一门控跳过写入。
- **BigHat**：唯一的战斗内触发是玩家第 1 回合开始生成 2 张虚无角色牌，发生在根捕获之前；原引擎也没有对应镜像，定义为空。

## 5. 通用卡牌与 Power

这一节是和角色无关、但结算口径不显然的卡牌与 Power，很多是怪物施加给玩家的。

**攻击后抽牌的死亡结算。** 原引擎 `CorePowerSupport.ApplyCardPowers` 在镜像攻击之后、卡牌专属补偿之前结算敌人死亡。PommelStrike、DaggerThrow、FlashOfSteel、MinionStrike、PhotonCut、RocketPunch、SweepingBeam、CompileDriver、Ftl 因此在攻击和抽牌之间调用 `ApplyEnemyDeathPowers`，让抽牌时看到的 `HittableEnemies` 和 Hellraiser 与原引擎一致。

**依赖本次出牌伤害的卡。** 这几张读的是本次出牌期间的瞬态伤害合计（字段见 [状态布局](combat-state-layout.md#42-动作期间的瞬态exec)）：

- **Fisticuffs**：镜像攻击后先结算敌人死亡，再按本次出牌全部命中的伤害合计获得格挡（`CorePowerSupport` 的 Fisticuffs 分支）。
- **Omnislice**：第一击后结算死亡，再把最近一次伤害的“格挡 + 未格挡 + 溢出”以 `Unpowered | Move` 打给目标以外的所有可命中敌人（`CardOnPlaySupport.Batch042.ApplyOmnislice`）。原始命中与复制命中在同一个 `AttackContext` 里：`BeforeAttack` 一次、`AfterAttack` 在两者都结束后一次，`AddHit` 不增加出牌的攻击历史计数。它的死亡结算也只遍历当前名单，所以击杀后离场的 Infested 尸体不会在复制伤害前生成 Wriggler。
- **HandOfGreed**：镜像攻击后检查本次出牌是否击杀了目标、且目标所有 Power 都允许致命（`ShouldOwnerDeathTriggerFatal`），成功则加金币和 `LongTermResourceValue`。BowlerHat、Ectoplasm、DragonFruit 这类金币修正遗物只影响战斗外的金币收入，已有空定义或能量定义，不改变战斗内结算。
- **GoldAxe**：`FinishedCardPlays` 计数器导入根历史的 `GetFinishedPlaysBeforePrediction`，每次完成出牌加一，作为 `CalculatedVarSpecRegistry` 的乘数。

**GalvanicPower 的反伤打出牌的人。** 原引擎 `AfterCardPlayedMirrors.HandleGalvanicPower` 的伤害目标是打出这张牌的人（`PreviewCard.Owner`），不是 Power 的持有者。Globe Head 身上挂着它，玩家每出一张牌，反伤都落在玩家身上。Sts2Solver 里所有牌都属于玩家，所以定义直接以 `Limits.PlayerOwner` 为目标。

**DampenPower（MagiKnight）。** 被降级的升级牌降到 0 级后仍可能被读取或打出，所以 `RulesCompiler` 为根上的升级牌补上 0 级规则。`DowngradeCard` 在 `AfterDowngraded` 之后，按 `PreservesVar0OnDowngrade` 恢复 Rampage、TheScythe 的累计伤害（原引擎的 `ExtraDamageFromPlays`、`IncreasedDamage`）。克隆、变化、替换产生的是新对象，`DampenOriginalUpgrade` 清零；离开战斗牌堆的 Dampen 记录按退役处理，不再被结算读取。牌在出牌中途被降级又被恢复时，`AddCurrentCardVar`、`SyncCurrentPlayCard` 以 `Exec.PlayPosition` 上的实时牌值为准（Rampage、TheScythe 在攻击后同步；原引擎读的是可变的 `CardModel`）。

**ChainsOfBindingPower（Queen）。** 原引擎 `BeforeCardPlayedMirrors.HandleChainsOfBindingPower` 跳过的是 `CardModel.IsDupe`，不是 `IsClone`：复制品在准入时已经拒绝，克隆（Swift、AdaptiveStrike）必须照常设置束缚标记。`Aux1` 是原版按玩家回合统计的 `CardAfflictedEntry` 数：回合末只清打出标记和 Bound 苦难，`Aux1` 保留到下一个玩家回合开始才清零。不能提前在 `BeforeSideTurnEnd` 重置，否则回合末状态会和原引擎的 `ChainsOfBindingPredictionState` 不一致。

**RampartPower（Turret Operator）。** 原引擎 `PersistentPowerSupport.TriggerRampart` 遍历 `EffectivePowers()`，只按数量过滤；Living Shield 已经离开名单时，它的 Rampart 行仍会派发。`RampartPass` 因此直接按 Power 表的行顺序遍历，不用会跳过离场 owner 的 `CollectListeners`。

**HellraiserPower。** 本回合的无限自动打出计数（`InfiniteAutoPlaysThisTurn`）存在 `Power.Aux0`，导入时状态条目优先，没有时取模型内部数据。`HittableEnemies.All(HpDisplay.IsInfinite())` 先按原生 `IsHittable` 排除已死/正在复活的敌人（TestSubject 的复活窗口因此得到空集合，`All` 为真）；非空时，Waterfall Giant 的蒸汽爆发或 `HardenedShellPower` 本回合累计伤害达到层数都会让 `HpDisplay` 成为无限。计数达到 9 后不再自动打出但仍然自增，有限分支清零，`EndTurnBatch048` 在 owner 参与时清零。

**GigantificationPower。** 原引擎的 `PowerAmountPredictionState` 在动作边界把数量写回模型；玩家在同一次动作中死亡时，`BeforeAttack` 已登记的层数会把被死亡清理清零的 Power 恢复回来。Sts2Solver 在 `BeforeAttack` 标记 `PowerFlags.PendingAmountState`，`HandlePlayerDeath`、`RemovePowersAfterDeath` 保留带这个标记的行，`Kernel.Step` 在稳定边界清除标记。

## 6. 事件内容

事件卡不属于任何角色的初始或奖励池，但会进入牌组并参与战斗，结算落在通用管线里。字段编码见 [状态布局](combat-state-layout.md#36-卡牌)。

- **动态卡牌类型。** `MAD_SCIENCE` 的原生 `CardModel.Type` 是保存属性 `TinkerTimeType` 的覆盖，目标类型、结果去向和出牌计数都随之变化。规则表仍按卡牌 Id 冻结规范数值（生成池和指纹用它）；结算和 `Scorer` 用 `CardDef.ResolveCardType` 读实时类型，目标类型也按卡值覆盖（攻击为 `AnyEnemy`，其余为 `Self`）。`CardStats.HasDynamicType` 是根级开关，没有动态类型卡的根只多一次布尔判断。`Private` 的低、高字节保存类型与骑手，由 `BattleImporter` 从原版两个保存属性投影，非法的 `None` 按 `UnrepresentableState` 拒绝。评分里这张卡的 `LatentCardSetupValue`、`LatentTrait` 按 Power 类型与 `Curious` 骑手照原引擎 `CombatBeamSolver.StateEvaluation` 取 10 或 5。
- **MAUL。** `_extraDamageFromMaulPlays` 同时写进 `Var0`（伤害变量偏移）与 `Private`，`AfterDowngraded` 用 `Private` 恢复 `Var0`；克隆复制该值，与原版复制卡牌模型字段一致。
- **实例格挡。** `TORIC_TOUGHNESS_POWER` 每个实例一行，`Amount` 是剩余回合，`Aux0` 是格挡清零时提供的格挡量。同一 `(owner, id)` 的两个实例可能在同一次 `AfterBlockCleared` 里先后移除、导致表下标移动，所以卡和 Power 定义都声明 `IsIdentityContent`，让派发走按出现序号跟踪移除的路径。格挡量在原版是 `decimal`，投影要求非负整数，否则显式拒绝。
- **结果去向与下回合资源。** `REBOUND_POWER` 用 `ModifyCardPlayResultLocation`、`AfterModifyingCardPlayResultLocation` 把下一张牌的去向从弃牌堆改为抽牌堆顶，并减层；`HELLO_WORLD_POWER` 在 `BeforeHandDraw` 按 `AmountOnTurnStart` 次 `TakeRandom` 取普通角色牌；`FEEDING_FRENZY_POWER` 是 `TemporaryStrengthPower`，在持有者侧回合末退役；`WRAITH_FORM_POWER` 在持有者侧回合开始扣敏捷。
- **战斗外的 Power 与成长收益。** `FORBIDDEN_GRIMOIRE_POWER`、`IMPROVEMENT_POWER` 的原生唯一钩子是 `AfterCombatEnd`，原引擎的战斗模拟不派发它，战斗内只保留计数行，也不需要投影私有状态。`FORBIDDEN_GRIMOIRE` 的 50 长线资源照原引擎记入计数器（`RecordLongTermResource(50)`），并在施加 Power 后记录 `ForbiddenGrimoire` 成长收益；`MAD_SCIENCE` 的 Improvement 骑手只在 `ImprovementPower` 实际增加时记录 `MadScience` 成长收益。
- **任务与令牌卡。** 四张 Quest 卡由事件加入牌组，按普通牌洗进战斗牌堆，原生不可打出、只有战斗外钩子，因此定义为空效果。`LUMINESCE` 是 `RadiantPearl` 生成的令牌，只获得能量。
- **事件怪物。** `FAKE_MERCHANT_MONSTER`、`MYSTERIOUS_KNIGHT` 的招式效果照原引擎 `MonsterMoveEffects` 移植；`ARCHITECT`、`BATTLE_FRIEND_*` 只有 `NOTHING_MOVE`。
- **战损假人的限时结局。** `BATTLEWORN_DUMMY_TIME_LIMIT_POWER` 层数大于 1 时只递减；最后一层（Amount 1）不再递减，直接置 `Outcome = EventDefeat`，并保留 Amount 1。敌方回合在 `TriggerEnemySideTurnEndEffects` 之后立即返回，跳过敌人死亡、毒、苦难归一化和下一轮准备，与原引擎 `SearchBoundaryReason.EventDefeat` 的早退一致。原引擎在 `CreatureCmd.Escape` 之前就返回，所以 Sts2Solver 也不移除假人。评分在 `EventDefeat` 上不做威胁投影（`ThreatProjection(currentHp, 0, 0)`，与原引擎评估的边界分支一致），终局键追加 `PolicyBoundaryRank(EventDefeat) = 4` 作为最后一个比较项。

## 7. 遗物与药水

来源：`Search/SimulatedCombatState.Relics.cs` 与 `Search/SimulatedCombatState.ReactiveRelics.cs`；药水逐条对应 `Prediction/PotionOnUseSupport.Use` 与 `Mirrors/Potions/OnUse/CardGenerationPotionMirrors.Generate`。额外回合（AmbergrisPower、PaelsEye）的回合流程见 [内核语义](combat-semantics.md#71-额外回合)。

### 7.1 药水

| 药水 | 效果 |
| --- | --- |
| `AMBERGRIS` | 按 `MaxHp × HealPercent / 100` 治疗，并给 1 层 `AmbergrisPower` |
| `BONE_BREW` | `SummonOsty(Summon)` |
| `COSMIC_CONCOCTION` | 去重取无色牌，升级后逐一入手 |
| `CUNNING_POTION` | 生成升级过的 Shiv |
| `FOCUS_POTION`、`POISON_POTION`、`POTION_OF_DOOM` | 施加对应 Power |
| `POTION_OF_CAPACITY` | `AddOrbSlots(Repeat)` |
| `POT_OF_GHOULS` | Soul 入手 |
| `ESSENCE_OF_DARKNESS` | 见 [2.2](#22-卡牌与药水) |

药水的通用结算顺序、随机药水和药水生成选择见 [内核语义](combat-semantics.md#4-出牌管线)。

### 7.2 HISTORY_COURSE 与 Pen Nib

这两件遗物都要识别“同一次出牌”，而嵌套自动打出会产生另一次出牌。

**HISTORY_COURSE。** 它只读上一玩家回合最后一张攻击牌的完整卡值，存在 `Battle.HistoryCourseCard`（见 [状态布局](combat-state-layout.md#311-historycoursecard)）：

- 每完成一张**非复制品**的攻击牌，就用打出区的实时值写入这个槽。
- 自动预打出阶段开始时，先取出并清空它（原引擎的两本字典在这时分开）；Mayhem 之后，每个未熔化的 `HISTORY_COURSE` 复制一份（`CloneCard`、去掉消耗关键字、清 `ReturnToHandNextTurn`、`IsClone = true`）并自动打出。
- 复制品的身份经栈上的 `Exec.CurrentPlayIsHistoryCourseDupe` 传播：它的结果去向是移出战斗（`PileType.None`），不写回“本回合最后一次攻击”；Feral、Rebound、ChainsOfBinding 等按 `IsDupe` 跳过的 Hook 在这里同样跳过。
- `Rules.HasHistoryCourse` 为假时，记录和复制都跳过。

**Pen Nib。** 第十次攻击的锁存保存触发它那次出牌的 `Exec.PlayIdentity`（在 `Relic.Aux`），不是布尔值。`ModifyDamageMultiplicative` 只在当前 `PlayIdentity` 与它相等、且 dealer 是玩家或玩家的 Osty 时加倍，`AfterCardPlayed` 只在 `LastPlayIdentity` 相等时清除。这样，弃牌选择里嵌套自动打出的 Sly 牌（另一次出牌）不会继承加倍。玩家死亡等原因留下的未完成锁存，由 `Rules.PenNibRelicIndex` 在稳定边界清掉。对照 `PenNib.ModifyDamageMultiplicative` 的 `dealer == Owner.Creature || dealer == Owner.Osty`，以及 `BeforeCardPlayedMirrors.HandlePenNib`、`ModifyDamageMirrors.HandlePenNib`、`AfterCardPlayedMirrors.HandlePenNib` 的 `AttackToDouble == CardSource.Original`。

### 7.3 其它有战斗内结算的遗物

| 遗物 | 结算 |
| --- | --- |
| `PAELS_LEGION` | 冷却在 `Counter`；用 `PlayIdentity` 把 `AfterModifyingBlockAmount` 与同一次出牌的 `AfterCardPlayed` 配对。触发中的 `CardPlay` 只在出牌期存在 |
| `MUSIC_BOX` | “本回合已用”在 `Counter`；被复制的攻击牌用同一 `PlayIdentity` 把 `BeforeCardPlayed` 与 `AfterCardPlayed` 配对 |
| `EMOTION_CHIP`、`PAELS_EYE` | `StatefulRelicState` 投影到 `Counter`、`Aux` |
| `BOOKMARK` | `HookMaskExt.AfterFlush`：回合末弃牌之后、清理之前 |
| `BIIIG_HUG` | `AfterShuffle` 生成 Soot |
| `BONE_FLUTE` | Osty 攻击后给格挡 |
| `METRONOME` | 按充能次数造成 Unpowered 伤害 |
| `THROWING_AXE` | 每场第一次出牌重放一次 |
| `SPIKED_GAUNTLETS` | 最大能量 +1，能力牌费用 +1 |
| `UNDYING_SIGIL` | 将死敌人打向玩家的强化攻击乘 `DamageDecrease` |
| `CROSSBOW`、`CHOICES_PARADOX` | 在玩家侧回合开始用冻结的卡池生成牌 |
| `TOASTY_MITTENS` | 用回合开始选择弃一张手牌，再加力量 |
| `LAVA_LAMP` | `AfterDamageReceived` 只记录本场是否受过可格挡的未格挡伤害（`TookDamageThisCombat`），存在 `Counter` 并在写回时写回 |
| `KUSARIGAMA` | 原生战斗进入结束（含 `PendingLoss`）时，`AfterSideTurnEnd` 不会派发。Sts2Solver 在玩家回合结束第二阶段的 `RelicPass(AfterSideTurnEnd)` 之前检查 `IsPendingLoss`，避免玩家在回合结束阶段死亡时错误清零 `Counter`；终局写回再按计数设置 `DisplayAmount`、`Status` |
| `FUR_COAT` | 命中的地图坐标在战斗外就固定了，导入时冻结为 `Rules.FurCoatSpawnHpOne`；`Kernel.AddCreatedMonster` 在入场 Power 之后、`PreparePredictedMonster` 之前，把新敌人的当前生命设为 1（`MonsterSpawnSupport.ApplyCreatureAddedRelics`） |
| `UNSETTLING_LAMP` | 按实例身份：`BeforePowerAmountChanged` 记录触发卡的来源序号和临时 Power 内部类型，`ModifyPowerAmountGivenMultiplicative` 对同一来源、同一种临时 Power 内部类型的首次减益翻倍，完成的锁存写入 `Counter`（见下） |
| `VAMBRACE` | `AfterCardPlayed` 用“触发卡的作用域序号”匹配；`OwnChoice` 弃置 Sly 牌触发的嵌套自动打出会完成自己的作用域，并改写 `LastCardPowerSourceSerial`，所以 `PlayFromPlay` 在嵌套打出返回后恢复外层的序号 |

**UnsettlingLamp 与“没有卡牌来源”的施加。** 原引擎的 `ApplyPowerFromSource(..., cardSource: null)` 会把“当前卡牌来源”压成 null，这时灯的观察和翻倍都不生效。Sts2Solver 用 `ApplyPowerWithoutCardSource`、`ApplyTemporaryStrengthWithoutCardSource` 在施加期间关闭 `CardPowerSourceActive`：Envenom、Concoct 的毒，ReaperForm、Underworld 的 Doom，MonarchsGaze 的临时力量降层都走这两个入口。否则卡牌作用域里非卡牌来源的减益会被错误翻倍。

**ChoicesParadox、ToastyMittens 的开局选择。** 两者的原版钩子在玩家第 1 回合开始、根捕获之前，所以搜索根里已经是选好的牌和弃掉的牌。之后的回合（`AfterPlayerTurnStart`），Sts2Solver 按同一个 `TurnStartChoiceSupport` 协议发出 `GenerateToHand`、`Exhaust` 请求，选择由结束回合动作携带。

### 7.4 没有原引擎镜像的遗物

- 有战斗内行为：`BOOK_REPAIR_KNIFE`（Doom 击杀后治疗）、`INFUSED_CORE`（闪电球值）、`LUNAR_PASTRY`（星能）、`PHYLACTERY_UNBOUND`（Osty 召唤）、`SEAL_OF_GOLD`（金币换能量）、`SNECKO_EYE`（抽牌数）、`WHISPERING_EARRING`（最大能量；也是自动预打出的计划来源，但没有战斗内效果）。
- 只在战斗外或根捕获之前生效，定义为空并注明原版 Hook：`BING_BONG`、`DARKSTONE_PERIAPT`、`DATA_DISK`、`DELICATE_FROND`、`DIAMOND_DIADEM`、`DIVINE_DESTINY`、`FAKE_SNECKO_EYE`、`FUNERARY_MASK`、`NINJA_SCROLL`、`RADIANT_PEARL`。
- 没有定义，导入时按 `unsupported-relic` 拒绝：`BYRDPIP`（原版宠物；原引擎不模拟 Osty 以外的宠物）、`DEPRECATED_RELIC`（Rarity None，无法获得）。
