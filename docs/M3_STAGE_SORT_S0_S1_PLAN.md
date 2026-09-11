# M3 中转站排序 S0–S1 实施计划

> **状态（2026-09-11）**：S0 合同、S1 数据快路及其后续 S2 局部重平衡均已完成。Core 请求/结果、同快照分页边界、SQLite 单项稀疏键和最多 64 项重平衡均已落地；完整隔离 Release 为 Core 83/83 + Integration 523/523，构建 0 warning / 0 error。未接正式排序入口、鼠标手势、跨页交互或真实产品数据库；下一步进入 S3 首个可见同页排序纵向切片。

## 1. 结论

S0 不单独发布一个可进入却不能拖动的排序模式。S1 先建立最终可复用的数据安全基础；首个用户可见排序候选必须等到后续同页真实排序纵向切片具备松手提交终态后再生成。

采用以下产品与所有权合同：

- 普通模式保留 pin、菜单及未来点击取用/向外拖出语义；普通模式下拖动卡片不解释为内部排序；
- 排序模式使用整张卡片作为内部鼠标拖动面，不恢复已被 V2 卡片取消的第三个 24 DIP 排序把手；
- 标题栏只提供“完成”或“退出排序”，并明确“松开后立即保存”；不提供会被理解为撤销已提交移动的“取消”按钮；
- Esc 在活动预览中只取消本次拖动，排序模式空闲时退出模式，普通模式下才隐藏 Vok；
- 已进入 `Committing` 的写入不能伪装成可取消。隐藏、设置、外部 DragEnter 或视图卸载只清理其视觉所有权；已认领写入仍收口一次且不得重放；
- Stage 内部排序不进入 OLE，不修改唯一 `WorkspaceFileDropRouter`，也不重开 KI-001。

## 2. 当前源码进入事实

- 基线 HEAD 为 `c117bcc`；Release 构建 0 warning / 0 error，完整测试 Core 74/74 + Integration 502/502，Stage/Router 筛选 146/146。
- `IStageRepository` 只有 `LoadPage / AddOrMove / Remove / SetPinned / Relink`，尚无排序契约。
- `StagePage` 只有当前项、较旧页 cursor 和总数，未携带当前页两侧的真实直接邻项。
- SQLite 使用 schema 7，按 `(sort_order DESC, id DESC)` 排序；`sort_order` 允许相同值，`id` 以规范化 `D` 文本存储并使用 SQLite BINARY 文本顺序打破平局。
- `StageViewModel` 只常驻当前 64 项页面，`_pageStarts` 保存走过的 keyset cursor，所有仓储操作经 `_operationGate` 串行进入。
- `StageView` 已有按行 Recycling、动态列投影、Availability 生命周期与外部拖入抑制，没有内部鼠标排序状态。
- `MainWindow` 的 Esc 和设置路径只取消 Launcher 交互；`StageWorkspaceFileDropTarget.CancelInteractions()` 当前为空，后续排序纵向切片必须补齐，但不属于 S1。

## 3. S0：合同修订

### 3.1 本阶段完成

- 以本计划修订 `M3_STAGE_UI_FOUNDATION_NEXT_PLAN.md`、`M3_STAGE_M31_PLAN.md §16` 与 `decisions.md §31` 的过时交接；
- 标记旧“独立 24 DIP 把手”和“单独发布静态排序壳层”不再适用；
- 冻结“每次松手独立提交，因此没有跨多次移动的取消/撤销”语义；
- 保留既有 revision-safe 复合邻接、最多 64 项局部重平衡、keyset 和 schema 7 合同。

### 3.2 本阶段不完成

- 不创建无消费者的 XAML 按钮、排序模式状态或鼠标事件；
- 不更改 `StageViewModel.Items`、仓储、真实数据库或运行实例；
- 不把后续 S2 局部重平衡、S3 同页交互或 S4 跨页交互提前塞入 S1。

## 4. S1：Core 与 SQLite 单项快路

### 4.1 交付范围

S1 新增最终排序 API 和足以安全表达目标插槽的分页边界，并实现普通稀疏键仍有空间时只更新源项的 SQLite 快路。

新增 Core 值类型：

```text
StageItemVersion(Id, Revision)
StageOrderAnchor(Id, Revision, SortOrder)

StageReorderRequest(
  Source: StageItemVersion,
  NewerNeighbor: StageOrderAnchor?,
  OlderNeighbor: StageOrderAnchor?)

StageReorderStatus =
  Applied | NoChange | NotFound | RevisionConflict |
  NeighborConflict | SortOrderExhausted

StageReorderResult(
  Status,
  ChangedItems: IReadOnlyList<StageItem>)
```

`IStageRepository` 增加：

```text
Task<StageReorderResult> ReorderAsync(
  StageReorderRequest request,
  CancellationToken cancellationToken = default)
```

`StagePage` 增加两个不进入 `Items` 的可选边界：

```text
StageOrderAnchor? NewerBoundary
StageOrderAnchor? OlderBoundary
```

- `NewerBoundary` 是当前首项之前的真实直接项；第一页为 `null`；
- `OlderBoundary` 是当前末项之后的真实直接项；最后一页为 `null`；
- 空页面不参与排序，两个边界都返回 `null`；
- `NextCursor` 继续等于当前页最后一个已展示项，不改变现有 keyset 语义；
- 新参数使用可选默认值，避免一次机械破坏所有既有测试夹具。

### 4.2 复合顺序合同

数据库顺序唯一以以下比较为准：

```text
sort_order 较大者更新；相等时，规范化 id.ToString("D")
按 SQLite BINARY/Ordinal 文本比较较大者更新。
```

不得在 Core 或测试 fake 中未经证明地改用其他 GUID 字节序。增加一项随机 GUID 同 sort-order 对照门禁，证明内存比较器与真实 SQLite 顺序一致。

参数拒绝：

- 空 source/anchor id；
- 非正 source/anchor revision；
- source 与任一 anchor 重复，或两个 anchor 互相重复；
- 两个 anchor 按复合顺序明显倒置。

调用者参数自身无效抛参数异常；数据库权威状态已变化返回显式业务终态，不折叠为 `bool`。

### 4.3 同快照分页边界

`LoadPage` 的 count、当前 `pageSize + 1` 读取和较新边界查询必须位于同一个 deferred read transaction 中，避免总数、页面与边界来自不同数据库瞬间。

读取顺序：

1. 开启 deferred transaction；
2. 读取总数；
3. 沿现有 `(sort_order,id)` keyset 读取 `pageSize + 1` 项；
4. 前 `pageSize` 项成为 `Items`；多读的一项直接成为 `OlderBoundary`；
5. 若 `Items` 非空，以首项复合键反向查询一项，得到 `NewerBoundary`；
6. 提交只读事务并返回页面。

所有查询继续命中 `ix_stage_items_order`，不得使用 `OFFSET`、`ROW_NUMBER` 或全表物化。

### 4.4 Reorder 单事务算法

S1 的 `ReorderAsync` 在唯一 `SqliteStorageExecutor` 中执行：

1. 开启事务并读取 source；不存在返回 `NotFound`，revision 不同返回 `RevisionConflict`。
2. 按 id 读取非空 anchor；anchor 消失、sort/id 不符返回 `NeighborConflict`，revision 不同返回 `RevisionConflict`。
3. 排除 source 后，用顺序索引证明请求的两侧确实是直接邻项：
   - `NewerNeighbor=null` 时，`OlderNeighbor` 必须是排除 source 后的全局首项；
   - `NewerNeighbor!=null` 时，它之后的直接项必须等于 `OlderNeighbor`；
   - `OlderNeighbor=null` 表示 `NewerNeighbor` 必须是排除 source 后的全局末项；
   - 两侧都为空只允许数据库除 source 外没有其他项。
4. 查询 source 当前的直接两侧；若已等于请求插槽，返回 `NoChange`，零写入。
5. 计算单项稀疏键，全部中间算术使用 `Int128` 或等价 checked 算术：
   - 中间：只有 `newer.SortOrder - older.SortOrder >= 2` 才取严格中点；
   - 顶部：`older.SortOrder + SortOrderStep`；
   - 底部：`newer.SortOrder - SortOrderStep`；
   - 无严格空间或超出 `Int64` 时返回 `SortOrderExhausted`，S1 不尝试局部重平衡。
6. 以 `WHERE id + expected revision` 更新 source 的 `sort_order / revision / updated_utc`，必须恰好命中一行；其余业务字段不变。
7. 提交并返回包含唯一权威源项的 `ChangedItems`。`NoChange` 和所有拒绝终态返回空集合。

source revision 溢出或 SQL 异常必须整笔回滚并传播异常；不能映射成“已排序”或吞掉错误。取消只保证队列准入前/尚未开始的工作可取消，事务一旦执行则按现有 SQLite executor 所有权完整收口。

### 4.5 S1 非目标

- 不实现最多 64 项局部重平衡；这属于 S2；
- 不增加 `IsSortMode`、`StageReorderSession`、乐观 `Items.Move`、鼠标捕获、拖影、插槽或自动滚动；
- 不修改 MainWindow Esc、设置、隐藏或外部 Router adapter；这些与首个真实同页切片一起接入；
- 不读取或改写 `%LOCALAPPDATA%\Vok` 的真实 Stage 数据，不替换当前 PID；
- 不实现跨页排序、点击取用、OLE 拖出或消费。

## 5. 文件级实施步骤

### S1.1 Core 契约

预计修改：

- `src/Vok.Core/Stage/StageReorderRequest.cs`
- `src/Vok.Core/Stage/StageReorderResult.cs`
- `src/Vok.Core/Stage/StagePage.cs`
- `src/Vok.Core/Stage/IStageRepository.cs`
- `src/Vok.Tests/Stage/StageReorderContractTests.cs`

步骤：

1. 先增加值类型、参数不变量和复合顺序 helper；
2. 为 `StagePage` 添加可选边界，保持旧构造调用编译；
3. 最后扩展仓储接口，并一次性给五个测试 fake 增加明确的未调用实现；
4. 只跑 Core 与编译门禁，确认尚无 UI/数据库行为变化。

### S1.2 同快照页边界

预计修改：

- `src/Vok.Infrastructure.Windows/Persistence/SqliteStageRepository.cs`
- `src/Vok.IntegrationTests/Persistence/SqliteStageRepositoryTests.cs`

顺序：

1. 先把现有 LoadPage 查询放入 deferred read transaction；
2. 保留第 65 项作为 `OlderBoundary`；
3. 增加反向一行查询产生 `NewerBoundary`；
4. 覆盖第一页、中间页、末页、单项页、相同 sort-order + id、空页和 10,000 条索引计划；
5. 页边界门禁通过后再进入写事务算法。

### S1.3 单项快路

仍只修改仓储及其测试：

1. 增加 source/anchor 权威读取 helper；
2. 增加排除 source 的直接邻接查询；
3. 先实现 `NotFound / RevisionConflict / NeighborConflict / NoChange` 零写入方向；
4. 再实现顶部、中间、底部单项更新；
5. 最后加入 sort/revision 溢出、异常回滚和文件/目录/`.lnk` 源摘要不变门禁。

### S1.4 收口

- 定向运行 Core Stage、SQLite Stage repository 和所有 `IStageRepository` fake 消费者；
- 运行完整 Release 测试与零警告构建；
- 执行 `git diff --check`；
- 核对 schema 仍为 7、没有真实数据库访问、没有运行实例替换；
- 只宣布“S1 数据快路完成”，不能宣布用户已经可以排序。

## 6. 自动验收矩阵

| 层 | 必须覆盖 |
|---|---|
| Core | 空 id、非正 revision、重复 anchor、倒置 anchor、规范化 id 文本平局顺序 |
| 页面 | first/middle/last/single/empty、两侧直接边界、NextCursor 不变、同快照一致性 |
| 快路 | 顶/中/底、两侧皆空单项 NoChange、只更新 source |
| 冲突 | source 缺失、source revision、anchor 缺失/revision/sort、邻接被插入项打断 |
| 数值 | 中点无空间、顶部/底部 Int64 溢出、source revision 溢出完整回滚 |
| 数据安全 | id/canonical/target/kind/pinned/created 不变；文件、目录、`.lnk` 摘要不变 |
| 规模 | 10,000 条页面与邻接查询使用顺序索引；无 OFFSET、全表 materialize 或全表 UPDATE |
| 回归 | 当前 576 项基线只增不减，构建 0 warning / 0 error |

## 7. 停止条件

出现以下任一情况停止并重新评审，不进入 S2/S3：

- 直接邻接无法通过现有 `(sort_order,id)` 索引有界证明；
- 页面边界必须依赖 UI 猜测或跨多个非一致快照；
- 单项快路需要 schema 8、连续行号、`OFFSET` 或全表扫描；
- 相同 sort-order 的真实 SQLite 顺序与计划中的内存比较不一致；
- 任一拒绝/异常方向产生部分写入，或提交成功需要重放写事务才能得到权威结果；
- 同一失败点连续两次仍未重新核对事务、复合顺序或 executor 所有权假设。

## 8. S1 完成后的交接

S1 通过后进入 S2 局部最多 64 项重平衡。S2 数据门禁完成后，再把排序模式、整卡鼠标手势、同页预览和一次提交合并为首个用户可见纵向切片；不在 S1 结束时发布不可操作的排序入口。

## 9. 实施结果（2026-09-11）

- S1.1：新增 `StageItemVersion`、`StageOrderAnchor`、`StageReorderRequest/Result` 与显式终态；`IStageRepository` 已接入最终方法签名，既有 fake 以不可调用 stub 保持当前消费者边界。
- S1.2：`LoadPage` 的 count、页面、`NewerBoundary` 与 `OlderBoundary` 已位于同一 deferred transaction，继续沿用原 keyset cursor；first/middle/last/single/empty、相同顺序键与 10,000 条索引计划均通过。
- S1.3：事务内完成 source/anchor revision 校验、排除 source 的直接邻接证明、NoChange、顶部/中间/底部稀疏键和单行权威更新；间隔/端点耗尽保守返回，revision 溢出与 SQL 异常回滚传播。
- 数据安全：测试证明仅 source 的顺序、revision、更新时间变化，文件、目录与 `.lnk` 源摘要不变；schema 保持 7，源码不含 `OFFSET`/`ROW_NUMBER` 或全表排序更新。
- 收口：完整隔离 Release 测试 593/593（Core 82 + Integration 511），构建 0 warning / 0 error，`git diff --check` 通过；默认 Release PID 5576 保持运行且仍是 S1 前候选。

## 10. S2 后续实施结果（2026-09-11）

- 新增纯 `StageOrderRebalancePlanner`：目标两侧候选由近到远输入，按 2–64 的最小连续最终窗口搜索；输出使用严格递减数值键，不依赖 GUID 平局，并以 `Int128` 计算 `Int64` 边界。
- SQLite 仅在 S1 快路无空间时从目标两侧各读取最多 64 项，查询继续使用 `(sort_order,id)` keyset；第 64 项可作为窗口外边界，但成功写入和 `ChangedItems` 永不超过 64。
- 全部成员 revision 在首个 UPDATE 前预检；事务只更新 sort 实际变化的行，统一 revision +1/updated time，读取权威结果后提交。成员溢出、后续 SQL 失败与 65 项需求均零部分写入。
- 2/63/64 项密集端点、65 项拒绝、中间/底部极值、窗口外行保持、文件/目录/`.lnk` 摘要和 10,000 条规模门禁通过；规模夹具实测重平衡 64 行、普通快路 1 行，两侧查询均命中 `ix_stage_items_order`。
- 完整隔离 Release 为 Core 83/83 + Integration 523/523，共 606/606；构建 0 warning / 0 error，schema 7、Router 与 KI-001 均未改变。随后按用户要求以最新默认 Release 构建替换旧 PID 5576，当前唯一 PID 1512 包含 S1/S2 数据代码但没有可见排序消费者；用户已确认呼出/隐藏、卡片交互、外部文件/目录/`.lnk` 拖入、管理动作与重启保持的必要人工回归通过。下一步只进入 S3 同页真实交互，不提前实现跨页。
