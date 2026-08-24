# 范围规则与死路

> 只有证据、范围、环境和状态仍匹配时才具备高可信度。仅记录已复现或有权威依据的约束，不记录猜测。

失效条目保留删除线，并注明原因和日期。

## 索引

| ID | 摘要 | 范围 | 高风险 |
|---|---|---|---|
| R1 | 全屏主窗保持不透明、非分层 | 窗口与渲染 | 是 |
| R2 | 系统资源由专属线程拥有，禁止固定延时同步 | 生命周期与并发 | 是 |
| R3 | 前台恢复必须观察结果并支持降级 | 焦点与注入 | 是 |
| R4 | 剪贴板由单一 STA 服务拥有，以 sequence 判定变化 | 剪贴板 | 是 |
| R5 | SQLite 同步执行出 UI；数据分页与 UI 虚拟化并行实施 | 存储与规模 | 是 |
| R6 | Native AOT、裁剪和发布优化必须先通过真实原型 | 发布与互操作 | 是 |
| R7 | xUnit v3 必须显式安装适配器并让零测试失败 | 测试基础设施 | 否 |
| R8 | M0 四项呈现结果独立观察，诊断值不能冒充输入就绪 | 宿主验收 | 是 |
| R9 | 性能门槛先做三方同机对照，再由用户确认 | 性能验收 | 是 |

## 规则

### 窗口与渲染

- [x] R1：全屏 `MainWindow` 使用普通不透明 HWND 和 `AllowsTransparency=false`；透明视觉仅允许隔离在按需小型窗口中。
  - 范围：WPF 主宿主、全屏/高 DPI 渲染。
  - 证据：WPF 官方说明渲染成本受像素量影响；姊妹项目全屏 layered surface 已出现显著固定内存成本；见 `decisions.md §1`。
  - 最后验证：2026-08-23。
  - 状态：active；M0 烟雾探针已确认目标监视器全屏矩形。

### 生命周期与并发

- [ ] R2：WPF UI、热键、剪贴板、Shell/COM、SQLite 和图片任务各有明确所有者；跨边界只投递不可变数据或调用窄接口，禁止同步等待 UI、散落 `Task.Run` 和不明固定延时。
  - 范围：进程内全部后台服务和系统事件。
  - 证据：Vok 产品要求覆盖失败、取消和重复触发；参考项目的轮询、跨线程等待和全局复位清单导致时序耦合；见 `decisions.md §1`。
  - 最后验证：2026-08-23。
  - 状态：active；M0 已实现热键消息线程、UI Dispatcher 边界、代次检查和先占有后副作用，未来服务仍须逐项落实。

### 焦点与注入

- [ ] R3：隐藏、启动和粘贴不得把 `SetForegroundWindow` 返回前台视为必然成功；必须观察真实前台窗口，失败或 UIPI 权限不匹配时明确降级。
  - 范围：目标窗口恢复、应用启动、剪贴板粘贴。
  - 证据：https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow 明确说明系统限制前台切换且即使满足条件也可能拒绝；2026-08-24 逐代实测将失败归为 B：顶层已恢复，目标线程子焦点为 0。按 `decisions.md §4` 做窄回退后，用户确认快捷键与 Esc 隐藏均返回原文本框。
  - 最后验证：2026-08-24。
  - 状态：active；M0 隐藏恢复已通过真实物理回归。后续启动与粘贴仍必须分别验证，不能从 M0 结果外推；默认禁止连接输入队列，只有 §4 已证明的 B 类条件允许单次窄回退。

### 宿主验收

- [ ] R8：M0 的窗口可见、会话期 Topmost、真实前台和真实键盘焦点必须作为四个独立结果观察；只有真实输入控件同时通过 Win32 前台/focus HWND 与 WPF 键盘焦点验证后才能进入输入就绪状态。
  - 范围：M0 显示状态机、窗口呈现、焦点锚点、自动探针和真实键盘验收。
  - 证据：当前实现把 `Show()/Activate()/Keyboard.Focus()` 的调用结果在未确认真实 OS 状态时提交为 `Visible`；2026-08-24 用户真机观察到窗口可见但不在顶层。用户同日确认 M0 是生产级宿主内核，诊断页不得冒充搜索功能或输入就绪。
  - 最后验证：2026-08-24。
  - 状态：active；进程内 WPF STA、外部结构化同代五门禁、长期曲线及真实物理呼出/输入/两种隐藏返还均已通过，M0 宿主冻结。该结论不包含正式搜索编辑器或完整 IME 语义。

### 性能验收

- [ ] R9：不得凭主观感受或单点进程快照制定/宣布 M0 性能通过；先在同机同条件下记录 Workbench、vok(old) 与 Vok 的隐藏常驻、冷/热呼出、稳定帧、输入就绪、恢复、资源峰值/回落及重复显隐增长斜率，再提出 Vok 门槛并由用户确认。
  - 范围：M0 性能遥测、外部探针、参照物对照和最终验收。
  - 证据：Workbench 历史探针记录透明 WebView2 HWND 可见后 surface 仍晚约 60–570ms；2026-08-24 已完成三方初始对照及 Vok 固定 HWND 1000 轮/5 分钟回落。Vok 自身已有可靠分布，但两个参照物仍只有约三份热样本，不能形成 P95；正式 Present 证据也尚缺，详见 `docs/M0_HOST_KERNEL.md`。
  - 最后验证：2026-08-24。
  - 状态：active；Vok 1000 轮与 5 分钟曲线已形成，但参照物热样本及正式 Present 证据不足，现阶段不提出最终门槛。

### 剪贴板

- [ ] R4：所有系统剪贴板读写由单一 STA 服务负责；`WM_CLIPBOARDUPDATE` 只负责唤醒，以 `GetClipboardSequenceNumber` 提交水位并隔离自写回流。
  - 范围：剪贴板监听、捕获、回写和粘贴。
  - 证据：https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener 与 https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber。
  - 最后验证：2026-08-23。
  - 状态：active。

### 存储与规模

- [ ] R5：不得在 UI Dispatcher 上执行 Microsoft.Data.Sqlite；数据库写入由显式执行器串行化并使用 WAL。大集合必须同时实施 SQL 分页和 WPF 容器虚拟化，不能把 UI 虚拟化当作数据虚拟化。
  - 范围：SQLite、启动台、剪贴板历史、图片与后续大列表。
  - 证据：https://learn.microsoft.com/dotnet/standard/data/sqlite/async 说明 Async 方法实际同步；https://learn.microsoft.com/dotnet/desktop/wpf/advanced/optimizing-performance-controls 说明 WPF 没有内建数据虚拟化。
  - 最后验证：2026-08-23。
  - 状态：active。

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

## 症状索引

- 全屏窗口固定内存或 GPU surface 过高 → 检查 R1、R6。
- 热键迟开、闪关、重复收尾或退出卡住 → 检查 R2、R3。
- 粘贴到了错误窗口或静默失败 → 检查 R3、R4。
- 剪贴板重复、自写回流或漏记录 → 检查 R4。
- 条目增加导致 UI/内存线性增长 → 检查 R5。
- Debug 正常但发布版互操作失败 → 检查 R6。
- 测试命令成功但显示 0 项测试 → 检查 R7。
- 烟雾测试找不到窗口但窗口实际存在 → 不依赖 `Process.MainWindowHandle`，按 PID 枚举 HWND。
- 面板全屏但被其他窗口遮挡 → 检查 R3；不要继续叠加 `Activate()` 或延时，重做 Z 序与前台的独立契约。
- 面板状态显示“输入就绪”但键盘仍落在外部窗口 → 检查 R8；不能用 WPF 调用返回值代替 Win32/WPF 双重观察。
- 单点内存较低或窗口很快变为 Visible 就宣布性能通过 → 检查 R9；可见 HWND 不等于稳定帧或输入就绪。
- 每次显隐 GDI 近似 `+1` → 检查获得过焦点的 WPF 编辑器生命周期与 `decisions.md §3`；先最小复现，不要强制 GC。
- 长测外部值周期性接近探针超时而内部值仍正常 → 检查是否在热循环 `EnumWindows`/CIM；固定 HWND/PID 后复测，不能把观察者停顿归因于宿主。
