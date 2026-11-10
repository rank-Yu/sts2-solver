# 场景请求清单

本目录是控制器场景、回合准备场景和整场回归的长期输入。`manifest.json` 是唯一清单：新增或调整一个场景只改这里，不复制请求 JSON。入口脚本 `tools/testing/run-scenario-regression.py` 按清单从 fixtures / quality 语料生成请求，放进 `.local/scenario-regression/requests/` 后运行；生成物不进入仓库。

每个条目记录：

- `id`：请求文件名，也是结果目录名；
- `scenario`：`controllerScenarioForTest` 的场景名；整场请求为 `null`；
- `group`：`controller`、`turn-setup`、`full-combat` 或 `panel-stress`；
- `fixture`：差分宿主生成的根，例如 `k3-basic-potions`、`ts-a-toasty`、`quality-DEFECT-001`；
- `threads`：测试请求的 `settingsThreadsForTest`；`auto` 表示按本机默认解析；
- `english`：是否用游戏自带英文覆盖；
- `scenarioId`：少数场景（胶囊来源）按 `scenarioId` 选择断言；
- `separate`：整场请求独占一个实例，避免同一进程的上一场战斗影响本场的面板检查；
- `request`：覆盖基础请求的其他测试字段，包括 `settingsModeForTest`、`turnSetupTakeoverForTest`、初始生命/敌人生命、药水注入和强制策略。

## 为到达稳定回合而调整的输入

| 场景 | 根 | 输入 |
| --- | --- | --- |
| `turn_setup_carry` | `ts-a-toasty` | 第 1 回合生命 1/80、敌人 120、注入并强制 `BLOOD_POTION`，全自动 + 接管；`RecoveredHp=16` |
| `turn_setup_continuation_*` | `ts-a-toasty` | 500/500 对 500，全自动或自动搜索 + 接管 |
| `turn_setup_continuation_hatchet` | `ts-m-hatchet-return` | 跑 `turn_setup_continuation_full`：第 1 回合打出嗡鸣手斧，第 2、3 回合的准备页沿用计划、0 偏差（Start 根要带上“下回合回手”标记） |
| `turn_setup_continuation_gap` | `ts-g-multi-choice` | 500/500 对 500，全自动 + 接管 |
| `turn_setup_turn2` / `turn_setup_plasma` / `turn_setup_autopreplay` | `ts-a-toasty` / `ts-i-plasma-pause` / `ts-d-autopreplay-choice` | 500/500 对 500 |
| `turn_setup_extra_turn` | `ts-ex-extra-turn` | 第 1 回合能量 0、PaelsEye 触发额外回合，全自动 + 接管；校验额外回合的准备沿用计划、无偏差 |
| `turn_setup_extra_turn_search` | `ts-ex-extra-turn` | 同上但玩家手动结束第 1 回合，额外回合的准备页没有可沿用的 EndTurn 计划，必须重新枚举和搜索 |
| `deviation_boundary_setup` / `deviation_setup_stamp` | `ts-a-toasty` | 500/500 对 500，全自动 + 接管 |
| `live_end_turn_risk_retained` | `k3-basic-potions` | 生命 65/80、敌人 200，结束回合前注入 60 伤害 |
| `import_capacity` / `import_failure` | `k3-basic-potions` | 清空跑局牌组后注入 641 张 `STRIKE_IRONCLAD`，超过 `Limits.Cards` |
| `search_failures-t1` / `manual_switch_at_turn_start` / `auto_turn_start_once` | `k3-basic-potions` | 单搜索线程；前两者切到手动，后者验证同一回合不重复自动搜索 |

整场组用五个角色的 `quality-<角色>-001`，面板压力组用 `quality-DEFECT-000` 和 `clone-289-axebots`。整场请求都是中档、全自动、默认搜索线程；面板检查共 54 项，失败必须为 0（每场独立实例的第一场按设计跳过两项进程间检查）。

## 运行

先构建测试版 Mod 和 Differential，然后：

```bash
tools/testing/run-scenario-regression.py                       # 全部组
tools/testing/run-scenario-regression.py --group controller    # 只跑一组，可重复
tools/testing/run-scenario-regression.py --scenario turn_setup_carry --generate-only
tools/testing/run-scenario-regression.py --jobs 4 --results .local/my-results
```

每个实例最多六个请求，并带 `--cleanup-instance-on-exit`；调度器同时运行的实例声明的搜索线程总数不超过 8。脚本最后按组打印通过数，失败时打印场景名和第一条失败断言。详细用法见 [测试与验证](../../../docs/testing.md#94-场景回归入口)。
