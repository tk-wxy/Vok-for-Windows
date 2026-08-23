# 项目清单

> 已于 2026-08-23 经用户确认，作为高可信项目锚点。目标架构见 `docs/ARCHITECTURE.md`，根本性选型见 `decisions.md §1`。

## 使命

- **名称**：Vok
- **定位**：面向 Windows 的全屏桌面工作台，通过全局快捷键快速呼出并在完成操作后隐藏，作为用户当前工作环境之外的“第二桌面”。
- **目标**：
  - 首期可靠交付启动台、剪贴板、Vok 呼出/隐藏与焦点恢复流程，以及必要的基础设置和数据保护能力；
  - 减少用户在开始菜单、资源管理器、剪贴板工具和多个窗口之间反复切换的成本；
  - 保证长时间后台运行、多次显隐、拖放、多显示器和高 DPI 场景下的正确性与稳定性能；
  - 为中转站、界面搜索和增强搜索保留清晰边界，作为后续正式功能持续演进。
- **非目标**：
  - 首期不正式实现中转站和增强搜索，也不以占位界面、无效开关或虚假能力冒充完成；
  - 不擅自移动、修改或删除用户原文件，不以删除既定功能解决性能问题；
  - 未经用户确认，不扩展需求范围或改变已确定的功能范围与界面结构；
  - 不复用 `D:\dev\workbench-app` 的代码实现；该项目仅作为界面、功能与样式参考和性能反例。

## 锁定技术栈

- 平台：Windows 原生桌面应用；首发 `win-x64`，其他架构须通过独立兼容门禁；
- 语言与运行时：C#、.NET 10 LTS（`net10.0-windows`）；
- UI：WPF 普通不透明 HWND、.NET 10 原生 Fluent 主题；WPF-UI 仅可作为隔离且可替换的局部视觉依赖；
- 应用架构：MVVM、CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection；默认不引入 Generic Host，后台资源由组合根显式拥有和释放；
- 分层：`Vok.Core` 保持平台无关，`Vok.Infrastructure.Windows` 隔离 Win32/COM/SQLite，`Vok.App` 负责 WPF 组合与表现；
- 持久化：Microsoft.Data.Sqlite 元数据 + `%LOCALAPPDATA%\Vok` 外置内容寻址资产；
- 系统集成：优先使用 Microsoft.Windows.CsWin32；仅对生成器未覆盖或需要审计的窄接口集中使用 `LibraryImport`，隔离 Win32、Shell 和 OLE/COM；
- 测试：xUnit、Microsoft.NET.Test.Sdk，分为纯单元测试与真实 Windows 集成测试；
- 发布：默认自包含、非裁剪、非 Native AOT；ReadyToRun、单文件、MSIX 和 Arm64 只能按实测独立启用。

## 架构不变量

变更不变量必须先在 `decisions.md` 中记录根因并取得用户确认。

- 使用 .NET 10/WPF 原生路线，不采用 WebView2/Tauri/Electron 作为 UI 运行时；性能、后台资源占用和原生交互是架构约束。
- 全屏主窗口必须是 `AllowsTransparency=false` 的不透明普通 HWND；若未来需要透明效果，只能将其限制在按需创建的小型附属窗口，不能让全屏主 surface 分层合成。
- 单进程内实行明确资源所有权：WPF UI STA、热键消息线程、剪贴板 STA、Shell/COM STA、SQLite 执行器和有界图片 worker 只访问各自资源，通过不可变消息或窄接口交接。
- `Vok.Core` 不引用 WPF、Win32、COM 或 SQLite；有返回值、取消、失败或资源所有权的交互走显式接口，Messenger 只承载无所有权广播。
- 每次呼出都建立带代次标识的工作台会话并捕获外部目标窗口上下文；显隐、启动、拖放和粘贴均用可取消、幂等状态机覆盖成功、失败、取消和重复触发，不以固定延时充当同步屏障。
- 焦点恢复必须视为可能失败的外部操作：先隐藏、请求恢复、观察真实前台结果，再注入输入；权限不匹配或恢复失败时明确降级，不静默丢失内容。
- 用户文件只按功能需要被引用或打开。SQLite 只保存可分页元数据；图片和大对象外置，缓存按字节设限，数据损坏必须可隔离并降级启动。
- 启动台、剪贴板、设置、中转站和搜索保持模块边界；首期只创建真实实现，不创建后续功能的空页面、空模块或虚假开关。
- Native AOT、裁剪、单文件、全屏透明窗口、第二进程和全盘常驻内存索引均不是默认基线；只有可复现证据证明收益大于风险时才能引入。

## 高风险区域

- 热键消息线程、WPF Dispatcher、窗口显隐、输入就绪和代次状态机；
- 目标窗口快照、Windows 前台限制、权限边界、焦点恢复与输入注入；
- 剪贴板 STA、格式解析、锁冲突、sequence 水位、自写回流、敏感内容与粘贴；
- Shell/COM、应用启动、图标/快捷方式解析和 OLE 拖入拖出；
- SQLite 串行执行、WAL、迁移、顺序一致性、资产引用和损坏恢复；
- 数据分页、WPF 容器虚拟化、图片解码、有界缓存以及长期运行资源曲线；
- 多显示器、Per-Monitor V2、高 DPI、物理像素/DIP 边界和工作区几何；
- CsWin32/PInvoke ABI、发布模式、架构差异以及 ReadyToRun/打包回归。

## 项目结构

当前为绿地项目，以下结构均为规划状态，尚未创建产品代码：

```text
src/
├── Vok.sln
├── Vok.App/                    # WPF 可执行程序、组合根、Views 与模块 ViewModel
├── Vok.Core/                   # 纯业务、应用用例、契约与可测试状态机
│   ├── Workspace/
│   ├── Launcher/
│   ├── Clipboard/
│   └── Settings/
├── Vok.Infrastructure.Windows/ # Win32、COM、SQLite、文件、图片和平台适配器
├── Vok.Tests/                  # Core 单元、契约和确定性状态机测试
└── Vok.IntegrationTests/       # 真实 Windows、SQLite 与发布集成测试
docs/
└── ARCHITECTURE.md             # 目标架构、数据流、门禁和实施阶段
.apc/                           # 项目知识与开发协议
```

中转站与搜索在正式进入里程碑前只保留文档边界，不创建空代码目录或占位 UI。

## 常用命令

产品工程尚未创建；安装 .NET 10 SDK 并创建 `src/Vok.sln` 后生效：

```powershell
dotnet restore src/Vok.sln
dotnet build src/Vok.sln -c Release
dotnet test src/Vok.sln -c Release
dotnet run --project src/Vok.App/Vok.App.csproj
dotnet publish src/Vok.App/Vok.App.csproj -c Release -r win-x64 --self-contained true
```

## 编辑规则

仅在项目发生根本性变化时修改本文件，并先记录相应决策。根目录的 agent 入口文件保持通用路由。
