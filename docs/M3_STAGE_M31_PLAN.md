# M3.1 中转站管理动作实施计划

> **状态（2026-09-11）**：实施中。Checkpoint 0–5、V2 静态样式、Hover/KeyboardFocus 与 Gate 3 已完成。排序 S0 合同、S1 Core/同快照分页边界/SQLite 单项快路和 S2 最多 64 项局部重平衡现已完成，完整隔离 Release 606/606、构建 0 warning / 0 error；下一步合并首个可见同页排序纵向切片。跨页排序、M3.2/M3.3 与 KI-001 均未进入本轮。

## 1. 阶段目标与完成边界

M3.1 在已归档的“外部拖入并持久落项”MVP 上补齐文件引用的管理能力。实施完成后，用户应能辨认失效引用、只移除中转站记录、固定、调整顺序、打开、定位、重新关联和复制原路径，同时任何管理动作都不得删除、移动或改写源文件。

本阶段包含：

- 只对当前可见卡片异步检查 `Available / Missing / Unavailable / Unknown`，不持久化可用性；
- 带 expected revision 的移除、固定、重新关联和排序仓储事务；
- 打开、在资源管理器中定位、复制原路径及重新关联选择器；
- 独立排序把手、内存预览、一次提交、取消完整回滚和跨页有界排序；
- 管理操作状态、可访问性、浅色/深色及窄列布局；
- schema 7 不变、真实库副本回放、全量自动回归和独立发布候选。

本阶段不包含：

- 单击/Enter 取用、`CF_HDROP`“仅复制”、粘贴注入或成功消费；这些属于 M3.2；
- 以 Stage 为 OLE 源的卡片拖出、隐藏源 HWND Gate 或自吞 token；这些属于 M3.3；
- 文本、图片、多选、标签、搜索、容量设置或自动清理；
- 修改 `WorkspaceFileDropRouter` 的外部拖放所有权，或处理 `KNOWN_ISSUES.md` KI-001；
- 以全表物化、`OFFSET`、连续行号或复制 Launcher 全表排序换取短期实现。

“复制原路径”是文本管理动作，复制的是路径字符串且条目始终保留；它不同于 M3.2 把文件引用写为 `CF_HDROP` 的“仅复制”。

## 2. 当前实现诊断

### 2.1 已具备的基础

- `StageItem` 已包含 `IsPinned`、64 位 `SortOrder` 和 64 位 `Revision`，schema 7 无需新增列或迁移版本。
- `stage_items` 已有大小写不敏感 canonical 唯一索引与 `(sort_order,id)` 顺序索引；新增/重复回顶已经使用稀疏步长。
- `StageViewModel` 用一个操作门禁串行化页面导航和入站提交，常驻页面最多 64 项；管理写入可进入同一权威顺序。
- `WindowsStageFileService` 已复用唯一 Shell/COM STA 完成路径规范化；Launcher 已有打开、定位和内部排序的可回归行为样例。
- Clipboard 已有可用性状态、可见容器异步工作及唯一 Clipboard STA/自写抑制机制，可作为机制参照，但 Stage 不复用其业务 DTO 或生命周期。

### 2.2 必须补齐的缺口

| 当前状态 | M3.1 要求 |
|---|---|
| `IStageRepository` 只有分页和 `AddOrMoveAsync` | 增加 revision 保护的移除、固定、重新关联和有界重排 |
| `StageItemViewModel` 是不可变投影 | 支持仓储返回的新 revision、固定状态和瞬时可用性 |
| `IStageFileService` 只解析入站路径 | 增加 Stage 自有的检查、打开和定位窄契约 |
| Stage 卡片只有展示 | 增加固定、更多菜单、状态徽标和独立排序把手；卡片主体继续不触发取用 |
| 页面只保存下一页 cursor | 排序需要当前窗口上下边界，提交后不能依赖失效 cursor |
| Launcher 排序读取并重写全表 | Stage 必须普通情况只改单项、间隔耗尽时最多重平衡一个 64 项窗口 |
| 当前无重新关联入口 | 增加可注入选择器，取消、无效目标和 canonical 冲突均不得改变旧引用 |

### 2.3 高风险点

1. 移除若只按 id 删除，迟到 UI 可能删除已被重复回顶或重新关联的新 revision。
2. Missing 若在加载整页或启动时同步检查，会让 UI、共享 Shell STA 或网络路径阻塞，并破坏高上限边界。
3. 排序若照搬 Launcher 的连续行号算法，会在 10,000 条门禁中把一次拖动放大为全表更新。
4. 打开/定位若绕过 Workspace 隐藏事务，可能在 Vok 仍可见时启动 Shell，破坏前台和焦点合同。
5. 重新关联若把 canonical 冲突静默合并，会丢失原条目的顺序、固定状态或用户可见身份。
6. 管理手势若绑定卡片主体，会与 M3.2 的单击取用和 M3.3 的主体拖出提前冲突。

## 3. 产品语义

### 3.1 可用性

Stage 定义自己的瞬时枚举：

```text
Unknown      尚未检查，或网络/UNC 路径不做同步存在性判断
Available    已确认当前可访问
Missing      本地文件系统明确返回文件/目录不存在
Unavailable  路径合法，但权限、设备或 I/O 状态令本次检查无法确认
```

- 数据库继续只保存原始引用；不得新增 `is_missing` 或把某次检查结果当成持久事实。
- 只检查已实现且位于可见视口的卡片；翻页、容器回收、隐藏、revision 变化或 Dispose 立即取消/丢弃旧结果。
- 结果应用必须同时匹配 `item id + revision + 当前容器绑定`，防止 Recycling 串卡。
- UNC、断连网络盘和其他可能长时间等待的来源先显示 `Unknown`，不为追求 Missing 精度占住共享 Shell STA。
- 重新呼出、重新关联或条目 revision 变化后允许重查；本阶段不增加后台轮询或定时全表扫描。

### 3.2 移除

- “移除”只执行 `DELETE FROM stage_items`，条件必须包含 `id + expected revision`。
- 删除前后都不调用 `File.Delete`、`Directory.Delete`、Shell recycle、移动或改名 API。
- `Removed / NotFound / RevisionConflict` 分开返回；冲突时重载权威页面，不猜测成功。
- 成功后明确提示“已从中转站移除；原文件未受影响”。入口放在更多菜单，不增加危险的整区点击。

### 3.3 固定

- 固定/取消固定使用 `id + expected revision` 原子更新并 `revision + 1`。
- 固定不改变顺序、不绕过容量，也不在本阶段触发任何消费或保留清理。
- 仓储返回完整的新 `StageItem`；ViewModel 不自行推算 revision。

### 3.4 打开与定位

- “打开”和“定位”是显式按钮/菜单动作；卡片主体仍保持无命令。
- 操作先通过新的 Stage 管理隐藏事务完成 Hide、Topmost 和目标恢复收尾，再调用共享 Shell STA；隐藏失败则不执行 Shell 副作用。
- Stage 使用自己的 Core 契约；Windows 层可以抽取 Launcher/Stage 已共同需要的内部文件动作帮助器，但不能让 Stage 构造 `LauncherItem`。
- Missing 状态下禁用打开/定位；`Unknown/Unavailable` 允许用户主动尝试并显示真实失败。

### 3.5 复制原路径

- 复制单个 `TargetValue` 文本，Vok 保持可见，条目和 revision 不变。
- 使用 Stage 自有的窄 clipboard adapter，并在 Windows 实现内部复用现有 Clipboard STA 与自写 receipt；不得直接调用 WPF `Clipboard`，也不得让该写入重新进入剪贴板历史。
- 该接口只处理路径文本；M3.2 再扩展文件引用 `CF_HDROP`，不能把两种“复制”混为一个结果。

### 3.6 重新关联

- 选择器按原种类选择文件、目录或 `.lnk`；取消时零写入。
- 新目标先经 `IStageFileService.ResolveAsync` 完整规范化，再用 `id + expected revision` 提交。
- 成功保留原 `id / sort_order / is_pinned / created_utc`，更新 kind、target、canonical、name、updated 和 revision。
- 新 canonical 已属于另一条 Stage 记录时返回 `CanonicalConflict`，保留两条原记录，不静默合并、删除或回顶。
- 对话框只返回原始路径；选择完成后、提交前必须重新经 `IStageFileService.ResolveAsync` 规范化。解析时已失效、解析失败或 SQL 提交失败时旧引用保持不变。文件系统与 SQLite 不能组成跨资源原子事务：若目标在解析成功后才消失，允许保存“解析时有效”的引用，并在权威条目换代后立即通过新 revision 的 Availability 重查显示 Missing；不得为消除这段不可避免的 TOCTOU 窗口而跨 Shell/SQLite executor 持有句柄或同步等待。

### 3.7 排序

- 排序只从独立把手开始，使用系统拖动阈值；不进入 OLE，也不建立 Workspace 外部拖放 lease。
- 会话保存 generation、源 `id/revision`、原页起点、原始顺序和当前插入边界；`Esc`、隐藏、设置、捕获丢失、翻页失败和 Dispose 都只结束一次。
- 同页移动显示真实内存顺序预览；跨页时保留一个轻量源快照和拖影，只加载目标 64 项页面并显示插入标记，不累计经过页的 VM。
- 指针首次进入顶部/底部换页区时最多切换一页，离开该区域后才重新武装；这里是交互阈值，不用固定延时猜测数据库或 OLE 状态。
- 松手只提交一次“源项 + expected revision + 目标相邻项/窗口边界”；SQLite 验证邻接关系和 revision 后决定新顺序。
- 普通情况只更新被移动项。没有可用中点时，只重平衡当前最多 64 项的窗口并保持窗口外边界不变；若边界空间仍不足或算术溢出，整笔返回 `SortOrderExhausted`，UI 回滚预览。
- 成功结果返回所有被改动条目的权威 sort/revision；UI 不用连续索引冒充持久排序。10,000 条门禁必须证明单次变更不退化为全表更新。

## 4. Core 与仓储契约

移除、固定与重新关联已经分别使用模块自有显式结果。Checkpoint 6 再增加以下排序专用类型；具体命名可按实现风格收紧，但失败方向不得折叠为一个 `bool`：

```text
StageItemVersion(Id, Revision)

StageOrderAnchor(Id, Revision, SortOrder)

StageReorderRequest(
  Source,
  NewerNeighbor?,
  OlderNeighbor?)

StageReorderStatus =
  Applied | NoChange | NotFound | RevisionConflict |
  NeighborConflict | SortOrderExhausted

StageReorderResult(Status, ChangedItems[0..64])
```

`NewerNeighbor / OlderNeighbor` 表示把源项从当前顺序中暂时排除后，目标位置在全局 `(sort_order DESC, id DESC)` 顺序中的直接相邻项。页首、页尾不能自行解释成全局首尾；只有对应页外边界为空才代表全局边界。

`IStageRepository` 增加：

```text
RemoveAsync(id, expectedRevision)
SetPinnedAsync(id, expectedRevision, isPinned)
RelinkAsync(id, expectedRevision, StageTargetDescriptor)
ReorderAsync(StageReorderRequest)
```

最终排序方法按上述专用请求收紧为 `ReorderAsync(StageReorderRequest)`。`StagePage` 增加当前页实际直接相邻的 `StageOrderAnchor? NewerBoundary / OlderBoundary`；原 `NextCursor` 翻页语义保持兼容。`OlderBoundary` 可复用现有多读的一行，`NewerBoundary` 用复合顺序索引查询当前首项之前的直接一行，不能直接信任可能已陈旧的调用方 cursor。两个边界不进入 `Items` 或创建常驻 VM，只用于排序邻接、跨页连续性和提交后安全页补位。排序请求必须能描述全局相邻位置，不能把“当前页最后”误写成“全局最后”。

所有写入在 `SqliteStorageExecutor` 内单事务执行：

1. 读取并核对目标 id/revision；
2. 核对 canonical 冲突、页面成员或相邻边界；
3. 计算结果和受影响行；
4. 只更新/删除目标行或一个不超过 64 项的重平衡窗口；
5. 提交后返回权威行；异常、取消或失败结果均不得留下部分更新。

取消语义沿用当前 SQLite executor 的既有边界：入队前取消不执行；一旦工作项已经入队，就让单事务明确提交或回滚并返回真实终态，不把调用方随后取消冒充数据库未执行。

schema `user_version` 保持 7；不增加列、表、触发器或依赖产品容量 200 的约束。

## 5. 文件、Shell 与剪贴板边界

`IStageFileService` 扩展 Stage 自有能力：

```text
InspectAsync(StageItem) -> StageFileAvailability
OpenAsync(StageItem)
RevealInExplorerAsync(StageItem)
```

Windows 实现继续使用现有 `WindowsShellStaExecutor`。先用 Launcher 特征测试锁定打开/定位行为，再把两个真实消费者共同需要的窄 Win32/Shell 代码抽到 internal helper；两个模块的 Core DTO、错误文案和生命周期仍然独立。

新增 `IStageClipboardService.CopyPathTextAsync`，Windows adapter 在内部映射到现有 Clipboard 平台服务，使自写 marker 和监听抑制继续只有一个所有者。M3.1 不在 Stage Core 引用 `ClipboardReplayPayload`、`ClipboardEntry` 或 receipt 类型。

重新关联选择器是 App/WPF 边界，不进入 Core 或 Infrastructure 的 SQLite/Shell 服务。选择器必须由当前主窗口拥有；对话框取消、窗口隐藏或应用退出返回取消结果，不产生仓储调用。若系统对话框导致意外 `Application.Deactivated`，先诊断真实激活链路，不用延时或全局失活抑制掩盖。

打开/定位新增明确的 `WorkspaceHideReason.StageManagementAction` 和对应命令入口；不得借用名称错误的 `HideForLauncherActionAsync`，也不得提前加入 M3.2 的 `StageTake` 原因。

## 6. ViewModel、页面与结果一致性

- 现有 `_operationGate` 继续串行分页、入站和所有数据库管理写入；不新增并行仓储真相。
- 可用性检查走独立的可取消可见项泵，不持有数据库操作门禁；共享 Shell STA 仍保证系统资源所有权。
- `StageItemViewModel` 改为可更新投影，持有权威 `StageItem` 和瞬时 Availability；每次 `Update` 重新通知固定、路径、名称、revision、自动化名称及操作可用性。
- 固定、重新关联成功直接应用仓储返回项；若 UI 应用失败，重新加载当前页，不重放已提交写入。
- 移除成功后重新加载当前页补位，并截断其后的 cursor 历史；若末页变空则回到上一页。提交已确认但刷新失败时，不把旧卡片继续显示成有效事实，并明确报告“已移除，列表刷新失败”。
- 排序提交成功只应用仓储返回的权威顺序/revision，并截断受影响页之后的 cursor 历史；冲突或失败恢复原始页/顺序。
- 重新关联成功后取消旧可用性工作，并以新 revision 重新检查；旧路径的迟到结果必须丢弃。
- 入站提交在管理动作排队期间仍由同一门禁排序；重复回顶造成 revision 变化后，迟到的移除、固定或重新关联返回冲突而不是覆盖新状态。

## 7. WPF 交互

每张卡保持三层职责：

```text
排序把手        只启动 Stage 内部排序
卡片主体        本阶段仍无单击/拖出命令，留给 M3.2/M3.3
固定与更多菜单  固定、打开、定位、复制原路径、重新关联、移除
```

- 更多菜单使用 `PlacementTarget.Tag` 或等价显式绑定，避免 WPF `ContextMenu` 脱离视觉树后取错 DataContext。
- Missing 显示明确警告图形和文字，不只靠颜色或降低透明度；完整路径仍在 ToolTip 和自动化名称中。
- 固定、菜单按钮和排序把手都有独立自动化名称与键盘焦点；按钮操作不能误启动排序。
- 外部拖入 active 时，现有 `StageInteractionSurface.IsHitTestVisible=false` 继续屏蔽全部管理手势；不得修改 Router 的目标/终态逻辑。
- 260 DIP 窄列下名称、状态和按钮不能相互覆盖；浅色、深色、高 DPI 与长 Unicode 路径使用动态主题资源并保留截断。

## 8. 实施顺序

每个检查点只引入一个主要变量，并在通过对应测试后再进入下一项：

### Checkpoint 0：冻结进入事实

- 记录 HEAD、tracked worktree、当前运行实例路径和 468/468 基线；
- 测试与构建使用独立 `%TEMP%` artifacts，不替换 PID 7864；
- 重新打开 KI-001，仅确认本阶段不触碰其代码路径，不启动该专项。

### Checkpoint 1：Core 与 revision-safe 仓储

- 增加显式结果、条目版本、窗口重排请求和仓储方法；
- 先完成 SQLite 单元/集成门禁，不接 UI、Shell 或文件选择器；
- 证明 schema 7 和既有入站 `AddOrMove` 行为完全不变。

### Checkpoint 2：可见项 Availability

- 增加 Stage 自有 availability 与 Windows 检查；
- StageView 只为真实可见容器请求，按 id+revision+容器绑定应用；
- 覆盖滚动回收、翻页、隐藏、重新显示、取消和 Dispose。

### Checkpoint 3：安全移除纵向切片

- 增加更多菜单中的“移除”；
- 完成 revision 冲突、当前页补位、末页回退、提交后刷新失败和源文件零变化门禁；
- 这是 M3.1 的首个可独立交付点，优先于其余管理动作。

### Checkpoint 4：固定、打开、定位与路径复制

- 接固定按钮及权威 item 回填；
- 抽取共享的内部 Shell 文件动作帮助器，新增 Stage 管理隐藏原因；
- 接文本路径 clipboard adapter，并验证自写不产生新历史。

### Checkpoint 5：重新关联

- 加入可注入选择器、解析、revision 提交和 canonical 冲突结果；
- 覆盖文件、目录、`.lnk`、取消、选择后失效和 UI 隐藏/退出。

### Checkpoint 6：有界手工排序

- 先完成同页会话、预览、取消与单项稀疏更新；
- 再加入窗口间隔耗尽的最多 64 项局部重平衡；
- 最后加入跨页边缘切换和插入标记，始终保持一个页面窗口常驻；
- 不通过 10,000 条更新范围门禁前，不接正式排序把手。

### Checkpoint 7：组合根、WPF 与候选验收

- 核对 singleton 和共享 executor 所有权，Dispose 不重复释放；
- 全量回归、真实 WPF 浅深/窄列、schema 7 在线备份副本和独立候选通过后，再由用户执行物理管理矩阵；
- 未完成物理矩阵前只能宣布“M3.1 代码就绪”，不能宣布管理动作归档。

任一高风险检查点连续失败两次，回到 revision、窗口边界、WPF 容器或宿主隐藏假设重新诊断；不得叠加固定延时、吞异常、全表重排或绕过 WorkspaceController。

## 9. 自动验收矩阵

### 9.1 仓储与数据安全

- 移除、固定、重新关联和排序的 `Applied / NotFound / RevisionConflict` 精确区分；
- 重新关联 canonical 冲突整笔拒绝，原/目标两条记录字节语义不变；
- 固定保留 id/order/created，重新关联保留 id/order/pinned/created；成功项 revision 只增加一次；
- SQL 异常和算术溢出完整回滚；预取消不入队，已入队工作即使调用方随后取消也返回真实提交/回滚终态；
- 移除前后测试文件、目录树、长度、SHA-256 和时间戳不变；
- 10,000 条中普通重排只更新目标项，局部重平衡不超过 64 项，查询计划继续使用顺序索引且没有 `OFFSET`；
- 排序后逐页 keyset 无重复、无遗漏，未涉及窗口的 id/revision/order 不变；
- schema 7 重开幂等且未产生 schema 8。

### 9.2 Availability 与生命周期

- 本地存在、明确 Missing、权限/I/O Unavailable、UNC/网络 Unknown 分流；
- 只请求可见容器，隐藏和回收立即取消，10,000 条不会启动 10,000 次检查；
- 旧页、旧 revision、容器换绑和 Dispose 后的迟到结果不更新新卡；
- 检查失败不把条目删除或持久化 Missing，共享 Shell STA 仍可继续接单。

### 9.3 ViewModel 与手势

- 管理动作与分页/入站共用单一 mutation 顺序，排队和预取消后门禁可继续使用；
- 移除当前页首/中/尾及末页最后一项后，计数、页面摘要和按钮状态正确；
- 已提交但 UI 刷新失败不重放写入，冲突会重载而不乐观覆盖；
- 固定/重新关联使 item revision 与文案同步，可用性旧结果失效；
- 同页/跨页排序的成功、无变化、Esc、隐藏、捕获丢失、提交异常和旧 generation 全部一次收口；
- 卡片主体仍没有取用或 OLE 拖出，固定/菜单点击不开始排序。

### 9.4 Shell、Clipboard、宿主与 WPF

- 打开/定位只在 `StageManagementAction` 隐藏事务成功后产生一次 Shell 副作用；隐藏失败为零副作用；
- 路径文本写入经过唯一 Clipboard STA，监听将其识别为 self-write，Stage/Clipboard 均无重复项；
- 文件/目录/`.lnk` 重新关联选择与取消正确，系统对话框不会留下失活、Topmost 或焦点残态；
- 浅色/深色、520×500 与 260×408 DIP 下状态、固定、菜单和排序把手可见且不裁切；
- 自动化名称包含类型、名称、路径、固定和可用性状态，Missing 不只依赖颜色；
- 现有 Launcher、Clipboard、Router、WorkspaceController 和 KI-001 所在外部拖放合同无业务期望变化。

## 10. 物理验收矩阵

使用独立发布候选和专用临时源目录；先记录源目录摘要，再执行：

1. 放入文件、目录、`.lnk`，删除或改名源对象后重新呼出，当前可见卡显示 Missing，界面与快捷键保持响应；
2. 对 Missing 项复制原路径、移除，确认 Stage 行消失且任何剩余/恢复的源内容未被 Vok 改动；
3. 固定/取消固定并重启，状态和顺序保持；容量计数不因固定改变；
4. 打开文件/目录和定位文件/`.lnk`，确认 Vok 先隐藏、Shell 只执行一次，失败时状态可见；
5. 将 Missing 文件、目录和 `.lnk` 分别重新关联到新目标，重启后引用、名称和状态保持；重复 canonical 冲突明确拒绝；
6. 对 50 条默认容量完成顶部、中间、底部排序；对隔离 200 条候选完成跨页排序，重启后顺序保持；
7. 100 次 Missing 检查/固定/移除/取消和跨页排序交替后，线程、句柄、Shell/SQLite 队列及常驻 VM 无单调增长；
8. 回归标准 Explorer/桌面拖入和 Launcher/Stage/None 跨区矩阵，确认 M3.1 没有改变外部拖放路由。

本矩阵不执行“活动外部拖动中反复关闭/呼出”；该路径仍属于 KI-001，不得借 M3.1 验收宣称已修复。

## 11. 验证命令与完成条件

实施时使用独立输出，避免当前进程或历史候选污染结果：

```powershell
$taskArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'Vok-M31'

dotnet test src/Vok.IntegrationTests/Vok.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~Stage" `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

dotnet test src/Vok.IntegrationTests/Vok.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~WorkspaceController|FullyQualifiedName~Launcher|FullyQualifiedName~Clipboard" `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

dotnet test src/Vok.sln `
  -c Release `
  --settings Vok.runsettings `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

dotnet build src/Vok.sln `
  -c Release `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

git diff --check
```

完成条件：

- Checkpoint 1–7 和全部新增自动门禁通过，进入阶段的 468 项基线零回归；
- Release 构建 0 warning / 0 error，Stage 常驻页面仍有界，SQLite/Shell/Clipboard 所有权未增加第二套线程；
- schema 保持 7，真实数据库只通过在线备份副本验证，发布候选启动前不写真实库；
- 用户完成物理管理矩阵，源文件摘要证明移除、固定、排序和失败路径零文件副作用；
- KI-001 仍明确延期，M3.2/M3.3 功能没有混入；
- 达成后更新 `M3_STAGE_PLAN.md`、APC 当前状态并提交 M3.1；下一步进入 M3.2 点击取用与 `CF_HDROP` 仅复制。

## 12. 首批实施结果（2026-09-05）

- 为避免先创建尚无消费者的固定/重新关联/排序接口，Checkpoint 1 先只落地 `RemoveAsync(id, expectedRevision)` 及 `Removed / NotFound / RevisionConflict` 显式结果；其余 mutation contract 在各自检查点实施。
- `SqliteStageRepository` 在单事务内读取 revision，并只以 `id + revision` 删除 Stage 行。真实临时文件门禁证明 stale revision 被拒绝、当前 revision 成功、重复移除为 NotFound，源文件路径、长度、时间戳和 SHA-256 全部不变。
- 新增 Stage 自有 `Unknown / Available / Missing / Unavailable`，并把 Stage/Clipboard 的 Windows 文件可用性判定提取为共享 internal helper；两个业务模块仍使用各自 Core 枚举和契约，唯一 Shell STA 所有权不变。
- `StageView` 只为与 ScrollViewer 视口相交的已实现卡片请求检查。绑定携带 item id/revision/容器/VM 身份；滚动回收、隐藏或 DataContext 变化会取消并重置，迟到结果不能串到旧卡或新卡。
- 卡片新增 Missing/Unavailable/Unknown 文字状态与“更多”菜单；“从中转站移除”绑定 Stage 命令，成功后从 SQLite 权威重载当前页，末页最后一项移除会回到上一页。提交已经确认但刷新失败时不重放删除，并从当前投影移除已确认删除的卡片。
- 新增 9 项自动门禁；最终 Release 为 Core 71/71、Integration 406/406，共 477/477。真实 WPF 正常、Missing、暗色和 260×408 DIP 窄列快照已生成并目视通过；`git diff --check` 通过。
- 本批未修改 schema 7、Stage 入站 Router、Workspace 外部拖放 lease、KI-001、真实数据库或当前运行实例。下一批进入固定、打开/定位和复制原路径，并在需要时补齐对应 mutation contract。

## 13. Checkpoint 4 实施结果（2026-09-05）

- 新增 revision-safe `SetPinnedAsync(id, expectedRevision, isPinned)`，在 schema 7 单事务内区分 `Applied / NoChange / NotFound / RevisionConflict`，成功返回完整权威条目并只更新固定状态、revision 与更新时间。固定/取消固定跨重开保留，revision 溢出完整回滚，既有 `AddOrMove` 回顶继续保留固定状态。
- `StageItemViewModel` 可由权威条目原位更新；revision 改变会重置瞬时 Availability，并通知 `StageView` 取消旧绑定、按新 revision 重新检查。提交已确认但投影应用失败时只重载当前权威页面，不重放固定写入。
- Windows 层从 Launcher 提取共享 internal Shell 文件动作，Launcher 与 Stage 继续共用唯一 `WindowsShellStaExecutor`。Stage 新增独立 `StageManagementAction` 隐藏原因和 `StageActionController`，只有隐藏事务成功后才打开或定位；隐藏回滚时 Shell 零副作用，Missing 项禁用两项动作。
- 新增 Stage 自有路径文本 clipboard adapter；Windows 实现内部映射到既有 `IClipboardPlatformService`，沿用 Unicode 文本、语义哈希、self-write marker 和唯一 Clipboard STA。复制原路径保持 Vok 可见，不修改条目/revision，也不引入 M3.2 的 `CF_HDROP`。
- 卡片增加独立固定按钮以及打开、定位、复制原路径菜单；卡片主体继续无命令。视觉复验发现并修复 260 DIP 下新增按钮挤出右边界的问题，浅色、深色、Missing、固定和窄列状态均通过。
- 最终 Release 为 Core 74/74、Integration 420/420，共 494/494，构建 0 warning / 0 error；schema 仍为 7，真实数据库、运行实例、Router 与 KI-001 均未修改。下一步进入 Checkpoint 5 重新关联。

## 14. Checkpoint 5 重新关联实施计划（2026-09-05）

### 14.1 当前源码诊断

- `IStageRepository` 当前只有分页、入站、移除和固定，没有重新关联契约；`StageItemMutationStatus` 只表达固定动作能够产生的四种终态，不能精确表达 canonical 冲突或类型不匹配。
- `WindowsStageFileService.ResolveAsync` 已在唯一 Shell STA 上把原始路径解析为 `StageTargetDescriptor`，并能严格区分普通文件、目录和 `.lnk`；重新关联应复用它，不新增第二套路由或同步文件系统访问。
- `StageViewModel` 的 `_operationGate` 已串行化分页、入站和管理写入；`ApplyAuthoritativeItem` 会让 `StageItemViewModel.Update` 重置 Availability，再由 `StageView` 按新 revision 取消旧绑定并重查，所需换代机制已经存在。
- 当前 App 没有文件/目录选择器。目标框架的 `Microsoft.Win32.OpenFileDialog` 和 `OpenFolderDialog` 均可用；选择 `.lnk` 时必须设置 `DereferenceLinks=false`，否则系统对话框可能返回快捷方式目标而不是 `.lnk` 自身。
- 选择器属于 WPF/App 边界。若直接让选择器构造函数依赖 `MainWindow`，会与 `MainWindow -> StageViewModel` 形成 DI 环；owner 应在调用时由窄 provider 取得，或由已在窗口创建后接线的 Stage controller 提供。
- 原生 owned dialog 与当前 `Application.Deactivated -> ForegroundLost` 隐藏规则的真实组合尚未验证。没有证据前不得增加全局失活抑制、固定延时或新的宿主 lease。

### 14.2 本检查点的完成边界

本检查点只交付“把一条现有 Stage 引用改绑到同种类的新目标”：

- 普通文件只能重新关联到普通文件，目录只能到目录，`.lnk` 只能到 `.lnk`；对话框过滤只是提示，`ResolveAsync` 返回的真实种类与仓储事务都必须再次校验。
- 成功保留 `id / sort_order / is_pinned / created_utc`，只更新 kind、target、canonical、name、`updated_utc`，revision 精确增加一次；容量与页面位置不变。
- 取消、解析失败、类型不匹配、NotFound、RevisionConflict、CanonicalConflict、revision 溢出和 SQL 异常均不改旧条目，也不触碰任一源文件。
- canonical 已属于另一条记录时明确拒绝，不能合并、删除冲突项、回顶或继承另一条记录的固定/顺序。
- 卡片主体仍无取用或拖出动作；只在更多菜单加入“重新关联…”，不修改 Router、Workspace drag lease、M3.2、M3.3 或 KI-001。

### 14.3 契约与所有权

采用专用结果而不是把重新关联专属状态塞入固定动作的结果集合：

```text
StageRelinkStatus =
  Applied | NoChange | NotFound | RevisionConflict |
  CanonicalConflict | KindMismatch

StageRelinkResult(Status, Item?)

IStageRepository.RelinkAsync(
  itemId,
  expectedRevision,
  StageTargetDescriptor target,
  cancellationToken)
```

- `Applied / NoChange` 返回权威条目；其余终态不返回猜测条目。
- 同一 `id` 且 canonical key 相同视为 `NoChange`，不增加 revision；路径大小写或对话框返回形式的差异不制造无意义版本。
- 仓储即使被非 UI 调用，也必须比较旧 `StageItem.Kind` 与新 descriptor kind；不把 WPF filter 当作领域约束。
- SQLite executor 内只运行数据库事务，Shell STA 只负责解析；两者之间传递不可变 descriptor，不交叉持有资源。
- App 层增加可注入的单项 picker 契约。Stage controller 负责把 WPF picker 接到 ViewModel；ViewModel 继续拥有 `_operationGate`、解析、仓储提交、权威投影和用户状态文本，避免 controller 形成第二条 mutation 顺序。
- WPF picker 内部再以窄 dialog factory 隔离真实 `OpenFileDialog/OpenFolderDialog` 创建；自动测试只捕获配置与 owner，不弹系统窗口。生产 owner 在调用时取得，避免构造期 `MainWindow` 依赖环。

### 14.4 单事务算法

`SqliteStageRepository.RelinkAsync` 在现有 executor 的一个事务内按以下顺序执行：

1. 读取 `itemId` 的完整权威行；不存在返回 `NotFound`。
2. 比较 revision；不一致返回 `RevisionConflict`。
3. 比较旧种类与新 descriptor；不同返回 `KindMismatch`。
4. canonical 与当前条目相同返回 `NoChange`；不更新 target/name，也不增加 revision。
5. 查询同 canonical 且 `id <> itemId` 的记录；存在则返回 `CanonicalConflict`。
6. 以 `checked(current.revision + 1)` 计算新 revision，使用 `WHERE id = $id AND revision = $expectedRevision` 更新四个目标字段、revision 与更新时间。
7. 重读完整权威条目，提交后返回 `Applied`；任何异常和溢出回滚全部字段。

事务不得读取、删除、移动、重命名或写入目标文件；schema 和 `user_version` 保持 7。

### 14.5 UI 与选择器流程

1. ViewModel 在 `_operationGate` 内确认条目仍属于当前页，快照 `id + revision + kind`，设置单项 Busy；整个 owned dialog、解析和提交期间保持唯一 Stage mutation 顺序。
2. picker 必须在 WPF UI STA 上同步显示 owned dialog，不得用 `Task.Run`。普通文件使用单选 `OpenFileDialog`；目录使用单选 `OpenFolderDialog`；`.lnk` 使用 `*.lnk` filter 且 `DereferenceLinks=false`。
3. 对话框取消返回显式 Cancelled，零次 Resolve、零次 Repository。对话框返回后还要复核 owner 可见性和应用 lifetime；窗口已经隐藏或应用正在退出时同样按 Cancelled 收口，不得继续迟到提交。是否需要主动关闭仍打开的 owned dialog 由 5D 的真实 Gate 决定，不能预设系统行为。
4. 选择成功后只解析这一个 raw path。失败显示具体类别；解析种类与快照种类不同则显示类型约束并保持旧引用。
5. 调用 `RelinkAsync(id, expectedRevision, descriptor)`：
   - `Applied`：原位应用权威条目，触发旧 Availability 取消和新 revision 重查；
   - `NoChange`：保留当前投影并报告目标未变化；
   - `NotFound / RevisionConflict`：不乐观覆盖，重载当前权威页；
   - `CanonicalConflict`：保留当前页两条记录并明确指出目标已在中转站；
   - `KindMismatch`：作为防御性终态报告，不尝试改类型。
6. 已提交后若投影应用失败，只重载当前页并报告“重新关联已保存，列表已重新加载/刷新失败”；不得重放仓储调用。

“重新关联…”放在复制原路径之后、危险的移除区之前。它只增加 ContextMenu 项，不占卡片横向按钮预算；仍需复验 Missing、长 Unicode、浅色/深色和 260×408 DIP。

### 14.6 实施顺序与停止条件

每步只引入一个主要变量：

1. **5A：Core/SQLite**——先增加专用契约和单事务实现，完成所有结果、字段保持、溢出回滚和源摘要门禁；不接 App。
2. **5B：ViewModel**——用假的 picker/解析/仓储接入命令，覆盖取消、无效、类型错误、冲突、权威更新和提交后 UI 失败；不显示真实对话框。
3. **5C：WPF picker**——实现文件/目录/`.lnk` 三种配置和 owner provider，自动测试配置、owner、单选及 `.lnk` 不解引用。
4. **5D：owned-dialog Gate**——在独立候选上实测打开、选择、取消、Esc、Alt+Tab 和应用退出，记录 `Application.Deactivated`、主窗可见性、Topmost 与遗留对话框。正常则保持宿主代码不变；若真实出现误隐藏或孤儿窗口，停止 Checkpoint 5，先形成 generation-scoped owned-modal 生命周期方案并重新评审 R11，不在本切片叠加延时或全局 suppression。
5. **5E：菜单与回归**——加入菜单、自动化文案和视觉复验，再跑 Stage 定向门禁、Workspace/Launcher/Clipboard 邻接回归和完整 Release。

若同一失败点连续出现两次，回到 picker owner、Application activation、revision 或 canonical 假设重新诊断；不得绕开 `_operationGate`、直接写 SQLite 或让 picker 自己提交。

### 14.7 自动与物理验收增量

- SQLite：六种终态、同 canonical NoChange、冲突两行字节语义不变、字段保持、revision +1/溢出、预取消/已入队事务边界、schema 7 重开；普通文件、目录和 `.lnk` 前后路径/长度/时间戳/SHA 或目录摘要不变。
- ViewModel：取消与解析失败零仓储、类型不匹配双层拒绝、stale revision 重载、canonical 冲突不合并、成功换代丢弃旧 Availability、提交后投影失败不重放、Dispose/隐藏后不迟到提交；可控 fake 覆盖“选择后、解析前失效”为零写入，以及“解析成功后才失效”为提交后 Missing 重查。
- Picker/WPF：三类对话框配置、owner、单选、`.lnk` 不解引用、菜单绑定与自动化名称、Missing/固定/窄列/浅深主题无回归。
- 物理最小矩阵：分别把 Missing 文件、目录、`.lnk` 关联到同类新目标并重启；三类取消；三类错误选择；冲突目标；成功后立即删除新目标并确认下次显示 Missing；对话框打开期间 Alt+Tab/退出无孤儿窗口；旧目标与新目标所在目录摘要除验收者主动操作外始终不变。
- 完整门禁从当前 494/494 基线只增不减；通过 Checkpoint 5 只能宣布“重新关联代码与候选路径就绪”，M3.1 仍须继续 Checkpoint 6 排序和 Checkpoint 7 最终物理验收。

## 15. Checkpoint 5 自动实施结果（2026-09-05）

- Core 新增专用 `StageRelinkStatus/StageRelinkResult` 和 `RelinkAsync(id, expectedRevision, target)`。SQLite 在 schema 7 单事务内依次核对 NotFound、revision、原种类、同 canonical NoChange 和其他条目 canonical 冲突；成功只更新目标四字段、revision 与更新时间，返回完整权威条目。
- 仓储门禁证明 Applied 保留 id/order/pinned/created、revision 精确 +1；NoChange 不制造版本；NotFound、RevisionConflict、KindMismatch、CanonicalConflict、revision 溢出均零部分写入。旧/新真实临时文件的长度、mtime 与 SHA-256 在重新关联前后不变，schema 未升级。
- ViewModel 在既有 `_operationGate` 内串行 picker、单目标 Resolve 和仓储提交；取消、解析失败和类型不匹配不会调用仓储。成功原位应用权威条目并触发现有 id/revision Availability 换代；Dispose 后迟到 picker 结果不能解析或提交，提交已确认但投影失败只重载页面、不重放写入。
- App 新增可注入 WPF picker 与 dialog factory；文件、目录和 `.lnk` 分别使用单选原生对话框，全部关闭 link dereference，避免普通文件选择静默跟随 `.lnk`。owner 在调用时取得，未引入 `MainWindow -> StageViewModel` DI 环；对话框返回后会复核 owner 可见性和应用 lifetime。
- 更多菜单已加入“重新关联…”并保持 Missing 可用；卡片横向结构未增加按钮。浅色、深色、Missing、固定与 260×408 DIP 快照目视无回归；Stage 定向 65/65、最终 Release Core 74/74 + Integration 433/433，共 507/507，构建 0 warning / 0 error，`git diff --check` 通过。
- 新增真实 Windows owned-dialog 自动 Gate：在 WPF STA 上以 Topmost owner 实际打开原生 `OpenFileDialog` 与 `OpenFolderDialog`，按精确标题、进程与 owner HWND 关闭并核验 Cancel 终态、owner 可见/Topmost、`Application.Deactivated=0` 和无遗留 `#32770`。独立命令连续 5 轮、共 10 次对话框全部通过；第一次 UI Automation 关闭方案无法命中模态窗，已中止测试宿主且未保留该死路实现，改用窄 `FindWindowEx/GetWindow/PostMessage(WM_CLOSE)` 后稳定通过。
- 首次物理 Gate 发现更多菜单中文呈方框：`StageItemActionButtonStyle` 在 Button 级设置 `Segoe Fluent Icons`，由 PlacementTarget 向 ContextMenu 传播了不含中文字形的图标字体。修复将字体与字号收窄到固定/更多按钮各自的图标 TextBlock，Button 与 MenuItem 恢复应用默认字体；WPF 回归同时锁定图标仍用图标字体、Button/ContextMenu/MenuItem 均不使用图标字体。Stage 65/65、完整 Release 507/507、构建 0 warning / 0 error 与 `git diff --check` 通过。
- 已生成字体修复后的独立 win-x64 自包含候选，共 415 个文件：`%TEMP%\Vok-M31-Relink-FontFix-Candidate-37fa4941a8b74dedb9b7848c3e7bd326`，`Vok.exe` SHA-256 为 `356DE006EF7CEC4B388EFEEDD59D03F3D46DB4176604B4F99376A20FC56C1838`，`Vok.dll` SHA-256 为 `782BDAA39FAC80E2B99557243BC884B61D7E2748037DD18975F4484011826CE9`。已停止旧候选 PID 17988 并启动修复候选 PID 25008 供物理 Gate；实例切换本身未执行数据编辑动作。
- 2026-09-06 提交前复验再次通过真实原生 owned-dialog 5 轮（共 10 个文件/目录对话框）、完整 Release Core 74/74 + Integration 433/433 共 507/507、构建 0 warning / 0 error 与 `git diff --check`；实际 `.lnk` 选择、Alt+Tab、应用退出与重启持久化继续保留为用户物理 Gate。
- 2026-09-06 用户确认 5D 实际候选物理 Gate 通过：菜单中文实际渲染正常，文件/目录/`.lnk` 的真实选择与取消、Alt+Tab、Esc/关闭、应用退出、无孤儿窗口及重启持久化均通过。Checkpoint 5 据此完成，下一步进入 Checkpoint 6 有界排序。

## 16. Checkpoint 6 有界排序实施计划（2026-09-06）

> **前置决策（更新于 2026-09-08）**：用户已确认单一自适应卡片布局，并在 Card 1D 自动门禁后要求继续重设计 Workbench 参考样式、Hover、内部拖动态和外部拖入效果。`M3_STAGE_CARD_REDESIGN_PLAN.md` 必须先于排序实现完成；本轮不实现排序。下文 16.3–16.5 的复合邻接与持久化契约保持有效，16.6、6D、6E 继续作为新视觉冻结后的二维排序设计。

> **S0 修订（2026-09-11）**：用户确认不单独发布静态排序壳层；首个可见候选必须同时具备真实同页拖动终态。V2 卡片不再恢复本节旧有的独立 24 DIP 把手，排序模式改为整卡内部鼠标拖动；每次松手独立保存，标题栏使用“完成/退出排序”而非可被误解为撤销已提交移动的“取消”。下文 16.6 与 6D 中的“独立把手”文字据此失效，S0–S1 的权威合同、事务步骤与门禁见 `M3_STAGE_SORT_S0_S1_PLAN.md`；复合邻接、最多 64 项重平衡、keyset 和 schema 7 边界继续有效。

### 16.1 源码诊断与进入事实

- 当前 HEAD 为 `88b3710`；Checkpoint 5 文档验收变更尚未提交，产品源码相对该提交无新增差异。最近完整基线仍为 Release Core 74/74 + Integration 433/433，共 507/507，构建 0 warning / 0 error。
- `SqliteStageRepository.LoadPageAsync` 按 `(sort_order DESC, id DESC)` 读取，允许相同 `sort_order` 并依靠 id 保持稳定；`SortOrderStep` 为 `1 << 20`，普通入站/重复回顶从当前最大值向上分配。
- `StagePage` 目前只有当前 64 项、`NextCursor` 和总数。当前页首之前、页尾之后的真实直接邻项没有进入契约，因此 UI 不能安全判断页边是不是全局边界。
- `StageViewModel` 只常驻一个 64 项页面，通过 `_pageStarts` 保存已经走过的页首 cursor。跨页移动会改变源页与目标页之间的页成员，受影响位置之后的历史 cursor 不能继续当作精确页界。
- `StageView` 目前只有 Availability 容器生命周期和管理菜单，没有内部排序会话；`StageWorkspaceFileDropTarget.CancelInteractions` 仍为空，`MainWindow` 的 Esc/设置切换只取消 Launcher 排序。
- Launcher 可复用的是系统拖动阈值、鼠标捕获、预览/取消、终态先认领和事件驱动滚动经验；其仓储按连续 slot 重写已加载全集，不满足 Stage 的 64 项/10,000 条边界，不能复制其持久化算法或 ViewModel 状态。

### 16.2 本检查点交付与非目标

交付：

- 独立排序把手启动的 Stage 内部鼠标排序；卡片主体继续没有点击取用或拖出行为；
- 同页实时内存预览、Esc/隐藏/失捕/设置/外部拖入/Dispose 完整回滚；
- 以目标直接邻项表达的单次 SQLite 提交，普通情况只更新源项；
- 数值间隔耗尽时最多更新 64 项的局部重平衡；
- 页内自动滚动、一次武装的跨页切换、轻量拖影与插入标记；
- 10,000 条数据下更新范围、keyset、VM 常驻量和 schema 7 门禁。

非目标：

- 不进入 WPF/OLE `DoDragDrop`，不创建 source token、外部拖放 lease 或第二个窗口级 Router；
- 不实现卡片主体点击取用、`CF_HDROP` 仅复制、成功消费或出站拖动；
- 不改变固定项排序资格，固定只影响后续消费语义，固定与未固定项均可排序；
- 不加入键盘重排、批量选择、分组、搜索、容量设置或 schema 8；排序把手保留键盘焦点和自动化说明，但本检查点只承诺鼠标拖动；
- 不处理 KI-001，也不把内部鼠标捕获结果外推为活动 Explorer OLE 显隐安全证据。

### 16.3 Core 请求、页边界与终态

新增排序专用值类型：

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

- `NewerNeighbor / OlderNeighbor` 是排除源项之后，目标插槽在全局复合顺序中的直接两侧；两者都为空只允许数据库中除源项外没有其他记录。
- 源项不存在返回 `NotFound`；源项或仍存在的锚点 revision 不匹配返回 `RevisionConflict`；锚点消失、sort/id 不匹配、顺序非法、两者不再直接相邻，或空边界不再是全局首尾时返回 `NeighborConflict`。
- 仓储必须以 SQL 查询证明两锚点在排除源项后直接相邻，不能只相信 UI 的列表索引。若源项当前的直接邻项已经等于请求邻项，返回 `NoChange` 和零写入。
- `StagePage` 增加完整的 `StageItem? NewerBoundary / OlderBoundary`，需要提交时再转换为 `StageOrderAnchor`。较新边界从当前首项向复合顺序上方查询一行，较旧边界使用现有 `LIMIT pageSize + 1` 多读的一行；现有构造调用以可选空边界保持兼容，`NextCursor` 语义不变。边界不进入 `Items`，因此 UI 仍只常驻最多 64 个 item VM。
- 参数验证拒绝空 id、非正 revision、源/锚点重复 id 和明显倒置的锚点。相同 numeric sort order 仍按 id 比较；相邻 numeric sort 相同不能走中点快路，必须进入局部重平衡。

### 16.4 SQLite 单事务算法

所有步骤继续由唯一 `SqliteStorageExecutor` 串行执行：

1. 开启事务并读取源项，核对 expected revision。
2. 按 id 读取非空锚点，核对 revision、sort order 和复合顺序。
3. 用顺序索引分别查询排除源项后的直接较新/较旧记录，验证请求邻接和全局首尾。
4. 查询源项当前直接邻项；目标未变化则回滚只读事务并返回 `NoChange`。
5. 优先计算单项稀疏键：
   - 两侧都有锚点时，仅在 `newer.SortOrder - older.SortOrder >= 2` 时取严格中点；
   - 全局顶部使用 `older.SortOrder + SortOrderStep`，全局底部使用 `newer.SortOrder - SortOrderStep`；
   - 全部算术以 `Int128` 或等价 checked 计算，最终值必须严格位于邻接范围并落入 `Int64`。
6. 快路成功时只更新源项的 `sort_order / revision / updated_utc`，其余字段不变；`WHERE id + expected revision` 必须恰好命中一行。
7. 快路无空间时，从插槽两侧用复合 keyset + 有界 `LIMIT` 读取候选，构造包含源项且最多 64 项的连续最终窗口。按窗口大小从小到大寻找可容纳的外部数值边界，保持所有非源项的相对顺序；均匀步长最大取 `SortOrderStep`，最小必须为 1。
8. 局部窗口只更新 sort order 真正变化的行，每行 revision 精确 +1，并返回完整权威 `StageItem`；外部边界、canonical、target、pinned、created 及未涉及行保持不变。任何成员 revision 溢出、SQL 异常或行数不符都整笔回滚。
9. 64 项内仍找不到严格递减的 `Int64` 空间时返回 `SortOrderExhausted`，零部分写入。不得扩大到第 65 项、重写全表或改 schema。

局部重平衡的候选读取量也必须与总条数无关；允许从插槽两侧各做有界 keyset 查询以选择最佳窗口，但不得使用 `OFFSET`、`ROW_NUMBER` 全表排名或物化全部 Stage 行。成功结果的 `ChangedItems.Count` 不得超过 64。

### 16.5 ViewModel 会话与分页恢复

新增 Stage 自有 `StageReorderSession`，至少保存：

- generation 与 `Previewing / LoadingPage / Committing / Cancelled / Completed` 状态；
- 源项 id/revision、源页索引、源页 start cursor、原始 64 项投影、两侧完整边界和原始插槽；
- 当前目标页索引、当前页实际项、两侧完整边界和目标插槽；
- 当前边缘武装、正在执行的页请求代次及一次性终态所有权。

行为顺序：

- `BeginReorder` 只在初始化完成、非 Loading/外部拖入/已有排序、源项属于当前页且至少存在另一项时成功；开始时只快照当前页，不持有 `_operationGate` 跨越整个鼠标会话。
- 同页 `PreviewReorder` 只移动 `ObservableCollection` 中的现有投影；请求邻项从预览后的源项两侧生成，不用连续索引作为持久化身份。
- 跨页期间保留源项的轻量快照用于拖影，`Items` 只替换成当前目标 64 项并显示独立插入标记；返回源页时恢复原始页快照并重新进入同页预览，不产生重复源卡。
- 页加载和最终提交仍进入 `_operationGate`。其他分页或管理命令在 `IsReordering` 时禁用；若已排队的入站/管理写入先完成并替换权威页面，必须先取消旧排序 generation。若数据库在会话期间发生合法变化，最终邻接/revision Gate 拒绝陈旧提交。
- `Esc`、隐藏、设置、外部 DragEnter、鼠标捕获丢失、页加载失败和 Dispose 都调用同一个 `TryCancel`，只有首个终态恢复源页快照、清 Busy/拖影/插入标记并释放捕获；迟到页结果按 generation 丢弃。
- 提交先 `TryBeginCommit`，再从目标页项目与页外边界构造直接邻项并调用仓储一次。鼠标重复抬起、捕获丢失、旧回调和重复命令不得产生第二次调用。
- `Applied` 先用会话快照构造确定的“较新受影响页”投影，再应用 `ChangedItems` 的权威 sort/revision，并让既有 Availability 的 id+revision 门禁自动换代。向较旧页移动时，该安全页是删除源项并用原 `OlderBoundary` 补位的源页；向较新页移动时，该安全页是按目标插槽插入源项、必要时挤出页尾的目标页。随后用数据库重载该页：其 start cursor 在移除/插入前方，仍能保持准确页界；若局部重平衡改变了某个已知 cursor 的 sort order，先按返回权威项修正同 id cursor，再截断该页之后的历史。
- 成功后的权威页读取失败不能重放排序写入；保留上述已确认的安全页投影和返回权威项，截断不再可信的 cursor，明确显示“顺序已保存，但列表刷新失败”。`NotFound / RevisionConflict / NeighborConflict` 不保留乐观预览，重载第一页并重置历史；`SortOrderExhausted` 恢复源页快照并解释未保存。

选择“重载两个受影响页中较新的页面”是当前分页结构下的安全恢复策略：移动只会改变该页及其后的页成员，该页之前的 start cursor 不受成员位移影响。它避免为停留在目标页而引入 `OFFSET`、从首页逐页回放或一次性双向分页重构。向较新页移动时成功项仍位于重载页；向较旧页移动时界面回到源位置并显示补位结果，状态文案明确目标已保存。

### 16.6 WPF 手势、几何与宿主收口

- 激活 Card Gate 已预留的约 24 DIP 独立排序把手区，不复用文件类型图标、卡片主体、固定按钮或更多菜单。图标字体只设置在把手内部图标 `TextBlock`，避免再次通过 Button/ContextMenu 继承污染中文。
- 把手具备 ToolTip、键盘焦点、`AutomationProperties.Name="拖动调整中转项目顺序"` 和 HelpText；按下本身不排序，移动超过 `SystemParameters.MinimumHorizontalDragDistance / MinimumVerticalDragDistance` 后才请求 `BeginReorder`。
- 鼠标捕获归 `StageView`，不调用 OLE。拖影与目标 cell 轮廓位于 `IsHitTestVisible=false` 的轻量 overlay；拖影复用 Card Gate 的内容模板，源卡只显示 dragging 视觉，不打开菜单或创建额外 HWND。
- 插槽几何基于当前已实现卡片的实际 `TransformToAncestor` 二维边界：先按 Y 定位真实行带，再按 X 与卡片中心确定 0..实际项数的插槽；不硬编码卡宽/卡高，不把尾行空 cell 当业务项。未实现容器只能通过页边界插槽表达。Recycling、DataContextChanged 和 Availability 绑定继续按原 id/revision 收口。
- 当前页尚可滚动时，顶部/底部 36 DIP 命名边缘区使用 `CompositionTarget.Rendering` 的真实 elapsed time 做有界自动滚动；事件在终态、失捕、隐藏和 Dispose 时必定解绑。只有 ScrollViewer 已到当前页物理边界，才请求跨页。
- 每次从非边缘进入顶部/底部换页区只消费一次跨页请求；必须离开该区后才重新武装，不使用 hover 延时或定时翻页。进入较旧页后初始插槽为页首，进入较新页后初始插槽为页尾，随后离开边缘再按真实容器更新。
- Card Profile、列数或视口 generation 变化时先取消活动排序、恢复原扁平顺序并清除拖影/目标轮廓，再重组展示行；禁止在旧二维几何上继续提交。
- `StageView.TryCancelReorderInteraction / CancelAllInteractions` 接入 `MainWindow` 的 Esc、设置切换和隐藏路径；`StageWorkspaceFileDropTarget.CancelInteractions` 只委托 Stage view 清理内部手势。允许补齐该 adapter 空实现，但不修改 `WorkspaceFileDropRouter` 的目标选择、数据快照或终态逻辑。
- 外部拖入 active 时继续让 `StageInteractionSurface.IsHitTestVisible=false`；设置该状态前先取消内部排序。第一次 Esc 只取消排序并保持 Vok 可见，第二次 Esc 才沿用宿主隐藏语义，与 Launcher 一致。

### 16.7 分步实施顺序

#### 6A：Core、页边界与单项快路

- 增加排序 DTO/结果和 `StagePage` 两侧实际边界，保持现有构造调用兼容；
- 实现邻接验证、NoChange、顶部/中间/底部单项更新；
- 先通过普通、相同 sort order、NotFound、revision、邻接变化、溢出和 schema 7 重开门禁，不接 ViewModel/WPF。

#### 6B：最多 64 项局部重平衡

- 加入有界两侧读取、窗口选择、统一步长和 ChangedItems 权威返回；
- 覆盖 2/63/64 项成功、65 项边界拒绝、相同 sort order 密集区、外侧边界和 revision 溢出完整回滚；
- 完成 10,000 条普通移动只更新一项、重平衡最多 64 项、未涉及行摘要不变和查询计划无 `OFFSET` 门禁。

**实施结果（2026-09-11）**：两侧候选分别以复合 keyset 有界读取 64 项，纯规划器按最小连续窗口搜索严格递减 `Int64` 键并把步长限制为 `1..SortOrderStep`；事务先预检全部 revision，再只写真实变更行并返回唯一权威条目。2/63/64 项密集端点、65 项拒绝、中间/底部极值、窗口外行、成员 revision 溢出、后续 SQL 失败和文件/目录/`.lnk` 摘要均通过；10,000 条真实库夹具证明重平衡 64 行、随后普通移动 1 行且两侧查询继续使用 `ix_stage_items_order`。schema 保持 7，无 UI 消费者。

#### 6C：ViewModel 同页会话

- 增加 generation 会话、`IsReordering`、预览/取消/一次提交和权威结果应用；
- 先用 fake repository 覆盖同页顶部/中间/底部、NoChange、冲突、提交异常、已提交后刷新失败、排队 mutation 与 Dispose；
- 此步不加跨页或正式 WPF 把手。

#### 6D：WPF 把手与页内交互

- 激活独立把手，接系统阈值、真实二维卡片插槽、共享内容拖影、目标 cell 轮廓、鼠标捕获和页内事件驱动滚动；
- 接 Esc、隐藏、设置、失捕、外部拖入和 adapter 取消；
- 覆盖尾行空 cell 不成锚点、活动排序遇列数变化先取消，以及浅色/深色、260×408 DIP、长 Unicode、Missing/固定和字体继承回归。

#### 6E：跨页切换与 cursor 恢复

- 增加一次武装的页边切换、源轻量快照、目标边界和返回源页；
- 提交后修正已知 cursor、重载较新受影响页并截断后续历史；
- 在 200/10,000 条 fake 与 SQLite 数据中证明跨页双向移动、失败回滚、重启顺序和单页 VM 常驻。

#### 6F：收口门禁

- 跑 Stage 定向、Workspace/Launcher/Clipboard 邻接回归、完整 Release、零警告构建和 `git diff --check`；
- 核对 schema 7、线程/句柄/CompositionTarget 订阅、SQLite 队列和 Availability 工作在 100 次排序/取消后归零；
- 只在全部通过后宣布 Checkpoint 6 代码完成，再进入 Checkpoint 7 独立候选和用户物理管理矩阵。

### 16.8 自动验收增量

| 层 | 必须覆盖 |
|---|---|
| Core/参数 | 空 id、非正 revision、重复锚点、复合顺序倒置、相同 sort order + id 顺序 |
| SQLite 快路 | 顶/中/底、NoChange、源缺失、源/锚点 revision、邻接被插入项打断、只更新源行 |
| SQLite 重平衡 | 2/63/64 项、相同 sort 密集区、窗口外边界不变、65 项不扩张、Int64/revision 溢出回滚 |
| 数据安全 | id/canonical/target/kind/pinned/created 不变，源文件/目录/`.lnk` 摘要零变化，schema 仍为 7 |
| 规模 | 10,000 条 keyset 无重复遗漏；普通更新 1 行、局部最多 64 行；无 `OFFSET`、全表 materialize 或全表 UPDATE |
| ViewModel | 同页预览、跨页轻量源、一次提交、Esc/隐藏/失捕/页失败/Dispose、冲突重载、提交后刷新失败不重放 |
| WPF | 只有把手过系统阈值才开始；卡片/固定/菜单不触发；真实二维容器、尾行实际插槽、换列取消、边缘一次武装、overlay 清理、中文字体链 |
| 邻接回归 | 外部 Router、Launcher 排序、Clipboard 可见项、Workspace Esc/设置、Stage Availability/管理动作业务期望不变 |

### 16.9 Checkpoint 6 物理 Gate 与停止条件

Checkpoint 6 自动门禁通过后，先在隔离候选执行最小物理 Gate，结果并入 Checkpoint 7 最终矩阵：

1. 默认 50 项中把文件、目录、`.lnk` 分别移动到顶部、中间和底部，重启后顺序保持；
2. 按下未过阈值、Esc、失去捕获、切设置和隐藏均回滚，第一次 Esc 不关闭 Vok；固定/菜单操作不误触排序；
3. 在隔离 200 条数据根中双向跨页，验证页内滚动、边缘一次武装、返回源页、目标 cell 轮廓和提交后页摘要；
4. Missing 与固定项可排序但不改变状态，源目录摘要前后相同；
5. 排序结束后立刻执行标准 Explorer/桌面拖入，确认内部捕获、拖影和目标 cell 轮廓已清零，Router 仍只分发一次；
6. 100 次成功/取消/跨页交替后，无鼠标捕获、渲染订阅、VM、SQLite 工作或句柄单调增长。

出现以下任一情况立即停止当前实现并重新评审，而不是加延时、扩大窗口或吞错：

- 正确排序需要第 65 项更新、全表重排、`OFFSET`、全量 VM 或 schema 8；
- 页边无法用真实复合邻接锚点表达，必须猜测“页首/页尾即全局边界”；
- 内部排序进入 OLE、改变 Router 终态，或重开 KI-001 的活动外部拖动显隐路径；
- 提交、取消、失捕或页加载产生双终态、跨 generation 应用或无法恢复的陈旧页；
- 同一失败点连续出现两次而现有 revision、邻接、cursor 或鼠标捕获假设仍未重新核对。
