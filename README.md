# Vok

Vok 是面向 Windows 的全屏桌面工作台。生产级 M0 宿主内核已经冻结：应用在后台隐藏，通过可替换的全局热键契约呼出不透明全屏面板，再次按热键或 `Esc` 隐藏并把焦点交还给呼出前的输入控件。M0 暂用 `Ctrl + Alt + Space` 作为诊断键位，它不是永久产品规则。当前进入 M1 启动台纵向切片。

## 环境

- Windows 10/11 x64
- .NET 10 SDK（仓库由 `global.json` 固定为 `10.0.400`）

## 构建与运行

```powershell
dotnet restore src/Vok.sln
dotnet build src/Vok.sln -c Release --no-restore
dotnet test src/Vok.sln -c Release --no-build --settings Vok.runsettings
dotnet run --project src/Vok.App/Vok.App.csproj -c Release
```

Vok 为单实例应用。若当前配置的全局热键已被其他程序占用，应用会显示明确诊断信息。

## M0 回归

```powershell
dotnet publish src/Vok.App/Vok.App.csproj -c Release -r win-x64 --self-contained true
& .\tools\Probe-M0.ps1
```

探针直接读取 HWND 上的结构化、generation-scoped 宿主快照，验证 Core `Visible`、可见、Topmost、前台、Win32 focus 和 WPF 输入锚点焦点，再验证隐藏后的 generation 结束、Topmost 清除及原前台/子焦点返还；它不从 UI 文案或 Win32 focus 反推 WPF 状态。合成按键仍受 Windows 前台切换策略约束，最终仍需按文档用真实物理键盘验收。

真实焦点返还诊断可使用 `tools\Record-M0FocusRestore.cmd`。记录器按 generation 输出捕获身份、Hide/Topmost/Restore 顺序、`SetForegroundWindow` 布尔结果和恢复后 foreground/focus；它不参与焦点恢复。历史合成循环不能替代真实外部文本框验收。2026-08-24 用户已确认快捷键隐藏与 Esc 隐藏均能返回原文本框。

架构边界与后续里程碑见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)；三方行为对照、性能数据、门槛证据状态与真实键盘步骤见 [docs/M0_HOST_KERNEL.md](docs/M0_HOST_KERNEL.md)。
