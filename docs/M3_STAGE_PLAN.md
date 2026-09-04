# M3 中转站产品契约与实施计划

> **状态（2026-09-04）**：产品边界和实施路线已确认，业务代码尚未创建。200 条是首期产品开放上限，不是 schema、仓储或分页的架构上限；初始实现即保留后续高上限能力。当前 429/429 自动测试是进入 M3 的回归基线；先完成 M3.0 的真实 WPF/OLE Gate 0，再进入正式接线。

## 1. 目标与完成定义

中转站是一个**引用型、可消费的临时货架**：用户把稍后要交给其他窗口的文件或目录放入中转站，Vok 持久保存引用；取用成功后默认移除，固定项继续保留。

首个纵向切片只处理本地文件系统引用，并完成以下闭环：

```text
外部 FileDrop
  → 单一窗口级 OLE 路由判定 Stage 目标
  → Shell STA 规范化/校验
  → SQLite 单事务去重、容量判断、插入或回顶
  → 分页/虚拟化展示
  → 点击取用、仅复制、拖出、排序、固定、打开/定位、移除
  → 按真实终态决定保留或消费
```

M3 文件型切片的完成定义：

- 文件、目录和 `.lnk` 各自作为原始引用拖入、跨重启保留并可再次取用；`.lnk` 不静默解析成目标；
- 启动台和中转站共用一个窗口级外部拖入会话，跨区域移动、取消、隐藏、关闭和异常都只终结一次；
- 点击取用安全完成“写文件剪贴板 → 隐藏/恢复目标 → 校验 receipt → 注入粘贴”，拖出使用真实 OLE `COPY`；
- 只有外部交付已确认完成时才消费未固定项；失败、取消和“仅复制”均保留；
- SQLite 是容量、去重、排序和消费的唯一权威，UI 不维护第二套真相；
- 全量自动回归、schema 6→7 实库副本迁移、真实 Explorer 拖入/拖出和跨应用取用通过，且现有启动台、剪贴板及宿主事务无回归。

## 2. 首期产品契约

### 2.1 条目与引用

- 一个文件系统路径对应一张卡片；批量拖入按来源顺序创建多张卡片。
- 首期接受现存文件、目录和 `.lnk`。不复制、移动、删除或接管源内容，只保存绝对路径引用。
- 路径规范化沿用 Windows 文件引用规则：保留可展示的原值，另存大小写不敏感的 canonical key；目录尾分隔符不制造重复。不得解析符号链接、junction 或 `.lnk` 后再合并身份。
- 同一路径再次拖入不新增条目，而是更新可刷新展示信息并把已有条目移动到本次插入位置。混合“已有 + 新增”的批量操作保持输入顺序。
- 源路径后来失效时不自动删行；`Missing` 是读取时得到的展示状态，不持久化成权威字段。卡片提供“重新关联”“复制原路径”“移除”，且失败检查不能阻塞 UI。

### 2.2 容量、保留与消费

| 规则 | 首期定义 |
|---|---|
| 默认容量 | 50 条 |
| 首期产品上限 | 200 条；M3 先实现策略，设置入口归 M4；不得写成数据库约束或仓储结构上限 |
| 容量计数 | 固定和未固定条目都计入 |
| 批量超限 | 先排除本批重复项，再在同一事务判断；空间不足则整批拒绝，不部分插入、不静默淘汰 |
| 跨重启 | 全部条目、顺序和固定状态保留 |
| 成功取用/拖出 | 未固定项移除，固定项保留 |
| 仅复制 | 保留，无论是否固定 |
| 失败或取消 | 保留，不以超时或 UI 推测成功 |

“交付结果”和“条目清理结果”必须分开表达：

```text
DeliveryOutcome = Completed | CopiedOnly | Failed | Cancelled
RetentionOutcome = Removed | RetainedPinned | RetainedByPolicy |
                   RetainedRevisionConflict | CleanupDeferred
```

外部交付已经成功、但 revision 变化或数据库清理失败时，不得重放外部动作；界面报告“已取用，条目未自动移除”，并保留该条目供用户处理。

### 2.3 操作语义

| 操作 | 外部效果 | 条目终态 |
|---|---|---|
| 单击卡片 / Enter | 写入 `CF_HDROP`，恢复呼出前目标并注入粘贴 | 完成后消费未固定项；只能复制或失败时保留 |
| 仅复制 / Ctrl+C | 写入 `CF_HDROP`，不隐藏、不注入 | `CopiedOnly`，始终保留 |
| 卡片主体拖出 | OLE `FileDrop + Preferred DropEffect(COPY)`，allowed effects 仅 `Copy` | `DoDragDrop` 返回 `Copy` 才消费未固定项；`None`/异常保留 |
| 排序把手拖动 | 仅中转站内存预览，松手一次 SQLite 提交 | 不触发 OLE，不消费 |
| 固定/取消固定 | revision 保护的单项更新 | 改变后续成功交付的保留策略 |
| 打开/定位 | 复用 Shell 文件动作 | 不消费 |
| 移除 | 只删除 Stage 引用 | 不触碰源文件 |

入站和出站都只声明 `COPY`。这里的 `COPY` 表示来源不得因 Vok 的操作删除原文件；中转站内部仍只保存引用，不复制文件内容。不得从 `COPY | MOVE` 掩码猜测移动成功，也不得让外部目标用 `MOVE` 反向删除源文件。

### 2.4 明确不实现

- 不把中转站做成文件管理器、同步盘、下载器或源文件缓存；
- 不共享 `ClipboardEntry`、`ClipboardReplayPayload` 或剪贴板保留/资产引用表作为 Stage 业务模型；
- 不在文件闭环前接入文本；图片须等所有模块共同参与的资产引用证明和回收事务完成后再进入；
- 不实现虚拟文件流、Shell namespace、压缩包内部项、远程下载承诺、跨设备同步或撤销历史；
- 不以固定延时判定 OLE 已开始/完成，不在 `DragEnter/DragOver` 读取外部数据，不新建第二个 Shell/Clipboard STA；
- 首期不把多选、批量拖出、自定义拖影、颜色分组、标签或搜索作为完成门禁。

## 3. 基于当前架构的落点

```text
MainWindow（唯一 AllowDrop）
  └─ WorkspaceFileDropRouter（唯一外部入站会话）
       ├─ Launcher target adapter
       └─ Stage target adapter

StageView / StageViewModel
  ├─ IStageRepository ─────────────→ 现有 SqliteStorageExecutor
  ├─ IStageFileService ────────────→ 现有 WindowsShellStaExecutor
  ├─ IStageClipboardService ───────→ 现有 Clipboard STA/自写 receipt
  └─ StageActionController
       ├─ IWorkspaceCommands ──────→ WorkspaceController
       └─ IPasteInputService ──────→ 现有安全输入注入

StageDragOutController（WPF UI STA）
  └─ WorkspaceOutboundDragClaim + OLE drag source
```

职责边界：

- `Vok.Core/Stage` 只定义 Stage 聚合、值对象、仓储和状态机；不能出现 HWND、WPF `IDataObject`、SQLite、Shell COM 或剪贴板业务类型。
- `Vok.Infrastructure.Windows/Stage` 负责文件引用检查、Shell 展示/动作和文件剪贴板适配。它复用现有 Shell/Clipboard 资源所有者，不拥有新线程。
- `Vok.App/Features/Stage` 负责 VM、手势、动作协调和用户可见结果；所有隐藏、目标恢复和退出继续经过 `WorkspaceController`。
- `MainWindow` 保留唯一 `AllowDrop=true`，现有事件处理改为委托给 `WorkspaceFileDropRouter`；Launcher 和 Stage 不各自建立窗口级租约。
- Launcher 与 Stage 可以共享已被两个真实消费者证明的 Windows 文件引用检查/图标读取内核，但聚合、结果类型、表和生命周期保持独立。提取前先用现有 Launcher 测试锁定行为。

### 3.1 后续高上限兼容契约

首期只向用户开放 200 条，但初始实现不得形成需要重建业务模型或迁移全部排序数据才能扩容的结构：

- `StageOptions` 的 200 是组合根传入的产品策略；`stage_items` 不增加 `COUNT <= 200`、分区表或依赖 200 的字段宽度，仓储也不把 200 编入 SQL 结构。
- `StagePage.TotalCount`、容量计算和 UI 计数从首版使用 64 位值；仓储按 `(sort_order,id)` keyset 读取，热路径禁止 `OFFSET` 和全表物化。
- `sort_order` 从首版使用带间隔的 64 位稀疏排序键。普通插入、回顶和可见范围重排只更新受影响条目；局部间隔耗尽时只分段重平衡，不在一次用户拖动中重写全表。
- Stage UI 使用有界页面窗口而不是“每次 LoadMore 后永久追加”：只保留视口附近的轻量 VM，离开窗口的图标、失效检查和容器可释放；图标/缩略图缓存继续有独立固定预算。
- 启动只读取统计与首屏，不枚举、验证或取图全部引用；canonical 去重走唯一索引，Missing 只检查可见项。
- 大批删除、重平衡和将来的保留维护使用有界批次并让出 SQLite 队列；单次结果不返回数万条完整对象或 ID 给 UI。
- M3 仍以 200 条完成产品验收，同时直接构造 10,000 条数据验证 schema、keyset、稀疏排序和有界 VM。未来开放 10,000 或更高档位前增加 100,000 条性能门禁，但不改变 Stage 聚合、表主结构和公开分页契约。

剪贴板已有独立聚合、keyset 分页、外置 CAS、32 MiB 缩略图缓存和有界 worker，因此条目总数不会直接变成常驻图片内存。其当前 10,000 条支持上限可以在原边界内继续提高；在开放更高档位前，应把“每次捕获后全量扫描保留集合”和“已翻页 VM 永久累积”分别替换为索引化增量保留和有界页面窗口。它们是仓储/UI 内部实现升级，不要求把 Clipboard 与 Stage 合并成通用内容模型。图片始终同时受条数、总字节和磁盘剩余空间约束；高条数不能冒充低磁盘占用。

建议文件布局：

```text
src/Vok.Core/Stage/
  StageItem.cs
  StageTargetDescriptor.cs
  StagePage.cs
  StageOptions.cs
  StageRepositoryResults.cs
  IStageRepository.cs
  IStageFileService.cs
  IStageClipboardService.cs
  StageTransferStateMachine.cs

src/Vok.Infrastructure.Windows/Stage/
  WindowsStageFileService.cs
  WindowsStageClipboardService.cs
src/Vok.Infrastructure.Windows/Persistence/
  SqliteStageRepository.cs

src/Vok.App/Features/Stage/
  StageView.xaml(.cs)
  StageViewModel.cs
  StageItemViewModel.cs
  StageActionController.cs
  StageReorderSession.cs
  StageDragOutController.cs
src/Vok.App/Hosting/
  WorkspaceFileDropRouter.cs

tools/Vok.StageDragProbe/            # Gate 0 隔离探针
```

## 4. Core 与持久化设计

### 4.1 聚合与接口

```text
StageItem
├─ Id
├─ TargetKind: File | Directory | Shortcut
├─ TargetValue
├─ CanonicalKey
├─ ResolvedName
├─ SortOrder                  # 64 位稀疏排序键
├─ IsPinned
├─ Revision
└─ CreatedUtc / UpdatedUtc
```

`StageTargetDescriptor` 是平台检查后交给 Core/仓储的不可变输入。首期不加入正文、缩略图 blob、通用 `ContentItem`、剪贴板语义哈希或持久化 `Missing` 字段。

仓储至少提供以下原子能力：

- keyset 分页读取；
- 批量 `AddOrMove`，在一个事务内完成去重、容量检查、排序重写和结果计数；
- revision 保护的固定、重排和移除；
- revision 保护的成功消费；
- 明确区分 `CapacityExceeded`、`InvalidInput`、`NotFound`、`RevisionConflict`、`Cancelled` 和存储失败。

状态机分别约束“点击取用”和“OLE 拖出”，但共享一次性终态原则：每个 generation 只能被 `Completed/CopiedOnly/Failed/Cancelled` 之一认领；迟到回调只能清理自己的资源，不能修改新代条目。

### 4.2 schema 7

在现有单一 `SqliteStorageExecutor` 中把 schema 从 6 升至 7：

```text
stage_items
  id TEXT PRIMARY KEY
  target_kind INTEGER NOT NULL
  target_value TEXT NOT NULL
  canonical_key TEXT NOT NULL COLLATE NOCASE UNIQUE
  resolved_name TEXT NOT NULL
  sort_order INTEGER NOT NULL
  is_pinned INTEGER NOT NULL
  revision INTEGER NOT NULL
  created_utc TEXT NOT NULL
  updated_utc TEXT NOT NULL

INDEX stage_items_order ON stage_items(sort_order, id)
```

- schema 6→7 建表、结构核验和 `user_version=7` 必须在同一事务；失败完整回滚，不能留下“版本 6 + 半张 Stage 表”。
- `sort_order,id` 是稳定 keyset 游标；禁止 `OFFSET` 扫描大表。
- schema 不编码首期 200 条上限；后续提高产品策略不需要迁移 `stage_items` 或更换分页游标。
- 容量、canonical key 冲突、批量顺序与 revision 在 SQLite 执行器内判定；不能先由 UI 查数量再插入。
- 排序键使用 64 位稀疏值；局部重平衡和批量维护必须有界，不能以连续行号导致回顶时更新全部记录。
- Stage 只存引用，没有资产文件和自动过期回收；删除行永远不得触碰 `target_value` 指向的用户文件。

## 5. 窗口级入站 OLE 路由

`WorkspaceFileDropRouter` 取代 `MainWindow` 中面向 Launcher 的直接判断，拥有唯一外部拖入 generation：

1. `DragEnter` 只探测 `FileDrop` 格式、识别是否为 Vok 自己的出站 token，并建立或替换会话；不调用 `GetData`。
2. `DragOver` 只按当前指针命中 Launcher/Stage/无目标，更新唯一视觉和 `Copy/None`；跨区不新建第二租约。
3. `Drop` 先一次性认领终态并清除全部视觉，再且仅再调用一次 `GetData(FileDrop)`，复制为不可变路径列表后分发给命中的目标 adapter。
4. `DragLeave`、Esc/取消、工作区隐藏、进入设置、窗口关闭、新 `DragEnter` 和异常都是幂等终点。
5. Stage 自身出站数据带进程内 token；拖回 Vok 时路由拒绝自吞，内部排序完全不进入 OLE。

现有 `WorkspaceController` 的入站拖放 lease 继续保护工作区不被普通失活提前关闭；router 只统一所有权，不复制宿主状态机。

## 6. 点击取用与仅复制

点击取用流水线：

```text
认领 Stage item generation / action gate
  → repository 读取当前 revision
  → Shell STA 检查全部路径仍可用
  → Stage clipboard adapter 写 COPY 文件载荷并取得 receipt
  → WorkspaceController 以 StageTake 原因隐藏、恢复呼出前目标
  → receipt 仍为当前值才允许注入 Ctrl+V
  → 输入提交成功 = DeliveryOutcome.Completed
  → repository 以 expected revision 消费未固定项
```

失败方向固定如下：

- 路径无效或写剪贴板失败：`Failed`，不隐藏，保留条目；
- 剪贴板已写入，但隐藏/目标恢复/receipt 校验/输入注入失败：`CopiedOnly`，保留条目并明确提示用户可手动粘贴；
- 注入已提交：`Completed`；随后清理失败只影响 `RetentionOutcome`，不得再次写入或注入；
- 用户或应用退出取消尚未提交的动作：`Cancelled`，保留条目；
- 固定状态或 revision 在动作期间改变：外部成功仍成立，但消费以 `RetainedPinned` 或 `RetainedRevisionConflict` 收口。

`IStageClipboardService` 使用 Stage 自己的文件载荷和 receipt 类型；Windows adapter 可在内部映射到现有剪贴板 writer，以复用同一个 Clipboard STA 和自写抑制，但该映射不得泄漏到 Core。`StageActionController` 不调用或继承 `ClipboardPasteController`。

“仅复制”只执行检查和 clipboard write；成功即为 `CopiedOnly`，不隐藏、不恢复目标、不注入，也不消费条目。

## 7. 出站 OLE 与 M3.0 Gate 0

### 7.1 首选实现

- 卡片主体按下后未超过系统拖动阈值仍是点击取用；超过阈值才认领 `WorkspaceOutboundDragClaim`。
- 在 WPF UI STA 创建只包含 `FileDrop`、`Preferred DropEffect=DROPEFFECT_COPY` 和进程内 source token 的 `DataObject`，调用 `DragDrop.DoDragDrop(..., Copy)`。
- 第一个可证明 OLE 已进入源会话的 `GiveFeedback/QueryContinueDrag` 边沿触发同 generation 的隐藏请求；不得在鼠标阈值刚越过时用延时猜测。
- 隐藏失败设置源端取消标志，由后续 OLE 回调安全返回 cancel；所有 claim、事件订阅和视觉只清理一次。
- `DoDragDrop` 返回 `Copy` 才算 `Completed`；`None`、Esc、目标拒绝或异常均保留条目。取消后不自动重新呼出 Vok，避免抢回用户正在操作的外部窗口。

### 7.2 Gate 0 必须先证明

隔离探针不得读写产品数据库，至少覆盖：

- WPF 源 HWND 在 OLE 开始后隐藏，Explorer、桌面和普通支持 FileDrop 的目标仍能完成 `Copy`；
- Explorer 不删除或移动源文件，返回效果只有 `Copy/None`；
- Esc、拖到空白/不支持目标、源路径中途失效、隐藏失败和目标异常都结束嵌套消息循环并释放 capture/claim；
- 隐藏发生在 OLE 真实开始之后，快速点击、未过阈值和拖动初始化失败不会误隐藏；
- 同一 Vok 窗口拒绝自己的出站 token，Launcher/Stage 入站视觉不残留；
- 20 轮成功/取消交替后线程、句柄、事件订阅和工作区 generation 回到稳定基线。

如果 WPF `DoDragDrop` 在隐藏源 HWND 后无法稳定完成，唯一允许的降级是在 **WPF UI STA** 上封装原生 `DoDragDrop/IDropSource`；不得把 OLE 源放到 Shell STA、增加固定 sleep 或保留不可见源窗骗过测试。Gate 0 只允许保留一个通过真实结果的实现分支。

## 8. 展示、分页与手势

- 中转站使用 `ListBox + VirtualizingStackPanel`，开启 Recycling 和 Pixel 滚动并隐藏滚动条；数据源保留视口附近的有界 keyset 页面窗口，而不是让已加载 VM 随滚动深度单调增长。
- 每张卡显示类型图标、名称、必要的父路径摘要、固定/失效状态；完整路径放入 ToolTip 和自动化名称，不把长路径撑破布局。
- 图标和可用性只为可见项异步加载，经 item id + revision 校验后回填；旧页、旧代和已回收容器的结果直接丢弃。
- 卡片主体承担“单击取用 / 拖出”的阈值会话；独立排序把手承担内部排序；固定、菜单和辅助按钮不参与两者。
- 内部排序沿用“内存预览、终态认领、一次 SQLite 提交、取消完整回滚”的机制，但 Stage 拥有自己的 reorder session，不共享 Launcher ViewModel 状态。
- 空状态只说明“将文件或文件夹拖到这里”；容量拒绝、路径失效、仅复制降级和清理延迟必须是可观察结果。

## 9. 分阶段实施与提交门禁

### M3.0：产品合同与 OLE 探针

交付：本计划、`decisions.md §27`、隔离 `Vok.StageDragProbe` 和可重复记录脚本。

门禁：上述真实 WPF/OLE 矩阵通过并只留下一个实现方向；未通过前不接产品 Stage UI。本文档和决策已完成，物理探针尚未实施。

### M3.1：Core、schema 7 与仓储

交付：Stage 聚合/结果/状态机、`SqliteStageRepository`、schema 6→7 迁移、DI 注册和仓储测试；不接 OLE。

门禁：容量/批量原子性、去重回顶、64 位稀疏排序/局部重平衡、revision 冲突、固定消费、10,000 条 keyset 分页、迁移成功/回滚全部通过；首期 200 只来自产品策略，schema 和仓储结构不得依赖该数值；全量基线无回归。

### M3.2：展示、统一入站路由与管理操作

交付：真实 Stage 区域、分页/虚拟化、图标/失效检查、外部拖入、排序、固定、打开/定位、重新关联和移除；Launcher 改走统一 router。

门禁：跨 Launcher/Stage 拖动只读一次数据；所有取消/隐藏/关闭路径清理；重启后引用和顺序一致；深度滚动后 VM/图标缓存回到固定窗口预算；源文件零改动。

### M3.3：仅复制与点击取用

交付：Stage clipboard adapter、`StageActionController`、`StageTake` 宿主隐藏原因和完整结果提示。

门禁：写入前失败、写入后隐藏/恢复/receipt/注入失败、重复点击、退出、旧代、revision 变化与成功消费全部有自动测试；真实记事本/Office/Explorer 目标完成取用与降级矩阵。

### M3.4：卡片拖出

交付：正式 `StageDragOutController`、generation-scoped outbound claim、Copy-only DataObject 和成功消费。

门禁：将 Gate 0 的成功/取消/隐藏/自吞/资源矩阵移入产品集成测试，并通过 Explorer、桌面和至少一个普通接收应用的真机验证。

### M3.5：收口与归档

交付：故障注入、可访问性、响应式视觉、实库副本迁移、自包含发布和文档归档。

门禁：全量测试与探针通过；schema 6 真实库副本升级后 `quick_check=ok`、外键/既有 Clipboard/Launcher 数量与资产 SHA 不变；启动、剪贴板、托盘和宿主物理回归通过。只有“文件拖入 → 重启 → 取用/拖出 → 正确消费”的真实闭环完成后，M3 文件切片才可归档。

每个阶段只引入一个主要变量，均以进入阶段时的全量测试为回归底线；一处连续失败两次应回到所有权/时序假设重新诊断，不能叠加延时或旁路。

## 10. 验收矩阵

### 自动验证

- 同批重复、跨批重复、大小写差异、目录尾分隔符和 `.lnk` 身份符合 canonical 规则；
- 49/50/51 条、混合重复批次、固定占满和并发添加均遵守 50 容量及整批原子拒绝；
- schema 6→7 保留现有全部数据，半迁移、未知列和未来版本安全拒绝并回滚；
- 10,000 条分页不使用 OFFSET，回顶/局部排序不全表改写，页面窗口和缓存不随滚动深度增长，容器回收后迟到图标/失效结果不串卡；
- router 覆盖跨区、无目标、一次 GetData、自身 token、DragLeave 缺失、隐藏、设置、关闭和新会话替换；
- 点击取用覆盖 `Failed/CopiedOnly/Completed/Cancelled` 及所有 retention outcomes；外部成功后清理失败不重放；
- 拖出覆盖 `Copy/None`、Esc、隐藏失败、源失效、双启动、旧代回调和 Dispose；
- Stage 删除、容量拒绝、排序和消费均不修改任何源文件；现有 429 项基线测试持续通过。

### 真机验证

- Explorer/桌面的文件、目录、Unicode 长路径和 `.lnk` 拖入后跨重启可见，重复拖入回顶且不复制源内容；
- 跨 Launcher/Stage 区域来回移动，蓝框/命中效果准确，取消和窗口隐藏后无残留；
- 点击取用到 Explorer、记事本/编辑器和 Office：成功时未固定项消失，固定项保留；无法注入时条目保留且系统剪贴板可手动粘贴；
- 拖出到 Explorer/桌面只复制，不移动；Esc 和拒绝目标后条目保留，Vok 不抢回前台；
- 删除或改名源文件后显示失效，重新关联恢复；“移除”只删 Stage 行；
- 100 次拖入/排序/取用/取消和 20 次 OLE 成功/取消交替后，UI、热键和剪贴板监听持续响应，线程/句柄/数据库队列无单调增长。

## 11. 后续顺序

文件型 M3 归档后再评审：

1. Stage 文本：独立文本聚合和写回语义，不借用剪贴板历史生命周期；
2. 模块中立资产引用：让所有消费者共同参与“引用为零”的删除证明；
3. Stage 图片：只有第 2 步完成后才接入捕获、展示、拖出和回收；
4. 多选、批量拖出、标签/分组和搜索：由真实文件切片使用数据决定，不预埋通用模型。

任何后续类型都必须保持本计划的两个核心不变量：窗口级 OLE 只有一个所有者，外部交付结果与内部条目清理结果分开提交。

后续提高文件型 Stage 上限时，只允许调整产品策略并新增对应规模门禁；若需要迁移 `stage_items` 主结构、替换分页游标或重写 Stage 聚合，视为首期架构门禁失败，不能把代价推迟到高上限版本。
