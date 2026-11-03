# 原生战斗状态导入

[返回文档索引](README.md)

## 1. 职责与边界

Import 层把游戏里正在进行的战斗读出来，变成三样东西：`Battle`（当前状态）、`Rules`（整场不变的规则表）和搜索请求（评分表、下界、政策）。它只读游戏对象，不改游戏状态；读私有字段的代码集中在 `NativeAccess`。和 CombatSolver 不同，这里直接从原生 `CombatState` 投影，中间不建 `SimulatedCombatState` 影子状态。

Core 不引用 Import。Import 引用游戏程序集，所以不在 `Sts2Solver.sln` 里（CI 没有游戏文件）。会写游戏状态的建局、注入和转储代码在 `src/TestHost`（`Sts2Solver.TestHost`），它也引用 `sts2.dll`、Harmony，并通过 publicizer 访问原生内部成员，但只给差分宿主和测试版 Mod 用。

不少导入口径沿用 CombatSolver 的原导入层（见 [术语](README.md#术语)），文中的“对照”指它的源码。怎样检查导入结果见 [测试与验证](testing.md#7-差分宿主)，冻结快照的格式见 [冻结快照](golden-data.md)。

## 2. 入口

```csharp
Rules rules = RulesCompiler.Compile(combatState, player, runState);
Battle battle = BattleImporter.Import(combatState, player, rules, options);
```

- `combatState`：当前的原生战斗。导入和规则编译都只读。
- `player`：本地单人玩家。普通根要求处于玩家出牌阶段；回合准备根见 [8.1](#81-回合准备根)。
- `runState`：玩家所在的跑局，必须和 `combatState.RunState` 是同一个实例。规则编译从它读当前地图坐标、解锁状态和 `CardMultiplayerConstraint`，并把它当作召唤物临时创建路径的 RunRng。
- `rules`：`RulesCompiler` 为这个根编译出的规则表。`BattleImporter` 不要求它来自冻结数据。前缀根和回合准备根必须用编译结果：前者要先有前缀之前的规则表才能回放，后者要用一张共享的规则表导入同一 fixture 的多个准备根。
- `options`（`ImportOptions`）：根上没有原生对应物的少量事实。默认值描述普通出牌阶段的根。

| `options` 字段 | 谁设置 | 用途 |
| --- | --- | --- |
| `ShufflesCrossed`、`HasRisk` | 根捕获 | 原引擎快照里的根事实 |
| 事件失败边界 | 根捕获 | `EventDefeat` 根 |
| `TurnSetupRoot`、`RootCards` | 离线宿主 | 回合准备根，以及 Start 时刻的卡牌对象集合 |
| `KnownEnemies`、`AllowDeadEnemies`、`RootEnemyCount` | `LiveCombatTracker` | 实时导入时固定敌人行的顺序，保留死亡墓碑 |
| `RevivingEnemies` | `LiveCombatTracker` | 预测为复活中的死亡行 |
| `Reconciliation` | `LiveCombatTracker` | 从原生 History 重建本回合和整场计数 |

最后三组字段只由 `src/Import/Combat/LiveCombatTracker.cs` 使用；根捕获不设置它们，结果与冻结快照一致。

输出先从 `default(Battle)` 开始，再逐字段写入，不复用缓冲，所以填充字节始终为零。卡牌的小数变量最后按“`DetachedCards` → 牌堆存储顺序”规范化。

## 3. `Battle` 字段的来源

| 字段组 | 原生来源 | 实现 |
| --- | --- | --- |
| `Round`、`PlayerTurn`、`Energy`、`Stars`、`Gold`、`Side`、`Phase` | `CombatState`、`PlayerCombatState`、`Player` | 直接读 |
| `NextCombatId` | `CombatState._nextCreatureId` | 直接读 |
| `ShuffleEventCount` | 模拟器创建时为 0 | 根事实 |
| `ShufflesCrossed` | 原引擎 `SimulationSnapshot` | 根事实（`ImportOptions`） |
| `Outcome` | 只有 `EventDefeat` 边界需要外部事实 | 根事实 |
| `Flags` | `CombatManager.IsAboutToLose`、`Player.IsActiveForHooks`、`Player.Osty`、原引擎快照风险 | 直接读 + 根事实 |
| `Player`、`Osty` | `Creature.CurrentHp/MaxHp/Block`；本回合未格挡伤害来自原生 History | 直接读 + History |
| `Rng`（9 条） | `RunState.Rng` 的 9 条流 | 直接读 |
| `Enemies` | `CombatState.Enemies`、`MonsterModel.NextMove/StateLog`、`MoveStateMachine`、`Rules.MonsterRule` | 直接读；AI 节点映射由规则编译器冻结 |
| `Powers` | `Creature.Powers` 的原生顺序，按 owner 分组 | 移植自原导入层 `FlashStateProjections` 的分支表；私有字段由 `NativeAccess` 集中读取 |
| `Relics` | `Player.Relics` 顺序 | 移植自 `ProjectRelic`；`StatefulRelicState` 从原生字段或原生 History 读取 |
| `Cards` | 五个牌堆的原生顺序、`CardModel`、`Rules.Card(id, upgrade)` | 移植自 `FlashStateImporter.Card` |
| `DetachedCards` | `NightmarePower.selectedCard`、`SwipePower.StolenCard` | 直接读 |
| `DecimalVars` | `DynamicVars` 与规则基值的差 | `DecimalVars.TryAllocate/TryCanonicalize` |
| `Counters` | `CombatManager.History` 的 13 类条目，加上根上为 0 的账本 | 独立实现，对照 `RootCombatHistorySnapshot`、`SimulatedCombatState` 的 getter |
| `HistoryCourseCard` | 原引擎根上的 `_lastAttackThisTurn` | 按原导入层的根口径物化；前缀根和准备根的起点为空，回放后由内核维护 |

新根上的成长、死亡、偷窃与累计 HP 账本都从零开始，本回合计数从原生 History 现算。`VitalSparkNormalizedAmount` 在普通根是 `int.MinValue`，在回合准备根是 0（原导入层在准备阶段至少做过一次规范化）。

### 3.1 实时导入与根捕获的区别

自动战斗每执行完一个动作都会重新导入（`Reconciliation = true`），用来和预测比较。它和根捕获有这些区别：

- `CardsPlayedThisTurn`、`SkillCardsPlayedThisTurn` 按“已完成的出牌”计数，与内核的 `RecordCardPlayed` 一致；根捕获保留原导入层“序列第一张”的口径。
- `CumulativeHpLost`、`PlayerUnblockedDamageEvents`、`OrbChannels` 从整场 History 现算；根捕获写 0 或本回合值，与冻结快照一致。
- `SkillPlayedThisSideTurn` 从本回合已完成的技能推导；根捕获写 0。
- 原生会在敌人死亡后把它移出 `CombatState.Enemies` 并重置行动状态机。实时导入用会话开始时冻结的已知敌人顺序保留墓碑行；预测为 `Reviving` 的行按复活行导入，行动和 AI 仍从原生 `MonsterModel` 读；永久死亡的行不读已经重置的状态机。
- `Roster` 按 `CombatState.Enemies` 的当前顺序导入（原生 `AddCreature` 后按遭遇槽稳定排序）。冻结的已知顺序只决定敌人行的下标；之后出现的召唤物按 `CombatId` 升序追加，恢复内核的创建顺序，而不是原生的槽位顺序。
- 卡牌 `Keywords` 只读 `CardModel.GetKeywordsWithSources(KeywordSources.Local)`。`CardModel.Keywords` 还会运行 `TryModifyKeywordsInCombat`（HexPower 给 Hexed 的牌加虚无）；这个授予在内核里是惰性查询，写进卡行的话，Hex 移除后会残留并被重复计算。
- `SOVEREIGN_BLADE` 的 `Private` 写成当前 `SWORD_SAGE_POWER` 的层数，因为原生 `BaseReplayCount` 已经包含这张牌见过的每次 SwordSage 加成；根捕获仍写 `Rules.RootSwordSageAmount`，保持冻结基线。

合成的 `STUNNED`、`REVIVE_MOVE` 只存在于 `MonsterModel.NextMove`，不在 `StateLog` 或 `MoveStateMachine.States` 里；后继统一从 `NextMove.FollowUpState?.Id ?? FollowUpStateId` 读取。当前行动是 `STUNNED` 且原生持有 `ImbalancedPower` 时，同时置 `EnemyFlags.SkipNextMove`，原因见 [内核语义](combat-semantics.md#12-强制行动与眩晕)。

## 4. 规则编译

`RulesCompiler` 输入原生战斗、玩家与 `RunState`，输出 `Rules`。语义以 CombatSolver `src/Flash/Bridge/FlashRulesCompiler.cs`（`a62f1850`）为准：原引擎对模拟状态的访问改成读原生模型，不复制 `src/Engine` 的状态定义。

卡牌规范值不读根上的卡，因为根卡可能带 Dampen、附魔或临时修饰。编译器取 `CanonicalInstance`，按目标升级等级逐级克隆并升级；升级后的 `CurrentUpgradeLevel` 必须等于目标等级，否则以 `unsupported-card` 拒绝。卡池和药水池都使用玩家 `UnlockState` 里的同一份解锁状态。

已知敌人的静态参数与初始 AI 寄存器由 `NativeAccess.MonsterInt` 读取。召唤物在一个私有的临时 `CombatState` 里通过 `ToMutable` + `SetUpForCombat` 创建，所以它的状态机、初始节点和静态值不依赖根上的同类型敌人。召唤闭包保持原生的 BFS 顺序。

### 4.1 30 个字段

`Rules` 有 30 个构造字段，编码时全部写入（`RulesCodec`）：

| # | 字段 | 实现 |
| ---: | --- | --- |
| 1 | `Cards` | 原生 `CardModel.ToMutable()` + `UpgradeInternal/FinalizeUpgradeInternal` 克隆规范卡；根卡、生成卡、卡池卡按升级等级建表 |
| 2 | `PowerVars` | 规范 Power 的 `DynamicVars[name].BaseValue`，名字来自 `PowerDef.VarNames` |
| 3 | `Potions` | 规范药水的目标、稀有度、变量；战略成本查固定政策表（Token=0、High=18、Elevated=14、其余=9） |
| 4 | `RelicVars` | 规范遗物的 `DynamicVars[name].BaseValue` |
| 5 | `Monsters` | 原生 `MonsterMoveStateMachine`、静态权重、条件分支、攻击意图与怪物私有整数；已知敌人按根上的模型编译 |
| 6 | `SpawnRules` | 从已知敌人的 `MonsterDef.SpawnedMonsterIds` 做 BFS，按首次出现的顺序分配下标 |
| 7 | `CardPools` | 玩家真实解锁状态 + `CardFactory.FilterForCombat` + 单人、多人约束 |
| 8 | `PotionPool` | `PotionFactory.GetPotionOptions` + `CanBeGeneratedInCombat` |
| 9 | `OutOfCombatPotionPool` | 同一批药水选项，不带战斗内过滤 |
| 10 | `RootPotions` | 原生药水槽的 `id + 1` |
| 11 | `PowerDeathHistoryRisk` | 查 `MirrorRiskTables` 静态表 |
| 12 | `HasEncounter` | 原生 `Encounter != null` |
| 13 | `EncounterSlots` | 原生 `Encounter.Slots` 的顺序 |
| 14 | `RootEnemyCount` | 原生 `CombatState.Enemies.Count` |
| 15 | `BaseMaxEnergy` | `Player.MaxEnergy`（不经过 Hook 的基础值） |
| 16 | `PlayerSlotIndex` | 单人恒为 0 |
| 17 | `BaseOrbSlotCount` | `Player.Character.BaseOrbSlotCount` |
| 18 | `RootSwordSageAmount` | 玩家 `SwordSagePower` 的当前层数 |
| 19 | `UnsettlingLampRelicId` | 遗物顺序中第一个 `UnsettlingLamp` 的规则 id |
| 20 | `HasIdentityContent` | 角色类型 + SwordSage + 根上卡牌、Power、遗物的身份标记 |
| 21 | `HasShuffleOrderEnchantment` | 根卡附魔的 `HookMaskExt.ModifyShuffleOrder` |
| 22 | `HasBeforeFlushEnchantment` | 根卡附魔的 `HookMaskExt.BeforeFlush` |
| 23 | `HasTheftContent` | 两个盗贼遭遇、盗贼怪物，或根上的盗贼 Power |
| 24 | `HasDecimalCardVars` | 已编译的卡牌中有任一 `UsesDecimalDamageVar` |
| 25 | `HasInfiniteHpDisplayMonster` | 已知敌人或召唤闭包中有任一 `HasInfiniteHpDisplay` |
| 26 | `HasPaelsLegion` | 未熔化的遗物中有 `PaelsLegion` |
| 27 | `FurCoatSpawnHpOne` | 未熔化 `FurCoat` 的标记坐标包含当前地图坐标 |
| 28 | `RootLostHpMask` | 原生 History 的本回合未格挡伤害，玩家、Osty、每个敌人各一位 |
| 29 | `HasHistoryCourse` | 未熔化的遗物中有 `HistoryCourse` |
| 30 | `PenNibRelicIndex` | 遗物行中 `PenNib` 的下标（熔化的也计） |

### 4.2 镜像覆盖率静态表

`Card.PlaysRecordRisk` 与 `Rules.PowerDeathHistoryRisk` 不是游戏数据，而是原引擎 `CardOnPlayMirrors.ResolveDispatchKind`、`AfterDeathMirrors.ResolveDispatchKind` 的镜像覆盖率。本仓库不重写这套推断逻辑，而是把结果冻结为 `src/Import/Combat/MirrorRiskTables.cs`：

- 导出源：CombatSolver `src/Flash/Bridge/FlashRulesCompiler.cs` 的同一查询，源提交 `a62f1850`；导出钩子在 CombatSolver `flash/m0` 的 `91641ff0`（环境变量 `M2A3_RULES_TABLES_DUMP`）。
- 覆盖 `ModelDb.All` 里全部规范内容：603 个原版卡牌 id、283 个原版 Power id。
- 表里没有的 id 以 `missing-mirror-risk` 显式拒绝，不默认为 false。
- `--rules-table-check` 对照 `ModelDb.All` 检查遗漏和多余的 id，并检查注册表里每个卡牌、Power id 都有表项。

游戏或 CombatSolver 更新后，要用上面的钩子重新导出，再用 `--rules-table-check` 复核；不能只手改本表。

## 5. 评估配置编译

评分表 `ScoreRules` 与下界配置 `BoundFeatures.Config` 也从原生模型重新编译，不读冻结的 `config.br`：

```csharp
SearchPolicy policy = SearchPolicyCapture.Capture(combatState, player);
EvaluationRootFacts facts = EvaluationRootFacts.Default with { InitialActionCount = ... };
Scorer scorer = EvaluationConfigCompiler.CompileScorer(combatState, player, rules, battle, policy, facts);
BoundFeatures bound = EvaluationConfigCompiler.CompileBound(combatState, player, battle, policy, facts);
SearchCase request = EvaluationConfigCompiler.CompileRequest(
    combatState, player, rules, battle, policy, facts);
```

`ScoreCompiler` 与 `BoundCompiler` 的语义以 CombatSolver `a62f1850` 的 `src/Flash/Bridge/FlashScoreBridge.cs`、`FlashBoundBridge.cs` 为准；原引擎对模拟快照的访问改成读原生模型和导入后的 `Battle`，不复制 `src/Engine`。

### 5.1 `SearchPolicy`

`SearchPolicy` 是评分、下界和搜索请求编译要读的政策：一部分是玩家设置，一部分从根推出来，构造后不可变。Mod 在搜索请求开始时把持久设置和本场盗贼会话状态冻结进 `SearchPolicyConfiguration`，再交给 `SearchPolicyCapture`；离线对照不传配置，按 CombatSolver M0 宿主当年的固定设置重建，所以同样不需要读 `config.br`。

| 字段 | 默认值 | 作用 | 对照（CombatSolver） |
| --- | --- | --- | --- |
| `GrowthBudgets` | 空 | 十项内置成长额度；`IgnoreLongTermRewards` 打开时有效额度归零。第三方额度不进入这里 | `ModRuntime.ApplyFixedBudgetSettings`、`SolverSettingsData` |
| `RequiredGrowthRewards` | 从根推出 | 手牌和牌堆里的成长来源目标，不只看根快照 | `GrowthOpportunityPolicy.Capture` |
| `HasUnboundedGrowthTargets` | 从根推出 | 有动态复制、重放来源时，目标无法证明 | 同上 |
| `RelicTargets` | 遗物策略开启、规则为空 | 按拥有的遗物、范围和 id 排序过滤 | `SolverSettings.RelicStrategyEnabled` |
| `Act3BossStrategy` | 从根推出 | 是否是第三幕 Boss（三个 encounter id） | `SearchPolicySnapshot.IsAct3BossEncounter` |
| `ActTransitionBossHpStrategy` | `ProgressionFirst` | 是否保留幕间回血 `ActClearHeal` | `SolverSettingsData` |
| `FinalBossHpStrategy` | `ProgressionFirst` | 是否保留 `RunEnding` | 同上 |
| `IgnoreLongTermRewards` | false | 同时影响成长额度和目标 | — |
| `BrightestFlameMaxHpLossLimit` | `null` | 至亮之焰整场累计最大生命消耗上限；`null` 不限，`0..1000` 为上限。根上已消耗量不算策略字段，由请求编译单独读原生历史 | `SolverSettingsData`、`GrowthCostPolicy` |
| `TheftPolicy` | 从根推出 | 盗贼遭遇或怪物时为 `PreserveResources`，否则为空；适用战斗可以由本场会话覆盖为 `LetEscape` | `TheftEncounterStrategy.IsApplicable`、`SolverController.ResolveTheftPolicy` |
| `PotionDefaultMode` | `Smart` | 没有逐瓶指令时的药水模式；当前只接受 `Smart` 和 `Disabled` | `SolverSettingsData.PotionPolicy` |
| `PotionDirectives` | 空 | 逐瓶指令，按当前药栏的 `(slot, potionId)` 对账；接受 `Smart`、`Disabled`、`Force`，`Smart` 不入表 | `CapturePotionStrategy` |
| `BaseScoreOnly` | false | 请求可以覆盖 | M0 宿主 `ordering=baseline` |
| `ContinuousThreatRanking` | false | 请求可以覆盖 | M0 宿主默认 |
| `BeamWeightPerturbation` | 空 | 请求覆盖时项和 scale 必须同时给出 | M0 宿主默认 |
| `HasDevelopmentStrategy`、镜像登记标志、第三方成长标志 | 全空 | 非空且生效时以 `unsupported-policy` 拒绝 | — |

评分和下界编译不读逐瓶药水指令，但请求编译会读；`PotionDefaultMode` 和 `PotionDirectives` 因此留在 `SearchPolicy` 里，再由 `SearchRequestCompiler` 转成 `PotionPolicy`。`FixedBudget`、并行度、`IncludeTurnSetup` 这类搜索执行字段仍然不在 `SearchPolicy` 里。差分宿主的请求 DTO 可以覆盖会影响政策的字段（见 [Differential](../tests/Differential/README.md#输入)）；冻结 fixture 都没有覆盖，全部用默认值和根派生值。未知字段显式失败。

### 5.2 `ScoreRules`

| 字段 | 实现 |
| --- | --- |
| `Cards` | 对注册表里每个卡牌 id、每个 `0..MaxUpgradeLevel`，用规范卡克隆并升级；读 `Damage/Block/Cards` 基值、类型、动态类型、变量下标、攻击段数、飞刀来源、动态变量关键字、回合结束效果、Osty/Shiv 标签、`LatentValue`/`LatentTrait` 与 `CalculatedKind` |
| `Powers` | 每个注册 Power 的 `StrategicKind`、`StrategicRequirements`、`PersistentSetupTraits`、`ITemporaryPower`、原生类型与 Danse Macabre 能量阈值 |
| `PotionStrategicCost` | 规范药水 + `PotionValuationRegistry` 的 Token/High/Elevated/Standard 政策；持有未熔化的 Petrified Toad 时 Potion Shaped Rock 为 0 |
| `GrowthBudgets`、`RequiredGrowthRewards` | 取 `SearchPolicy.Effective*`；第三方额度以 `unsupported-policy` 拒绝 |
| `RelicCounterRules` | 按政策目标的顺序映射规范遗物与 `MeatOnTheBone` 阈值 |
| `PostCombatRelicHeal` | 原生 `BurningBlood`、`BlackBlood` 的 `Heal`，以及 `MeatOnTheBone` 的 `Heal`/`HpThreshold` |
| `BossHpRelief` | 原生 `Encounter.RoomType`、当前幕、`SecondBossEncounter`，再按两个 Boss 策略裁决 |
| `ContinuousThreatRanking`、`BaseScoreOnly`、`BeamWeightPerturbation` | 来自 `SearchPolicy` |
| `InitialPlayerMaxHp`、`IsActEndingBoss`、`InitialEnemyCount` | 导入后 `Battle` 的玩家最大生命、Boss 减免、敌人数 |
| 根药水与药水计数 | 导入后 `Battle` 的 `RootPotions`、`RootPotionSlotCount`；普通根的初始计数为 0 |
| 33 个命名 id | `ModelDb.Power<T>()`、`Card<T>()`、`Relic<T>()`、`Potion<T>()` 的原生 id 经注册表解析；未注册为 `ushort.MaxValue` |

**四个排名基线。** `InitialPersistentBuffValue`、`InitialEnemyStrengthSuppression`、`InitialEnemyWeakTurns`、`InitialRetainedAttackValue` 的求法：先用这四项为 0 的 `ScoreRules` 构造 Core 的 `Scorer`，在根上算 `ScoreFeatures`，再把四个特征值填回最终表。这样与原引擎 `CombatBeamSolver`、`SimulationSnapshot` 冻结的值逐字段一致，又不用复制原引擎的计算。回合准备根按 `FlashScoreBridge.cs:74-80` 把四项强制为 0；前缀根按同一原导入层的请求根语义，用前缀之前的 `Battle` 和规则表计算这四项，评分表本身仍取回放后的内容。

### 5.3 `BoundFeatures.Config`

下界的逐内容分类表冻结在 `src/Import/Combat/HealingBoundTables.cs`。它来自模拟回归数据里 384 份启用了下界的 `config.br`：这 384 份的 `ComponentUnsafe*`、`SafeEnchantment`/`SafeAffliction`、`KnownBlocking*`、`Regen*`/`Reattach*`/`Swipe*`、药水分类与数值逐字节相同，所以只保存一份，生成时逐一比较过。

这些数组是 CombatSolver `FlashBoundBridge` 分类器在导出时的输出，本仓库没有重新推导分类判定。因此 `--config-compare` 在这一部分只证明表被完整、原样地取出并按根正确组装，不是独立的编译证据；覆盖合同验证数组长度等于 `Registry.*Count`。游戏版本或分类规则变化时，要在 CombatSolver 侧重新导出（用 `flash/m0` 的 `FLASH_CONFIG_OUT` 钩子生成新的 `config.br` 再提取），不能只改本表。

根上的判定按 `FlashBoundBridge`：

- 失效顺序：`growth_targets` → `relic_targets` → `theft_policy` → `growth_rewards` → `root_risk` → `pending_returning_cards` → `remaining_certificate`。失效时所有数组为空，`FutureHealPotential = int.MaxValue`、`Certificate = None`、`RootPotionSlotCount = 0`。
- `HasOnlyPostCombatHealing`：角色属于封闭集合、敌人都是原生的、没有药水、遗物只有 Burning Blood、Black Blood、Bound Phylactery、Ring of the Snake，玩家 Power 只有 Neurosurge、Doom，卡牌都在白名单里且没有附魔或苦难。
- `Component` 证书：程序集 MVID 与审计时一致、单人、没有 Modifier、卡牌都属于 109 个 `ComponentInitialCard` 类型且附件安全、根监听者分类安全、上界可计算。`Tainted` 等卡牌附件在根监听者枚举里与原引擎一样被过滤。
- `KnownNative` 证书：单人、全部原生模型与附件、没有 Modifier 或第三方监听。
- `FutureHealPotential`、`PostCombatHeal`、`RequireCertifiedHealingProof`、`PruneEqualHp` 按原导入层的根事实和 `policy.RelicTargets.Count` 设置。`ForceAllPotionsDisabled = false`、`MaximumPotionUses = null`：离线求解器没有用药政策覆盖或上限。
- 有组件证书的根上有 RegenPotion 时，以 `regen_potion_policy` 失效；根上有 SwipePower 时，以 `swipe_power` 失效。

## 6. 搜索请求编译

`SearchRequestCompiler` 只读原生根、已编译的规则和 `SearchPolicy`，冻结 `SearchCase` 需要的评分、下界、根生命、逐瓶药水政策、失窃目标、遗物与成长目标以及回放状态。能力承诺目录根据根中已登记的能力牌和真实的 `RunState.TotalFloor` 编译；生成闭包里的已登记能力也有描述符。默认的药水模式是 Smart；默认策略和空指令直接走原请求对象，不改变 SREQ 字节。

至亮之焰上限来自 `SearchPolicy.BrightestFlameMaxHpLossLimit`。设了上限时，请求编译再从原生 `CombatManager.History.CardPlaysStarted` 读本场已经打出的至亮之焰，按每张牌的动态 `MaxHp` 求和，作为根上的已消耗量；`null` 时不读历史，请求也不写扩展。上限和根上已消耗量只在设了上限时写进 SREQ 末尾的可选扩展（`FLM1`），旧请求没有该扩展时照常解码。

`SearchPerformanceProfile` 给出基准、窄、宽、进攻四种宽度和每单位宽度的时限，并决定是否追加补搜（档位值见 [战斗搜索](combat-ai.md#2-档位与时间)）。Mod 和差分宿主再把宿主的内存预算放进 `CombatSearchRequest`，交给 `CombatSearchCoordinator` 构造成员。成员的宽度、评价、保留、用药限制和时限都在协调器里设置，全部受原请求的保护和强制指令约束；生产搜索没有展开节点上限。宿主回调和帧压力信号只在运行时传递，worker 不读实时战斗或全局搜索设置。

测试用的确定性捕获使用单成员请求编码（`SREQ`），用固定的节点上限（512,000）代替墙钟。生产调度由协调器按计划构造，不经过请求编码。

## 7. 拒绝规则

所有拒绝都抛 `UnsupportedImportException`，`Reason` 是稳定的字符串。绝不返回带默认值的半份 `Battle` 或半份 `Rules`。

| 原因码 | 含义 |
| --- | --- |
| `multi-player` | 玩家数不是 1，或有多人专用内容 |
| `root-phase` | 根不在玩家出牌阶段 |
| `unsupported-card/power/relic/potion/orb/monster/enchantment/affliction` | 内容定义或变量缺失 |
| `unsupported-closure` | 生成卡、Power、卡池、药水池或召唤闭包缺定义 |
| `missing-mirror-risk` | 镜像覆盖率静态表没有该卡牌或 Power id |
| `test-content`、`deprecated-content` | 测试用或已废弃的内容 |
| `third-party-content` | 模型类型来自 `sts2` 以外的程序集 |
| `missing-private-field` | 找不到私有字段、内部数据为 null 或类型不符 |
| `unclaimed-private-state` | 捕获到私有状态，但没有任何投影认领它 |
| `unrepresentable-state` | 原生值无法用 `Battle` 表示（小数、越界、复制品等） |
| `capacity` | 固定容量不足 |
| `rules-mismatch` | 原生根与传入的 `Rules` 不一致，或 `RunState` 不是同一个实例 |
| `known-enemy-history` | 已逃离或已死亡的敌人破坏了 `KnownEnemies` 的顺序 |
| `prefix-plan` | 前缀动作或选择无法按 CombatSolver 的计划语义翻译和回放（缺手牌、目标、选项，或选择次数不符） |
| `turn-setup-root` | 回合准备计划或根集合无法编成一个搜索请求：终局分支、阶段/回合/最大生命/药水槽/敌人数不一致 |
| `run-deck-hook` | 牌组里的卡实现了局外监听 Hook |
| `unsupported-policy` | 评估配置政策含未移植的第三方登记、DevelopmentStrategy 或非法覆盖 |

差分宿主另有两个只在离线对照里出现的原因码：`invalid-golden`（冻结对象损坏）和 `rules-build`（规则编译的内部不变量失败）。

### 7.1 全有或全无

`PrivateStateLedger` 在导入前枚举原导入层会捕获的根私有状态：

- `PowerPredictionStateSupport.CaptureRootState` 的 13 种 Power；
- `RelicPredictionStateSupport.IsTracked` 的 25 种遗物；
- `SimulatedCombatState.IsStatefulRelic` 的 19 种遗物。

每个条目都必须被 `ProjectPower` 或 `ProjectRelic` 认领。例如 `SoulboundPower`、`CacophonyPower` 会被原导入层捕获，但目前没有投影实现，所以根以 `unclaimed-private-state` 拒绝，而不是丢掉这份状态。没有捕获条目的怪物私有整数也要么被 `MonsterDef.AiRegisterNames` 或惰性名单认领，要么拒绝。

### 7.2 第三方内容

读到的每个 `AbstractModel`（卡、Power、遗物、药水、球、怪物、附魔、苦难）都先检查类型所在程序集必须是 `sts2`，否则以 `third-party-content` 拒绝。`RejectRunListenerDeckCards` 对玩家牌组做同样的检查，并拒绝带局外监听 Hook 的非惰性牌组卡。

## 8. 前缀根与回合准备根

**前缀根。** 有些冻结 fixture 在可操作之前还带一段固定动作。导入时先在原生前缀之前导入 `Battle`，再用 `PrefixReplay` 在内核上执行这段冻结动作；前缀之后的规则表由 `RulesCompiler.CompileAfterPrefix` 从“前缀之前的原生状态 + 回放后的 `Battle`”重建。前缀动作本身不在游戏里执行，所以前缀里如果有内核还不支持的结算，会以 `prefix-plan` 或内核异常显式拒绝，不会退回到游戏里重放。实机测试模式显式拒绝前缀动作。

### 8.1 回合准备根

`qf2-gradient-failure`、`ts-b-toolbox`、`ts-a-toasty` 是冻结的回合准备 fixture，`golden.br` 里分别有 4、3、4 个根。`ts-a-toasty` 和 `qf2-gradient-failure` 的 4 个根里各有一对 `Fingerprint.Equal` 的根：按内核去重规则，三个 fixture 的 Play 根数都是 3。原导入层在 `CombatManager.SetupPlayerTurn` 之前捕获 Start 根，再在影子引擎上枚举准备选择。离线宿主在同一个原生入口用 Harmony 前缀记录 Start 时刻的卡牌对象集合，并同时导入两份 Start 根：

- 冻结口径：`ImportOptions{RequirePlayPhase=false, TurnSetupRoot=true, RootCards=...}`，不给 `golden.br`/`config.br` 对照使用；
- 生产口径：`LiveCombatTracker.ImportSetupRoot`，`Reconciliation=true`、`RequirePlayPhase=false`、`TurnSetupRoot=true`，供 `TurnSetupRootEnumerator` 和生产比对使用。生产 setup 根携带最近一次已核对预测边界的非原生账本与复活行；`VitalSparkNormalizedAmount` 按准备根写 0，`ShuffleEventCount` 写 0，`ShufflesCrossed`/`HasRisk` 从 tracker 继承。原生 History 能重建的计数仍从实机读取。

然后按原生准备页的每个枚举计划重建一次同一场战斗（每次跳过测试宿主的初始注入），Play 根用同一个 tracker 的 `ImportForComparison`（`Reconciliation=true`）导入，和内核枚举根比较。这样准备选择本身仍由原版执行，内核不需要重写 `SetupPlayerTurn`。现有第 1 回合 fixture 的 Start 根在两种口径下逐字节相同；第 2 回合起历史计数可能不同（例如 `CumulativeHpLost`、`OrbChannels`），生产口径从 History 重建，内核前奏也按同样的 History 语义累计。

`TurnSetupRootEnumerator` 从 Start 根枚举 Play 根：`Kernel.PreparePlayerPlayPhase` 按原生 `SetupPlayerTurn`/`RunAutoPrePlayPhase` 的顺序重放选择前缀，遇到选择用 `ChoiceEnumerator.Enumerate` 展开物理选项，递归到 `Phase=Play`。分支按枚举顺序去重（`Fingerprint.Equal`，保留先出现的）；去重后的根数上限是 `BeamSolver.MaxChoiceBranches`（32），超出部分丢弃并计数。连续选择超过 `Limits.MaxChoices`（8）不截断，返回不支持；额外回合也不支持。准备过程中结束战斗的分支标记为终局根。

原生 `SetupPlayerTurn` 是独立 task：第一次请求选择时任务暂停，`CombatManager.StartTurn` 先跑 `Hook.AfterSideTurnStart`，紧接着跑 `OrbQueue.AfterTurnStart`，然后才恢复 setup。`PreparePlayerPlayPhase`（回合准备根）和正常 EndTurn 进入下一回合这两条路径都用 `Exec.SetupPause` 在第一次 `PlayerTurnStart` 请求（或用携带的选择回答第一个请求）时插入这两步；没有暂停时侧回合开始仍在 `AfterPlayerTurnStart` 后，球的触发保持原来的位置。原生 `CardSelectCmd.FromHand`/`FromCombatPile` 先发暂停信号、再读取牌堆，所以准备前奏里这些入口在构建候选之前调用 `Exec.RunSetupPause`；候选数量和选择边界仍按原生调用方在暂停前固定的值。Toasty Mittens、Tyranny、Gambling Chip、Foregone Conclusion 等入口即使此时手牌为空也先经过暂停点，侧回合开始新增的牌随后进入候选。额外回合仍不进入这套暂停前奏。准备暂停时侧回合开始或球的触发如果自己请求选择，当前单前缀前奏显式返回不支持；`AfterPlayerTurnStart` 一轮里有多个选择是支持的（例如烤面包手套 + 暴政），侧回合开始和球在第一次选择处只跑一次。`ApplyEnemyDeathPowers` 与 `NormalizeCardAfflictions` 是内核照原引擎补的结算，不属于原生 `StartTurn`，仍放在 `AfterPlayerTurnStart` 之后、AutoPrePlay 之前；暂停时球已经在暂停处跑过，不再在它们后面重复。

`SearchRequestCompiler.CompileSetupRequest` 用 Start 根编译评分、下界与路线排序，把去重后的 Play 根作为 `SearchCase.Battles`；计划放在 host-only 的 `SearchCase.TurnSetupPlans`，按根下标对齐，不进入 SREQ。编译前校验每个 Play 根的阶段、回合、最大生命、药水槽和敌人数与 Start 根一致，不一致以 `turn-setup-root` 拒绝。

原生选项的顺序和枚举顺序不同，比较时按 `golden.br` 的根顺序把独立重建的根逐一对齐；根的内容仍由原生重建。`--setup-enumerate` 还会对每个枚举计划重建一次原生准备页，比较每个计划的 Play 根；三个冻结 fixture 的每个原生计划都一致。多段选择已离线验证：`qf-turn-start-choice-forge` 第 2 回合的 BeforeHandDraw + AfterPlayerTurnStart 两段、`ts-a-toasty` 第 2 回合再次触发；测试专用 fixture 覆盖 AutoPrePlay（`ts-d-autopreplay-choice`）、AfterPlayerTurnStart 两段 + 熔炉侧回合开始（`ts-g-multi-choice`）、同一轮选择后还有监听器（`ts-h-choice-then-listener`）和 Plasma 在暂停处触发（`ts-i-plasma-pause`）。这些 fixture 不属于冻结数据。

## 9. 已知限制

- 冻结 `config.br` 的根顺序仍按 `golden.br` 的选择记录对齐；`--setup-enumerate` 已独立逐计划验证枚举，不再依赖 M0 选择记录来选取根。实机测试模式只捕获第一个准备选项，完整枚举只在差分宿主里验证。
- 回合准备的多段选择、侧回合开始/球在暂停处的顺序已在离线差分里验证。生产 Mod 的 `SetupPlayerTurn` 接管使用 `ImportSetupRoot` 和 `CompileSetupRequest`，只对自动搜索/全自动档、门禁通过且有可见页面的准备执行；纯隐式选择不启动准备搜索。`--setup-enumerate` 仍只覆盖离线枚举，不证明可见页面驱动。
- 准备暂停时侧回合开始或球的触发自己请求选择，当前显式不支持。
- `BeforeHandDraw`/`AfterPlayerTurnStart` 的遗物监听器在 rootSetup 下使用入口快照（按 id/occurrence）并在调用前复查仍存在；这和原生 `IterateHookListeners` 的“入口快照 + Contains 过滤”一致。同 id 遗物在暂停期间被移除/新增的极端情形没有独立 fixture。
- 手牌上限读游戏的 `CardPile.MaxCardsInHand`（当前为 10）。RitsuLib 等 Mod 另行修改手牌上限的方式不在支持范围内。
- 私有字段的读取绑定当前游戏版本；字段缺失、类型不符或内部数据为 null 都显式拒绝。
- 镜像覆盖率表和下界分类表是从 `a62f1850` 一次性导出的数据，不是运行时推断。
- 下界的 environment 证书只移植了 `CanCertifyRemainingHealingEnvironment` 的 Regent、Silent 一般分支；Defect 与 Decimillipede 的特殊分支没有移植，这些根拿不到 environment 证书（证书为 false），下界更保守，但不会被拒绝。
- `Component`、`KnownNative` 证书和 `HasOnlyPostCombatHealing` 按“没有第三方 run/combat 订阅者、BaseLib 卡牌修饰、AdaptedOnPlay”的固定口径处理；实机遇到这些内容时要另行拒绝或补证据。
- `pending-choice` 与 `unsupported-healing-certificate` 两个原因码已经定义，目前没有代码抛出。
