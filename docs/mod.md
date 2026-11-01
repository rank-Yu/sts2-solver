# Mod 安装与构建

[返回文档索引](README.md)

本文讲发布包里有什么、怎么构建正式版和测试版，以及 Mod 在游戏里的线程分工。玩家用法（F8 开关、什么时候停止）见仓库根的 [README](../README.md)；按下 F8 之后的决策与核对流程见 [战斗自动执行与核对](autoplay.md)。

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

设置保存在 Godot 用户目录下的 `user://sts2solver/settings.json`；实际路径由游戏的用户数据目录决定。文件不存在时使用默认值，第一次修改设置时创建。文件顶层的 `schemaVersion` 当前是 `1`；写入先写 `.tmp` 再原子替换。文件在 Mod 初始化时读取，直接修改文件需要重启游戏；运行中通过设置接口修改后，下一次搜索开始时生效。JSON 损坏或字段校验不通过时，文件会改名为同目录的 `settings.bad-<UTC 时间>.json`，记录 `SETTINGS_CORRUPT`，然后用默认值继续，不阻止游戏启动；其他读取失败记录 `SETTINGS_LOAD_FAILED`，同样用默认值继续。

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

当前只有搜索档位、内存上限覆盖和“每场自动开启全自动”已经接入运行逻辑。药水、成长、遗物、Boss 血量和盗贼策略字段只保存，还没有送入搜索请求，修改它们不会改变搜索或执行结果。

搜索内存上限按以下优先级解析：

1. 环境变量 `STS2_SOLVER_SEARCH_MEMORY_MB`（MiB）；
2. 设置里的 `search.memoryBudgetBytes`；
3. 默认为运行时可用内存（物理内存或容器限制）的 15%，下限 1 GiB。

解析结果写入 `SEARCH_MEMORY source=environment|settings|default` 日志。环境变量不是正整数时记录 `SEARCH_MEMORY_IGNORED`，继续使用设置或默认值。这个预算限制搜索自己的逻辑存储，不是游戏进程的 RSS；细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

F8 仍然是开关整场全自动：空闲时启动，运行中停止。`automation.autoEnableFullAuto` 打开后，进入可操作的单人战斗会自动走与 F8 相同的入口开始一次全自动；停止或暂停后不会在同一场战斗里自动重启。默认关闭，因此默认行为不变。

## 线程分工

入口订阅 RitsuLib 的战斗开始、结束事件，并挂上 F8 节点。一次搜索分三段：

1. **主线程捕获。** 读取原版状态，由 Import 编译出 `Battle`、`Rules`、评分表和搜索配置。
2. **后台搜索。** worker 只访问上一步冻结的数据，按组合调度依次运行搜索成员。普通模式下会按游戏帧压力让出时间；确定性测试不挂让帧器。
3. **主线程执行。** 结果交回主线程，经内核重放确认路线有效后，通过游戏的动作入口执行并逐动作核对。

F8、战斗结束和退出场景都会取消后台搜索与自动执行。

搜索内存预算的解析顺序见上一节；它限制的是搜索自己的逻辑存储，不是游戏进程的 RSS。细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

## 尚未提供

设置面板、路线与搜索面板、`Ctrl+F9`、单回合执行/采用/冻结/重算、问题报告、Steam Workshop 上传和第三方 Mod 兼容层都还没有。设置文件目前只能手工编辑；策略字段已经保存，但还没有接入搜索请求。
