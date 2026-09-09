# M3 中转站卡片重设计 Gate 0：静态样式实施计划

> **状态（2026-09-08）**：视觉 Gate 未通过，已被 `M3_STAGE_CARD_REDESIGN_GATE0_V2_PLAN.md` 取代。本文件保留第一版实现与验证事实，但其中“透明外卡 + Accent 图标块 + 124 DIP 高度”的视觉合同不得继续实施。Hover、内部拖影、外部拖入效果和排序均未进入本 Gate。

## 1. 目标

把当前“整张管理面板卡”改为 Workbench 参考的“图标主导文件单元”，同时保持 Stage 已验证的管理能力、状态语义、行虚拟化和未来手势分区。完成后只宣布静态卡片候选就绪，不能宣布整个重设计或排序完成。

验收结果必须同时满足：

- 默认卡片不持续显示强背景与描边；
- 文件类型图标成为第一视觉层级，名称为第二层级，状态和管理入口退居第三层级；
- 104 DIP 最窄卡片仍完整容纳未来排序槽、固定、更多、两行名称和组合状态；
- Missing/Pinned/Unavailable 不只依赖颜色，浅色与深色使用同一组动态主题资源；
- 既有命令、ContextMenu、Automation、Availability 与虚拟化零回归。

## 2. 证据与尺寸判断

当前 Workbench 浅色截图为 192 DPI；首行蓝色图标块的连续像素范围是 92×92 px，对应约 46×46 DIP。其外部卡片单元约 72×94 DIP，只承载图标和名称，没有 Vok 的固定、菜单、Missing 状态与排序把手。

当前 Vok Stage 卡片为 104–124×124 DIP，但图标块只有 24×24 DIP；整卡默认使用 `LayerFillColorDefaultBrush + ControlStrokeColorDefaultBrush`，右上固定/更多常驻，名称为 11 DIP SemiBold。这解释了截图中“卡面重、图标小、控制项抢视觉”的结果。

本 Gate 因此只替换卡片内部比例与层级：

- 保留 104–124×124 DIP、8 DIP 间距、2/4/6/7/8 列；
- Workbench 的约 46 DIP 图标作为上界参照，Vok 首候选用 42×42 DIP，为控制带和状态留出空间；
- 不通过扩大卡片或减少列数解决视觉问题；若 42 DIP 实测失败，先只回退图标到 40 DIP。

## 3. 精确布局合同

卡片外层高度 124 DIP、Padding 4 DIP，内部恰有 116 DIP。第一候选按固定行分配：

| 行 | 高度 | 内容 |
|---|---:|---|
| 管理控制带 | 28 DIP | 左侧 24 DIP 隐藏排序槽；右侧两个 24×28 DIP 按钮 |
| 图标 | 42 DIP | 42×42 DIP Accent 图标块，20 DIP Fluent glyph |
| 名称 | 30 DIP | 最多两行，10.5–11 DIP、Medium/Normal 候选 |
| 状态 | 16 DIP | 9.5 DIP 紧凑状态，字符省略 |

四行合计 116 DIP，不依赖负 Margin、浮动覆盖或运行时测量猜测。顶部控制带保留原命中图：

```text
104 DIP 最窄卡
┌──────────────────┐
│ [handle] [pin][…]│  handle 本 Gate 仍 Hidden/无命中
│     [ glyph ]    │
│    两行名称       │
│ 固定 · 已失效     │
└──────────────────┘
```

左侧槽不能改放 pin：Checkpoint 6 会从这里接排序把手；若本轮占用，之后会再次重写卡片命中结构。

## 4. 表面与图标

### 4.1 默认表面

- `StageItemCard` 保留 1 DIP `BorderThickness`，但默认 Background 与 BorderBrush 都为 Transparent；
- 透明描边槽确保后续 Hover 切换 Brush 时 ActualWidth/ActualHeight 不变化；
- CornerRadius 先冻结为 10 DIP，与 Workbench Hover 外轮廓和 Vok Launcher 一致；
- 不添加 `DropShadowEffect`、模糊、透明顶层窗口或硬编码浅/深色。

### 4.2 图标块

- Available/尚未得出失败结果：42×42 DIP，`AccentFillColorDefaultBrush`，glyph 使用 `TextOnAccentFillColorPrimaryBrush`；
- Missing：保持相同几何，切为 `ControlFillColorDefaultBrush`，glyph 使用 `SystemFillColorCriticalBrush`；
- Unavailable/Unknown：保持相同几何，使用 `ControlFillColorDefaultBrush` 与 `TextFillColorSecondaryBrush`；
- 文件、目录和 `.lnk` 继续只靠现有 Fluent glyph 区分；不在 Gate 0 引入 Shell 图标或缓存。

这里选择改变 icon tile 而不是整卡染色，避免错误状态压过名称和管理操作，也让浅/深主题保持一致层级。

## 5. 名称与状态

### 5.1 名称

- 保留两行居中、`CharacterEllipsis` 和完整路径 ToolTip；
- 首候选 `FontSize=10.5`、`LineHeight=14.5`、`MaxHeight=29`、`FontWeight=Medium`；
- 不使用更粗的 SemiBold 作为默认，因为当前 200% DPI 截图中粗名称已压过图标与状态；
- 长中文、无空格 Latin、emoji/组合字符必须在两行内稳定截断，不能缩小字体逐项适配。

### 5.2 紧凑状态

当前“已固定”和 Availability 是两个独立 Auto 列，最坏组合可能超出 104 DIP。Gate 0 在 App 层增加只读呈现属性，不改变 Core：

- `CompactAvailabilityText`：`已失效 / 暂不可用 / 状态未知 / 空`；
- `CompactStatusText`：按 `固定` 与 compact availability 用 ` · ` 连接；
- `HasCompactStatus`：决定状态行显隐；
- `AutomationName` 和现有完整 `AvailabilityText` 保持原语义，不因视觉缩写而缩水。

颜色优先级：Missing 为 Critical；否则 Pinned 为 Accent；其余为 Secondary。Pinned+Missing 的文本整体使用 Critical，但固定按钮仍保持 Accent，文字和图标共同表达两个事实。

`Update`、`ApplyAvailability` 与 `ResetAvailability` 必须同步发布 compact 属性通知；这是表现投影，不持久化、不进入 Stage Core 或仓储。

## 6. 管理入口默认态

- `StageReorderSlot` 继续 Hidden、`IsHitTestVisible=false`、`Focusable=false`；本 Gate 不呈现把手图标；
- pin 与更多按钮仍位于顶部右侧，保持 24×28 DIP、原 Command/CommandParameter/ToolTip/AutomationName；
- Pinned pin 使用 Accent；未固定 pin 与更多按钮使用 Secondary；Gate 0 暂不隐藏或改变 Opacity，避免在 Hover 逻辑实现前产生不可见键盘焦点；
- Gate 1 再根据真实键盘门禁把未固定 pin/menu 调整为低强调→Hover/Focus 完整显示；
- 图标字体只能设置在按钮内部 `TextBlock`，Button、ContextMenu、MenuItem 必须继续使用应用字体。

## 7. 文件级实施顺序

### 0A：呈现属性

修改 `StageItemViewModel`，增加 compact 状态只读属性与通知；先补定向测试覆盖：

- 未固定 Available 为空；
- Pinned 为“固定”；
- Missing/Unavailable/Unknown 三种短文案；
- Pinned+状态的组合与分隔符；
- revision 权威更新、Availability 应用和重置后的通知；
- 完整 `AvailabilityText` 与 `AutomationName` 不变。

此步不改 XAML，可单独运行 Stage ViewModel 测试。

### 0B：静态 XAML

只修改 `StageCardContentTemplate` 与 `StageItemCard` 内部结构：

- 外层透明表面/描边槽；
- 28/42/30/16 行；
- 42 DIP 动态状态图标；
- Medium 两行名称；
- 单一 compact 状态文本；
- 保持顶部命中区和全部命令。

不修改布局计算器、行投影、code-behind 生命周期、Drop overlay 或 Router。

### 0C：结构门禁与截图

- 断言 104/124 DIP 卡片内所有可见后代不越界；
- 断言透明默认表面、10 DIP CornerRadius、42 DIP icon 和 24×28 操作按钮；
- 断言 Available/Missing/Unavailable/Pinned/Pinned+Missing 的资源与文字；
- 断言两按钮、五菜单项、中文字体、ToolTip、AutomationName 与命令保持；
- 生成浅/深、260/520/720/800/960 DIP 截图并与 Workbench 默认节奏并排复核。

### 0D：回归与用户 Gate

- Stage 定向测试；
- MainWindow 1:2:1、Router 邻接测试；
- 完整 Release 保持至少 567/567，构建 0 warning / 0 error；
- 用户只确认默认静态样式、信息层级和宽窄节奏；Hover/拖影/外部拖入留到后续 Gate。

## 8. 不变量与停止条件

- 若 42 DIP icon 导致 104 DIP 卡片任一按钮、名称或状态越界，只把 icon 调为 40 DIP 后重跑，不同时扩大卡片；
- 若透明默认卡面让项目边界完全不可辨，先比较极弱 `SubtleFillColorTransparentBrush` 候选，不恢复当前强描边；
- 若 compact 状态必须改变 Core enum、schema 或仓储，停止；呈现缩写只能在 App 层；
- 若 pin/menu 需要覆盖图标或缩小命中区才能放下，停止并重新分配固定行，不使用负 Margin；
- 若暗色主题需要硬编码颜色，停止并改用已有动态 Fluent 资源；
- 若本 Gate 出现 Hover Storyboard、鼠标捕获、drag ghost、Drop overlay 或排序调用，拆回后续 Gate。

## 9. 完成定义

Gate 0 完成必须具备代码、定向/完整自动门禁、浅深宽窄截图和用户静态视觉确认。它只冻结后续效果依赖的卡片骨架；通过后进入 Redesign Gate 1，实现 Hover 与 KeyboardFocusWithin，而不是进入排序。

## 10. 实施与验证结果（2026-09-08）

- Gate 0A：`StageItemViewModel` 已增加 compact availability/status 只读投影及完整通知；完整 `AvailabilityText` 与 `AutomationName` 保持原语义，不持久化、不进入 Core。
- Gate 0B：`StageCardContentTemplate` 已采用 42/30/16 内容行，配合既有 28 DIP 管理带构成 116 DIP 内部预算；图标块为 42×42 DIP、名称为两行 Medium、状态合并为单行，默认外卡表面和描边均透明。
- Gate 0C：WPF 门禁覆盖透明描边槽、10 DIP 圆角、42 DIP icon、20 DIP glyph、名称排版、compact 状态、Pinned+Missing、命令与 Automation 语义，以及全部卡内后代不越界；浅色、深色和 260/520/720/800/960 DIP 截图位于 `.artifacts/m3-stage-card-redesign-gate0-20260908/`。真实渲染未触发 42→40 DIP 停止条件。
- Gate 0D：Stage View/ViewModel 定向测试 34/34；完整 Release 为 Core 74/74 + Integration 497/497，共 571/571；独立 Release 构建 0 warning / 0 error，`git diff --check` 通过。
- 候选：当前代码已发布为 259 文件的独立 win-x64 自包含候选 `m3-stage-card-redesign-gate0-candidate-20260908`；按用户要求停止旧 PID 15852 后，新候选以 PID 20160 运行。实际加载 `Vok.dll` 的 SHA-256 为 `C2BF3A66A976F3970368D2E5D9D7CD7668DB96E68FD16C20D7EA600FADD2F3E4`，且当前只有一个 Vok 进程。
- 尚未完成：用户静态视觉确认。本结果不能冒充 Hover、拖动、外部 Drop 或排序已实现。
