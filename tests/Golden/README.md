# Golden：冻结快照校验器

`Golden` 通过 `ProjectReference` 引用 `src/Core/Sts2Solver.Core.csproj`，在只有 BCL 的进程里校验冻结快照 `tests/Golden/data`。它不引用游戏 DLL、Godot、Harmony 或 RitsuLib。

| 子命令 | 作用 |
| --- | --- |
| `k0` | `Rules` 编码合同、格式版本、五个魔数与 `Battle` 尺寸 |
| `verify <data>` | 先核对 manifest，再跑模拟回归和搜索回归；`--only simulation|search`、`--jobs N`、`--shard i/n` |
| `compare <data>` | 只报告搜索结果的变好、变差，不判失败 |
| `refreeze <src> <dst>` | 在副本上重算搜索回归的期望结果并重写 manifest |
| `rerecord <src> <dst>` | 在副本上重录指定的模拟回归根；`--roots`、`--reason` 必填，可加 `--recapture-root <dir>` |
| `manifest <data>` | 确定性地重写 `manifest.json` |

```bash
dotnet run --project tests/Golden -c Release -- k0
dotnet run --project tests/Golden -c Release -- verify tests/Golden/data
```

各子命令的用法、输出和适用条件见 [测试与验证](../../docs/testing.md#4-模拟回归与搜索回归)；数据格式、来源与更新规则见 [冻结快照](../../docs/golden-data.md)。

哈希函数按 M0 宿主的字段顺序显式移植（`GoldenHash.cs`），不依赖反射顺序。

`--only` 只接受 `simulation` 和 `search`。
