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

## 线程分工

入口订阅 RitsuLib 的战斗开始、结束事件，并挂上 F8 节点。一次搜索分三段：

1. **主线程捕获。** 读取原版状态，由 Import 编译出 `Battle`、`Rules`、评分表和搜索配置。
2. **后台搜索。** worker 只访问上一步冻结的数据，按组合调度依次运行搜索成员。普通模式下会按游戏帧压力让出时间；确定性测试不挂让帧器。
3. **主线程执行。** 结果交回主线程，经内核重放确认路线有效后，通过游戏的动作入口执行并逐动作核对。

F8、战斗结束和退出场景都会取消后台搜索与自动执行。

搜索内存预算默认取运行时可用内存（物理内存或容器限制）的 15%，下限 1 GiB。环境变量 `STS2_SOLVER_SEARCH_MEMORY_MB` 可按 MiB 覆盖；值不是正整数时记一条 `SEARCH_MEMORY_IGNORED` 日志并回到默认值。这个预算限制的是搜索自己的逻辑存储，不是游戏进程的 RSS。细节见 [战斗搜索](combat-ai.md#7-内存与容量)。

## 尚未提供

设置面板、路线展示、Steam Workshop 上传和第三方 Mod 兼容层都还没有。当前搜索档位固定为中档。
