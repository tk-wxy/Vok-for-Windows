# 决策

> 证据可追溯时，在记录范围内具备高可信度。只追加已采用或经用户确认且需要独立记录根因的决策，不记录头脑风暴。被取代的决策应保留并指向替代项。

## 索引

- §1 Vok 分层原生桌面架构与 .NET 10 基线 — 采用不透明 WPF 主窗、明确资源所有权和 SQLite/资产分离。

## 详情

### §1 Vok 分层原生桌面架构与 .NET 10 基线

- **最终方案**：以 C#、.NET 10 LTS 和 WPF 为主技术路线。全屏主窗使用不透明普通 HWND，WPF 原生 Fluent 为默认视觉，WPF-UI 只允许隔离且可替换地使用。解决方案拆为 `Vok.App`、平台无关的 `Vok.Core`、`Vok.Infrastructure.Windows`、单元测试和 Windows 集成测试。单进程内由 WPF UI STA、热键消息线程、剪贴板 STA、Shell/COM STA、SQLite 执行器和有界图片 worker 显式拥有资源。数据采用 SQLite 可分页元数据与 `%LOCALAPPDATA%\Vok` 外置内容寻址资产。默认发布为自包含、非裁剪、非 Native AOT，其他优化按真实门禁启用。详细契约见 `docs/ARCHITECTURE.md`。
- **范围**：首期启动台、剪贴板、宿主显隐/焦点、必要设置与数据保护；同时约束后续中转站和搜索接入方式。
- **证据与根因**：Vok 的核心风险来自前台窗口限制、剪贴板/OLE/COM、全屏合成、长时间常驻和大数据量，而不是普通页面开发。`D:\dev\workbench-app` 的 Tauri/React/WebView2 实现被用户指定为性能反例且禁止复用代码；`D:\dev\vok(old)` 的实际 .NET 8/WPF 配置证明原生路线可行，但其分层全屏窗口、隐式通信、混乱模块边界和资源曲线也说明“换成 WPF”不足以保证轻量和可维护。2026-08-23 的官方资料显示 .NET 8 将于 2026-11-10 结束支持，.NET 10 LTS 支持至 2028-11-14；WPF .NET 10 已提供性能与 Fluent 改进。Microsoft.Data.Sqlite 的 Async 实际同步，WPF UI 虚拟化不等于数据虚拟化，`SetForegroundWindow` 也可能被系统拒绝，因此这些能力边界必须直接进入架构。用户于 2026-08-23 完全同意该分析与新方针。
- **否决方案**：继续以 .NET 8 开始绿地项目（支持期过短）；WebView2/Tauri/Electron（重复已知多进程、合成与时序代价）；仅因微软通用推荐而直接改用 WinUI 3（尚未证明 Vok 的隐藏、焦点、剪贴板和 OLE 高风险路径更优）；C++/Win32 全量 UI（维护和交互开发风险过高）；全屏 `AllowsTransparency=true`（不必要的分层合成成本）；单项目上帝组件、全局 Messenger、散落 `Task.Run`、JSON 全量存储或 EF Core（弱化所有权与规模控制）；提前使用 Native AOT/裁剪/单文件（WPF、XAML、COM/OLE 和发布兼容风险未验证）。
- **决定 / 最后验证**：2026-08-23 / 2026-08-23。
- **状态**：active；文档方针已落地，产品代码和 M0 原型尚未创建。
