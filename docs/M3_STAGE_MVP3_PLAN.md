# M3.MVP3 中转站入站 MVP 发布与真机验收计划

> **状态（2026-09-05）**：已完成并按用户指定归档。Checkpoint 0–4、数据保护、Release 468/468、独立候选及 Explorer/桌面代表矩阵通过；用户因时间限制实际完成 5 组共 50+ 次稳定性操作且均无异常，不将其写成计划的 100 次。随后发现的活动 Explorer 拖动反复显隐风险已延期为 `KNOWN_ISSUES.md` KI-001；相关临时修正/测试和 DWM/OLE Gate 实验代码均已移除，源码恢复到问题报告前的 M3.MVP3 行为。下一步进入 M3.1。

## 1. 阶段目标与完成边界

M3.MVP3 不增加中转站能力，只把已经完成的 M3.MVP1 数据内核和 M3.MVP2 UI/唯一入站 Router 验收到真实用户路径：

```text
锁定源码与数据基线
  → 在线备份并在副本回放迁移/幂等
  → 隔离 Release 全量回归与真实 WPF 渲染
  → 生成独立 win-x64 自包含候选
  → 精确替换当前实例
  → Explorer / 桌面真实拖动中呼出并在 Stage 松手
  → 核对展示、重启保留、源文件零改动与资源回落
```

全部门禁通过后，只能宣布：

> 文件/目录/`.lnk` 的“外部拖动中呼出 → Stage 松手 → 持久展示”入站 MVP 可用。

不得据此宣布点击取用、仅复制、拖出、消费、固定、排序、打开/定位、移除、重新关联、文本、图片或完整 M3 文件切片可用。

本阶段允许的仓库变更只有：

- 与 M3.MVP3 证据直接相关的只读审计/记录脚本；
- 为 100 轮生命周期、发布构成或已存在合同补充的自动门禁；
- 发布和验收结果文档；
- 物理验收真实复现故障后，单独诊断并修复根因所必需的窄代码变更。

最后一项不是预授权重构。若未复现产品故障，不修改 `WorkspaceFileDropRouter`、`StageViewModel`、`StageView`、`WorkspaceController`、schema 7 或 Stage 仓储合同。

## 2. 规划期已核验的真实基线

### 2.1 源码、进程和构建

- 规划开始时 HEAD 为 `40a48c2 feat(stage): add MVP2 workspace inbound UI and routing`，工作区只有既有未跟踪 `.codex/`；本规划随后只新增/更新文档与 APC 状态。
- 当前 Vok 为 PID 24292，路径是 `src\Vok.App\bin\Release\net10.0-windows\Vok.exe`；进程启动于 2026-09-04 21:38:51，`Vok.exe` 最后写入时间为 21:38:50。
- 对构建产物做只读字符串/符号核对，可见 `Vok.Features.Stage.StageViewModel` 与 `Vok.Hosting.WorkspaceFileDropRouter`；因此它不是旧 MVP1 二进制。
- 该产物生成早于提交约 6 分钟。它包含当前 MVP2 功能，但本阶段仍须从锁定 HEAD 重新生成独立发布候选，不能把默认 `bin` 目录当可追溯发布物。

### 2.2 真实数据库

规划期使用 `Microsoft.Data.Sqlite` 的 `Mode=ReadOnly`、private cache、pooling off 打开正在运行的数据库，只输出结构、完整性和计数，不输出用户正文或路径：

| 项目 | 结果 |
|---|---:|
| `PRAGMA user_version` | 7 |
| `PRAGMA quick_check` | `ok` |
| 外键违规 | 0 |
| `app_settings` | 2 |
| `launcher_items` | 35 |
| `clipboard_entries` | 100 |
| `clipboard_representations` | 102 |
| `clipboard_files` | 5 |
| `content_assets` | 48 |
| `clipboard_asset_refs` | 48 |
| `stage_items` | 1 |

`stage_items` 同时存在 `ux_stage_items_canonical` 与 `ix_stage_items_order`。这一条 Stage 数据的来源没有在审计中读取，不能仅凭计数声称 Explorer/桌面物理路径已经通过。

数据库当前处于 WAL 模式且有活动的 `vok.db-wal`/`vok.db-shm`。主 `vok.db` 文件时间和裸文件复制都不能代表最新一致状态；M3.MVP3 必须使用 SQLite Online Backup API 取得已提交快照，禁止只复制 `.db` 或拼装 `.db/.wal/.shm`。

### 2.3 可用历史样本

`%LOCALAPPDATA%\Vok\backups` 当前只有 `vok-pre-m24-20260902-112140.db`，它是 M2.4 前的 schema 5 备份，没有真实 schema 6 备份。由此产生三个互补而不可混称的门禁：

1. **当前真实 schema 7 在线快照**：证明现有真实数据可完整备份、由当前代码重复打开且 Stage 数据不变；它证明幂等和当前健康，不证明首次 6→7。
2. **真实数据派生 schema 6 回放**：只在当前在线快照的第二份副本中删除 schema 7 唯一新增的 Stage 表/索引并把 `user_version` 设为 6，再由当前 `SqliteStorageExecutor` 升级；它证明当前 Launcher/Clipboard/资产真实数据形状上的 6→7 行为，但必须标注为“派生回放”，不能冒充历史原件。
3. **既有真实 schema 5 备份直升**：只复制历史备份后运行当前初始化，证明真实旧数据可完成 5→6→7 连续升级；它不能证明 2026-09-04 首次迁移当时的精确现场。

自动测试中的忠实 schema 6 夹具仍是故障注入和事务回滚的权威可重复证据。以上真实副本只补足数据规模/形状和发布构成，不替代自动反例。

## 3. 不变量与数据保护原则

### 3.1 真实数据

- 所有审计默认只读；写操作只允许指向经解析后位于本次 `.artifacts` 运行目录内的副本。
- 在任何进程替换之前，对实时数据库执行 Online Backup 到新文件；目标必须 `CreateNew` 语义，存在即失败，不覆盖旧备份。
- 在线备份前后读取同一源连接的 `PRAGMA data_version`；未变化时才要求源与备份的计数/聚合摘要精确相等，若期间有其他连接提交则先审计备份自洽性并在用户停止产生新剪贴板内容后重做一次。备份完成后立即执行 `quick_check`、外键检查、表/索引结构和不含正文的顺序摘要；未通过则停止，不启动候选。
- 迁移副本前后对 `app_settings`、Launcher、Clipboard 表、资产表分别生成稳定 SHA-256 摘要；schema 6→7 回放中它们必须逐表不变，Stage 只允许从不存在变为空表。
- 资产核验从数据库相对路径映射到 `%LOCALAPPDATA%\Vok\assets\clipboard`，比较长度和 SHA-256，只在报告中保存总数、总字节、缺失数和聚合摘要，不输出用户文件路径。
- 候选运行后的实时审计允许剪贴板监听产生合法新历史，因此不要求整库摘要与启动前相同；要求 schema/外键/资产引用一致，Stage 变化只对应本轮已确认的 Drop。
- 不自动恢复整库备份。若候选后出现数据异常，立即停止应用并保留现场；恢复会覆盖期间新增的剪贴板/Stage 数据，必须另行取得用户确认。
- 不运行旧 schema 6 二进制打开当前 schema 7 数据库，不通过改 `user_version` 降级真实库。

### 3.2 用户文件

- 物理测试只使用用户认可且愿意长期保留在 Stage 中的现有文件、目录和 `.lnk`。MVP 尚无移除动作，不用即将删除的临时源污染真实 Stage。
- 稳定性循环反复拖入同一个 canonical 文件并交替成功/取消，利用去重回顶验证生命周期；不为 100 轮制造 100 个条目。
- 文件和 `.lnk` 在测试前后比较长度、最后写入时间和 SHA-256；目录选择小型、稳定目录，比较相对项数、总字节和按相对路径排序后的聚合 SHA-256。报告只保存摘要。
- 容量满、路径中途失效、Shell 解析异常和 SQLite 提交失败只在 `%TEMP%`/`.artifacts` 隔离库与受控 `IDataObject` 中验证，不以填满或破坏真实库换取“物理覆盖”。Explorer/桌面物理矩阵负责证明真实 OLE、热键呼出、区域命中和持久展示。
- Launcher 回归若产生测试卡片，只通过 Launcher 已有“移除”命令清理元数据；清理前后都确认源文件未被删除。

### 3.3 OLE、窗口和观察者

- Explorer/桌面物理拖动必须由用户真实按住鼠标完成；合成鼠标消息、测试 `IDataObject` 或 WPF 直接调用不能冒充物理验收。
- 记录器只观察固定 PID/HWND、数据库只读摘要和窄资源计数，不在热循环枚举全部窗口/进程树，避免重现 R9 的观察者停顿。
- 记录器启动即创建 `reportVersion=1` 的结果并逐检查点持久写入；挂接、超时、用户取消或进程退出也必须留下阶段和错误，不能只在全部成功后生成报告。
- 任何成功条件都以真实结果为准：当前区域唯一蓝框、Drop 后 Stage 状态/条目、隐藏/重启后重新读取。`Copy` 光标只表示 OLE 接受，不表示 SQLite 已提交。

## 4. 证据工具

M3.MVP3 实施时先增加两个范围受限的 PowerShell 工具，不把诊断开关接入产品 UI：

### 4.1 `tools/Probe-M3StageData.ps1`

职责：对显式路径执行只读审计、在线备份或副本迁移回放，并输出 JSON。

约束：

- `-DatabasePath`、`-AppAssemblyDirectory` 和 `-OutputPath` 必须显式传入；不隐式猜测产品路径。
- `Audit` 使用 `SqliteOpenMode.ReadOnly`；不得设置 WAL、执行初始化或创建缺失目录。
- `Backup` 使用 SQLite Backup API；目标存在、与源同路径或不在允许的备份/运行目录时拒绝。
- `ReplaySchema6`/`ReplaySchema5` 只接受位于本次 `.artifacts` 根内的副本；先核验来源版本和结构，再改变副本。
- 报告包含版本、结构、计数、摘要、资产一致性、`quick_check` 和外键结果，不包含正文、文件列表或可逆路径。
- 每次操作先写 `Started` 报告，再逐阶段原子替换结果文件；失败报告保留异常类型和阶段。

脚本本身必须通过 PowerShell AST 解析；另用临时 WAL 数据库证明在线备份包含已提交 WAL、目标已存在时不覆盖、只读审计不改变源文件、回放拒绝真实产品路径。

### 4.2 `tools/Record-M3StageInbound.ps1`

职责：在用户完成物理步骤时固定观察候选 PID，记录开始/检查点/结束资源与数据库摘要。

至少记录：

- 候选绝对路径、文件 SHA-256、HEAD、PID、启动时间和固定顶层 HWND；
- 每 10 轮的 Working Set、Private Bytes、线程、Handle、USER/GDI；
- 呼出前后 Workspace 可见/前台/Topmost/输入就绪诊断；
- 每个检查点的期望、用户结果、Stage 总数/顶部摘要、错误状态和是否需要下次呼出确认；
- 最终 `quick_check`、外键、Stage/资产一致性与进程仍响应结果。

脚本不模拟拖动、不替产品决定成功，也不因单次采样波动硬判内存泄漏。资源结论看稳定平台和增长斜率；线程、句柄或 GDI/USER 连续多个检查点同向增长时暂停并诊断。

## 5. 实施顺序

每个检查点只改变一个主要变量；任一点连续失败两次，回到数据、所有权或事件时序假设，不叠加延时、吞异常或第二套路由。

### Checkpoint 0：冻结当前事实

1. 读取 `.apc/manifest.md`、当前状态和本计划；确认 HEAD 与工作区边界。
2. 精确记录所有 Vok PID、路径、启动时间及候选前二进制哈希。
3. 对真实库重做严格只读审计，确认版本不低于规划基线；若结构、`quick_check` 或外键不一致，停止。
4. 记录真实 Stage 当前计数，不读取或打印路径。后续只允许由已确认物理 Drop 解释增量。

### Checkpoint 1：数据探针与副本门禁

1. 先在临时 WAL 数据库验证 `Probe-M3StageData.ps1` 的只读、在线备份、拒绝覆盖和失败留证。
2. 对实时产品库创建时间戳在线备份，审计备份与源的结构、计数和聚合摘要。
3. 从在线备份产生两个运行副本：当前 schema 7 幂等副本、派生 schema 6 回放副本。
4. 对 schema 7 副本用当前执行器初始化两次；全部表摘要和 Stage 1 条摘要必须保持。
5. 对派生 schema 6 副本执行一次 6→7 和第二次幂等初始化；非 Stage 表逐表摘要保持，Stage 新表为空且索引/排序契约正确。
6. 复制既有 schema 5 历史备份并直升 schema 7；核验旧 Launcher/Clipboard/资产元数据、`quick_check`、外键和可验证资产引用。
7. 任一副本失败都保留原件和失败副本；不得回头操作真实库验证猜想。

### Checkpoint 2：自动回归与 WPF 证据

1. 使用独立 `ArtifactsPath` 在 `%TEMP%` 执行 Release 全量测试和构建，不覆盖当前运行的默认 Release 文件。
2. 保持现有 466 项全部通过；新增探针和 100 轮门禁后测试数只能增加，不能用过滤器隐藏回归。
3. 补充 100 轮 Router 成功/取消/替换交替：每轮终态后 active target、两区视觉、Workspace drag lease 和待分发任务均归零；每个 Drop 仍最多一次格式探测/一次数组快照。
4. 补充 Stage 仓储/VM 100 次同路径回顶与取消组合，最终仅一条、revision/顺序单调合法、固定页面不超过 64、执行器队列可继续接单。
5. 通过 fault fake 保持容量整批拒绝、混合有效/无效、Shell 异常、SQLite 提交失败、提交成功但刷新失败及隐藏后下次呼出可见状态。
6. 设置 `VOK_CAPTURE_DIR` 重跑真实 WPF Stage 测试，目视核验浅色、深色、520 DIP 和 260×408 DIP；截图只来自真实 WPF 渲染。
7. `dotnet build`、所有测试、PowerShell AST 和 `git diff --check` 必须为零错误；编译警告仍按错误处理。

### Checkpoint 3：发布候选

1. 从同一锁定 HEAD 发布 `win-x64` 自包含、非裁剪、非 Native AOT、非单文件、`PublishReadyToRun=false` 候选到新的 `artifacts/m3-stage-inbound-mvp-<timestamp>-publish`。
2. 发布目录不得是当前 PID 路径，也不得覆盖既有归档；目标存在即失败。
3. 记录 SDK、RID、HEAD、命令、完整文件清单以及 `Vok.exe`、`Vok.dll` 和原生 SQLite 的 SHA-256。
4. 从发布物核验 WPF XAML、Stage 类型、Router 类型、SQLite native x64 资产和依赖存在；直接启动后必须保持单进程、托盘、热键、剪贴板监听和隐藏主窗。
5. 若联网仅因 NuGet 漏洞审计不可达，可在包版本和 lock 状态不变的前提下，仅对本次还原关闭 audit 并记录；其他还原失败不得绕过。

### Checkpoint 4：受控替换

1. 确认 Checkpoint 1 的在线备份可读，Checkpoint 2–3 全绿后才进入。
2. 优先通过产品退出路径让 PID 24292 正常收口；只有明确超时后才精确停止已核验路径/PID，不按进程名批量结束其他实例。
3. 从独立发布目录以隐藏窗口启动候选，记录新 PID，并核验实际 `Process.Path` 和文件哈希等于候选。
4. 启动后只读审计真实库：schema 7、`quick_check=ok`、0 外键违规、Checkpoint 0 记录的原有 Stage 引用仍在；若此时失败，停止候选并保留现场。
5. 核验托盘登记、当前已配置快捷键、一次普通呼出/隐藏与剪贴板监听仍响应。该烟雾只证明候选可运行，不代替拖入。

### Checkpoint 5：Explorer/桌面物理矩阵

按下列顺序逐项完成，每一步都先记录源摘要，再由用户真实拖动：

1. **Explorer 单文件**：隐藏态开始拖动，保持鼠标按下，用当前快捷键呼出；进入 Stage 时只亮 Stage，松手后显示处理中，再在当次或下次呼出看到一条权威条目。
2. **Explorer 目录与 `.lnk`**：分别重复；`.lnk` 卡保留快捷方式自身，不显示为解析后的目标。
3. **Unicode 与批量**：使用愿意长期保留的现有 Unicode 路径和一组小批量对象；核验批内相对顺序和每个有效对象只出现一次。
4. **重复回顶**：再次拖入已有文件；总数不增加，该项回到首位并跨隐藏保持。
5. **跨区命中**：同一拖动在 Launcher、Stage、无目标区域间来回；任一时刻最多一个蓝框，最终 Stage Drop 不写 Launcher。
6. **取消终态**：在 Stage 命中后移出到无目标、按 Esc、用显式热键关闭以及开始新 DragEnter 取代旧会话；每次蓝框、交互暂停和 Workspace lease 都归零。
7. **桌面来源**：至少对一个文件和一个 `.lnk` 重复核心“拖动中呼出 → Stage Drop”路径。
8. **100 轮稳定性**：对同一个已存在 canonical 文件执行 50 次成功重复回顶和 50 次 Esc/无目标取消，交替进行；每 10 轮采样，结束后 Stage 只保留一份该引用，应用、热键和剪贴板持续响应。
9. **重启保留**：正常退出候选并从同一发布目录重启；本轮新增/回顶结果仍按权威顺序显示，无加载错误。
10. **源文件核验**：所有文件、目录和 `.lnk` 的测试前后摘要一致，数量、内容和路径没有被 Vok 改动。

真实库已有 1 条 Stage 数据且 MVP 无移除功能，因此不在这里填满 50 条或制造会删除的临时源。容量、混合无效和提交故障以 Checkpoint 2 的隔离反例作为完成证据，并在最终结论中明确其不是 Explorer 物理破坏测试。

### Checkpoint 6：收尾审计与状态更新

1. 对真实库做只读收尾：schema 7、`quick_check=ok`、0 外键违规、Stage 唯一 canonical、排序索引有效；Stage 增量只能对应用户确认的物理 Drop。
2. 核验内容资产 Ready/引用/实际文件长度与 SHA 一致，0 未解释 staging；不得因 Stage 文件引用去扫描或读取用户源文件正文。
3. 对比 100 轮资源样本：不要求单点完全相等，但不能出现线程、Handle、USER/GDI 或 Private Bytes 随轮次持续单调增长且不形成平台。
4. 核验 Launcher 拖入、普通显隐、焦点返还、托盘与剪贴板最小烟雾；全量自动测试保持通过。
5. 更新 `M3_STAGE_PLAN.md`、`ARCHITECTURE.md`、manifest/rules/memory 与本文件结果。状态只能写“入站 MVP 完成”，下一步进入 M3.1 管理动作。
6. 发布候选通过后保持其运行；若失败，停止候选并重启已知 schema 7 兼容的上一构建。任何数据库恢复仍需用户单独确认。

## 6. 自动验收矩阵

### 数据与迁移

- live read-only audit 不创建缺失数据库、不执行 SQL 写入，也不改变 `user_version`、schema 或业务行；WAL/SHM 的内部读事务状态和文件时间不能用作业务变更判据；
- 在线备份在 live WAL 下得到一致已提交状态且自身 `quick_check=ok`、0 外键违规；只有 `data_version` 证明期间无并发提交时，才要求源/备份结构、计数和聚合摘要精确一致；
- schema 7 副本两次初始化幂等，Stage 现有行不变；
- 派生 schema 6 副本升级只新增空 Stage 表/两个显式索引并写版本 7，非 Stage 摘要不变；
- schema 5 历史备份直升 7，Launcher/Clipboard/资产保持，`total_bytes`/Stage 结构正确；
- 缺表、错列、错唯一性/排序索引、未来版本和索引名冲突继续原事务拒绝；
- 所有成功副本 `quick_check=ok`、0 外键违规，失败副本保留前态。

### Router、Stage 与生命周期

- 100 轮 Enter/Over/Drop/Leave/Cancel/Replace 后没有 active drag/target/overlay；
- Enter/Over 不读取外国数据，Drop 每轮最多一次 `GetDataPresent` 和一次 `GetData`，冻结目标后不改投；
- Drop 先认领并清视觉，异步 Shell/SQLite 随后完成；异常被观察且不重写已返回的 OLE 结果；
- 同路径 100 次只保留一条并回顶；容量并发由 SQLite 事务权威，批量拒绝零部分写入；
- Stage 固定 64 项页面、keyset 前后导航和 WPF Recycling 保持；
- 设置、隐藏、退出、新 DragEnter 与重复终态继续幂等收口。

### 发布构成

- Release 全量测试不少于进入阶段的 466 项且 0 failure；
- Release 构建 0 warning / 0 error，脚本 AST 与 `git diff --check` 通过；
- 浅/深真实 WPF 和 260×408 DIP 布局目视通过；
- `win-x64` 自包含、非裁剪/非 AOT/非单文件候选从独立目录启动；
- 发布物的 WPF、Stage、SQLite、Shell/COM、热键、托盘和剪贴板依赖齐全。

## 7. 物理验收判定

必须全部满足：

- Explorer 与桌面都真实完成拖动中呼出和 Stage Drop；文件、目录、`.lnk`、Unicode 与批量有代表性覆盖；
- Launcher/Stage/None 命中互斥，取消、隐藏、替换和重启后没有蓝框或租约残留；
- OLE `Copy` 与持久化结果分开表达；成功条目当次或下次呼出可见，正常重启后仍在；
- 重复只回顶不增加，源文件/目录/`.lnk` 摘要在成功和取消前后完全一致；
- 100 轮成功/取消交替后应用各模块响应，资源形成稳定平台，无持续单调增长；
- 真实数据库最终 `quick_check=ok`、0 外键违规，Stage canonical/排序索引和资产引用一致；
- 既有 Launcher 外部拖入与宿主显隐最小真机回归通过，全量自动测试仍通过。

以下任一项都阻止完成：

- 只有测试 `IDataObject` 成功，没有 Explorer/桌面真实拖动；
- 蓝框消失但 Workspace lease、目标或交互暂停仍残留；
- Drop 显示 Copy 后没有条目且无可观察错误；
- 依赖重启、固定等待、强制 GC、第二个 `AllowDrop`/Router 或同步读取 DragOver 才能稳定；
- 真实库或源文件出现无法解释的变更；
- 以 schema 7 当前健康冒充首次真实 schema 6→7 现场已被完整记录。

## 8. 失败分流与回滚

| 失败点 | 处理 |
|---|---|
| live 只读审计不健康 | 停止，不创建候选、不替换进程；保留只读报告 |
| 在线备份/副本摘要不一致 | 停止，修正备份/审计算法；不操作源库 |
| 副本迁移失败 | 在失败副本诊断 schema/事务；不得对真实库重试猜测 |
| 自动测试/WPF/发布失败 | 保持当前实例运行，只修复可复现根因后从 Checkpoint 2 重跑 |
| 候选启动失败 | 停止候选，重启上一 schema 7 兼容构建；不自动还原数据库 |
| Explorer/桌面拖入失败 | 保存当步报告和源摘要，停止矩阵；最小复现事件链后加自动反例 |
| Copy 已返回但异步落库失败 | 保持源文件不动，核对状态可观察性；不重放 Drop |
| 资源持续增长 | 固定 PID/HWND 复测并定位所有权；不以 GC/延时掩盖 |
| 数据完整性异常 | 立即停止应用、保留现场和在线备份，恢复前请求用户确认 |

修复任何物理故障时都必须解释实际机制，并自审迟到终态、双执行、跨实例干扰、错误路径不一致和资源释放；验证既要证明原机制不再触发，也要证明故障没有被移动到异步刷新、下次呼出或重启路径。

## 9. 阶段交付物

执行完成时应得到：

- `tools/Probe-M3StageData.ps1` 与其临时库安全门禁；
- `tools/Record-M3StageInbound.ps1` 与 `reportVersion=1` 真机记录；
- 一份不覆盖历史文件的实时 SQLite 在线备份；
- schema 7 幂等、派生 schema 6→7、历史 schema 5→7 三份副本报告；
- Release 全量测试/构建、PowerShell AST、WPF 浅深/窄列截图；
- 带 HEAD、RID、文件清单和 SHA-256 的独立自包含发布候选；
- Explorer/桌面矩阵、100 轮资源曲线、重启保留和源摘要记录；
- 只声明入站 MVP 范围的最终文档状态。

证据默认写入被 `.gitignore` 排除的 `.artifacts/m3-stage-mvp3-<timestamp>/` 或 `artifacts/`，不把真实库、副本、用户路径、截图中的敏感内容或大日志提交到 Git。仓库只提交可复用工具、自动测试和不含用户内容的结论。

## 10. 执行记录（2026-09-04）

- 新增 `tools/Probe-M3StageData.ps1`。显式路径/允许根约束、PowerShell AST、目标已存在不覆盖、真实库路径拒绝回放和在线备份均通过；实时备份为 `%LOCALAPPDATA%\Vok\backups\vok-pre-m3mvp3-20260904-221906.db`，源在备份期间稳定，源/备份逻辑摘要精确相同，备份自身 `quick_check=ok`、0 外键违规。
- 三组副本门禁通过：当前 schema 7 连续初始化两次后全部业务表/结构摘要与 Stage 1 条不变；当前真实数据派生 schema 6 升级后仅新增空 Stage 结构，非 Stage 摘要不变；既有真实 schema 5 备份直升 schema 7 后 Launcher 34、Clipboard 90、资产元数据 19 条保持且 Stage 为空。历史 schema 5 备份没有配套资产目录快照，当前资产树只能核验其中 8/19 个旧资产、11 个已不存在，因此这里只证明旧资产**元数据**迁移，不声称历史资产文件完整；当前实时 schema 7 的 48/48 Ready 资产则全部通过长度和 SHA-256。
- 新增两项 100 轮自动门禁：Router 交替 50 次 Drop/50 次 Cancel 后会话、目标和视觉均归零；Stage VM/真实 SQLite 交替重复回顶与预取消后只保留一个 canonical 条目、revision 为 50、页面不超过 64 且执行器可继续接单。最终 Release 为 Core 71/71、Windows/WPF 集成 397/397，共 468/468；独立构建 0 warning/0 error，10 个 PowerShell 脚本 AST 与 `git diff --check` 通过。
- 真实 WPF 浅色、深色、完整工作台及 260×408 DIP Stage 截图已生成并目视通过。产品源目录相对锁定 HEAD `40a48c2136de7a6514f840a9857bb66d5b1da819` 无差异。
- 独立候选位于 `artifacts/m3-stage-inbound-mvp-20260904-222705-publish`：`win-x64` 自包含、非裁剪、非 AOT、非单文件、ReadyToRun 关闭，共 415 文件/150,497,661 bytes。`Vok.exe` SHA-256 为 `E469A6963D44B919EC8DE1D8EDE6F7418D8CB2788E3EF3C532F833789A557800`；完整清单保存在忽略目录的 `publish-manifest.json`。
- 精确停止原源码 Release PID 24292 后，只从上述候选路径启动 PID 2664；路径、哈希、单实例和启动后实时库审计通过。只读记录器 PID 20924 已固定观察该候选，报告位于 `.artifacts/m3-stage-mvp3-20260904/physical-session-20260904-2233/`。它只记录资源/窗口/数据库摘要，不模拟拖放。
- 用户从 Explorer 对一个含 HTML/JPEG/PDF/LNK 的四文件目录完成批量入站，并从桌面完成整个目录入站；随后完成 `.lnk` 重复回顶、JPEG 命中后 Esc 取消，以及 HTML 在 Launcher/Stage/None 间切换后于 Stage 松手。用户确认各步蓝框、取消和条目结果全部通过；只读审计证明 Stage 从 1 条精确变为 6 条，重复/取消未额外增项，schema 7、`quick_check=ok`、0 外键违规和 48/48 Ready 资产仍健康。测试目录 4 文件/552,368 bytes 的内容、长度、文件时间戳与根目录时间戳前后摘要完全一致。
- 因时间限制，用户随后完成 5 组、合计 50+ 次同一 `.lnk` 的成功回顶/取消交替，报告均无界面或交互异常；不能把它写成计划的 100 轮。记录器正常完成 225 个固定 PID/HWND 样本：一次性 Handle 阶跃后连续 104 个样本/8.5 分钟没有继续增长，最后 60 个样本/4.85 分钟 Handle `-19`、GDI `0`、Private Bytes `+0.75 MiB`，形成平台；该一次性首用分配尚未归因，不标记为泄漏或已定位根因。Stage 最终仍为 6 条，数据库和 48/48 Ready 资产健康。
- 验收后另发现“活动 Explorer 拖动中呼出再关闭”可能重新暴露源目录，并在带 Ctrl 修饰的鼠标松开时生成同目录 `.lnk` 副本。临时关闭抑制虽然保护源摘要，却导致拖动时无法关闭；DWM cloak Gate 也因失去 OLE 目标命中而失败。用户决定不让该专项阻断第三阶段：源码恢复到问题报告前状态，所有调查期产品/测试代码与 Gate 工具移除，问题简要延期为 `KNOWN_ISSUES.md` KI-001；现有副本未经授权不删除。这一决定不把实际 50+ 次扩写为 100 次，也不宣称反复显隐路径已经安全。

## 11. 下一阶段

M3.MVP3 已归档，下一步进入 **M3.1 管理动作**：固定、手工排序、打开/定位、移除、重新关联和可见项 Missing 检查。由于真实 Stage 已开始产生引用且 MVP 没有移除入口，M3.1 应优先交付安全“移除引用”与 Missing 状态，再扩展排序/固定；它仍不得删除、移动或改写用户源文件。

点击取用/仅复制属于 M3.2；隐藏源 HWND 的出站 OLE Gate 与卡片拖出属于 M3.3，继续不得提前塞入当前验收阶段。
