# 范围规则与死路

> 只有证据、范围、环境和状态仍匹配时才具备高可信度。仅记录已复现或有权威依据的约束，不记录猜测。

失效条目保留删除线，并注明原因和日期。

## 索引

| ID | 摘要 | 范围 | 高风险 |
|---|---|---|---|
| R1 | 主工作台窗口保持不透明、非分层 | 窗口与渲染 | 是 |
| R2 | 系统资源由专属线程拥有，禁止固定延时同步 | 生命周期与并发 | 是 |
| R3 | 前台恢复必须观察结果并支持降级 | 焦点与注入 | 是 |
| R4 | 剪贴板由单一 STA 服务拥有，以 sequence 判定变化 | 剪贴板 | 是 |
| R5 | SQLite 同步执行出 UI；数据分页与 UI 虚拟化并行实施 | 存储与规模 | 是 |
| R6 | Native AOT、裁剪和发布优化必须先通过真实原型 | 发布与互操作 | 是 |
| R7 | xUnit v3 必须显式安装适配器并让零测试失败 | 测试基础设施 | 否 |
| R8 | M0 四项呈现结果独立观察，诊断值不能冒充输入就绪 | 宿主验收 | 是 |
| R9 | 性能门槛先做三方同机对照，再由用户确认 | 性能验收 | 是 |
| R10 | 呼出窗口使用目标显示器的系统工作区 | 窗口与渲染 | 是 |
| R11 | Visible 丢失应用前台时被动隐藏并保留新前台 | 生命周期与并发 | 是 |
| R12 | 托盘与设置复用唯一宿主事务和统一退出所有权 | 生命周期与并发 | 是 |
| R13 | 输入就绪与可见 HWND 不得冒充内容已呈现 | 宿主验收 | 是 |
| R14 | 快捷键录制必须保持可恢复的注册/持久化事务 | 输入与热键 | 是 |
| R15 | 局部控件样式不得截断 Fluent 隐式主题样式 | 主题与控件 | 否 |
| R16 | 长短按释放必须绑定按压代次与工作台代次 | 输入与热键 | 是 |
| R17 | 启动台卡片单元均分完整视口且不限制自由滚动 | 启动台布局 | 否 |
| R18 | 启动台只保存引用，拖入与启动按代完成且先隐藏后 Shell | 启动台业务 | 是 |
| R19 | 启动台反馈、排序和外部拖放必须先认领终态再产生副作用 | 启动台交互 | 是 |
| R20 | 文件剪贴板意图必须按 DROPEFFECT 掩码解析，模糊值不得成为 MOVE | 剪贴板文件 | 是 |
| R21 | CF_BITMAP 不按 HGLOBAL 读取，优先复制系统合成 DIB/DIBV5 | 剪贴板图片 | 是 |
| R22 | 图片像素格式与 CF_HDROP 同现时不得仅凭 HDROP 判为文件 | 剪贴板图片 | 是 |
| R23 | 图片缩略图只按稳定展示槽可见性加载并以版本化容器门禁落地 | 剪贴板图片 UI | 是 |
| R24 | 图片 replay 必须区分资产完整性失败与暂存/写入环境失败 | 剪贴板图片写回 | 是 |
| R25 | 图片目录导出必须双重正向确认目标并在文件副作用前认领工作区代次 | 剪贴板图片写回 | 是 |

## 规则

### 窗口与渲染

- [x] R1：工作台 `MainWindow` 使用普通不透明 HWND 和 `AllowsTransparency=false`；透明视觉仅允许隔离在按需小型窗口中。
  - 范围：WPF 主宿主、全屏/高 DPI 渲染。
  - 证据：WPF 官方说明渲染成本受像素量影响；姊妹项目全屏 layered surface 已出现显著固定内存成本；见 `decisions.md §1`。
  - 最后验证：2026-08-23。
  - 状态：active；窗口几何另见 R10。

- [ ] R10：每次呼出使用目标显示器 `MONITORINFO.rcWork` 作为窗口矩形，保留任务栏可见且可点击；不得硬编码任务栏高度或方向。目标显示器选择、会话期 Topmost、前台/焦点及隐藏恢复保持独立契约。
  - 范围：M0 窗口放置、多显示器、任务栏与高 DPI 工作区几何。
  - 证据：2026-08-24 用户确认产品契约；实现已从 `rcMonitor` 切换到系统 `rcWork`，顶部、底部、左侧、右侧合成矩形测试及 Release 全套 46/46 测试通过。
  - 最后验证：2026-08-24。
  - 状态：active；自动验证与用户真机确认的任务栏交互、铺满范围、两种隐藏焦点返还及视觉完整性均已通过。

### 生命周期与并发

- [ ] R2：WPF UI、热键、剪贴板、Shell/COM、SQLite 和图片任务各有明确所有者；跨边界只投递不可变数据或调用窄接口，禁止同步等待 UI、散落 `Task.Run` 和不明固定延时。
  - 范围：进程内全部后台服务和系统事件。
  - 证据：Vok 产品要求覆盖失败、取消和重复触发；参考项目的轮询、跨线程等待和全局复位清单导致时序耦合；见 `decisions.md §1`。M2.2 将启动台与文件剪贴板适配器收敛到 DI 唯一拥有的容量 128 Shell/COM STA executor。M2.3.2–3 新增单消费者 MTA 图片 worker，只接受持有精确 `input+canonical` reservation 的 job。M2.3.4 又以单 MTA/容量 4 asset coordinator 唯一串行化文件提交、Image 元数据、删除和 quarantine；预取消必须在占用 slot 后才注册回调，否则同步回调会在 slot 尚不存在时发布终态并泄漏后来取得的容量。两条 worker 的取消、队列/容量拒绝、失败、shutdown 和 5 秒 watchdog 均先认领一次终态；自动测试覆盖入队前/排队中/认领后取消、同轮 shutdown、watchdog 超时后延迟归零、重复 Dispose、删除失败启动重试和旧 revision claim 失效。
  - 最后验证：2026-09-01。
  - 状态：active；WPF、热键、Clipboard STA、共享 Shell/COM STA、SQLite、图片 worker 与 asset coordinator 均已有显式所有权和退出边界并已接入产品组合根。隔离 report v6 的 20 轮、60 次 4K/8K 捕获→规范化→schema 5/CAS 提交中，每项完成后 normalizer job、asset job、临时 lease 与 staging 均归零。

- [ ] R11：`Application.Deactivated` 是整个 Vok 丢失前台的唯一权威信号；宿主不得暴露或转发无消费者的单窗口失活接口。除同代持有由真实外部 OLE `DragEnter` 建立的窄拖放租约外，当前 generation 在下一 Dispatcher 轮次仍为 `Visible` 时必须以 `ForegroundLost` 被动隐藏；真实前台捕获只用于诊断，HWND 为 0 或捕获失败也不得阻断。拖放租约必须在 Drop、窗口级 DragLeave、隐藏、换代、退出或下一次 DragEnter 时收尾，终态先占有后执行，陈旧/重复终态不得影响新代；已经可见时再次显式热键关闭可抢占租约，只有本次长按从隐藏态呼出的原始松键仍等待拖放终态。普通失活必须完成 Hide、退出 Topmost、状态机和诊断收尾，但不得恢复旧捕获目标；状态明确为 `NotRequested`。
  - 范围：M0 Deactivated、前台切换、隐藏策略、诊断和下一代目标捕获。
  - 证据：2026-08-24 用户确认产品契约；开始菜单首次点击实测揭示前台 HWND 短暂无效会让第一版漏关，现以应用级因果独立于诊断捕获。2026-08-28 的 M1 外部拖放实现仅在存在 `FileDrop` 的真实 DragEnter 后建立同代租约；自动测试覆盖租约内失活、窗口内部取消、接受/重复终态、显式热键优先、原始长按呼出延后及旧终态跨代隔离，见 `decisions.md §19`。
  - 最后验证：2026-08-28。
  - 状态：active；普通失活与外部物理拖放均已真机验收，拖放窄抑制和竞态自动验证通过；混合 DPI 跨屏仍是后续专项边界。

- [ ] R12：托盘左键和菜单“显示界面/设置”必须进入 `WorkspaceController` 的幂等显示事务，不得直接调用 WPF `Window.Show/Activate`；右键菜单取得本进程前台前先捕获外部目标。设置只作为唯一主 HWND 内的视图切换。窗口、托盘菜单和其他入口的退出必须先占有一次退出事务，完成宿主隐藏/Topmost 清理后再由唯一应用生命周期所有者关闭；退出占有后迟到失活、热键和显示请求不得产生第二条事务。
  - 范围：系统托盘、设置壳层、显隐代次、失活竞态、Alt+F4 与应用退出释放。
  - 证据：现有 M0 把显示器放置、会话期 Topmost、真实前台、输入焦点和目标恢复集中在 `WorkspaceController`，旁路显示会跳过这些门禁；任何通知区域菜单都会在打开后成为本进程前台，菜单项回调时再捕获可能只得到 Vok 自身。当前通知图标适配器在右键按下或键盘菜单交互开始时捕获目标，原生 `HMENU` 返回命令后才发布；旧代失活与新代设置显示按同优先级队列排序，每轮结束清除快照，退出先占有且失败可重试。
  - 最后验证：2026-08-27。
  - 状态：active；Release 默认/隔离构建 0 警告/错误、Core 24/24 与 Windows/WPF 69/69 共 93/93 测试、自包含 win-x64 发布均通过；用户已确认原生菜单视觉和其余壳层/托盘测试通过。

### 输入与热键

- [ ] R14：快捷键录制、暂停、恢复和更换必须由专属热键消息线程串行拥有。录制入口只有在旧组合确认注销后才可接受候选；取消、失焦、切页或隐藏必须恢复旧组合。候选先注册、后持久化；候选冲突不得写库，保存失败必须反向恢复旧运行时注册。命令一旦投递不得用调用方取消掩盖系统副作用；候选与旧组合都无法注册时必须报告聚合失败，不能显示或保存为成功。
  - 范围：设置页快捷键录制、`RegisterHotKey` 生命周期、运行时重注册、SQLite 设置与启动加载。
  - 证据：固定注册会让当前组合在录制时产生 `WM_HOTKEY` 并触发工作台隐藏；注册与 SQLite 保存分属两个资源所有者，单边成功会形成当前进程与重启状态分叉。`decisions.md §11` 的消息线程命令和补偿事务已通过暂停/恢复、候选冲突回注册、初始冲突恢复、取消、保存失败回滚、损坏值局部降级、重启持久化及录制中 Esc 路由测试。
  - 最后验证：2026-08-27。
  - 状态：active；隔离 Release 0 警告/错误、127/127 自动测试（含真实 Win32 注册暂停/更换）和 win-x64 自包含发布通过；真实物理录制与重启呼出待用户验收。

- [ ] R16：短按/长按只能以一次完整物理按压的 Started/Released 代次判定，并把该代绑定到按下时的 Workspace generation；旧代、取消代和重复释放不得按“当前可见性”切换新会话。`RegisterHotKey` 继续拥有按下入口，释放观察只允许在活跃按压期间运行，释放、暂停、换键、取代或退出后必须停止；不得用常驻全键态轮询、低级键盘 Hook 或固定等待阈值替代。
  - 范围：全局快捷键短/长按、按压期键态采样、显隐与焦点恢复竞态、录制事务交接。
  - 证据：`WM_HOTKEY` 不提供释放事件；自动测试已复现并约束快速释放、旧释放晚于新工作台、重复释放、丢失释放后新按压取代及计时器失败。双 generation 和释放先占有使这些路径不产生跨会话隐藏；见 `decisions.md §16`。
  - 最后验证：2026-08-28。
  - 状态：active；独立 Release 0 警告/错误、149/149 自动测试和 win-x64 自包含发布通过，真实物理手感与焦点往返待验收。

### 主题与控件

- [ ] R15：使用 .NET WPF Fluent 主题的控件若只需局部触发器或布局状态，不得直接声明一个无 `BasedOn` 的本地 `Style` 覆盖控件隐式样式；优先把显隐等状态移到外层容器，确需控件本地样式时显式继承 `{StaticResource {x:Type ControlType}}`。主题修复不得为浅色/深色分别硬编码颜色。
  - 范围：主壳层和设置页的 WPF Fluent Button 等主题控件。
  - 证据：右上设置按钮是 Header 中唯一声明无 `BasedOn` 本地 `Button.Style` 的按钮，因此截断应用的 Fluent 隐式 Button 样式并在暗色主题保留浅色外观；返回按钮没有该覆盖。将 Visibility 触发器移到外层 Grid 后，WPF 测试确认按钮无本地 Style、重新解析到隐式 Button Style，实际 Foreground/Background 在浅色与深色间均改变；两套真实渲染快照确认浅色白底与暗色深灰底正确切换。
  - 最后验证：2026-08-27。
  - 状态：active；隔离 Release 0 警告/错误、127/127 自动测试、浅/深真实 WPF 快照目视复核及 win-x64 自包含发布通过。

### 启动台布局

- [ ] R17：启动台有效视口不得为适配固定卡片高度而缩短或留下不足一行的余量。以整数行数均分完整视口，卡片单元高度保持 `viewportHeight / rowCount` 的分数 DIP，不能对每行独立布局取整；卡片单元包含其纵向视觉间距。滚动必须保留原生像素偏移，不得强制整行步进、吸附或修改用户滚动位置。启动期按显示器工作区/DPI预热布局档案，稳定呼出只查表；显示环境变化可为新视口建立一次新档案。
  - 范围：启动台四列卡片、Per-Monitor V2 高度、ScrollViewer 与布局缓存。
  - 证据：固定 96 DIP 卡片不能普遍整除可变视口；900 DIP 工作区的 770 DIP 启动台视口均分八行为 96.25 DIP，实测 `UseLayoutRounding=true` 会把每行压到 96 DIP并重新产生 2 DIP 余量。关闭逐行布局取整后，490/770/910 DIP 自动验证满足 `H = N × C`，任意 37.5 DIP 滚动偏移不被改写；见 `decisions.md §17`。
  - 最后验证：2026-08-28。
  - 状态：active；156/156 自动测试、620/900 DIP 浅色/深色与悬浮态真实渲染通过，混合 DPI 跨屏待真机确认。

### 启动台业务

- [ ] R18：外部文件、目录与应用快捷方式拖入启动台只创建引用，不得移动、改写或删除目标；`.lnk` 必须保留快捷方式自身路径，不能解析后丢失参数、工作目录或图标语义。路径规范化、Shell 图标/打开/定位由专属 Shell/COM STA 拥有，SQLite 元数据由单一执行器串行拥有。单击“打开”和右键“打开/打开所在目录”必须先占有一次动作并完成工作台 Hide、退出 Topmost、恢复呼出前目标，再调用 Shell；隐藏失败不得启动。移除只删元数据引用。
  - 范围：M1 启动台外部拖入、去重、持久化、原图标、卡片命令、打开/定位/移除与生命周期竞态。
  - 证据：`decisions.md §18`；SQLite 重启/排序/大小写去重与“移除不删源文件”、真实 Shell 文件/目录/链接解析和 EXE PNG 图标、隐藏失败不调用 Shell、严格“隐藏后 Shell”顺序，以及拖放终态竞态均有 Windows 集成测试。
  - 最后验证：2026-08-28。
  - 状态：active；迁移修复后隔离 Release 0 警告/错误、174/174 自动测试、自包含 win-x64 发布及真实 WPF 渲染通过；2026-08-28 用户确认物理拖入、卡片显示、打开/定位/移除真机测试通过。

- [ ] R19：启动台交互的完成、取消和替换必须先认领当前 generation 再清理视觉或产生 Shell/SQLite 副作用。外部拖放不得在连续 DragOver 中读取外国 `IDataObject`，数据只在 Drop 终态最多快照一次；整区提示必须随任一窗口级终点、隐藏或换代同步清除。内部排序只在系统拖动阈值后开始，移动阶段仅预览内存顺序，松手后最多调用一次仓储排序事务；Esc、失去捕获、隐藏、设置或外部拖入须精确回滚 Previewing 会话。单击反馈由动画完成驱动启动，watchdog 只回收缺失回调且不得造成双启动或跨轮启动。
  - 范围：启动台单击动画、内部拖动排序/边缘滚动、外部 OLE FileDrop 热路径、工作台显隐与热键竞态。
  - 证据：旧 DragOver 同步读取 COM 数据且坐标过滤窗口 DragLeave，使源取消时租约与蓝框泄漏并持续延后关闭；计数 IDataObject、窗口内部 DragLeave、热键抢占、generation 双终态、排序回滚/失败/取消和真实 WPF 动画/蓝框测试已覆盖，见 `decisions.md §19`。
  - 最后验证：2026-08-28。
  - 状态：active；用户确认外部拖放、关闭与物理排序功能测试通过；最终零边距/零圆角/无内容整区蓝框在隔离 Release 190/190 自动测试、win-x64 自包含发布与浅/深渲染中通过。混合 DPI 边缘滚动仍待专项回归。

### 焦点与注入

- [ ] R3：隐藏、启动和粘贴不得把 `SetForegroundWindow` 返回前台视为必然成功；必须观察真实前台窗口，失败或 UIPI 权限不匹配时明确降级。
  - 范围：目标窗口恢复、应用启动、剪贴板粘贴。
  - 证据：https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow 明确说明系统限制前台切换且即使满足条件也可能拒绝；2026-08-24 逐代实测将失败归为 B：顶层已恢复，目标线程子焦点为 0。按 `decisions.md §4` 做窄回退后，用户确认快捷键与 Esc 隐藏均返回原文本框。M2.1 粘贴只有 Workspace 同代隐藏成功、恢复结果 `Succeeded`、receipt 仍当前且注入前顶层/子焦点身份再次匹配才发送 Ctrl+V；M2.2 文件主动作复用同一顺序，明确 MOVE 的手动降级风险，Unknown 强制按 COPY，独立 COPY 次动作不隐藏或注入。自动测试覆盖恢复失败、外部覆盖、焦点变化、重复动作、Missing 预检、COPY/MOVE/Unknown 和成功顺序。
  - 最后验证：2026-08-28。
  - 状态：active；M0 隐藏恢复与 M2.1 文本粘贴已通过真实物理回归；M2.2 文件粘贴门禁已自动验证但 Explorer 真机交互待验收。默认禁止连接输入队列，只有 §4 已证明的 B 类条件允许单次窄回退。

### 宿主验收

- [ ] R8：M0 的窗口可见、会话期 Topmost、真实前台和真实键盘焦点必须作为四个独立结果观察；只有真实输入控件同时通过 Win32 前台/focus HWND 与 WPF 键盘焦点验证后才能进入输入就绪状态。
  - 范围：M0 显示状态机、窗口呈现、焦点锚点、自动探针和真实键盘验收。
  - 证据：当前实现把 `Show()/Activate()/Keyboard.Focus()` 的调用结果在未确认真实 OS 状态时提交为 `Visible`；2026-08-24 用户真机观察到窗口可见但不在顶层。用户同日确认 M0 是生产级宿主内核，诊断页不得冒充搜索功能或输入就绪。
  - 最后验证：2026-08-24。
  - 状态：active；进程内 WPF STA、外部结构化同代五门禁、长期曲线及真实物理呼出/输入/两种隐藏返还均已通过，M0 宿主冻结。该结论不包含正式搜索编辑器或完整 IME 语义。

- [ ] R13：呼出链路必须把输入就绪与目标窗口内容真正呈现作为独立结果；`IsWindowVisible`、前台/焦点五门禁以及全局、渲染前触发的 `CompositionTarget.Rendering` 均不能单独证明该 generation 的视觉树已经提交或被 DWM 呈现。渲染回调内不得执行进程采样、批量诊断发布或无消费者的 ViewModel 更新等阻塞实际帧的同步工作；不得用固定延时掩盖空背景帧。
  - 范围：长期隐藏 WPF 主窗的重复呼出、首帧/Present 观测、渲染关键路径和宿主状态命名。
  - 证据：2026-08-27 对修复前 Release PID 的同轮外部像素与 HWND 诊断采样复现：约 23.4 ms 时 HWND 已可见但只有背景；内部输入就绪为 31.1 ms 后，约 41.7 ms 的外部采样仍只有背景；内部 `CompositionTarget.Rendering` 时间戳为 41.1 ms 后，约 71.5 ms 的外部采样仍无静态按钮、边框和文字；约 95.9 ms 才观察到完整内容。首张与稳定截图同时证明缺失的是整棵内容视觉树而非单独文字。修复采用 `decisions.md §10` 的 generation cloak + render-return + 专属 `DwmFlush` 栅栏；最终 Release 20/20 加 5/5 轮真实快速探针均零失败，且每轮首次解除 cloak 的屏幕稀疏样本都与后续稳定样本一致。
  - 最后验证：2026-08-27。
  - 状态：active；generation 级内容呈现门禁、失败 watchdog、跨代清理和诊断协议已实现并通过 107/107 自动测试及 25 轮真实像素/焦点往返；2026-08-27 用户真机确认主观呼出观感测试通过，ETW/PresentMon 仅保留为额外性能分析边界。

### 性能验收

- [ ] R9：不得凭主观感受或单点进程快照制定/宣布 M0 性能通过；先在同机同条件下记录 Workbench、vok(old) 与 Vok 的隐藏常驻、冷/热呼出、稳定帧、输入就绪、恢复、资源峰值/回落及重复显隐增长斜率，再提出 Vok 门槛并由用户确认。
  - 范围：M0 性能遥测、外部探针、参照物对照和最终验收。
  - 证据：Workbench 历史探针记录透明 WebView2 HWND 可见后 surface 仍晚约 60–570ms；2026-08-24 已完成三方初始对照及 Vok 固定 HWND 1000 轮/5 分钟回落。Vok 自身已有可靠分布，但两个参照物仍只有约三份热样本，不能形成 P95；正式 Present 证据也尚缺，详见 `docs/M0_HOST_KERNEL.md`。
  - 最后验证：2026-08-24。
  - 状态：active；Vok 1000 轮与 5 分钟曲线已形成，但参照物热样本及正式 Present 证据不足，现阶段不提出最终门槛。

### 剪贴板

- [ ] R4：所有系统剪贴板读写由单一 STA 服务负责；`WM_CLIPBOARDUPDATE` 只负责唤醒，以 `GetClipboardSequenceNumber` 提交水位并隔离自写回流。
  - 范围：剪贴板监听、捕获、回写和粘贴。
  - 证据：https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener 与 https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber。M2.0 已实现独立 STA/message-only listener HWND、只比较相等性的 sequence 协调器、一次 `OpenClipboard` 内多格式快照和实际忙占用后的有界退避；M2.1 将 write/verify 作为同一 STA 的可认领命令，marker 绑定 instance/operation/semantic hash，只有匹配当前 receipt 与 sequence 才抑制自写。M2.2 继续在同一 STA 写 `CF_HDROP`、精确 COPY/MOVE effect 和实际意图 hash marker；部分多格式写入失败会在仍持有剪贴板时清除半份内容，已转移句柄不被重复释放。独立 Window Station 探针真实覆盖 Unicode MOVE、ANSI COPY、Unknown、畸形拒绝、虚拟文件 Unsupported、文件自写抑制及外部覆盖失效。
  - 最后验证：2026-08-28。
  - 状态：active；文本与文件 COPY/MOVE 平台服务均已接入组合根并闭环 receipt，且已通过用户物理验收；图片多表示仍属于 M2.3。

- [ ] R20：`Preferred DropEffect` 是 `DROPEFFECT` 位集合，读取方必须掩码检查 COPY/MOVE，不能与 1/2 做精确相等比较。仅 COPY 操作位可归一为 Copy，仅 MOVE 操作位可归一为 Move；COPY 与 MOVE 同现、没有两者但值非零或载荷畸形必须拒绝，绝不能把模糊值猜成 Move。
  - 范围：Explorer 文件/目录剪贴板捕获、文件语义哈希和 MOVE 安全回放。
  - 证据：2026-08-28 真实 Explorer Ctrl+C 快照的有效 `CF_HDROP` 伴随 `Preferred DropEffect=5 (COPY | LINK)`；旧精确白名单因此拒绝整个文件快照，真实库保持 `clipboard_files=0`。Microsoft DROPEFFECT 文档要求掩码比较。修复后 effect 5 定向回归、COPY/MOVE/附加位/歧义矩阵、304/304 全套测试和隔离探针通过，交互同形语料在真实库生成 FileList/文件行。
  - 最后验证：2026-08-28。
  - 状态：active；根因修复已部署，2026-08-28 用户复测 Explorer 物理复制/剪切、文件卡与目标粘贴符合预期。

- [ ] R21：`CF_BITMAP` 的剪贴板句柄是 HBITMAP，不得调用 `GlobalSize`/`GlobalLock`，也不得在 `CloseClipboard` 后继续借用。只发布 `CF_BITMAP` 的来源应优先在同一次 `OpenClipboard` 内复制 Windows 合成的 packed `CF_DIBV5`/`CF_DIB`；若没有可复制表示，则明确标为不支持，不能延长 GDI 句柄生命周期。
  - 范围：剪贴板图片目录、捕获载荷所有权与后续图片 normalizer。
  - 证据：2026-08-28 独立 Window Station producer 通过真实 GDI `CreateBitmap` 只发布 `CF_BITMAP`；平台快照先将 `CF_BITMAP` 编目为 MetadataOnly，随后捕获 Windows 合成的 `CF_DIB` 68 bytes 与 `CF_DIBV5` 152 bytes。2026-08-29 M2.3.1 产品 reader 已只复制合成的 DIBV5；M2.3.2 隔离探针进一步确认该 152-byte V5 在 header 内已有 masks 后仍重复附加 12-byte RGB mask block，严格 parser 只在重复块与 header 三个 masks 精确一致时接受并按无 Alpha 解码，任意同长度尾随仍拒绝。`reportVersion=4, success=true` 同时验证 PNG/DIBV5 像素等价与纯 Bitmap 系统合成解码。Microsoft 标准剪贴板格式契约将 `CF_BITMAP` 定义为 HBITMAP，并说明系统可在 `CF_DIB`、`CF_DIBV5` 与 `CF_BITMAP` 间按需转换。
  - 最后验证：2026-09-01。
  - 状态：active；产品捕获所有权、严格 decoder、内容寻址资产、schema 5 与 replay 已通过单一图片功能所有者接入组合根；隔离 report v6 再次通过纯 Bitmap 合成 DIB、4K LeasedBlob DIBV5 与 PNG 同像素身份，用户已确认单屏真实来源×目标矩阵通过。

- [ ] R22：不能把 `CF_HDROP` 的存在等同于用户复制文件；同一目录存在注册 `PNG`、`CF_DIBV5`、`CF_DIB` 或 `CF_BITMAP` 等像素表示时必须先按 Image 候选处理，随附 HDROP/HTML/URL 不得抢占类型，也不得在图片损坏后跨类降级。只有没有图片表示的有效 HDROP 才进入 FileList 路由；产品实现必须保留已通过的 Explorer 图片文件正反门禁。
  - 范围：剪贴板 Image/FileList/Text 路由、候选裁剪和畸形载荷降级。
  - 证据：2026-08-28 非敏感真实语料中，Snipping Tool 区域截图、Print Screen 和 QQ 图片三种明确图片动作都同时发布 `CF_HDROP` 与图片像素格式；旧“HDROP 绝对优先”会把三者稳定误归为文件。Office 同次 PNG 865×541 与 DIB 554×346 还证明多表示不能默认等像素。2026-08-29 产品 reader/router 定向测试验证图片目录使 HDROP/Text 保持 NotRequested，只有 PNG 一份 Captured；畸形图片不跨类降级，纯 Explorer 文件既有回归保持通过。
  - 最后验证：2026-08-29。
  - 状态：active；Image/FileList/Text 精确路由、合法单候选规范化和 asset/schema 5 提交均已接入产品 Controller；连续图片失败不会终止后续监听，Explorer 图片文件反例仍由全量回归覆盖。

- [ ] R23：图片缩略图不得按分页条目或空 `Image` 自身尺寸预加载。只有 Recycling 容器已实现、展示槽与真实 ScrollViewer viewport 相交且工作台可见时才能请求；离开视口、Unloaded、CleanUp、DataContext/entry revision 变化、删除、隐藏或退出必须先撤销当前绑定身份，再取消请求并清空容器 `Source`。迟到结果只有 entry id、thumbnail hash、entry/asset revision 与当前容器数据项全部匹配才可落地。ViewModel 不持有原图或 thumbnail `ImageSource`；UI cache 只按 asset hash 保存已 `OnLoad`/`Freeze()` 的缩略图，并按解码字节硬限制 32 MiB。
  - 范围：M2.3.5 图片卡、WPF Recycling、缩略图读取/缓存、隐藏与退出生命周期。
  - 证据：2026-08-31 真实 WPF 测试复现无 Source 的 `Image` 即使声明 Width/Height，初次布局 `ActualWidth/Height` 仍为 0；若以其自身尺寸作为加载门禁，会形成“有图才有尺寸、有尺寸才读图”的永久空卡。改用固定展示槽的已布局矩形后，80 条 Image 列表只加载视口容器；滚动回收前一条绑定并故意迟到完成时不串图，隐藏后所有已实现 `Source` 清空。32 MiB LRU 按 `width × height × 4` 淘汰，损坏 decode 不入缓存；定向生命周期测试连续五轮通过。
  - 最后验证：2026-09-01。
  - 状态：active；图片卡、缓存与单一资产 coordinator 已接入产品组合根，Release 408/408、浅/深当前 DPI 渲染、真实重启卡片/缩略图、20 轮大图资产压力及单屏来源×目标门禁通过，M2.3 完成；跨屏/多屏按用户决定后置且不得冒充支持，见 `decisions.md §23`。

- [ ] R24：Image replay 的仓储查询只能返回 semantic hash、主资产引用与尺寸/Alpha/DPI descriptor；物化阶段必须以有生命周期的只读 lease 重新校验 Ready/revision、SHA-256、严格 PNG/WIC 和 `Vok.Image.Pixel.v1`，不得把路径或完整原图 `byte[]` 交给 App/VM。只有内容/像素完整性失败可按 `(hash, revision)` quarantine；取消、暂存目录不可用、DIB 生成或剪贴板写入失败不得污染 Ready 资产。writer 只在单一 Clipboard STA 一次 `EmptyClipboard` 后按 PNG→CF_DIBV5→marker 分块写入；成功转移的 HGLOBAL 归系统，失败只释放未转移句柄并清除半份内容。Controller 必须在物化成功后才隐藏，并在所有终态释放 materialization，继续复用 receipt/generation/focus 门禁。
  - 范围：M2.3.6 primary replay、DIBV5、WindowsClipboardWriter、ClipboardPasteController、取消与退出清理。
  - 证据：2026-08-31 隔离测试验证 semantic mismatch 会 quarantine，而预取消、过期 revision 和已关闭 replay staging store 均不改变 Ready primary；生成的 124-byte V5 header、bottom-up BGRA8、RGBA masks、sRGB、DPI 与严格 DIB parser 一致。writer 的 marker 失败、流长度不符和分块中取消均清除已转移格式、只显式释放未转移句柄。2026-09-01 新增 `ERROR_DISK_FULL` 第二资产提交故障，证明不发布半条 Image 元数据且结果/job 所有权归零；有效 embedded ICC PNG 成功转换到 sRGB 并从 canonical 资产剥离 profile。独立 Window Station `reportVersion=6, success=true` 继续验证 PNG+DIBV5+marker receipt、自写抑制和外部覆盖失效；用户确认 Paint、Chrome、Office、聊天软件及其他单屏来源×目标矩阵通过，收尾真实库 10 条 Image/20 个双角色引用/19 个 Ready 资产与文件 SHA 全部一致。
  - 最后验证：2026-09-01。
  - 状态：active；M2.3.6 replay 切片已通过 M2.3.7 单一图片功能所有者接入产品，Paint 当前/跨重启、Chrome、Office、聊天软件及其他单屏来源×目标 replay 均已真机通过，M2.3 完成。

- [ ] R25：图片直接落盘只允许正向识别的 Explorer 当前文件系统 View 或当前用户 Desktop。解析必须绑定已捕获顶层 HWND/进程/线程与焦点活动 View，拒绝虚拟、歧义和失效目标，并在写入前于共享 Shell STA 再次确认同一目标；Automation、标题、地址栏和进程名不能授权。工作区 generation 声明只能在最终复核后、`IFileOperation` 副作用前原子消费；未消费声明被隐藏、换代、退出或取消后不得落盘。目录分支不得修改剪贴板或注入输入；创建成功后隐藏失败、回调结果不明或取消都不得回退/重试，必须按“已创建”或“已提交待确认”报告。App/VM 不能取得资产路径或完整原图，目标文件须经单项回调、目录归属、SHA/长度和碰撞改名确认。
  - 范围：M2.3.8 图片目标分流、Workspace generation、Shell/COM STA、IFileOperation、资产 export lease 与控制器终态。
  - 证据：2026-08-31 真机 Gate 0 通过 Explorer、Desktop、同窗双标签、Terminal 与“此电脑”虚拟目录；双标签证明同一顶层 HWND 可有多个 Shell 候选，虚拟目录证明 `explorer.exe` 不能代表文件系统路径。IFileOperation report v2 通过单项 `PostCopyItem/psiNewlyCreated`、Unicode、连续碰撞、预取消、字节与清理。产品自动测试覆盖零 clipboard/input、非目标回退、旧代/未授权零副作用、双击一次、创建后隐藏失败与授权后结果不明；真实临时目录测试使用产品 COM ABI 验证碰撞和每项结果。
  - 最后验证：2026-08-31。
  - 状态：active；Gate 0、产品接线、0 警告 Release、406/406 自动测试及 Explorer/Desktop/碰撞/虚拟目录/Paint 新版物理交互全部通过。

### 存储与规模

- [ ] R5：不得在 UI Dispatcher 上执行 Microsoft.Data.Sqlite；数据库写入由显式执行器串行化并使用 WAL。数据库升级必须同时核验版本号与真实表结构，在单事务内迁移并让未知结构原样回滚；`CREATE TABLE IF NOT EXISTS` 只能建新表，不能冒充已有表迁移。大集合必须同时实施 SQL 分页和 WPF 容器虚拟化，不能把 UI 虚拟化当作数据虚拟化。
  - 范围：SQLite、启动台、剪贴板历史、图片与后续大列表。
  - 证据：https://learn.microsoft.com/dotnet/standard/data/sqlite/async 说明 Async 方法实际同步；https://learn.microsoft.com/dotnet/desktop/wpf/advanced/optimizing-performance-controls 说明 WPF 没有内建数据虚拟化。2026-08-28 真机旧库因已有 `launcher_items` 使 `CREATE TABLE IF NOT EXISTS` 静默跳过，但代码仍写入版本并查询新列，复现 `no such column: target_kind`；schema 2 改为列集合识别和单事务迁移。M2.1–2 形成 schema 3/4。M2.3.4 以同一事务增加五个 nullable 图片列、`content_assets`、`clipboard_asset_refs`、CASCADE/RESTRICT 外键及引用/状态索引；fresh、schema 3、schema 4 与完整 schema 5 均按真实列/FK/index 识别，半迁移列和损坏 asset FK 原样拒绝。迁移定向验证 Text/FileList replay 均保留，图片分页只 LEFT JOIN 两个轻量引用，不读取 PNG。
  - 最后验证：2026-08-31。
  - 状态：active；设置、启动台与剪贴板共享串行所有权，schema 1→2→3→4→5、失败回滚、SQL 分页、WPF 虚拟化、去重/排序和取消一致性均有自动测试；真实用户库已迁移到 schema 5，并在 OS 进程终止后离线读取、新进程重开及前后状态摘要一致性中通过持久化核验。

### 发布与互操作

- [ ] R6：Native AOT、裁剪、单文件、ReadyToRun、MSIX 和新 CPU 架构均不得仅凭理论收益进入默认发布；必须在 WPF、XAML、COM/OLE、PInvoke、SQLite 和真实交互路径上单独验证。
  - 范围：Release 发布和安装包。
  - 证据：https://learn.microsoft.com/dotnet/core/deploying/native-aot/ 列出裁剪、COM、动态加载和单文件限制；WPF 官方 roadmap 仍将 Native AOT 视为现代化方向而非无条件基线。
  - 最后验证：2026-08-23。
  - 状态：active；M0 的 win-x64 自包含、非裁剪、非 AOT 发布已通过。

### 测试基础设施

- [x] R7：xUnit v3 项目必须同时引用 `xunit.runner.visualstudio`，并通过 `Vok.runsettings` 设置 `TreatNoTestsAsError=true`；不得把退出码为 0 但发现 0 项测试视为通过。
  - 范围：`dotnet test`、CI 与本地测试发现。
  - 证据：M0 首次只引用 `xunit.v3` 时命令退出 0 但未发现测试；加入适配器和零测试失败门禁后，13 个单测与 3 个集成测试被发现并通过。
  - 最后验证：2026-08-23。
  - 状态：active。

## 死路

- M0 窗口 `ShowInTaskbar=false` 且启动隐藏时，`Process.MainWindowHandle` 可持续为 0，即使进程已有真实 WPF 顶层 HWND；窗口烟雾测试须按 PID 枚举顶层窗口并核验可见性/矩形，不能依赖该属性。
- 后台测试进程主动创建窗口再调用 `SetForegroundWindow` 会被 Windows 前台锁拒绝；不能用延时、`AttachThreadInput` 或强制置顶把该前置条件伪装成成功。自动探针应使用真实当前前台快照，最终焦点门禁仍需真实用户输入。
- `SetWindowPos(...SWP_NOZORDER|SWP_NOACTIVATE)` 后调用 WPF `Show()+Activate()` 不能构成工作台顶层契约；真实用户热键已复现 `Activate()` 失败后全屏窗口留在前台窗口之后。必须分别验证 Z 序、前台 HWND 和键盘焦点。
- 普通不透明 WPF 宿主中，获得过焦点的 `TextBox` 在反复 Show/Hide 时于本机复现每轮增加一个 GDI 对象；清焦点、关闭 Topmost/遥测/首帧或跳过重复尺寸均不能消除。尚未识别具体 GDI 对象或 WPF/TSF/IME/插入符所有权与回收条件，因此这是已隔离相关机制，不是正式编辑器根因修复。M0 不用编辑器控件冒充搜索，也不得用强制 GC 掩盖；正式 Search 必须重新做生命周期门禁。见 `decisions.md §3`。
- 外部长期探针不得在热循环高频枚举全部顶层窗口/进程树；本机 1000 轮复现约 5 秒观察停顿、伪失败和资源平台污染。启动时固定 PID/HWND，热循环只读窄 Win32/诊断属性，并将该探针死路与宿主结果分开。
- 合成显隐循环中“最终 foreground 等于捕获顶层且目标线程 focus HWND 等于探针起始值”不能证明真实外部文本框的逻辑焦点已返还；2026-08-24 用户物理测试已复现两种隐藏都无法继续在原文本框输入。必须保留捕获 child 身份/关系和完整 Restore 因果链，再按 A/B/C/D 分类。
- 仅恢复顶层 foreground 不会可靠恢复原子文本框；本机 B 类实测为顶层已恢复而目标线程 `hwndFocus=0`。不得用子 HWND `SetFocus` 掩盖 A/C/D；只有 `decisions.md §4` 的同代、身份与父子关系校验全部通过时，才允许一次性窄回退并以最终观察结果判定。
- 物理诊断记录不得只在全部步骤成功后才创建结果；第一版记录器在挂接失败、权限不一致、超时或第二代未完成时不写任何文件，用户已真实复现“操作后无 JSON”。结果必须启动即落盘、每代提交并在失败时保留阶段与错误；这只改进证据可观测性，不得参与恢复行为。
- 程序化恢复到 CMD、PowerShell 等终端宿主时，用户真机确认偶发转圈光标，其他应用正常，焦点/输入不受影响且鼠标移动后立即恢复；用户报告 Windows Terminal 上游存在同类现象。将其作为低优先级终端宿主兼容边界，不得以 `SetCursor`、`SetCursorPos`、伪造鼠标消息或延时刷新掩盖。仅当非终端复现、移动后仍不恢复或影响输入时重开调查。
- `WM_HOTKEY` 只有按下通知，`MOD_NOREPEAT` 也不提供释放；不能用第二个热键消息、自动重复间隔或固定 250 ms 等待推断松开。短长按必须另行观察真实键态，并遵守 R16 的有界生命周期。

## 症状索引

- 全屏窗口固定内存或 GPU surface 过高 → 检查 R1、R6。
- 热键迟开、闪关、重复收尾或退出卡住 → 检查 R2、R3。
- 点击任务栏或切换应用后 Vok 仍覆盖工作区 → 检查 R11；不要用控件 LostFocus、固定延时或恢复旧目标处理。
- 粘贴到了错误窗口或静默失败 → 检查 R3、R4。
- 剪贴板重复、自写回流或漏记录 → 检查 R4。
- Explorer 文件只出现路径文本、文件卡完全不生成或 COPY 全部漏记 → 检查 R20；先读取真实 `Preferred DropEffect`，不要恢复精确 0/1/2 比较。
- 纯 Bitmap 来源漏图、GDI 句柄越界或把 `CF_BITMAP` 当普通字节读取 → 检查 R21；优先观察同次快照中的系统合成 DIB/DIBV5。
- 截图或聊天图片被记成临时文件、图片格式存在却走 FileList → 检查 R22；不要仅按 `CF_HDROP` 是否存在分类。
- 图片卡一直空白、滚动后串图、隐藏后缩略图仍驻留或图片越多内存越高 → 检查 R23；不要用空 `Image.ActualWidth` 判可见、分页预加载或只凭异步完成顺序绑定。
- 图片原图无法回放、资产无故被隔离、粘贴失败后剩半份格式或句柄/临时文件不回落 → 检查 R24；先区分内容完整性、环境故障和 HGLOBAL 是否已转移。
- 图片点入错误 Explorer 标签、虚拟目录误落盘、双击生成两份、创建成功却提示重试或目录分支改了剪贴板 → 检查 R25；不要用标题/Automation/进程名授权，也不要在最终 Shell 复核前消费代次声明。
- 条目增加导致 UI/内存线性增长 → 检查 R5。
- Debug 正常但发布版互操作失败 → 检查 R6。
- 测试命令成功但显示 0 项测试 → 检查 R7。
- 烟雾测试找不到窗口但窗口实际存在 → 不依赖 `Process.MainWindowHandle`，按 PID 枚举 HWND。
- 面板全屏但被其他窗口遮挡 → 检查 R3；不要继续叠加 `Activate()` 或延时，重做 Z 序与前台的独立契约。
- 面板状态显示“输入就绪”但键盘仍落在外部窗口 → 检查 R8；不能用 WPF 调用返回值代替 Win32/WPF 双重观察。
- 单点内存较低或窗口很快变为 Visible 就宣布性能通过 → 检查 R9；可见 HWND 不等于稳定帧或输入就绪。
- 呼出后先出现纯背景，文字/按钮/边框慢半拍 → 检查 R13；不要调整字体、透明度或增加固定延时，先分离输入就绪、WPF 内容提交与 DWM Present。
- 录制当前快捷键时工作台直接隐藏、改键失败后无法呼出或重启恢复旧键 → 检查 R14；不要在 UI 线程直接重注册、先写库或让录制期旧注册继续生效。
- 长按松开不隐藏、短按闪关或旧松开误关新界面 → 检查 R16；不要按当前可见性处理无代次 Released，也不要恢复常驻轮询/Hook。
- Fluent 控件在暗色主题仍呈浅色、像贴图或与同类控件不一致 → 检查 R15；先找无 `BasedOn` 的局部 Style，不要直接硬编码暗色 Brush。
- 启动台底部出现半行、余白或滚动被强制吸附 → 检查 R17；不要缩短视口、按卡片逐行取整或修改用户滚动偏移。
- 拖文件呼出后无法关闭、界面卡顿或蓝框不消失，以及重复松手误关新界面 → 检查 R11、R19；不要在 DragOver 读取外部数据，也不要用坐标、鼠标轮询、固定延时或透明度常量补终态。
- 内部拖动排序后顺序回弹/错乱、Esc 不回滚或单击偶发启动两次 → 检查 R18、R19；不要逐格写库，也不要让动画完成与 watchdog 各自产生副作用。
- 移除卡片影响源文件或隐藏失败仍启动 → 检查 R18；不要用文件移动实现，也不得绕过隐藏后 Shell 事务。
- 启动台加载或拖入报 `no such column` → 检查 R5 与 `launcher_items` 的真实列结构；不要删用户库、只改 `user_version` 或继续叠加 `CREATE TABLE IF NOT EXISTS`。
- 每次显隐 GDI 近似 `+1` → 检查获得过焦点的 WPF 编辑器生命周期与 `decisions.md §3`；先最小复现，不要强制 GC。
- 长测外部值周期性接近探针超时而内部值仍正常 → 检查是否在热循环 `EnumWindows`/CIM；固定 HWND/PID 后复测，不能把观察者停顿归因于宿主。
