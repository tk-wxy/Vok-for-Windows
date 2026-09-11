# M3 中转站卡片重设计与前端效果计划

> **状态（2026-09-11）**：Redesign Gate 0 V2 静态视觉、Gate 1 Hover/KeyboardFocus 与 Gate 3 外部拖入均已完成对应 Gate。Gate 2 将与 M3.1 Checkpoint 6 真实排序合并，不单独交付假拖影或空模式壳层；排序 S0–S2 数据前置现已完成，下一步合并整卡同页真实拖动。当前唯一默认 Release PID 1512 已包含 S1/S2 数据代码，但没有可见排序消费者。

> **路线调整**：用户指出当前只有 Gate 1 可实际测试，并已确认 `M3_STAGE_UI_FOUNDATION_NEXT_PLAN.md` 的调整：先完成已有真实 Router 支撑的 Gate 3，再采用显式排序模式，把 Gate 2 拖影视觉与真实 Checkpoint 6 合并为可测试纵向切片。该文件覆盖本计划原先的纯拖影 Gate 2 顺序。

> **方向校正**：此前“默认弱容器、Accent 图标主导”的判断实际对应 Workbench Launcher，不对应用户给出的 Workbench 中转卡。新的静态权威计划为 `M3_STAGE_CARD_REDESIGN_GATE0_V2_PLAN.md`；旧 Gate 0 的 571/571 只保留为行为回归证据，不算视觉通过。

## 1. 结论

现有卡片已经证明行虚拟化、可见项 Availability 和管理动作可以稳定共存，但第一版重设计仍沿用了 124 DIP 管理宫格，并把 Workbench Launcher 的透明图标单元误当成中转卡。用户给出的中转卡参考实际是持续可见的两段式文件卡。

当前只冻结 Workbench 中转卡的前端视觉规律，而不复制其代码：

- 卡片持续显示细描边圆角表面；
- 上方为中性预览/类型区，下方为名称/meta 标签区；
- 宽 72 DIP、gap 6 DIP、最多 10 列；
- 高度与 Vok 启动台卡面完全一致，纵向 row unit 同节奏；
- pin/menu 叠在预览区，不再用独立控制带占高。

详细静态权威合同见 `M3_STAGE_CARD_REDESIGN_GATE0_V2_PLAN.md`。Hover、键盘焦点、内部拖动、外部拖入新效果和排序继续保留在路线中，但当前全部暂停，不能混入 V2 静态实施。

## 2. 已核验事实

### 2.1 Workbench 外部形态

已有浅/深截图能直接证明：

- 默认 Launcher 单元没有持续可见的整卡描边，主要视觉是圆角 Accent 图标块和两行名称；
- Hover 时出现浅色圆角底、细描边和轻微层级提升；
- 外部 FileDrop 命中 Launcher 时，以整块分区的 2 DIP Accent 边框反馈目标。

现有证据没有完整捕获 Workbench 内部拖动的每一帧，因此本计划不冒充已经知道其拖影尺寸、透明度或缓动。内部拖动态先通过 Vok 自有可交互视觉原型择优，再由用户确认。

### 2.2 Vok 当前基础

- `StageViewModel.Items` 是当前页最多 64 项的唯一扁平业务顺序；`StageCardRowsProjection` 只在 App 层分行。
- 外层 `VirtualizingStackPanel` 已按行 Recycling/Pixel 虚拟化，当前 260/520/720/800/960 DIP 外宽形成 3/6/9/10/10 列。
- Missing、固定、打开、定位、复制路径、重新关联和安全移除已经可用。
- `WorkspaceFileDropRouter` 是唯一窗口级 OLE 路由；Stage 只接收 `sessionActive + targeted` 两个呈现事实。
- Gate 3 已在既有整区 2 DIP Accent 边框上加入 0.08 wash 与明确松手文案，并继续由真实 Router 状态驱动；人工视觉确认已通过。
- Gate 1 Hover 已实现；V2 已移除 `StageReorderSlot`，内部拖影与源卡 dragging 状态仍不存在。

## 3. 本轮范围

### 3.1 实现

1. 重构卡片默认、Missing、Pinned、Unavailable、Busy 的静态视觉层级；
2. 实现鼠标 Hover 与键盘焦点等价反馈；
3. 在显式排序模式中把源卡、拖影、目标插槽和取消恢复视觉与真实 Checkpoint 6 排序一同实现；
4. 重设计外部拖入的非目标、命中、离开和取消视觉；
5. 生成浅/深、宽窄、高 DPI、长名称和组合状态截图及独立候选；
6. 保持所有既有管理命令、Automation、Availability 和虚拟化生命周期有效。

### 3.2 不实现

- Gate 3 与排序模式壳层阶段不实现 `ReorderAsync`、复合邻接、稀疏中点、局部重平衡或任何 SQLite 顺序写入；这些只在 Gate 2 + Checkpoint 6 纵向切片进入；
- 不让用户在正式产品里执行“看起来能排序、松手却无结果”的空拖动手势；正式鼠标入口只随 Checkpoint 6 的真实提交/取消终态开放；
- 不改变 `WorkspaceFileDropRouter`、Drop 数据快照、`DragDropEffects.Copy`、异步入站或 Workspace drag lease；
- 不实现点击取用、OLE 拖出、消费、多选、筛选、颜色标签或 Shell 缩略图；
- 不修改 schema 7、Stage Core、仓储、DI、真实数据库、容量策略或分页；
- 不重开 KI-001 的活动外部拖动显隐路径。

## 4. 卡片视觉合同

### 4.1 默认结构

V2 使用固定 72 DIP 宽、6 DIP cell 间距、最多 10 列；高度绑定同窗 Launcher 卡面：

```text
┌────────────────┐
│ pin        more│
│    [ icon ]    │  上部中性预览区
├────────────────┤
│ 文件名…         │  10 DIP Medium，单行
│ .docx / 文件夹  │  9 DIP meta/异常状态
└────────────────┘
```

- 外层持续显示主题动态表面与 0.5 DIP 细描边，CornerRadius 9 DIP；
- 底部标签区固定 32 DIP，上部预览区使用剩余高度；
- 中性图标内框 40×40 DIP、9 DIP 圆角，不再使用默认 Accent 蓝块；
- 名称和 meta 各一行左对齐；异常 availability 替换 meta，不再增加状态行；
- 完整路径、固定与 availability 语义继续保留在 ToolTip/Automation；
- Shell 图标、图片缩略图和文本预览只预留宿主，本轮不实现。

### 4.2 管理入口

- 删除独立 28 DIP 控制带与排序预留行；
- pin 与更多保留不小于 24×24 DIP 的透明命中区，视觉 glyph 约 12 DIP，叠在预览区左上/右上；
- 未固定 pin/menu 默认 Tertiary 低强调，Pinned pin 保持 Accent；本轮不加入显隐动画；
- ContextMenu 继续使用应用字体，不继承 `Segoe Fluent Icons`；中文、长名称和 200% DPI 下不得裁切。

## 5. 前端效果状态矩阵

| 状态 | 卡片表面 | 内容与操作 | 允许的终态 |
|---|---|---|---|
| Default | 持续可见两段式表面、0.5 DIP 描边 | 中性预览 + 单行名称/meta；角落管理入口低强调 | 仅既有管理命令 |
| Hover | 120 ms 显示 Subtle fill/细描边，约 1.02 scale，ZIndex 提升 | pin/menu 淡入；名称和状态不位移 | 离开 160 ms 恢复 |
| KeyboardFocusWithin | 与 Hover 同等可见，不依赖鼠标 | 焦点框清楚，操作入口完整可见 | Tab/Shift+Tab 顺序稳定 |
| Missing/Unavailable | 默认/悬浮表面不变 | glyph 与状态使用语义色，危险动作仍按现有规则禁用 | 不改变 Availability 真相 |
| DragSourcePreview | 原位轮廓保留，内容约 0.25–0.35 opacity | 隐藏 hover/action chrome | 本轮只能取消并恢复 |
| InternalDragGhost | 同尺寸共享内容模板、Accent 2 DIP 描边、约 0.94–0.96 opacity、1.02 scale | 无按钮、无焦点、无命中 | 仅视觉 Gate；不提交顺序 |
| ExternalDragActive/NotTargeted | 不显示接收边界 | 所有卡片停止 Hover，管理命中禁用 | Router 切目标或终结 |
| ExternalDragTargeted | Stage 整区 2 DIP Accent 边界 + 低透明 Accent wash + “松开以存入中转站”提示 | 卡片保持可读但退居背景 | 仍只由现有 Router Drop/Cancel 终结 |

Hover 动画沿用已验证的 `SnapshotAndReplace` 语义，不堆积 animation clock。首候选不使用 WPF `DropShadowEffect`：全屏、多卡和滚动场景下它可能引入软件渲染成本，Workbench 的层级可由 fill、stroke、scale 和 ZIndex 表达。

## 6. 内部拖动态的安全边界

用户要求本轮设计拖动效果，但排序明确不实现。为避免把假能力打入产品：

- 产品卡片本轮不新增 `PreviewMouseLeftButtonDown/Move/Up` 排序入口，也不捕获鼠标；
- 在 `StageView` 表现层建立可复用的源卡/拖影视觉资源，隔离 WPF Gate 可以用鼠标移动它、录制截图或短视频；
- Gate 的松手、Esc、失捕、隐藏、DataContext 更换、外部 DragEnter 和 Unloaded 都只清视觉并恢复源卡；
- 不增加 `IsDragging` 到 `StageItemViewModel`，不调用 ViewModel/Repository，不移动 `Items`，不产生插槽或“将会放在这里”的假排序承诺；
- Checkpoint 6 后续只负责把真实 generation、鼠标捕获、二维插槽和提交终态接到这套已冻结视觉上。

若用户在视觉验收时明确要求正式候选也能用鼠标拖动预览，则只能作为标记清楚的设计候选存在；归档到产品前仍必须移除无功能入口或同时实现真实排序，不能保留无效手势。

## 7. 外部拖入效果

外部拖入继续由现有 `SetExternalDragState(sessionActive, targeted)` 驱动，不新增第二套 OLE 状态：

1. `sessionActive=true, targeted=false`：隐藏接收提示，停止卡片 Hover/管理命中，防止外部 OLE 指针与内部 UI 同时表达可操作；
2. `sessionActive=true, targeted=true`：显示整区 Accent 边界、轻量 wash 和居中/顶部紧凑提示；提示层 `IsHitTestVisible=false`，不能挡住唯一窗口级 Drop；
3. `sessionActive=false`：一次性移除边界、wash、提示和抑制状态；
4. Drop/DragLeave/取消/隐藏/Dispose 的清理仍由 Router 现有终态拥有，视觉代码不得用 Timer 猜测拖动已经结束；
5. 不给单张现有卡显示 Drop target，因为外部拖入语义是“加入/去重回顶到 Stage”，不是覆盖或插入该卡。

## 8. 实施顺序

### Redesign Gate 0：静态视觉候选

> 2026-09-09 实施状态：第一版视觉 Gate 不通过；V2 已实施并完成整体对齐修正，用户静态视觉 Gate 已通过。详细权威合同见 `M3_STAGE_CARD_REDESIGN_GATE0_V2_PLAN.md`。

- 详细文件级实施、尺寸分配和状态文案见 `M3_STAGE_CARD_REDESIGN_GATE0_PLAN.md`；
- 按 V2 权威计划把宽度冻结为 72 DIP、gap 6 DIP、最多 10 列，并与 Launcher 共用 surface/unit height；重构两段式 card surface、图标内框、名称/meta 和覆盖式管理 chrome；
- 保持当前布局计算器、行投影、页面与 Availability 不变；
- 生成浅/深、72 DIP 固定宽、与 Launcher 同高、260/520/720/784/800/960 DIP、长中文/Latin/emoji、File/Directory/`.lnk`、Pinned/Missing/Pinned+Missing 截图；
- 只调整一组静态变量后复验，用户确认后冻结候选。

通过条件：默认视觉达到“持续可见的两段式文件卡”，同窗 Stage/Launcher 卡面严格等高，800 DIP 中区形成 10 列，最窄宽度仍不重叠，所有既有管理动作和完整语义保持。

### Redesign Gate 1：Hover 与键盘焦点

> 2026-09-09 实施状态：产品候选与自动门禁已完成，等待用户视觉 Gate。首次实现让 MouseOver、FocusWithin 和 ContextMenu 三个触发器分别写同一动画，定向测试复现菜单打开时 FocusWithin 退出动画覆盖菜单进入动画；最终收敛为 view-owned 单一卡片交互状态，再由该状态唯一驱动画面。100 轮聚焦/外部抑制/换列/滚动/显隐与最终静止态、菜单上下文、浅色 Hover/键盘焦点、暗色和 260 DIP 窄布局均通过；未修改 VM、仓储、Router 或排序入口。

- 增加 Hover/FocusWithin 的 fill、stroke、scale、ZIndex 和 action chrome 过渡；
- 快速进出、跨卡移动、滚动回收、主题切换和窗口隐藏都恢复默认状态；
- 键盘焦点与鼠标悬浮视觉等价，ContextMenu 打开时所属卡保持明确上下文。

通过条件：动画无布局跳动、无裁切、无时钟堆积；100 次 hover/滚动/隐藏循环后无残留变换或待办。

### Redesign Gate 2：与 Checkpoint 6 合并的真实排序拖动

- 先以标题栏显式排序模式隔离普通模式下未来的点击取用与 OLE 拖出；
- 只在排序模式中增加复用 `StageCardContentTemplate` 的 drag ghost、源卡 dim、真实二维目标插槽和 view-owned 清理入口；
- 超过系统拖动阈值后才预览，松手最多提交一次 revision-safe 仓储事务；取消恢复原顺序，不暴露松手永远取消的假能力。

通过条件：排序模式边界清楚；成功、取消、Esc、失捕、隐藏、换列、外部 DragEnter 和回收均有真实终态，100 轮后无残留或重复提交。

### Redesign Gate 3：外部拖入视觉

> 2026-09-09 实施状态：表现层、自动门禁和用户真实拖入视觉 Gate 均已完成。现有唯一 Router、Drop 快照、Workspace lease、入站持久化和 KI-001 均未修改。

- 在现有整区边界上加入轻量 wash 与明确文案，复核 Stage/Launcher/None 跨区；
- 定向测试 `active/targeted` 全状态转换、交互抑制和一次性清理；
- Explorer/桌面真实拖入只验证视觉和既有入站零回归，不进入 KI-001 的反复关闭路径。

通过条件：同一时刻只有一个分区高亮，Drop/取消/跨区后无残留；既有 Copy-only 入站、持久化和 Router 终态不变。

### Redesign Gate 4：候选与人工验收

- Stage 定向、MainWindow、Router、完整 Release 与零警告构建；
- 100%/200% 系统 DPI、浅/深、260/520 等效宽度、滚动/翻页、长名称、Pinned+Missing、菜单和键盘；
- 生成独立自包含候选和固定截图/短视频证据；
- 用户确认静态、Hover、拖影和外部拖入四类视觉后，才归档卡片重设计并恢复 Checkpoint 6 规划。

## 9. 文件边界

预计产品修改集中在：

- `src/Vok.App/Features/Stage/StageView.xaml`
- `src/Vok.App/Features/Stage/StageView.xaml.cs`
- 必要时增加一个仅负责视觉状态、无 Core 依赖的 App 层 presenter/attached state
- `src/Vok.IntegrationTests/Stage/StageViewIntegrationTests.cs`
- 必要时增加独立的 Stage 视觉状态测试文件

不得修改：

- `Vok.Core.Stage`、`IStageRepository`、`SqliteStageRepository` 与 schema；
- `WorkspaceFileDropRouter`、`MainWindow` 的 OLE 终态和 `WorkspaceController` drag lease；
- Launcher、Clipboard 的业务或样式；
- 真实产品数据库和 KI-001 路径。

## 10. 自动验收矩阵

- 默认态持续显示两段式表面；Stage 卡面/row unit 与 Launcher 完全相等；
- 72 DIP 卡内 40 DIP icon、两行标签、pin/menu 覆盖命中区均不越界；
- 260/520/720/784/800/960 DIP 分别满足 V2 固定宽度列数合同，784/800 达到 10 列；
- 浅/深动态资源切换后不存在硬编码白底/黑字；
- Pinned、Missing、Unavailable、Pinned+Missing 同时具有文字/Automation 语义；
- Hover 进入/退出、ContextMenu 打开/关闭和键盘焦点恢复稳定；
- 排序模式中的源卡、ghost、目标插槽与清理可重复 100 次，成功只提交一次、取消不提交；
- 外部 drag 在 Launcher/Stage/None 间切换时只有当前 target 可见，终结后全部清零；
- Recycling、换列、翻页、隐藏、DataContext 换绑和 Dispose 后无残留动画、ghost、overlay 或 Availability 请求；
- 当前 567/567 Release 基线只增不减，构建保持 0 warning / 0 error。

## 11. 停止条件

- 若 Hover 必须依赖改写业务 VM 或数据库状态，停止并收回 App 表现层；
- 若隐藏按钮会造成不可见键盘焦点或 260 DIP 下无法发现管理入口，停止并调整 chrome 层级；
- 若 scale 导致相邻卡片裁切/覆盖，先只调整 scale/clip，不同时改卡宽与间距；
- 若动画依赖固定延时完成业务清理，停止并改为属性/生命周期终态；
- 若内部拖动在普通模式触发，或排序模式的插槽没有真实提交/取消终态，停止并收回该手势；
- 若外部拖入样式要求修改 Router、OLE 读取或显隐租约，拆为独立高风险任务；
- 若真实 DPI 失败，不用隐藏状态、缩小命中区或降低可访问性掩盖。

## 12. 完成定义与后续

本计划完成只表示“中转站卡片样式与前端状态已冻结并进入产品”，不表示排序、取用或拖出完成。

Gate 3 人工确认后先实现不接拖动的显式排序模式壳层；其视觉 Gate 通过后，恢复 M3.1 Checkpoint 6，先实现布局无关的 revision-safe 排序内核，再把真实二维插槽、鼠标会话、drag source/ghost 和提交终态作为同一纵向切片接入。M3.2 点击取用与 M3.3 OLE 拖出仍按原路线后置。
