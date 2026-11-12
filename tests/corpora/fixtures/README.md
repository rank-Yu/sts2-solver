# 冻结 fixture 输入

本目录是 `tests/Golden/data/d1` 冻结根以及长期差分、场景回归的建局输入，供差分宿主（`tests/Differential`）重建录制时的原生战斗。

## 不属于 D1 冻结根的输入

- `clone-289-axebots.json`：原质量语料里的大牌组回归输入，33 张原牌组加 256 张升级版 `NEUTRALIZE`（带 `CLONE` 附魔）的 289 张牌组，固定第三幕普通战斗 Axebots，用于 640 容量与 `IndexSelection` 的长期实机回归；不进入质量评测。
- `tw-throwing-axe.json`：最小 Ironclad 开局，只注入未使用的 `THROWING_AXE`，用于覆盖生产请求的 `PreserveReplayAllocatorOpening`。
- `tw-queen-setup-stages.json`：1 点生命的 Queen 开局并带一瓶药水；第一遍搜索以失败路线结束后，生产协调器会走到用药阶段，覆盖 `Setup`、`ContinuousSetup` 与 `QueenSearchPolicy` 成员。

## 历史课回归输入

下列输入由 sts2-sim 固定种子整局导出，再加入历史课，用原版逐动作对照确认。它们属于场景回归，不是 D1 冻结根。

- `hc-fetch-record`：Fetch 打出后更新原牌的出牌记录。
- `hc-cost-refresh`、`hc-sovereign-blade`：后续动作改变已记录攻击牌的费用或伤害。
- `hc-early-attack`、`hc-early-stomp`：回合准备期间先打出攻击，历史课仍复制上一回合的攻击，并保留本回合记录。
- `hc-adaptive-strike`：历史课复制适应打击，生成牌保留复制品身份，后续打出时移出战斗。
- `hc-slumber-imbalanced`：失衡传播到沉睡甲虫后，醒来行动仍需执行。

## 来源

- 源仓库：CombatSolver，源提交 `a62f1850`
- 源路径：`coverage/fixtures/cards/flash-<root>.json`
- 复制目标：`tests/corpora/fixtures/<root>.json`，文件名去掉 `flash-` 前缀；`<root>` 与 `tests/Golden/data/d1/<root>` 一一对应。

文件内容**逐字节未改**，复制后已用 SHA-256 与源文件逐个比对。每个根的源路径记在 `tests/Golden/data/d1/<root>/meta.json` 的 `input.fixture` 字段里。

`specs/flash-qf-misery-roster.spec.json` 也是从 `coverage/fixtures/cards/specs/` 原样复制的。`qf-misery-roster` 是唯一一个带 `generatedScenarioPath` 的 fixture，它的 JSON 按原字节指向这个规格，差分宿主按文件名在本目录里解析。

## 特殊的 fixture

- 13 个 fixture 带 `flashPrefixActions` 或 `flashPrefixPlan`：先建前缀之前的原生状态，再用内核回放前缀动作。
- `ts-a-toasty`、`ts-b-toolbox`、`qf2-gradient-failure` 除了普通根，还会产生回合准备根。
- 以下测试专用 fixture 不对应冻结根：
  - `ts-d-autopreplay-choice`：第 2 回合由 Mayhem 自动打出 Discovery，覆盖 AutoPrePlay 里的选择；
  - `ts-g-multi-choice`：第 2 回合 `AfterPlayerTurnStart` 两次选择（暴政 + 烘焙手套），并带熔炉的 `AfterSideTurnStart`；
  - `ts-h-choice-then-listener`：第 1 回合烘焙手套之后还有宝石面具和十字弓，覆盖同一轮选择后仍有监听器；
  - `ts-i-plasma-pause`：第 2 回合等离子球与烘焙手套，覆盖准备暂停处的球触发；
  - `ts-j-crossbow-one-card` / `ts-j-crossbow-many`：烘焙手套 + 十字弓，覆盖暂停后由侧回合开始加入手牌的攻击牌（手牌 1 张和多张两种）；
  - `ts-k-two-relic-choices`：烘焙手套 + 赌博筹码，同一准备阶段产生两段可见选择；
  - `ts-l-turn-one-heal` / `-energy` / `-regent` / `-defect`：第 1 回合烘焙手套加只在第 1 回合触发的遗物（鲜血瓶、灯笼、节庆礼炮、准备背包、热可可、面包、击剑手册、注能核心等），覆盖准备根重放里的这些效果；
  - `ts-m-hatchet-return`：`ts-a-toasty` 加 10 张嗡鸣手斧，供场景 `turn_setup_continuation_hatchet` 使用，覆盖上回合打出的回手牌在下一回合 Start 根里的导入；
  - `ts-ex-extra-turn`：PaelsEye + 烘焙手套，第 1 回合能量 0 且只抽防御牌，结束回合必触发额外回合，覆盖额外回合开始的准备选择。

sts2-sim 整局导出批次（FS1）的归类 fixture 同样是测试专用，不对应 d1 冻结根：

- `fs1-multi-hit-dagger-spray`、`fs1-multi-hit-volley`、`fs1-multi-hit-stardust-combat-end`：多段攻击的段数没有完整结算；
- `fs1-skittish-osty`：Osty 攻击没有触发 Phantasmal Gardener 的 Skittish；
- `fs1-the-boot-osty`：The Boot 没有把 Osty 攻击的最低伤害抬到 5；
- `fs1-waterfall-steam-death`：Waterfall Giant 在蒸汽爆发状态下死亡时，原版与内核的 `SteamPhase` 不同；
- `fs1-lagavulin-osty-plating`：Osty 攻击对 Lagavulin Matriarch 的伤害/格挡结算不同；
- `fs1-import-unsupported-kaiser`：抽牌钩子移动过的牌无法按原位置弃置，导入明确拒绝；Scrape 按抽牌身份弃置后已转 same。
- `df1-gold-hooks`：致命 HAND_OF_GREED 加 BOWLER_HAT 与 DRAGON_FRUIT，覆盖战斗内得金币的修正与 `AfterGoldGained`（金币 25、最大生命与当前生命各 +1）；修复前对拍在金币和生命上不一致。
- `kc1-scrape-sly-autoplay`：Scrape 抽到 Strike（Hellraiser 在 `AfterCardDrawn` 自动打出）、Sly 的 Reflex（弃置后自动打出并再抽两张）和两张 Defend，覆盖抽牌钩子移走牌后的按身份弃置。

FS1B 批次由 FS2B 修好的 Osty/Doom/Power 类 fixture：

- `fs1b-osty-summon-flags`：搜索中召唤 Osty 后同步原生 `Player.Osty` 的“已存在”标志，后续玩家回合开始会清零 Osty 的攻击计数；
- `fs1b-power-order-footwork`：Monologue 的待结算 Strength 在卡牌效果之后结算，力量/敏捷按原版获得顺序进入 Power 表；
- `fs1b-power-count-missing`：PhilosophersStone 的 `AfterCreatureAddedToCombat` 力量在新敌人获得 MinionPower 之前施加；
- `fs1b-power-count-extra`：Tunneler 的 BurrowedPower 在 `AfterBlockBroken` 立即眩晕并移除，覆盖玩家攻击与反伤造成的破甲；
- `fs1b-blight-strike-doom`：BlightStrike 在敌人死亡 Power 结算前保存本次攻击的伤害，Doom 数量按原版攻击结果施加；
- `fs1b-doom-counter`：死亡的敌人不再接收 Power（有 Osty 时原版 `DieForYouPower` 阻止），DoomAppliedThisTurn 不再记录未实际施加的 Doom；
- `fs1b-danse-macabre-block`：DanseMacabrePower 每次出牌读取当前解析费用，重放中已过期的费用修饰不会再次触发格挡；
- `fs1b-sovereign-blade-private`：闭环生成的 Sovereign Blade 进入战斗时初始化 identity ledger，不再保留未初始化哨兵；
- `fs1b-souls-power-exhaust`：卡牌被降级重置本地关键字后重新执行 SoulsPower 的移除 Exhaust；
- `fs1b-hellraiser-aux`：Hellraiser 的 HittableEnemies 排除已死敌人，并识别 HardenedShellPower 的无限生命显示。

FS1B 批次修好的遗物/怪物/卡牌类 fixture（FS2C）：

- `fs1b-spiral-razor-block`、`fs1b-razor-tooth-genetic-algorithm`：Spiral 的重复出牌下标和 Razor Tooth 的升级时机；
- `fs1b-gremlin-horn-respawn`、`fs1b-axebot-respawn-power`：Axebot 重生期间 Stock 阻止战斗提前结束，Philosophers Stone 在新增敌人入场后施加力量；
- `fs1b-fabricator-enemy-count`、`fs1b-enemy-count-removal`、`fs1b-phrog-enemy-count`：导入保留已死亡召唤物的已知敌人顺序；
- `fs1b-knights-hexed-hand`、`fs1b-knights-modifier-count`：Hexed 卡牌的全局 Ethereal 查询，以及 Stomp 监听者在出牌区挪动后的身份定位；
- `fs1b-guiding-star-draw`：Dampen 恢复后同步本张出牌的数值；
- `fs1b-kusarigama-terminal`：战斗在玩家回合第一阶段结束时不再重置 Kusarigama 计数；
- `fs1b-cards-played-counter`：Eidolon 对 Hexed Ethereal 牌的全局关键字查询；
- `fs1b-rng-divergence`：TrashToTreasure 每层先消耗随机球再尝试充能；
- `fs1b-waterfall-power-count`：Null 的击杀/能力顺序与蒸汽死亡后的 Poison 清理；
- `fs1b-nunchaku-energy`：Hexed 的 Veilpiercer 费用判断，以及 Misery 克隆临时 EnfeeblingTouch 时先执行内部负力量再插入临时 Power 行。


FS2D 修复批次的归类 fixture 同样是测试专用，不对应 d1 冻结根：

- `fs2d-end-turn-ethereal-moved`：回合末以太牌被先结算的以太牌/弃牌钩子移到其他牌堆后，仍按原牌对象找到并耗尽；
- `fs2d-selected-card-moved`：同一回合末流程中，先结算的以太牌把另一张已选中的以太牌移出原牌堆；
- `fs2d-autoplay-pile-order`：Cascade/Imbued 自动打出时，前一张自动打出的牌移动了尚未打出的排队牌；
- `fs2d-known-enemy-revive`：Test Subject 在回合准备自动打出后进入 RESPAWN_MOVE，根仍以 AdaptablePower 保留死亡行；
- `fs2d-thrash-squeeze`：Thrash 随机选中 SQUEEZE，读取其 CalculatedDamage 而不是 Damage/OstyDamage；
- `fs2d-orb-channel-cap`：预测中的充能球生成达到 1000 次后记录 risk 并停止本次生成，不再拒绝整局。

FS5 批次修好的对照 fixture：

- `fs5-enchant-downgrade-keywords`：MagiKnight 的 Dampen 降级带 TezcatarasEmber/RoyallyApproved 的牌后，附魔 `OnEnchant` 重新加上 Eternal、Innate 与 Retain（并恢复 TezcatarasEmber 的零费）；
- `fs5-expertise-bound-retain`：ChainsOfBinding 在 `AfterCardDrawn` 给抽到的牌加 Bound 后，Expertise 仍按身份给实际抽到的牌加 `RetainThisTurn`；
- `fs5-expose-block-break`：Expose 按 `CreatureCmd.LoseBlock` 清空格挡并派发 `AfterBlockBroken`，Tunneler 的 BurrowedPower 立即移除并眩晕；
- `fs5-suck-osty-hit`：DieForYou 把 FossilStalker 的攻击重定向给 Osty 时，SuckPower 按原生命中列表只计一次，不把宠物结果和玩家溢出结果重复计为两次；
- `fs5-pale-blue-dot-autoplay`：PaleBlueDotPower 在正常 `AfterCardPlayed` 阶段、把 DecisionsDecisions 的嵌套自动打出计入本回合第 5 张出牌；
- `fs5-nested-autoplay-star`：嵌套自动打出返回后恢复外层的 `StarValue`，BlackHolePower 按外层出牌实际消耗的星能触发；
- `fs5-kusarigama-death-trigger`：Kusarigama 的第 3 次攻击伤害后立即结算敌人死亡效果，PossessStrength 的返还影响下一次重放伤害；
- `fs5-happy-flower-terminal`：战斗在敌人回合结束时，下一次玩家侧回合开始的 HappyFlower 不推进计数；
- `fs5-unset-move-summon`：敌人在 FABRICATE_MOVE 召唤的 bot 以 `MoveStateMachine` 当前首次行动导入 `UNSET_MOVE`，后续行动与原版一致。
- `fs5-postlethal-monster-effect`：玩家 1 点生命、Fabricator 强制 `FABRICATING_STRIKE_MOVE`，校验致命攻击后同一行动仍执行后续召唤（aggro bot）；致命后按原版结束门控跳过的 Power 施加不计入终局，终局为 defeat、1 动作/1 回合。

FS6 批次修好的对照 fixture（均为原 FS6 请求，只改 `scenarioId`）：

- `fs6-suck-hit-list`：SuckPower 按原生 `AttackCommand.Results` 的每个命中列表计数；Osty 代受的命中保留宠物结果、移除同一列表里的玩家结果，因此整次命中只算一次（FossilStalker 的 LATCH 打中 Osty 并溢出给玩家时仍加一次力量）；
- `fs6-uproar-post-attack-death`：Uproar 的两段攻击后先结算敌人死亡 Power，再读取抽牌堆自动打出下一张攻击；击杀 ShrinkerBeetle 后 ShrinkPower 已移除，后续攻击按未缩小伤害计算；
- `fs6-corrosive-wave-card-source`：CorrosiveWavePower 的 Poison 以 `cardSource: null` 施加，UnsettlingLamp 不得观察或翻倍这些 Poison；
- `fs6-memento-mori-thrash`：Thrash 读取 Memento Mori 的 CalculatedDamage（CalculationBase + ExtraDamage × 本回合弃牌数）作为附加伤害，不再按缺失 Damage 变量拒绝；
- `fs6-crossbow-tools-order`：ToolsOfTheTradePower 的弃牌选择在读取手牌前先运行原生回合准备暂停，暂停期间 Crossbow 的 AfterSideTurnStart 生成的攻击牌进入候选；
- `fs6-terminal-turn-end-hooks`：玩家回合末 `Hook.AfterSideTurnEnd` 在进入阶段二时是否已结束决定整次监听器迭代是否运行；ArtOfWar 在阶段二入口已结束时保持本回合攻击标记；
- `fs6-kusarigama-turn-end-reset`：同一阶段二入口未结束时 Kusarigama 的 AfterSideTurnEnd 即使迭代过程中战斗才进入结束也照常清零；
- `fs6-terminal-pendulum-ending`：战斗在回合准备阶段已进入结束时，`ModifyHandDraw` 监听器为空，Pendulum 不推进 TurnsSeen；
- `fs6-detached-card-id-zero`：牌 Id 0 是有效内容；DetachedCards 的占用标记不再只靠 Id 判断，Nightmare 选中的 Accuracy 正确保留在 detached 槽位；
- `fs6-terminal-action-queue`：原版战斗结束清理会取消排队中的 Godot GameAction；离线差分宿主在终局前缀捕获后不再驱动该队列，避免无 Godot 的 `CallDeferred`/`PopAction` 崩溃。

这些 fixture 不在 d1 冻结根中；需要逐个对照时，把要跑的 fixture 复制到一个只含这些文件的目录，再用 `--combat-pilot-compare --pilot-dir <目录>`。归类结论和复现命令保存在本地批次报告里。

两类根的导入方式见 [原生导入](../../../docs/import.md#8-前缀根与回合准备根)。

控制器、回合准备和整场回归使用的长期请求清单在 [`../scenarios/`](../scenarios/README.md)，与冻结 fixture 输入分开维护。
