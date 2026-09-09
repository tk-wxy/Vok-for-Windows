# M3 中转站卡片重设计 Gate 0 V2：两段式文件卡静态规格

> **状态（2026-09-09）**：V2 静态视觉 Gate 已由用户确认通过。首轮发现的 Stage 卡面整体高于 Launcher 3 DIP 已按真实行内几何修复，绝对坐标、隔离 Release 576/576 与新截图均通过；本计划取代 `M3_STAGE_CARD_REDESIGN_GATE0_PLAN.md` 的静态视觉合同。Hover、拖动、外部 Drop 新效果、排序、缩略图管线和业务能力仍全部暂停。本轮未停止、启动或替换运行实例。

## 1. 结论

Vok Stage 不再采用启动台式“透明图标单元”，改为 Workbench 截图所示的持续可见两段式文件卡：上方是预览/类型区，下方是名称/类型区。它与 Vok 启动台卡片保持同高、同一纵向行节奏，但宽度独立收窄为 72 DIP。

冻结首候选：

| 项目 | 数值 / 规则 |
|---|---|
| 卡片宽度 | 固定 72 DIP |
| 卡片高度 | 与同一窗口中的 Launcher `CardSurfaceHeight` 完全一致，不另写死 |
| 横向间距 | 6 DIP |
| 纵向行节奏 | 与 Launcher `CardUnitHeight` 完全一致；卡间空隙为 unit-height 与 surface-height 的差，当前合同为 6 DIP |
| 最大列数 | 10 |
| 外圆角 | 9 DIP |
| 外描边 | 0.5 DIP 主题动态描边，始终占槽 |
| 底部标签区 | 固定 32 DIP |
| 上部预览区 | `CardSurfaceHeight - 32 DIP` |
| 图标内框 | 40×40 DIP，9 DIP 圆角，0.5 DIP 描边 |
| 名称 | 10 DIP、Medium、单行省略 |
| 类型/状态 | 9 DIP、Secondary/Tertiary、单行省略 |

这套比例在当前 1600×952 DIP 工作区中成立：1:2:1 中区约 800 DIP，Stage 现有左右 8 DIP 留白后有效宽度约 784 DIP；`10×72 + 9×6 = 774 DIP`，可形成 10 列并保留约 5 DIP 的左右余量。Workbench 的 800 DIP 中区、72 DIP 卡与 6 DIP gap 使用同一视觉节奏，但 Vok 只采用其呈现规格，不复制 React/Tauri 代码、交互控制器或后端。

## 2. 第一版失败结论

第一版把 Workbench 启动台的“透明图标单元”误当成了中转站卡片本身，导致：

- 104–124×124 DIP 仍显得过宽、过高，一行只有 8 张；
- 28 DIP 独立管理带、42 DIP Accent 图标和额外状态行形成纵向堆叠，像管理宫格而不是文件卡；
- 默认透明外表面丢失 Workbench 中转卡持续可见的边界、预览区和标签区；
- 蓝色 Accent 图标块复制了 Launcher 的视觉角色，没有复现参考图中的中性预览底、文件图标/缩略图和下方标签分区。

因此不能在第一版上继续微调图标尺寸或颜色；必须切换卡片结构。第一版 571/571 自动门禁只证明实现没有破坏既有行为，不构成视觉通过。

## 3. 已核验参考事实

只读取 Workbench 的前端样式与截图，未参考或移植其业务、拖放、持久化和后端实现：

- 中转卡宽 72、横纵 gap 6、外圆角 9；
- Workbench 固定中区 800 DIP，可排 10 列；
- 上部缩略图区 62 DIP，下部标签区约 32 DIP，卡面总高约 94 DIP；
- 卡片在浅色为白色表面 + 浅灰预览区 + 极细描边，在暗色为弱亮表面 + 更弱预览区 + 低对比描边；
- 文件图标位于 40×40 的中性内框，图片可以铺满上部预览区；
- 名称与 meta 各占一行，名称更强、meta 更弱；长名称不是两行居中铺开；
- Workbench Launcher 卡约 96 DIP、中转卡约 94 DIP，并以各自 gap 对齐到 100 DIP 行节奏。用户本次要求比参考更严格：Vok Stage 卡面直接等于 Vok Launcher 卡面高度。

## 4. Vok 静态卡片结构

```text
72 × LauncherCardSurfaceHeight
┌────────────────┐
│ pin        more│  24 DIP 透明命中区叠在预览区，不独占一行
│                │
│    ┌──────┐    │
│    │ glyph│    │  40×40 中性图标内框；未来可被缩略图替换
│    └──────┘    │
├────────────────┤
│ 文件名…         │  10 DIP Medium，单行
│ .docx / 文件夹  │  9 DIP，单行；异常时显示状态
└────────────────┘
```

### 4.1 外卡和分区

- `StageItemCard` 不设外 Padding，直接裁切两个分区；
- 整卡使用持续可见的 `LayerFillColorDefaultBrush` 候选表面、`ControlStrokeColorDefaultBrush` 候选描边和 9 DIP 圆角；实施时先用 WPF 真实浅/深渲染确认资源等价，不硬编码 Workbench 的 RGBA；
- 上部预览区使用比标签区更弱一级的动态中性填充，底部以 0.5 DIP divider 分隔；
- 不使用 Accent 作为所有文件的默认底色，不使用阴影、模糊、Scale 或透明外卡；
- 上部保留统一预览宿主。V2 只放当前 File/Directory/`.lnk` Fluent glyph，不接 Shell 图标、图片缩略图、文本预览或缓存；以后替换内容时不能改卡片几何。

### 4.2 图标

- 默认图标内框 40×40 DIP、9 DIP 圆角、0.5 DIP 低对比描边；
- glyph 为 20–22 DIP，使用中性主题前景，不再使用整块 Accent 蓝；
- File、Directory、`.lnk` 继续用现有三个 Fluent glyph 区分；不借用 Workbench 图标数据或分类逻辑；
- Missing/Unavailable 只改变 glyph、badge 或 meta 的语义色，不改变卡片尺寸。

### 4.3 名称与 meta

- 名称从两行居中改为单行左对齐：10 DIP、Medium、`CharacterEllipsis`；完整路径继续保留在 ToolTip 与 `AutomationName`；
- meta 使用 9 DIP、Secondary/Tertiary：普通文件显示含点扩展名，目录显示“文件夹”，Shell link 显示“.lnk”；无扩展名文件显示“文件”；
- Availability 异常时 meta 被短状态替换：`已失效 / 暂不可用 / 状态未知`；不再为状态单独增加第四行；
- Pinned 主要由 pin badge 表达，不再重复占用底部文字行；完整“已固定/未固定 + Availability”仍留在 Automation 语义。

### 4.4 管理入口

- 删除 28 DIP 独立管理控制带和永久排序预留行；排序已暂停，未来手势不能提前占据静态卡片高度；
- pin 与更多仍保留原命令、ToolTip、AutomationName 和不小于 24×24 DIP 的透明命中区，但视觉 glyph 收敛到约 12 DIP，叠在预览区左上/右上；
- Pinned pin 保持 Accent 可见；未固定 pin 与更多在 V2 静态阶段使用 Tertiary 低强调，不加入淡入动画；
- Hover 后把操作迁入标签覆盖层、键盘焦点等价显示等行为不在 V2 实施。V2 只冻结默认态的几何与层级。

## 5. 响应式列数

Stage 宽度计算器改为固定 72 DIP 卡宽、6 DIP gap、最小 4 DIP side inset、最大 10 列。剩余空间只进入左右 side inset，不把卡片拉宽。

按传入的真实 viewport width 预期：

| viewport | 列数 | 卡片占用宽度 | 两侧余量 |
|---:|---:|---:|---:|
| 260 | 3 | 228 | 16 / 侧 |
| 520 | 6 | 462 | 29 / 侧 |
| 720 | 9 | 696 | 12 / 侧 |
| 784 | 10 | 774 | 5 / 侧 |
| 800 | 10 | 774 | 13 / 侧 |
| 960 | 10 | 774 | 93 / 侧 |

实际 WPF 测试必须以 ScrollViewer 的真实 viewport 为准；表格只是纯计算合同。超宽时保持 10 列并居中，不扩宽卡片；超窄不足 72+两侧 inset 时才单列收缩，不能出现横向滚动。

## 6. 与 Launcher 等高的实现边界

“同高”不是复制一个当前看起来接近的常量：

- Launcher 当前按真实 viewport 在 94–116 DIP 的 unit-height 预算中计算 `CardSurfaceHeight = CardUnitHeight - 6`；
- Stage 必须消费同一窗口已经确定的 `CardSurfaceHeight` 与 `CardUnitHeight`，而不是用自己的较矮 viewport 再独立计算一次；Stage 有 28 DIP 分页栏，独立计算会产生不同结果；
- 共享只发生在 App 表现层的只读卡片节奏，不进入 Core、设置、schema 或数据库；
- 实施可由 MainWindow/模块中立的 App 视觉 profile 把同一组值分发给两个 View，不允许 Stage 依赖 Launcher 业务 VM，也不修改 Launcher 卡片样式。

门禁在多个 viewport/work-area 高度下直接断言：每张 Stage card 的 `ActualHeight == Launcher tile ActualHeight`，Stage row unit 等于 Launcher row unit。不能只在一张截图里“看起来差不多”。

## 7. 实施顺序

### V2-0A：几何先行

- 先为 72/6/10 和固定宽度补纯计算测试；
- 建立只读共享 vertical rhythm，使 Stage surface/unit 与 Launcher 完全相等；
- 更新行投影和滚动锚点门禁，但不改卡片内容。

### V2-0B：两段式模板

- 移除独立控制带、透明外表面和 Accent 图标块；
- 建立动态上部预览区、固定 32 DIP 标签区、0.5 DIP divider、40 DIP 中性图标内框；
- pin/menu 改为覆盖命中区，不缩小命令或可访问面积。

### V2-0C：meta 投影

- 在 App VM 增加纯呈现 `CompactMetaText`，普通状态显示扩展名/类型，异常状态显示短 availability；
- 保留现有完整 `AvailabilityText`、compact availability 和 Automation 语义；删除不再被模板使用的组合状态行，但不急于删除仍有测试价值的投影，避免在样式变量同时清理无关代码。

### V2-0D：截图与回归

- 浅/深、260/520/720/800/960 DIP；
- File/Directory/`.lnk`、无扩展名、长中文、长 Latin、Pinned、Missing、Pinned+Missing；
- 同一 MainWindow 截图中直接比较 Launcher/Stage 卡面高度和逐行节奏；
- Stage 定向、MainWindow、Router、完整 Release、0 warning build 与 `git diff --check`；
- 生成独立候选，用户只验收默认静态卡面。

## 8. 明确暂停

- Hover/KeyboardFocus 动画与操作栏；
- 内部 drag source、ghost、插槽和排序；
- 外部 DragActive/Targeted 新视觉；
- Shell 图标、图片缩略图、文本预览与缓存；
- 点击取用、OLE 拖出、多选、筛选、标签；
- Core、SQLite、schema 7、Router、Workspace drag lease、真实数据库与 KI-001。

## 9. 停止条件与完成定义

- 若 72 DIP 在 200% DPI 下无法容纳 40 DIP icon、24 DIP 角落命中区和两行标签，先检查覆盖层和字体测量；不能把卡宽与卡高同时放大；
- 若动态主题资源无法形成“标签表面 > 预览表面”的浅深层级，只在 App 资源中增加语义 brush alias，不硬编码浅/深色；
- 若共享高度要求 Stage 读取 Launcher VM 或业务状态，停止并把节奏提升到模块中立 App UI profile；
- 若实现顺手加入 Hover、缩略图、拖动或排序，拆回后续任务；
- V2 只有在代码、自动门禁、同窗对比截图和用户静态视觉确认全部通过后才完成。通过后仍不自动进入其他效果，等待用户另行指令。

## 10. 2026-09-08 实施与验证记录

- V2-0A：布局计算器已冻结为 72 DIP、6 DIP、最多 10 列；纵向尺寸从横向 profile 中移除，`MainWindow` 直接把同一 `LauncherView.CardSurfaceHeight/CardUnitHeight` 只读绑定给 Stage，避免两套高度真相。
- V2-0B：`StageItemCard` 已改为 9 DIP 圆角、0.5 DIP 描边的持续可见两段式卡面；标签区固定 32 DIP，图标内框 40×40 DIP；pin/menu 为预览区两角 24×24 DIP 覆盖命中区，旧 28 DIP 管理带和排序占位已删除。
- V2-0C：`CompactMetaText` 已覆盖扩展名、无扩展名、文件夹、`.lnk` 与三种异常 availability，完整 ToolTip、Automation 和既有管理命令不变。
- V2-0D：聚焦 Stage/MainWindow 80/80，通过；完整 Release Core 74/74 + Integration 502/502，共 576/576；构建随测试通过且 0 warning / 0 error，`git diff --check` 通过。浅/深、260/520/720/800/960 DIP 与 MainWindow 1600 DIP 十列截图已生成到 `.artifacts/m3-stage-card-redesign-gate0-v2-20260908/`。
- 用户于 2026-09-09 确认静态视觉 Gate 通过。未生成或启动独立候选，未进入 Hover、拖动、外部 Drop 新效果、排序或缩略图。

### 2026-09-09 对齐修正

- 用户首轮视觉检查指出整体未与 Launcher 卡片对齐。新增绝对窗口坐标断言后，修复前稳定复现 Launcher 首卡 `Y=89`、Stage 首卡 `Y=86`，相差 3 DIP。
- 根因不是卡高或 row unit 不同：Launcher 的 94 DIP 卡面位于 100 DIP 行单元中央，上下各 3 DIP；Stage 虽共享相同高度和行高，却把卡面顶对齐在行单元的 `Y=0`，所以每行都整体上移 3 DIP。
- Stage 卡面改为在共享 row unit 内垂直居中，没有复制 3 DIP 补偿常量。修复后绝对 Y 坐标相等；共享 row unit 继续保证后续各行节奏一致。
- 当前运行中的源码 Release PID 21652 占用默认 `Vok.exe`，未擅自停止。改用隔离 Release 输出完成 Core 74/74 + Integration 502/502，共 576/576，0 warning / 0 error；新 1600 DIP 十列截图位于 `.artifacts/m3-stage-card-alignment-20260909/workbench-layout-1600-light.png`。
