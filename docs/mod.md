# Mod 安装与构建

[返回文档索引](README.md)

本文讲发布包里有什么、怎么构建正式版和测试版、设置文件、战斗面板，以及 Mod 在游戏里的线程分工。玩家用法（三档模式、什么时候停止）见仓库根的 [README](../README.md)；控制器的决策与核对流程见 [战斗自动执行与核对](autoplay.md)。

## 安装包

把 `Sts2Solver/` 目录放进游戏的 `mods/` 目录。包里有 `Sts2Solver.dll`、`Sts2Solver.json`、`LICENSE` 和 `README.md`，不含游戏文件，也不含 RitsuLib，RitsuLib 由玩家另装。

- 兼容性：当前适配游戏 v0.111.0，清单要求 RitsuLib 至少 0.6.0。
- 与 CombatSolver 共存：检测到 CombatSolver 已加载或等待加载时，本 Mod 直接禁用（不注册补丁、不订阅事件，只写一行日志），避免两个求解器同时控制战斗。
- 第三方内容不在支持范围内，导入时会明确拒绝。
- 状态和错误写进游戏日志，条目前缀是 `[Sts2Solver]`。

## 两种构建

正式版只有战斗功能；测试版额外编入场景注入、状态捕获和无头测试补丁。游戏与 RitsuLib 的路径写在被 git 忽略的 `src/Mod/local.props`（模板 `local.props.example`）；Import 和测试宿主的游戏路径来自 `tests/Differential/local.props`。

```bash
# 正式版
dotnet build src/Mod -c Release

# 测试版
dotnet build src/Mod -c Release -p:EnableModTesting=true
```

| 项目 | 正式版 | 测试版 |
| --- | --- | --- |
| 打包目录 | `artifacts/mod/Sts2Solver` | `artifacts/mod-testing/Sts2Solver` |
| 中间文件与程序集 | `artifacts/obj`、`artifacts/bin` | `artifacts/testing/obj`、`artifacts/testing/bin` |
| Core、Import、战斗执行 | 相同源码 | 相同源码 |
| TestHost、测试入口与补丁 | 不编入 | 编入 |
| `STS2_SOLVER_TEST_DIR` | 不读取 | 设置后进入测试循环 |

游戏加载器只加载与清单 ID 同名的 DLL，所以 Core 与 Import 的源码直接编入 `Sts2Solver.dll`，不附带其它求解器 DLL。两种构建的 Mod ID 相同，不要同时安装。构建只写仓库里的 `artifacts/`，不碰玩家的游戏目录。测试版入口只在显式测试模式下注册测试补丁；正式版既没有测试补丁目录，也没有补丁注册逻辑。

不加载游戏就能检查两种包的编译边界（类型与程序集引用）：

```bash
dotnet run --project tests/Architecture -c Release --no-build -- --mod artifacts/mod/Sts2Solver/Sts2Solver.dll
dotnet run --project tests/Architecture -c Release --no-build -- --testing-mod artifacts/mod-testing/Sts2Solver/Sts2Solver.dll
```

测试版的无头运行方法见 [测试与验证](testing.md#9-实机无头测试)。

## 设置文件

设置保存在 Godot 用户目录下的 `user://sts2solver/settings.json`；实际路径由游戏的用户数据目录决定。文件不存在时使用默认值，第一次修改设置时创建。文件顶层的 `schemaVersion` 当前是 `3`；写入先写 `.tmp` 再原子替换。读取到 `schemaVersion=1` 的文件时，按旧字段 `automation.autoEnableFullAuto` 迁移：`true` 对应“全自动”，否则“自动搜索”；读取到 `schemaVersion=2` 的文件时补上新字段的默认值。迁移后立即写回 schema 3。文件在 Mod 初始化时读取，直接修改文件需要重启游戏；运行中通过面板的设置页修改后，搜索设置在下一次搜索开始时生效。JSON 损坏或字段校验不通过时，文件会改名为同目录的 `settings.bad-<UTC 时间>.json`，记录 `SETTINGS_CORRUPT`，然后用默认值继续，不阻止游戏启动；其他读取失败记录 `SETTINGS_LOAD_FAILED`，同样用默认值继续。

默认值与当前行为一致。设置按搜索、自动化、界面三个分区保存：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `search.tier` | `Medium` | 搜索档位：`Low`、`Medium`、`High`、`ExtraHigh`。下一次搜索开始时生效，已经开始的搜索不变。 |
| `search.memoryBudgetBytes` | `null` | 搜索内存上限（字节）。`null` 表示按下面的优先级解析。 |
| `search.threads` | `null` | 一个搜索成员的总线程数（含搜索线程本身）；`null` 表示按逻辑核数自动决定。 |
| `search.potions.defaultMode` | `Smart` | 药水默认策略：`Smart`、`Disabled`、`RequireAtLeastOne`。 |
| `search.potions.preset` | `Smart` | 药水预设：`Smart`、`ProtectAll`、`ForceAll`、`OnlyForced`。 |
| `search.potions.directives` | `[]` | 逐槽策略，每项是 `{ "slot", "potionId", "directive" }`，`directive` 为 `Smart`、`Force` 或 `Disabled`。 |
| `search.growthBudgets` | 十个来源全为 `0` | 成长来源的额外战损预算。 |
| `search.ignoreLongTermRewards` | `false` | 不考虑局外收益。 |
| `search.relicTargets` | `[]` | 遗物计数目标；每项包含 `id`、`minimum`、`maximum`、`hpAllowance`、`period`、`priority`。 |
| `search.actTransitionBossHpStrategy` | `ProgressionFirst` | 幕末 Boss 的血量策略，另一取值是 `MinimizeHpLoss`。 |
| `search.finalBossHpStrategy` | `ProgressionFirst` | 最终 Boss 的血量策略，另一取值是 `MinimizeHpLoss`。 |
| `search.theft` | `Auto` | 盗贼策略：`Auto` 沿用按遭遇捕获的行为，`PreserveResources` 保资源，`LetEscape` 放走。 |
| `automation.mode` | `AutoSearch` | 三档模式：`Manual`、`AutoSearch`、`FullAuto`。跨战斗、跨重启保留。 |
| `automation.solverDisabled` | `false` | 求解器总开关。关闭时取消搜索/执行、清空结果，并把全自动退回自动搜索。 |
| `automation.deploymentSpeed` | `FollowGame` | 自动出牌速度：`FollowGame`、`Normal`、`Fast`；执行期间临时覆盖游戏战斗速度，结束后恢复。 |
| `automation.interActionDelaySeconds` | `0` | 两张牌之间的额外停顿，范围 0–3 秒。 |
| `ui.panelX`、`ui.panelY` | `null` | 主面板位置；两个值同时存在或同时为空。 |
| `ui.panelWidth`、`ui.panelHeight` | `null` | 主面板尺寸；两个值同时存在或同时为空。 |
| `ui.collapsed` | `false` | 主面板是否收起。 |
| `ui.opacity` | `0.65` | 主面板透明度，范围 0.25–1。 |
| `ui.showBattleDamagePerformanceHint` | `true` | 是否显示“本场战斗出现大战损”提示；玩家点击提示后写为 `false`，之后不再显示。 |
| `ui.theme` | `Dark` | 旧文件的兼容字段；面板固定深色，不提供浅色主题。 |

接入运行逻辑的是搜索档位、内存上限覆盖、搜索线程数、`automation` 分区和 `ui` 分区（由战斗面板读写）。药水、成长、遗物、Boss 血量和盗贼策略字段只保存，还没有送入搜索请求，修改它们不会改变搜索或执行结果。

搜索内存上限按以下优先级解析：

1. 环境变量 `STS2_SOLVER_SEARCH_MEMORY_MB`（MiB）；
2. 设置里的 `search.memoryBudgetBytes`；
3. 默认为运行时可用内存（物理内存或容器限制）的 15%，下限 1 GiB。

解析结果写入 `SEARCH_MEMORY source=environment|settings|default` 日志。环境变量不是正整数时记录 `SEARCH_MEMORY_IGNORED`，继续使用设置或默认值。这个预算限制搜索自己的逻辑存储，不是游戏进程的 RSS；细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

搜索线程数按以下优先级解析：

1. 环境变量 `STS2_SOLVER_SEARCH_THREADS`（正整数）；
2. 设置里的 `search.threads`；
3. 默认取 `clamp(逻辑核数 - 2, 1, 8)`。

显式值都夹到 1..8，越界时记录 `SEARCH_THREADS_CLAMPED`；环境变量不是正整数时记录 `SEARCH_THREADS_IGNORED` 并继续使用设置或默认值。解析结果写入 `SEARCH_THREADS value=N source=environment|settings|default logical=P`。环境变量和逻辑核数在进程内缓存，设置值在每次搜索开始时重新读取，因此会话中修改 `search.threads` 会在下一次搜索生效。N 是一个成员内的总线程数，包括搜索线程本身，即 N−1 个工作线程加搜索线程；它不改变成员调度、评分或路线选择。

展开工作区按线程持有，不计入搜索内存上限。每个工作区的可变缓冲默认上限 8 MiB，N 线程时窗口取 2N 个工作区，因此可变部分上界约为 2N×8 MiB；每个工作区另有约 134 KiB 的固定草稿，固定部分合计约 2N×134 KiB。N=8 时可变部分上界约 128 MiB，合计约 130 MiB。细节见 [战斗搜索](combat-ai.md#5-单个成员内部)。

三档模式取代原来的“全自动开关”和“自动开启全自动”：手动档不自动搜索；自动搜索档每回合开始自动搜索，但不出牌；全自动档在搜索完成后自动执行本回合。全自动档每场战斗开局武装一次；停止搜索、三条暂停条件或关闭求解器都会把持久档位改成自动搜索，本场不会自动重启。首次安装默认自动搜索，因此进入战斗会开始搜索，但不会自动出牌。

## 战斗面板

单人战斗进行中，屏幕左上方显示“尖塔求解器”面板；战斗结束、离开战斗或处于多人会话时隐藏。面板只在主线程读取只读快照，按钮只经由 `ICombatUiCommands` 下发命令，不直接调用搜索、`SearchSession` 或游戏动作。面板每帧读取快照，只在快照序号变化时更新控件；内容相同的路线行沿用原有控件。

面板从上到下分为：

- **标题栏**：产品名“尖塔求解器”、“求解器：开/关”按钮、三档模式占位按钮、“设置”和“收起”。标题和按钮使用粗体，没有标题前的装饰竖条。按住标题栏可以拖动面板，右边缘、下边缘和右下角可以缩放；松开鼠标后，位置和尺寸写入设置文件的 `ui.panelX`、`ui.panelY`、`ui.panelWidth`、`ui.panelHeight`。面板使用原版的展开宽度 560–820、展开高度上限 440、收起尺寸 520×120，始终限制在窗口内。收起后路线内容和设置页隐藏，标题栏、求解器开关、模式按钮和动作栏仍然可用；收起状态写入 `ui.collapsed`。药水、成长和遗物按钮不显示。
- **提示条**：标题栏下面依次是搜索上限提示和大战损提示。搜索上限提示只在本轮搜索因时间或节点上限提前结束时出现，文案按稳定边界码选择。大战损提示在本场预计战损至少 8 HP 时出现；点击后写入 `ui.showBattleDamagePerformanceHint=false`，永久不再显示。
- **状态摘要**：状态文字、上下文、摘要和统计行。上下文只写回合、已规划到的回合、暂停原因或“已排队执行”，不显示生命。搜索中显示“后台搜索中”、已用时间和整次请求的进度条；摘要先显示“正在搜索当前回合，等待可存活候选…”，有候选后改为“候选摘要 │ 已查阅 N 条世界线（S 条/s）”，世界线和速度按指数平滑滚动。搜索结果就绪时显示“方案就绪 / 方案已复用 / 重算后战损上升”，摘要为“本回合结束战斗”或“预计路线 N 回合”，统计行为“花费了 X 秒，共查阅了 N 条世界线（S 条/s）”。执行中显示“正在执行”，每步更新为“正在执行 k/N：卡名”，结束回合时改为“正在执行： 结束回合”。执行完成后转“等待下一回合方案”。
- **路线**：第一行是结果的起始回合；复用的路线从当前回合开始；执行完成后、下一回合结果出来之前，第一行仍是刚执行的那一回合。最多显示 16 行，超出时在摘要末尾注明“另有 N 回合未展开显示”，不显示更早的回合。动作胶囊左侧色条区分攻击、技能、能力、状态或诅咒、药水；胶囊上写目标、选牌、击杀和重放次数，悬停显示生命与能量变化。连续重复至少三次的动作折叠成“循环 ×N”。执行中的动作高亮，已执行的动作变暗；当前回合的行带蓝色强调标记。行尾依次是对敌伤害、本回合生命变化和结束回合时剩余的能量。结束回合只在造成击杀、或者整回合只有结束时单独画出。搜索中显示搜索线程当前考虑的未验证候选。
- **状态详情**：点“状态详情”展开。内容是本场的搜索次数、沿用路线的回合数、预测偏差和失败次数，最近一次搜索的原因、档位、搜索线程数、结果、用时、展开节点、状态转移、路线动作、轮次、采用的轮次、停止原因、搜索内存峰值、工作区峰值与进程内存峰值，以及逐轮成员表和逐次运行表。工作区峰值单独列出可增长部分、总占用和超限父节点数，不计入搜索内存。
- **底栏**：按原版放置“执行本回合 / 应用当前回合”、采用当前路线、开始/重新搜索、停止搜索，以及内存条。按钮的可见性和可点性由快照的 `Can*`、`CanApplyCurrentTurn`、`SolverDisabled`、`IsSearching`、`IsDeploying`、`IsAdoptingRoute` 和各阶段标志决定：搜索中隐藏执行按钮和重新搜索、显示停止按钮；有可采用的已渲染候选时显示采用按钮；等待下一回合时执行按钮显示“等待下一回合”。下方内存条整条代表可用内存，彩色段是托管堆，灰色段是工作集里托管堆以外的部分；标签写“游戏占用 · 系统可用”，搜索中追加“搜索峰值 / 上限”。搜索上限只约束求解器自己的搜索存储，不是游戏进程的内存上限；展开工作区不计入这个上限。

暂停状态按原版分成四种：`combat_end_turn` 显示“方案就绪 / 第 N 回合    全自动已暂停 / 当前方案预计结束战斗，操作权已交还。”；`death_turn` 显示“已暂停执行 / 第 N 回合    预计死亡 / 当前方案预计本回合死亡，操作权已交还。”；`worse_recalculation` 显示“已暂停执行 / 第 N 回合    重算后战损上升 / 完整路线原预计 X HP，重算后为 Y HP；全自动已暂停。”；`live_end_turn_risk` 显示“已暂停执行 / 第 N 回合    实机复核将死亡（或实机复核战损上升）/ 路线预计掉血 X HP，结束回合前实机复核为 Y HP；全自动未提交结束回合。”。停止、手动档等待、求解器关闭分别显示原版的停止、开始/重新搜索和禁用文案。

没有战斗快捷键；面板只能收起，不能完全隐藏。注入 F8 或 Ctrl+F9 都不会改变面板状态。

暂停码只有这些稳定值：`worse_recalculation`、`death_turn`、`combat_end_turn`、`live_end_turn_risk`、`repeated_failure`、`choice_mismatch`、`stopped`。不在出牌阶段、求解器关闭、多人等拒绝原因写入 `rejectCode`，不写入 `pauseCode`。

### 设置页

点标题栏的“设置”切换到设置页，点“返回”回到路线。设置页只提供已经接入运行逻辑的项，修改后写入设置文件：

| 设置 | 字段 | 何时生效 |
| --- | --- | --- |
| 模式 | `automation.mode` | 立即；切换不打断进行中的搜索或执行 |
| 搜索档位 | `search.tier` | 下一次搜索开始时；每个档位的宽度与每轮时限由搜索实际使用的档位配置生成 |
| 搜索内存上限（MiB） | `search.memoryBudgetBytes` | 下一次搜索开始时；留空使用默认值，下方显示当前生效的值和来源 |
| 背景不透明度 | `ui.opacity` | 立即预览，停止拖动 0.4 秒后保存；只改变背景，文字保持不透明 |

设置页的“模式”与标题栏按钮是同一个持久字段。设置了环境变量 `STS2_SOLVER_SEARCH_MEMORY_MB` 时，内存上限一栏只读显示环境变量的值。内存上限输入范围是 256 MiB 到运行时可用内存。“恢复默认”把上表各项和面板位置、尺寸恢复为默认值。面板固定深色，没有主题选择。药水、成长、遗物、Boss 血量和盗贼策略不在设置页里。搜索线程数、自动出牌速度和牌间额外停顿目前只有后端字段，设置页不提供控件；大战损提示的持久字段也没有控件，只由提示本身写入。

### 语言与字体

游戏语言为简体或繁体中文时面板显示中文，其他语言显示英文。英文文案在 `src/Mod/UI/English.json`，键是中文源串。切换游戏语言后面板整体重建，文字、提示、路线行和卡片名称都换成新语言。中文、日文、韩文、俄文等使用游戏自带的替换字体；其余语言不额外覆盖字体，使用游戏主题字体。切换语言时面板整体重建。

## 线程分工

入口订阅 RitsuLib 的战斗开始、结束事件和游戏的玩家回合开始事件。一次搜索分三段：

1. **主线程捕获。** 读取原版状态，由 Import 编译出 `Battle`、`Rules`、评分表和搜索配置。
2. **后台搜索。** worker 只访问上一步冻结的数据，按组合调度依次运行搜索成员。每个成员内部按搜索线程数并行展开动作层：搜索线程按父节点原顺序提交，工作线程只做展开计算。普通模式下会按游戏帧压力让出时间，所有工作线程共用一个帧压力信号；`Solve` 结束前把工作线程的让出和等待指标合并进主 pacer。确定性测试不挂让帧器，单次搜索默认只用 1 个线程。
3. **主线程执行。** 结果交回主线程，经内核重放确认路线有效后，通过游戏的动作入口执行并逐动作核对。

停止搜索、关闭求解器、战斗结束和退出场景都会取消后台搜索与自动执行；切换档位不会。界面只在主线程运行，读取的快照由主线程在上面三段的边界上发布；后台 worker 不接触快照和 Godot 节点。

搜索内存预算的解析顺序见上一节；它限制的是搜索自己的逻辑存储，不是游戏进程的 RSS。展开工作区按线程单独持有，不计入这个预算。细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

## 尚未提供

冻结路线、药水/成长/遗物/Boss 血量/盗贼策略的设置界面、问题报告、Steam Workshop 上传和第三方 Mod 兼容层都还没有。药水、成长和遗物按钮不显示；策略字段已经保存，但还没有接入搜索请求。
