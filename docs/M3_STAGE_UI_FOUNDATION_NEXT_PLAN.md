# M3 中转站 UI/交互基础下一步实施方案

> **状态（2026-09-11）**：Gate 3 表现层及真实拖入人工 Gate 已完成；排序 S0 合同和 S1 Core/同快照分页边界/SQLite 单项快路也已完成。下一步进入 S2 最多 64 项局部重平衡，之后把模式 UI、Gate 2 拖影与真实同页排序合并；Checkpoint 6、取用与拖出仍未完成。

## 1. 结论

下一步不宜直接把“只显示拖影、松手永远取消”的手势接入正式产品。它既不能让用户验证完整交互，也会形成看似可排序但没有结果的假能力。

已采用的后续顺序为：

1. 关闭 Gate 1 实际视觉验收；
2. 先实施可由现有真实外部拖入链路完整驱动的 Gate 3；
3. 先完成 S0 合同冻结与 S1/S2 数据安全基础，不向正式产品暴露空排序入口；
4. 以显式“排序模式”分配排序与未来拖出的手势，并将模式 UI、Gate 2 拖影视觉与真实同页排序合并为可测试纵向切片；
5. 完成 Gate 4 综合候选后，再进入 M3.2 点击取用与 M3.3 OLE 拖出。

这仍然遵守“UI/交互基础先行”：先冻结真实可观察状态与手势边界；只有无法脱离业务终态验证的内部拖动，才与最小必要排序能力一同实现。

## 2. 依赖诊断

### 2.1 外部拖入视觉已经有真实状态源

- `WorkspaceFileDropRouter` 已唯一拥有外部 FileDrop 会话，并向 Launcher/Stage 分发 `sessionActive + targeted`；每次切目标会先清除所有 target，再只激活一个 target。
- `StageView.SetExternalDragState` 已区分未活动、活动未命中、活动且命中，并在活动期间禁用卡片命中、关闭菜单和清理 Gate 1 状态。
- 当前 `DropOverlay` 只是无内容的 2 DIP Accent 边框，所以用户看到的是旧入站 MVP 的命中反馈，不是 Gate 3 完整效果。
- 因此 Gate 3 可以只修改表现层，使用真实 Explorer/桌面拖动直接验收，不需要假入口、第二 Router 或数据库变化。

### 2.2 内部拖影没有独立的产品终态

- 当前 Stage 没有按下、系统拖动阈值、鼠标捕获、源卡状态、拖影、目标插槽或取消会话。
- 若只接拖影，松手只能取消；正式产品会暴露无结果手势，与产品“不以虚假能力冒充完成”的边界冲突。
- 若卡片主体直接承担排序拖动，后续 M3.3 的文件 OLE 拖出会争用相同按下/移动阈值，届时仍需重做手势分配。
- 原 Checkpoint 6 设想独立 24 DIP 排序把手，但 V2 的 72 DIP 卡片顶部已有 24 DIP pin、24 DIP menu 和居中的 40 DIP 图标；第三个命中区会遮挡预览或压缩既有操作，旧把手布局已不适配 V2。

### 2.3 已确认显式排序模式

在 Stage 标题栏提供明确的“排序”入口：

- 普通模式：保留 Hover、pin/menu 和未来点击取用/拖出语义，卡片拖动不解释为排序；
- 排序模式：隐藏或禁用 pin/menu，整张卡片成为清晰的排序拖动面，标题栏显示“完成/退出排序”与状态说明；
- 进入模式不修改数据；只有真实拖动超过系统阈值、松手并通过仓储事务后才提交；
- Esc 优先取消当前拖动，再退出排序模式，之后的 Esc 才隐藏 Vok；
- 外部 DragEnter、设置、隐藏、列数变化、换页失败和 Dispose 都取消内部会话并退出排序模式。

该方案用显式模式隔离“内部重排”和“向外拖出”，也避免在 72 DIP 卡片上增加第三个重叠按钮。它是本计划唯一需要用户确认的产品交互选择。

## 3. 阶段 A：Gate 1 关闭

当前代码和自动门禁已经完成，只补用户实际确认：

- Hover 的 fill、0.5 DIP stroke、1.02 scale 和按钮淡入是否合适；
- Tab/Shift+Tab 焦点是否清楚且顺序稳定；
- ContextMenu 打开时所属卡是否持续明确；
- 离开、关闭菜单、隐藏再呼出后是否完全复原。

若只调整强度，一次只改变一组命名参数，不改状态所有权。

## 4. 阶段 B：先实施 Gate 3 外部拖入视觉

### 4.1 产品改动

只扩展 `StageView` 现有状态，不修改 Router：

- `Inactive`：无 overlay，Gate 1 正常工作；
- `ActiveNotTargeted`：无接收边界，暂停 Hover/键盘/菜单命中；
- `ActiveTargeted`：整区 2 DIP Accent 边界、低透明 Accent wash、非命中的卡片保持可读，并显示“松开以存入中转站”；
- overlay 全部 `IsHitTestVisible=false`，不能改变 `ResolveDropTarget`；
- 重复 DragOver 的相同状态不得重启动画；Drop、DragLeave、取消、隐藏和换代由现有 Router 终态一次清零。

预计修改：

- `src/Vok.App/Features/Stage/StageView.xaml`
- `src/Vok.App/Features/Stage/StageView.xaml.cs`
- `src/Vok.IntegrationTests/Stage/StageViewIntegrationTests.cs`
- 必要时只增加 Stage 表现层状态值对象，不进入 Core。

### 4.2 Gate

- `Inactive → ActiveNotTargeted → ActiveTargeted → ActiveNotTargeted → Inactive` 全矩阵；
- Launcher/Stage/None 跨区时同一时刻最多一个 target 可见；
- 空列表、满页、Missing+Pinned、处理中、浅色、深色、260/520/800 DIP；
- Drop/取消后 overlay、菜单、Hover、焦点和动画时钟无残留；
- 真实 Explorer/桌面拖入成功、取消和跨区移动；不测试 KI-001 的活动拖动中反复关闭。

完成后用户可直接在正式候选中测试，不需要测试专用入口。

## 5. 阶段 C：排序 S0 合同与数据前置（2026-09-11 修订）

Gate 3 人工确认后，不再单独发布可观察但不能拖动的模式壳层。先完成：

- S0：冻结整卡拖动、“完成/退出排序”、Esc 分层及 committing 不可伪取消合同；
- S1：Core 排序请求/结果、同快照页外直接邻项和 SQLite 单项快路；
- S2：数值间隔耗尽时最多 64 项的局部重平衡。

标题栏入口、模式状态、pin/menu/分页显隐、焦点和整卡鼠标手势将在首个真实同页排序纵向切片中一起进入产品。每个拖动松手独立保存，因此退出按钮不得命名为能够撤销既有提交的“取消”。详细实施与门禁见 `M3_STAGE_SORT_S0_S1_PLAN.md`。

## 6. 阶段 D：Gate 2 与 Checkpoint 6 合并交付

### D1：数据安全基础（原 6A/6B）

- 增加 revision-safe 的源项与复合邻接锚点；
- 普通排序只更新源项；间隔耗尽时局部重平衡最多 64 项；
- 10,000 条下保持 keyset、有界读取和 schema 7；不使用 `OFFSET`、全表物化或全表重写。

### D2：ViewModel 会话（原 6C）

- generation-scoped `Previewing/Committing/Cancelled/Completed`；
- 同页内存预览、一次提交、取消恢复原顺序；
- revision/邻接冲突回载权威页；提交成功后刷新失败不得重放写入。

### D3：真实 WPF 拖动（原 Gate 2 + 6D）

- 只在排序模式中，卡片移动超过系统阈值后开始；
- 源卡 dim、同尺寸拖影、真实二维目标插槽和鼠标捕获一起出现；
- 松手执行一次真实提交，而不是永远取消；
- Esc、失捕、隐藏、设置、外部 DragEnter、列数变化和回收统一认领取消；
- 不进入 OLE，不修改 `WorkspaceFileDropRouter` 或 KI-001 路径。

### D4：跨页与收口（原 6E/6F）

- 页边一次武装切换，不用 hover 延时；
- 保持单页最多 64 个 VM，修正受影响 cursor 并重载安全页；
- 文件、目录、`.lnk` 移动到顶/中/底及跨页后重启顺序保持；
- 100 次成功/取消/跨页后，鼠标捕获、渲染订阅、VM、SQLite 工作和视觉层全部归零。

只有 D1–D4 全部完成后才发布实际排序候选，用户届时能测试完整拖动，而不是单独测试无结果拖影。

## 7. 阶段 E：Gate 4 与后续功能

综合候选覆盖：

- Gate 0 静态卡片；
- Gate 1 Hover/KeyboardFocus；
- Gate 3 真实外部拖入视觉；
- 排序模式及 Gate 2/Checkpoint 6 真实内部拖动；
- 100%/200% DPI、浅/深、260/520/800 DIP、长名称、Missing+Pinned、分页、显隐和跨区。

用户确认后归档 UI/交互基础，再按顺序进入：

1. M3.2 点击取用与仅复制；
2. M3.3 出站 OLE Gate 与拖出；
3. 完整文件切片收口。

## 8. 保持不变的边界

- 不复制 Workbench 代码，只参照其外部视觉与交互结果；
- 不移动、删除或改写用户源文件；
- 不建立第二个外部 Drop router，不在 DragOver 读取外国数据；
- 不用固定延时判断拖动、翻页或动画完成；
- 不在正式产品暴露没有终态的按钮、设置或拖动手势；
- 不重开 KI-001，不把内部鼠标捕获验证外推为外部 OLE 显隐安全；
- 排序模式和 Gate 顺序以用户本次确认及 `decisions.md §31` 为准；后续若改变普通模式/排序模式的手势边界，须重新确认。

## 9. 已确认路线与当前交接

用户已于 2026-09-09 确认本路线。Gate 3 当前实现继续消费既有 `sessionActive/targeted`：命中时显示 2 DIP Accent 边界、0.08 Accent wash 和“松开以存入中转站”，未命中只抑制卡片交互；overlay 不参与命中。状态矩阵、浅色 260 DIP、深色 520 DIP 截图、完整 Release Core 74/74 + Integration 502/502 和零警告构建通过，未修改 Router、仓储、真实数据库或 KI-001。用户要求重启后，默认 Release 已重新构建并由 PID 22220 运行。

用户已确认真实 Explorer/桌面拖入的 Gate 3 视觉与清理测试通过。2026-09-11 又确认并完成 S0+S1；完整隔离 Release 593/593、构建 0 warning / 0 error。下一步实施 S2，不提前暴露内部拖动手势或不可操作的排序入口。
