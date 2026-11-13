# Battle 状态布局

[返回文档索引](README.md)

`Battle` 是一场战斗在某个稳定边界上的全部分支可变状态。搜索里的“一个状态”就是一个 `Battle`：复制它就是分叉，在同一份共享根数据下比较它的字节，就能判断两个局面是否相同。所以本文关心三件事：什么值放进 `Battle`、每个值怎么编码、怎样保证同一局面只有一种字节表示。

`Battle` 是 `unmanaged` 结构体，没有对象引用，按值赋值就是深拷贝。牌区是最后一个字段，搜索池只保存“头部 + 前 `CardCount` 张牌”的打包前缀，这段前缀才是状态身份。下标不小于 `CardCount` 的牌格内容无定义，任何代码都不得读取；其它内联数组的未用槽位仍必须为零。

文中的“原引擎”见 [术语](README.md#术语)。修改字段时同步本文和 KernelChecks 里的尺寸断言（AGENTS.md 第 2 节）。

## 1. 归属原则

| 放在哪里 | 放什么 |
| --- | --- |
| `Battle` | 会随分支变化、而且影响之后合法动作或结算的值。下限是原引擎状态键（`BuildStateKey`、`SimulatedCombatState.AppendFingerprint`）覆盖的内容；原引擎状态键漏掉但影响未来结算的值（例如回合数、Power 的 `AmountOnTurnStart`）也放进来。 |
| `Rules` | 根导入时冻结、整场搜索不变的值：卡牌各升级等级的基础数值与费用、怪物行动的静态参数和编译后的状态机、可生成怪物的闭包、遭遇槽名、根监听前缀顺序、最大能量基础值、手牌上限、药水槽数、根药水（用来区分生成药水和根药水）、战斗内外的随机药水池、卡牌洗牌排序键、角色基础球槽数、固定遗物清单和遗物槽位映射等。`Rules` 是不可变引用类型，所有分支共享。完整字段见 [原生导入](import.md#41-32-个字段)。 |
| `Exec` | 一个动作执行期间的临时值，放在栈上，动作结束前排空，稳定边界上不存在（见第 4 节）。 |
| 都不放 | 纯 UI、日志、显示名、派生启发式值。历史日志只保留会影响结算的计数器。 |

## 2. 总览

容量是编译期常量，所有数组都是 `[InlineArray]`。下表按职责分组，不完全等于内存顺序；完整字段顺序见 `Battle.cs`。

| 区块 | 内容 | 容量 |
| --- | --- | --- |
| 头部标量 | 轮次、玩家回合数、洗牌计数、下一个 CombatId、能量、星能、金币、当前方、玩家阶段、结局、标志位、五个 `ushort` 牌堆张数、手牌上限、各数组的已用数 | — |
| `DetachedCards` | 不在任何牌堆、但之后还要用到的完整卡值（见 3.9） | 3 |
| `DecimalVars` | 精确的 `decimal` 卡牌变量偏移（见 3.10） | 4 |
| `Roster` | 名单：在场敌人在 `Enemies` 里的下标，按原生名单顺序 | 12 |
| `Potions` | 各药水槽 | 5 |
| `Player`、`Osty` | `Creature`：HP、最大 HP、格挡、标志 | 各 1 |
| `Counters` | 整数计数器（见 3.8） | 48 |
| `Rng` | 十条随机流（含角色的 NonCombatGeneration），每条一个计数器加四个 64 位状态字 | 10 |
| `Enemies` | 已知敌人，含已死和已逃跑的（见 3.2） | 12 |
| `Powers` | Power 表（见 3.3） | 28 |
| `RelicStates`、`RelicStateFlags` | 遗物动态整数与本场标志（见 3.4） | 24 |
| `Orbs` | 球（见 3.5） | 10 |
| `Fool` | 箭矢状态、旋律次数、悲伤生成计数与关联卡牌身份 | 32 支箭 |
| `HistoryCourseCard` | 上一次攻击的完整卡值（见 3.11） | 1 |
| `Cards` | 五个牌堆的卡（见 3.6、3.7）；最后一个字段 | 640 |

`HistoryCourseCard` 紧挨在 `Cards` 前面，`Battle.HeaderSize = 2896` 就是 `Cards` 的偏移。头部把五个牌堆张数存成 `ushort`，并用显式填充字节让 `DetachedCards` 之后的 `DecimalVars` 保持 8 字节对齐；头部没有编译期插入的填充。打包大小 `PackedSize = HeaderSize + CardCount × 64`。

## 3. 各部分的含义

### 3.1 头部

**标志位 `BattleFlags`。**

| 位 | 名字 | 含义 |
| --- | --- | --- |
| 0 | `PlayerHooksActive` | 玩家拥有的监听者是否还能触发 Hook（见 [内核语义](combat-semantics.md#2-内容定义)） |
| 1 | `PlayerTurnEndRequested` | 有效果请求了结束玩家回合 |
| 2 | `OstyPresent` | 宠物 Osty 存在（死亡后对象仍在） |
| 3 | `PendingLoss` | 原生战斗已进入失败结算 |
| 4 | `HasRisk` | 根快照带有原引擎未补偿的风险（见下） |
| 5 | `OstyEverPresent` | 原生 `Player.Osty` 已非空（根捕获或搜索中首次召唤后）；只会从 0 变 1 |
| 6 | `HistoryCourseCardPresent` | `HistoryCourseCard` 里有有效卡值 |
| 7 | `TurnStartChoicePauses` | 宿主在可见准备页上回答之后回合的回合开始选择（已释放 selector，原生 setup 在第一个选择处暂停）；只有普通 EndTurn 读它。搜索根按宿主当前的接管模式设置，比对导入沿用预测里的值，一次搜索内不变 |

`HasRisk` 对应原引擎快照的 `SimulationSnapshot.HasRisk`：根导入时读取，预测中遇到未补偿风险时置位，并保持到战斗结束。置位后现任解界限把未来回血当作无限，不再剪枝。Discovery、Splash 虽然记录 `UnresolvedPlayerChoice`，但补偿目录把它们标成已补偿，内核不为它们置位；SeekerStrike 的选择原引擎不补偿，但本内核逐一枚举了这个选择，没有预测不确定性，所以也不置位（只保留路线历史标记）；充能球生成达到 1000 次上限时置位并停止该次生成。

**结局 `Outcome`。** 除胜负外，枚举值 3 是 `EventDefeat`：战损假人限时 Power 走完时的结局，结算见 [内容专属机制](combat-content.md#6-事件内容)。

**其它头部值。** `NextCombatId` 是原引擎的 `_nextCreatureId`，生成敌人时先取当前值再加一。`MaxHandSize` 来自游戏的 `CardPile.MaxCardsInHand`（当前 10）。

### 3.2 生物与敌人

Osty 死亡后保留本体和 DieForYouPower，其它能力按原版死亡清理规则移除；再次召唤时不会带回已移除的力量。

`Player` 和 `Osty` 都是 `Creature`。Osty 只在 `Osty` 区块和 `OstyPresent` 标志里表示，死亡后对象仍在，`Osty.CurrentHp <= 0` 就是死亡；它的 Power 和其它生物一样占 `Powers` 的行，`Owner = Limits.OstyOwner`。`OstyEverPresent` 供“玩家回合开始时宠物计数清零”的语义使用（见 [内容专属机制](combat-content.md#4-亡灵契约师ostydoom-与灵魂)）。

`Enemy` 每项 64 字节：

| 字段 | 含义 |
| --- | --- |
| `Creature`、`MonsterId`、`CombatId` | 生命与格挡、怪物内部 Id、原生 CombatId |
| `Slot` | 遭遇槽下标（`Rules.EncounterSlots` 的顺序）；没有槽名时为 255 |
| `Rules` | 在 `Rules` 怪物规则表里的下标（见下） |
| `DeathPhase` | 存活、复活中、永久死亡等阶段 |
| `Move` | 当前状态机节点 |
| `ForcedMove` | 合成 `STUNNED` 或 `REVIVE_MOVE` 节点的动态后继（原版 `MoveState.FollowUpStateId`）；当前节点不是这两个时恒为 255 |
| `EverUsedMoves`、`RecentMoves`、`LoggedMoves` | 行动日志：曾经用过的节点位集，加最近 8 条；原版的重复规则只读这两部分。冷却或连续次数超出 8 条的状态机在运行时抛 `UnsupportedException` |
| `Ai` | 4 个私有 AI 寄存器，编号由各怪物定义的 `AiRegisterNames` 决定 |
| `PoweredAttackHitsThisTurn` | 本回合玩家对它的强化攻击命中数（BeatIntoShape 的锻造乘数），玩家回合开始清零。16 位：低字节是原字段，高字节用原来的填充字节（旧数据里为 0），超过 65535 显式拒绝；循环保护允许伤害循环打到敌人死，255 次不够 |
| `SteamPhase` | 瀑布巨兽的蒸汽爆发阶段（0 无、1 即将爆发、2 爆发）；其它怪物恒为 0 |
| `Flags` | 见下 |

`EnemyFlags` 里，`PerformedMoveThisRound`、`ForcedMustPerformAfterMove` 和 `ActingThisRound` 只在敌方回合内部使用，任何稳定边界上都为 0。

**名单与已知敌人。** `Enemies` 是已知敌人数组，只追加不删除；`Roster` 是当前在场的名单，按槽下标稳定升序（255 视作 -1，排在有名字的槽位之前），名单顺序就是 Hook 监听和敌方行动的顺序。根上已知的敌人占规则表前 `RootEnemyCount` 项，之后每个可生成的怪物类型一项；因为数组只追加，新敌人的规则编号按怪物 Id 查可生成表（`Rules.SpawnRulesId`）。

敌人是否是“主要敌人”不单独存，和原引擎一样由它持有的 Power 推出（`OwnerIsSecondaryEnemy`）。

**逃跑。** 原版 `CreatureEscaped` 把敌人移出名单。内核同步左移名单，递减后续 owner 在 `Power.Owner`、`Applier`、`Target` 里的下标，移除该敌人的全部 Power，再把它作为 `Escaped | PermanentlyDead` 墓碑追加到已知敌人数组末尾，保留 `MonsterId`、`CombatId`、`Slot`、`Rules`。这样写回历史时仍能补上原生 `CreatureCmd.Add` 加入过的怪物。墓碑不进名单，也没有 Power 引用它。

### 3.3 Power

全场共享 28 个实例槽位，包含玩家、奥斯蒂和所有敌人；叠加层数不额外占槽，独立实例分别占槽。第 29 个实例显式报告容量不足，不截断。文件使用同一份 28 槽运行时布局。

**顺序。** `Powers` 按持有者分组，组的顺序就是 Hook 监听顺序里各生物的顺序；组内按获得顺序排列。新 Power 追加到持有者组的末尾，对应原引擎“插在持有者最后一个 Power 之后”的规则。数量归零的 Power 立即移除并压实，之后再获得时排在组末，对应原引擎退役根槽位的语义。

**实例型 Power。** 原版 `PowerInstanceType.Instanced` 的 Power，每个原生实例占一行，同一 `(owner, id)` 的多行按获得顺序排列。Hook 和阶段遍历都保存出现序号（occurrence），定位到确切的那一行，而不是取 `(owner, id)` 的第一行。实例型的有 Monologue、Nightmare、Swipe、Automation、Panache、RollingBoulder、TheBomb、Gigantification、Orbit、Sandpit、ToricToughness 等。

**字段。** `Amount`、`AmountOnTurnStart`、`Applier`、`Target` 和两个辅助值 `Aux0`、`Aux1`：

- `AmountOnTurnStart` 只为三个会被结算读取的 Power 保存（`DrawCardsNextTurnPower`、`SummonNextTurnPower`、`HelloWorldPower`），其余为 0，原因见 [内核语义](combat-semantics.md#32-派发规则)。
- `Target` 只有原版自己写 `PowerModel.Target` 的 Power 才有值（`byte` owner，`NoOwner` 表示无目标），见 [内核语义](combat-semantics.md#6-格挡与-power)。`SandpitPower` 每个原生 `ApplyTargeted` 实例一行，`Kernel.SetPowerAmountAt` 按行精确减层，避免同 `(owner, id)` 的实例互相覆盖。

辅助值的用法：

| Power | `Amount` | `Aux0` | `Aux1` |
| --- | --- | --- | --- |
| `MonologuePower` | 1 | 施加时从卡牌变量写入的 Strength | 已施加的 Strength |
| `NightmarePower` | 3 | `DetachedCards` 槽位 + 1 | — |
| `SwipePower` | 1 | `DetachedCards` 槽位 + 1（被偷卡） | — |
| `ThieveryPower` | — | 被偷金币账本（`DynamicVars.Gold`） | — |
| `HeistPower` | 被偷金币 | — | — |
| `OrbitPower` | 每次触发给的能量 | `energySpent % 4`（原引擎 `_orbitEnergyRemainders`） | — |
| `PaleBlueDotPower`（非实例型） | — | 本回合锁存位 0/1，玩家侧回合末清零 | — |
| `HellraiserPower` | — | 本回合无限自动打出计数 | — |
| `VoidFormPower` | — | 本回合已打出数 | — |
| `ChainsOfBindingPower` | — | — | 本玩家回合的 `CardAfflictedEntry` 数 |
| `ToricToughnessPower` | 剩余回合 | 格挡清零时提供的格挡量 | — |
| `AutomationPower`、`PanachePower`、`TheBombPower` | — | 沿用原引擎的投影 | 同左 |

**标志 `PowerFlags`。** 跳过下次衰减、刚由敌人施加等。`PendingAmountState`（bit 3）是瞬态标志：本动作里读过这个 Power 的原生 `PowerAmountPredictionState`（目前只有 GigantificationPower 的 `BeforeAttack`）；玩家死亡清理时带这个标志的行保留，`Kernel.Step` 在稳定边界统一清除。

### 3.4 遗物与药水

遗物分成共享清单和分支状态：

- `Rules.Relics` 是不可变的 `RelicRoster`：按原生顺序保存每个实例的 Id、熔化标志、固定值和动态槽映射。重复遗物保留独立下标；输入数组会复制，外部不能修改内部数组。固定清单不占 `Battle` 空间，技术上限为 65,535 件。
- `Battle.RelicStates` 为 24 对 `int Counter/Aux`，共 192 字节；`RelicStateFlags` 为 24 个单字节标志，共 24 字节。映射按清单顺序分配，零值实例也占槽，熔化的有状态实例仍保留其动态值。复制分支只复制这 216 字节。
- `RelicDef.HasCombatState` 声明是否有动态状态；新增会改变私有值的遗物必须声明。PhilosophersStone 的力量值与 PumpkinCandle 的 KindleCount 在战斗中不变，留在共享清单里。
- `Battle.GetRelic(rules, index)` 重建只读 `Relic` 值用于导入、编码和查询；写入通过 `RelicState`、`RelicFlags` 定位动态槽。遗物监听下标为 `ushort`，根处预先整理监听列表。
- 根导入必须验证清单的数量、顺序、Id、熔化标志和固定值全部一致，然后初始化动态槽；超出 24 槽或清单技术上限时显式失败，不截断。

各遗物借用两个动态整数的方式：

| 遗物 | `Counter` | `Aux` |
| --- | --- | --- |
| `MUSIC_BOX` | 本回合已用 | — |
| `THROWING_AXE` | 本场已用 | — |
| `METRONOME` | 已充能次数 | — |
| `PAELS_LEGION` | 冷却 | 本次格挡所属的 `PlayIdentity`；`UsedThisCombat` 标志是原版的显示锁存 |
| `PEN_NIB` | — | 第十次攻击的 `PlayIdentity`（瞬态，见下） |
| `EMOTION_CHIP`、`PAELS_EYE` | `StatefulRelicState.Current` | `Previous` |
| `VAMBRACE` | 本场获得的格挡（`BlockGainedThisCombat`） | — |
| `UNSETTLING_LAMP` | 锁存位 | — |
| `LAVA_LAMP` | 本场是否受过可格挡的未格挡伤害 | — |

`PEN_NIB` 的 `Aux` 只在一次出牌内有意义；`Rules.PenNibRelicIndex` 让 `Kernel.Step` 在每个稳定边界用一次下标写入把它清零，与导入投影的 0 一致。

药水槽存“内部 Id + 1”，0 表示空。药水的使用次数、生成药水的战略成本和免费槽位记在计数器里（见 3.8）。

### 3.5 球

`Orb` 有球 Id 和一个 `Aux`。`Aux` 是球唯一的私有整数：暗球的 `_evokeVal`（初始 6，被动时累加）或玻璃球的 `_passiveVal`（初始 4，被动后递减）；闪电、冰霜、等离子恒为 0。导入时要求它是可表示的整数，未用槽位清零。球的顺序就是原引擎 `OrbQueue.Orbs` 的顺序，`OrbCount`、`OrbCapacity` 与原引擎同义。

### 3.6 卡牌

`Card` 是一张牌的完整值，64 字节。字段集以原引擎的卡牌状态键（`CaptureCardStateFingerprintForTesting`、`CardChoiceSupport.ChoiceCardKey`）为准：

- 卡牌 Id、升级等级、本地关键字位图，以及外部添加/移除关键词的两组位图；
- 费用：基础费用（含 X 费标志）、能量费用修饰栈（本场、本回合、直到打出、临时；容量 10，Stomp 每打出一张攻击累加本回合相对减费）、星能费用；`ModifierCount` 低 5 位保存修饰数量，第 5–6 位保存 `HistoryCourseRole`，第 7 位保存 `IsDupe`；
- 临时星能费用：当前条目 `TemporaryStarCost`，加上最多两个同成本的旧条目（`TemporaryStarCostStack`：低 2 位是旧条目数，之后每个旧条目 2 位清除标志）；回合末或打出后按清除标志逐条移除，成本不同的旧条目按容量拒绝；
- 状态标志 `CardFlags`：下次打出时消耗、本回合狡诈、本回合保留、是否牌组卡、已移出战斗、X 费、X 星费，以及 bit 7 `PowerAfflictionChecked`；
- `ReplayCount`；
- 语义动态变量相对规则表基础值的增量 `Var0`、`Var1`，以及私有值 `Private`（见下）；
- 附魔 Id、数量、私有值，苦难 Id 与数量；
- `DampenOriginalUpgrade`：被 MagiKnight 的 DampenPower 降级前的升级等级加一（0 表示未降级）。`DowngradeCard` 把卡重置到 0 级动态变量、基础费用与本地关键字时写入；最后一个施法者死亡时 `RestoreDampenedCards` 按它逐级恢复。克隆、变化和替换产生的卡清零。
- `HistoryCourseRole`：1 为本回合最后攻击的来源，2 为回合准备期间的上一回合来源，3 为两者都是；随原牌移动，克隆清零。
- `IsDupe`：原版复制品身份，克隆保留，手动和自动打出后都移出战斗。
- `TransientMark`：一次动作内的对象身份标记（见下）。

能量费用修饰 `CostModifier` 占 3 字节：两字节数量，以及一字节标志（低 3 位有效期、bit 3–4 类型、bit 7 只降低费用）。容量为 10 项。相邻、有效期相同且不带“只降低费用”标志的相对修饰按总量合并；绝对设费、不同有效期与“只降低费用”记录保留为合并边界。总量超出 short 时按正向 32767 或负向 -32768 分段，避免截断，并确保导入和清理后的表示一致；总量为零仍保留一条，以保留“存在修饰”的区别。追加时合并尾部，过期清理后重新合并相邻段；导入与旧状态解码使用同一规则。容量按合并后的记录数计算。

**`CostState` 的位。**

| 位 | 含义 |
| --- | --- |
| 0–2 | 当前临时星能费用：存在、回合末清除、打出时清除 |
| 3 | `IsClone`（原版 `CardModel.IsClone`）：由 `CloneCard` 置位，导入时从原版卡读取；克隆卡跳过 `AfterCardEnteredCombat` 里卡牌专属的部分（Stomp 等） |
| 4 | `ReturnToHandNextTurn`：Bolas、ThrummingHatchet 打出后置位，下回合 `BeforeHandDraw` 的回手阶段按牌堆顺序移回手牌并清位；对应原引擎 `_returnToHandNextTurn` 的成员关系 |
| 5–7 | 原版附魔状态 |

设置或清除临时星能费用和附魔状态时，必须保留 bit 3 和 bit 4。

**`PowerAfflictionChecked` 与 `CreatedAfterRoot`。** `CardFlags.PowerAfflictionChecked` 对应原引擎 `PredictedCard.HasCheckedPowerAfflictionEntry`：根导入的卡置位，根之后生成的卡为 0，第一次苦难归一化后置位。`Keywords.CreatedAfterRoot`（bit 7）不是原生关键字，表示不在原引擎 `_liveCardsAtSnapshot` 里的卡，只用于一次性的苦难进场检查。

**`Private` 的用法。** `Private` 是 `short`，按卡牌复用：

| 卡牌 | `Private` 存什么 |
| --- | --- |
| Claw、Rampage、GeneticAlgorithm、TheScythe 等 | 卡牌私有计数 |
| 君王之刃（`SOVEREIGN_BLADE`） | 已应用的 SwordSage 重放加成；`short.MinValue` 表示尚未初始化 |
| Fetch | bit 0 = 这张牌本回合打出过；其余位为 0 |
| `MAD_SCIENCE` | 低字节是原版 `TinkerTimeType`（卡牌类型 1/2/3），高字节是骑手效果（0–9） |
| `MAUL` | `_extraDamageFromMaulPlays`（同时写进 `Var0`，降级后据此恢复） |
| Thrash 等声明 `UsesDecimalDamageVar` 的卡 | `DecimalVars` 槽位 + 1（0 表示没有偏移） |

超出 `short` 范围按 `CapacityException` 拒绝；`ReplayCount` 仍按 `byte` 容量。

**`TransientMark`。** 原引擎拿着 `CardModel` 对象跟踪“同一张牌”，即使它在一次动作里被抽走、洗进牌堆、换了位置。Sts2Solver 的牌没有对象身份，就在动作内给要跟踪的牌写一个标记字节，按标记重新找牌：

- 1..63 归 BeatDown：选中的牌按序号 + 1 标记，`finally` 清零，只清自己的区间。
- 64..255 是共享的动态区间，由栈上的 `Exec.TransientMarkBase` 给嵌套批次分配互不重叠的子区间：Sly 弃牌后的自动打出、`DecisionsDecisions` 的重复自动打出、Mayhem 的自动预打出队列、Scrape 抽到后待弃的牌都从这里取。Scrape 按最大抽牌数预留连续字节，每张实际抽到的牌在 `AfterCardDrawn` 之前拿到 `markStart + 抽牌序号`；同一张牌在本批里被自动打出、洗回抽牌堆后再次抽到时复用已有字节，并按出现次数各处理一次。嵌套批次弃掉一张已带外层标记的牌时，内层在 `finally` 中把外层标记写回，让卡值带着它移动。
- 0 表示未标记。标记在动作结束前清除，稳定边界恒为 0；克隆和生成的卡从 0 开始。
- 同一标记出现两次或标记丢失，抛 `UnsupportedException`；确认牌已离开战斗牌堆时，自动打出走无牌堆分支；区间耗尽抛 `CapacityException`。BeatDown 恰好选中一张已带 Sly 标记的牌时，覆盖后的标记按显式未支持处理。

标记占用卡牌原有的尾部填充字节，不增加卡牌大小。

**身份原则。** 卡牌没有实例身份：牌堆里两张完全相同的打击字节相同，可以互换，路线和选择都按位置记录。原引擎按 `CardModel` 对象记账的值，在这里随卡值移动，不维护全局引用表：

- 移动（牌堆间、回手、洗牌）：整块 `Card` 复制，`Private` 随行。
- 复制、克隆（`CreateClone`、`Clone`、`Duplicate`）：`Kernel.CloneCard` 把 `Private` 重置为未初始化并清掉 Fetch 位，对应原引擎“新对象不在字典里”。SwordSage 的首次归一化对克隆使用当前层数，所以克隆不会重复加层。`MAD_SCIENCE` 和 `MAUL` 例外：原版克隆会复制它们的保存属性，所以克隆也复制 `Private`。
- 生成（`CreateCard`）：`Private` 未初始化、Fetch 位为 0。SwordSage 首次归一化对非克隆使用根层数（`Rules.RootSwordSageAmount`），与原引擎 `NormalizeSwordSageReplays` 的基线一致。
- 变化、替换（`Transform`、`ReplaceCard`）：新卡按生成语义创建，不继承旧卡的账本。
- 消耗、离场（`Exhaust`、`RemoveFromCombat`、战斗结束）：卡值从 `Cards` 移除，相关账本随之消失。原引擎字典里指向已离场对象的残留条目不会被任何结算读取，按惰性放过。
- 分叉：`Battle` 按值复制，`DetachedCards` 一起复制；`Exec` 的瞬态不跨越分叉。

`Rules.HasIdentityContent` 为假（根牌组、Power、遗物和可生成内容都不涉及身份）时，整批身份扫描跳过。它只是优化前提，为真时仍按上面的语义逐项结算。

### 3.7 牌堆

五个牌堆共用一个 `Cards` 数组，按“手牌 | 抽牌堆 | 弃牌堆 | 消耗堆 | 打出区”顺序连续排列，各自记录张数（`ushort`，单堆和总数上限都是 640）。抽牌堆顶是抽牌堆段的第一个元素，与原引擎 `SimCardPile` 的“下标 0 为顶”一致，所以从顶上抽一张入手只需移动两个计数。其它跨堆移动由 `Piles.Move` 在 `[0, CardCount]` 内旋转一次，只搬源位置与目标位置之间的牌；回合末整手弃牌也按同样顺序一次压实。

### 3.8 计数器

`Counters` 是按 `Counter` 编号的 48 个玩家整数计数器，当前正好用满。新计数器只能追加，已有编号不变。

| 分组 | 计数器 | 何时清零 |
| --- | --- | --- |
| 玩家本回合 | 出牌类：`CardsPlayedThisTurn`、`ManualCardsPlayedThisTurn`（自动打出不计）、`CardPlayStartsThisTurn`、`AttackPlayStartsThisTurn`、`AttackSkillStartsThisTurn`、`ZeroCostAttackStartsThisTurn`、`CardPlaySeriesStartedThisTurn`、`AttacksPlayedThisTurn`、`ShivsPlayedThisTurn`、`SkillCardsPlayedThisTurn`、`BlockCardsPlayedThisTurn`；其它：`CardsExhaustedThisTurn`、`CardsDiscardedThisTurn`、`PlayerAttacksThisTurn`、`NonHandDrawsThisTurn`、`StatusCardsDrawnThisTurn`、`EnergySpentThisTurn`；`StarsGainedThisTurn`（本回合获得的正星能，Radiate 读）；`DoomAppliedThisTurn`（DeathsDoor）；`OstyAttacksThisTurn`（Rattle）\* | 玩家回合开始（`BeginSideTurn`），敌方回合保持 |
| 本侧回合 | `SkillPlayedThisSideTurn`（原引擎 `_skillsPlayedThisTurn`，苦难归一化用；不同于 `SkillCardsPlayedThisTurn`） | 本侧回合末 |
| 整场 | 累计失血、回复量、玩家未格挡受伤事件数（TearAsunder）；`FinishedCardPlays`（GoldAxe，含根历史）；`CardsDrawn`（Murder，含根历史）；`LightningChannels`（Voltaic，导入原版历史）；`OrbChannels`（预测期充能次数，对应原引擎 1000 次充能的安全上限）；`EtherealPlays`（BansheesCry、PullFromBelow，含根历史）；`CardsGeneratedThisCombat`（Supermassive，含战斗结束时被拒绝加入的生成卡） | 不清零 |
| 评分用的路线计数 | `LongTermResourceValue`、`AngerCopiesGenerated`、死亡保护回复量（遗物、药水）与次数、`AutomaticPotionUseCount` | 不清零 |
| 药水 | `GeneratedPotionUseCount`：本场生成后又被使用的药水数（“生成”指使用时槽里的药水不是根药水；评分计入用药数）。`GeneratedPotionStrategicCost`：低 16 位是显式用药的战略成本，高 16 位是自动用药（Fairy in a Bottle）的成本。`EntropicFreePotionMask`：5 个槽位的免费位，第 i 位对应槽 i，使用该槽时清除，免费药水成本为 0 | — |
| 偷窃 | `OutstandingStolenGold`（`ThieveryPower` 的金币账本加所有 `HeistPower` 层数）、`OutstandingStolenCards`（每个持有被偷卡的 `SwipePower` 一张） | — |
| 其它 | `VitalSparkNormalizedAmount`（原引擎 `_lastNormalizedVitalSparkAmount`，`int.MinValue` 表示 null）；`DampenCasterMask`（MagiKnight 对玩家施加 DampenPower 的施法者位集，按敌人 owner 下标一位一个） | — |
| 成长 | 5 个打包计数（`GrowthRewards`），每个装两个 16 位来源，十个原版成长来源共用 | — |

\* `OstyAttacksThisTurn` 只在根捕获时原生 `Player.Osty` 存在才于玩家回合开始清零。

生物的本回合失血标记不在计数器里，而是 `CreatureFlags.LostHpThisTurn`，换边或换轮时清除（额外回合的处理见 [内核语义](combat-semantics.md#71-额外回合)）。

### 3.9 `DetachedCards`

不在任何牌堆、但之后还要用到的完整卡值，容量 3：

- `NightmarePower` 选定的牌：下一次 `BeforeHandDraw` 要被克隆 `Amount` 次，是真正的跨回合值。每个 Nightmare 实例一行，`Power.Aux0 = 槽位 + 1`；Power 被移除时清空槽位，避免下一次获得读到旧卡。
- `SwipePower` 偷走的牌（THIEVERY 从抽牌堆或弃牌堆移出时分配），含 `DeckCard`、`RemovedFromState`。击杀飞贼时保留槽位并清除 `RemovedFromState`（写回把牌组卡移到末尾）；飞贼逃跑时保留槽位和 `RemovedFromState`（写回按丢失移除）；仍被 `SwipePower` 引用的槽位是还被扣着的牌。归还或丢失的卡值保留到写回，不在移除 `SwipePower` 时立即释放。

同一场战斗里 ThievingHopper 只有一个、THIEVERY 只发生一次，所以 Nightmare 的两个实例加一张被偷卡正好够用。第 4 张并存的卡按 `CapacityException` 丢弃该子状态，不覆盖、不逐出。

卡 Id 0 是有效内容（例如 Accuracy），所以槽位占用不能只检查 `Id != 0`。`Card.IsDetachedCardPresent` 用 `Id != 0` 或 `CardFlags.PowerAfflictionChecked` 判断占用：所有进入 detached 槽位的卡都带这个一次性检查位（导入卡和 `CloneCard` 快照都经 `AllocateDetachedCard` 补齐），未用槽位保持全零。`BattleReconciler.Normalize` 只清除 Id 非零槽位的该标志，保留 Id 0 卡的占用身份。

### 3.10 小数卡牌变量

原引擎的 Thrash 把 `ModifyDamage` 的 `decimal` 结果同时加进 `DynamicVars.Damage.BaseValue` 和私有的 `ExtraDamage`，降级后再加回，克隆按模型字段复制。Sts2Solver 在头部 `DecimalVars` 保存“当前 `BaseValue` 减规则表规范值”的精确 `decimal`，卡上的 `Private` 存槽位 + 1。

- `DecimalVars.Assign` 只在值变化时移动槽位；值相同的卡共享槽位，更新一张不会改写另一张。
- 每个稳定边界按“`DetachedCards` → `HistoryCourseCard`（仅当存在）→ 牌区存储顺序”重建槽位，并对相同的位模式去重，所以字节表示与分配历史无关。导入投影和内核走同一个函数。
- 槽位不足抛 `CapacityException`；没有声明 `UsesDecimalDamageVar` 的卡收到小数偏移，显式抛 `UnsupportedException`。
- Thrash 的伤害读取走同一个 `decimal`（`CardEffects.Var`），`Scorer` 也先按 `decimal` 相加再转 `double`，与原引擎 `CardChoiceSupport.CardValue` 的取整点一致。

### 3.11 `HistoryCourseCard`

玩家当前回合最后完成的非复制攻击牌的值，有效与否看 `HistoryCourseCardPresent`。牌堆中的原牌通过 `HistoryCourseRole` 标记，动作完成时刷新槽值；槽里的副本不带来源或临时身份标记。

出牌阶段导入本回合记录，回合准备 `Start` 根导入上一玩家回合记录。执行准备阶段时，`Exec.PreviousHistoryCourseCard` 保存上一回合的后备值，原牌暂带上一回合标记；自动出牌前再次读取原牌最新值。本回合准备期间新打出的攻击写入当前槽，两个回合不会相互覆盖。结算见[内容专属机制](combat-content.md#72-history_course-与-pen-nib)。

## 4. 不进入 `Battle` 的值

### 4.1 规则表里的根级字段

`Rules` 里有一些按根求值、整场不变的布尔或下标，用来让热路径在根不含相关内容时直接跳过：`HasIdentityContent`、`HasDecimalCardVars`、`HasTheftContent`、`HasHistoryCourse`、`HasPaelsLegion`（`GainBlock` 是否需要收集变化监听者列表）、`FurCoatSpawnHpOne`（命中的生成敌人当前生命设为 1）、`RootLostHpMask`（根捕获时每个 owner 的“本回合已失血”，额外回合恢复用）、`PenNibRelicIndex`、`RootSwordSageAmount` 等。怪物规则里的 `MonsterRules.InitialMoveLogged` 表示状态机构造器已经把初始行动记进日志，敌方回合内生成的召唤物要在生成时立即记录该行动。完整列表见 [原生导入](import.md#41-32-个字段)。

生成候选池、随机药水池、按原卡展开的变化池也只在 `Rules` 里。

### 4.2 动作期间的瞬态（`Exec`）

下面这些值只在一个动作执行期间存在，放在栈上的 `Exec` 或相关结构里，稳定边界前排空，不进入状态字节、指纹或续用：

| 值 | 用途 |
| --- | --- |
| `PlayIdentity`、`LastPlayIdentity`、`PlayIdentityCounter`、`MusicBoxPlayIdentity` | 每次原生 `CardPlay` 一个单调编号，供 PAELS_LEGION、MUSIC_BOX、PEN_NIB 把同一次出牌的前后回调配对；嵌套自动打出保存并恢复 |
| `CardPlayPairs`（容量 `Limits.CardPlayPairs` = 32） | 出牌期的金额快照：Afterimage、Strangle 在 `BeforeCardPlayed` 记下层数、`AfterCardPlayed` 消费（原引擎 `CardPlayPairPredictionState`）；Oblivion 的当次层数（`OblivionDoom`）。嵌套自动打出的条目压在外层之上，先被消费 |
| `MonologuePending`（容量 8） | 每次出牌开始时快照所有正层 Monologue 的 `(owner, occurrence, Strength)`，效果结束后逐条结算 |
| Vambrace、UnsettlingLamp 的触发卡来源序号与位置 | 一次出牌内的触发卡身份；UnsettlingLamp 另存本次已翻倍的临时 Power 内部 Id 集合 |
| `PendingPowerChanges`（容量 64） | 待派发的 Power 数量变化队列（见 [内核语义](combat-semantics.md#32-派发规则)）；原引擎同样要求分叉前 `_pendingPowerAmountChanges` 为空 |
| `TransientMarkBase` | `TransientMark` 动态区间的分配游标 |
| `CardDamageTotal`、`LastDamageTotal`、`LastDamageOverkill` | 本次出牌全部命中的伤害合计，以及最近一次伤害的结果（Fisticuffs、Omnislice 读；对应原引擎 `CombatPredictionDamageReceivedEntry.Result`）；每次出牌开始清零 |
| `AttackProps`、`AttackHasCardSource` | 当前攻击上下文的伤害属性和卡来源事实（`AttackCommand.DamageProps`/`ModelSource is CardModel`），供 `AfterAttack` 监听者读取 |
| `AttackFirstResultSeen`、`AttackFirstResultUnblocked`、`DamageCallDepth` | 当前攻击上下文内每个敌人的第一条伤害结果及是否未格挡；`DamageCallDepth` 区分攻击自身的伤害与结果钩子里的嵌套伤害。SkittishPower 的 `AfterAttack` 读取 |
| `CurrentPlayIsHistoryCourseDupe` | 当前打出的牌带有原版 IsDupe 身份，包括复制品再生成的克隆 |
| `PreviousHistoryCourseCard`、`HasPreviousHistoryCourseCard` | 回合准备开始时保存的上一回合攻击；包含在结束回合的执行上下文快照中 |
| `StarValue` | 本次出牌的星能 X 值（原版 `LastStarsSpent`） |
| `BrightestFlameMaxHpSpent` | 本动作中至亮之焰请求消耗的最大生命合计：按每次打出（含自动打出和重放）卡牌的动态 `MaxHp` 累计；动作结束后由搜索读取，不进入 `Battle` |
| `ChoiceRequest.GeneratedOptions` | 不在牌堆里的选项卡值：Discovery、Splash 的生成选项，Knowledge Demon 的两张诅咒；需要选择时整个动作从父状态重执行 |

伤害来源直接放在栈上的 `DamageResult` 里。搜索容器（候选池、路线节点）不属于状态布局，见 [战斗搜索](combat-ai.md#7-内存与容量)。至亮之焰的路线累计消耗也只是搜索节点账本：只有请求设了上限才按路线节点保存，不进入 `Battle`、不进入状态指纹。

### 4.3 原引擎按对象记账的值

原引擎用 `CardModel`、`PredictedCard` 对象做键的账本，在 Sts2Solver 里的表示：

| 原引擎 | Sts2Solver 表示 | 稳定边界语义 |
| --- | --- | --- |
| `MonologuePower.amountsForPlayedCards`（`Dictionary<CardModel,int>`） | 不进入状态，用 `Exec.MonologuePending` | `BeforeCardPlayed` 登记、`AfterCardPlayed` 删除，只在一次出牌内非空；原引擎的 `CapturePendingMonologues` 也只在出牌期间读取 |
| `_swordSageReplayBonuses`（`Dictionary<PredictedCard,int>`） | 每张君王之刃的 `Private` | 随卡值移动；`NormalizeSwordSageReplays` 按“当前 SwordSage 层数 − 已应用”调整 `ReplayCount` 并写回 |
| `SummonForth` 的 `MoveSovereignBladesToHand`（按对象取非手牌君王之刃） | 先按卡牌 Id 快照抽牌、弃牌、消耗、打出段里的君王之刃，再逐张移到手牌底部（满手改送弃牌堆） | 只依赖牌值；快照避免把刚送入弃牌堆的牌再捡回来 |
| `VambracePredictionState.TriggeringCard` | 不进入状态；`Exec` 保存触发卡所在出牌的来源序号与相对位置 | `BlockGainedThisCombat` 持久化到 `Relic.Counter` |
| `_unsettlingLampTriggeringCards`、`_unsettlingLampInternalPowerTypes` | 不进入状态；`Exec` 保存触发卡来源序号与已翻倍的临时 Power 内部 Id | 完成施加时清除；锁存位持久化到 `Relic.Counter` |
| Fetch 的 `_fetchCardsPlayedThisTurn` | `Private` 的 bit 0 | 随卡值移动；打出时置位，玩家回合开始清零 |
| `_nightmareSelections`（`Dictionary<NightmarePower,PredictedCard>`） | `DetachedCards` | 见 3.9 |
| `SwipePower.StolenCard` | `DetachedCards` | 见 3.9 |

**导入时的投影。**

- SwordSage：已应用值取 `_swordSageReplayBonuses[card]`；没有条目时按根层数（克隆取当前层数）推导；有正层 Power 却没有条目，拒绝。
- Fetch：`_fetchCardsPlayedThisTurn` 的成员置位；只对当前牌堆里的卡有意义，离场成员不投影。
- Orbit、PaleBlueDot、Nightmare：读 `_orbitEnergyRemainders`、`_paleBlueDotActivated`、`_nightmareSelections`；正层实例缺条目时拒绝（原引擎读取时会抛异常）。退役实例（`Amount == 0`）的残留条目按惰性放过，依据与原引擎 `PowerAmountPredictionState` 相同：读取点只遍历正层有效 Power，重新获得是新的原生实例。
- Vambrace、UnsettlingLamp：持久部分投影到 `Relic.Counter`；触发卡和临时类型集合在稳定边界必须为空。
- Monologue：内部字典必须为空，否则按 `UnrepresentableState` 拒绝（与 `CardPlayPairPredictionState` 同样的稳定边界前提）。

`Exec.MonologuePending` 容量 8，更多同时等候结算的 Monologue 实例按 `CapacityException` 回落。所有退役条目只按惰性放过，不写回、不清理活实例。

### 4.4 回合准备根

回合准备根在导入阶段就已经回放到出牌阶段，导入的只是那个出牌阶段的 `Battle`。准备选择本身不进入状态字节、指纹或续用；各准备根之间的差异（手牌、弃牌堆、Power、能量、HP 等）全部已经在导入后的 `Battle` 里。导入方式见 [原生导入](import.md#81-回合准备根)。

## 5. 规范化

同一局面必须只有一种字节表示，否则去重失效；不同局面绝不能字节相同。

- `Enemies`、`Powers`、`RelicStates`、`RelicStateFlags`、`Orbs`、`DetachedCards`、`DecimalVars` 的未用槽位和填充字节全部为零。牌堆移除元素时只在前 `CardCount` 张内前移，不要求清空旧的尾格；`Cards` 下标不小于 `CardCount` 的内容不参与规范化、比较、编码或任何读取。
- 牌堆保持真实顺序，不排序。原引擎在部分牌堆上忽略顺序（`PileOrderInvariantMask`），Sts2Solver 目前不做这种合并：只会少合并，不会错误合并。
- 布尔值只用 0 和 1。
- 结构体用 `StructLayout(LayoutKind.Sequential)`，并由检查断言 `Unsafe.SizeOf<Battle>()` 等于预期，防止字段改动悄悄引入填充。

## 6. 指纹与相等

- 指纹是 64 位哈希，只覆盖打包前缀里各区块已用的部分（头部、前 `EnemyCount` 个敌人、前 `PowerCount` 个 Power、前 `CardCount` 张牌等），四路并行混合。`HistoryCourseCard` 在内存里位于头部末尾，但最后才混入。
- 固定清单不参与分支指纹；状态比较、去重和重放必须使用同一份或等价的共享清单。跨请求比较先比较清单，再比较动态状态。
- 相等比较打包前缀（`Fingerprint.Bytes`），唯一入口是 `Fingerprint.Equal`。搜索去重在指纹命中后必须再比较打包前缀，不允许只凭哈希合并；后缀的垃圾字节不参与比较。

和原引擎状态键相比，有几处有意的差别，都只会让合并变少，不会把不同局面误合并：

- SwordSage：原引擎键里没有每张牌的已应用值，但“相同 `ReplayCount`、不同已应用值”在之后层数变化时会产生不同结果。Sts2Solver 把它放进卡值，因此会区分原引擎会合并的这类状态。这是有意的正确性修正。
- Fetch：原引擎键把打出过的 Fetch 按 `_registeredCombatCards` 下标规范化；Sts2Solver 用卡上的位，等价于按实例区分。两张没打出的同值 Fetch 仍然字节相同、可以合并；打出其中一张后两张卡值不同，正好对应“哪一张打出过”。
- Orbit、PaleBlueDot：原引擎键是无序集合；Sts2Solver 的 Power 行按获得顺序，同 `(owner, id)` 的多个实例互换时会少合并。Nightmare 选定卡在原引擎键里按 `ChoiceCardKey` 归一化，Sts2Solver 用完整卡值，更保守。
- Monologue、Vambrace、UnsettlingLamp 的瞬态不进入字节，与原引擎键一致。

## 7. 容量与失败

- 写入超过容量时抛 `CapacityException`，带区块名和容量。在搜索展开中，这只丢掉出错的那一个子状态（同一选择的其它选项分支不受影响），按区块名计入 `BeamResult.CapacityDroppedChildren`，其余子状态照常提交和去重；不截断、不覆盖、不逐出。
- 根状态本身超容量（根导入或根评分时、展开循环之前抛出），整次求解作废，Mod 记录 `SEARCH_FAILED` 并停止部署，不部分提交。
- `UnsupportedException`（内核不支持的语义）不管出现在哪一步，都让整次求解作废。
- 数值钳制与原引擎一致：HP、格挡、能量、Power 数量钳制在 ±999,999,999，由内核统一实现。

搜索池的容量与内存账本见 [战斗搜索](combat-ai.md#7-内存与容量)。

## 8. 尺寸

| 结构 | 字节 | 说明 |
| --- | ---: | --- |
| `Card` | 64 | 费用修饰容量 10，每项 3 字节；外部关键词记录；`FoolIdentity`；`DampenOriginalUpgrade`；`TransientMark` |
| `Enemy` | 64 | `Rules`、`PoweredAttackHitsThisTurn`、`SteamPhase` 各占原填充的一个字节，最后一个字节仍是填充 |
| `Power` | 24 | — |
| `RelicState` | 8 | `Counter` + `Aux`；标志另存为每槽 1 字节 |
| `Orb` | 8 | `Id` + `Aux` |
| `RngState` | 40 | 计数器 + 四个 64 位状态字 |
| `Battle` | 43,856 | 头部 2,896 + 满牌 640 × 64；打包前缀和满容量结构体尺寸相同 |

典型的 23 张牌状态打包后约 4.3 KiB；30 张牌为 4,816 字节。

### 大结构体与 JIT 限制

`Battle` 的 43,856 字节低于 CLR 的数组元素上限 65,535，所以 `Battle[]`、`List<Battle>`、`Dictionary<..., Battle>` 这类容器可以正常使用。超过 65,535 字节的结构体不能作为数组元素，第一次 JIT 到创建该数组的代码时会抛 `TypeLoadException`，错误是“base value type is too large”。以后若要再增大 `Battle`，先检查这个上限。

C# 元组（`ValueTuple`）另有一条更低的限制：JIT 计算元组大小时把每个字段按 8 字节对齐，总大小达到 128 KiB（131,072 字节）时，第一次 JIT 就抛 `InvalidProgramException`。`Battle` 本身刚好在安全范围内：两个 `Battle` 的元组是 87,712 字节，可以编译；三个 `Battle` 的元组是 131,568 字节，不能编译。不要在元组、`ValueTuple` 或由元组承载的返回值里放多个大结构体。普通结构体的局部变量、按值参数、返回值和 async 状态机字段没有这条 128 KiB 限制：43,856 字节的 `Battle` 可以按值放进这些位置；实测更大的普通结构体（约 294 KiB 的局部变量、两个约 98 KiB 的 async 状态机字段）也能编译。但多个 `Battle` 按值复制会放大栈帧和复制成本，热路径和异步路径仍优先用 `in`、引用类型或把大局部量拆进单独方法。

这类错误只看方法是否被 JIT 编译，与牌组大小无关：一条很少执行的分支第一次运行就可能让整个方法报 `InvalidProgramException`。测试版 Mod 启动时由 `JitChecks` 对 Mod/Core 程序集（测试构建里是同一个 `Sts2Solver.dll`）的所有非泛型方法调用 `RuntimeHelpers.PrepareMethod`，包括 async 状态机的 `MoveNext`；泛型方法因缺少闭合类型参数跳过。任何 `InvalidProgramException` 或 `TypeLoadException` 都会列出方法名并让启动检查失败。

卡牌的 `CapturedEnergy` 保存 X 费用最近捕获的能量值，参与同名牌排序。洗牌从所用随机流取一个种子，再用临时随机流完成打乱；主流只推进一次。

### 5.1 愚者状态与调用尺寸

卡牌末尾两个字节记录外部添加、移除的关键词，降级时与未升级的规范关键词合成；升级本身不会改写这两组记录。

`FoolState` 在 `HistoryCourseCard` 之前，占 120 字节。32 支箭各用状态位和链表下标表示；`ImpaledPower` 引用箭链，离开敌人时恢复或销毁对应箭。旋律记录分别保存开始次数和完成次数，避免一次多重旋律与重复打出混淆。`FirstPlay`、`AttackSerial`、`ActiveAttack` 只用于动作内关联，在稳定边界清零。

卡牌的 `FoolIdentity` 为 16 位，用于瞄准和炫技能力保留的原牌引用。稳定边界按牌堆顺序重编号；没有能力引用的身份清零，已脱离牌堆的引用以 -1 表示。生成牌的瞬态身份属于动作内嵌套批次，与这里的跨动作身份分开。

CLR 还限制单次调用的参数总尺寸。一个满容量 `Battle` 加上其它按值参数可能超过 65,535 字节，即使被调用方法本身可以 JIT，调用者仍可能抛 `InvalidProgramException`。搜索入口通过引用类型会话传递根状态；新增调用用 `in Battle` 或已有状态容器，并运行实机 JIT 检查。

模式、楼层和领域规则属于根常量：`Rules.Mode` 冻结标准、恐惧或极速；`TotalFloor` 提供塔罗牌的楼层数；`Dominion` 冻结领域类型及召唤后施加的能力。它们均进入规则编码，不能从搜索线程读取原生全局游戏模式。

BATL v10 直接保存当前运行时打包前缀：头部 2896 字节，加每张活动牌 64 字节；最大前缀为 43856 字节。卡牌指纹直接读取当前 Card 字节。

前缀后保存 `ushort` 遗物件数和完整的固定清单，每行 12 字节；动态状态只在 Battle 的 24 槽里保存一次。清单上限为 65535 件。解码返回 Battle 和清单；SREQ 为所有根绑定同一个等价清单。RULE 只编码内容规则表，清单由 BATL 携带，不在 RULE 中重复保存。只接受当前格式，旧布局不提供兼容读取。
