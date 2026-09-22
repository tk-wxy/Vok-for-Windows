# Stage N2：单项/批量出站 OLE Gate 实施计划

> 日期：2026-09-22。状态：**N2.0、N2.1-A/B/C/D、N2.2 与 N2.3-A 自动契约已通过；下一步 N2.3-B**。C/D 与 N2.2 已提交至 `ffea5f8`，N2.3 A–D 总体规划已提交至 `3e009b8`；本次 A 已实现并完成模拟门禁，随本次提交归档，仍无 source 窗口或真实 OLE 调用。精确 4K/100% Gate 已按用户决定冷藏，不阻塞 N2。N2 只裁决出站 OLE 技术路线、隐藏源 HWND、宿主所有权、自吞拒绝和资源闭环；不接正式 Stage 选择、消费或真实产品库。权威产品合同仍为 `M3_STAGE_FOCUSED_ROADMAP.md` 与 `decisions.md §32`。

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

N2 probe 最终须在 N2.6 模拟以下完整手势，但不修改正式 `StageView`；N2.3 只用普通卡面按压与系统阈值验证可见 source，不提前接排序/横向升级：

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
  → 接管在热路径外预先准备的同代 data object
  → OleCallEntered
```

约束：

- 纵向越过 Stage 顶/底边仍属于内部排序/自动滚动假设，不触发出站；只有横向越界触发升级；
- 升级一旦认领，同一次按压不能退回排序；
- pin/menu/辅助按钮不建立候选；
- 单项列表不能因 `Items.Count < 2` 被禁止拖出；该限制只属于排序开始条件；
- 批量 probe 载荷不等于产品 selection，N3 才实现 Ctrl/Shift 和轻量 occurrence 快照。

### 4.2 OLE source 状态

下图包含 N2.4 起的隐藏分支；N2.3 保持窗口可见，使用其检查点内的简化 source 生命周期。

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
│   ├── ProbeFileDragSourceWindow.xaml(.cs)
│   ├── ProbeOutboundDragSession.cs
│   ├── ProbeOutboundDataObject.cs
│   ├── ProbeOutboundDragToken.cs
│   ├── ProbeSourceReport.cs
│   ├── ProbeSourceEventRecorder.cs
│   ├── ProbeSourceProcessHost.cs
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

进入结果（2026-09-21）：

- 进入提交固定为 `a90d66a4ef00c3b63cc24d0e932f10226c0ef88a`；本地 `main` 比 `origin/main` 超前 59，tracked 工作树无修改，只有既存未跟踪 `.codex/`；检查时没有运行中的 Vok，因此没有进程需要保留或停止；
- 真实 `%LOCALAPPDATA%\Vok\vok.db` 只读审计为 schema 8、WAL、Stage 50、Ready 资产 60、`quick_check=ok`、0 外键违规、0 缺失/长度/SHA/越界资产异常。审计和完整测试前后 schema、八表、Stage 与资产摘要均不变，数据库文件 SHA-256 均为 `AC343AE5CF4CBDDFA2746B8CA10A04D66114AC2A9D61D6AF5062C70EC05D2CA9`；旧状态中的 Ready 62 已被本次直接审计取代；
- 使用 SDK 10.0.400 和唯一 `%TEMP%` ArtifactsPath 完成 Release restore/build/test：构建 0 warning / 0 error，Core 114/114、Integration 601/601，共 715/715；测试后没有残留 Vok 进程；
- `src/` 与 `tools/` 搜索确认没有 `StageDragOut`、`WorkspaceOutboundDrag`、outbound claim/token、`DoDragDrop` 或 `IDropSource` 实现；现有 `Preferred DropEffect` 只属于 Clipboard 读写/探针，不构成出站 OLE；
- 聚焦路线已经把本计划作为 N2 权威执行入口，M3.MVP1–3 的归档结论未改。N2.0 Gate 通过，不产生产品代码或部署变化。

### N2.1：probe 骨架、夹具和 create-only 报告

实施：

1. 新增 console/WPF 双用 probe project，并加入 solution Release 配置；
2. 实现严格参数解析和路径边界，输出根必须显式、绝对、新建且位于允许的探针根；
3. `prepare` 创建 `single`、`batch10`、`batch100`、`mixed`、`duplicate` 五种夹具；文件名使用 run id，包含普通文件、目录、`.lnk` 和 Unicode，但不指向用户已有文件；
4. 生成有序 manifest 和 source 摘要；重复 profile 在载荷 manifest 中重复 occurrence，但磁盘只需一个对应 source；
5. 实现独立 `receive` 模式，作为另一个进程的 Copy/None/异常受控目标，记录收到的格式、路径顺序和 effect；
6. 实现报告 create-only、版本核验、部分失败报告和外部 watchdog 的失败保存。

自动门禁：路径逃逸、已有目录覆盖、产品目录/仓库根/用户主目录拒绝，非法/未知参数或 profile 零创建，夹具摘要可重复读取，receiver 异常不破坏 source。

Checkpoint 完成条件：尚不调用 OLE；只证明安全夹具、接收端和报告骨架可靠。

进入 N2.1 的实施收窄：

1. 新建 `Vok.StageDragProbe` 为 `net10.0-windows`/WPF 可执行探针，但本切片不引用 `Vok.App`、`Vok.Core`、`Vok.Infrastructure.Windows` 或 SQLite；这从项目依赖图上阻断产品组合根和真实库初始化。`Vok.IntegrationTests` 只引用探针以测试其 internal 边界；
2. 本切片只实现 `prepare` 与 `receive`。不提前创建 `OutboundDataObjectFactory`、`WpfOutboundFileDragSource`、`ProbeOutboundDragSession`、正式 source 窗口或任何产品 host 类型；这些分别留给 N2.2–N2.5；
3. 所有路径参数必须由调用方提供绝对路径。已存在的 allowed root 之下只能创建一个此前不存在的严格子目录；输出根不得等于或落入产品 `%LOCALAPPDATA%\Vok`、仓库根，亦不得直接使用用户主目录。allowed-root 到输出父目录之间出现 reparse point 时拒绝，避免 lexical descendant 经 junction 逃逸；报告、manifest 和 source 文件均用 `CreateNew`；
4. 五种 profile 固定为：`single` 一项普通文件、`batch10` 十项、`batch100` 一百项、`mixed` 覆盖 ASCII 文件、Unicode 文件、目录和指向 probe-owned 文件的有效 `.lnk`、`duplicate` 以固定非相邻顺序 `[A,B,A,C,A,B]` 表达 6 个 occurrence 但只创建 3 个物理 source。所有名称携带 run id；manifest 只保存根内相对路径、ordinal、kind 和 source id，加载时重新做根边界及摘要校验；
5. source snapshot 对唯一物理项记录相对路径、类型、长度、时间和 SHA-256/目录递归摘要；有序 occurrence 另算摘要，因此重复位置和磁盘对象身份不会混淆。`.lnk` 只允许指向同一 fixture 内对象，创建或复核失败即令 prepare 失败，不生成伪快捷方式；
6. `receive` 作为独立 WPF 进程支持 `Copy`、`None`、`Throw` 三种受控策略，记录进入/悬停/放下次数、公开格式、FileDrop 原序和 raw effect；N2.1 自动门禁直接验证 receiver recorder 与异常报告，不从任何 source 调用 OLE。watchdog 只能写明失败并终止探针，不能作为成功同步屏障；
7. 报告采用启动记录、append-only `events.jsonl` 与一次性最终 `report.json`/`failure.json`，不得覆盖旧证据。参数/profile 在创建输出根之前完成验证；输出创建后的异常必须保留已完成阶段和错误，且 source-before 摘要仍可读取；
8. N2.1 Gate 至少覆盖相对路径、逃逸、junction、已有目标、产品/仓库/用户根、未知/重复参数、非法 profile 零创建；五种 profile 的项目数、类型、Unicode、有效 `.lnk`、重复 ordinal、manifest 篡改拒绝和可重复摘要；receiver 的 Copy/None/Throw 记录、关闭/watchdog 失败留证、source 摘要不变；solution Debug/Release 配置和完整 Release 回归。

首个提交边界保持 `test(stage): add outbound drag probe fixtures`：只增加 probe、solution/test 引用和 N2.1 门禁，不修改 `src/Vok.App`、`src/Vok.Core`、`src/Vok.Infrastructure.Windows`、真实库、运行实例、Stage UI、Router 或 KI-001。

#### N2.1 文件与协议定案

源码诊断：

- `Vok.StageScaleProbe` 已证明 `net10.0-windows` WPF console project、`InternalsVisibleTo`、solution Release 配置、create-only JSON 和 `Vok.IntegrationTests` 引用方式可行；N2.1 只复用这些基础形态，不引用其 Stage/SQLite/App 业务对象；
- 产品 `WorkspaceFileDropRouter` 的可靠边界是 Enter/Over 只探测 `FileDrop`、Drop 才读取一次 `string[]`，并捕获外国 COM 源的 `COMException`/`InvalidOperationException`。受控 receiver 继续保持“Drop 前不读取 FileDrop、Drop 最多读取一次”，额外记录 native/all formats 但不把记录逻辑移入产品；
- 现有 Launcher/Stage 文件服务测试用空 `.lnk`，只证明按扩展名保留快捷方式自身路径，不证明 Shell link 可解析。物理探针必须以 probe-local `IShellLinkW.SetPath` + `IPersistFile.Save` 创建指向 fixture 内文件的真实 `.lnk`，重新加载并核对 target；失败时不退化为空文件或指向用户对象；
- `Directory.CreateDirectory` 在目标已存在时也返回成功，不能单独承担 create-only 证明。N2.1 将输出根限制为已存在 allowed root 的直接新子目录，校验完成后用 probe-local `CreateDirectoryW` 一次创建并把 `ERROR_ALREADY_EXISTS` 视为拒绝；后续每个文件仍用 `FileMode.CreateNew`。

预计本切片文件：

```text
tools/Vok.StageDragProbe/
├── Vok.StageDragProbe.csproj       # Exe + UseWPF；零 ProjectReference/PackageReference
├── Program.cs                      # [STAThread]；parse → reserve root → prepare/receive
├── ProbeCommandRunner.cs           # 命令阶段、单终态与释放后退出码
├── ProbeReceiverProcessHost.cs     # 独立 Application/Dispatcher 生命周期
├── ProbeProcessReport.cs           # startup/exit 身份与 exe/assembly SHA-256
├── GlobalUsings.cs
├── StageDragProbeOptions.cs        # verb/profile/effect 与严格参数矩阵
├── ProbePathPolicy.cs              # protected roots、reparse、原子新目录
├── ProbeFixtureManifest.cs         # versioned manifest/occurrence/source records
├── ProbeFixtureBuilder.cs          # 五种 profile 与 deterministic contents
├── ProbeSourceTreeSnapshot.cs      # 唯一物理 source 摘要
├── ProbeShellLink.cs               # probe-only IShellLinkW/IPersistFile adapter
├── ProbeFileDropReceiver.cs        # 可独立测试的 receiver 状态/一次性终态
├── ProbeFileDropReceiverWindow.xaml(.cs)
├── ProbeReceiverReport.cs          # receiver report/failure 与一次性原子发布
├── StageDragProbeReport.cs         # 通用 create-only JSON 与 append-only event writer
└── Properties/AssemblyInfo.cs      # 仅向 Vok.IntegrationTests 开放 internal

src/Vok.IntegrationTests/Stage/
├── StageDragProbePathTests.cs
├── StageDragProbeFixtureTests.cs
├── StageDragProbeReceiverTests.cs
└── StageDragProbeProcessTests.cs
```

本切片不创建计划树中的 source session、data object factory、正式 source window 或 PowerShell 双进程编排器；`Start-M3StageDragGate.ps1` 等到 N2.2/3 已有 source 与 receiver 可联合运行时再加入，避免先提交无法证明 OLE 的空编排。

CLI 固定为：

```text
Vok.StageDragProbe.exe prepare
  --allowed-root <existing-absolute-directory>
  --output-root <new-direct-child>
  --repository-root <absolute-vok-root>
  --source-revision <commit>
  --profile single|batch10|batch100|mixed|duplicate

Vok.StageDragProbe.exe receive
  --allowed-root <existing-absolute-directory>
  --output-root <new-direct-child>
  --repository-root <absolute-vok-root>
  --source-revision <commit>
  --effect Copy|None|Throw
```

参数规则：所有参数在创建输出根前完成解析；verb 专属参数不能泄漏到另一 verb；相对路径、drive-relative 路径、UNC、重复/未知参数均拒绝。`repository-root` 必须包含当前 `src/Vok.sln`；product root 从 `LocalApplicationData\Vok` 推导。allowed/output 不得等于或位于 repository/product root 内，也不得把 user profile 本身当 allowed/output；专用 `%TEMP%` 子目录允许。路径比较使用 Windows ordinal-ignore-case，逐段检查 existing ancestor；allowed root 或到 output parent 的任何 reparse point 首版直接拒绝，不尝试跟随后继续创建。

fixture/report 协议：

- `manifest.json` 使用 `ManifestVersion=1`，包含 run id、profile、source revision、唯一 source 表和有序 occurrence 表；occurrence 只引用 `sourceId`，ordinal 从 0 连续，路径只保存 fixture-root-relative 形式；
- `source.json` 对唯一物理项记录 kind、相对路径、attributes、长度、last-write UTC 与 SHA-256；目录摘要递归包含排序后的相对路径和子项摘要，并拒绝 fixture 内 reparse point；
- `orderedOccurrenceDigest` 与 `sourceTreeDigest` 分开计算。`duplicate` 的 `[A,B,A,C,A,B]` 必须保留全部六个 ordinal，而 source 表只有 A/B/C 三项；
- file contents、名称和 `.lnk` 描述均带 run id；`.lnk` target 是同一 fixture 的普通文件。Shell link COM 只在 `[STAThread]` prepare 中使用，释放 RCW，不执行 target、不启动 Shell UI；
- `events.jsonl` 以 `CreateNew` 打开并逐事件 flush，至少保留 `ProcessStarted`、`OutputReserved`、`FixtureCreated`/`ReceiverReady` 和终态；最终 `report.json` 或 `failure.json` 只创建一次。进程被外部 watchdog 终止时允许没有最终报告，但已写事件必须足以区分“未启动”和“启动后失去响应”；watchdog 不能写入成功；
- `startup.json`/`exit.json` 为版本 1 的 `ProcessLifecycle` 证据，单次原子发布；startup 包含当轮 run id、PID、STA owner、source revision、实际 executable 路径/SHA-256 和托管 assembly 路径/SHA-256（不能只用 apphost 哈希代表代码身份）。run id 贯穿 fixture、receiver 与进程报告；exit 必须在资源释放之后生成，并单列宿主错误与 `EvidenceError`；
- 退出码固定为 `0`（Prepared/Accepted 且报告与释放无错误）、`2`（Rejected/Cancelled 且无额外错误）、`1`（非法参数、启动/关闭失败、宿主/报告/释放错误）。未 Drop 就关闭窗口是 `WindowClosedBeforeDrop`，其 smoke 预期退出 1；通过 smoke 不代表接收成功。入口不添加诊断/故障注入 CLI，自动 fault 只通过 internal 组合边界验证；
- N2.1 的 `prepare`/`receive` 是基础设施阶段，不冒充 §7 的 OLE run。N2.2/3 的真实 run root 再复制 manifest 身份并同时生成 `source-before.json`/`source-after.json`。

receiver 合同：

- WPF Window 是唯一声明 `AllowDrop` 并处理 Preview 拖放的接收路由；只读内容不参与 hit test，客户区保持稳定的模板背景命中面。Enter 首次认领 receiver generation，捕获 native/all format 名称和 `AllowedEffects`，只用 `GetDataPresent(FileDrop)` 判断候选；Over 不读取 FileDrop；
- `Copy` 只有在 source allowed effects 含 Copy 且 FileDrop 可用时返回 Copy，Drop 恰好读取一次 `string[]` 并按原序记录，不复制、移动、打开或删除任何路径；`None` 始终返回 None 且零 FileDrop 读取；
- `Throw` 在 Drop 的受控读取点制造目标失败，但异常由 receiver 边界捕获，返回 None、发布一次 Failed 终态并保存报告，不能成为未处理 Dispatcher 异常；
- Drop、DragLeave、窗口关闭、取消、受控异常只能有一个终态；迟到/重复事件只记诊断，不覆盖报告或重新读取数据。N2.1 用 fake `IDataObject` 直接验证该状态机和读取次数，不启动 source OLE；真实跨进程行为留给 N2.2/3。
- `reportVersion=1`、`evidenceScope=ReceiverObservation` 只证明 receiver 的读取/反馈；不证明目标文件已复制或 source 的 OLE 返回值。终态先认领，再把 JSON 写入 create-only pending 文件、flush 后 non-overwrite move 发布；事件或报告写入失败返回 None。已完成交付后的日志释放错误单独保留为 `EvidenceError`，不改写旧报告；D 的进程宿主必须把该错误计入退出失败，不能仅看 Accepted。

#### N2.1 实施顺序与 Gate

1. **A：项目/参数/路径** — 加入零产品依赖 project、solution/test 引用、严格 verb 参数、protected-root/reparse/原子目录与 create-only writer。先通过无输出的参数失败、existing/escape/protected/reparse 拒绝和同目标竞争测试；
2. **B：fixture/manifest** — 增加五种 profile、真实 Shell link、相对 manifest、source/occurrence 双摘要和重新加载校验。通过 1/10/100、mixed 类型、duplicate ordinal、Unicode、篡改/缺项/越界拒绝；
3. **C：receiver/report** — 增加 receiver core 与薄 WPF Window，验证 Copy/None/Throw、Drop 前零读取、Drop 一次读取、单终态、窗口关闭与 failure/event 留证；只做本进程 WPF 测试，不调用 `DoDragDrop`；
4. **D：收口** — 用实际 probe executable 分别跑一次 `prepare mixed` 与无 OLE 的 receiver 启停 smoke，核对 executable SHA、报告版本和无产品程序集依赖；再运行唯一 ArtifactsPath 的完整 Release build/test、`git diff --check` 及真实库前后只读摘要。

N2.1-A 实施结果（2026-09-21）：

- 新增 `Vok.StageDragProbe` `net10.0-windows`/WPF Exe；项目自身无 `ProjectReference`/`PackageReference`，只由 IntegrationTests 反向引用以验证 internal 合同，并已加入 solution 的 Debug/Release 配置；
- `prepare`/`receive` 严格命名参数和 verb 专属 profile/effect 已落地。相对/UNC、重复/未知参数在创建输出前拒绝；当前 exe 在 A 阶段只做 parse + path validate 后以未启用状态退出，不冒充 prepare/receiver 已完成；
- 输出策略已覆盖仓库/产品/用户根、direct-child、已存在目标与 existing ancestor reparse；`CreateDirectoryW` 原子竞争保证同一目标恰有一个创建者，JSON 与 append-only event 文件均以 `CreateNew` 创建，文件名拒绝目录穿越和 NTFS alternate stream；
- 定向门禁 5/5；唯一 `%TEMP%` ArtifactsPath 的 Release build 为 0 warning / 0 error，Core 114/114、Integration 606/606，共 720/720。真实库有效前后只读审计保持 schema 8、Stage 50、Ready 资产 60、`quick_check=ok`、0 外键/缺失/长度/SHA/越界异常；八表与资产摘要、数据库 SHA-256 均不变，无残留 Vok；
- 本检查点未创建 fixture、manifest、Shell link、receiver 或任何 OLE 调用，未修改产品三个 project。下一步严格进入 B，A 不因后续功能扩展而放宽路径/create-only 合同。

N2.1-B 实施结果（2026-09-22）：

- `single`、`batch10`、`batch100`、`mixed`、`duplicate` 五种 profile 均由 probe 在 reserved output 内创建；项目数分别为 1/10/100/4/6，duplicate 只创建 A/B/C 三个 source 并保留 `[A,B,A,C,A,B]` 六个 ordinal；
- mixed 固定包含 ASCII 文件、Unicode 文件名、含嵌套内容的目录，以及指向同一 fixture 内已声明文件的真实 `.lnk`。Shell link 只在 STA 上经 `IShellLinkW`/`IPersistFile` 创建并回读；为保持最终路径 create-only，先保存到带 run id 的 probe-owned 临时 link，验证 target 后用 non-overwrite `File.Move` 原子发布；
- `manifest.json` 只保存规范化 direct-child 相对路径、source id/kind/target id 和有序 occurrence；`source.json` 保存实物 attributes、UTC 时间、长度/递归项数与 SHA-256/目录摘要。loader 重新枚举实物、拒绝 root/source reparse、越界、路径/ID/profile 结构错误、missing/untracked/篡改项，并分别重算 source tree 与 ordered occurrence digest；
- 新增 10 个 fixture 测试用例，A+B 定向 15/15。唯一 `%TEMP%` ArtifactsPath 的完整 Release build 0 warning / 0 error，Core 114/114、Integration 616/616，共 730/730；真实库前后保持 schema 8、Stage 50、Ready 60、`quick_check=ok`、0 外键/资产异常，八表、资产摘要与数据库 SHA-256 不变，无 Vok 进程；
- 本检查点没有启用 probe executable 的 prepare/receive 执行，没有 receiver、`DoDragDrop`、source session 或产品项目改动。下一步严格进入 C。

N2.1-C 实施结果（2026-09-22）：

- 新增 STA-owned `ProbeFileDropReceiver`、薄 WPF Window 和版本化 receiver recorder；每个实例拥有独立 run id 与单次 drag generation，不保留外国 `IDataObject`。格式查询前先进入 Inspecting、Drop 读取前先认领 Reading；每次外国调用返回后重新检查状态，关闭/取消期间返回的载荷直接丢弃。
- 首次 Enter 快照 `GetFormats(false/true)` 并探测 FileDrop；100 次 Over 与重复 Enter 不读取载荷。Copy 在 Drop 最多读取一次并冻结原序、重复项和格式数组；None 零载荷读取，Throw 在受控读取边界抛出并转为 Failed/None；格式撤回、非法载荷、COM 异常和不允许 Copy 均有明确结果。
- 窗口只在一个 Preview 路由处理事件并设置 Handled/effect。真实 WPF 测试验证客户区 25 个点命中同一模板背景、事件从该命中面路由到窗口；窗口内部 DragLeave 也结束本轮。关闭被取消时仍可接收，读取期间实际关闭则只释放一次，迟到/重复事件及旧实例均不能重读、重写终态或干扰新实例。
- `events.jsonl` 逐事件 flush，最终 report/failure 经 create-only pending 文件原子发布；报告含 PID/HWND、owner thread、source revision、generation、公开格式、路径顺序、计数、raw effect 和错误。实测覆盖启动/事件/最终发布失败、旧报告碰撞保留、关闭/Esc/watchdog endpoint 失败留证、重复 Dispose 与释放异常；source fixture 前后双摘要不变。
- C 定向 50/50。唯一 `%TEMP%` ArtifactsPath 的完整 Release build 0 warning / 0 error，Core 114/114、Integration 666/666，共 780/780、0 skip。真实库前后保持 schema 8、Stage 50、Ready 60、`quick_check=ok`、0 外键/资产异常，schema、八表、资产摘要与数据库 SHA-256 均不变，无残留 Vok/probe 进程。
- 本检查点只验证 receiver core 和本进程 WPF 事件；`Program` 仍只 parse/validate 后以未启用状态退出。外部 watchdog 编排/卡死进程终止、实际 executable 启停、跨进程 OLE、Explorer/桌面物理接收均未验证；未修改产品项目、源端或调用 `DoDragDrop`。下一步 D 接入 prepare/receive 进程宿主、失败退出与 executable smoke，再做 N2.1 收口。

N2.1-D 实施结果（2026-09-22）：

- `Program` 已接通 parse/validate → 捕获代码身份 → 原子 reserve → prepare/receive。`ProbeCommandRunner` 统一 startup、阶段错误、单次最终报告与释放后 exit；prepare 真实生成并复核 mixed fixture，receiver 使用独立 WPF Application，终态后排入一次关闭操作，手动关闭会取消旧操作，Dispatcher 异常按失败退出。
- command 与 receiver 共享一次性 terminal publisher，因此异常兜底不能重试发布或覆盖已接受的旧报告。交付后宿主/释放错误保留原 receiver observation，并由 `exit.json`/退出码 1 表达失败；启动/夹具部分失败保留当时阶段、已完成 source/manifest 与错误。补充反例证明非法 `.` 文件名曾先生成 pending 再拒绝；现先校验最终文件名，`.`/`..`/空白/越界/alternate stream 均零创建。
- 实际 apphost 在只含 exe、dll、deps、runtimeconfig 的隔离目录中通过 mixed prepare smoke（退出 0、4 source/4 occurrence、manifest 双摘要回读一致）及 Copy/None/Throw 三种无 OLE 启停 smoke。接收端等待 `ReceiverShown` 事件，核验可见 HWND 属于固定 PID 后发送 WM_CLOSE；三种策略均以 `WindowClosedBeforeDrop`、零 Drop/读取、单 failure 报告、退出 1 结束。exe/DLL 哈希与子进程 startup 自报身份一致，deps/assembly references 均无产品程序集或 SQLite。
- 外部测试 supervisor 以固定 Process handle、文件事件和真实退出信号编排，不按进程名查杀。30 秒仅是测试工程侧失败上限，100 ms 仅用于人为无 Drop 的 deadline 注入，不是产品规则或成功同步屏障；超时先记录 `ExternalWatchdog/Failed`，再终止并等待该进程退出。强制退出时允许没有 report/exit，已 flush 的事件与完整二进制证据目录保留。首轮测试的递归清理曾遇到 exe 删除拒绝且删掉部分证据，现超时分支不做证据删除或延时重试；退出断言仍保持。该测试不冒充真实阻塞 COM/OLE 或物理拖放 Gate。
- D 新增 20 个测试用例，N2.1 定向 85/85；Debug/Release solution 构建均 0 warning / 0 error。最终完整 Release 为 Core 114/114、Integration 686/686，共 800/800、0 skip；真实库前后 schema 8、Stage 50、Ready 60、`quick_check=ok`、0 外键/资产异常，schema、八表、资产摘要与数据库 SHA-256 均不变，无 Vok/probe 残留进程。
- 最终构建与前后审计/TRX 位于 `%TEMP%\vok-n2-1d-artifacts-1790058629999`（`gates/db-before.json`、`db-after.json`、`tests-final/`）。Release exe SHA-256 为 `157F2A968978B67AB1AFC437F8FE011F4A6D856A89107D672225BB78692B61A7`，DLL 为 `7EBDE7A5080D2959B7F5DF1CC664ED2E4C4989817B500717195CBCEFE3DAB29A`；最后一份强制退出证据为 `%TEMP%\vok-drag-process-tests-415ebc79468e4136b7704c16427174cc`。C/D 尚未提交，未修改产品三个项目或调用 `DoDragDrop`；下一步提交剩余 N2.1 后进入 N2.2，真实 source/跨进程 OLE/Explorer/桌面 Gate 仍待后续。

N2.1 停止条件：任何路径失败能在 protected/已有目录下创建内容；manifest 可把 source 指到 fixture 外；有效 `.lnk` 只能靠用户现有文件或 Shell UI 创建；receiver 在 Enter/Over 提前读取 FileDrop；异常/关闭产生两个终态；probe 引入产品 ProjectReference/SQLite；或自动测试必须真实调用 OLE 才能通过。命中任一项先收窄机制，不能用空 `.lnk`、删除输出、吞异常、固定等待或把 N2.2 证据提前写成 N2.1 通过。

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

执行记录（2026-09-22）：

- `ProbeOutboundDataObject.Create` 仅接受经 loader 重新核验的 probe manifest/fixture，按 occurrence 原序复制路径，不去重；保留 run id 与 source/occurrence 双摘要，准备中的磁盘/Shell 校验不进入未来 MouseMove 热路径。owner 固定在创建 STA，allowed effect 恒为 Copy；显式拥有两个只读 MemoryStream，重复释放幂等，跨 STA 访问/释放拒绝。未来 N2.3 必须持有该 owner 到 OLE 返回，本步尚未创建 source 会话。
- 实际 WPF DataObject 仅发布 FileDrop、Preferred DropEffect 和实例 token 三种原生格式；Preferred 使用四字节 little-endian `01 00 00 00` 流，token 使用单字节 marker。五种 profile 均通过真实 `ComTypes.IDataObject.GetData(TYMED_HGLOBAL)` 回读：Shell `DragQueryFileW` 得到 1/10/100/mixed/duplicate 原序数组，重复读取两轮仍得到精确 Copy/marker 字节，所有 STGMEDIUM 均释放。验证不写系统剪贴板，也不调用 `DoDragDrop`。
- `ProbeOutboundDragToken.Current` 每进程实例复用同一 nonce format，独立 token owner 不同；它只用于自识别，不是鉴权。probe-local Router 在任何新 generation/target activation 之前只查询 `GetDataPresent(本实例 format, false)`，绝不读取 token 数据；自吞/外国 COM 或 InvalidOperation 异常安全拒绝，旧 inbound 会话单独认领并释放一次。Over 无数据对象入口；Drop 重查 token，旧 generation 不影响新会话，取消仍能终止已认领但正在外国数据读取中的 receiver。
- receiver 的版本 1 observation 新增可选 `preferredDropEffectBytes` 与 `preferredDropEffectReadCount`，只在 Copy Drop 中最多读取一次，再读取 FileDrop；Preferred 格式异常、畸形数据、读取中取消均不继续读取 FileDrop。原始 mask 与实际返回的 Copy 分开记录，不把 COPY|LINK 或其他原始值归一成 Move；None/Throw 策略保持零载荷读取。
- 生命周期审查的新增反例复现：普通 Leave/Cancel 虽已摘除旧 generation，但未覆盖清理期的准入保护，使 recorder 回调可提前创建新 session（修复前 leave/cancel 两例失败、dispose 通过）。现 Cancel 也在整个清理期间持有准入保护，并保留外层重入状态；修复后正常终态、旧会话替换、外国调用内取消、Dispose、迟到/重复终态和清理失败均只释放一次，不引入延时或新 watchdog。
- 新增 54 个自动用例，累计探针定向 139/139；Debug/Release solution 构建均零警告。此处仅为本机原生数据格式与 probe-local Router 合同证据，未执行跨进程 OLE、隐藏 HWND、Explorer/桌面或产品 Router Gate；prepare/receive CLI 不新增 source verb，产品三个项目保持不变。
- 最终完整 Release 为 Core 114/114、Integration 740/740，共 854/854、0 skip；真实库前后 schema 8、Stage 50、Ready 60、`quick_check=ok`、0 外键/资产异常，完整 Database（含 schema、八表、索引和 Assets）审计对象与数据库 SHA-256 均不变，无 Vok/probe 残留进程。构建、前后审计和 TRX 位于 `%TEMP%\vok-n2-2-artifacts-1790070014718`（`gates/`、`tests-targeted-final/`、`tests-final/`）；修复前反例位于同目录 `tests-cancel-regression-before/`。
- 本次 Release exe SHA-256 为 `157F2A968978B67AB1AFC437F8FE011F4A6D856A89107D672225BB78692B61A7`，DLL 为 `D9C67B675DE34F707F83EA7E4E870E9745B833CD7ED57FC9E3840B078F9DD6E7`；完整回归的强制退出证据保留于 `%TEMP%\vok-drag-process-tests-0715f73d783840d780bde80d74979c64`。C/D 与 N2.2 随后按用户要求集中提交，提交轮仅更新归档状态并沿用上述验证，不推送或部署；下一步 N2.3 可见源窗真实 WPF OLE 与一次性终态，不提前隐藏 HWND 或接正式 Stage。

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

#### N2.3 进入时源码诊断与边界（2026-09-22）

以下记录总体规划时的进入状态，不是当前完成记录；随后完成的 A 另见下方实施记录。当时提交为 `ffea5f8`，tracked 工作树干净，只有既存未跟踪 `.codex/`；没有运行中的 Vok/probe。本机安装的最高 WindowsDesktop runtime 为 10.0.11。规划时沿用 N2.2 的 139/139 定向、854/854 完整 Release 证据，未重跑测试或打开拖放窗口。

- 已有 `ProbeOutboundDataObject` 可在 owner STA 预先准备和重验夹具；`ProbeOutboundDragToken.Current` 按进程实例复用；`ProbeFileDropRouter` 可验证自吞。但尚无 source 窗口、OLE invoker、source generation、`source` CLI 或双进程物理编排器。
- `ProbeFileDropReceiver` 与 `ProbeTerminalReportWriter` 都是一次会话/一次发布；`ProbeReceiverProcessHost` 使用 `OnMainWindowClose`，默认 terminal 后排入关闭。它们不能直接充当同一源进程连续 20 轮的 source host。现有 Background 关闭表达的是事件驱动的稍后关窗，不能当作另一进程的 OLE 调用已返回。
- `None` 策略在 Enter/Over 就给 None。真实 OLE 下不应预设一定收到 Drop，更不能拿单元测试直接调用 `Drop()` 得到的 `PolicyNone` 当物理流程。`Throw` 策略则在 Drop 内捕获故障并返回 None；目标报告的异常不等于源端 `DoDragDrop` 一定抛异常，二者要分开验收。
- `ProbeEventLog.Write` 目前每次同步 `Flush(true)`，只有 UTC/sequence，没有单调时钟。不能从每次 MouseMove/GiveFeedback/QueryContinueDrag 直接照搬该写法；否则源端热路径会做同步磁盘 I/O，观测器也会改变交互。
- `ProbeOutboundDataObject.Create` 会读取磁盘、摘要和 Shell link，因此必须在允许按压之前准备，不能从阈值越界的 MouseMove 才调用。物理 source-before/after、同 run 多轮归档和对端身份关联也尚未接通。

官方语义核对：`DoDragDrop` 返回的是最终 effect；`QueryContinueDrag` 默认处理 Esc 和鼠标释放，处理器若覆盖默认行为却不给结束路径会阻止返回；`GiveFeedback` 是高频反馈，不代表目标已接受。因此“进入回调”“取消意图”“实际返回”必须是不同事件。[WPF DoDragDrop](https://learn.microsoft.com/en-us/dotnet/api/system.windows.dragdrop.dodragdrop?view=windowsdesktop-10.0)、[QueryContinueDrag](https://learn.microsoft.com/en-us/dotnet/api/system.windows.dragdrop.querycontinuedrag?view=windowsdesktop-10.0)、[GiveFeedback](https://learn.microsoft.com/en-us/dotnet/api/system.windows.dragdrop.givefeedback?view=windowsdesktop-10.0)。已同时核对 [WPF v10.0.11 源码](https://github.com/dotnet/wpf/blob/v10.0.11/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/DragDrop.cs) 与 [原生 OLE 循环说明](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-dodragdrop)；文档/源码不能替代本机物理 Gate。

本步只使用普通、不透明、可见、非 Topmost 的独立源窗和受控目标；先以普通卡面左键按压 + 系统拖动阈值开始。1:2:1 区域、内部排序、横向升级、ghost/render pump 留给 N2.6；隐藏 HWND 留给 N2.4，产品 claim/Router 留给 N2.5。N2.3 不启动 Explorer/桌面接收、不引入原生 fallback，不修改三个产品项目或访问真实库。完整测试前后仍使用既有外部只读审计。

#### N2.3 会话、关闭与证据合同

1. **一个 source manager、多代单次 session**：固定 source PID/HWND 和实例 token；每轮新 generation、payload owner 与 immutable snapshot。准备、在途、清理和归档期间都不允许第二次 source 调用；只有上一轮清理及报告完成后才准备下一轮。
2. **准备与启动分离**：`source` 在自己的 create-only 输出内调用现有 builder/loader 创建五种 profile，不新增任意用户 source 路径入口。启动和每轮重新准备在鼠标热路径之外完成，source-ready 之后的阈值处理只认领已有快照。阈值认领先于释放手势 capture，以免本次主动释放触发 LostMouseCapture 反过来取消升级；真正的意外失捕仍取消未进入 OLE 的候选。
3. **调用栈拥有资源**：实际 `DoDragDrop` 只在当前 Window 所属 UI STA 的真实 MouseMove 中同步调用；不跨线程、不延后到鼠标已松开的 Dispatcher 回调发起。所有 per-round 回调捕获自己的 generation。首次 GiveFeedback/QueryContinueDrag 合并为一个 `OleStartedObserved`，默认光标和正常松手语义保留；取消时明确设置 `Action=Cancel` 与 `Handled=true`，不以 PreviewKeyDown 单独承担活动 OLE 的 Esc。
4. **请求取消不等于完成**：Close/Dispose/宿主故障在 OLE 在途时只关闭新准入、记录取消/退出请求，保持 HWND、payload、marker 和 source 回调，等待 QueryContinueDrag 表达取消以及调用栈真正返回/抛异常。此期间不能直接 `Application.Shutdown`、释放 DataObject 或宣布 CleanupCompleted；外部调用不返回时由独立 supervisor 失败收口，不让 UI STA 上的 timer 冒充可抢占取消。
5. **终态先认领，释放后证明**：返回/抛异常后先认领一次 terminal，再在 owner STA 的 finally 中解除本代事件、释放本代 capture/payload/marker，分别记录每个清理步骤。错误不跳过余下步骤，也不先把诊断计数清零再假装已释放；清理或写报告失败禁止重新启动。关闭意图只在归档完成后执行，旧代延迟关闭不能结束下一轮。
6. **外部结果与内部健康分离**：原始 effect、source exception、CancelRequested/reason、receiver observation、cleanup/evidence error 独立保存。只有精确 Copy 视为接受；None 不猜测具体原因，没有观察到 Esc 就不填写 Esc。晚到的关闭/取消不能把已返回 Copy 改写成“未交付”；Copy 后清理失败也保留接受事实，同时令过程/Gate 失败。其他 effect 不归一猜测、不自动重试外部动作。
7. **回调不直接刷盘**：新增 source 专用有界记录器，UI 只记录不可变的关键边沿和累计计数，由一个显式拥有的 writer 串行写 append-only 事件。记录 producer 的单调 ticks/frequency、UTC、generation 与序号；连续反馈合并计数，不无界保存逐帧事件。队列溢出、writer 失败或证据缺口必须可见并令 Gate 失败，不能静默丢失关键边沿。返回后的归档阶段等待 writer 排空，不能阻塞活动 OLE 等待磁盘。
8. **逻辑零点与常驻基线分开**：在 CleanupCompleted、尚未准备下一轮时，检查本代 data object、marker stream、capture、callback 订阅和活动 session 全部为零；Ready 阶段预载的一份下一轮 payload 单独计数。进程级 nonce format 和窗口级静态路由是常驻基线，不要求每轮“注销 format”或重新生成 token。无产品 claim/render pump 的本阶段应标记 NotApplicable，不伪造实现或把累计 callback 次数写成 0。

最小状态顺序：

```text
Preparing → Ready → Pressed → OleCallEntered [OleInFlight=1]
                                  ├─ 首个 source 回调 → OleStartedObserved
                                  └─ Close/Esc/Dispose/fault → CancelRequested（仍在途）
             实际返回 / 抛异常 → TerminalClaimed → 逐项释放 → CleanupCompleted
                                                        → 报告发布 → 下一轮准备 / 退出
```

进入调用前取消可直接释放并记录 `OleInvoked=false`；未发生的 raw effect、回调和资源观察保持 null/未执行，不能填 0 冒充物理通过。

#### N2.3 实施切片与 Gate

| 切片 | 本步交付 | 通过条件与停止位置 |
|---|---|---|
| **A：source 会话/报告契约（自动已通过）** | `ProbeOutboundDragSession`、窄 invoker seam、source report 与有界 recorder；只有测试 fake invoker | 67 个 source 用例覆盖 Copy/None/抛异常、双启动、首回调双触发、预取消/在途取消、旧代、关闭/释放重入、日志/清理失败；证明 fake 返回前数据未释放。尚无真实 OLE，详见 A 实施记录 |
| **B：可见源窗与 WPF adapter** | `WpfOutboundFileDragSource`、`ProbeFileDragSourceWindow`、独立 source host、严格 `source` CLI；复用现有 payload/Router | WPF 自动测试覆盖真实路由、阈值、capture 转交、仅一次 invoker、同 HWND 多代、关闭推迟；apphost 无按压 smoke 为零 OLE。真实 OLE 不进默认 test suite，首次受控实测在 C |
| **C：双进程编排与首轮物理 Gate** | `Start-M3StageDragGate.ps1`、`Read-M3StageDragReport.ps1`，固定 source/receiver 身份、case 绑定、受控 receiver 保活和外部 watchdog | 同一次构建的独立进程完成 single 的 Copy/None/Esc；对比源返回、目标报告和前后 source 摘要。首轮不通先定位这一变量，不扩到批量或隐藏 |
| **D：故障、批量与连续 20 轮** | 故障门禁、1/10/100/mixed/duplicate 物理记录，同源进程/同 HWND 的 Copy/None 交替 | 每轮独立报告，20 轮不重启 source；None/Esc/关闭/目标退出均能如实收口，资源零点可测。完整 Release 回归和真实库前后只读审计后，才能宣布 N2.3 通过 |

A 已按最小边界独立实现，没有同时加入 B/C 或真实 `DoDragDrop`。A–D 可分步提交；所有通过结果必须区分 simulation、实际 HWND 启停和物理 OLE。

#### N2.3-A 具体实施合同（规划于 2026-09-22；结果见下方实施记录）

规划进入提交为 `3e009b8`，当时 tracked 工作树干净，既存 `.codex/` 不在本任务内。以下为进入实施前的合同；规划轮未构建、运行测试、启动窗口或访问真实产品库。随后按用户要求实施 A，结果另列，不把原计划当作验证证据。

##### A.1 源码诊断与改动边界

- `ProbeOutboundDataObject.Create` 已强制 owner STA，并经 loader 重验 fixture；继续作为唯一真实 payload 创建路径，不增加任意路径数组入口。它的 `IsDisposed` 只判断 `_data is null`，而 `Dispose()` 先清掉 `_data`、再释放两个流；因此不能单凭此布尔值证明所有资源都释放了。现有测试已检查两个流的 `CanRead`，A 应把这种逐项观察接入会话诊断，而不是推翻已通过的 Copy-only 格式实现。
- `ProbeFileDropReceiver.Finish` 会在报告失败时把自己的 effect 改成 None；这是接收方尚在决定返回值时的策略，不能照搬来覆盖 source 已观察到的 raw Copy。`ProbeFileDropRouter.Cancel/Dispose` 的立即清理也不能照搬到持有同步 invoker 调用栈的 source。
- `ProbeTerminalReportWriter` 在写入前置 `Attempted=true`，失败也只有一次尝试；可复用这一发布底座，但每代必须新建。`ProbeReceiverReportRecorder` 同步写入和释放后另报错误的时序不适合 source 的“清理、事件排空后再冻结报告”。
- `ProbeEventLog.Write` 会在调用线程做 JSON 序列化、文件写入及 `Flush(true)`；A 新增独立 source recorder，不全局改造已验证的 receiver 日志。
- `WpfApplicationFixture.InvokeAsync` 已提供真实 owner STA 与异步测试入口；用它在 invoker 返回后等待归档，不在该 STA 上 `.Wait()` / `.Result`。现有 outbound fixture、COM 回读和路径保护测试可复用；当前 `Program` / options / runner 仍只有 prepare、receive，不为 A 增加不可用的 source 命令。

预期文件落点（名称可随最小实现调整，职责与边界不变）：

| 文件 / 类型 | A 内职责 |
|---|---|
| `ProbeOutboundDragSession.cs` | 长寿命 manager、单次 session、实例绑定 ticket、取消/停止意图、终态与归档准入；可同文件定义窄 invoker / payload / cleanup lease 接口，不另建通用框架 |
| `ProbeSourceReport.cs` | 不可变 invocation / cleanup / evidence 观察、错误分类、完成 receipt；与 receiver schema 独立 |
| `ProbeSourceEventRecorder.cs` | 有界事件队列、一个被显式等待的 writer、排空/关闭/单次发布；可注入测试 sink |
| `ProbeOutboundDataObject.cs`（仅必要窄改动） | 接入 payload 所有权 seam，提供 owner STA 上的逐项资源快照；若需调整释放，保证两条流均被尝试且分别保留结果，不改格式、顺序、重复项、token 或校验策略 |
| `StageDragProbeSourceSessionTests.cs`、`StageDragProbeSourceRecorderTests.cs`、`StageDragProbeSourceReportTests.cs` 及少量 test support | fake invoker/lease/sink 故障与重入矩阵，以及真实 fixture payload + fake invoker 的存活断言 |

A 不改 `Program`、CLI、receiver host/Router 或三个产品项目，不创建 source Window、真实 WPF invoker、paired supervisor、physical case 协议、隐藏逻辑或消费逻辑。测试依赖继续只有 IntegrationTests → probe，probe 不反向依赖 App/Core/Infrastructure。

##### A.2 所有权、接口与状态合同

1. **manager 持有准入权，session 持有本轮资源**。manager 固定 owner STA、实例身份和递增 generation；ticket 同时绑定实例与代次，两个 manager 的 generation=1 不能互认。所有状态入口先验证 owner 线程，再验证 ticket/phase。只读报告是不可变快照，不向 writer 暴露 session、DataObject、Dispatcher 或 Window。
2. **先占准备槽，再调用外部 seam**。`TryPrepare` 在创建 recorder、重验 fixture 或取得 payload 前进入 Preparing；拒绝路径不调用工厂、不创建新报告、不接管调用者资源。准备可以异步等待 recorder 的 ready 信号，但期间不允许按压启动，续体必须回到 owner STA 检查本轮资格，不能同步阻塞 UI。取得的 payload 只能由该 session 释放；准备回调中发生取消/停止时，新取得的对象进入收尾，不能又发布 Ready。部分构造失败由工厂释放尚未移交的资源（包括已启动的 writer）；移交后的失败由 session 负责。
3. **invoker 是同步且不拥有资源的窄接口**。概念签名为 `Invoke(DataObject, Copy, callbacks) -> DragDropEffects`；只借用数据，不释放、不发布报告、不自行结束 session。A 只有测试项目中的 fake 实现，没有默认真实实现或按异常切换 OLE 的分支。`TryInvokePrepared(ticket, invoker)` 在当前 STA/当前调用栈中执行；若返回 completion task，其首次异步等待必须在 invoker 返回/抛异常和 STA 资源清理之后，不能把真正调用排到后台或另一 Dispatcher 轮次。
4. **先认领启动，再转交 capture**。启动的唯一资格是当前 Ready（B 的 Pressed 资格由窗口验证后交付）；进入 Starting 即排除第二次启动和清理重入。在 B 需要的 capture 释放/回调 lease 挂接处，每个可重入 seam 返回后重新检查取消与 phase；尚未进入 invoker 的取消走未调用终态。A 只用 fake lease 测此合同，默认真实 capture/订阅为 NotApplicable，实际获取和路由留给 B。
5. **回调只有观察和取消建议**。callbacks 捕获本轮 ticket；只在本轮 Invoking 有效。GiveFeedback 与 QueryContinueDrag 任一首次到达共同认领一次 start-observed，再调用 recorder；分别保留两种 first 时间及累计次数。QueryContinueDrag 输入不可变的 Esc/key-state 快照，输出 `KeepDefault` 或 `Cancel`；无取消时不自行编造 Drop/Continue，实际 WPF `Handled/Action/UseDefaultCursors` 映射留给 B。陈旧回调返回不干预的结果，只记 manager 级有界计数，不能给新代请求取消或修改已封存报告。
6. **取消、关闭、释放是三种不同意图**。`RequestCancel(ticket, reason)` 只取消该轮；`RequestStop(Close/Dispose/HostFailure)` 永久关闭 manager 新准入，同时请求当前轮取消。延迟的每轮关闭动作必须带 ticket，不能变成无代次的全局 stop。Dispose 在 invoker 在途时只请求停止、不等待、不销毁资源；`DisposeRequested`、`Completion` 和 `IsFullyDisposed` 分开表达，调用者必须等待 Completion，不能仅凭 using 退出就删除夹具或销毁宿主。外部线程不能直接修改状态，后续 B 必须经 owner Dispatcher 投递；A 不注册会在任意线程同步清理的 CancellationToken 回调。
7. **真实返回才取得 terminal 权**。返回后立即保存原始 effect；异常则保存 source exception、raw effect=null；随后先置 TerminalClaimed，再调用任何 recorder、release 或通知 seam。进入调用前取消/准备失败也可以取得 terminal，但 `InvocationEntered=false`。不存在对外开放、可被 Cancel/Close 任意调用的“强制标成已返回”入口。晚取消、日志错误、cleanup 错误均不得改写已经观察到的 Copy。
8. **终态不等于释放，也不等于重新准入**。owner STA 独立尝试每项 cleanup，一项失败仍尝试其余项；同一 lease 的 attempt 在调用前认领，重复/重入不再次释放，不靠自动重试掩盖失败。按实际释放结果记录 success/error/unknown；只要一项残留或未知，就不发成功的 CleanupCompleted。payload 数据访问被撤销不等于 COM 对象已 GC，报告只声明自身引用和显式流所有权，不强制 GC/FinalReleaseComObject。
9. **资源归零与归档占位分开**。per-round 拖动资源清理后 active-drag 可以归零，但 manager 仍持有 Archiving 槽；只有事件 writer 排空且关闭、terminal 发布 receipt 回到 owner STA 后，才把槽交还 Idle。健康的 None/预取消不妨碍下一轮；准备/调用/cleanup/evidence 故障或未预期 effect 默认锁止该 manager，保留证据，不自动重试外部动作。Close/Dispose 则转入停止完成状态；A 只产生完成 receipt，不实际关闭窗口。

| 阶段 | 是否允许下一轮 | 取消 / 关闭的行为 | 权威完成信号 |
|---|---|---|---|
| Idle | 是（未请求 stop） | stop 后不再接纳 | 无活动轮次 |
| Preparing / Ready | 否 | 取消准备/候选，已取得资源走未调用收尾 | 准备 seam 已返回，且不再移交资源 |
| Starting | 否 | 在每个可重入 seam 后复核；未调用则直接收尾 | 不能把主动 release capture 当作意外失捕 |
| Invoking | 否 | 只记录意图，QCD 建议 Cancel；payload 保持存活 | invoker 实际返回或抛异常 |
| TerminalClaimed / Cleaning | 否 | 不再重写结果，不重复释放 | 每项 release 的结果与资源快照 |
| Archiving | 否，即使 active-drag=0 | 仅记 stop，不重新开启 writer 或 report | writer 停止 + 单次发布 receipt |
| Idle / Stopped / Faulted | 仅健康 Idle | 迟到 ticket 不影响新轮 | owner STA 消费完成 receipt |

预期事件顺序是 `PreparationStarted → PayloadReady → InvocationEntered → [first callbacks / CancelRequested] → InvocationReturned 或 InvocationThrew → TerminalClaimed → CleanupAttempted → CleanupCompleted 或 CleanupFailed → seal/drain → publication receipt`。未调用路径跳过 invoker/回调，不补造事件；没有任何回调但 fake 返回也必须能收尾，start-observed 保持未观察。关闭/取消的 first reason 与发生阶段固定保存，重复原因只作有界计数，不无界追加。

##### A.3 source report 与归零口径

source 自用 schema 从 version 1 开始，不能沿用 receiver 的 status 含义。至少包含以下独立字段组：

| 字段组 | 必须表达的事实 |
|---|---|
| 身份 / 证据等级 | source run id、round id、manager instance、generation、fixture run id、revision、PID/owner thread、fixture/profile/双摘要；A 固定 `EvidenceScope=SourceSessionSimulation`、`InvocationKind=Fake`、`PhysicalOleInvoked=false`，没有窗口时 HWND=null，`UserVerdict=NotCaptured` |
| 调用观察 | `InvocationEntered`、返回/抛异常/未调用、nullable raw effect、source exception；精确 Copy / None / UnexpectedEffect 分开，不把含 Copy 的其他位掩码当成 Copy |
| 取消 / 停止观察 | 是否请求、首个原因/阶段/单调时刻、Esc 是否由 QCD 观察；None 不自动改名为 Escape，晚 Close 不抹去 Copy |
| 回调观察 | 两种回调各自的 first stamp、共享 start-observed 边沿和累计计数；标明 Simulation，不能当作物理 OLE 启动证据 |
| 清理观察 | 每项是否取得、是否尝试/成功、残留或 Unknown、错误集合；清理前后快照与 `CleanupSucceeded`，不能只填统一的 disposed=true |
| 证据健康 | 产生/入队/写入/丢失计数、首个 gap/错误、queue high-water、drain/stream close 状态；与 raw effect 独立 |
| 未执行的后续 Gate | 实际 HWND、受控 target、物理 source-before/after、产品 claim、render pump 按字段标注 NotExecuted/NotApplicable；不复制 fake 值冒充 C/D 证据 |

计数区分 `preparedPayloadOwner`、`activeDragSession`、payload 引用、PreferredDropEffect stream、marker stream、capture lease、callback lease，以及独立的 `roundAdmissionSlot` / `archivePending`。默认无 capture/订阅的 A 测试用 NotApplicable；注入 fake lease 的测试报告显式标明 Simulated。`CleanupCompleted` 的逻辑零点只检查本轮已取得的拖动资源；writer/归档尚未完成时不能把整个进程写成“全部释放”。下一轮 Ready 的 payload=1 与上一轮零点是两张快照。

nullable raw effect / first callback / HWND 等未知字段不得被现有 `WhenWritingNull` 设置悄悄解释为 0：必须带显式 observation 状态，消费者把缺失视为未观察。计数 0 只表示该证据范围内真实初始化并观察到零次，未调用的物理回调使用 NotExecuted。集合、错误及元数据在 producer 端复制冻结；只保留有上限的错误摘要，不把异常对象或可变 details 跨线程排入队列。

最终文件不自称“自身已成功发布”。`ProbeSourceCompletion` 是独立的内存 receipt，表示 report publication 是否实际成功、最终路径与 publication error；future host 根据 receipt 决定失败退出。若发布失败，保留 pending/已写事件，不重试第二个终态文件，不覆盖旧文件。健康 None/取消仍是完整 source observation，不照搬 receiver “Cancelled 必须写 failure.json”的约定；source 内部故障用 failure.json，但保留 raw Copy（若已返回）。seal 之后的 stop 只进入 manager 生命周期/完成 receipt，不再改已冻结的本轮取消观察；报告明确观察截止点，不能反向把归档期间的 Close 写成“调用中取消”。

##### A.4 有界 recorder 的具体机制

1. 每轮一个 recorder、一个显式拥有且最终被等待的 writer task，负责序列化、写 NDJSON、排空/flush/关闭以及单次 terminal 发布。允许在 recorder 内集中启动这一 worker，不允许每事件 Task.Run、fire-and-forget 或 writer 反向同步调用 UI。启动/文件创建在准备阶段完成；Ready 之前确认 recorder 已可用。
2. 使用 bounded Channel，`SingleWriter=true`（owner STA）、`SingleReader=true`、`AllowSynchronousContinuations=false`、`FullMode=Wait`，producer **只用 TryWrite**。不使用 WriteAsync 等待容量，也不使用静默 DropOldest/DropWrite。满时立即返回失败并锁存 evidence fault；已入队记录仍尽力排空。这个选择基于 [Channels 的满队列语义](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) 和 [同步 continuation 的调用线程风险](https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.channeloptions.allowsynchronouscontinuations?view=net-10.0)，不表示已经实测热路径时延。
3. 默认候选 `EventQueueCapacity=128`、`MaxErrorTextChars=1024`，均是可命名、可注入、记入报告的工程内存上界，不是产品 SLA。测试用容量 1 和受控暂停 sink 强制溢出；实施时用实际边沿清单复核默认容量。队列之外也不保留无界事件/错误列表；每种 callback 只发 first、其余累计，在 seal 时发一个汇总。
4. 单调 ticks/frequency、UTC、sequence、run/round/generation、event name 和固定大小观察在 owner producer 取得；sequence 在 TryWrite 前分配，丢失的 first sequence/数量独立锁存。writer 使用原 stamp，不把写盘时间当事件发生时间。健康情况下 Written=Accepted=Produced；不健康情况下明确 gap/未写入数量，不能靠重新编号使证据看似连续。
5. writer 出错只发布线程安全的不可变 health 结果，不从后台释放 payload。owner 在下一状态边界/QCD 发现 fault 时请求取消；即使没有下一回调，也只能等 invoker 返回，不能提前清理。owner 已返回 Copy 时发现 writer 错误，记录 Copy + EvidenceFailed。不尝试通过同一故障 recorder 再递归记录日志错误。
6. `SealAndPublishAsync(immutableDraft)` 认领单次完成，关闭 producer 并提供独立完成信号；seal 不需要挤入已经满的队列。worker 尽力排空、flush、关闭事件流，合并真实 health 后才发布 terminal；任一失败仍尝试其余可执行的收尾。队列溢出但 sink 健康时继续写已接纳的事件；sink 首次写入失败后停止向该流追加，剩余项计为未写入，保留可能不完整的尾行，不把后续 JSON 拼在破损尾行上。Written 只计已写完换行的记录，flush/close 健康另计。重复 seal 共用同一 receipt，不启动第二个 worker/publisher。最终 receipt 必须等 writer 已退出，不能仅等待 Channel reader 已空。
7. 关闭事件流后不再把 `ReportPublished` 强塞回已关闭的同一个事件文件；发布由 final 文件和 receipt 证明。seal 后旧回调不能回填冻结报告。若 writer/外部 invoker 永久不返回，manager 保持不可准入，A 不增加 UI timer 或伪清理；测试使用有上限的外部等待保护且在 finally 放行 fake，真实进程 watchdog 留给 C/D。

##### A.5 自动测试矩阵与实施顺序

下面每行是场景族，实际测试数量以执行结果为准；不预报通过数。所有 invoker 都是 fake，不能通过默认测试触发真实 `DragDrop.DoDragDrop`。

| 场景族 | 必须断言 |
|---|---|
| Copy / None / throw / 未知 effect | 一次调用/terminal/发布；raw Copy 不变、throw 无虚构 effect；Move/Link/Copy 组合/Scroll 等原值保留且不重试 |
| 双 prepare / 双 start / 嵌套 start | 工厂和 invoker 各最多一次；Preparing、Starting、Invoking、Cleaning、Archiving 全程禁止新轮 |
| 首回调任意顺序、重复、只有一种、完全没有 | 共用 start-observed 最多一次；两个 first 和累计值正确；无回调返回照常清理，不补造 start |
| 准备中取消/停止、Ready 预取消、Starting seam 重入取消 | 不调用 invoker；所有已移交资源释放；准备后返回的 payload 不重新变成 Ready |
| fake 内 Cancel / Close / Dispose / fault | fake 继续执行期间 data、两个流和 callbacks 仍有效，Completion 未完成；只有 fake 返回后开始清理 |
| Esc 与普通 QCD | Esc 被单独记录并建议 Cancel；普通输入保持 KeepDefault；不把 GiveFeedback=Copy 视为最终接受 |
| 取消后迟到 Copy；Copy 后 cleanup/log/report 失败 | raw Copy 和取消/失败同时存在，不返回“未交付”的假结论，不自动重放 |
| 旧 ticket / 旧回调 / 两实例同 generation / 旧归档 continuation | 不改新轮资源、effect、取消或关闭意图；不能解除新轮准入槽 |
| 每个 release 分别失败、重入 Cancel/Dispose/prepare/start | terminal/attempt 先认领；其余项仍尝试；资源失败保留 >0/Unknown，不发假 CleanupCompleted，不开新轮 |
| owner 线程 / immutable snapshot | 跨线程入口在产生副作用前拒绝；writer 不接触 WPF；producer 后续变化不改变已排队记录；注入 UTC 回退不影响单调时间/序号顺序 |
| 容量 1 / 热回调洪泛 / writer 慢 | TryWrite 不等 sink，溢出/gap 可见；十万次 fake feedback 只增长计数，不增长逐次事件列表 |
| sink open/write/flush/close 与 terminal 发布分别失败 | 有独立分类、单次发布、残留证据；部分行写入失败后不再追加，未写入数可见；报告健康值以真实 worker 结果为准；没有日志错误递归 |
| writer drain 被受控暂停、重复 seal、receipt 回调重入 | owner STA 可继续处理消息，下一轮仍被拒绝；必须等 worker 退出才交回准入；只完成一次 |
| 真实 fixture + fake invoker | 五种 profile 的路径/重复和四字节 Copy 不变；fake 内直接读 DataObject/两条流证明存活，返回后两个流实际不可读，fixture 重新校验摘要不变 |
| 同 manager 连续 20 轮 Copy/None 交替 | 同 token、不重建 manager，generation 单调；每轮使用不同新输出/recorder/publisher，上一轮零点先于下一轮准备；最后不预载，累计 callback 不假清零 |
| report schema / create-only | Simulation/未执行/未知状态往返可辨；预取消 raw=null；已有 terminal 不覆盖，第二次发布拒绝；三类结果不互相覆盖 |

测试同步使用显式信号/受控 completion 与 `WpfApplicationFixture.InvokeAsync`；禁止用 Thread.Sleep/固定延时让竞态消失。fake invoker 的回调在同一 STA 同步重入，用断言检查返回前资源，不让它真实挂住测试 UI；后台 sink 的暂停必须由非 UI 测试驱动在 finally 解除。

实施顺序：

1. **A1：契约与同步会话**。先建 ticket/结果/资源观察和最小 manager/session，用 in-memory recorder + fake invoker 验证准入、返回边界、取消与逐项清理；先测故障再扩展。
2. **A2：有界证据与归档**。落地 source recorder、不可变 draft/receipt、create-only 文件发布及排空；追加满队列、writer/发布失败、归档重入门禁。已有 receiver writer 不改。
3. **A3：真实 payload 接合**。仅做必要的 payload ownership/逐项快照窄改动，五种 fixture 在 fake 调用前/中/后验证；补跨实例/旧代及同 manager 20 轮。这里的 20 轮只是 simulation，不是 D 的同 HWND 物理 Gate。
4. **A4：回归与收口**。唯一临时 ArtifactsPath 下执行 `StageDragProbe` 全部定向测试、Debug/Release 零警告构建及完整 Release suite；使用既有外部只读审计证明真实库/资产前后不变。保存 TRX、源代码 revision/候选 SHA 和明确的 simulation scope，检查 diff 仅 probe/tests/文档且无真实 OLE 入口。

A 完成条件：上述自动场景实测、所有正常轮的 payload/流/已取得 lease 真正释放且 writer 退出、故障轮不能伪归零或重新准入、已有 N2.1/N2.2 行为不回退。之后**停止在 B 之前**，报告为“N2.3-A 自动契约通过，真实窗口/物理 OLE 未验证”；不自动提交、推送、部署或启动拖放。

规划阶段按生命周期自审识别的主要风险是：把取消当调用完成、清理前撤销引用冒充资源归零、异步 writer 将工作回推 UI、报告先发布后发现释放失败、归档空档重新准入、旧代 completion 关闭新轮。规划本身不证明已复现或修复；实施验证记录如下，通用约束继续使用 R2/R19/R29，不新增重复规则或产品决策。

##### A 实施记录（2026-09-22）

- 在进入源码 `3e009b8` 及未提交的 A 规划之上完成 A1–A4：新增 source manager/session、source report 与有界 recorder，以及四份 source 测试/支架文件；只对既有 `ProbeOutboundDataObject` 做所有权接口、逐项快照和两流释放的窄改动。`Program`、options、command runner、receiver/Router 和三个产品项目均未修改，仍无可执行的真实 source invoker。
- manager 以实例 + generation + round ticket 隔离轮次，从准备开始到归档 receipt 回到 owner STA 才交还准入。fake 同步调用期间的 Cancel/Close/Dispose/QCD Esc 只记录意图；terminal 在清理前认领，payload/两流/已取得 lease 分别观察，失败或未知不能假归零。主动 capture 转交只屏蔽该 release 栈内的 CaptureLost，不屏蔽其他取消。
- 准备和归档续体显式通过 owner Dispatcher 提交。自审用“owner STA 上暂时没有 SynchronizationContext”的反例复现首版准备续体落到线程池、未能准备 payload；修正后该反例通过。另用调用中/归档中的 HostFailure 复现首版只保存 stop 原因却仍返回成功；现以独立 `HostFailureObserved` 锁存过程失败，即使稍后 Close/Dispose 或 raw Copy 也不能抹去。修复前反例分别为 1 项、2 项失败，最终全部通过。
- recorder 默认容量 128，只入队不可变、带 producer 单调时钟/UTC/序号的边沿；两个高频回调各保留 first 和累计值。唯一 owned writer 执行磁盘 I/O；满队列、open/write/flush/close、partial line 和 publication 故障都有独立门禁。partial write 后不再向破损尾行追加；seal 不占队列容量，返回的 task 直到 worker 退出才完成，不能以队列已空冒充释放。错误摘要上限 1024 字符，参数是工程上界，不是性能 SLA。
- source 报告固定为 `SourceSessionSimulation` / `Fake` / `PhysicalOleInvoked=false`；缺失的 effect、HWND、callback 以 nullable + observation 状态区分。NDJSON 的 `InvocationDispatchPrepared` 只证明已准备派发；实际是否调用及进入/返回时刻由 `InvocationEntered` / `EnteredAt` / `ReturnedAt` 保存，避免日志 seam 重入取消后仍把派发预备事件冒充调用。文件只陈述 seal 截止的观察，发布结果及 seal 后的关闭/宿主故障由独立 completion receipt 表达，不回写已经冻结的报告。
- 五种真实 fixture 均在 fake 内验证数据/两流存活、返回后释放并重验摘要。另用同一 manager、同一 token、不同新输出/recorder/publisher 完成 batch100 的 20 轮 Copy/None 交替；每轮先证明上一轮零点，再准备下一轮，最后不预载。这个结果没有 source HWND，不能作为 D 的物理 20 轮证据。
- 新增 source 67/67；全部 `StageDragProbe` 定向 **206/206**；source 连续五轮 **335/335**；完整 Release 为 Core **114/114** + Integration **807/807**，共 **921/921**、0 skip。独立 Debug/Release solution build 均为 **0 warning / 0 error**，差异检查通过。
- 完整有效只读前后审计比较整个 `.Database`（含 `.Assets`）：schema 8、Stage 50、Ready 60、`quick_check=ok`、0 外键/缺失/长度/SHA/越界异常，schema、八表与全部资产摘要不变。数据库 SHA-256 仍为 `AC343AE5CF4CBDDFA2746B8CA10A04D66114AC2A9D61D6AF5062C70EC05D2CA9`，最终无 Vok/probe 残留。首次审计传错 AssetRoot 层级，仅形成两份无效观察，未写库；已按产品源码确认应为 `%LOCALAPPDATA%\Vok\assets\clipboard`，有效基线只使用下面的 `db-before-verified.json`。
- 构建/审计/测试证据位于 `%TEMP%\vok-n2-3a-artifacts-1790081637754`：`gates/db-before-verified.json`、`gates/db-after.json`、`gates/build-release.binlog`、`gates/build-debug.binlog`、`tests-targeted-final/`、`tests-final/`、`tests-source-repeat-1/` 至 `-5/`；红/绿反例保留于 `tests-host-failure-before/`、`tests-prepare-owner-before/` 及后续 source TRX。早期 `db-before.json` / `db-before-valid.json` 的 AssetRoot 参数错误，不得作为通过证据。
- 最终 Release probe DLL SHA-256 为 `1A5BB36A2E4251BCF2CAF454B4DC0B610048796437FD01E52B0175FF05EB6205`，exe 为 `7EF16DD7CBD8F14B525766046B687FF045E089642EF767DCBA38A1D5A0A5C64D`。完整回归的外部 watchdog 受控超时证据保留于 `%TEMP%\vok-drag-process-tests-e8682008d3f541b6bce19378e33b69e5`，未删除既有失败证据或用户文件。
- N2.3-A 自动契约通过，随后按用户要求随本次提交归档；提交轮只更新状态，沿用上述验证记录，未重跑测试、推送或部署。下一步 B 才增加可见 source 窗口、WPF adapter、host 与严格 CLI，同时明确扩展 A 目前固定的 Simulation 报告模式；B 默认自动套件仍只用 fake invoker，真实调用留给 C 的受控物理 Gate。没有窗口/真实 OLE、永不返回的外部调用、隐藏 HWND 或产品消费的完成声明。

#### 双进程编排与报告布局

- 一个 gate root 原子创建，source 与各轮 receiver 使用互不覆盖的子输出目录。source 进程拥有自己的固定 fixture；每个 case 描述绑定 gate/case id、source run id/generation、fixture run id/digests、双方 PID/HWND 和 exe/DLL SHA。case 文件 create-only 原子发布，只在非在途准备期读取并验证；旧 case、错实例、不同构建或缺失字段拒绝 arm。
- 握手顺序固定为：source 准备 fixture 并发布未 arm 的 `SourcePrepared` → receiver 发布 `ReceiverShown` → supervisor 核验双方身份并原子发布 case → source 读取/验证后发布 `SourceReady` → 才允许真实按压。准备和 arm 的事件分开，避免 source 等 case、supervisor 又等 source-ready 的循环等待。复核 HWND 归属，不靠启动后的固定等待；每轮可以重建一次性 receiver，但 source PID/HWND 必须贯穿 20 轮，不能用重启 20 次 source 冒充长期资源 Gate。
- 受控 paired receiver 复用现有 `closeOnTerminal:false` 能力，终态 observation 发布后仍保留窗口；supervisor 观察源调用返回/本轮归档后，才向固定 receiver HWND 请求关闭并等待原 Process handle 退出。独立 `receive` 既有默认行为继续保留；“排入 Background 关闭”不作为跨进程完成信号。
- 只允许对已绑定受控窗口执行物理 Drop。使用 probe-only 对端存活/目标 HWND 检查，在 QueryContinueDrag 的真实松手分支中对未知、已退出或本实例目标请求 Cancel；不拿坐标轮询推断拖动已结束，不激活或移动外部窗口。这是 N2.3 防误投的测试限制，不是最终产品目标白名单；N2.4 外部目标 Gate 再单独扩展。
- source 结果、receiver observation 和 case gate verdict 是三份不同事实。None 可以没有目标 Drop；None 目标门禁须观察到绑定目标的 Enter/Over 拒绝、源返回 None、零载荷读取和双方收口，不能把从未进入目标的自吞/区域外取消混作拒绝通过。不强造 `PolicyNone` 或“缺 Drop 挂死”；Throw 的目标 Failed + 源 None 也不强改成 source exception。若源已返回而 paired target 尚无终态，用同 case 的显式 source-return 握手收口，单独记录“来源已结束但未观察到 Drop/Leave”，不伪造原生事件、不改独立 receive 的 `WindowClosedBeforeDrop` 语义；Copy 却没有匹配的目标接受报告必须失败。
- 沿用一次性 publisher，但每轮拥有自己的新 report 目录，进程 startup/exit 与最终 run summary 在 run 层；不复用已 `Attempted` 的 writer，也不覆盖上一轮。各轮 source-before/after 都独立保存；缺 after 或摘要不符不通过。无实际文件复制的 receiver 只证明协议接受，不是 Explorer 的物理文件产物证据。
- watchdog 保留原 Process handle，分开记录准备等待与 OLE/归档期限；人工尚未开始按压不算 OLE 卡死。deadline 为可记录的工程侧参数，不是产品 SLA，N2.1-D 的 30 秒自动测试上限也不直接当人工总操作时限。缺回调/阻塞/写日志停滞只判明确的阶段超时失败，不伪造返回或 cleanup；强制终止时保留已有证据，绝不按进程名查杀或覆盖终态报告。
- 只解析完整发布的 JSON/完整换行事件；源与目标分别用本进程序号/单调时间建立顺序，通过 case 与真实握手关联，不把两个独立相对计时器相减当跨进程因果证明。`UserVerdict` 未收集始终为 NotCaptured。

#### N2.3 验收矩阵与停止条件

自动层至少覆盖：真实 OLE invoker 零调用的启动/关闭；低于阈值和非左键；释放 capture 时重入 LostMouseCapture；首次两个 source 回调的任意顺序与重复；正常松手默认行为不被 Handled 吞掉；按压中 Esc/Close/Dispose；invoker 抛异常/无回调返回；取消后迟到 Copy 的原始事实保留；清理和 writer 故障；旧代回调不改新代；同 manager 连续 20 轮；paired receiver 的发布/保活/真实退出；case 串线、输出覆盖和坏参数零创建。

物理层单独执行：

| 案例 | 必须保存的观察 |
|---|---|
| Copy：1/10/100、mixed、duplicate | 源 raw Copy；目标只读一次 FileDrop/Preferred，原序、重复位置和四字节 Copy 一致；源摘要不变 |
| None：1/10/100 | 源 raw None；目标零载荷读取，允许没有 Drop；源仍可见且 UI 可继续使用 |
| Esc：1/10/100 | source QueryContinueDrag 观察到 Esc、请求取消、最终 None；不得只依据 receiver 的 PreviewKeyDown |
| Throw：1/10；source invoker exception 另作故障注入 | 目标失败和源原始返回分别记录；源异常有独立测试，不混同两条机制 |
| 同源 HWND 的自吞回路 | token 拒绝，无新 inbound target/租约/载荷读取；松手不重新进入 source |
| 活动 OLE 内关闭/Dispose、受控目标退出/崩溃 | 正常返回/异常与是否取消分别记录；返回前 payload 未释放；源未抢前台、未误投到目标消失后的外部窗口 |
| 同一 source PID/HWND 连续 20 轮（建议固定 batch100，奇数 Copy、偶数 None） | 每轮逻辑资源零点和进程资源样本；最后不再预载下一轮，全部 per-round 所有权归零；20 个新 source 进程不能算通过 |

本阶段全部保持源 HWND 可见、不调用 Hide；§9 的隐藏源窗/Explorer/桌面/跨目标 20 轮是后续 N2.4/N2.7 门禁，不要求在 N2.3 提前执行。Handle/Thread/USER/GDI 和内存先记录基线及趋势，不靠强制 GC 或虚构硬阈值通过。

任何双启动、返回前释放 payload/销毁源窗、raw Copy 被取消意图抹去、自吞副作用、伪造清理零点、无界日志/同步热路径磁盘写入、跨 case 或同点重复失败，均停止当前切片并分析，不继续叠加延时或扩展到隐藏 HWND。A 自动契约已按上文记录验证，B–D 剩余自动/物理门禁仍待实现和执行。

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

已实现的 N2.1 命令面（以下路径占位符须替换）：

```powershell
dotnet restore src/Vok.sln
dotnet build src/Vok.sln -c Release
dotnet test src/Vok.sln -c Release --settings Vok.runsettings

dotnet run --project tools/Vok.StageDragProbe/Vok.StageDragProbe.csproj -- prepare --allowed-root <existing-root> --output-root <new-direct-child> --repository-root <vok-root> --source-revision <revision> --profile mixed
dotnet run --project tools/Vok.StageDragProbe/Vok.StageDragProbe.csproj -- receive --allowed-root <existing-root> --output-root <new-direct-child> --repository-root <vok-root> --source-revision <revision> --effect Copy
```

以下 source/双进程编排命令仍属 N2.3 计划，尚未实现；case 绑定由编排器创建，具体参数在 B/C 中校验：

```powershell
dotnet run --project tools/Vok.StageDragProbe/Vok.StageDragProbe.csproj -- source --allowed-root <gate-root> --output-root <new-source-child> --repository-root <vok-root> --source-revision <revision> --profile batch100 --case-root <gate-case-root>

pwsh -File tools/Start-M3StageDragGate.ps1 -AllowedRoot <existing-root> -OutputRoot <new-gate-child> -Profile batch100 -Rounds 20
pwsh -File tools/Read-M3StageDragReport.ps1 -ReportRoot <path>
```

后续编排必须保持显式输出根、create-only、分进程 receiver/source、固定 PID/HWND 和无产品存储初始化。N2.1 进程测试只用独立 test-owned 四文件目录（exe、dll、deps、runtimeconfig）运行实际 apphost，并验证可重读 fixture 和无 OLE receiver 启停；没有调用计划中的 source 命令。

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
