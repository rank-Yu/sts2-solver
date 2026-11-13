# 战斗内核语义

[返回文档索引](README.md)

本文规定内核怎样执行一个动作：执行上下文、内容定义、Hook 派发、出牌、伤害、回合流程、抽牌、怪物 AI、选择协议和随机数。具体卡牌、遗物、怪物的细节见 [内容专属机制](combat-content.md)，字段编码见 [状态布局](combat-state-layout.md)。

内核照原引擎（CombatSolver `a62f1850c8586e5951e222eecb0dd34231e76d8a`，见 [术语](README.md#术语)）的结算顺序翻译，下文的步骤顺序都摘自它的源码。这些语义已经冻结在 `tests/Golden/data`。本文和快照或差分测试结果冲突时，以快照和测试为准，并回头修正本文；以后改语义以快照和实机证据为准。

每节开头的“来源”和条目里的“对照”都指原引擎的源码，不指向本仓库。

路径缩写（CombatSolver `a62f1850`）：`S/` = `src/Engine/InCombat/Simulation/`，`M/` = `src/Engine/InCombat/Mirrors/`，`E/` = `src/Engine/InCombat/Extensions/`，`Q/` = `src/Search/`，`P/` = `src/Prediction/`。

## 1. 执行上下文

内核每次执行一个完整动作，从一个稳定边界走到下一个稳定边界。中间的临时信息都在栈上，不进入状态，所以搜索可以随意复制和比较 `Battle`。

内核入口是静态方法，签名形如 `StepResult Step(ref Battle battle, in PlayerAction action, Rules rules)`。执行期间的临时信息放在栈上的 `Exec`（`ref struct`，持有 `ref Battle`、`Rules`、选择游标、当前出牌上下文），不进入状态。

- 动作执行到稳定边界才返回，返回值是 `Completed`、`NeedsChoice`（附选项规格）或 `Terminal`。
- 执行中途不允许分配托管对象；内容代码只读写 `Battle` 和栈上值。
- 未知内容 Id、未实现的 Hook 组合抛 `UnsupportedException`。容量溢出抛 `CapacityException`：在搜索展开中只丢弃这一个子状态，根上出现则整次求解作废（见 [状态布局](combat-state-layout.md#7-容量与失败)）。

### 1.1 结束回合的前缀共享

搜索时，同一个父状态下很多叶子都要执行结束回合，而其中“玩家回合结束 + 敌方回合”这一段对它们完全一样。所以内核把 `EndTurn` 拆成两段：第一次完整执行时（`Kernel.StepEndTurnSharingPrefix`），在进入 `AdvanceRoundPlayerStart` 之前把快照 `EndTurnPrefix` 直接写进调用方的存储，续跑期间不再改动；之后的叶子各自从快照续跑后半段（`Kernel.StepEndTurnFromPrefix`）。这只复用计算结果，不改变转移序列：每个叶子仍然逐一经过原来的池、去重和计数路径。

- 快照包含当时的 `Battle`，以及后半段可能读取的全部 `Exec` 瞬态：力量变化队列、伤害、击杀与溢出暂存、出牌身份与来源序号、手牌与生成选项、Monologue、Vambrace、Lamp 等。
- 拍快照时，选择游标、效果深度和嵌套 Sly 标记区间都必须为空，逐项断言。
- 遇到额外回合，或前半段已经请求选择，返回“不可共享”（`EndTurnPrefixStatus`），调用方改跑完整的 `Step`，不做近似；完整 `Step` 的额外回合仍使用同一套 setup 暂停语义。
- 前半段在玩家回合开始前就结束了战斗时，仍会拍快照；续跑跳过玩家回合开始，只做动作收尾，与完整 `Step` 相同。
- 后半段包含下一回合的 setup 暂停：首次选择仍会让出 `NeedsChoice`，前缀可以继续复用；侧回合开始、球与选择顺序和完整 `Step` 相同。

`Exec` 新增的字段会不会被漏进快照，由 KernelChecks 的 `ExecCoverage` 合同把关，见 [测试与验证](testing.md#43-搜索等价自检)。

## 2. 内容定义

内容定义只描述“这张牌、这个 Power 在某个时机做什么”，本身没有状态，数值从规则表读。这样同一份定义能被所有分支共享，热路径也不用分配对象。

每类内容一个抽象基类：`CardDef`、`PowerDef`、`RelicDef`、`PotionDef`、`MonsterDef`、`OrbDef`、`EnchantmentDef`、`AfflictionDef`。具体内容是 `sealed` 子类，覆写需要的 Hook 虚方法。

- 每个定义用特性声明原版 Id，例如 `[ContentId("BASH")]`。注册表在首次使用时扫描定义，分配连续的 `ushort` 内部 Id，并按"覆写了哪些 Hook"建立每个 Hook 的监听位图，派发时跳过非监听者。
- 动作 Hook 按快照派发，但每个监听者被调用前都会重新检查玩家是否仍能触发 Hook。`BattleFlags.PlayerHooksActive` 清零后，玩家拥有的 Power、遗物、药水、卡牌、苦难、附魔、球的监听都被跳过；死亡被阻止或复活期间这个标志保持，`BeforeDeath`、`AfterDeath` 不受影响。这与原版 `CombatState.Contains` 按 `Owner.IsActiveForHooks` 过滤一致。
- `HookMask` 的 64 位已经用满，之后新增的 Hook 放在 `HookMaskExt`。两者用同一套反射计算（`ContentDef.Hooks`、`HooksExt`），`HookDispatch.CollectListeners` 对两种掩码各有一个重载，收集顺序相同。
- 定义对象不可变、无字段状态；所有可变值存在 `Battle` 里。
- 数值从 `Rules` 读取：`rules.Card(id, upgrade).Damage`、`rules.Monster(slot).Move(moveId).Hits` 等。只有原版数据里不存在、写在原版代码中的常量才允许出现在定义里，并注释原版出处。
- 原版 Id 到内部 Id 的映射由内容注册表维护，Import 按原版 Id 查表；内核只把原版 Id 当元数据字符串，不引用原版类型。

## 3. Hook 派发

原版的大部分结算都通过 Hook 回调各个监听者完成，监听者的顺序会改变结果。本节规定按什么顺序找监听者、怎么调用，以及内核提供了哪些派发点。

### 3.1 监听顺序

监听列表按以下顺序遍历（来源 `Q/SimulatedCombatState.cs:1903-2064`）：

1. 根前缀：根捕获时原生 `IterateHookListeners` 的顺序，去掉卡牌、苦难、附魔和球；只保留仍在场的生物与药水。玩家侧新生物插在敌人块之前；敌人块（先 Power，后怪物本身）按当前槽位重新分组；新药水追加在前缀末尾。根前缀的分组顺序存在 `Rules`。
2. 球：每个玩家的球，按队列顺序。
3. 卡牌：手牌、抽牌堆、弃牌堆、消耗堆、打出区，按堆内顺序；每张卡之后紧跟它的苦难，再跟它的附魔。

Power 的顺序见 [状态布局](combat-state-layout.md#33-power)。

### 3.2 派发规则

出牌钩子每次调用前都从 Play 牌堆重新读取正在打出的牌。前一个钩子生成新牌后，后续附魔仍作用于原牌。Play 中的卡牌监听优先按牌堆内位置重新定位；其它牌堆保留仍有效的绝对位置，再按牌堆定位。牌组原牌、生成牌和复制品分开识别，保证同名牌回手顺序。

- 按列表顺序逐个调用；遇到待处理选择时停止（与 `M/HookMirrors.cs:1439` 一致）。
- 多阶段 Hook 依次跑 Early、普通、Late。`AfterCardPlayed` 的 Late 阶段重新读取监听列表。
- 修正类 Hook（`ModifyDamage*`、`ModifyHpLost*`、`ShouldClearBlock` 等）不得改写状态：预报与评估会在只读语义下调用它们。需要随攻击捕获的值在 `BeforeAttack` 中记录（例如活力，对应 `VigorPowerMirrors.BeforeAttack`）。
- 战斗正在结束时，战斗监听者的 Hook 返回空，`AfterCardPlayed`、`AfterBlockBroken`、`AfterAttack` 除外；伤害和死亡类的跑局监听者不受此限（`M/HookMirrors.cs:1351`）。`TryModifyEnergyCostInCombat`（普通与 Late）、`ModifyCardPlayResultLocation`、`ModifyCardPlayCount`、`ModifyXValue` 也通过 `IterateCombatHookListeners` 遍历，因此 Sts2Solver 在 `IsOverOrEnding` 时跳过它们；费用查询和评估在结束状态读到的是未修正费用。
- 费用 Hook 是纯查询：普通阶段按战斗监听顺序跑一遍，再按同一顺序跑 Late 阶段；调用方最后 `(int)` 截断并钳制到 ≥ 0（`M/HookMirrors.cs:486-530`）。`AreCardsFree` 分支在 Sts2Solver 中没有内容。
- **`AfterPowerAmountChanged` 延迟派发。** 施加 Power 时不立即派发。`ApplyWithBeforeApplied` 只把 `(PowerId, Delta, Applier)` 放进栈上的队列，到下面这些固定时点再由 `ResolvePowerAmountChanges` 统一派发，时点与原引擎一致：卡牌补偿结束、`CompleteCardPlayEffects`；回合开始 `BeforeHandDraw` 之后、`TriggerSideTurnStart` 末尾；玩家或敌方回合结束的 `TriggerRegular` 之后、每个敌人行动结束、敌人死亡效果末尾；药水使用后；Dominate 的 Vulnerable 与 Strength 之间。
  - 排空时先把整批复制到栈上的 `drained` 并清空队列，再逐条派发。所以处理器在派发过程中新加的变化，只会被嵌套的那次排空看到，与原引擎 `DrainPowerAmountChanges` 先整批取走一致。例子：Vicious 抽到 Strike，Hellraiser 把它自动打出，这次打出产生的变化由内层排空处理。
  - 派发只遍历当前的 Power（原引擎 `EffectivePowers()`），按监听顺序对每条变化调用。Vicious 在“Delta > 0、变化的是 Vulnerable、施加者是持有者”时抽牌。
  - 原引擎**没有**通用的 `AfterApplied`；PhantomBlades、Juggling、Feral 等少数 Power 的初始化在各自的定义里单独处理。原生 `AfterApplied` 只在新建实例时调用，叠加到已有实例（`ModifyAmount`）不重新初始化：再打一张 Feral 不会重置本回合已返回的次数。
- 原引擎的 `ModifyPowerAmountForRelics` 在 Artifact 检查之前依次跑 `BeforePowerAmountChanged`、`ModifyPowerAmountGiven`（SneckoSkull）与 `TryModifyPowerAmountReceived`（RuinedHelmet）。Sts2Solver 用 `HookMaskExt.ModifyPowerAmountGiven` 实现给予侧的纯查询，再实现接收侧的 `TryModifyPowerAmountReceived` 与 `AfterModifyingPowerAmountReceived`；修正阶段不得改写状态，RuinedHelmet 的“本场已用”标记在 After 阶段写入。
- 卡牌从战斗外进入战斗时，`SimulatedCombatState.CardEventHistory.AfterCardEnteredCombat` 先遍历卡牌拥有者的遗物（GhostSeed），再执行卡牌自身的 `AfterEnteredCombat`（Stomp、Pinpoint 等费用反应）。Sts2Solver 的 `Kernel.Generation` 的进入战斗路径按同一顺序派发。本回合攻击完成计数在 `AfterCardPlayed` 派发之前写入（对应原引擎在 `AfterCardPlayed` 前记录 `CardPlayFinished`），因此 Calamity 在攻击的 `AfterCardPlayed` 中生成的 Stomp 会把该次攻击计入 `AfterCardEnteredCombat` 的初始减费；Finisher 等在自己 `OnPlay` 内读取计数的牌仍不会把本次出牌算进去。
- **`AmountOnTurnStart` 只为三个 Power 保存。** 只有 `DrawCardsNextTurnPower`、`SummonNextTurnPower`、`HelloWorldPower` 的这个值会被结算读取（`PowerLifecycleSupport.SemanticallyRelevantAmountOnTurnStart`）。其余 Power 的这个值只反映原生模型的数量，而原引擎的镜像扣层（`PowerAmountPredictionState`）要到动作边界才同步回模型；Sts2Solver 是立即扣层的，所以这些 Power 的 `AmountOnTurnStart` 在导入和回合开始快照时都归零，不去复制一个只存在于原生模型里的滞后值。
- **批量顺序。** 只有原版监听者时，原引擎对回合阶段 Hook、Power 反应、最大能量与抽牌数、`BeforeHandDraw` 用的是“先全部 Power、再全部遗物”的批量顺序，而不是逐监听者顺序。Sts2Solver 在相同位置用相同的批量顺序；这处和原版的偏差以原引擎为准。


新一轮侧回合开始派发在入口检查战斗是否结束；滚石等效果已击杀最后敌人时，不再清零开信刀等回合计数。

**回合末监听器迭代。** `Hook.AfterSideTurnEnd` 在进入阶段二时创建一次监听器迭代；`IsOverOrEnding` 只在迭代开始时检查。阶段二的 Power 监听先运行、随后才是遗物监听，如果前一个监听在本次迭代中击杀最后一名主要敌人，剩余监听（ArtOfWar、Kusarigama 等）仍按原版快照继续执行。相反，阶段二入口已经结束或玩家已进入失败结算时整次迭代为空，遗物不得清零。回合准备的 `ModifyHandDraw` 在战斗已结束/结束中时同样为空，Pendulum 等监听不推进计数。

### 3.3 Hook 目录

内核为下列 Hook 提供派发点，名称与原版一致；回合阶段方法见第 7 节。内容需要新的 Hook 时，在内核增加派发点并补差分测试：

- 出牌：`TryModifyEnergyCostInCombat`（含 Late；Corruption、FreeAttackPower、FreeSkillPower、FreePowerPower）、`TryModifyStarCost`（BrilliantScarf、VoidFormPower；纯查询，只在原费用非负且战斗未结束时派发）、`AfterStarsSpent`（先遗物后 Power 的固定顺序）、`AfterStarsGained`、`ShouldPlay`、`BeforeCardPlayed`、`ModifyCardPlayCount` / `AfterModifyingCardPlayCount`（OneTwoPunchPower、DuplicationPower）、`ModifyCardPlayResultLocation` / `AfterModifyingCardPlayResultLocation`（Corruption；Nostalgia 还返回 `Draw` 的 `Top` 位置）、`AfterCardPlayed`（含 Late）、`AfterEnergySpent`、`AfterStarsSpent`、`AfterCardExhausted`、`AfterCardDiscarded`、`AfterHandEmptied`。
- 卡牌目标：`CardDef.ResolveTargetType` 覆写原生 `CardModel.TargetType`。规则表冻结规范目标；`Shiv` 在持有 `FanOfKnivesPower` 时返回 `AllEnemies`，合法动作枚举、自动打出目标解析与卡牌攻击都读这个动态值。
- 攻击与伤害：`BeforeAttack`、`ModifyAttackHitCount`、`AfterAttack`、`ModifyDamageAdditive`、`ModifyDamageMultiplicative`、`ModifyDamageCap`、`BeforeDamageReceived`（`TryDamageTarget` 中修正之后、格挡之前，不受战斗结束闸门限制）、`ModifyHpLostBeforeOsty`（含 Late）、`ModifyHpLostAfterOsty`（含 Late）、`AfterModifyingHpLostAfterOsty`、`AfterBlockBroken`、`AfterCurrentHpChanged`、`AfterDamageGiven`、`AfterDamageReceived`（含 Late）。
- 格挡：`BeforeBlockGained`、`ModifyBlockAdditive`、`ModifyBlockMultiplicative`、`AfterModifyingBlockAmount`、`AfterBlockGained`、`ShouldClearBlock`、`AfterBlockCleared`。
- 死亡：`BeforeDeath`、`ShouldDie`（含 Late）、`AfterDeath`、`AfterPreventingDeath`。
- 回合：`BeforeSideTurnStart`、`AfterSideTurnStart`、`AfterEnergyReset`（含 Late）、`ModifyMaxEnergy`、`BeforeHandDraw`、`ModifyHandDraw`（含 Late）、`AfterPlayerTurnStart`、`AfterAutoPostPlayPhaseEntered`（阶段切到 AutoPostPlay 后、切到 End 之前，卡牌与 Power 监听者；HowlFromBeyond、StampedePower）、`BeforeSideTurnEnd`（VeryEarly、Early、普通三轮，受战斗结束闸门限制，参与者以位集合传入）、`OnTurnEndInHand`、`AfterSideTurnEnd`（含 Late）、`AfterSideTurnEndLate`（`HookMaskExt`；刷新监听快照后派发，DisintegrationPower）、`ShouldTakeExtraTurn`。
- 抽牌与牌堆：`ShouldDraw`、`AfterCardDrawn`（含 Early，以及被抽卡自身的 Hook；HellraiserPower 在 Early 自动打出刚抽到的 Strike）、`ModifyShuffleOrder`（稳定洗牌后、写回抽牌堆前；Slither 的抽牌随机费用走 `AfterCardDrawn`）、`AfterShuffle`（TheAbacus）、`AfterCardChangedPiles`。`AfterShuffle` 在洗牌计数 +1 后、战斗未结束时派发，监听者为全部战斗监听者。
- 金币：`ModifyGoldGained`、`AfterModifyingGoldGained`（只对截断金额变化的监听者）、`AfterGoldGained`，由 `Kernel.GainGold` 按 `PlayerCmd.GainGold` 的顺序派发。前两者先遍历跑局监听者（当前实现为遗物、药水），再遍历战斗监听者；`AfterGoldGained` 只遍历跑局监听者（牌组卡在导入时拒绝）。
- Power：`TryModifyPowerAmountReceived` / `AfterModifyingPowerAmountReceived`（RuinedHelmet；UnsettlingLamp 按触发卡的出牌来源逐卡翻倍，触发卡与已翻倍的临时 Power 类型只存在 `Exec`，见 [状态布局](combat-state-layout.md#43-原引擎按对象记账的值)）、`AfterPowerAmountChanged`（Vicious、Shroud、SleightOfFlesh、PossessStrength、PossessSpeed；派发携带被修改 Power 的目标 owner，因为原版读取的是 `power.Owner`）。**实例型 Power**（`PowerInstanceType.Instanced`）每个原生实例占 Power 表一行，`(owner, id)` 相同的行按出现序区分；阶段遍历与 Hook 快照都保存 occurrence，`FindPowerOccurrence` 精确回指该行（Automation、Panache、RollingBoulder、TheBomb、Gigantification、Monologue、Orbit、Nightmare）。
- 数值与遗物：`ModifyXValue`（ChemicalX，位置与 `CardModel.ResolveEnergyXValue` 一致）、`ShouldPlayerResetEnergy`（IceCream）、遗物 `AfterCardEnteredCombat`（GhostSeed）、`AfterCreatureAddedToCombat`（`Kernel.AddCreatedMonster` 在入场力量与首个行动准备之后、`MinionPower` 之前派发；PhilosophersStone 给新敌人施加力量。它的 `StrengthPower` 值不进入 `RelicVar` 表，导入时冻结在 `Relic.Aux`）。
- 生成卡与额外结算时机：`AfterCardGeneratedForCombat` 在 `AddGeneratedCard` 插入牌堆并派发 `AfterCardEnteredCombat` 之后按监听顺序派发，参数是生成卡当前的**卡牌数组绝对下标**与生成者 owner（玩家或 `NoOwner`，对应原引擎的 `Player? Creator`；怪物及其 Power 生成的牌为 `NoOwner`）。内核在进入与生成钩子期间给该卡值打一个瞬态身份字节；前一个监听器把它移进另一个牌堆（例如 Gremlin Horn 抽牌触发洗弃牌堆）后，后一个监听器仍得到同一张卡的新下标。Aeonglass 用它把新生成的 Wither 直接提升到当前 `WitherUpgradeCount`。`Rampart` 只在玩家侧非额外回合的回合开始、`OwnerAfterSideTurnStart` 之后执行（`PersistentPowerSupport.TriggerRampart`）。`HasKeyword` 在稳定边界上查询本地关键字后，还会为持有 `HexPower` 的玩家的 Hexed 卡补上 Ethereal（原版 `TryModifyKeywordsInCombat` 是惰性查询，不写入卡牌状态）。
- 球：`AfterOrbChanneled`、`AfterOrbEvoked`、`ModifyOrbPassiveTriggerCounts`、`ModifyOrbValue`；被动与激发、回合末和回合开始的触发见 [内容专属机制](combat-content.md#2-故障机器人充能球)。

### 3.4 苦难与附魔

卡牌上的苦难与附魔是 `Card` 的值（`AfflictionId/Amount`、`EnchantmentId/Amount/Status/Private`），与原引擎的 `PredictedCard.Preview.Affliction/Enchantment` 同形；导入时只接受已登记的定义，其余按 `UnsupportedAffliction`/`UnsupportedEnchantment` 拒绝。定义类是 `AfflictionDef`（`IsStackable`、`CanAfflictCardType`、`CanAfflictUnplayableCards`、`OnPlay`）与 `EnchantmentDef`（`EnchantDamageAdditive/Multiplicative`、`EnchantBlockAdditive/Multiplicative`、`EnchantPlayCount`、`OnPlay`，以及通用的 `AfterCardPlayed` 等监听 Hook）。

- **修正类 Hook。** 原引擎在 `HookMirrors.ModifyDamage`/`ModifyBlock` 中先调用来源卡的附魔修正，再跑普通监听者；Sts2Solver 在 `HookDispatch.ModifyDamage/ModifyBlock` 的同一位次调用附魔定义。`ModifyDamage` 的附魔修正只用卡值，是纯查询。`ModifyBlock` 的溢出口径与普通修正相同。
- **出牌次数。** `CardModel.GetEnchantedReplayCount` 先应用附魔的 `EnchantPlayCount`，再进入 `ModifyCardPlayCount`；Sts2Solver 的 `EnchantedReplayCount` 同时供出牌管线和评估的 `ReplayPotentialValue` 使用。
- **卡牌尾部。** 每次打出在卡牌效果与 `OwnChoice` 之后、`AfterCardPlayed` 之前依次执行附魔 `OnPlay`、苦难 `OnPlay`；持有者死亡或出现待处理选择时中止，与 `CardTailContinuation` 的 Enchantment → Affliction → Finish 顺序一致。
- **苦难归一化。** 原引擎不直接逐 Hook 施加/清除苦难，而是在 `SimulatedCombatState.NormalizeCardAfflictions` 里按当前 Power 归一化：Hex/Tangled/Ringing 先按 Power 数量施加或清除；随后 `NormalizePowerAfflictions` 处理 Vital Spark 的 Tainted（数量变化才回写、Power 归零才清除）、Smoggy 的 Smog（本侧回合已打出技能时给新进场技能）与 Galvanic 的 Galvanized（新进场 Power 牌）。Sts2Solver 的 `Kernel.NormalizeCardAfflictions` 逐条照抄，并在原引擎的同一位置调用：`AfterCardEnteredCombat` 末、卡牌补偿效果末、每个动作结算、玩家回合末阶段一/阶段二、玩家回合开始末、每个敌人行动末。`PowerAfflictionChecked` 对应 `PredictedCard.HasCheckedPowerAfflictionEntry`：根导入的卡视为已检查，根之后生成的卡在下一次归一化时检查一次。`SkillPlayedThisSideTurn` 对应 `_skillsPlayedThisTurn`，在技能牌完成后置位、在该侧回合末清零（与持久化的 `SkillCardsPlayedThisTurn` 不同）。
- **施加/移除路径。** `ApplyAffliction` 对应 `CardCmd.Afflict` 的 `CanAfflict` 与同类型叠加；`ClearAffliction(s)` 对应 `ClearAffliction`。Vital Spark 的 Tainted 层数同步使用导入的 `LastNormalizedVitalSparkAmount` 计数器（`int.MinValue` 表示 null），首次导入不回写，保留实机战斗开始时可能叠高的层数。
- **四个附魔的派发位置。** `Slither` 在 `AfterCardDrawn` 中按 `CombatEnergyCosts.NextInt(4)` 给刚抽到的手牌追加绝对本场费用（`AfterCardDrawnMirrors.HandleSlither`）；`SlumberingEssence` 在玩家回合末清手牌前的 `BeforeFlush` 给每张自身手牌追加 `WhenPlayed` −1（`EnchantmentLifecycleSupport.BeforeFlush`）；`PerfectFit` 在非初始洗牌的 `ModifyShuffleOrder` 中把自己的牌移到合并列表最前（`ModifyShuffleOrderMirrors.HandlePerfectFit`）；`Imbued` 在玩家第 1 回合的自动预打出阶段按 `AllCards` 顺序自动打出（`EnchantmentLifecycleSupport.TriggerAutoPrePlay`，在 Mayhem 之后）。
- **本节涉及的 Power 与怪物。** 当前实现 Plow、Ringing、Tangled、Smoggy、Vital Spark、Tainted、Hex 七个 Power，以及 Ceremonial Beast、Vine Shambler、Infested Prism、Living Fog、Gas Bomb 的移动效果。Living Fog 的 BLOAT 在攻击前生成 Gas Bomb（`MonsterDef.BeforeMove`），Gas Bomb 的 EXPLODE 攻击后自毁。MysticLighter 是遗物定义：带附魔卡牌的 powered attack 加 `Damage`。Hexed 的 Ethereal 由上面的 `HasKeyword` 惰性提供；HexPower 被移除或施加者死亡后不再提供，卡牌本地关键字不变。

## 4. 出牌管线

一张牌从“能不能打”到“打完去哪”的完整顺序。手动出牌、自动打出和药水共用其中的大部分步骤，差别写在 4.1 和本节末尾。

来源：`Q/SimulatedCombatState.cs:1552`、`S/CombatPredictionSimulator.Card.cs:122-330`、`S/CombatPredictionSimulator.CardExecutionContinuation.cs:29-127`、`S/CombatPredictionSimulator.CardTailContinuation.cs:16-125`、`P/CorePowerSupport.cs:21-407`。

1. **可否打出**：Enthralled 锁 → Unplayable 关键字 → 能量费用 → 星能费用 → `ShouldPlay` → `CardIsPlayable` 镜像 → Smog 苦难。能量费用 = 基础费用 → 本地修饰 → `TryModifyEnergyCostInCombat` 普通阶段 → Late 阶段（战斗结束时两阶段都跳过）；钳制 ≥ 0。费用 Hook 接收卡牌当前牌堆，供 FreeAttackPower 判断手牌/打出区；评估侧读取本地修饰（`CostModifiers.Local`/`All`）而不读取费用 Hook。X 费等于当前全部能量。`ShouldPayExcessEnergyCostWithStars` 为真时超额能量按 2:1 用星能支付。
2. **支付**：重算两种费用和超额换算；记录 X 值与本次星能消耗；扣能量 → `AfterEnergySpent`；扣星能 → `AfterStarsSpent`。
3. **进入打出区**：卡从手牌移到打出区（不触发 `AfterCardChangedPiles`）；确定基础去向（复制品或能力牌 → 无；下次打出时消耗或带消耗关键字 → 消耗堆；其余 → 弃牌堆；个别卡牌覆写）；`ModifyCardPlayResultLocation`，随后只对改变去向的监听者派发 `AfterModifyingCardPlayResultLocation`（Corruption 把 Skill 改成消耗；改变“移出战斗”的基础去向显式拒绝）。去向的位置和原生 `CardLocation` 结构一样逐个监听者传下去：不改去向的监听者保留传入的位置（ShiningStrike 的基础去向是抽牌堆顶，Feral 不生效时它仍回到堆顶，也不算 Feral 改过），改去向的只改原版改的字段；消耗堆不支持非堆底位置，遇到时显式拒绝；打出次数 = 附魔重放 + 1 → `ModifyCardPlayCount` → `AfterModifyingCardPlayCount`（只对改变次数的监听者；OneTwoPunch/Duplication 在此递减）；持有者已死则结束。
4. **每次打出**：战斗已结束则停止；`BeforeCardPlayed`（原生 `CardPlay.Card` 是可变的 `CardModel`，这里读打出区上的实时牌值：魅惑在第一次打出的 `AfterCardPlayed` 里停用，重放开始时 Juggling 克隆出的牌带着已停用的魅惑）；历史计数；执行卡牌效果；结算卡牌自身选择。
5. **卡牌效果顺序**：一张牌的效果分两段执行，顺序与原引擎相同。先跑“镜像”部分，也就是原引擎从原版代码推断或专门复刻的攻击、格挡、抽牌；再跑“补偿”部分，也就是 `CardEffectSpecRegistry` 与 `CorePowerSupport.ApplyCardPowers` 里另外补写的 Power、资源、生成效果。**补偿效果总在镜像效果之后**，这是原引擎的实际顺序。
   - 两段之间要先结算一次敌人死亡。原引擎在 `CorePowerSupport.ApplyCardPowers` 里先执行 `CardOnPlaySupport.Apply`，接着立即 `ApplyEnemyDeathPowers`，最后才进入这张牌专属的补偿 `switch`。像 Thunderclap 这样的牌，镜像攻击可能杀掉一个死后会召唤新怪的敌人，接着又要给“当前所有敌人”上 Power，新怪必须先进场。Sts2Solver 在击杀内部就结算死亡效果（见[伤害管线](#5-伤害管线)第 6 步），新怪在补偿效果之前已经进场；对应定义仍在镜像攻击和专属补偿之间调用 `ApplyEnemyDeathPowers`，派发排队的 Power 数量变化；`PlayFromPlay` 还会在整张牌的 `OnPlay` 之后、`OwnChoice` 之前再调用一次。
   - 卡牌可以覆写 `ResultLocation`，复刻 `CardResultLocationMirrors.GetResultLocationForCardPlay`：ShiningStrike 回抽牌堆顶，ParticleWall 回手牌。基础去向仍由规则表的类型和关键字决定。
6. **收尾**：持有者已死则中止；附魔 OnPlay → 苦难 OnPlay；`AfterCardPlayed` 普通阶段 → 重新读取监听列表后跑 Late 阶段（战斗正在结束也照常触发）；`ResolvePowerAmountChanges`（`CompleteCardPlayEffects`）；生命周期计数。
7. **去向**：仍在打出区的卡按去向处理（无 → 移出战斗；消耗 → `AfterCardExhausted`；否则加入目标牌堆）；不在嵌套效果中时触发 `AfterHandEmptied`；清理费用与目标。
8. **动作边界**：结算敌人死亡效果（`ApplyEnemyDeathPowers`）→ 胜负判定 → 处理被请求的回合结束。

药水（`Q/CombatBeamSolver.Expansion.Replay.cs` 的 `UsePotion` 分支与 `P/PotionExecutionSupport.cs`）：清空槽位（使用记录只供搜索评分）→ `BeforePotionUsed` → 药水效果（`PotionDef.OnUse`，对应 `PotionOnUseSupport.Use` 的分支；自身药水作用于玩家）→ 药水选择 → 玩家存活时 `AfterPotionUsed` → 敌人死亡效果；自动药水 `FairyInABottle` 的复活在 `AfterPreventingDeath` 里同样先 `AfterPotionUsed`（`FairyInABottleMirrors`），再走死亡效果；之后与出牌相同：洗牌计数、胜负判定、处理被请求的回合结束。药水的目标类型与变量（含伤害、格挡变量的 ValueProp）从规范药水编译进规则表；合法目标按 `TargetsForPotion`（任一敌人逐个可命中敌人；自身/任一玩家在玩家存活时无目标；全体敌人与无生物目标无目标）。

### 4.1 自动打出

自动打出（Mayhem、Sly、Hellraiser、HISTORY_COURSE 等）和手动出牌走同一条管线，只是不付费、目标另行决定。

来源：`S/CombatPredictionSimulator.AutoPlay.cs:21-73`、`S/CombatPredictionSimulator.AutoPlayContinuation.cs`。

自动预打出顺序是 `Bombardment` → Mayhem 的计划自动打出 → HistoryCourse 的上一回合最后攻击复制品 → 第 1 回合的 Imbued 附魔卡。WhisperingEarring 是计划来源，但没有战斗内效果，只保留最大能量。

`Kernel.AutoPlay` 与 `ManualPlay` 共用 `PlayFromPlay`（结果牌堆、每次打出、`BeforeCardPlayed`、效果、`OwnChoice`、`AfterCardPlayed` 普通与 Late、收尾），只替换资源与目标两步：

1. 战斗结束/正在结束或拥有者死亡时不打出。卡牌不在任何牌堆时先加入打出区（触发 `AfterEnteredCombat`）。
2. 不可打出关键字、`ShouldPlay` 拒绝或无法解析目标时，`MoveToResultPileWithoutPlaying`：先加入打出区，再按 `ExhaustOnNextPlay`/消耗关键字进入消耗堆，否则进入弃牌堆；不触发任何出牌 Hook、计数或费用清理。
3. 不支付能量/星能；`SpendResources(isAutoPlay: true, skipXCapture)` 只在 `!skipXCapture` 时把当前费用写入本次 X 值（X 费卡取当前能量），并记录星能值。`ManualCardsPlayedThisTurn` 不计自动打出。嵌套自动打出返回后，调用方恢复自己的 `XValue`、`StarValue` 和 `CurrentPlayEnergyValue`，对应原版每次 `CardPlay.Resources` 独立。
4. 目标：`AnyEnemy` 用 `CombatTargets` 流从 `HittableEnemies`（名单顺序、排除复活/永久死亡）取随机目标，候选为空时不消耗流；单人没有其他队友，`AnyAlly` 只有调用方已给出目标才成立；其他目标类型直接通过。
5. `AutoPlayFromDrawPile` 先按原引擎把最多 `count` 张抽牌堆顶牌移到打出区，再按顺序逐张自动打出；每张打出前写入 `ExhaustOnNextPlay = forceExhaust`（false 也写入）。打出的卡结果去向为消耗堆时清除该标志（`CardModel.GetResultLocationForCardPlay`），未打出而直接移入去向时保留（`MoveToResultPileWithoutPlaying`）。自动打出中战斗结束/正在结束时，原引擎仍对**剩余队列的每张卡**写入该标志，然后 `AutoPlay` 立即返回；Sts2Solver 在队列仍连续位于 `start` 的前提下复刻这一写入并停止。`count` 超过 `Limits.Cards` 时抛 `CapacityException`。
6. 自动打出中的卡牌自身选择走现有嵌套选择协议：`OwnChoice` 请求选择、动作未携带时返回 `NeedsChoice`，调用方从父状态带选择重执行。生成选项（Discovery/Splash）的选择协议见 4.2。
7. **按对象身份跟踪被打出的牌。** BeatDown 在原引擎里先收集选中的 `CardModel` 引用，再逐张 `AutoPlay`；前一张把后面选中的牌移进手牌、抽牌堆或消耗堆后，仍按对象引用从新牌堆打出。Sts2Solver 给每张选中的牌写 `TransientMark`，每轮按标记在 `Cards` 里重新定位再自动打出，`finally` 清零。完全移出战斗牌堆或被转化的牌没有对应的无牌堆执行方式，按显式 `UnsupportedException` 处理，不按值猜牌。`DecisionsDecisions` 的重复自动打出和 Mayhem 队列用同样的办法。标记区间和异常规则见 [状态布局](combat-state-layout.md#36-卡牌)。

### 4.2 卡池随机生成与变化

来源：`E/CombatCardGenerationExtensions.cs`、`S/CombatPredictionSimulator.AutoPlayContinuation.cs`、`P/CardOnPlaySupport.Batch042.cs`、`Q/CardChoiceResolution.cs`。

- **候选池在规则编译时冻结。** 定义用 `CardPools` 的键声明可能生成的池；`CharacterAll` 的 `FilterForCombatAndPlayerCount` 已排除 Basic/Ancient/Event 与 `CanBeGeneratedInCombat=false`，所以 CallOfTheVoid 的“角色池非 Basic/Ancient”直接复用它，`CharacterEthereal`（BigHat 的候选）按原版本地 Ethereal 关键字过滤。`RulesCompiler` 用原引擎的同一枚举（`player.GetUnlocked...` + `FilterForCombatAndPlayerCount`，再按调用点的类型/费用/排除谓词过滤）把候选按原顺序写进 `Rules`。内核只按 RNG 取样，不重新过滤。
- **取样。** 去重（`TakeRandom`）先对整个候选列表做一次 Fisher-Yates（`n-1` 次 `Shuffle` 流 `NextInt(n+1)`），再取前 `count` 张；有放回（`NextItem`）每张消耗一次 `CombatCardGeneration` 流 `NextInt(0, n)`。候选为空时不消耗 RNG。生成的卡按 `CreateCard` 创建为 0 级规范卡，`UpgradeIf` 再升级一次。
- **加入战斗。** `AddGeneratedCard` 对应 `AddGeneratedCardsToCombat` 的单卡路径：手牌已满改投弃牌堆，按 `Bottom`/`Top`/`Random` 插入，调用卡的 `AfterEnteredCombat`。`SetToFreeThisTurn` 写入绝对 0 能量修饰（EndOfTurn|WhenPlayed）和 0 临时星能费用（回合末清除）。
- **生成选项选择。** Discovery/Splash 先记录生成的选项并写入风险（原引擎的 `History.CardGenerationOptions` + `RecordRisk(UnresolvedPlayerChoice)`），随后 `GetSpec` 的 `GenerateToHand` 分支在 `OwnChoice` 提问。Sts2Solver 把选项值直接放进 `ChoiceRequest.GeneratedOptions`（不在任何牌堆），选择后按 `PredictedCard.Clone()` 的语义复制卡值（不是 `CreateClone`，`IsClone` 保持为假）、`SetToFreeThisTurn`、加入手牌；空选项也提一个空范围选择，与 `RangeSpec` 一致。
- **回合开始的生成。** SentryMode 在 `BeforeHandDraw` 生成 `SweepingGaze`；CallOfTheVoid 在同一时机对 `CharacterAll` 逐张执行 `TakeRandom(1)`（每张一次完整洗牌，等价原引擎的 `GetDistinctForCombat(1)` 循环），生成后补 Ethereal 关键字再入手；Pagestorm 在 `AfterCardDrawn` 只按本地 Ethereal 关键字追加抽牌。BigHat 的生成发生在玩家第 1 回合开始，早于根捕获，故不在内核路径里。
- **变化的候选池。** `CardFactory.GetDefaultTransformationOptions` 对 Quest/Event/Ancient/Token 用无色池，其余用卡牌自己的原生池（角色、无色、Status、Curse）。Sts2Solver 在规则表为每张卡编译 `TransformPool`，Entropy 的变化按它选择冻结池，而不是按“当前是否在角色池里”猜测；Status/Curse 池分别对应 `CardPools.StatusAll`/`CurseAll`。`ReplaceCard` 对应 `CardCmd.Transform` 的原引擎结算：从原牌堆移除原卡（`RemoveFromCombat`），再把替换卡插回 `min(原下标, 当前长度)` 并调用 `AfterEnteredCombat`；不触发 `AfterCardChangedPiles`。Sts2Solver 没有实例身份，按位置定位；PrimalForce 对 snapshot 的手牌攻击按升序原位替换为 GiantRock（PrimalForce 升级时 GiantRock 升 1 级）。Entropy 的回合开始变化按 `TurnStartChoiceSupport.Resolve(Transform)` 选择手牌，再对每张走 `CombatPredictionSimulator.CreateRandomCardForTransform`：候选是原卡 `Pool`（Quest/Event/Ancient/Token 取无色池）中 `CanBeGeneratedInCombat`、非 Basic/Ancient/Event/Epoch 排除的卡，排除原卡 Id，用 `CombatCardSelection` 流 `NextItem` 抽一张 0 级规范卡；候选为空按 `UnsupportedException` 拒绝。
- **随机药水。** 一次 `CombatPotionGeneration` `NextFloat` 选稀有度（Rare ≤ 0.1、Uncommon ≤ 0.35、否则 Common），再一次 `NextItem` 在冻结的同稀有度池中选；`TryProcurePotion` 放入第一个空槽，成功时记录 `LongTermResourceValue + 20`。
- **药水生成选择。** Attack/Skill/Power/ColorlessPotion 在 `OnUse` 里生成 3 张去重候选，再走 `PotionChoiceSupport.GetSpec` 的 `GenerateToHand` 0..1 选择。该选择不走 `PendingTurnStartChoice`：原引擎把候选写进 `History.CardGenerationOptions`，`PotionExecutionSupport.Complete` 从 `PlanAction.Choice` 直接结算 `PotionChoiceSupport.Apply`。Sts2Solver 走普通的选择协议：动作没带选择时返回 `NeedsChoice`，调用方带上选择从父状态重执行；选定后按 `PredictedCard.Clone()` 语义复制（`IsClone` 保持假）、`SetToFreeThisTurn`，加入手牌底部。
- **风险标记。** `BattleFlags.HasRisk` 表示原引擎快照里**未补偿**的风险，根导入时读取（含义见 [状态布局](combat-state-layout.md#31-头部)）。Discovery、Splash 虽然记录 `UnresolvedPlayerChoice`，但 `CardOnPlayCompensationCatalog` 把它们的 `OnPlay` 风险标为已补偿，内核不置位。SeekerStrike 的 `BuildSeekerSpec` 不在补偿目录里，原引擎会记录风险；本内核逐一枚举这个选择，不置位（保留路线历史标记）。充能球生成达到 1000 次上限时置位，并一直保持到战斗结束。

## 5. 伤害管线

攻击、伤害、击杀三层依次嵌套：攻击决定打几下、打谁；伤害对每个目标算数值和格挡；击杀处理死亡和死亡阻止。敌人的死亡效果在击杀内部当场结算，与原版 `CreatureCmd.Kill` 一致；原引擎把它延后到出牌补偿之前或动作边界，Sts2Solver 不沿用这一近似。

来源：`S/CombatPredictionSimulator.Attack.cs:83-165`、`S/CombatPredictionSimulator.Damage.cs:55-467`、`M/Hooks/Damage/ModifyDamageMirrors.cs`。

1. **攻击**：结束中或攻击者已死则返回；`BeforeAttack`；`ModifyAttackHitCount`；逐次命中：攻击者死亡或无存活目标则停止，随机目标用 `CombatTargets` 流；最后记录历史并触发 `AfterAttack`。攻击上下文在 `Exec` 上记录每个敌人收到的第一条伤害结果（是否未格挡），`AfterAttack` 监听者据此判断一次攻击的完整结果。EchoingSlash 走 `CardAttackRepeating`，在同一个攻击上下文里分波伤害；Omnislice 的原始命中与复制命中也共用一个上下文，`AfterAttack` 只在最后触发一次，见 [内容专属机制](combat-content.md#11-其它卡牌)。
2. **伤害**：先对全部目标计算并扣除，再统一处理结果。
3. **单目标**：目标已死则跳过；`ModifyDamage` = 附魔加成与倍率 → 按监听顺序的加法阶段（力量等）→ 乘法阶段（易伤、虚弱等）→ 取所有 `ModifyDamageCap` 的最小值 → 钳制到 ≥ 0。全程 `decimal`，不取整。卡牌动态变量同样按原引擎的 `decimal` 读取：Thrash 的 `Damage` 偏移存在 `Battle.DecimalVars`，`CardEffects.Var` 返回同一个 `decimal`；只有 HP 损失按原引擎的位置 `(int)` 截断，评估先做 `decimal` 加法再转 `double`。`BeforeDamageReceived`；格挡吸收（目标是 Osty 时扣其主人的格挡；不可格挡伤害跳过）；`ModifyHpLost` BeforeOsty 与其 Late → Osty 重定向 → AfterOsty 与其 Late → `AfterModifyingHpLostAfterOsty`；失血时 `(int)` 截断；失血 ≥ 当前 HP 视为击杀。
4. **结果处理**：`AfterBlockBroken` → `AfterCurrentHpChanged` → `AfterDamageGiven` → 未被击杀时 `AfterDamageReceived`（含 Late）；之后执行击杀，或所有玩家死亡时判负。每次结果同时按 `RecordDamageReceived` 记录玩家对敌人的 powered attack 命中（完全格挡也计数，非强化伤害不计），供 BeatIntoShape 的按目标锻造乘数读取；该计数在玩家回合开始清零，超过 255 按 `UnsupportedException` 拒绝。卡牌击杀目标（`Exec.CardKilledTarget`）按伤害的卡来源判断，玩家或 Osty 打出的同一张牌都能置位。
5. **击杀**：扣光剩余 HP 并触发 `AfterCurrentHpChanged` → `BeforeDeath` → `ShouldDie`（第一个阻止者生效）。死亡：`AfterDeath` → 敌人死亡效果的前半（第 6 步）→ 读取存活队友（包含刚召唤的新怪）→ 移出阵容 → 判定是否主敌人 → 死亡效果的后半 → 主敌人死亡且只剩附属时附属一并死亡。被阻止：`AfterDeath(prevented)` 与 `AfterPreventingDeath`，最多递归 10 次。
6. **敌人死亡效果**：在击杀内部、原版 `AfterDeath` 的位置结算（`Kernel.BeginEnemyDeath`、`Kernel.CompleteEnemyDeath`）。前半标记死亡已处理，按死亡前的 Power 快照执行死亡 Power（Surprise、Infested 召唤新怪，Possess 归还偷走的力量或敏捷，Reattach、Adaptable 转入复活招式），再归还被偷的资源；后半在死者离开阵容后移除它的 Power、完成死亡阶段，并清除它施加的 Constrict、Hex、Shrink、MagicBomb。所以同一次攻击的下一击已经能看到新召唤的怪和还回的力量：随机多段攻击在新怪里抽目标，打全体的多段攻击也会打到它们。`ApplyEnemyDeathPowers` 仍在原引擎的位置调用（出牌补偿之前、Hook 伤害之后、动作边界），只结算没有经过 `Kill` 的死亡，并派发排队的 Power 数量变化。击杀内部的结算会增删 Power 行（移除死者的行、召唤新怪的入场 Power，打死 Possess 敌人时还回的力量可能让玩家的力量行归零移除），所以 Power 定义在造成伤害之后还要写自己那一行时，按（所属者、id、序号）重新定位（RollingBoulder、Panache），不沿用伤害前的行号。`SteamEruptionPower.AfterDeath` 属于普通 `AfterDeath` 监听者，在 `Kill` 内部立即改写死亡，不进入这条清算；见 [偷窃与瀑布巨兽](#14-偷窃与瀑布巨兽)。

**攻击后的死亡结算。** 原版 `DamageCmd.Attack.Execute` 返回前已结算被击杀敌人的死亡 Power；同一张卡在攻击之后立刻读取状态时必须看到这些结算。Uproar 在两段攻击后先结算敌人死亡 Power，再抽牌选择自动打出的攻击，所以击杀 ShrinkerBeetle 后 ShrinkPower 已移除，后续伤害按未缩小计算。

**SuckPower 的命中列表。** 原生 `SuckPower.AfterAttack` 按 `AttackCommand.Results` 的每个命中列表计数：先移除宠物命中对应的宠物主人结果，但保留宠物命中本身，列表里任一结果有未格挡伤害就计一次。Osty 代受并溢出给玩家的整次命中只算一次，不因宠物和玩家两个结果重复计数。内核用 `Exec.AttackSuckHitLists` 在每次最外层 `Damage` 调用上记录该布尔结果。

## 6. 格挡与 Power

格挡和 Power 施加都先经过一串修正 Hook，再写入状态；Power 的衰减时点按类型固定。

格挡（`S/CombatPredictionSimulator.Block.cs:49-93`）：结束中或已死返回 0 → `BeforeBlockGained` → `ModifyBlock`（附魔、加法阶段含敏捷、乘法阶段含脆弱、溢出钳制、≥ 0）→ `AfterModifyingBlockAmount` → 大于 0 时按出牌记录并加到格挡（`int` 截断，上限 999,999,999）→ `AfterBlockGained`。己方回合开始清格挡：`ShouldClearBlock` 或保留者，然后 `AfterBlockCleared`。

Power 施加（`Q/SimulatedCombatState.cs:648-711`）：战斗处于 `Kernel.IsEnding` 时直接返回（原版 `PowerCmd.Apply`/`ModifyAmount` 的 `CombatManager.IsEnding` 门控；`Kernel.IsEnding` 逐项对齐 `IsCombatEnding`）→ 数量为 0 或目标不能接受 Power 时返回 → 遗物修正（先 `ModifyPowerAmountGiven` 给 SneckoSkull 等加层，再 `TryModifyPowerAmountReceived` 普通阶段记录被修改的监听者，随后在 Artifact 检查前对它们派发 `AfterModifyingPowerAmountReceived`；RuinedHelmet 在此翻倍并标记本场已用）→ 目标有人工制品且是减益时抵消一层并返回 → 实例型 Power 新建实例，其余按（持有者、类型）叠加并钳制 → 玩家侧新获得的虚弱、易伤、脆弱标记跳过下次衰减 → 更新监听顺序 → 把非零变化放进待处理队列。原引擎**没有**通用的 `AfterApplied`，只有少数 Power 初始化。原版 `PowerCmd.Apply` 不写 `PowerModel.Target`，因此 `Kernel.ApplyPower` 默认把 `Power.Target` 写成 `NoOwner`；只有原版自身写 `Target` 的五处（Swipe/Thievery/Sandpit/Heist/WitheringPresence）才走 `ApplyPowerTargeted` 或由导入层保留原生目标；`Misery` 是另一种情况，它克隆源 `PowerModel`（`ClonePreservingMutability`）后用非泛型 `PowerCmd.Apply` 施加，`Target` 由克隆值原样保留，因此走 `ApplyPowerPreservingTarget`，不算新的写入点。其余卡牌、遗物、药水与怪物都不能传 `powerTarget`。

衰减：虚弱、易伤、脆弱、无实体、NoBlock 在**敌方回合结束时对所有生物**衰减并尊重跳过标记；Blur 在己方回合开始衰减且该回合开始不清格挡；DoubleDamage 在每方回合结束衰减；瞬时 Power 在己方回合结束清除；临时力量、敏捷、集中在 `TriggerRegular` 中按监听顺序退回。`PowerDef.PreventsCombatEnding` 对应原版 `ShouldStopCombatFromEnding`：战斗结束判定会遍历仍在阵容中的生物的正层 Power，AdaptablePower、StockPower 等返回真时 `IsEnding` 保持假（TestSubject 的复活窗口）。`SandpitPower` 是实例型、带目标的计数 Power：根导入按原生实例逐行写入 `Power.Target`；敌方侧回合开始的固定序列（`TurnStartPowerSupport.TriggerAfterSideTurnStart` 的 SANDPIT case）按行把层数减一，归零时对记录的目标执行 `Kill(force: true)`（玩家死亡时由 `HandlePlayerDeath` 顺带清理 Osty），与 `SimulatedCombatState.IncrementSandpitTargeting`/`FranticEscape` 的加层路径一致。

## 7. 回合流程

`EndTurn` 动作一次走完“玩家回合结束 → 敌方回合 → 下一个玩家回合开始”，停在新回合的出牌阶段。下面按这三段列出顺序。

来源：`Q/CombatBeamSolver.Expansion.Replay.cs:965-1222`、`P/PlayerTurnEndLifecycle.cs`、`P/CorePowerSupport.cs`、`Q/CombatBeamSolver.RoundTransition.cs`、`Q/CombatBeamSolver.TurnExecutionContinuation.cs`。

**玩家回合结束**

1. 第一阶段：遗物 `BeforeSideTurnEnd`（含 VeryEarly）；`AfterAutoPostPlayPhaseEntered`；`BeforeSideTurnEnd` VeryEarly → Early → 普通；胜负判定；球的回合末被动；虚无卡先消耗，再处理回合末在手效果（移到打出区 → `OnTurnEndInHand` → 虚无则消耗，否则弃置）；胜负判定；玩家再生 −1。
2. 敌人死亡效果。
3. 未保留的手牌按手牌顺序弃置；回合末清理。
4. 第二阶段：缠绕伤害；清除 Tangled 与 Ringing；DoubleDamage 衰减；`TriggerRegular`；瞬时 Power；遗物 `AfterSideTurnEnd`；`AfterSideTurnEndLate`。
5. 敌人死亡效果；`ShouldTakeExtraTurn`。

**敌方回合**

1. 记录本回合行动的敌人快照；重置计数；`BeforeSideTurnStart`；清格挡与 `AfterBlockCleared`。
2. 己方回合开始效果：Blur、下回合抽牌清零、轮次大于 1 时 Plating 递减、Slow 重置、Power 与遗物的 `AfterSideTurnStart`。
3. 敌人中毒：触发 min(层数, 1 + 加速) 次，不可格挡且不受修正，每次 −1；敌人死亡效果；胜负判定。
4. 按快照顺序逐个敌人：不能行动则跳过 → 眩晕 → 强制行动 → 否则执行当前行动：`BeforeAttack` 补偿；逐次命中按基础伤害经完整伤害管线结算，命中后结算死亡效果，出现死亡则停止；失衡眩晕；行动附加效果（`P/MonsterMoveEffects.cs` 中按"怪物、行动"登记）；每个敌人后判定胜负。
5. 敌方回合结束效果：Doom 击杀、再生、Plating 格挡、`TriggerRegular`、持续时间衰减、`AfterSideTurnEndLate`。
6. 玩家中毒；清除 NoDraw；胜负判定。
7. 为下一轮准备怪物行动：只有本轮执行过行动的敌人推进状态机（见第 9 节）；例外为必须执行一次的行动、眩晕和新怪物的初始掷骰。

**玩家回合开始**

1. 轮次 +1（额外回合不加）；重置计数；记录 `AmountOnTurnStart`；`BeforeSideTurnStart`（原版：先遗物批次，再 Power）。
2. 清格挡与 `AfterBlockCleared`。
3. 能量：`ShouldPlayerResetEnergy`（IceCream 从第 2 回合起拒绝重置；否则清零）→ 加修正后的最大能量与 `ConsumeEnergyNextTurn`（`EnergyNextTurnPower` 的层数直接消费并清零）；`AfterEnergyReset`、遗物 `AfterEnergyReset`、`AfterEnergyResetLate`。
4. `BeforeHandDraw`：Power → 遗物 → 返回手牌的卡。返回手牌阶段处理 `Card.ReturnToHandNextTurn`（Bolas/ThrummingHatchet），牌已在手牌时只清位。
5. 抽牌数 = 5 + 下回合抽牌 + DrawCardsNextTurn → `ModifyHandDraw` → Late；抽牌。`ConsumeModifiedHandDraw` 的“尚未快照”项（`DrawCardsNextTurnPower` 的 `AmountOnTurnStart == 0`）在监听者之前直接加到基础抽牌数；已快照的层数由该 Power 的 `ModifyHandDraw` 加。`DrawCardsNextTurnPower` 在 `TriggerBaseSideTurnStart` 清零。
6. `AfterPlayerTurnStart`（原版：先 Power，再遗物）。回合开始的选择（Tyranny、Toasty Mittens、Gambling Chip、工具箱、Foregone Conclusion 等）在这里提出。原生的 `CardSelectCmd.Selector == null` 时（包括只有一个候选或空手牌的隐式请求），`SetupPlayerTurn` 在第一个选择处暂停；`StartTurn` 先跑侧回合开始（`AfterSideTurnStart`）与球 `AfterTurnStart`，之后才恢复 setup、读取候选并结算选择。Selector 非空时选择被立即回答，setup task 不暂停，侧回合开始在抽牌与 `AfterPlayerTurnStart` 之后执行。
7. 没有暂停时，玩家己方回合开始效果（`TriggerSideTurnStart`：第 1 回合之后 Plating 递减，Rampart 只在非额外回合执行等）在 `AfterPlayerTurnStart` 之后执行；发生准备暂停时它已经在暂停处跑过，不再重复。
8. 敌人死亡效果 → 苦难归一化 → 回合开始的附魔与球触发 → 自动预打出 → 胜负判定。发生准备暂停时球在暂停处已经触发，此步不再重复。自动预打出打出的 `VoidForm` 会调用原版 `PlayerCmd.EndTurn`，把本回合标记为准备结束；原版仍走完当前 `StartTurn`，之后回合循环立即跑完整结束回合、敌方回合和下一回合准备。内核在自动预打出后检查 `PlayerTurnEndRequested`，需要时再跑一个完整回合，所以同一个 EndTurn 动作可以携带本回合与下一回合的多个 `PlayerTurnStart` 选择。

`PreparePlayerPlayPhase` 重放捕获的准备页时使用 `Exec.SetupPause`/`RunSetupPause`：`Selector == null`，在第一个 `PlayerTurnStart` 选择处暂停，侧回合开始和球只在暂停处跑一次。`CardSelectCmd.FromHand`/`FromCombatPile` 在读取牌堆前发暂停信号，所以 Toasty Mittens、Tyranny、Gambling Chip、Foregone Conclusion 等入口即使在暂停前手牌为空也先调用 `RunSetupPause`，侧回合开始生成的牌随后进入候选。普通 EndTurn 按 `BattleFlags.TurnStartChoicePauses` 选择语义：宿主在可见准备页上接管下一回合时（Mod 的自动搜索开着或本场执行中）会释放 selector，原生在第一个选择处暂停，内核走同一条暂停路径；宿主用部署的 `PlannedNativeSelector` 回答选择时（标志为 0，离线宿主和 pilot compare 都是这种），`SetupPreamble=false`，`DrawCardsNextTurnPower` 等侧回合开始效果在抽牌后才清理，Toasty Mittens/Crossbow 也不会在普通结束回合提前改变候选。额外回合（PaelsEye、AmbergrisPower）按同一区分处理：原生 `StartTurn` 在额外回合只把起始生物限定为 `PlayersTakingExtraTurn` 并跳过敌方 `PrepareForNextTurn`，可见准备页与普通 EndTurn 的暂停差异都不变 [GAME CombatManager.cs:701-706, 725-733, 777-830]。

### 7.1 额外回合

AmbergrisPower 和 PaelsEye 能让玩家连续行动一回合，这时整个敌方回合被跳过。

来源：`Search/CombatBeamSolver.Expansion.Replay.cs:1038,1220-1232`、`Search/CombatBeamSolver.RoundTransition.cs:23-28`、`Engine/InCombat/Mirrors/HookMirrors.ExtraTurn.cs`。

玩家回合结束第二阶段和敌人死亡结算之后、敌方回合之前，`Kernel.ShouldTakeExtraTurnPass` 按监听顺序询问 `ShouldTakeExtraTurn`，第一个返回真的生效。生效时：

1. 先跑 `AfterRelicRoundPass`（原引擎 `RecordRelicRoundDamage` 的位置）：EmotionChip 在这里把“本回合失血”翻到“上一回合”。
2. 再在固定的监听快照上跑 `AfterTakingExtraTurnPass`：Ambergris 自减一层，PaelsEye 置已用。
3. **跳过整个敌方回合**和 `PrepareMonsterMovesForNextRound`，以 `AdvanceRoundPlayerStart(takingExtraTurn: true)` 进入玩家回合开始：轮次不加一，玩家回合数加一，`TriggerSideTurnStart` 的 Rampart 不执行。随后的 `StartTurn` 与普通回合走同一个 `SetupPlayerTurn`：额外回合只把起始生物限定为 `PlayersTakingExtraTurn`，并跳过敌方 `PrepareForNextTurn` [GAME CombatManager.cs:701-706, 725-733]；抽牌、`AfterPlayerTurnStart` 的选择、`AfterSideTurnStart` 和球 `AfterTurnStart` 都按同一顺序执行；`Selector == null` 的可见准备页在第一次选择处暂停，普通 EndTurn 的立即选择不暂停 [GAME CombatManager.cs:777-830]。
4. 本回合失血标记：原引擎的 `AdvancePlayerTurn` 只清空 `_unblockedDamageThisTurn` 缓存，之后从不可变的根历史重算 `HasLostHpThisTurn`。Sts2Solver 在额外回合用 `Rules.RootLostHpMask`（根捕获时每个 owner 的答案）恢复这些标记；普通回合换轮时全部清零。根历史之外的预测期伤害不计入，与原引擎一致。

### 7.1.1 准备暂停处的侧回合开始与球

可见准备页暂停时，侧回合开始和球 `AfterTurnStart` 在恢复选择前执行。原版 v0.111.0 没有内容能在这两个位置请求新的玩家选择：`Hook.AfterSideTurnStart` 不接收 `PlayerChoiceContext`（[GAME AbstractModel.cs:1268、Hook.cs:1175]），51 个覆写里没有 `CardSelectCmd` 调用；InfusedCore 和 SymbioticVirus 用 `BlockingPlayerChoiceContext` 只是 `OrbCmd.Channel`。球只有 PlasmaOrb 覆写 `AfterTurnStartOrbTrigger`，它触发 passive 获得能量，不选牌。内核仍对这两个位置检查 `NeedsChoice`/`ChoicesTaken`，一旦出现无法表示的请求就显式抛 `UnsupportedException`；当前内容不会走到。
准备暂停期间，原生 `CardSelectCmd.FromHand` 先暂停、再读取手牌候选；暂停中 `AfterSideTurnStart` 的 Crossbow 会把生成的攻击牌加入手牌，因此 ToolsOfTheTradePower 等基于手牌的回合开始选择必须先运行 `Exec.RunSetupPause`，再取手牌数量。`CardSelectCmd.FromHand` 即使手牌为空也会暂停。普通 EndTurn 的 `PlannedNativeSelector` 非空，不进入这段暂停，候选按侧回合开始前的手牌读取。

### 7.2 实现形状

对照 `Kernel.TurnPhases.cs`。原引擎的回合阶段有两种写法，Sts2Solver 分别对应：

- 按监听器顺序遍历 Power（或遗物）再按类型 `switch`：每个 `case` 是定义上的同名阶段方法（`PowerDef.EndTurnRegular`、`BeforeHandDraw`、`AfterPlayerTurnStart` 等；遗物为 `RelicDef` 的批次方法）。每一轮遍历开始时按监听器顺序（玩家、Osty、名单中的敌人，各自的 Power 按表内顺序）取快照，访问时按 (owner, id, occurrence) 重新定位（实例型 Power 每个实例一行），层数必须仍为正。
- 固定类型序列（例如 `TriggerOwnerAfterSideTurnStart` 的 BiasedCognition → … → Clarity、`TriggerBaseSideTurnStart` 的 Blur → DrawCardsNextTurn → Plating → Slow）：内核保存从原引擎代码抄录的原版 Id 列表，只调用已登记且在场、层数为正的类型。

持有者范围与原引擎的遍历一致：`AfterEnergyReset` 与 `AfterPlayerTurnStart` 只看玩家生物，`BeforeHandDraw` 包含 Osty（`power.Owner.Player == player`）。敌方回合的每个阶段都重新读取当前敌人名单，对应原引擎直接传入 `simulatedCombat.Enemies`。`ShouldClearBlock` 是原版 Hook，战斗结束中不枚举监听者；第一个拒绝清除的监听者通过 `BlockAfterPreventingClear` 结算保留量。

两处按批处理的实现与逐项处理结果相同：每方回合开始的 `AmountOnTurnStart` 快照按所有者集合一次扫描（只写各 Power 自己的字段，与逐所有者调用结果相同）；敌方回合末的持续时间衰减先把各生物的可衰减 Power 按“生物顺序 + Power 表内顺序”分桶，再按原顺序逐项衰减，前一个生物只移除自己的 Power，因此后续生物的 `(owner, id)` 序列与逐生物重扫一致。

## 8. 抽牌与洗牌

抽牌和洗牌决定随机数怎么消耗，顺序错一步，之后所有随机结果都会偏。

来源：`S/CombatPredictionSimulator.CardPile.cs`。

- 小数抽牌数向上取整；`ShouldDraw` 失败则整次不抽。Pagestorm 在每张本地 Ethereal 牌抽到后追加一次抽牌（`AfterCardDrawn`）。
- 逐张：结束中或手牌达到上限则停止；抽牌堆空且弃牌堆非空时洗牌；取抽牌堆顶入手；历史 → `AfterCardDrawn` Early → 普通 → 被抽卡自身 Hook。`TryDrawOne` 在抽牌前保存被抽卡的值；Pillage 用它判断“刚抽到的牌是否攻击”，即使 `AfterCardDrawn` 中的 Hellraiser 已把该牌自动打出并从手牌移除。`DrawIntoMarked` 在进入手牌后、`AfterCardDrawn` 之前写入本批的瞬态身份字节，供 Scrape 在抽牌钩子移动牌后仍按对象身份弃置。
- 洗牌：列表 = 弃牌堆（原顺序）+ 抽牌堆；按卡牌排序键稳定排序后用 `Shuffle` 流做不稳定洗牌（与 `StableShuffleProjection` 相同）；`ModifyShuffleOrder`；全部放到抽牌堆底；`ShuffleEventCount + 1`；战斗未结束时 `AfterShuffle`（TheAbacus 获得其 Unpowered 格挡）。排序键来自原版 `CardModel.CompareTo`，在根导入时编译进 `Rules`。
- 随机插入牌堆位置用 `Shuffle` 流的 `NextInt(n + 1)`。手牌满时加入手牌的卡改进弃牌堆。

## 9. 怪物 AI

原引擎不运行原版的怪物状态机，而是解释它的图（`P/BranchMonsterAi.cs`）。Sts2Solver 在根导入时把每个敌人的状态机编译成表，搜索时只查表、按规则推进：

- 节点类型：行动节点（攻击列表：基础伤害与次数）、随机分支（各后继的基础权重）、条件分支（根捕获时的选择结果）。
- 推进：从当前行动的后继出发，随机分支用 `MonsterAi` 流按 `MonsterRandomBranchResolver.Pick` 的规则选择（依赖最近行动日志），条件分支取根选择，直到落到行动节点；最多 32 步。
- 特例：原引擎在 `ResolveConditional`、`GetBranchWeight` 与 `Advance` 里按怪物类型写的特殊逻辑（KnowledgeDemon、FrogKnight、Fabricator、TestSubject、Ovicopter、LagavulinMatriarch、SlumberingBeetle、Queen、LivingShield、BowlbugRock、TwoTailedRat），由对应 `MonsterDef` 覆写同名方法实现。
  - KnowledgeDemon 的 `_curseOfKnowledgeCounter` 是一个 AI 寄存器：根导入取 `_knowledgeDemonCurseCounters` 的条目，没有条目时取模型字段。CURSE_OF_KNOWLEDGE 的选项集合、选择后的加一，以及 PONDER 之后的 `CurseOfKnowledgeBranch` 都读这个寄存器。
  - 怪物的动态伤害和连击数用 `ModifyAttackDamage`、`ModifyAttackRepeats` 实现，对应原引擎 `AdjustMonsterMoveDamage` 和 `BranchMonsterAi.CurrentMove` 的动态值（TheForgotten 的 DREAD、TestSubject 的 MULTI_CLAW）。
  - 有些动态值以根上冻结的 Power 数量为基线，由 `MonsterDef.RootPowerAmountNames` 声明、编译进 `MonsterRules.RootPowerAmounts`。原引擎在**请求根**上捕获这个基线、随分叉共享（`SimulatedCombatState._rootPowerAmounts`）。所以有固定前缀时，规则编译必须从前缀之前的请求根读基线（按 CombatId 找原生生物；前缀里新召唤的敌人没有基线，记 0），不能取前缀之后的搜索根，否则 MIASMA 偷走 2 点敏捷后，DREAD 会少 2 点伤害。没有前缀时基线就是当前根。
- 行动执行：攻击按编译表结算；攻击前的效果由 `MonsterDef.BeforeMove` 实现（`P/MonsterMoveEffects.ApplyBeforeAttack`，例如 Living Fog 的 BLOAT 先生成 Gas Bomb）；非攻击效果由 `MonsterDef.ApplyMoveEffects` 实现，规范来源是 `P/MonsterMoveEffects.cs` 与 `P/MonsterMoveSemantics.cs`。
- 致命攻击后的行动收尾：原版在 `MonsterModel.PerformMove` 里调用行动方法，`DamageCmd.Attack(...).Execute(null)` 返回后同一 C# 方法的后续语句照常执行，`CombatManager.ExecuteEnemyTurn` 等整个 `enemy.TakeTurn()` 返回后才判胜负。因此玩家被本次攻击打死时，`ExecuteMonsterMoveCore` 仍按顺序执行失衡眩晕、`ResolveReviveMove`、`ApplyMoveEffects`（例如 Fabricator 的 FABRICATING_STRIKE 在致命攻击后仍召唤 aggro bot）和敌人死亡效果；`ExecuteMonsterMove` 返回 `playerDied`，`RunEnemyTurn` 据此停止后续敌人。玩家死亡在伤害管线里已经置 `PendingLoss`。原版 `PowerCmd.Apply`/`ModifyAmount` 的门控是全局的：`CombatManager.IsEnding` 在 `PendingLoss` 或没有存活主要敌人且 `ShouldStopCombatFromEnding` 不阻止时成立，因此这些收尾效果里的 Power 施加、叠加和数量修改都不生效（`Kernel.ApplyPower*` 与 `ApplyTemporary*` 使用 `Kernel.IsEnding`，`Kernel.ModifyPowerAmount` 对应 `PowerCmd.ModifyAmount`）。生成敌人、写怪物 AI 寄存器与 `PowerCmd.Remove` 等非 Power 施加操作仍照常执行。
- 遭遇槽位：`Enemy.Slot` 来自 `SimulatedCombatState.GetSlotIndex`，名字不在 `_encounterSlots` 时就是原版的 -1（`NoSlot`）。`EncounterModel.Slots` 为空但仍用 `first`/`middle`/`last`、`odd`/`even`、`first`/`second`/`third` 的遭遇（碗虫、沉睡甲虫）因此不排序、也不报错；`NextFreeSlot` 对空槽表返回 `""`、`LastFreeSlot` 返回 `null`，与原引擎一致，不编造槽位顺序。
- 生成初始状态：`MonsterDef.SpawnedStunned` 让规则编译器用 `StartStunned = true` 编译该类型的生成规则（Wriggler 的初始节点是 SPAWNED_MOVE）；`SpawnSlotName` 给这类按槽名选择 INIT_MOVE 分支的怪物一个固定合法槽名，避免私有编译遇到无匹配条件。
- 私有字段：原引擎通过 `_monsterIntStates` 读写的怪物私有整数，在 Sts2Solver 中映射到 `Enemy` 的 AI 寄存器，编号由各怪物定义的 `AiRegisterNames` 按声明顺序决定；定义另外用 `InertMonsterIntNames` 声明只被状态指纹/续接文本读取、不影响结算的条目。导入时由定义认领，未认领的条目拒绝（见第 13 节）。

## 10. 选择协议

需要玩家选择的效果（选牌、选项）不在内核里“猜”。内核遇到选择就停下，把选项告诉调用方；调用方枚举选项，把选择放进动作里，从父状态重新执行。所以一个动作连同它的所有选择，才是搜索里的一条边。

- `PlayerAction` 最多携带 `Limits.MaxChoices`（当前 8）个选择，每个选择是“选项列表上的位集合”。`DecisionsDecisions` 的嵌套自动打出可能需要更多，超出时按 `CapacityException` 丢弃这个分支。选项列表由需要选择的效果按原引擎 `CardChoiceSupport.GetSpec` 的顺序生成（来源牌堆中满足条件的卡的位置）。
- 效果请求第 k 个选择而动作只提供了不到 k 个时，内核停止执行，返回 `NeedsChoice` 与选项规格（效果、时机、来源牌堆、选项的绝对位置、最少与最多数量；数量已按选项数截断，与 `BuildChoices` 一致）。调用方丢弃这个半成品状态，枚举选项后从父状态重新执行同一动作。
- 卡牌自己的选择在 `CardDef.OwnChoice` 中，位于 `OnPlay` 之后、出牌收尾之前，对应原引擎的 `ResolveManualCardChoice` 阶段（`GetSpec` → `Apply` → `ApplyPostChoiceEffects`）。`Kernel.ChooseCards` 对应 `CardChoiceSupport.Spec`：没有选项时不建规格、不提问，选择后效果照常执行；`ChooseCardsInRange` 对应 `RangeSpec`：没有选项也提问。`BuildRequiredEmptyChoice` 列出的卡（烙印等）在没有选项时提一个空选择。
- 选中的卡按选择顺序结算（与令牌顺序一致）。字节相同的卡在不同位置并不等价（拿走哪一张会改变中间卡的顺序），所以内核按"值 + 第几张相同的卡"定位选中的卡，对应原引擎令牌的 `SourceOccurrence`；结算过程中有 Hook 介入时每张卡重新定位。`Kernel.TransformSelectedTo` 对应 `CardChoiceResolution.TransformSelectedCards`（Charge/Guards/Begone 的固定 Minion 替换），同样按值与同值序号重定位；`Duplicate`（HeirloomHammer 的 `CreateClone` 复制）与 `AutoPlayRepeated`（DecisionsDecisions）在卡定义内按同一位置规则结算。
- 时机标签按原引擎 `SetActionChoiceTiming` 的位置切换：出牌为 Action，回合结束开始时为 PlayerTurnEnd，敌方回合为 EnemyTurn，玩家回合开始为 PlayerTurnStart。回合开始的选择由 `EndTurn` 动作携带。
- **固定选项选择。** Knowledge Demon 的 CURSE_OF_KNOWLEDGE 没有牌堆来源：选项是两张诅咒卡的值（`ChoiceKind.SelectOption`、`ChoiceEffect.ApplyKnowledgeCurse`、`OptionsAreGenerated`，`Source=None`，1..1）。`Kernel.ChooseOption` 与卡牌选择共用 `TryTakeChoice`，因此动作必须恰好选中一项；选择后按所选卡施加对应的诅咒 Power，并把恶魔的计数器加一。选项顺序与原引擎 `KnowledgeDemonChoiceSupport.BuildChoices` 相同。

## 11. RNG

同种子 SL 模式下，随机数状态是已知的，搜索可以直接读取。内核必须和原版消耗随机数的次数、顺序完全一致。

- 九条流与原引擎一致：Shuffle、CombatCardGeneration、CombatPotionGeneration、CombatCardSelection、CombatEnergyCosts、CombatTargets、CombatOrbGeneration、MonsterAi、Niche。
- 每条流保存计数器与四个状态字，算法逐位复刻原版 `Rng`/`MegaRandom`，用纯内核检查对照原版类录制的输出向量。
- 每次取值计数器 +1，与原版一致；状态字和计数器都进入状态字节。

## 12. 强制行动与眩晕

眩晕、失衡和“必须先执行一次”的行动会临时改写怪物的下一步。内核用一个合成的 `STUNNED` 节点加动态后继来表示，不改动编译好的状态机。

来源：`Q/SimulatedCombatState.MonsterAi.cs:58-128`、`Q/SimulatedCombatState.cs:1174-1181`、`Q/CombatBeamSolver.Expansion.Replay.cs:1105-1180`、`P/BranchMonsterAi.cs:139-166,245-255`。

**规则编译**（`RulesCompiler.CompileMonster`、`CompileMove`）：`MoveState.MustPerformOnceBeforeTransitioning` 编译成 `MonsterNode` 标志。为每个怪物在已登记状态（以及未登记进状态表的初始分支）之后追加一个合成节点：Id 为 `STUNNED`、`Kind = Move`、无攻击、无效果、`MustPerformOnceBeforeTransitioning = true`、静态后继为空。已有节点下标保持不变；节点的后继动态存放在 `Enemy.ForcedMove`（255 表示当前不是 STUNNED）。节点总数因此加一，超过 64 个节点的状态机在导入时按容量拒绝。

**失衡眩晕**：一次怪物行动的全部命中若至少有一次 `DamageResult.WasFullyBlocked`，且该怪物仍有 `ImbalancedPower`，`MonsterMoveSemantics.ApplyForecastMove` 在攻击收尾后、行动附加效果前强制 `STUNNED` 并 `StunNextMove`。Sts2Solver 的 `Damage` 返回"是否有完全格挡结果"，在 `ExecuteMonsterMoveCore` 的同一位置执行；BowlbugRock 的合成后继是 `HEADBUTT_MOVE`，其余怪物用最近一条行动日志。沉睡甲虫的 `SlumberPower` 在受击掉到 0 时强制 `STUNNED`（后继 `ROLL_OUT_MOVE`），在侧回合末掉到 0 时清 Plating 并由 `SNORE_NEXT` 条件分支走到 `ROLL_OUT_MOVE`。

**强制行动 API**：

- `Kernel.ForceMonsterMove(ref Exec, byte owner, int node)`：把当前节点换成 `node`，清除 `NeedsInitialRoll` 与 `ForcedMove`。不写 `LoggedMoves`/`RecentMoves`（`BranchMonsterAi` 只在 `Advance`/`RollInitial` 落到行动节点时写日志）。
- `Kernel.ForceStunnedMove(ref Exec, byte owner, int nextNode)`：`nextNode` 小于 0 时取原引擎的缺省后继 `StateLog.LastOrDefault() ?? Current.Id`，即最近一条日志（`RecentMoves` 的末项），日志为空时取当前节点；随后写入 `ForcedMove = nextNode`、`Move = StunnedNode`、清除 `NeedsInitialRoll`。同样不写日志：原引擎的 `ForceStunnedMove` 只替换 `Current`。
- 原引擎的 `performedMoves` 是行动开始时的 `MoveState` 对象；强制行动在本轮已经行动之后替换当前节点时，这个对象与新的 `Current` 不同。Sts2Solver 用 `EnemyFlags.ForcedMustPerformAfterMove` 记录这一条件（`ForceStunnedMove` 总是新建对象，在已行动之后调用时记录；`ForceMonsterMove` 只在目标不同且目标 must-perform-once 时记录）。`ActingThisRound` 覆盖原引擎在动作返回后才写入 performed 标记的时序。两个标志都只在敌方回合内部使用，`PrepareMonsterMovesForNextRound` 消费并清零，稳定边界上恒为 0。

**推进**（`PrepareMonsterMovesForNextRound`，逐行对应 `MonsterAi.cs:102-128`）：不在场或不能行动则跳过；`NeedsInitialRoll` 走 `RollInitial` 对应的分支；本轮没有执行任何行动则跳过；当前是 STUNNED 且 `SkipNextMove` 为真则不推进；被强制替换且 must-perform-once 未满足则不推进；否则从 `ForcedMove`（STUNNED）或节点的静态后继推进，`ResolveMonsterState` 落到行动节点时写入日志并输出强制行动。被 `SkipNextMove` 跳过的敌人仍记入 `PerformedMoveThisRound`（`Replay.cs:1120-1125`）。

**执行**：STUNNED 没有攻击，跳过 `BeforeAttack`/`AfterAttack`；`MonsterDef.ApplyMoveEffects` 仍以 `STUNNED` 调用，怪物定义可据此实现 `MonsterMoveEffects.Apply` 的 STUNNED 分支（例如乐加维林族母清空格挡）。STUNNED 本身不写行动日志。

**导入**：原生当前行动 Id 为 `STUNNED` 时映射到合成节点，后继（`FollowUpState?.Id ?? FollowUpStateId`）映射到 `ForcedMove`；后继仍是 `STUNNED` 时显式拒绝。合成 `STUNNED`/`REVIVE_MOVE` 只安装在 `MonsterModel.NextMove`，不会登记进 `StateLog` 或 `MoveStateMachine.States`，因此不能按注册表查后继。当前行动 `STUNNED` 且原生持有 `ImbalancedPower` 时同时置 `EnemyFlags.SkipNextMove`：`ImbalancedPower.AfterDamageGiven` 只在完全格挡的攻击后调用 `CreatureCmd.Stun`（BowlbugRock 走 `IsOffBalance`），这是该眩晕的唯一原生来源。进行中的 must-perform-once 行动由节点标志表示，`PerformedMoveThisRound` 在稳定边界上为 0，因此导入后不会误推进。

**乐加维林族母的 `_isAwake`**：原引擎在 AsleepPower/STUNNED 中写入、在指纹与续接文本中读取，但行动选择读 AsleepPower 数量（`BranchMonsterAi.ResolveConditional`），该布尔值不影响后续结算。原引擎的不可投影状态检查只放过 `(LagavulinMatriarch, "_isAwake")` 这个已证明惰性的 `_monsterIntStates` 条目（不修改模拟器状态）；其他怪物私有整数仍被拒绝。

## 13. 敌人生成与召唤

战斗中途出现的敌人（召唤、分裂、替换）也必须和原版一样分配 CombatId、生命、槽位和初始行动。能生成哪些怪物在导入时就确定下来，它们的规则提前编译进规则表。

来源：`P/MonsterSpawnSupport.cs`、`Q/SimulatedCombatState.cs:2800-2875,2977-3000`、`P/MonsterMoveEffects.cs` 的 `Fogmog`/`TwoTailedRat` 分支、`P/DeathPowerSupport.cs` 与 `Q/SimulatedCombatState.DeathLifecycle.cs:100-119,190-220`。

**规则编号与闭包**：每个 `Enemy` 用 `Rules` 记录怪物规则表下标；根已知敌人按 `KnownEnemies` 下标占用表的前 `RootEnemyCount` 个条目，之后每个可生成怪物类型一个条目。`MonsterDef.SpawnedMonsterIds` 声明该怪物能生成的原版 Id，`RulesCompiler` 从根敌人出发递归求闭包。可生成类型的状态机、静态值与初始私有整数，由编译器在一个私有的临时 `CombatState` 上走原版的创建路径得到：克隆规范怪物模型（`ToMutable`）、套用定义的生成配置、由原版 `SetUpForCombat` 生成状态机，再用和已知敌人相同的 `CompileMonster` 编译；不手抄静态值。闭包里有没有定义的怪物时按 `unsupported-closure` 拒绝；规则表总容量 `Limits.MonsterRules`（32），溢出按 `capacity` 拒绝。

**`SpawnMonster`**（`Kernel.SpawnMonster`，严格对应 `MonsterSpawnSupport.AddCreated` 的顺序）：

1. `CombatId = NextCombatId++`；生命在 `[MinInitialHp, MaxInitialHp]` 中排除名单敌人的当前 `MaxHp` 后取值。可选值按原引擎 `HashSet<int>` 的枚举顺序（等于升序插入顺序）由 Niche 流 `NextInt(0, count)` 取第 k 个；没有可选值时用 `NextInt(min, max + 1)`。K0 断言该枚举顺序。
2. 追加到已知敌人数组（`EnemyCount++`），再按遭遇槽下标稳定插入名单，对应 `AddCreature` + `SortEnemiesBySlotName`；同槽位保持先后顺序，无槽名（255）排在有名字的槽位之前。
3. 写入编译器捕获的 `SetUpForCombat` 初始值：`PendingInitialMoveNode`、初始 AI 寄存器。
4. 调用定义上的 `ApplyEntrancePowers`（对应 `ApplyNativeEntrancePowers`，只为已登记的内容实现）。
5. 玩家回合内立即从 `InitialNode` 掷出初始行动并写日志；敌人回合内保留 `NeedsInitialRoll`，由本轮结束的 `PrepareMonsterMovesForNextRound` 掷出。例外：状态机构造器已经把初始 `MoveState` 记进 `StateLog` 的怪物（`MonsterRules.InitialMoveLogged`），直接采用这个行动（`NeedsInitialRoll = false`），敌人回合内生成时也在 `PrepareSpawnedMonster` 立即记录，而不是等到 `PrepareMonsterMovesForNextRound`，因为回合可能在玩家死亡时提前结束。对照 `RegisterPendingInitialMonsterAi`。
6. `minion` 为真且没有 MinionPower 时补 1 层 Minion，applier 是召唤者。

**Fabricator 与 Axebot 生成**：Fabricator 按 `MonsterSpawnSupport.SpawnFabricatorBot` 的同一随机选择（`MonsterAi` 流 `NextItem`，排除 `fabricator_last_spawn`）生成 Guardbot/Noisebot/Zapbot/Stabbot，槽位取下一个空槽并带 Minion；Zapbot 的入场 HighVoltagePower 2 由 `ApplyEntrancePowers` 补上。Axebot 被击杀且仍有 StockPower 时，在死亡钩里以同一槽位生成替代体（`StockPower.AfterDeath`）。补货时新的层数是原层数减一，而 `RespawnCount = 2 - StockAmount` 会平移整个初始生命区间 `MinInitialHp..MaxInitialHp`。生成规则按 StockAmount 1 编译（从 BOOT_UP 开始），`AfterDeath` 传入 `hpRangeShift = (1 - newStock) × 10`，`RollInitialHp` 在平移后的区间上做“避开名单同生命”的抽取；入场只补上剩余的 StockPower 层数，不再加生命。

**空槽**：`Kernel.NextFreeSlot`/`LastFreeSlot` 与 `SimulatedCombatState` 同义：战斗没有遭遇时返回 `null`；有空槽时返回遭遇槽名；`NextFreeSlot` 在无空槽时返回 `""`，`LastFreeSlot` 在无空槽时返回 `null`。

**幻象与爪牙**：`EyeWithTeeth` 入场时先获得 IllusionPower 1（applier 为自己）再获得 MinionPower 1（无 applier），与 `ApplyNativeEntrancePowers` 一致。IllusionPower 的 `PreventsRemovalAfterDeath` 让尸体留在名单；其 `AfterDeath` 对应 `BeginIllusionRevive`：死亡阶段改为 `Reviving`，强制合成 `REVIVE_MOVE` 节点，动态后继取最近一条非复活行动日志。规则编译在 `IllusionRevive` 为真的怪物上追加该合成节点（无攻击、必须先执行一次），节点下标存在 `MonsterRules.ReviveNode`。`REVIVE_MOVE` 执行时把生命回满并清回复活阶段，随后按动态后继推进。尸体的 Power 清理按 `RemovePowersAfterDeath`：有 IllusionPower 时保留增益与临时减益、移除其余减益；已离开名单的敌人清空全部 Power。MinionPower 的 `OwnerIsSecondaryEnemy` 让主要敌人死亡时连带杀死剩余爪牙；`MinionPower.ShouldPowerBeRemovedAfterOwnerDeath` 为假。

**TwoTailedRat**：AI 寄存器 0 是 `_turnsUntilSummonable`（初始 2），1 是 `_callForBackupCount`（初始 0）。`SCRATCH_MOVE`/`DISEASE_BITE_MOVE`/`SCREECH_MOVE` 递减前者；`CALL_FOR_BACKUP_MOVE` 在 `LastFreeSlot` 生成一只新鼠，再把名单中所有鼠的后一个寄存器同步为（最大值 + 1）。随机分支权重按 `BranchMonsterAi.GetBranchWeight`：可召唤时 CALL_FOR_BACKUP 为 0.75、其余三者各 1/12；不可召唤时 CALL_FOR_BACKUP 为 0、其余为 1。可召唤条件按 `CanTwoTailedRatSummon`：回合计数不大于 0、计数小于 3、`NextFreeSlot` 非空，且其他鼠当前行动不是 CALL_FOR_BACKUP。`SCREECH_MOVE` 还给玩家 1 层 Frail。

**寄生死亡与召唤**：`InfestedPower` 在击杀内部、原版 `AfterDeath` 的位置生成四只 `Wriggler` 到 `wriggler1..4` 槽位，创建时 `StartStunned = true`；此时尸体还在名单里，与原版先 `AfterDeath` 后移出战斗的顺序一致。它们是主要敌人，同一次攻击的后续命中可以打到它们。`HasUnresolvedSpawningDeath`（`SpawnsPrimaryEnemyOnDeath`）让尸体已离开名单、死亡效果尚未结算时 `IsEnding` 继续为假；击杀已在内部结算，只有不经过 `Kill` 的死亡才会走到这里。

**重接复活**：`ReattachPower` 不阻止击杀命中，但阻止尸体离场（`PreventsRemovalAfterDeath`），`ShouldPowerBeRemovedAfterOwnerDeath` 为假。死亡效果清算时，若还有另一段能行动（存活或 `Reviving`）则死亡阶段改为 `Reviving` 并强制 `DEAD_MOVE`，否则所有段改为 `PermanentlyDead`。`REATTACH_MOVE` 由怪物的 `ResolveReviveMove` 处理：仍有一段能行动时按 `ReattachPower` 数量回血并清除 `Reviving`。Reviving 段的 `CurrentHp` 为 0，`IsHittable` 自然为假；评估的 `EffectiveEnemyHp` 在有其他未永久死亡段时取 `ReattachPower` 数量，否则为 0。`ShouldOwnerDeathTriggerFatal` 同样按分支名单的 Power 与 HP/死亡状态计算；Sts2Solver 使用 `Battle` 段状态，原引擎使用 `SimulatedCombatState.ShouldOwnerDeathTriggerFatal`，双方都不能调用原版 live `ReattachPower.AreAllOtherSegmentsDead`。

**导入认领**：`_monsterIntStates` 的每个条目必须被某个已知敌人的 `AiRegisterNames` 或 `InertMonsterIntNames` 认领，否则按 `UnrepresentableState` 拒绝；声明为寄存器的名字取 `GetMonsterInt` 的值（有捕获条目用条目，没有时读模型字段），因此与原引擎没有条目时的语义一致。乐加维林族母与沉睡甲虫的 `_isAwake` 由 `InertMonsterIntNames` 声明。Fabricator 的 `fabricator_last_spawn`（经 `GetFabricatorLastSpawn`）、TestSubject 的 `_respawns`/`_extraMultiClawCount`/`SecondFormHp`/`ThirdFormHp` 与 Aeonglass 的两个字典计数同样投影为 AI 寄存器。

## 14. 偷窃与瀑布巨兽

这两类怪物带有跨回合、跨死亡的账本（被偷的牌和金币、蒸汽爆发阶段），死亡和逃跑时的处理各不相同。

来源：`P/MonsterMoveEffects.cs` 的 THIEVERY/ESCAPE/GIMME 分支、`S/SimulatedCombatState.Theft.cs`、`S/CorePowerSupport.ApplyEnemyDeathPowers`、`S/SimulatedCombatState.cs:1363-1460`。

- **偷窃。** THIEVERY 在攻击前按 `ThievingHopper._stealPriorities`（Uncommon → Common/Rare/Event → Basic/Quest → Ancient/Imbued；前三类排除 Imbued）从抽牌堆+弃牌堆的牌组卡中选一张，用 `CombatCardGeneration` 流取走并从战斗中移除，完整 `Card` 存 `DetachedCards`、`SwipePower.Aux0` 指向槽位。GIMME/DOUBLE_SMASH/HEHE 在攻击后 `RecordThievery`：每个 `ThieveryPower` 按 `min(层数, 当前金币)` 偷金币，写入 `Gold` 余额、Power 的 `Aux0` 账本和 `OutstandingStolenGold`。`SurprisePower` 的死亡效果（`SURPRISE_POWER.DeathTrigger`）先创建 fat（它的 CombatId 和 Niche 流的生命抽取都在 sneaky 之前），把每份 `ThieveryPower.Aux0` 转移到 fat 身上的 `HeistPower`（保留 Target），再生成 sneaky。死亡清算末尾 `RecoverStolenResources` 按 `SwipePower` 回收卡数、按 `HeistPower.Amount` 回收金币。`SwipePower.BeforeDeath` 在飞贼死亡时把 `StolenCard.DeckVersion` 追加到 `runState` 牌组末尾；Sts2Solver 在死亡移除 `SwipePower` 时保留 detached 卡值并清除 `RemovedFromState`，写回再把对应牌组卡移到末尾。逃跑时 `CreatureEscaped` 先移除全部 Power；Sts2Solver 保留 detached 卡值并保留 `RemovedFromState`，写回按“丢失”从牌组移除。仍被 `SwipePower.Aux0` 引用的 detached 行是仍被扣住的卡，写回直接移除。`OutstandingStolenCards`/`OutstandingStolenGold` 按原版在死亡回收、逃跑不回收。
- **逃跑。** `CreatureEscaped` 把生物移出 `CombatState.Enemies`，Sts2Solver 在左移 owner 下标和名单后，把该怪物作为 `EnemyFlags.Escaped | DeathPhase.PermanentlyDead` 墓碑追加到已知敌人表末尾（不进入名单），保留 `MonsterId`/`CombatId`/`Slot`/`Rules` 供历史写回补上该怪物。`LiveCombatTracker` 用原版 `CreaturesChanged` 事件记录 `AddCreature` 的**加入顺序**（不是 CombatId 或槽位顺序），并把已逃跑生物按逃跑顺序追加到已知敌人末尾；逃跑生物的 loot 在 `CreatureCmd.Escape` 移除 Power 前已不可见，tracker 把逃跑前的 `ThieveryPower`/`HeistPower`/`SwipePower` 数量计入不可追回的 outstanding 计数。
- **Possession。** `PossessStrengthPower`/`PossessSpeedPower` 的 `_internalData` 是 `Dictionary<Creature, decimal>`，`AfterPowerAmountChanged` 记录带符号的负值；导入按玩家目标读取当前账本值；根账本为空时投影为 0。
- **瀑布巨兽。** `PRESSURIZE/STOMP/RAM/SIPHON/PRESSURE_GUN/PRESSURE_UP` 在攻击后施加 `SteamEruptionPower`（数值见 `MonsterMoveEffects`），`PRESSURE_GUN_MOVE` 另把 `PressureGunIncrease` 加入原版 `_currentPressureGunDamage`（`AdjustMonsterMoveDamage` 的 `damage + bonus` 效果）；`SIPHON_MOVE` 治疗 `SiphonHeal`。原版没有原引擎的 `_pressureGunBonus` 字段：AI 寄存器 `PressureGunBonus` 由 `_currentPressureGunDamage - BasePressureGunDamage` 投影，`SteamEruptionDamage` 由 `_steamEruptionDamage` 投影；两者在根编译时都为 0。死亡时 `SteamEruptionPower.AfterDeath` 立即调用 `Kernel.TriggerSteamEruptionDeath`（对应 `WaterfallGiant.TriggerAboutToBlowState`）：HP/最大 HP 设为钳制值、强制 `ABOUT_TO_BLOW_MOVE`、记录 `AboutToBlow` 阶段并清理其它 Power（`SteamEruptionPower.ShouldPowerBeRemovedAfterOwnerDeath=false` 保留自身）。因此同一动作里后续的伤害仍会打在这个被改写并复活的巨人身上；死亡效果延后清算不会再次触发改写。敌方回合在普通行动前消费强制阶段：AboutToBlow 记录当前层数为 `SteamEruptionDamage`、移除 Power、切到 Explode；Explode 用记录值经完整伤害管线打玩家一次，再 `Kill(owner, force:true)` 并结算死亡效果。爆发阶段 `EffectiveEnemyHp` 为 0，威胁投影只计 Explode 的记录伤害；`SteamEruptionPower` 的 `PreventsCombatEnding`/`PreventsRemovalAfterDeath` 与 `Kernel.IsEnding`、`ShouldRemoveAfterDeath` 一致。导入时 `ABOUT_TO_BLOW_MOVE`/`EXPLODE_MOVE` 分别投影为 `AboutToBlow`/`Explode`，见 [原生导入](import.md#3-battle-字段的来源)。
- **评估。** `OutstandingStolenResource` 是金币+卡数计数，`PreserveResources` 策略下乘 `OutstandingStolenResourcePenalty` 进评分并作为最终路线的追回键；`MinimumOutstandingStolenResource` 只把已不可追回的部分留给 incumbent 下界（当前偷窃根的下界因 `theft_policy`/`swipe_power` 显式禁用）。

具体药水与遗物的结算见 [内容专属机制](combat-content.md#7-遗物与药水)。
