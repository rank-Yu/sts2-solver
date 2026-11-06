# 架构与文档索引

主分支只发布战斗求解 Mod。局外开发代码在 `dev/full-run` 分支，完整版本的存档标签是 `archive/full-run-before-combat-release`；它们不参与主分支构建。

## 分层

```text
Mod（生命周期、控制器、UI、动作执行与核对）
  ├── Import.Combat（只读游戏 → Battle / Rules / 搜索配置）
  └── Autoplay（共享路线规则；CombatPilot 仅离线整场）
        ├── Ai.Combat（搜索与评价）
        └── Simulation.Combat（状态与结算）
Content ──→ Simulation.Combat（经注册表提供内容行为）
```

- `Simulation.Combat` 不引用 Content、Ai、Autoplay 或游戏。
- `Content` 与 `Ai.Combat` 只向下引用 `Simulation.Combat`，Ai 不直接引用 Content。
- `Autoplay` 引用 Simulation 与 Ai，通过宿主接口请求搜索和执行。
- Core 只依赖 BCL。Import 负责读取原生模型和私有字段。Mod 在游戏主线程捕获与执行，设置和只读 UI 快照也在主线程维护；后台 worker 只处理冻结后的数据。
- `TestHost/Combat` 提供场景创建和注入，供 Differential 与测试版 Mod 使用；`TestHost/ModTesting` 只编入测试版 Mod。
- 正式 Mod 不引用 TestHost，不注册测试补丁。无头测试用的是和正式版相同的搜索、执行和核对代码。

| 目录 | 职责 |
| --- | --- |
| `src/Core/Simulation/Combat` | 战斗状态（打包牌区）、规则、动作和回合结算 |
| `src/Core/Content` | 五个角色的卡牌，以及 Power、怪物、遗物、药水、球与附魔的效果 |
| `src/Core/Ai/Combat` | 搜索调度、评分、分支保留、去重与预算 |
| `src/Core/Autoplay` | 共享路线规则（续接、裁剪、偏差、暂停策略）与 `CombatPilot` 离线整场循环 |
| `src/Import/Combat` | 只读导入游戏状态，编译规则与搜索配置 |
| `src/Mod` | 游戏入口、控制器、战斗面板（设置页与策略侧栏）、设置存储、只读快照、后台搜索、动作执行与实际结果核对 |
| `src/TestHost/Combat` | 测试场景创建、注入、原生状态转储 |
| `src/TestHost/ModTesting` | 只编入测试版 Mod 的实机测试入口与补丁 |
| `tests` | 内核合同、架构检查、冻结回归、原版差分与质量语料 |
| `tools/testing` | 本地验证与无头实机测试脚本 |
| `tools/sts2-sim-export` | 用 sts2-sim 跑整局并导出 `OfflineRequest` 的本地工具 |

`Sts2Solver.sln` 只包含不需要游戏文件的 Core、KernelChecks、Golden 和 Architecture；Import、Mod、TestHost、Differential 单独构建。游戏加载器要求单一 Mod DLL，所以 Mod 把 Core 与 Import 的源码直接编入 `Sts2Solver.dll`，测试版再额外编入 TestHost。

## 设计约束

`Battle` 是不含对象引用的值类型。一个状态由“头部 + 前 `CardCount` 张牌”组成，搜索时只复制这段打包前缀，所以牌越少复制越快；热路径不分配托管对象。容量不足或遇到不支持的语义时显式失败，不截断、不返回默认值。

固定预算的请求可以编码保存、离线重放，用于确定性回归；正常搜索只按每个成员的墙钟截止停下，没有节点上限：机器越快搜得越多，结果可能与离线可复现评测（固定节点预算）不同。

战斗评分要用到一些局外信息：药水留着值多少、永久成长、战后回血，以及当前幕数、楼层和随机数状态。这些都由 Import 在开战时从游戏里读出来、冻结进请求；主分支不维护自己的跑局状态。

## 术语

| 词 | 含义 |
| --- | --- |
| 原版 | 游戏本体（`sts2.dll`）的实现。 |
| 原引擎 | CombatSolver 提交 `a62f1850` 的战斗模拟引擎。Sts2Solver 内核照它的结算顺序翻译，冻结快照的语义也来自它；各文档里的“对照”指它的源码位置，路径缩写见 [内核语义](combat-semantics.md)。 |
| 原导入层 | CombatSolver 的 `src/Flash/Bridge`：当年把原引擎的影子状态转成 `Battle` 的那一层，旧文档里叫 Bridge 或“旧桥”。本仓库的 Import 直接读游戏对象，但不少导入口径沿用它。 |
| 镜像 / 补偿 | 原引擎对卡牌效果的两段划分：“镜像”是从原版代码推断或专门复刻的攻击、格挡、抽牌；“补偿”是另外补写的 Power、资源、生成效果。 |
| M0 宿主 | CombatSolver `flash/m0` 分支上的离线宿主，录制了 `tests/Golden/data`；提交号记在 manifest 的 `provenance`。 |
| 根 | 一次搜索的起点状态。**普通根**是玩家出牌阶段直接导入的状态；**前缀根**先导入一段冻结动作之前的状态，再用内核回放这段动作；**回合准备根**是回合开始有选择（如 Toolbox、Toasty Mittens）时，每个选项各得到的一个 Play 根。原生 `SetupPlayerTurn` 入口、还没做能量重置和抽牌的状态叫**回合准备 Start 根**；内核前奏从它枚举准备计划，去重后的 Play 根组成多根请求。 |
| 稳定边界 | 一个动作连同它触发的所有效果都执行完、也没有待处理选择的时刻。`Battle` 只在稳定边界上比较、去重和保存；`Exec` 上的瞬态此时必须为空。 |
| 成员 | 一轮束搜索的设置：宽度、途中评价、候选保留、用药限制。一次决策顺序跑多个成员，见 [战斗搜索](combat-ai.md)。 |
| 模拟回归 / 搜索回归 | 冻结快照的两部分，数据目录分别是 `d1`、`d2`，旧编号 D1、D2。 |
| K0 / K1 / K2 / K4 | K0 是纯内核合同（`tests/KernelChecks` 与 `Golden k0`）；K1 是模拟回归里每个根上每个合法动作的单步展开；K2 是固定种子的随机游走；K4 是记录在模拟回归里的评分、下界和路线标签哈希（`scoreHash`、`boundHash`、`traitHash`）。 |
| BATL / RULE / GOLD / SREQ / SRES | 二进制格式标签：状态、规则表、模拟回归文件、单成员搜索请求与结果。格式见 [冻结快照](golden-data.md)。 |

## 文档

| 文档 | 什么时候读 |
| --- | --- |
| [Mod 安装与构建](mod.md) | 安装发布包、构建正式版或测试版、检查包边界、查看设置文件、战斗面板与线程分工 |
| [战斗自动执行与核对](autoplay.md) | 想知道三档模式怎么搜、怎么执行、什么时候停 |
| [战斗搜索](combat-ai.md) | 改搜索调度、评分或预算；想知道一次决策跑了哪些成员 |
| [战斗状态布局](combat-state-layout.md) | 改 `Battle`、`Card` 等结构的字段或容量之前 |
| [战斗内核语义](combat-semantics.md) | 改结算顺序、Hook 派发、选择协议、怪物 AI |
| [内容专属机制](combat-content.md) | 新增或修正某张卡、某个 Power、遗物、药水、怪物 |
| [原生导入](import.md) | 改状态导入、规则编译、评估配置或拒绝规则 |
| [冻结快照](golden-data.md) | 读写快照格式，或需要更新快照时 |
| [测试与验证](testing.md) | 该跑哪些检查、每层证明什么、不证明什么 |

测试目录的说明：

| 目录 | 内容 |
| --- | --- |
| [tests/KernelChecks](../tests/KernelChecks/README.md) | 纯内核合同 |
| [tests/Golden](../tests/Golden/README.md) | 冻结快照校验器 |
| [tests/Differential](../tests/Differential/README.md) | 原版差分宿主的命令清单 |
| [tests/corpora/fixtures](../tests/corpora/fixtures/README.md) | 模拟回归的建局输入 |
| [tests/corpora/quality](../tests/corpora/quality/README.md) | 质量评测语料 |
