# Mod 安装与构建

[返回文档索引](README.md)

本文讲发布包里有什么、怎么构建正式版和测试版、设置文件、战斗面板，以及 Mod 在游戏里的线程分工。玩家用法（F8 开关、什么时候停止）见仓库根的 [README](../README.md)；按下 F8 之后的决策与核对流程见 [战斗自动执行与核对](autoplay.md)。

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

设置保存在 Godot 用户目录下的 `user://sts2solver/settings.json`；实际路径由游戏的用户数据目录决定。文件不存在时使用默认值，第一次修改设置时创建。文件顶层的 `schemaVersion` 当前是 `1`；写入先写 `.tmp` 再原子替换。文件在 Mod 初始化时读取，直接修改文件需要重启游戏；运行中通过面板的设置页修改后，搜索设置在下一次搜索开始时生效。JSON 损坏或字段校验不通过时，文件会改名为同目录的 `settings.bad-<UTC 时间>.json`，记录 `SETTINGS_CORRUPT`，然后用默认值继续，不阻止游戏启动；其他读取失败记录 `SETTINGS_LOAD_FAILED`，同样用默认值继续。

默认值与当前行为一致。设置按搜索、自动化、界面三个分区保存：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `search.tier` | `Medium` | 搜索档位：`Low`、`Medium`、`High`、`ExtraHigh`。下一次搜索开始时生效，已经开始的搜索不变。 |
| `search.memoryBudgetBytes` | `null` | 搜索内存上限（字节）。`null` 表示按下面的优先级解析。 |
| `search.potions.defaultMode` | `Smart` | 药水默认策略：`Smart`、`Disabled`、`RequireAtLeastOne`。 |
| `search.potions.preset` | `Smart` | 药水预设：`Smart`、`ProtectAll`、`ForceAll`、`OnlyForced`。 |
| `search.potions.directives` | `[]` | 逐槽策略，每项是 `{ "slot", "potionId", "directive" }`，`directive` 为 `Smart`、`Force` 或 `Disabled`。 |
| `search.growthBudgets` | 十个来源全为 `0` | 成长来源的额外战损预算。 |
| `search.ignoreLongTermRewards` | `false` | 不考虑局外收益。 |
| `search.relicTargets` | `[]` | 遗物计数目标；每项包含 `id`、`minimum`、`maximum`、`hpAllowance`、`period`、`priority`。 |
| `search.actTransitionBossHpStrategy` | `ProgressionFirst` | 幕末 Boss 的血量策略，另一取值是 `MinimizeHpLoss`。 |
| `search.finalBossHpStrategy` | `ProgressionFirst` | 最终 Boss 的血量策略，另一取值是 `MinimizeHpLoss`。 |
| `search.theft` | `Auto` | 盗贼策略：`Auto` 沿用按遭遇捕获的行为，`PreserveResources` 保资源，`LetEscape` 放走。 |
| `automation.autoEnableFullAuto` | `false` | 每场战斗进入可操作状态后自动开始全自动。 |
| `ui.panelX`、`ui.panelY` | `null` | 主面板位置；两个值同时存在或同时为空。 |
| `ui.panelWidth`、`ui.panelHeight` | `null` | 主面板尺寸；两个值同时存在或同时为空。 |
| `ui.collapsed` | `false` | 主面板是否收起。 |
| `ui.opacity` | `0.65` | 主面板透明度，范围 0.25–1。 |
| `ui.theme` | `Dark` | 主面板主题：`Dark` 或 `Light`。 |

接入运行逻辑的是搜索档位、内存上限覆盖、“每场自动开启全自动”和 `ui` 分区（由战斗面板读写）。药水、成长、遗物、Boss 血量和盗贼策略字段只保存，还没有送入搜索请求，修改它们不会改变搜索或执行结果。

搜索内存上限按以下优先级解析：

1. 环境变量 `STS2_SOLVER_SEARCH_MEMORY_MB`（MiB）；
2. 设置里的 `search.memoryBudgetBytes`；
3. 默认为运行时可用内存（物理内存或容器限制）的 15%，下限 1 GiB。

解析结果写入 `SEARCH_MEMORY source=environment|settings|default` 日志。环境变量不是正整数时记录 `SEARCH_MEMORY_IGNORED`，继续使用设置或默认值。这个预算限制搜索自己的逻辑存储，不是游戏进程的 RSS；细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

F8 仍然是开关整场全自动：空闲时启动，运行中停止。`automation.autoEnableFullAuto` 打开后，进入可操作的单人战斗会自动走与 F8 相同的入口开始一次全自动；停止或暂停后不会在同一场战斗里自动重启。默认关闭，因此默认行为不变。

## 战斗面板

单人战斗进行中，屏幕左上方显示“尖塔求解器”面板；战斗结束或离开战斗后自动隐藏。面板只在主线程读取只读快照，按钮只经由与 F8 相同的命令入口开始或停止全自动，不直接调用搜索、`SearchSession` 或游戏动作。面板每帧读取快照，只在快照序号变化时更新控件；内容相同的路线行沿用原有控件。

面板从上到下分为五块：

- **标题栏**：标题、“设置”和“收起”。按住标题栏可以拖动面板，右边缘、下边缘和右下角可以缩放；松开鼠标后，位置和尺寸写入设置文件的 `ui.panelX`、`ui.panelY`、`ui.panelWidth`、`ui.panelHeight`。面板始终限制在窗口内，窗口变小时自动收回。收起后只保留标题栏、阶段徽标和全自动按钮，收起状态写入 `ui.collapsed`。
- **状态摘要**：当前阶段（待命、等待回合、搜索中、执行中、已暂停、胜利、战败、已停止、失败）、回合与生命，以及搜索进度。搜索中显示档位、正在跑第几轮、本轮宽度、本轮已用时间与时限和累计用时；搜索结束后显示总用时、实际运行轮数与计划轮数、展开节点数。暂停、停止和错误按稳定代码显示本地化说明。细进度条在搜索时表示本轮时限的进度，执行时表示整条路线的完成比例。
- **路线**：每回合一行，最多显示 16 行，窗口从当前回合的前一回合开始，超出的回合在末尾注明数量。动作胶囊左侧的色条区分攻击、技能、能力、状态或诅咒、药水；胶囊上写目标、选牌和击杀，悬停显示生命与能量变化。连续重复至少三次的动作折叠成“循环 ×N”。执行中的动作高亮，已执行的动作变暗；当前回合的行带金色标记。行尾依次是对敌伤害、本回合生命变化和结束回合时剩余的能量。结束回合是路线里的普通动作，只有它造成击杀、或者整回合只有结束回合时才单独画出。
- **状态详情**：点“状态详情”展开。内容是本场的搜索次数、沿用路线的回合数、预测偏差和失败次数，最近一次搜索的原因、档位、结果、用时、展开节点、状态转移、轮次、采用的轮次、停止原因、搜索内存峰值与进程内存峰值，以及逐轮表格（宽度、评价、用药阶段、结果、用时、内存峰值）。
- **底栏**：“开始全自动”或“停止全自动”按钮；不在自己的出牌阶段时点击会提示稍后再试。下方是内存条：整条代表运行时可用内存，彩色段是托管堆，灰色段是工作集里托管堆以外的部分。右侧单独写搜索内存上限及其来源，搜索结束后改为“搜索峰值 / 上限”。搜索上限只约束求解器自己的搜索存储，不是游戏进程的内存上限。

快捷键：

- `F8`：开关本场全自动，与面板按钮效果相同。
- `Ctrl+F9`：显示或隐藏整个面板，只在单人战斗中生效。隐藏状态保留到退出游戏，不写入设置文件。

### 设置页

点标题栏的“设置”切换到设置页，点“返回”回到路线。设置页只提供已经接入运行逻辑的项，修改后写入设置文件：

| 设置 | 字段 | 何时生效 |
| --- | --- | --- |
| 每场自动开启全自动 | `automation.autoEnableFullAuto` | 下一场战斗 |
| 搜索档位 | `search.tier` | 下一次搜索开始时；每个档位的宽度与每轮时限由搜索实际使用的档位配置生成 |
| 搜索内存上限（MiB） | `search.memoryBudgetBytes` | 下一次搜索开始时；留空使用默认值，下方显示当前生效的值和来源 |
| 主题 | `ui.theme` | 立即，面板按新配色重建 |
| 背景不透明度 | `ui.opacity` | 立即预览，停止拖动 0.4 秒后保存；只改变背景，文字保持不透明 |

设置了环境变量 `STS2_SOLVER_SEARCH_MEMORY_MB` 时，内存上限一栏只读显示环境变量的值。内存上限输入范围是 256 MiB 到运行时可用内存。“恢复默认”把上表各项和面板位置、尺寸恢复为默认值。药水、成长、遗物、Boss 血量和盗贼策略不在设置页里。

### 语言与字体

游戏语言为简体或繁体中文时面板显示中文，其他语言显示英文。英文文案在 `src/Mod/UI/English.json`，键是中文源串。切换游戏语言后面板整体重建，文字、提示和路线行都换成新语言，卡牌和药水名立即按新语言解析；路线里的选牌名称在下一次发布路线时更新，目标和击杀名称在下一次搜索后更新。中文、日文、韩文、俄文等使用游戏自带的替换字体，其余语言使用游戏的 Kreon 字体。

## 线程分工

入口订阅 RitsuLib 的战斗开始、结束事件，并挂上 F8 节点。一次搜索分三段：

1. **主线程捕获。** 读取原版状态，由 Import 编译出 `Battle`、`Rules`、评分表和搜索配置。
2. **后台搜索。** worker 只访问上一步冻结的数据，按组合调度依次运行搜索成员。普通模式下会按游戏帧压力让出时间；确定性测试不挂让帧器。
3. **主线程执行。** 结果交回主线程，经内核重放确认路线有效后，通过游戏的动作入口执行并逐动作核对。

F8、面板的停止按钮、战斗结束和退出场景都会取消后台搜索与自动执行。界面只在主线程运行，读取的快照由主线程在上面三段的边界上发布；后台 worker 不接触快照和 Godot 节点。

搜索内存预算的解析顺序见上一节；它限制的是搜索自己的逻辑存储，不是游戏进程的 RSS。细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

## 尚未提供

单回合执行/采用/冻结/重算、药水/成长/遗物/Boss 血量/盗贼策略的设置界面、问题报告、Steam Workshop 上传和第三方 Mod 兼容层都还没有。策略字段已经保存，但还没有接入搜索请求。
