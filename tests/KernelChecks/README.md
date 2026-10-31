# 纯内核合同

通过 `ProjectReference` 引用编译后的 `Sts2Solver.Core`，不依赖游戏程序集或 Sts2Solver 的其它代码。

```bash
dotnet run --project tests/KernelChecks -c Release
```

检查项清单和它证明什么，见 [测试与验证](../../docs/testing.md#32-内核合同k0)。差分正确性不在本项目里检查。

输出 JSON 里的 `benchmark` 是在纵切内容上跑一段类似束搜索的负载（复制、执行、哈希分别计时）和测量循环的托管分配量，只作为内核性能的回归参考，不代表完整搜索的耗时。比较这项数字时，可以先设置 `DOTNET_TieredCompilation=0`（PowerShell 里是 `$env:DOTNET_TieredCompilation = '0'`）关闭分层编译，减少预热带来的抖动；合同检查本身不需要它。
