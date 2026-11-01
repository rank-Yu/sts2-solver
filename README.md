# STS2 Solver · 尖塔求解器

《杀戮尖塔 2》的战斗求解 Mod，面向原版单人、同种子 SL 模式。它读取当前战斗和真实的随机数状态，搜索整场战斗的出牌路线，再通过游戏自己的操作入口自动出牌、用药，并逐个动作核对实际结果。

“同种子 SL”指同一个种子下存档读档：随机结果是确定的，求解器正是靠这一点预测每一步的结果。

本 Mod 只接管战斗；地图、事件、商店等局外操作仍由玩家完成。

## 安装

需要游戏 v0.111.0，以及兼容的 RitsuLib（清单要求最低 0.6.0）。

1. 单独安装 RitsuLib。
2. 把发布包里的 `Sts2Solver/` 目录放进游戏的 `mods/` 目录。

不要和 CombatSolver 同时安装：检测到 CombatSolver 时，本 Mod 不启用，以免两个求解器同时控制战斗。

## 使用

- 在自己的出牌阶段按 **F8**，开启本场战斗的自动执行；再按一次 F8 停止。停止时已经入队的原版动作会执行完。
- 战斗结束后自动停止；默认下一场需要重新按 F8。在设置文件里打开“每场自动开启全自动”后，进入可操作战斗会自动开始。
- 预测和实际状态不符时，丢弃剩下的路线并重新搜索；同一回合连续失败 3 次就停下，交还手动操作。
- 遇到不支持的内容（例如第三方 Mod 加入的卡牌）会明确拒绝，不会乱打。
- 搜索档位默认中档，可在设置文件里修改；还没有设置界面。设置文件位置、默认值和内存上限优先级见 [Mod 安装与构建](docs/mod.md#设置文件)。状态和错误写在游戏日志里，条目前缀是 `[Sts2Solver]`。

## 构建

需要 .NET 9 SDK。

不需要游戏文件的完整验证（内核合同、架构检查、冻结回归）：

```bash
tools/testing/verify-core.sh --jobs 8
```

冻结回归与基准一致只说明行为没变，不代表路线最优；搜索质量另由质量语料评估。

构建 Mod 本身需要本机的游戏文件，路径配置和正式版、测试版的区别见 [Mod 安装与构建](docs/mod.md)。架构、文档索引和术语见 [docs/README.md](docs/README.md)。

## 来源与许可

本仓库参考了 [CombatSolver](https://github.com/Torch1230/CombatSolver) 与 [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer)，部分代码移植自 CombatSolver（文件头注明出处）。

本仓库以 [MIT 许可证](LICENSE) 发布。两个上游项目也都是 MIT 许可，许可文本与本仓库的 `LICENSE` 相同，按其要求保留版权声明：

- CombatSolver：Copyright (c) 2026 Torch1230 and contributors
- Random Foreseer：Copyright (c) 2026 hotwords123
