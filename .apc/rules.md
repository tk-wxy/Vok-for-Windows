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

## 规则

### 窗口与渲染

- [ ] R1：全屏 `MainWindow` 使用普通不透明 HWND 和 `AllowsTransparency=false`；透明视觉仅允许隔离在按需小型窗口中。
  - 范围：WPF 主宿主、全屏/高 DPI 渲染。
  - 证据：WPF 官方说明渲染成本受像素量影响；姊妹项目全屏 layered surface 已出现显著固定内存成本；见 `decisions.md §1`。
  - 最后验证：2026-08-23。
  - 状态：active；新项目实测待 M0。

### 生命周期与并发

- [ ] R2：WPF UI、热键、剪贴板、Shell/COM、SQLite 和图片任务各有明确所有者；跨边界只投递不可变数据或调用窄接口，禁止同步等待 UI、散落 `Task.Run` 和不明固定延时。
  - 范围：进程内全部后台服务和系统事件。
  - 证据：Vok 产品要求覆盖失败、取消和重复触发；参考项目的轮询、跨线程等待和全局复位清单导致时序耦合；见 `decisions.md §1`。
  - 最后验证：2026-08-23。
  - 状态：active；实现契约待 M0。

### 焦点与注入

- [ ] R3：隐藏、启动和粘贴不得把 `SetForegroundWindow` 返回前台视为必然成功；必须观察真实前台窗口，失败或 UIPI 权限不匹配时明确降级。
  - 范围：目标窗口恢复、应用启动、剪贴板粘贴。
  - 证据：https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow 明确说明系统限制前台切换且即使满足条件也可能拒绝。
  - 最后验证：2026-08-23。
  - 状态：active。

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
  - 状态：active。

## 死路

暂无经当前项目实证的死路。参考项目失败仅作为选型证据，不冒充当前代码复现。

## 症状索引

- 全屏窗口固定内存或 GPU surface 过高 → 检查 R1、R6。
- 热键迟开、闪关、重复收尾或退出卡住 → 检查 R2、R3。
- 粘贴到了错误窗口或静默失败 → 检查 R3、R4。
- 剪贴板重复、自写回流或漏记录 → 检查 R4。
- 条目增加导致 UI/内存线性增长 → 检查 R5。
- Debug 正常但发布版互操作失败 → 检查 R6。
