# 质量语料

本目录是整场搜索质量评测的输入，共 123 个：

- **120 个生成牌组**：五个角色各 24 个（`<角色>-000` … `<角色>-023`），每个子目录是一份 `spec.json` + `request.json`，`request.json` 的 `generatedScenarioPath` 指向本目录内的规格。
- **3 个固定牌组**：两份女王（`SILENT-QUEEN-*`）和一份沙漏（`SILENT-AEONGLASS-CG7UX7V5XZS9`），每个子目录是 `request.json` + `expectations.json`。
- **大牌组回归输入**：`CLONE-289-AXEBOTS` 是 33 张原牌组加 256 张升级版 `NEUTRALIZE`（带 `CLONE` 附魔）的 289 张牌组，固定第三幕普通战斗 Axebots，用于 640 容量与 `IndexSelection` 的长期实机回归；不进入默认 108 场名单。

默认评测名单在 `default-roots.txt`，共 108 场：生成牌组去掉第三幕首领的 15 场（各角色的 017、020、023）后剩 105 场，再加 3 个固定牌组。去掉的 15 场仍保留输入，可以用 `--root` 单独跑，也供冻结快照的一致性检查使用。

## 生成牌组

- **编号规则**：序号 000–007 是第一幕，008–015 是第二幕，016–023 是第三幕；遭遇按序号 `i % 3` 依次取普通、精英、首领，所以每个角色普通、精英、首领各 8 场，合计各 40 场。
- **生成口径**：生成器在 CombatSolver 仓库，固定提交的说明见 [FlashRegression README](https://github.com/Torch1230/CombatSolver/blob/a62f1850c8586e5951e222eecb0dd34231e76d8a/tools/search/FlashRegression/README.md#survey)。固定进阶 10，`includeStartingDeck`、`includeStartingRelics`、`includeAscendersBane` 为真；额外角色卡 `6 + i % 7` 张（升级 1 级），遗物 `2 + i % 4` 个，无色牌 `(i // 3 + 1) % 3` 张，药水 `(i // 3) % 3` 瓶（后两项不随遭遇种类同步变化）。生成多少收多少，不按 Sts2Solver 的准入规则筛选，没有重复规格。
- **种子名不能改**：种子是 `FLASH-QUALITY-<角色>-<NNN>`。它是生成牌组的随机种子，换了名字就是另一批牌组。

## 固定牌组

两份女王和沙漏请求使用固定的已有牌组、遗物、附魔、生命与金币，走原版开战流程；它们是按同样配置重建的开局，不是原存档的完整随机数状态。`ownedLoadoutForTest` 要求同时设置 `clearRunDeck` 和 `preserveNativeCombatStateForTest`：先恢复牌组，再恢复没有获取效果的遗物，避免对已拥有的牌再次触发升级；生命和金币在进入战斗前设置。

沙漏使用种子 `CG7UX7V5XZS9`、进阶 10、75/75 生命、35 金币、无药、34 张牌、壶铃举重 3 次、余像迅捷 2，与保存的用户配置一致。

`expectations.json` 指定档位、胜利要求和最低剩余生命。中档整场结果没达到要求或出现部署偏差时返回失败；其它档位不套用中档的阈值。质量期望用来提示已知路线的退步，不代替维护者决定是否接受策略取舍；维护者确认接受的质量变化要同步更新期望，原版核对与前后对照记录保存在本地。

## 运行

```bash
dotnet build tests/Differential/Differential.csproj -c Release
dotnet artifacts/bin/Differential/release/Differential.dll --list-quality-roots
dotnet artifacts/bin/Differential/release/Differential.dll --offline-combat --search-tier Medium --jobs 4 --out .local/quality
```

默认读取 `default-roots.txt`，不要求新根先有冻结快照。也可以用 `--root quality-SILENT-QUEEN-CG7UX7V5XZS9` 或 `--roots-file` 显式指定集合，包括不在默认名单里的根。自定义语料目录没有名单时，按其中的 `request.json` 枚举。

报告结果时，分别列出 105 个生成牌组和 3 个固定牌组，并注明总数 108。结果和实验记录保存在本地，不提交到本目录。
