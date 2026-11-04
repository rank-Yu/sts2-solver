# 冻结 fixture 输入

本目录是 `tests/Golden/data/d1` 中 290 个根的建局输入，供差分宿主（`tests/Differential`）重建录制时的原生战斗。

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
  - `ts-g-multi-choice`：第 2 回合 `AfterPlayerTurnStart` 两次选择（暴政 + 烤面包手套），并带熔炉的 `AfterSideTurnStart`；
  - `ts-h-choice-then-listener`：第 1 回合烤面包手套之后还有宝石面具和十字弓，覆盖同一轮选择后仍有监听器；
  - `ts-i-plasma-pause`：第 2 回合等离子球与烤面包手套，覆盖准备暂停处的球触发；
  - `ts-j-crossbow-one-card` / `ts-j-crossbow-many`：烤面包手套 + 十字弓，覆盖暂停后由侧回合开始加入手牌的攻击牌（手牌 1 张和多张两种）；
  - `ts-k-two-relic-choices`：烤面包手套 + 赌博芯片，同一准备阶段产生两段可见选择；
  - `ts-ex-extra-turn`：PaelsEye + 烤面包手套，第 1 回合能量 0 且只抽防御牌，结束回合必触发额外回合，覆盖额外回合开始的准备选择。

两类根的导入方式见 [原生导入](../../../docs/import.md#8-前缀根与回合准备根)。

控制器、回合准备和整场回归使用的长期请求清单在 [`../scenarios/`](../scenarios/README.md)，与冻结 fixture 输入分开维护。
