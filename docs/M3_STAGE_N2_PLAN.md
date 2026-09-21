# Stage N2：单项/批量出站 OLE Gate 实施计划

> 日期：2026-09-21。状态：**计划已细化，尚未实施**。精确 4K/100% Gate 已按用户决定冷藏，不阻塞 N2。N2 只裁决出站 OLE 技术路线、隐藏源 HWND、宿主所有权、自吞拒绝和资源闭环；不接正式 Stage 选择、消费或真实产品库。权威产品合同仍为 `M3_STAGE_FOCUSED_ROADMAP.md` 与 `decisions.md §32`。

## 1. 阶段目标与完成边界

N2 要回答一个先于产品接线的问题：在 Vok 的 WPF UI STA、普通不透明全屏主 HWND 和现有显隐状态机约束下，能否从 Stage 发起只允许 `Copy` 的文件 OLE 拖出，并在 OLE 已真实开始后隐藏源窗口，仍由 Explorer、桌面和普通 FileDrop 目标稳定接收。

N2 完成时必须交付：

- 一个不读写产品数据库的 `Vok.StageDragProbe`，可构造单项及 1/10/100 项有序 FileDrop 载荷；
- WPF `DragDrop.DoDragDrop` 或原生 `DoDragDrop/IDropSource` 中唯一一条通过真实 Gate 的实现方向；
- `FileDrop + Preferred DropEffect(COPY) + 本进程 source token` 的精确数据合同；
- OLE 开始、隐藏、失活、Topmost 退出、终态和清理的同代事件时间线；
- 出站专用 workspace claim、`PreserveCurrentForeground` 隐藏策略和现有入站 Router 的自吞拒绝门禁；
- Explorer/桌面/受控接收端的物理报告，以及 20 轮成功/取消交替后的资源报告；
- 明确的 N3 接线输入：胜出的 source adapter、终态语义、token 合同和已证实的手势假设。

N2 明确不实现：

- 不把拖出入口接到正式 `StageView`，不向用户宣称 Stage 已可拖出；
- 不实现 Ctrl/Shift selection、窗口淘汰后的选择快照或批量内部排序；
- 不新增 repository consume/remove API，不在 `Copy` 后删除任何 Stage occurrence；
- 不读取、迁移、备份或写入 `%LOCALAPPDATA%\Vok\vok.db`；
- 不启动产品 mutex、全局热键、剪贴板监听、托盘、Availability 或 Shell 图标泵；
- 不实现点击取用、系统剪贴板写入、粘贴注入、文本/图片或虚拟文件流；
- 不用自动测试、受控接收端或 `DoDragDrop` 返回值冒充 Explorer/桌面物理 Gate。

N2 的 `Copy` 只表示整个 OLE 会话被目标接受，不表示每个 occurrence 都有独立回执。N2 不做消费，因此无论 `Copy`、`None`、Esc 或异常，任何真实 Stage 条目都不改变。

## 2. 进入事实与源码诊断

### 2.1 已具备基础

- `StageView` 已有普通卡面按下、系统拖动阈值、鼠标捕获、零集合变化排序预览、拖影、邻位 transform、自动滚动和一次性取消链；
- `WorkspaceController.Hide` 已具备显隐状态机、Hide 失败回滚、Topmost 退出、目标恢复、同代完成和诊断发布；
- `WorkspaceFileDropRouter` 已是主窗口唯一入站 OLE Router，Enter/Over 只检查格式，Drop 终态才读取一次 FileDrop；
- N0/N1 已建立 occurrence、重复路径、有序批量、双向 keyset 和有界 rolling window；N2 只需使用隔离夹具，不需重新证明数据库路线；
- 现有 `Vok.StageScaleProbe` 已证明 solution 可纳入独立 WPF 探针、create-only 输出、固定报告和集成测试引用，N2 应复用这套安全形态而不是复用其业务对象。

### 2.2 当前缺口

| 缺口 | 当前事实 | N2 要证明或建立的边界 |
|---|---|---|
| 出站 OLE source | 源码没有 `StageDragOutController`、`DoDragDrop` 或 `IDropSource` | 先由隔离探针裁决 WPF/原生路线 |
| 出站 workspace claim | 宿主只拥有外部拖入 lease 和 paste claim | DoDragDrop 前认领当前 workspace generation；旧代不得隐藏新会话 |
| 隐藏策略 | Stage 管理动作会恢复呼出前目标 | 出站隐藏必须 `PreserveCurrentForeground`，不能抢回旧窗口 |
| 失活竞态 | `Application.Deactivated` 只为外部拖入 lease 延后 | 出站 claim 存在时不得走普通 ForegroundLost 隐藏 |
| 自吞拒绝 | Router 只检查 FileDrop | 本进程 token 必须在建立入站租约、取消产品交互或显示反馈前拒绝 |
| 手势升级 | 超阈值后立即进入排序；少于两项不能建立候选 | 单卡即使只有一项也可拖出；内部排序只能单向升级为 OLE |
| 批量载荷 | 尚无 selection | N2 用固定夹具列表验证 1/10/100 传输，不提前实现 selection |
| 终态与清理 | 尚无出站 generation | `Completed/Cancelled/Failed` 只能有一个终态，迟到回调只清理自身 |

### 2.3 证据分层

N2 使用两层互补证据，不把其中一层冒充另一层：

1. **物理 WPF/OLE 证据**：独立 probe 真实调用 OLE，固定 PID/HWND，验证隐藏源窗口后 Explorer/桌面是否仍接收；
2. **产品宿主所有权证据**：在现有 `WorkspaceController` 测试夹具中验证真实 workspace generation、失活、隐藏失败、Topmost/restore 顺序和旧代拒绝，但不接产品 Stage UI。

完整产品中“真实 Stage 手势 + 真实 WorkspaceController + 真实 occurrence 消费”的组合证据属于 N3，不在 N2 虚构完成。

## 3. 不变量与安全合同

### 3.1 线程与资源所有权

- `DataObject` 构造、source 事件、`DoDragDrop` 和可能的原生 `IDropSource` 全部运行在发起拖动的 WPF UI STA；
- 不把 OLE source 放到 Shell STA、Clipboard STA、后台线程或第二进程代替产品源端；受控接收端可以是独立进程；
- data object、source token 和载荷快照强引用至少保持到 `DoDragDrop` 返回；
- 不使用系统剪贴板模拟拖出，不复用 `WindowsClipboardWriter` 的剪贴板事务；
- MouseMove 热路径不访问 SQLite、Shell、文件系统，不创建新的 Availability 请求或集合变更。

### 3.2 文件与存储安全

- probe 只拖出其在显式新建输出根下创建的夹具；拒绝 `%LOCALAPPDATA%\Vok`、仓库根、用户主目录和已有非空目录作为夹具根；
- 默认不接受任意用户文件作为 source，避免故障注入误删或改名用户数据；
- 除“源路径中途失效”专用案例外，所有 source 在前后摘要中必须保持路径、类型、长度、最后写入时间和 SHA-256 一致；
- 失效案例只能删除或改名 probe 当轮创建且清单精确列出的夹具，并在报告中标记预期差异；
- Explorer/桌面接收会产生真实副本。probe 记录预期目标名和用户 verdict，不自动删除桌面或外部目标内容；需要清理时另行取得明确授权并按清单执行；
- 所有报告目录 create-only；存在即拒绝，不覆盖旧证据。

### 3.3 Copy-only 合同

- `DataFormats.FileDrop` 保存按 occurrence/夹具顺序排列的绝对路径数组，不按 canonical 或路径字符串去重；
- `Preferred DropEffect` 编码为 32 位 `DROPEFFECT_COPY`；allowed effects 只传 `DragDropEffects.Copy`；
- 原始返回 effect 必须记录。只有明确的 `Copy` 才记为外部接受，`None`、Esc、异常或含义不明确的返回都记为未接受；
- 不声明 `Move`、`Copy | Move`，不因目标显示“移动”提示而猜测成功；
- 重复路径在载荷中保持次数和顺序，但目标可能合并、拒绝或只生成一个副本；报告真实行为，不把目标行为解释成逐 occurrence receipt。

### 3.4 无延时同步

- 第一个同代 `GiveFeedback` 或 `QueryContinueDrag` source 回调是“WPF/OLE 已进入 source 会话”的候选边沿；两者必须汇入同一个原子认领，不得各自隐藏一次；
- 不以 `Task.Delay`、`Thread.Sleep`、DispatcherTimer 或固定帧数判断 OLE 已开始、隐藏完成或目标已接受；
- 外部 watchdog 只负责把卡死的 probe 判为失败并保存已有证据，不能作为产品成功路径同步屏障；
- 若同一阶段连续两次在同一点失败，先核对事件顺序和所有权，再决定是否进入允许的降级分支，不能叠加延时或吞异常。

## 4. 状态机与事件顺序

### 4.1 指针与手势状态

N2 probe 必须模拟最终手势，但不修改正式 `StageView`：

```text
Idle
  → Pressed
      ├─ 未过系统阈值 → ReleasedWithoutDrag
      └─ ExceededThreshold
           ├─ 单卡且指针仍在 Stage → ReorderPreview
           │    ├─ Stage 内松手 → ProbeReorderEnded（无数据库）
           │    └─ 横向越出 Stage → OutboundUpgradeClaimed
           └─ 批量固定载荷 → OutboundUpgradeClaimed

OutboundUpgradeClaimed
  → 清除 reorder transform / ghost / capture / render 订阅
  → 构造同代 data object
  → OleCallEntered
```

约束：

- 纵向越过 Stage 顶/底边仍属于内部排序/自动滚动假设，不触发出站；只有横向越界触发升级；
- 升级一旦认领，同一次按压不能退回排序；
- pin/menu/辅助按钮不建立候选；
- 单项列表不能因 `Items.Count < 2` 被禁止拖出；该限制只属于排序开始条件；
- 批量 probe 载荷不等于产品 selection，N3 才实现 Ctrl/Shift 和轻量 occurrence 快照。

### 4.2 OLE source 状态

```text
Created
  → OleCallEntered
      → OleStartedObserved
          → HideClaimed
              ├─ Hidden → RunningHidden
              └─ HideRejected/RolledBack → CancelRequested

OleCallEntered / RunningHidden / CancelRequested
  → DoDragDropReturned(Copy|None)
  → TerminalClaimed(Completed|Cancelled|Failed)
  → CleanupCompleted
```

核心规则：

- 每个 session 使用单调 generation；所有回调捕获 generation 并在写状态前复核当前所有者；
- `OleStartedObserved`、`HideClaimed`、`TerminalClaimed` 和 `CleanupCompleted` 各最多一次；
- Hide 失败只设置同代取消标志，后续 `QueryContinueDrag` 返回 Cancel；不能把仍可见窗口当作成功隐藏，也不能再开第二次 OLE；
- `DoDragDrop` 抛异常走 Failed；返回 `None` 或 Esc 走 Cancelled；返回明确 Copy 走 Completed；
- 迟到的 GiveFeedback/QueryContinueDrag/Deactivated/Dispatcher 回调只能撤销自己的订阅，不能修改新代；
- 终态统一释放 capture、事件订阅、data object、token、payload snapshot、ghost、transform、render pump 和 outbound claim；
- probe 为下一案例重新显示窗口是测试控制动作，必须发生在本轮 `CleanupCompleted` 后并单独记录；它不代表产品会在取消或成功后自动呼回 Vok。

### 4.3 宿主 claim 与隐藏策略

胜出 OLE 路线确定后，再把最小宿主边界加入产品代码，但仍不接 Stage UI：

- `AcquireOutboundDragClaim` 只在当前 workspace 为 Visible、未退出且没有其他 outbound claim 时返回；claim 固定 workspace generation；
- claim 必须在调用 `DoDragDrop` 前建立，防止 OLE 进入后 `Application.Deactivated` 抢先走 ForegroundLost；
- claim 存在时，Deactivated 只记录 pending，不直接隐藏；第一个 OLE-start 边沿持 claim 请求出站隐藏；
- 增加独立 `WorkspaceHideReason.StageDragOut`，策略为 `PreserveCurrentForeground`；不得复用会恢复呼出前目标的 `StageManagementAction`；
- Hide 成功必须观察 `IsVisible=false`，随后退出 Topmost；不请求恢复旧目标，不激活任何目标；
- Hide 回滚时 claim 保持到 source 取消终态，不让 pending Deactivated 再发起第二次隐藏；
- OLE 终态释放 claim。若 claim 仍属当前可见 generation 且此前有 pending Deactivated，再按明确终态策略收口；不得影响后续新 workspace session；
- 终态时若窗口已经隐藏，丢弃该 claim 的 pending Deactivated；若窗口仍可见且存在 pending Deactivated，释放 claim 后重新核验同一 generation，再走现有 ForegroundLost 隐藏。两种方向都不自动 Show 或恢复捕获目标；
- 应用退出、普通 Hide、新 reveal generation 和 Dispose 都使旧 claim 失效；失效不重放 OLE 或隐藏。

## 5. 自吞 token 与入站 Router 边界

全屏 Vok 中，单卡横向离开 Stage 后仍可能先经过 Launcher/Clipboard 区域；此时 OLE source 和入站 target 都属于同一主 HWND。自吞拒绝必须先于任何入站副作用。

计划采用实例级 token format：

- 应用/探针启动时生成不可复用的实例 nonce，并形成当前进程唯一的 data format 名；
- outbound data object 只需发布该 format；Router 在 Enter 只做 `GetDataPresent(format, autoConvert:false)`，不读取 token 数据；
- token format 只用于本实例自吞识别，不承担安全认证或跨进程授权；
- Router 检测到 token 后返回 `Effects=None`，不得创建新的 inbound drag id、调用 Launcher/Stage target、显示 overlay 或读取 FileDrop；
- 若存在一个已遗留的旧 inbound session，必须将旧 session 单独认领并清理一次，再拒绝 self drag；不能让旧 lease 跨入出站代次；
- self token 检查本身抛出 COM/InvalidOperationException 时按不可信外部数据拒绝，不继续读 FileDrop；
- 外部普通 FileDrop 仍保持 Enter/Over 只探测格式、Drop 最多一次快照的既有合同。

## 6. 计划文件落点

N2 初始实验代码只放在 probe；WPF 路线通过后，才把胜出的最小宿主边界加入产品。预计结构：

```text
tools/
├── Vok.StageDragProbe/
│   ├── Vok.StageDragProbe.csproj
│   ├── Program.cs
│   ├── StageDragProbeOptions.cs
│   ├── StageDragProbeWindow.xaml(.cs)
│   ├── ProbeOutboundDragSession.cs
│   ├── OutboundDataObjectFactory.cs
│   ├── WpfOutboundFileDragSource.cs
│   ├── ProbeFileDropReceiver.cs
│   ├── ProbeFixtureBuilder.cs
│   ├── ProbeSourceTreeSnapshot.cs
│   └── StageDragProbeReport.cs
├── Start-M3StageDragGate.ps1
└── Read-M3StageDragReport.ps1

src/Vok.App/Hosting/                 # 仅在 WPF/原生路线胜出后进入
├── WorkspaceOutboundDragClaim.cs
├── WorkspaceOutboundDragToken.cs
├── IWorkspaceCommands.cs
├── WorkspaceController.cs
└── WorkspaceFileDropRouter.cs

src/Vok.Core/Workspace/
├── WorkspaceHideReason.cs
└── WorkspaceHidePolicy.cs

src/Vok.IntegrationTests/
├── Stage/StageDragProbeTests.cs
└── Hosting/WorkspaceOutboundDragTests.cs
```

约束：

- `Vok.IntegrationTests` 可像现有 scale probe 一样引用 probe project；probe 对测试开放 internal，不向正式 App 暴露测试 API；
- N2 不创建 `StageDragOutController.cs`、selection 类型或 repository consumption 类型；这些属于 N3；
- 原生 fallback 若被启用，P/Invoke/COM 定义集中在 probe 的单一 adapter；路线胜出后才按现有互操作边界迁移，不能同时保留 WPF 与原生生产分支；
- solution 的 Debug/Release 都纳入 probe，完整 Release build/test 不依赖 probe 的人工模式。

## 7. 报告与可审计证据

每次 run 使用新目录，至少生成 `manifest.json`、`events.jsonl`、`report.json` 和 `source-before/source-after.json`。

### 7.1 固定元数据

- report schema version、run id、UTC/本地时间；
- executable 路径、SHA-256、进程 PID、源 HWND；
- OS、运行时、进程架构、WPF/原生分支；
- 显示器 bounds/work area、DPI、窗口 bounds、Topmost；
- fixture profile、项目数、类型计数、顺序摘要、重复项位置；
- receiver 类型：受控 Copy、受控 None、Explorer、Desktop 或其他人工目标；
- 自动检查结果与独立 `UserVerdict=NotCaptured|Pass|Fail`。

### 7.2 事件时间线

事件使用单调时钟并携带 session generation，至少记录：

- `PressStarted`、`ThresholdExceeded`、`ReorderPreviewStarted`、`OutboundUpgradeClaimed`；
- `OleCallEntered`、首次 `GiveFeedback`、首次 `QueryContinueDrag`、`OleStartedObserved`；
- `ApplicationDeactivated`、`HideRequested`、`HiddenObserved`、`TopmostExitObserved`、Hide rollback/failure；
- self DragEnter/DragOver/Drop 是否发生及 Router 结果；
- `DoDragDropReturned` 的 raw effect、异常类型；
- `TerminalClaimed`、`CleanupCompleted`、probe-only re-show；
- capture、事件订阅、render pump、token、data object、claim 的期末计数。

报告不得因为缺少事件而填零冒充成功；缺失保持 `null` 并令对应客观检查失败。路径默认记录为 fixture 根相对路径和摘要，不采集任意用户文件名。

### 7.3 资源采样

每轮前、OLE 开始、隐藏后、终态后和下一轮前记录：

- Process private/working set、managed heap；
- Handle、USER、GDI、线程数；
- source callback、Deactivated、Router Enter/Over/Drop 次数；
- 活动 session、claim、capture、事件订阅和 render pump 计数。

20 轮只要求回到稳定基线且无单调增长；在取得同机数据前不发明硬编码内存/毫秒 SLA。

## 8. 分检查点实施步骤

每个 Checkpoint 只引入一个主要变量；前一 Gate 未通过时不进入后一层。

### N2.0：冻结进入基线与边界

实施：

1. 记录进入提交、当前运行 PID/版本和 `git status`，不停止现有 Vok；
2. 对真实产品库只做既有只读审计，确认 schema/Stage/资产摘要；probe 后续不得初始化产品组合根；
3. 用唯一 ArtifactsPath 重跑进入时完整 Release build/test，固定当前基线；
4. 将本计划加入聚焦路线权威入口，不修改已归档的 M3.MVP1–3 结论；
5. 搜索确认源码没有出站 source、outbound claim、self token 或 consumption 实现，防止并行旧实验混入。

Gate：进入事实、工作树归属和真实数据摘要明确；测试失败先诊断，不为开始 N2 修改无关基线。

### N2.1：probe 骨架、夹具和 create-only 报告

实施：

1. 新增 console/WPF 双用 probe project，并加入 solution Release 配置；
2. 实现严格参数解析和路径边界，输出根必须显式、绝对、新建且位于允许的探针根；
3. `prepare` 创建 `single`、`batch10`、`batch100`、`mixed`、`duplicate` 五种夹具；文件名使用 run id，包含普通文件、目录、`.lnk` 和 Unicode，但不指向用户已有文件；
4. 生成有序 manifest 和 source 摘要；重复 profile 在载荷 manifest 中重复 occurrence，但磁盘只需一个对应 source；
5. 实现独立 `receive` 模式，作为另一个进程的 Copy/None/异常受控目标，记录收到的格式、路径顺序和 effect；
6. 实现报告 create-only、版本核验、部分失败报告和外部 watchdog 的失败保存。

自动门禁：路径逃逸、已有目录覆盖、产品目录/仓库根/用户主目录拒绝，非法 count/profile 零创建，夹具摘要可重复读取，receiver 异常不破坏 source。

Checkpoint 完成条件：尚不调用 OLE；只证明安全夹具、接收端和报告骨架可靠。

### N2.2：Copy-only data object 与自吞格式原型

实施：

1. 按 manifest 原序构造 `FileDrop`，保留重复路径；
2. 写入 32 位 little-endian `Preferred DropEffect=Copy`；
3. 写入实例 nonce 派生的 self token format；
4. source 只声明 allowed `Copy`，不写 Move；
5. 受控 receiver 分别检查格式存在、数组顺序、重复位置和 effect 原始字节；
6. 建立 probe-local Router：self token 检测先于 FileDrop、交互取消、租约和反馈。

自动门禁：1/10/100 顺序、重复项不去重、Copy 字节、token 跨实例不同、self drag 零 FileDrop 读取/零 target 激活、外国 IDataObject 抛异常时安全拒绝。

Checkpoint 完成条件：数据合同和自吞判断成立；仍不隐藏窗口，不修改产品 Router。

### N2.3：可见源窗 WPF OLE 与一次性终态

实施：

1. 在 WPF UI STA、真实鼠标按压中调用 `DragDrop.DoDragDrop`；
2. 建立 probe-local generation 状态机，统一 GiveFeedback/QueryContinueDrag 的首次边沿；
3. 第一阶段保持 source 窗口可见，只验证受控 receiver 的 `Copy/None/Esc` 和异常终态；
4. data object 和 snapshot 保持到 `DoDragDrop` 返回，终态后统一释放；
5. 增加双启动、重复回调、旧代回调、Dispose/receiver 崩溃和外部 watchdog 门禁；
6. 完成 20 轮受控 Copy/None 交替，确认 callback/claim/capture/订阅归零。

物理 Gate：受控 receiver 单项、10 项、100 项均返回预期结果；Esc 和 None 不挂住 UI STA。

Checkpoint 完成条件：只有 OLE source/终态成立，不声称隐藏源窗可行。

### N2.4：隐藏源 HWND 可行性裁决

实施顺序严格保持单变量：

1. 只在 N2.3 胜出的 WPF source 上增加首次 OLE-start 边沿；
2. 首个边沿原子认领 Hide，直接在同一 UI STA 请求 `Window.Hide()`，不加延时；
3. 后验观察 `IsVisible=false` 和 Topmost 退出，再记录 Hidden；
4. Hide 被注入拒绝/回滚时设置 CancelRequested，由 QueryContinueDrag 取消；
5. 分别验证 Hide 前后 Deactivated 的真实顺序、重复回调和终态；
6. 先用受控 receiver，再做 Explorer、桌面；只有前者稳定才触碰外部目标；
7. 对 1/10/100、Copy/None/Esc 各记录 raw effect、source 摘要和资源收口。

通过条件：WPF 源 HWND 隐藏后，Explorer/桌面仍能稳定接收；窗口不靠 opacity、最小化、移出屏幕或透明代理伪装隐藏。

失败裁决：

- 若 WPF 在隐藏后稳定返回 None、丢失 source 回调、目标无法接管或嵌套循环不收口，复跑一次相同夹具确认；
- 同点第二次失败后停止 WPF 路线，保留失败报告，不叠加 sleep、不可见代理 HWND 或长期 Topmost；
- 此时才建立原生 `DoDragDrop/IDropSource` probe，仍在 WPF UI STA、使用同一 payload/session/report/物理矩阵；
- 原生通过后删除或隔离 WPF 失败生产分支；原生也失败则 N2 blocked，提交证据并请求用户重新确认手势/平台方案。

### N2.5：产品宿主 claim 与 self Router 门禁

仅在 N2.4 选定唯一 OLE 方向后实施：

1. 新增 `WorkspaceHideReason.StageDragOut`，映射 `PreserveCurrentForeground`；
2. 增加引用当前 workspace generation 的 `WorkspaceOutboundDragClaim`，Acquire/Hide/Release 均一次认领；
3. `Application.Deactivated` 在 claim 活动时只记 pending，不走普通 ForegroundLost；
4. 同 claim 的 OLE-start 只允许一次 Hide；Hide 回滚返回明确失败并保留 claim 直到取消终态；
5. Copy/None/Esc/异常只收口 claim：已隐藏则丢弃 pending Deactivated；仍可见且存在 pending Deactivated 时复核同代后走 ForegroundLost；不自动重新 Show 或恢复捕获目标；
6. 增加实例级 `WorkspaceOutboundDragToken`，把 token format 注入 product Router；
7. Router 在任何 target 取消、入站 drag id、overlay 或 FileDrop 读取前拒绝 self token；旧 inbound session 如存在则独立终结一次；
8. 不把这些 host API 接到 StageView，不构造真实 Stage data object，不访问 repository。

自动门禁至少覆盖：

- 非 Visible/退出/已有 claim 时拒绝 Acquire；
- claim 固定 generation，旧 claim 不能隐藏新 session；
- Deactivated-before-OleStart、OleStart-before-Deactivated、Hide 期间 Deactivated 三种顺序；
- Hide 成功顺序为 hide → topmost exit，且无 foreground restore；
- Hide 失败回滚、重复 Hide、Copy/None/异常/Dispose、普通 Hide/退出抢占均只一个终态；
- self drag 不创建 inbound lease、不取消 Stage/Launcher 交互、不显示反馈、不读 FileDrop；
- 外部 FileDrop 原有一次 GetData、跨区、DragLeave、新 session 替换全部回归。

Checkpoint 完成条件：宿主所有权成立，但产品仍无可见拖出入口。

### N2.6：手势仲裁 probe

实施：

1. probe 创建与产品 1:2:1 几何一致的 Launcher/Stage/Clipboard 区域及普通卡面；
2. 使用系统水平/垂直拖动阈值；未过阈值只更新焦点，不调用 OLE、不隐藏；
3. 单卡在 Stage 内进入零集合变化排序预览；纵向边缘只模拟内部滚动；
4. 横向越界先原子认领升级，再清除 ghost、transform、capture、render pump，最后进入 OLE；
5. OLE 进入后即使指针返回 Stage 也不能恢复排序；
6. 一项夹具也能横向拖出；两项以上才允许排序；
7. 批量固定载荷从“已选卡”直接进入 OLE，不启动排序；这里只验证手感，不实现正式 selection；
8. 横向进入同一 probe 的其他区域时 self Router 只给 None，窗口隐藏后底层目标接管。

自动门禁：阈值、横/纵向判定、单向升级、清理先于 OLE、单项可拖出、批量不排序、按钮子树排除、失捕/Esc/Hide/Dispose 和旧 generation。

用户 Gate：确认内部排序手感未被明显改变，横向越界意图清晰，没有长按、右键或隐藏拖出按钮。若该假设不成立，停止并让用户选择新手势，不能静默改变合同。

### N2.7：完整物理矩阵与资源收口

按 §9 执行受控接收端、Explorer 和桌面矩阵。编排器固定 source PID/HWND/executable hash；窗口消失后不能按进程名误选新实例。每轮结束等待逻辑所有权立即归零；外部同步调用尚未返回时单独记录物理在途，不用 GC 或延时掩盖。

通过后：

1. 删除未采用的 OLE 实现分支及其生产依赖；失败报告保留在隔离证据目录，不保留双运行时开关；
2. 完整 Release build/test、probe 自动门禁和 20 轮资源矩阵通过；
3. 只读复核真实产品库、真实 source 文件和当前产品 PID 未变；
4. 更新本计划实施结果、聚焦路线、APC 当前状态；
5. 宣布“出站 OLE Gate 通过”，不能宣布“Stage 拖出完成”；
6. N3 只消费已冻结的 source adapter、claim、token 和结果合同，再增加 selection、正式手势接线与消费事务。

## 9. 验收矩阵

### 9.1 自动/受控矩阵

| 类别 | 案例 | 必须结果 |
|---|---|---|
| Payload | 1/10/100、mixed、duplicate | 顺序与重复位置原样保留；只有 Copy allowed |
| Token | 本实例、旧实例、外国对象、格式异常 | 本实例拒绝且零副作用；外国 FileDrop 正常；异常安全拒绝 |
| Threshold | 点击、低于阈值、横向、纵向 | 点击不隐藏；横向可升级；纵向不出站 |
| Terminal | Copy、None、Esc、source exception | 一次终态；无自动 re-show/restore；全部临时所有权归零 |
| Hide | 成功、抛异常、仍可见、重复回调 | 成功后才记 Hidden；失败请求 Cancel；不双 Hide |
| Deactivated | Hide 前、Hide 中、Hide 后 | claim 内不走普通 ForegroundLost；终态无迟到副作用 |
| Generation | 双启动、旧回调、新 workspace | 旧代不能隐藏或结束新代 |
| Router | self Enter/Over/Drop、遗留 inbound lease | self 零 target/overlay/GetData；旧 lease 单独一次收口 |
| Lifetime | Dispose、receiver 退出、watchdog | UI STA 可退出；报告保留失败事实；无孤儿进程 |
| Source | 正常、预期中途失效 | 正常摘要零变化；失效只影响 probe-owned 清单项 |

### 9.2 物理目标矩阵

| 目标 | 载荷 | 操作 | 预期 |
|---|---|---|---|
| 受控 Copy receiver | 1/10/100、mixed、duplicate | 正常放下 | raw Copy；格式/顺序可审计；源窗已隐藏 |
| 受控 None receiver | 1/10/100 | 正常放下 | raw None；一次取消终态；源摘要不变 |
| Explorer 空测试目录 | 1、10、100 | Copy | 文件/目录/`.lnk` 可达；源零修改；返回 Copy |
| Desktop surface | 1、10、100 | Copy | 真实 Shell 目标接收；产物精确记录但不自动删除 |
| Explorer/Desktop | duplicate | Copy | 记录目标合并/拒绝/提示行为，不声称逐项 receipt |
| 不支持 FileDrop 的区域 | 1/10 | 放下 | None；无挂起、无源变化 |
| 任一真实目标 | 1/10 | Esc | None；嵌套循环退出；Vok 不自动抢回前台 |
| 受控异常目标 | 1/10 | Drop handler 失败 | source 安全收口；报告异常；无双终态 |
| 受控 Copy receiver | 1 | OLE-start 后使 probe source 失效 | 不挂起；结果如实记录；只改 probe fixture |
| 任一受控目标 | 1 | 注入 Hide 失败 | Cancel/None；源窗仍可见事实不被伪装 |

Desktop 的 100 项会创建真实测试副本。文件名必须含唯一 run id，报告列出精确清单；执行前由用户确认测试目标可接受这些产物，N2 不自动清理。

### 9.3 20 轮交替矩阵

- 奇数轮受控 Copy，偶数轮 Esc/None；至少穿插 Explorer 与 Desktop 代表轮次；
- 每轮使用新 session generation，但复用同一 source 进程和 HWND；
- 每轮固定记录 Hide/OLE/terminal/cleanup 次数和资源样本；
- 第 20 轮后输入、Dispatcher、窗口 re-show（probe-only）、receiver 和报告写入均仍响应；
- active session/claim/capture/token/data object/subscription/render pump 均为 0；Handle/Thread/USER/GDI 不呈单调增长。

## 10. 停止条件与禁止方案

出现以下任一情况，当前实现方向不得进入 N3：

- WPF/原生 source 在隐藏 HWND 后无法稳定完成 Explorer 或桌面 Copy；
- Hide 发生在 OLE-start 证明之前，或依赖固定延时；
- `Application.Deactivated`、Hide 和 OLE terminal 可产生双隐藏、双终态或旧代副作用；
- self drag 建立入站 lease、显示 Launcher/Stage overlay 或读取自身 FileDrop；
- Copy-only 载荷出现 Move、修改/删除 source，或把不明确 effect 当作 Copy；
- Esc、None、目标异常或 source 失效能卡住 UI STA/嵌套循环；
- 终态后存在捕获、事件、render pump、data object、token、claim 或进程残留；
- probe 访问产品数据库、任意用户 source 或覆盖已有报告；
- 横向升级手势未获用户接受。

禁止以以下方式“通过”：

- sleep、固定 Dispatcher delay、轮询毫秒数或等待若干 Rendering；
- 保持 1×1、透明、移出屏幕或 opacity=0 的源窗口骗过隐藏 Gate；
- 在 Shell/Clipboard STA 或后台线程调用 OLE source；
- 同时保留 WPF 和原生两套生产实现，以配置开关逃避路线裁决；
- 捕获异常后强制返回 Copy，或用目标目录出现某文件倒推所有 occurrence 成功；
- 自动恢复 Vok 抢回前台，掩盖取消/目标拒绝；
- 为覆盖 Desktop 产物而自动删除未精确认领的用户文件。

## 11. 验证命令与提交门禁

计划中的命令面：

```powershell
dotnet restore src/Vok.sln
dotnet build src/Vok.sln -c Release
dotnet test src/Vok.sln -c Release --settings Vok.runsettings

dotnet run --project tools/Vok.StageDragProbe/Vok.StageDragProbe.csproj -- prepare --output-root <new-path> --profile batch100
dotnet run --project tools/Vok.StageDragProbe/Vok.StageDragProbe.csproj -- receive --output-root <new-path> --effect Copy
dotnet run --project tools/Vok.StageDragProbe/Vok.StageDragProbe.csproj -- run --fixture <manifest.json> --output-root <new-path> --source Wpf

pwsh -File tools/Start-M3StageDragGate.ps1 -FixtureManifest <manifest.json> -OutputRoot <new-path>
pwsh -File tools/Read-M3StageDragReport.ps1 -ReportRoot <path>
```

精确参数名可在 N2.1 随实现收紧，但必须保持显式输出根、create-only、分进程 receiver/source、固定 PID/HWND 和无产品存储初始化。

建议提交边界：

1. `test(stage): add outbound drag probe fixtures` — N2.1；
2. `feat(stage): add copy-only OLE drag probe` — N2.2–N2.3；
3. `test(stage): gate hidden WPF drag source` — N2.4 路线裁决；
4. `feat(host): add outbound drag ownership gate` — N2.5；
5. `test(stage): gate outbound gesture arbitration` — N2.6；
6. `docs(stage): record N2 physical gate` — N2.7。

每个提交前至少满足：定向门禁、完整 Release build/test、`git diff --check`、真实库只读摘要前后一致、probe source 摘要符合案例预期。物理 Gate 未捕获时报告必须保持 `UserVerdict=NotCaptured`，不能因自动检查全绿而宣布 N2 完成。

## 12. N2 完成定义与 N3 交接

只有以下条件全部成立，N2 才能记为通过：

- WPF 或原生唯一实现完成 Explorer、Desktop、受控 Copy/None 的单项及 1/10/100 项物理矩阵；
- OLE-start 先于 Hide，Hide 后 source HWND 真实不可见且退出 Topmost；
- Copy-only、重复路径、自吞拒绝、Esc/None/异常/失效、Hide 失败均有报告；
- 出站 claim 在 Deactivated/Hide/terminal/新 generation 竞争中一次收口；
- 20 轮交替后逻辑所有权归零、资源无单调增长、UI STA 仍响应；
- source 文件除专用失效夹具外零变化，产品数据库和现有 Vok 进程零变化；
- 横向越界单向升级手势通过用户物理确认；
- 失败实现分支未进入产品，胜出合同已冻结并写入路线状态。

N3 接手后才实施：正式 selection、`StageDragOutController`、StageView 手势接线、occurrence `id + revision` 快照、Copy 后固定项保留/未固定项原子消费、冲突整批保留、用户状态提示和真实候选回归。N2 的完成结论只能是“出站 OLE 技术路线与宿主边界已通过”，不能写成“中转站拖出已完成”。
