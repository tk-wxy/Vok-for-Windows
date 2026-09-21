# 项目清单

> 已于 2026-08-23 经用户确认，作为高可信项目锚点。目标架构见 `docs/ARCHITECTURE.md`，根本性选型见 `decisions.md §1`。

## 使命

- **名称**：Vok
- **定位**：面向 Windows 的全屏桌面工作台，通过全局快捷键快速呼出并在完成操作后隐藏，作为用户当前工作环境之外的“第二桌面”。
- **目标**：
  - 以 `D:\dev\workbench-app` 的成熟功能/交互和 `D:\dev\vok(old)` 的部分 WPF/Win32 成果作为产品形态参照，最终实现两者体现的功能形态、界面结构与核心体验，但不复用其代码实现；
  - 首期可靠交付启动台、剪贴板、Vok 呼出/隐藏与焦点恢复流程，以及必要的基础设置和数据保护能力；
  - 减少用户在开始菜单、资源管理器、剪贴板工具和多个窗口之间反复切换的成本；
  - 保证长时间后台运行、多次显隐、拖放、多显示器和高 DPI 场景下的正确性与稳定性能；
  - 为中转站、界面搜索和增强搜索保留清晰边界，作为后续正式功能持续演进。
- **非目标**：
  - 首期不正式实现中转站和增强搜索，也不以占位界面、无效开关或虚假能力冒充完成；
  - 不擅自移动、修改或删除用户原文件，不以删除既定功能解决性能问题；
  - 未经用户确认，不扩展需求范围或改变已确定的功能范围与界面结构；
  - 不复制、迁移或改写 `D:\dev\workbench-app` 与 `D:\dev\vok(old)` 的代码实现；两者只允许作为外部行为、交互流程、视觉结果和同机性能参照，旧项目当前可工作不等于其内部机制符合 Vok 架构标准。

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
- M0 是后续产品模块直接依赖的生产级宿主内核，不是一次性诊断页；窗口可见、会话期 Topmost、真实前台和真实键盘焦点是四个独立结果，必须分别观察后才能宣布输入就绪。
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

M0 生产宿主、M1 启动台和单屏范围的 M2 剪贴板已完成并冻结；项目现进入 M3 中转站开发，产品契约已锁定。“外部拖动中呼出并在 Stage 松手持久落项”的 M3.MVP1 数据内核、M3.MVP2 Stage UI/唯一入站路由和 M3.MVP3 数据/发布/真机验收均已完成并归档。活动 Explorer 拖动中反复关闭与呼出的源目录副本风险按用户要求延期为 `KNOWN_ISSUES.md` 的 KI-001，源码恢复到问题报告前的 M3.MVP3 行为。当前实际结构如下：

```text
src/
├── Vok.sln
├── Vok.App/                    # WPF 可执行程序、组合根、产品壳层与启动台/剪贴板表现
├── Vok.Core/                   # 平台契约、会话状态机与启动台/剪贴板/Stage 业务模型
├── Vok.Infrastructure.Windows/ # Win32/Shell/剪贴板 STA、SQLite、Stage 入站与外置资产实现
├── Vok.Tests/                  # Core 状态机与 Stage 选项单元测试
└── Vok.IntegrationTests/       # Windows API、SQLite/Stage、控制器副作用与真实 WPF STA 测试
docs/
├── ARCHITECTURE.md             # 目标架构、数据流、门禁和实施阶段
├── M1_LAUNCHER_PLAN.md         # 已完成的启动台实现与验收记录
├── M2_CLIPBOARD_PLAN.md        # 已归档的剪贴板实现与验收记录
├── M3_STAGE_PLAN.md            # 中转站产品契约、完整路线与分期门禁
├── M3_STAGE_FOCUSED_ROADMAP.md # 当前大容量、重复 occurrence 与单/批量拖放权威路线
├── M3_STAGE_MVP1_PLAN.md       # 已完成的 Stage 数据内核实施与验收记录
├── M3_STAGE_MVP2_PLAN.md       # 已完成的 Stage 展示与统一入站路由实施记录
├── M3_STAGE_MVP3_PLAN.md       # 已完成的入站 MVP 数据保护、发布候选与真机验收记录
└── KNOWN_ISSUES.md             # 已确认但延期处理的问题索引
tools/
├── Probe-M0.ps1                # 结构化同代五门禁烟雾探针
├── Measure-HostInteraction.ps1 # 固定 PID/HWND 时延与长期资源探针
├── Measure-M3StageScale.ps1    # Stage 50/10k/100k 隔离规模编排与报告
├── Probe-M2Clipboard.ps1        # 独立 Window Station 剪贴板与图片资源探针
├── Probe-M3StageData.ps1        # Stage 实时只读审计、在线备份与副本回放探针
├── Record-M3StageInbound.ps1    # 固定候选 PID/HWND 的物理验收记录器
└── Vok.StageScaleProbe/         # 真实 WPF + SQLite 连续窗口规模探针
.apc/                           # 项目知识与开发协议
```

中转站 M3.MVP3 入站与 M3.1 管理/当前窗口排序均已完成；普通卡面直接拖动、零集合变化 transform 预览已通过用户观感 Gate。2026-09-12 用户进一步明确：当前默认容量可暂设 50，但架构必须支持超大上限；同一路径可重复成为独立 occurrence；单项/批量拖入和拖出是核心。产品删除的是页码、较新/较早和整页跳转，不是内部 keyset 与数据虚拟化。N0 已将源码和真实产品库迁移到 schema 8 occurrence 身份与原子批量写入；N1.1 双向复合 keyset、N1.2 默认 3×64 项 rolling window、N1.3A ViewModel 权威接管及 N1.3B WPF 连续滚动均已完成源码验证。N1.3B 删除页脚，以视口容量加一段缓冲只增扩展窗口，接入双向触边预取、淘汰前 occurrence 像素锚点及隐藏/揭示/外部拖动/排序暂停；N1.3A/B 尚未部署，当前运行候选仍停留在 N1.2。Stage 仍没有拖出，因此尚不符合完整新标准。长时间后台后呼出迟钝与 Stage 空白的前置 P0 已通过同代静态门禁、按需根重绘和一次性 cloak 内真实视图 materialization 修复；Release 662/662、用户长静置确认呼出无缓慢且 Stage 无白屏。冷/热细微差异和 Clipboard 图片短暂空白仅登记 KI-002/KI-003。下一步进入 N1.4 大规模资源/物理门禁，再按 N2 单项/批量 OLE Gate 推进。KI-001 仍延期；再次修改活动外部拖动显隐时必须重新打开并验证。

## 常用命令

当前已安装并由 `global.json` 固定 .NET SDK 10.0.400：

```powershell
dotnet restore src/Vok.sln
dotnet build src/Vok.sln -c Release
dotnet test src/Vok.sln -c Release --settings Vok.runsettings
dotnet run --project src/Vok.App/Vok.App.csproj
dotnet publish src/Vok.App/Vok.App.csproj -c Release -r win-x64 --self-contained true
```

## 编辑规则

仅在项目发生根本性变化时修改本文件，并先记录相应决策。根目录的 agent 入口文件保持通用路由。
