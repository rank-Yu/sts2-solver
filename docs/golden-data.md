# 冻结快照

[返回文档索引](README.md)

`tests/Golden/data` 是 Sts2Solver 的冻结证据：在固定输入上录下的标准答案，用来发现内核或搜索的行为变化。语料最初来自 CombatSolver，当前根状态和规则由 0.113.0 Beta 原引擎重捕获，模拟和搜索期望由本仓库重录。纯 Core 校验不依赖游戏程序集。

本文说明目录、格式、来源和更新条件。怎么运行校验见 [测试与验证](testing.md#4-模拟回归与搜索回归)。数据分两部分：模拟回归（目录 `d1`）和搜索回归（目录 `d2`），术语见 [文档索引](README.md#术语)。

`meta.json` 的 `input` 仍保存 CombatSolver 时期的 fixture 和语料路径（例如 `coverage/fixtures/cards/flash-dx5-pen-nib-sly.json`），那是出处，不是格式的一部分。

## 1. 目录结构

```text
tests/Golden/data/
  manifest.json
  objects/rules/<sha256>.br          # RULE 规则对象，按内容 SHA-256 去重
  d1/<root>/config.br               # 该根的 SREQ 配置（见第 7 节）
  d1/<root>/golden.br               # GOLD：根、K1、K2
  d1/<root>/meta.json               # 输入映射、计数、格式版本、Battle 大小
  d2/requests/<sha256>.request.br   # SREQ 请求，按完整请求字节去重
  d2/expected/<sha256>.result.br    # SRES 期望结果
  d2/replay-report-<shard>-of-<n>.json
  d2/rules-index-<shard>-of-<n>.json
```

`meta.json` 的 `root` 与目录名一致。一个 `d1` 目录可以记录多个根（例如回合准备 fixture），所以 manifest 里的根数和目录数不是同一个计数。

## 2. 压缩、哈希与格式版本

- 所有 `.br` 文件都用 `System.IO.Compression.BrotliStream` + `CompressionLevel.SmallestSize` 压缩。文件内容不含时间戳，同样的数据两次编码字节相同。
- `manifest.json` 里的 `sha256` 是对压缩后的文件字节计算的；校验器逐文件比较大小和 SHA-256。
- 状态哈希是打包前缀（`Fingerprint.Bytes`）的 SHA-256 截断到 64 位（`Codec.BattleHash`）。搜索里的指纹只用于分桶；去重在指纹命中后仍比较打包前缀，不允许只凭哈希合并。
- `SREQ` 请求按完整请求字节的 SHA-256 去重，`RULE` 按完整规则字节的 SHA-256 去重。规则对象和请求的文件名就是内容的 SHA-256，期望结果沿用对应请求的哈希作文件名；格式字节变化会改变这些名字。
- `BATL`、`RULE`、`GOLD` 使用版本 10（`Codec.FormatVersion`）；`SREQ` 使用请求版本 12，`SRES` 使用结果版本 6。遗物内部编号表包含拇指夹；新增内容导致编号变化时也必须升级格式，避免读取成另一件遗物。读取器只接受对应的当前版本，其它版本显式抛 `InvalidDataException`。

## 3. `BATL`：Battle

```text
u32 magic="BATL" | u16 version=10 | i32 payloadLength
i32 packedSize | 当前 Battle 打包前缀
u16 relicCount | relicCount × 12 字节固定 Relic 行
```

魔数常量是 `0x4C544142`（`BATL`）。打包前缀直接使用运行时布局：头部 2896 字节，加 `CardCount × 64` 字节；`BattleCodec.SerializedSize` 为最大前缀 43856 字节。状态哈希只覆盖此前缀。费用修饰容量为 10，Power 容量为 28，动态遗物容量为 24。

编码必须显式提供共享清单，解码通过 `out RelicRoster` 返回清单。固定清单上限 65535 件；有动态状态的行只保存固定字段，Counter、Aux 和战斗使用标记由 Battle 动态槽保存。清单的动态槽数必须与 Battle 一致。

解码检查魔数、版本、总长度、前缀大小与牌堆计数，以及内容 id 和各容量边界；固定清单不得夹带动态值。不接受旧布局。字段布局和规范化见 [状态布局](combat-state-layout.md)。

RULE 是内容规则表；根清单由 BATL 携带。SREQ 解码先绑定第一根清单，检查后续根等价后再构造评分器。GOLD 每根也保留清单，D1 验证时绑定对应规则上下文。

## 4. `RULE`：Rules

```text
u32 magic="RULE" | u16 version=10 | i32 fieldCount=32 | 32 个字段，按固定顺序
```

魔数常量是 `0x454C5552`（`RULE`）。32 个字段的含义和顺序见 [原生导入](import.md#41-32-个字段)。

- 卡牌数组按内部 Id 保存每个升级等级的 `CardStats`；Power、遗物变量按内部 Id 保存 `decimal` 数组；药水、怪物以“存在位 + 完整值”保存。
- 生成规则与卡池按键排序。`fieldCount` 必须同时等于 `Rules.SchemaFieldCount` 与 `RulesCodec.EncodedFieldCount`。
- `decimal` 写成 4 个 `int`（`decimal.GetBits`），保留 scale 和精确值；字符串用 UTF-8 字节长度做前缀。
- K0 合同对 32 个字段逐个变异，任何一个字段没写进字节都会失败；`decode → encode` 必须字节相同。

## 5. `GOLD`：模拟回归文件

```text
u32 magic="GOLD" | u16 version=10
string inputJson | string rulesSha256
rootCount | 每个根：i32 BATL 长度 + BATL 字节 + string choices
          + u64 scoreHash + u8 boundStatus + u64 boundHash
k1Count | 每个 K1：i32 rootIndex + 动作（无选择基动作）+ 选择数组
        + 选择请求数组 + u64 childHash + bool ended
        + u64 scoreHash + u8 boundStatus + u64 boundHash
        + bool traitPresent + u64 traitHash
walkCount | 每条游走：i32 rootIndex + i32 seed + stateCount
          每个状态：u64 stateHash + 合法动作数组 + 每个动作的 immediateStatus
                   + 并行 immediateRequests + bool hasStep
                 若有 step：基动作 + 选择数组 + stepRequests + u64 childHash
                            + bool ended + scoreHash/boundStatus/boundHash
                            + traitPresent/traitHash
```

魔数常量是 `0x444C4F47`（`GOLD`）。

- K1 对每个合法动作记录完整补全后的请求序列；K2 对每个状态记录枚举顺序的合法动作、第一次选择请求，以及随机选中动作的完整请求序列。
- 动作编码：`Kind, HandIndex, Target, PotionSlot, ChoiceCount, ChoiceCount × u64 选择位集`（不写结构体填充）。
- 选择请求编码：`Kind, Effect, Timing, Source, SourceIsNone, OptionsAreGenerated, OptionCount(u16), MinCount, MaxCount, IndexSelection(bool), OptionCount × 选项(u16)`；生成卡选项再写 `OptionCount × Card` 的原始 64 字节。`OptionCount` 和选项位置/选项值都是 2 字节；`IndexSelection` 用于总牌数超过 255、单选选项超过 64 的请求。
- `immediateStatus`：0 = 动作不需要选择；1 = 需要，并行的请求有效；2 = 内核边界（`UnsupportedException` 或 `CapacityException`）。
- K4 哈希：`scoreHash` 是 `ScoreFeatures` 全部当前字段的确定性 SHA-256 截断；`boundHash` 是 `BoundValues`；`traitHash` 是 `SearchRouteTraits + pure + declined + pocketwatch threshold/played`。模拟回归只验证内核能复现的部分（K1、K2 和这些哈希）。
- `meta.json` 记录 `formatVersion`、`battleSize`、`root`、`input`、`rulesSha256`，以及根、K1、游走、游走状态、转移的计数。当前版本 10、`battleSize` 43,856；校验只接受这一组值。

## 6. `SREQ` / `SRES`：搜索回归的请求与结果

### 6.1 `SREQ`

```text
u32 magic="SREQ" | u16 version=12 | rules 长度 + RULE | battles 数量 + 每个 BATL | options
```

魔数常量是 `0x51455253`（`SREQ`）。`options` 按 `SearchCodec.EncodeRequest` 的固定顺序写入全部确定值字段：主构造参数（宽度、节点上限、回合上限、软时间、初始最大生命）、用药政策、评分与排序、下界、各类策略开关等。字段清单以代码为准。

- `ScoreRules` 完整写入：每张卡每个等级的 `CardScore`、每个 Power 的 `PowerScore`、药水战略代价、成长额度与目标、遗物计数目标、战后治疗、Boss 生命减免、初始值、根计数、根药水，以及所有命名的 Power、卡牌、遗物、药水 Id。
- `Scorer` 的 base/offensive 评估方式作为两个显式布尔写在 `ScoreRules` 前面；setup/queen 仍是追加扩展。
- `BoundFeatures.Config` 完整写入所有开关、证书、数组和根药水。
- 请求尾部按固定顺序保存火焰限制、搜索偏好（无／女王／准备）、胜利目标和已有胜利路线信息，各可选值都有存在位。遗物计数目标槽下标统一为 `u16`。没有扩展标记扫描或缺省旧字段。
- 宿主回调 `Pacer`、`Trace` 不进入 SREQ。
- 回归重解不采用请求里的墙钟截止和节点/内存预算；`SearchBudgetPolicy.ApplyRegression` 把它们覆盖成固定回归参数（见 6.3）。

### 6.2 `SRES`

```text
u32 magic="SRES" | u16 version=6
BestRoute | BestIsVictory | BestScore(double bits)
ExpandedNodes | Transitions | Duplicates | TurnsSearched | PrunedIncumbents
CapacityPruned | CapacityDroppedChildren（按 key 排序） | EndedCompactions
EndedCompressedStates | BestRootIndex | PotionPolicySatisfied

```

魔数常量是 `0x53455253`（`SRES`）。

- 不写 `Elapsed`、`WorkerYieldCount`、`FrameRecoveryWaitCount/Duration`、`MaxObservedGcPause` 这类不参与路线选择的值。`BestScore` 用 `BitConverter.DoubleToInt64Bits` 精确保存。
- `CapacityDroppedChildren` 的键是数据格式的一部分，池容量区块的键是 `Beam.Pool`。

### 6.3 保留槽位与搜索回归参数

`SREQ` v12 与 `SRES` v6 没有保留槽位。

搜索回归不直接使用请求里的软时间和宿主回调，而是通过 Core 的
`SearchBudgetPolicy.ApplyRegression` 注入固定的回归参数：结束回合池保留宽度 × 8、触发宽度 × 16；
状态池预算和搜索内存预算都使用 2,576,980,377 字节。协调器用同一个
`SearchBudgetPolicy.Apply`，只是传入计划里的宽度倍数和内存预算。`MaxExpandedNodes`、`MaxTurns` 和
`UnlimitedNodes` 保持请求值。

## 7. `d1/<root>/config.br`

`GOLD` 只冻结了 `Rules`（`objects/rules` 的 SHA-256），没有冻结 `ScoreRules` 和 `BoundFeatures.Config`。所以每个模拟回归的根另存一个 `config.br`：一个 Brotli 压缩的 `SREQ` 请求，包含该根运行时完整的 `Rules`、`ScoreRules` 和 `BoundFeatures.Config`。请求不再有固定前缀计数；13 个旧前缀根按计数为 0 重录了对应的 score/bound 哈希。

校验时只从 `config.br` 读 `Scorer` 和 `Bound`，其余完全由 `Battle` + Core 重算；配置缺失时显式失败，不静默跳过。`config.br` 计入 manifest 的完整性检查和计数。

## 8. manifest 与 provenance

`manifest.json` 由 `Golden manifest` 确定性地重写，不手改。

| 字段 | 内容 |
| --- | --- |
| `formatVersion` | 9 |
| `battleSize` | 43,856，即 `Unsafe.SizeOf<Battle>()` |
| `provenance.d1` / `.d2` | M0 宿主的捕获提交 `75217006903195bc94654680423bb9ca580ec48a` |
| `provenance.d1Config` | 模拟回归配置的导出提交 `66a43838d89e7a03b05c0f04d5e4941ff555baec` |
| `provenance.formatMigration` | 当前格式迁移的来源与转换范围；记录版本 10 的当前布局、32 字段规则和 SREQ 12 固定顺序选项。模拟动作和搜索期望保留，仅转换表示及对应哈希 |
| `provenance.refreeze` | 由 `Golden refreeze` 写入：当前代码重算后的搜索回归结果集 SHA-256 |
| `provenance.d1Rerecord` | 由 `Golden rerecord` 写入：重录的根、原因和新记录的内容 SHA-256 |
| `compression` | `brotli` |
| `counts.d1` | 根、K1、游走、游走状态、转移、文件数 |
| `counts.d2` | 捕获调用、唯一请求、期望结果、可比较、相等、不等、受时间影响的计数 |
| `totalBytes` / `targetBytes` | 总字节与目标预算 |
| `files` | 按 `path` 序数严格递增的 `{path, bytes, sha256}` 列表 |

普通的 `manifest` 命令保留已有的 `provenance.refreeze` 和 `provenance.d1Rerecord`，不会把它们丢掉。

`provenance.d2` 保留最初 M0 捕获的提交；后续用 `Differential --capture-d2` 补录的成员请求及其生产根、设置记录在 [差分宿主](../tests/Differential/README.md#补录-d2-成员请求)，结果集由 `provenance.refreeze` 的 SHA-256 标识。

两套语义来源要分清：数据的语义参照 CombatSolver `a62f1850`，但快照本身由 M0 宿主在 `752170…` 捕获，两者不是同一个提交。模拟回归的 `config.br` 与 `golden.br` 来自同一次确定性运行。

当前冻结数据的来源记录保存在 `tests/Golden/data/manifest.json` 的 `provenance` 里；`d1Rerecord` 由 `Golden rerecord --reason` 写入，`refreeze` 由重冻工具写入，不手改。

当前冻结数据覆盖标准、恐惧和极速模式，包含愚者。D1 保留原有场景和游走种子；D2 同时包含当前生产协调器成员请求，以及前缀和回合准备根的固定预算请求。当前包含 404 个场景、414 个根、5,071 个一步分支、14,928 条游走（473,886 个状态、461,403 个转移），以及 2,094 个搜索请求及对应期望结果。精确计数和内容哈希以 manifest 为准。跨版本迁移的旧新计数、实机对照和验证记录保存在本地交接文档。

侧回合开始的终局门控由原版最小场景核对。对应的 D1 根 id-c-instanced-powers 使用工具重录，K1、游走状态和转移数量不变；这项修正不改变 D2 结果。

## 9. 重录与重冻会改什么

两个工具都先复制整棵数据目录，在副本上修改；源目录只读，目标目录必须为空，两者不能互相包含。命令见 [测试与验证](testing.md#5-更新快照)。

**重录（`Golden rerecord`）。** 原版语义修正后，旧录制里记的是修正前的内核行为。重录用当前代码重算指定的模拟回归根：

- 未列出的根保持原样。列出的根保留根状态、`config.br`、`meta.json` 的 `input` 与 `rulesSha256`，重算 K1 补全、K2 游走、`scoreHash`、`boundHash`、`traitHash`，并按新计数更新 `meta.json`。
- K2 只用 `GOLD` 里已记录的种子，不自行发明种子。游走策略来自 M0 宿主的 `tools/search/OfflineSearchHarness/Flash/FlashDifferentialProbe.cs`（CombatSolver `75217006`）：每条游走 `new Random(seed)`，每步按“4 次里 3 次偏向手牌”的规则抽取合法动作，再用同一个 `Random` 从 `ChoiceEnumerator.Enumerate` 的选择里抽；最多 60 步。K1 按 M0 的深度优先补全顺序，每个动作最多 64 个补全。
- 语义没变时，所有根的 `golden.br`、`config.br`、`meta.json` 都逐字节相同。
- 清单写入 `provenance.d1Rerecord`，格式是 `roots=<按序数排序的根名> reason=<原因> sha256=<每个 golden.br 解码后 GOLD 内容的 SHA-256 汇总>`；重新捕获根时另写 `recaptureRoots=<根名>`。

**重新捕获根（`--recapture-root`）。** 重录把根状态当作输入。如果语义修正改的正是“从原版状态导入或回放出的根”，就要先由差分宿主重新捕获根和 `config.br`，再交给重录替换 `golden.br` 的根段与 `config.br`。

**重冻（`Golden refreeze`）。** 用当前代码重解全部搜索回归请求，写回 `d2/expected`，把旧的 `replay-report-*.json` 合并为一份新报告，再重写 manifest，并在 `provenance.refreeze` 写入新结果集的 SHA-256。

## 10. 校验范围

- 模拟回归的 `golden.br` 和 `config.br` 来自同一套根输入；`meta.json` 记录输入映射、规则哈希和计数。
- 模拟回归重放比较状态、合法动作、选择、K1、K2 和 K4 哈希。
- 搜索回归对冻结请求重新求解，逐字段比较路线、分数、计数、容量丢弃和药水审计。确定性重放不用墙钟截止。
- manifest 核对逐文件的大小和 SHA-256、总字节数、计数和来源；数据的更新记录保存在 `provenance`。
- 时间、GC、帧压力和运行期路径只用于诊断，不写进确定性结果。

## 11. 什么情况下允许更新快照

默认冻结。只有下列条件同时满足时，才允许更新 `tests/Golden/data`：

1. 有意改变内核语义或搜索行为，而不是为了让测试通过；
2. 有实机或明确的游戏证据支持这次改变；
3. 经用户同意，并在同一提交中更新数据文件和 `manifest.json`；
4. 提交说明写清变更原因、影响范围（哪些根变了）以及新旧计数。

不允许：

- 手改 `.br` 里的字节或 `manifest.json` 的哈希来绕过校验；
- 只更新 `expected`，不更新对应的请求、规则或配置；
- 把未验证的实机行为写成已验证；
- 为了让当前代码通过而改数据格式的键、魔数或根名。

更新后运行完整的 `Golden verify`，确认模拟回归 `mismatched=0`、搜索回归全部相等；重写 manifest 后确认 `git diff` 只包含有意的变更。

**不能改的名字。** 下面这些名字里带着 `Flash` 或 CombatSolver 的旧路径，清理命名时不要动：

- 卡牌 `FlashOfSteel` 与内容 Id `FLASH_OF_STEEL`：这是原版卡牌的名字；
- 质量语料的随机种子 `FLASH-QUALITY-<角色>-<NNN>`：换了种子就是另一批牌组；
- `meta.json` 的 `input`、移植文件的来源头、提交哈希和固定的 GitHub 链接（CombatSolver 出处）。

## 12. 旧版本

读取器只接受当前版本：`BATL`、`GOLD`、`RULE` 为 9，`SREQ` 为 12，`SRES` 为 6。更早的 `BATL`、`GOLD`、`SREQ`、`SRES`、`RULE` 都不再支持，旧格式会因版本号不符显式抛 `InvalidDataException`。格式迁移规则由 [重录与重冻](#9-重录与重冻会改什么) 和 `provenance.formatMigration` 记录。
