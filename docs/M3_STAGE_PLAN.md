# M3 中转站产品契约与实施计划

> **当前路线（2026-09-12）**：当前默认容量可暂设 50，但架构必须支持超大上限并保持低内存、流畅 UI 与良好存储性能；同一路径允许重复 occurrence；单项/批量拖入和拖出都是核心。删除的是用户可见页码、较新/较早、Load More 与 S4 整页跳转，不是内部 keyset、预取/淘汰或数据虚拟化。排序 S3 已通过用户观感 Gate；后续实施与完成定义以 `M3_STAGE_FOCUSED_ROADMAP.md` 和 `decisions.md §32` 为准。本文的 M3.MVP1–3 去重/显式页面内容只记录历史已实现基线，不再代表目标产品合同。

## 0. 历史入站 MVP（已完成；身份与页面语义已被 §32 取代）

本轮只证明这一条用户路径：

```text
Explorer / 桌面选中文件、目录或 .lnk 并保持外部拖动
  → 按当前全局快捷键呼出 Vok
  → 指针进入中转站，显示唯一 Copy 命中反馈
  → 在中转站松开鼠标
  → Drop 终态只快照一次 FileDrop
  → Shell STA 规范化
  → SQLite 单事务加入或去重回顶
  → 中转站显示对应条目，重启后仍存在
```

本 MVP 包含：

- 文件、目录、`.lnk` 和同一次多项拖入；只保存源路径引用，不复制、移动、删除或解析源对象；
- 固定产品容量 50，批量超限整批拒绝，canonical path 重复项不新增而回顶；
- 中转站首屏、空状态、处理中状态、目标命中描边和可观察的成功/部分无效/整批拒绝结果；
- schema 7、跨重启保留、`(sort_order,id)` keyset 和 64 位稀疏排序；虽然本轮不开放手工排序，重复回顶仍使用同一排序契约；
- `MainWindow` 唯一 `AllowDrop` 和唯一窗口级外部拖放 generation，同时服务既有 Launcher 与新增 Stage，保留现有“长按呼出后松键等待拖放终态”的宿主语义。

本 MVP 明确不包含：点击取用、仅复制、从中转站拖出、成功消费、固定、手工排序、打开/定位、移除、重新关联、文本、图片、搜索或设置入口。它们继续保留在本文后续路线中，但不得作为当前 MVP 完成条件或前置 Gate。

最小完成定义：

- 从 Explorer 和桌面真实执行“开始拖动 → 呼出 → Stage 松手”，每个有效源对象只形成一个可见且持久的 Stage 条目；
- Launcher/Stage 跨区移动只改变同一会话的目标，Drop 最多读取一次外国 `IDataObject`，取消、隐藏、关闭和新 DragEnter 均清理视觉与租约；
- 源文件字节、路径和数量在成功、失败、取消、重复、超限路径中均不变化；
- Drop 后的异步规范化或 SQLite 失败可观察，不把 OLE `Copy` 光标冒充为已成功落库；
- 全量基线无回归，10,000 条结构门禁证明提升产品上限无需迁移 Stage 主结构；本 MVP 的真机结论不得外推为拖出或取用已可用。

## 1. 完整文件型切片目标（MVP 后续）

以下内容已按 2026-09-12 合同更新，为文件型 M3 的完整目标。

中转站是一个**引用型、支持重复 occurrence、可消费的大容量拖放货架**：用户把稍后要交给其他窗口的文件或目录放入中转站，Vok 持久保存每次放入的独立引用；拖出成功后默认移除未固定 occurrence，固定项继续保留。

首个纵向切片只处理本地文件系统引用，并完成以下闭环：

```text
外部 FileDrop
  → 单一窗口级 OLE 路由判定 Stage 目标
  → Shell STA 规范化/校验
  → SQLite 单事务按 occurrence 做容量判断和批量插入
  → 无可见分页的连续数据虚拟化展示
  → 单项/批量拖出、排序、固定、打开/定位、移除
  → 按真实终态决定保留或消费
```

M3 文件型切片的完成定义：

- 文件、目录和 `.lnk` 各自作为原始引用拖入、跨重启保留并可再次取用；`.lnk` 不静默解析成目标；
- 启动台和中转站共用一个窗口级外部拖入会话，跨区域移动、取消、隐藏、关闭和异常都只终结一次；
- 单项与批量拖出使用真实 OLE `COPY`，并在外部明确接受后按固定状态决定是否消费；
- 只有外部交付已确认完成时才消费未固定项；失败、取消和“仅复制”均保留；
- SQLite 是 occurrence 容量、批量插入、排序和消费的唯一权威，UI 不维护第二套真相；
- 全量自动回归、schema 7→8 实库副本迁移、真实 Explorer 单项/批量拖入拖出和 50/10k/100k 分层规模门禁通过，且现有启动台、剪贴板及宿主事务无回归。

## 2. 首期产品契约

### 2.1 条目与引用

- 每一次有效放入对应一个独立 occurrence 和一张卡片；同一路径可在同批或跨批重复出现。
- 首期接受现存文件、目录和 `.lnk`。不复制、移动、删除或接管源内容，只保存绝对路径引用。
- 路径规范化沿用 Windows 文件引用规则：保留可展示的原值，另存大小写不敏感的 canonical key。canonical 只用于比较、Missing/重新关联辅助和索引查询，不参与唯一身份或去重。不得解析符号链接、junction 或 `.lnk` 后再合并 occurrence。
- 同一路径再次拖入必须新增 occurrence；同批重复也按来源顺序逐项保存。管理、排序与清理均按 `id + expected revision` 操作，不能按 canonical 误伤同源卡片。
- 源路径后来失效时不自动删行；`Missing` 是读取时得到的展示状态，不持久化成权威字段。卡片提供“重新关联”“复制原路径”“移除”，且失败检查不能阻塞 UI。

### 2.2 容量、保留与消费

| 规则 | 首期定义 |
|---|---|
| 默认容量 | 50 条 |
| 当前产品策略 | 默认/暂定上限 50 条；不是架构上限 |
| 架构规模 | 10,000 条完整工程门禁；宣布超大上限前增加 100,000 条压力门禁 |
| 容量计数 | 固定和未固定条目都计入 |
| 批量超限 | 按 occurrence 数量在同一事务判断；空间不足则整批拒绝，不部分插入、不去重、不静默淘汰 |
| 跨重启 | 全部条目、顺序和固定状态保留 |
| 成功拖出 | 未固定项移除，固定项保留 |
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
| 卡片主体拖出 | OLE `FileDrop + Preferred DropEffect(COPY)`，allowed effects 仅 `Copy` | `DoDragDrop` 返回 `Copy` 才消费未固定项；`None`/异常保留 |
| 选中组拖出 | OLE 载荷按 occurrence 顺序包含全部选中路径，allowed effects 仅 `Copy` | `Copy` 后一次事务清理全部未固定项；任一 revision 冲突时整批保留 |
| 卡片在 Stage 内拖动 | 仅中转站合成预览，松手一次 SQLite 提交 | 不触发 OLE，不消费 |
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
- 不把自定义高成本拖影、颜色分组、标签或搜索作为完成门禁；多选只为批量拖出提供会话级选择，不在本阶段引入批量内部排序。
- 不提供用户页码、较新/较早、Load More、整页跳转或 S4 式边缘换页；内部 keyset、双向预取、窗口淘汰和数据虚拟化必须保留。点击取用与 `CF_HDROP` 粘贴注入也不再是下一阶段完成条件。

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

### 3.1 大容量无可见分页边界

Stage 当前默认容量为 50，但存储和 UI 必须按可提高到超大上限设计：

- `stage_items` 不增加依赖 50 的数据库约束；容量由组合根策略和 SQLite 写事务判定，改变策略不迁移聚合或主表。
- 64 位计数、`(sort_order,id)` 复合 keyset 和 64 位稀疏顺序是高容量正式契约；禁止 `OFFSET`、全表物化、连续行号或回顶/排序全表更新。
- UI 不显示页面概念，只呈现连续像素滚动；内部使用双向 keyset、视口前后预取、稳定锚点与窗口淘汰。
- WPF 容器虚拟化只解决视觉容器，数据虚拟化还必须保证常驻 item VM、选择快照、图标、Availability 请求和动画状态不随总量线性增长。
- 启动不读取全部 occurrence，也不对全部路径同步做存在性或 Shell 图标检查；Availability 只跟随真实可见容器。
- 50 条用于完整物理功能 Gate，10,000 条验证工程窗口与排序，100,000 条验证启动/读取/写入/资源曲线；数值阈值先取同机基线。
- 若数据库实际条数高于当前策略，必须明确诊断并仍可有界读取；不得静默隐藏、删除或淘汰数据。

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

tools/Vok.StageDragProbe/            # MVP 后续的出站隔离探针
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

- 双向有界 keyset 读取，供连续数据窗口按锚点预取和淘汰；
- 批量 `AddBatch`，在一个事务内按 occurrence 完成容量检查、逐项插入、稳定输入顺序和结果计数；
- revision 保护的固定、重排和移除；
- revision 保护的成功消费；
- 明确区分 `CapacityExceeded`、`InvalidInput`、`NotFound`、`RevisionConflict`、`Cancelled` 和存储失败。

出站状态机约束 OLE 拖出的一次性终态：每个 generation 只能被 `Completed/Failed/Cancelled` 之一认领；迟到回调只能清理自己的资源，不能修改新代条目。

### 4.2 schema 8 目标（schema 7 为历史基线）

在现有单一 `SqliteStorageExecutor` 中把 schema 从 7 升至 8；表列可保持，但 canonical 不再唯一：

```text
stage_items
  id TEXT PRIMARY KEY
  target_kind INTEGER NOT NULL
  target_value TEXT NOT NULL
  canonical_key TEXT NOT NULL COLLATE NOCASE
  resolved_name TEXT NOT NULL
  sort_order INTEGER NOT NULL
  is_pinned INTEGER NOT NULL
  revision INTEGER NOT NULL
  created_utc TEXT NOT NULL
  updated_utc TEXT NOT NULL

INDEX stage_items_order ON stage_items(sort_order, id)
INDEX stage_items_canonical ON stage_items(canonical_key)
```

- schema 7→8 必须在同一事务删除 `ux_stage_items_canonical`、创建非唯一 `ix_stage_items_canonical`、核验结构并设置 `user_version=8`；失败完整回滚。既有行逐条保真，不合并或重排。
- `sort_order,id` 是稳定 keyset 游标；禁止 `OFFSET` 扫描大表。
- schema 不编码当前 50 条产品容量；提升产品上限只调整策略并增加门禁。
- 容量、批量顺序、occurrence id 与 revision 在 SQLite 执行器内判定；不能先由 UI 查数量再插入，也不能用 canonical 冲突实现去重。
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

## 6. 点击取用与仅复制（暂不进入）

本节保留为早期设计记录，不属于当前聚焦路线或完成门禁；只有拖入—排序—拖出闭环归档后，才能基于真实需求重新评审。

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

## 7. 出站 OLE 与后续独立 Gate

### 7.1 首选实现

- 卡片主体按下后未超过系统拖动阈值只更新选择/焦点，不触发已后置的点击取用；从未选卡开始拖出是单项，从已选卡开始拖出包含当前 selection snapshot。
- 在 WPF UI STA 创建只包含有序 `FileDrop`、`Preferred DropEffect=DROPEFFECT_COPY` 和进程内 source token 的 `DataObject`，调用 `DragDrop.DoDragDrop(..., Copy)`；重复路径 occurrence 不在 Vok 内去重。
- 第一个可证明 OLE 已进入源会话的 `GiveFeedback/QueryContinueDrag` 边沿触发同 generation 的隐藏请求；不得在鼠标阈值刚越过时用延时猜测。
- 隐藏失败设置源端取消标志，由后续 OLE 回调安全返回 cancel；所有 claim、事件订阅和视觉只清理一次。
- `DoDragDrop` 返回 `Copy` 才算 `Completed`；`None`、Esc、目标拒绝或异常均保留条目。批量 Copy 后在一个事务核对全部 `id + expected revision`，任一冲突整批保留且不重放。取消后不自动重新呼出 Vok，避免抢回用户正在操作的外部窗口。

### 7.2 出站 Gate 必须先证明

隔离探针不得读写产品数据库，至少覆盖：

- WPF 源 HWND 在 OLE 开始后隐藏，Explorer、桌面和普通支持 FileDrop 的目标仍能完成单项及 1/10/100 项 `Copy`；
- Explorer 不删除或移动源文件，返回效果只有 `Copy/None`；
- Esc、拖到空白/不支持目标、源路径中途失效、隐藏失败和目标异常都结束嵌套消息循环并释放 capture/claim；
- 隐藏发生在 OLE 真实开始之后，快速点击、未过阈值和拖动初始化失败不会误隐藏；
- 同一 Vok 窗口拒绝自己的出站 token，Launcher/Stage 入站视觉不残留；重复路径和目标应用可能拒绝/合并重复载荷的行为必须被实际记录，不能冒充逐项成功回执；
- 20 轮成功/取消交替后线程、句柄、事件订阅和工作区 generation 回到稳定基线。

如果 WPF `DoDragDrop` 在隐藏源 HWND 后无法稳定完成，唯一允许的降级是在 **WPF UI STA** 上封装原生 `DoDragDrop/IDropSource`；不得把 OLE 源放到 Shell STA、增加固定 sleep 或保留不可见源窗骗过测试。出站 Gate 只允许保留一个通过真实结果的实现分支。

## 8. 展示、滚动与手势

- 中转站使用 Recycling/Pixel 容器虚拟化并叠加有界数据窗口；用户看到连续滚动，不显示分页按钮、页码或整页替换。
- 接近窗口边缘时异步预取，离开足够距离后淘汰旧 VM；generation + anchor 拒绝迟到结果，稳定 occurrence id/锚点保持像素位置。
- 每张卡显示类型图标、名称、必要的父路径摘要、固定/失效状态；完整路径放入 ToolTip 和自动化名称，不把长路径撑破布局。
- 图标和可用性只为可见项异步加载，经 item id + revision 校验后回填；旧代和已回收容器的结果直接丢弃。
- 卡片非按钮区域的首选交互是假设一次左键拖动：指针留在 Stage 内时直接排序，横向越出 Stage 后单向升级为出站 OLE；固定、菜单和辅助按钮不参与两者。无修饰手势为单卡，Ctrl 切换、Shift 范围形成批量拖出选择；该假设必须先通过 N2 物理 Gate。
- 内部排序沿用“零集合变化预览、终态认领、一次 SQLite 提交、取消完整回滚”的机制；拖动热路径只更新合成属性。
- 空状态只说明“将文件或文件夹拖到这里”；容量拒绝、路径失效、仅复制降级和清理延迟必须是可观察结果。

## 9. 分阶段实施与提交门禁

### M3.MVP1：Core、schema 7 与入站仓储

状态：已于 2026-09-04 完成实现与自动门禁；Release 71/71 Core + 385/385 集成共 456/456，构建 0 warning / 0 error。未接 WPF/OLE、未迁移真实产品库，不能据此宣称入站 MVP 已可用。

交付：本轮所需的 `StageItem`、`StageTargetDescriptor`、页面/新增结果、`StageOptions`、`IStageRepository`、文件规范化窄接口、`SqliteStageRepository`、schema 6→7 迁移和 DI 注册；不接 WPF/OLE。

门禁：文件/目录/`.lnk` 身份、批量原子性、容量、canonical 去重回顶、64 位稀疏排序、10,000 条 keyset 存储健壮性、迁移成功/回滚全部通过；schema 和仓储 SQL 结构不依赖产品容量；全量基线无回归。

具体 Core 契约、SQL 算法、迁移风险、实施检查点和自动验收矩阵见 `M3_STAGE_MVP1_PLAN.md`。

### M3.MVP2：真实 Stage 展示与唯一入站路由

状态：已于 2026-09-04 完成实现与自动门禁；固定 64 项 keyset 页面、真实 Stage WPF、唯一三态 Router、异步入站状态、生命周期与 DI 已落地，Release 71/71 Core + 395/395 集成共 466/466，构建 0 warning / 0 error。未读取真实产品库、生成发布候选或执行 Explorer/桌面物理验收。

交付：用真实 `StageView` 替换中转站空区域，接入有界展示和入站状态；把 `MainWindow` 当前面向 Launcher 的拖放代码收敛为唯一 `WorkspaceFileDropRouter`，由 Launcher/Stage target adapter 分发。现有 Workspace 外部拖放租约继续由宿主拥有，不创建第二套租约。最初实现的页面控件属于历史事实，将在聚焦路线 F0 移除。

门禁：跨 Launcher/Stage/无目标区域移动只更新一个命中目标；Enter/Over 不读取外国数据，Drop 最多读取一次；Drop 先认领并清理视觉再异步入库；DragLeave、Esc/取消、隐藏、设置、关闭、新 DragEnter 和异常均幂等收口；源文件零改动。

固定大小页面状态、WPF 结构、Router 终态顺序、目标 adapter、实施检查点和自动验收矩阵见 `M3_STAGE_MVP2_PLAN.md`。

### M3.MVP3：入站 MVP 验收

状态：已于 2026-09-05 按实际完成范围归档。当前 schema 7 在线快照幂等、真实数据派生 schema 6→7 回放和既有 schema 5 历史备份直升三组互补数据证据通过；Release 468/468、独立候选、Explorer/桌面代表矩阵和用户 5 组共 50+ 次物理循环通过。首次真实 6→7 现场不可回溯，50+ 次也不冒充计划的 100 次；KI-001 单独延期。

交付：实时库 SQLite Online Backup、三组副本迁移/幂等报告、WPF 浅/深与窄窗口渲染、自动回归、独立自包含发布候选和真机记录。

门禁：Explorer/桌面的文件、目录、`.lnk`、Unicode 路径和批量项完成“拖动中呼出 → Stage 松手 → 当次或下次呼出可见 → 重启仍存在”；重复回顶和 100 次成功/取消交替无租约、线程、句柄或队列单调增长；超限整批拒绝、无效项和持久化失败在隔离数据/受控 `IDataObject` 验证，不能为覆盖反例而填满或破坏真实库；既有 Launcher 拖入和进入阶段的 466 项基线保持通过。达到此处只宣布“入站 MVP 完成”。详细安全边界、检查点和验收矩阵见 `M3_STAGE_MVP3_PLAN.md`。

### M3.1：管理动作（MVP 后续）

交付：固定、手工排序、打开/定位、移除、重新关联和 Missing 可见项检查。

状态：Checkpoint 0–5 与排序 S0–S3 已完成；revision-safe 管理动作、V2 卡片、Availability、外部拖入反馈和普通卡面直接排序均已落地。S3 修订后的零集合变化 transform 预览已通过用户观感 Gate 并提交。整页切换式 S4 候选被用户否决并完整回退；M3.1 只归档当前已加载窗口的排序能力。未来连续数据窗口间排序属于 N1/N4，不恢复 S4 页跳转。schema 仍保持 7，外部拖放 Router 与 KI-001 未修改。

### M3.2：仅复制与点击取用（暂不进入）

不再作为下一阶段或文件型 Stage 完成条件。拖入—排序—拖出闭环完成后，只在真实使用反馈证明需要时重新评审。

### M3.3：出站 Gate 与卡片拖出（N0/N1 之后）

当前必须先按聚焦路线完成 N0 occurrence/schema 8 与 N1 连续数据虚拟化，再按 `M3_STAGE_N2_PLAN.md` 以隔离探针验证第 7 节的隐藏源 HWND、单项/批量 Copy/None、自吞拒绝和资源闭环。N2 先裁决 WPF/原生唯一 source 方向，再只接入未连接 Stage UI/数据库消费的 workspace outbound claim、Preserve-current-foreground 隐藏和 product Router token 门禁；N3 才实现 selection、正式 `StageDragOutController` 与 Copy 后消费事务。出站探针不得改写已归档的 M3.MVP1–M3.MVP3 历史结论。

### M3.4：完整文件切片收口与归档

交付：完整故障注入、可访问性、响应式视觉、拖入/排序/拖出物理矩阵、自包含发布和文档归档。只有“文件拖入 → 排序 → 重启 → Copy-only 拖出 → 正确保留/消费”的真实闭环完成后，完整 M3 文件切片才可归档。

每个阶段只引入一个主要变量，均以进入阶段时的全量测试为回归底线；一处连续失败两次应回到所有权/时序假设重新诊断，不能叠加延时或旁路。

## 10. 验收矩阵

### 历史入站 MVP 自动验证（schema 7 去重基线）

- 隐藏态开始的同一次物理按压只能创建一个 Workspace generation；短按保持显示，原始长按松键在真实 FileDrop lease 内只延后到 Drop/取消终态；
- router 覆盖 Launcher/Stage/无目标跨区、一次 `GetData`、DragLeave 缺失、隐藏、设置、关闭和新会话替换，且不会产生双重 `ExternalDragEnded`；
- 同批重复、跨批重复、大小写差异、目录尾分隔符和 `.lnk` 身份符合 canonical 规则；
- 49/50/51 条、混合重复批次、无效项和并发添加遵守容量与整批原子性；
- schema 6→7 保留现有数据，半迁移、未知结构和未来版本安全拒绝并回滚；
- 10,000 条内部存储门禁不使用 OFFSET，回顶不全表改写；当时产品 UI 只承载固定页面；
- Stage 入站的成功、拒绝、取消和异常均不修改任何源文件；现有 429 项基线持续通过。

### 历史入站 MVP 真机验证（已完成范围）

- 在 Explorer/桌面按住鼠标拖动文件、目录或 `.lnk`，用当前快捷键呼出 Vok，在 Stage 松手后条目可见且重启仍存在；
- 一次拖入多个对象保持来源顺序；重复对象回顶但不新增；无效对象和容量拒绝给出真实结果；
- 指针在 Launcher/Stage 之间来回移动时只有当前区域显示命中反馈；拖到无目标区域或按 Esc 取消后无蓝框/租约残留；
- 成功、失败和取消前后比较源路径、长度与哈希，确认 Vok 只保存引用；
- 100 次拖入/取消交替后，UI、热键和剪贴板监听持续响应，线程、句柄和数据库队列无单调增长。

### 当前完整文件切片自动验证

- 同批重复、跨批重复、大小写相同 canonical、目录尾分隔符和 `.lnk` 均创建独立 occurrence，并可按 id 分别管理；
- 49/50/51 条、混合重复批次、固定占满和并发添加均按 occurrence 遵守当前 50 策略及整批原子拒绝；
- schema 7→8 保留全部现有行，唯一索引迁移为普通索引；半迁移、错误索引和未来版本安全拒绝并回滚；
- 10,000 条工程门禁和 100,000 条压力门禁不使用 OFFSET，不全表物化或全表重排；数据窗口滚动/预取/淘汰后常驻 VM/资源有界，迟到图标/Availability 不串卡；
- router 覆盖跨区、无目标、一次 GetData、自身 token、DragLeave 缺失、隐藏、设置、关闭和新会话替换；
- 单项/批量拖出覆盖 `Copy/None`、Esc、隐藏失败、源失效、重复路径、selection revision 冲突、双启动、旧代回调和 Dispose；Copy 后冲突整批保留、不部分清理或重放；
- Stage 删除、容量拒绝、排序和消费均不修改任何源文件；现有 429 项基线测试持续通过。

### 当前完整文件切片真机验证

- Explorer/桌面的文件、目录、Unicode 长路径和 `.lnk` 单项/批量拖入后跨重启可见；重复路径形成可独立操作的多张卡且不复制源内容；
- 跨 Launcher/Stage 区域来回移动，蓝框/命中效果准确，取消和窗口隐藏后无残留；
- 单项/批量拖出到 Explorer/桌面只复制，不移动；重复路径载荷按 occurrence 顺序构造，但明确记录目标应用对重复路径的实际处理；Esc 和拒绝目标后条目保留，Vok 不抢回前台；
- 删除或改名源文件后显示失效，重新关联恢复；“移除”只删 Stage 行；
- 100 次拖入/排序/取消和 20 次 OLE 成功/取消交替后，UI、热键和剪贴板监听持续响应，线程/句柄/数据库队列无单调增长。

## 11. 后续顺序

文件型 M3 归档后再评审：

1. Stage 文本：独立文本聚合和写回语义，不借用剪贴板历史生命周期；
2. 模块中立资产引用：让所有消费者共同参与“引用为零”的删除证明；
3. Stage 图片：只有第 2 步完成后才接入捕获、展示、拖出和回收；
4. 批量内部排序、标签/分组和搜索：由真实文件切片使用数据决定，不预埋通用模型。多选与批量拖出已进入当前文件闭环，不列为后置项。

任何后续类型都必须保持本计划的两个核心不变量：窗口级 OLE 只有一个所有者，外部交付结果与内部条目清理结果分开提交。

文件型 Stage 当前默认容量为 50，但架构不得固定上限；提高容量时只能调整产品策略和门禁，不能迁移聚合或把全部数据常驻。用户可见分页、页码和 S4 整页跳转不再进入；内部 keyset、连续数据窗口和规模门禁是高上限的必要实现。
