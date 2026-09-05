# M3 中转站卡片布局时机分析与决策门禁

> **状态（2026-09-06）**：用户已确认采用单一自适应卡片布局，不提供列表/卡片切换。Card 0A 已完成纯布局模型、计算器和门禁，尚未修改 XAML、schema、真实数据库或运行候选。下一步执行 Card 0B 视觉候选择优，再完成 Card Gate 1 并进入 M3.1 Checkpoint 6；Checkpoint 6 的 Core/SQLite 排序算法保持不变，WPF 部分改用二维卡片几何。

## 1. 结论

卡片式中转站应在 **M3.1 Checkpoint 6 的前端排序实现之前** 定型和落地基础壳层，而不应等到排序、点击取用或拖出之后再改。

原因不是视觉偏好，而是卡片布局会共同决定：

- 排序把手、卡片主体、固定按钮和更多菜单的互斥命中区域；
- 排序插槽从纵向一维中点变为二维行列位置后的命中算法；
- 页内滚动、顶部/底部跨页和窄列折行的几何；
- M3.2 单击/Enter 取用与 M3.3 卡片拖出的手势预留；
- 页面卡片、内部排序拖影与未来原生 OLE 拖出预览是否使用同一视觉模型；
- 可见项异步状态、Recycling、键盘焦点和高 DPI 下的容器生命周期。

如果先按当前纵向列表完成 Checkpoint 6 的 WPF 层，之后再切卡片网格，至少需要重写插槽计算、插入标记、拖影尺寸、自动滚动和窄宽验收。相反，现有 revision-safe 复合邻接锚点、稀疏中点和最多 64 项局部重平衡都与布局无关，可以继续作为排序持久化内核。

## 2. Workbench 参照的使用边界

本次只把 `D:\dev\workbench-app` 作为产品视觉与已验收行为参照，不复用其代码或内部状态机制。

可采纳的产品规律：

- 以预览区为视觉主体、两行名称和克制的状态标记，形成紧凑卡片节奏；
- 页面卡片、页内拖影和原生拖出预览应来自同一展示模型，避免三套尺寸和内容漂移；
- 排序必须从独立把手开始，卡片主体留给取用/拖出，固定和更多菜单保持独立命中；
- 持久化必须由一次明确终态拥有，鼠标失捕、计时器或旧回调不能补做第二次提交；
- 过滤后的可见索引不等于全局持久顺序，若以后引入筛选，默认应禁止筛选态重排，除非请求仍能表达数据库中的真实直接邻项。

Workbench 的约 72×94 紧凑卡片只能作为节奏参考，不直接成为 Vok 尺寸常量。Vok 当前还要容纳 Missing、固定、更多菜单、未来独立排序把手和键盘可访问性，具体尺寸应由 Vok 的渲染 Gate 决定。本次尝试以当前 Release 做只读视觉核对时未获得可用顶层 HWND，连续两次到达同一阻点后已停止；因此这里不把“本轮新截图”写成证据。

## 3. 当前基础与受影响面

当前 Stage 是纵向 `ListBox + VirtualizingStackPanel`，单行最低 54 DIP；一个页面最多 64 个 item VM。现有卡片已提供 Missing、固定和更多菜单中的打开、定位、复制原路径、重新关联、移除，但卡片主体按计划仍不执行取用或拖出。

持久化现状：

| 能力 | 当前落点 | 卡片基础是否需要迁移 |
|---|---|---|
| 路径、名称、类型 | `stage_items` schema 7 | 否 |
| 顺序、固定、revision | `stage_items` schema 7 | 否 |
| Missing/可用性 | 可见项运行时检查 | 否，继续不持久化 |
| 单一卡片布局 | 固定产品呈现 | 否 |
| 列表/卡片用户切换 | `app_settings` 新键及 Core 设置扩展 | 不改 Stage schema，但会扩大产品状态矩阵 |
| Shell 图标/缩略图 | 可重建、有界缓存 | 否，不写入 Stage 业务表 |
| 颜色标签、筛选 | 新业务字段与查询契约 | 是，后续独立 schema/功能切片 |
| “新加入/未使用” | 新业务字段与所有取用终态 | 是，应等 M3.2/M3.3 行为完整后定义 |

## 4. 建议的卡片产品契约

第一版确定只提供一种自适应卡片布局，不开放列表/卡片切换：

- 卡高、间距和交互目标按少量命名档位固定；同档内 cell 宽度只在命名上下限内伸缩，只有列数或档位变化时才重组展示行；
- 外层按“行”虚拟化，每行只展示当下列数对应的 2–N 张卡；继续保证一个页面最多 64 个 item VM，不直接改用无虚拟化的 `WrapPanel`；
- 上部为文件/目录/快捷方式预览区，第一版可沿用稳定 glyph；真实 Shell 图标/缩略图在卡片几何稳定后，以可见项异步解析、`id + revision + container` 收口和有界缓存接入；
- 名称最多两行、省略显示，完整名称和路径通过 ToolTip/可访问性信息提供；Missing 与固定应是不同的视觉和语义状态；
- 独立排序把手预留明确区域，但在 Checkpoint 6 接入前不启动排序；固定和更多菜单不与主体手势重叠；
- 卡片主体现在保持无业务动作，但预先锁定为 M3.2 单击/Enter 取用和 M3.3 过阈值拖出区域，避免后续再移动管理控件；
- 焦点框、Hover、Pressed、Dragging、Drop target、Missing、Pinned 的视觉状态应一次定义，实际能力未开放时不能用可点击反馈冒充。

建议不要在这一基础切片同时引入颜色标签、筛选、多选、批量操作、“未使用”状态或持久化布局切换。这些都会增加业务状态或改变排序语义，无法作为纯布局工作验收。

## 5. 分步时机

### Card Gate 0：产品和几何契约（现在）

- 已确认单一卡片布局；本 Gate 不再设计或保留双布局设置入口；
- 用静态/测试数据做至少两个尺寸候选，不预先锁死 Workbench 的 72×94；
- 明确卡片命中图、断点、每行列数、两行名称、状态徽标和键盘顺序；
- 渲染覆盖 100%/200% DPI、浅色/深色、Stage 宽 260/520 DIP、长 Unicode、Missing、固定。

通过条件：尺寸与命中契约冻结，能够证明窄宽下操作按钮不被挤出，且未来排序/取用/拖出各有稳定区域。

### Card Gate 1：基础呈现（Checkpoint 6 UI 之前）

- 把当前纵向行改为按行虚拟化的自适应卡片呈现；
- 复接现有 Missing、固定和更多菜单，卡片主体仍无取用/拖出动作；
- 保持 `StageViewModel.PageSize = 64`、keyset 页面、外部 Drop Router 和 schema 7 不变；
- 覆盖 Recycling、可见项 Availability、键盘/Automation、外部 DragEnter 命中和隐藏/设置清理回归。

通过条件：现有 M3.1 Checkpoint 1–5 能力在卡片布局下全部等价工作，完整 Release 基线只增不减。

### Checkpoint 6A/6B：布局无关的排序持久化

按 `M3_STAGE_M31_PLAN.md §16.3–16.4` 实现复合邻接锚点、单项中点和最多 64 项局部重平衡。这部分不依赖网格列数，也不把 UI 索引持久化。

### Checkpoint 6C–6F：基于卡片网格接入排序

- ViewModel 仍只提交源项和全局直接邻项；
- WPF 插槽改为基于实际卡片容器中心与行列排序的二维插槽，不再采用“纵向列表中点”；
- 插入反馈使用卡片插槽/占位轮廓，拖影来自同一卡片展示模型；
- 页内自动滚动仍只沿垂直方向，跨页只在物理顶部/底部边界一次武装；
- 窄宽改变列数时，活动排序应取消并恢复权威投影，不能在旧几何上继续提交。

### M3.2 / M3.3 前后的后续视觉

- M3.2 才把卡片主体接为单击/Enter 取用，并按真实交付终态消费未固定项；
- M3.3 前建立共享 `StageCardPreview` 展示模型，再接原生 OLE 拖出图像和资源释放 Gate；
- 颜色、筛选、“未使用”和多选在文件切片闭环后作为独立产品切片评估，不能顺带塞进卡片基础工作。

## 6. 风险与停止条件

- 若只能依赖无虚拟化 `WrapPanel` 才能完成网格，停止并先解决行虚拟化/容器回收；
- 若卡片改造需要改变 `WorkspaceFileDropRouter` 所有权或重开 KI-001，停止并拆分边界；
- 若布局选择要持久化，却没有明确是否长期同时维护两套排序、取用、拖出和可访问性行为，先做产品决策，不先加设置键；
- 若二维排序只能通过可见序号推算全局位置，停止并回到复合邻接锚点；
- 若真实 Shell 图标解析进入启动、呼出或 Drop 同步热路径，停止并改为可见项异步有界管线；
- 若尺寸在 DPI/窄宽 Gate 失败，不用隐藏按钮或缩小触控目标掩盖，应重新选择断点与信息层级。

## 7. 已确认的产品选择

2026-09-06 用户确认：**Vok 中转站现在切为单一自适应卡片布局，不提供列表/卡片切换。**

因此基础切片不新增持久化状态，Checkpoint 6 只维护一套二维排序交互，M3.2/M3.3 也只使用一个卡片命中契约。`AppSettings/IAppSettingsStore` 本阶段不修改。

## 8. 可直接实施的组件设计

### 8.1 唯一数据顺序与展示投影

- `StageViewModel.Items` 继续是当前页面的唯一扁平有序集合，最多 64 个 `StageItemViewModel`。分页、移除、固定、重新关联以及未来排序仍只读写这组业务投影。
- 新增 App 层 `StageCardRowsProjection`，只把同一批 item VM 引用按当前列数分组为 `StageCardRowViewModel`；它不复制 `StageItem`、不生成第二套顺序、不持久化任何值。
- 列数来自 Stage 真实内容视口，只由 `StageView` 持有。Core、仓储、`StageViewModel` 和 SQLite 都不能接收屏幕宽度、列数、行号或卡片坐标。
- `StageCardRowsProjection` 订阅一个源集合。`Items.Clear()` 后连续加入页面项时，用一次 Dispatcher 合并重建，避免 64 次中间行树；每次绑定源集合和应用布局都增加 generation，排队回调只有仍匹配当前 source/layout generation 才能发布。切换 DataContext 时先退订旧集合，再清理旧行和可见项请求。
- 普通属性更新（固定、Missing、revision 换代）继续更新同一个 item VM，不重建行；只有源集合增删、移动、重置或列数变化才重新分组。

这保持了清晰的单向关系：

```text
SQLite 复合顺序
  → StageViewModel.Items（唯一扁平业务顺序，≤64）
  → StageCardRowsProjection（运行时行分组）
  → VirtualizingStackPanel 行容器
  → 当前行中的卡片容器
```

### 8.2 纯布局计算器

新增无 WPF 控件依赖的 `StageCardLayoutCalculator` 和不可变 `StageCardLayoutProfile`：

```text
Calculate(viewportWidthDip)
  → Profile(
      Tier,
      ColumnCount,
      CardWidth,
      CardHeight,
      RowUnitHeight,
      HorizontalGap,
      VerticalGap,
      SideInset)
```

- 输入只使用 `StageItemsViewport.ActualWidth`，拒绝 NaN、Infinity 和非正值；不根据显示器像素或数据库条数猜测布局。
- 先定义命名尺寸，不把视觉数字散落在 XAML/code-behind。Gate 0 以两个候选起步：紧凑候选约 92×112 DIP、管理安全候选约 104×124 DIP；Workbench 的约 72×94 只作为密度下界参照。
- 支持范围的 260 DIP Stage 宽度必须至少容纳 2 列；更窄的异常宿主宽度允许安全降为 1 列，不挤压或隐藏按钮。520 DIP 的精确 4/5 列结果由 Gate 0 选中的尺寸固定后写入测试。
- 列数按可用宽度、最小卡宽和间距计算，并由 Gate 0 选定命名最大列数，使一个已实现行中的卡片数量始终有界；每个 cell 可在命名最小/最大宽度内伸缩，卡高和交互目标不随每个像素抖动。
- `SizeChanged` 在同一 Dispatcher 轮次只排队一次布局应用；Profile 档位或 `ColumnCount` 未变化时不重建行，只更新必要的卡宽。禁止用 DispatcherTimer、防抖延时或重复全量 Measure 作为稳定手段。
- 列数变化前记录当前最上方可见 item id 与相对 Y，重建后恢复到其新行；未来存在活动排序时必须先取消排序，再改变布局 generation。

### 8.3 WPF 容器结构

`StageItemsList` 保留为外层 `ListBox`，但 ItemsSource 改为卡片行：

- 外层继续使用 `VirtualizingStackPanel`、Recycling、Pixel scroll、隐藏竖向滚动条和不可聚焦行容器；行高固定为 `RowUnitHeight`。
- 行模板使用不拥有滚动的 `ItemsControl + UniformGrid`。`Columns` 绑定当前 `ColumnCount`，每行只包含该行真实 item；尾行空 cell 只是布局空间，不成为业务项、Automation 元素或未来排序锚点。
- 卡片视觉拆成可复用的 `StageCardContentTemplate` 与页面交互 chrome。内容模板只包含预览、两行名称和状态；页面 chrome 承载排序把手预留、固定和更多菜单。Checkpoint 6 内部拖影及 M3.3 原生拖出预览复用内容模板，避免复制视觉结构。
- Stage 标题 24 DIP、页脚 28 DIP、空态、错误态、进度条和整区 `DropOverlay` 保持原宿主结构，不把布局工作扩散到 1:2:1 主界面。
- 不新建顶层窗口、Popup 拖影或第二个 ScrollViewer；不改变 `MainWindow` 的唯一 `AllowDrop` 与 Router。

### 8.4 可见项 Availability 生命周期

现有绑定继续以 `card container + item id + revision + view model` 四重身份为准，但行虚拟化需要以下改造：

- 行容器触发 `CleanUpVirtualizedItem` 时枚举其中全部 `StageItemCard`，逐张撤销请求；当前只查找第一个命名后代的实现不能保留。
- 卡片 `Unloaded`、`DataContextChanged`、离开真实 ScrollViewer viewport、隐藏、DataContext 更换和 revision 权威换代仍先撤销身份、再取消请求、最后清除运行时 Availability。
- 行重新分组前统一撤销旧容器绑定；新容器只在 Loaded、可见且与 viewport 相交后请求。迟到结果仍必须通过原 `id + revision + container` Gate。
- 不把 Availability 写入行 VM、布局 Profile 或 SQLite；行 VM 只保存 item VM 引用。

## 9. 卡片命中区与阶段能力

| 区域 | Card Gate 1 | Checkpoint 6 | M3.2 | M3.3 |
|---|---|---|---|---|
| 左上独立把手区 | 只保留几何，不呈现假按钮 | 过系统阈值后页内排序 | 不变 | 不触发出站拖动 |
| 卡片主体 | 只展示，不执行动作 | 不启动排序 | 单击/Enter 取用 | 从主体过阈值开始 Copy-only 拖出 |
| 固定按钮 | 沿用 revision-safe 固定/取消固定 | 不触发排序 | 不触发取用 | 不触发拖出 |
| 更多按钮/菜单 | 打开、定位、复制原路径、重新关联、移除 | 不触发排序 | 不触发取用 | 不触发拖出 |
| Missing/固定状态 | 只表达状态并进入 AutomationName | 排序不改变状态 | Missing 阻止不安全取用 | Missing 阻止不安全拖出 |

按钮继续至少保留现有 24×28 DIP 的命中预算，具体视觉尺寸由 Gate 0 冻结。卡片主体在能力开放前不添加 Command、不进入 Tab 顺序，也不显示 Pressed/可点击反馈；当前键盘顺序只覆盖实际可用的固定与菜单按钮，并保持行优先、列优先的阅读顺序。

## 10. 持久化与资源边界

- **零 schema 变化**：Card Gate 0/1 不修改 `stage_items`、schema version、迁移器或真实数据库。
- **零设置变化**：单一布局不写 `app_settings`，不修改 `AppSettings` 或 `IAppSettingsStore`。列数、尺寸和滚动位置都是运行时视图状态。
- **现有业务持久化不变**：路径、名称、类型、`sort_order`、`is_pinned`、`revision` 与时间字段继续由 schema 7 权威保存。
- **Availability 不持久化**：Missing/Unavailable 继续按可见卡片动态检查。
- **图标不进入本 Gate**：第一版沿用稳定类型 glyph。后续 Shell 图标/缩略图只能进入可重建、有界 UI cache，不写 Stage 表，也不允许同步进入启动、呼出或 Drop 热路径。
- **后续字段不夹带**：颜色、筛选、“未使用”和多选没有进入当前业务契约，不借卡片布局提前增加 nullable 列或影子设置。

## 11. 文件级实施顺序

### Card 0A：布局模型与纯计算门禁

计划新增：

- `src/Vok.App/Features/Stage/StageCardLayoutProfile.cs`
- `src/Vok.App/Features/Stage/StageCardLayoutCalculator.cs`
- `src/Vok.IntegrationTests/Stage/StageCardLayoutTests.cs`

先锁定无效输入、断点两侧、260/520 DIP、尾部剩余宽度、卡宽上下限和重复相同 Profile。此步不改 `StageView.xaml`，可以单独构建和回归。

完成记录（2026-09-06）：

- 已新增不可变 `StageCardLayoutProfile`、`StageCardLayoutTier` 和无 WPF 控件依赖的 `StageCardLayoutCalculator`；计算只接收正有限 viewport DIP 宽度，最大列数、8 DIP 横纵间距和 4 DIP 最小侧边距均为命名常量。
- 两个候选仍显式隔离：Compact 为 92–104×112 DIP，ManagementSafe 为 104–124×124 DIP。260 DIP 下两者均为 2 列；520 DIP 下分别为 5/4 列。单参数入口暂以 ManagementSafe 作为 Card 0B 前的候选默认值，不代表视觉 Gate 已冻结；Card 0B 必须移除落选档及临时默认语义。
- 计算器对超窄正宽安全降为单列，对超宽限制为 5 列，并把卡宽达到上限后的余量对称放入 `SideInset`；没有引入 Timer、Dispatcher、ViewModel、设置或持久化依赖。
- 新增纯计算门禁 35/35；完整 Release 为 Core 74/74 + Integration 468/468，共 542/542，构建 0 warning / 0 error。

### Card 0B：卡片视觉候选择优

- 只通过命名尺寸资源切换 92×112 与 104×124 两个候选，使用同一内容结构；不同时改颜色、字体和间距多个变量。
- 生成浅/深、260/520 DIP、长 Unicode、文件/目录/`.lnk`、Missing/固定组合截图。
- 选择一个尺寸档案并记录淘汰原因；检查按钮边界、两行名称、状态辨识、空白密度和 1:2:1 主界面节奏。

通过后才把尺寸写成计算器门禁；不把两个候选或临时开关留在产品中。

### Card 1A：只接运行时行投影

计划新增：

- `src/Vok.App/Features/Stage/StageCardRowViewModel.cs`
- `src/Vok.App/Features/Stage/StageCardRowsProjection.cs`

覆盖 0/1/整行/尾行/64 项，增删、Move、Reset、列数切换、合并重建和旧集合退订。确认所有行引用回扁平 `Items` 后能恢复完全一致的 id 顺序。

### Card 1B：迁移 XAML 容器与视觉

修改 `StageView.xaml` 与 `StageView.xaml.cs`：外层行虚拟化、内层 UniformGrid、共享内容模板、卡片 chrome、视口宽度应用和滚动锚点恢复一次完成。当前 Pin/Menu command、参数、ToolTip、AutomationName 和中文字体链必须原样保留。

### Card 1C：Availability 与外部拖入收口

- 把虚拟化清理从“找到一张卡”改为逐张卡；覆盖整行 Recycling、列数变化、隐藏和 revision 换代的迟到拒绝。
- `SetExternalDragState` 继续先禁止 `StageInteractionSurface` 命中并只显示一个整区 overlay；不改 Router 目标选择、Drop 数据快照或异步分发。

### Card 1D：完整回归与独立候选

- Stage 定向测试、MainWindow 1:2:1/虚拟化测试、Workspace Router 邻接测试；
- 完整 Release Core/Integration、零警告构建和 `git diff --check`；
- 隔离候选完成卡片物理 Gate 后，才开始 Checkpoint 6A/6B。

## 12. 自动与物理验收矩阵

| 层 | 必须证明 |
|---|---|
| Calculator | 非法宽度拒绝；断点前后结果稳定；支持宽度 260 至少 2 列；card/cell/gap 总宽不越界；相同列数不重组行 |
| Row projection | 0/1/64 项与尾行正确；扁平化后 id 顺序完全一致；增删/Move/Reset 合并为一次权威重建；旧 DataContext 不再驱动新视图 |
| WPF virtualization | 外层确为 Recycling + Pixel；长页面只实现与 viewport 相交的行及其卡片；尾行不生成假 item；滚动自由偏移不被吸附 |
| Availability | 每张可见卡只按当前 id/revision 检查；回收一整行会取消其中全部请求；隐藏、换列、换绑和权威 revision 后迟到结果不能落地 |
| 操作 | 每卡只有一个固定与一个更多入口；六个现有菜单项和 enable 规则不变；主体无 Command；按钮不触发外部 Drop 或未来排序 |
| 视觉/字体 | 浅/深、260/520 DIP、长中文/Unicode、Missing、Pinned、文件/目录/`.lnk`；图标字体只在 glyph TextBlock，中文菜单不继承图标字体 |
| 宿主/Router | 1:2:1、24/28 DIP 框架、空/错/忙状态、整区 Drop overlay 和唯一 Router 不变；卡片化不重开 KI-001 |
| 稳定性 | 100 次 260↔520 换列、滚动、翻页、隐藏/显示后，无活动 Availability 请求、容器、Dispatcher 排队或句柄单调增长 |

自动截图只能证明当前测试机的实际 DPI。100%/200% 必须分别在真实缩放环境或独立候选上核验，不能用 `LayoutTransform` 放大冒充系统 DPI。Card Gate 1 的通过基线至少保持当前 Release Core 74/74 + Integration 433/433 共 507/507，只允许新增测试数量。

## 13. 与 Checkpoint 6 的精确交接

Card Gate 1 完成后，Checkpoint 6 不再面对“列表还是卡片”的结构变化：

- 6A/6B 继续只实现源 revision、直接邻项、稀疏中点和最多 64 项局部重平衡；
- 6C 的排序预览只移动扁平 `StageViewModel.Items`，行投影根据相同顺序生成视觉；不得以 row/column 作为持久化身份；
- 6D 从已实现卡片容器取得真实二维矩形。先按 Y 选择真实行带，再按 X 与卡片中心确定插槽；尾行只允许 0..实际 item 数，空 UniformGrid cell 不生成锚点；
- 内部拖影用 `StageCardContentTemplate` 的轻量副本，页面原卡只切换 dragging 状态；插入反馈使用目标 cell 轮廓/占位，不再使用贯穿整行的列表插入线；
- 页内自动滚动仍只沿 Y 轴。只有 ScrollViewer 到达当前页物理顶部/底部才切页，进入目标页后按真实首/尾插槽表达；
- 列数/Profile/视口 generation 在活动排序中变化时，先占有取消终态、恢复原扁平快照、清拖影与插槽，再重建行，绝不在旧几何上提交；
- `StageWorkspaceFileDropTarget.CancelInteractions`、MainWindow Esc/设置/隐藏、外部 DragEnter 和 Dispose 最终接入同一个排序取消入口。

若实际原型证明行投影在 60 Hz 排序预览中产生不可接受的布局 churn，先把投影更新收窄到受影响行并保留同一数据合同；不得改用无界 WrapPanel、自定义全表物化或把坐标写入 ViewModel/SQLite。
