# M3.1 中转站管理动作实施计划

> **状态（2026-09-05）**：实施中。首批纵向切片已完成 revision-safe 移除仓储、可见项 Availability、Missing 展示和安全移除 UI；Release Core 71/71、Windows/WPF Integration 406/406，共 477/477，构建 0 warning / 0 error。固定、Shell/路径动作、重新关联、排序、发布候选与物理验收尚未实施。当前运行 PID 7864 来自已移除源码的拖动关闭实验候选；本阶段尚未停止或替换它，也未连接或改写真实产品库。

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
- 目标在选择与提交之间失效、解析失败或 SQL 提交失败时，旧引用保持不变。

### 3.7 排序

- 排序只从独立把手开始，使用系统拖动阈值；不进入 OLE，也不建立 Workspace 外部拖放 lease。
- 会话保存 generation、源 `id/revision`、原页起点、原始顺序和当前插入边界；`Esc`、隐藏、设置、捕获丢失、翻页失败和 Dispose 都只结束一次。
- 同页移动显示真实内存顺序预览；跨页时保留一个轻量源快照和拖影，只加载目标 64 项页面并显示插入标记，不累计经过页的 VM。
- 指针首次进入顶部/底部换页区时最多切换一页，离开该区域后才重新武装；这里是交互阈值，不用固定延时猜测数据库或 OLE 状态。
- 松手只提交一次“源项 + expected revision + 目标相邻项/窗口边界”；SQLite 验证邻接关系和 revision 后决定新顺序。
- 普通情况只更新被移动项。没有可用中点时，只重平衡当前最多 64 项的窗口并保持窗口外边界不变；若边界空间仍不足或算术溢出，整笔返回 `SortOrderExhausted`，UI 回滚预览。
- 成功结果返回所有被改动条目的权威 sort/revision；UI 不用连续索引冒充持久排序。10,000 条门禁必须证明单次变更不退化为全表更新。

## 4. Core 与仓储契约

建议在 `Vok.Core.Stage` 增加下列模块自有类型；具体命名可按实现风格收紧，但失败方向不得折叠为一个 `bool`：

```text
StageItemVersion(Id, Revision)

StageMutationStatus =
  Applied | NoChange | NotFound | RevisionConflict |
  CanonicalConflict | SortOrderExhausted

StageItemMutationResult(Status, Item?)
StageRemoveResult(Status)

StageReorderWindow(
  OrderedItems[id + expected revision],
  UpperBoundary?,
  LowerBoundary?)

StageReorderResult(Status, ChangedItems)
```

`IStageRepository` 增加：

```text
RemoveAsync(id, expectedRevision)
SetPinnedAsync(id, expectedRevision, isPinned)
RelinkAsync(id, expectedRevision, StageTargetDescriptor)
ReorderWindowAsync(StageReorderWindow)
```

`StagePage` 增加当前页之后第一条记录的只读边界，或提供等价的稳定窗口边界；原 `NextCursor` 翻页语义保持兼容。排序请求必须能描述全局相邻位置，不能把“当前页最后”误写成“全局最后”。

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
