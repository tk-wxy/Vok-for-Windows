# Vok for Windows

> 一个热键唤出的 Windows 全屏效率工作台 —— 启动台、剪贴板历史与文件中转站。  
> 召之即来，用完即隐，自动恢复前台焦点。

---

## 简介

**Vok** 是专为 Windows 设计的原生全屏工作台，作为您日常工作流之外的“第二桌面”。

通过全局快捷键随时呼出，完成应用启动、查找历史剪贴板或整理拖拽文件后立即自动隐藏，并将焦点精准无损地还给先前的活动窗口或文本输入控件，大幅降低在多窗口和多工具之间反复切换的割裂感。

基于 **.NET 10 (C#) + WPF + Win32/Shell** 原生打造，杜绝 Web 套壳的高内存开销与渲染延迟，专注极速响应与低系统资源占用。

---

## 核心功能

- **🚀 应用启动台 (Launcher)**
  - 快速索引与呼出常用应用程序与文档
  - 响应式自适应卡片布局，支持分类与排序
- **📋 剪贴板历史 (Clipboard)**
  - 支持文本、图片与文件列表等多类型历史记录
  - 本地 SQLite 安全存储，基于内容寻址管理离线图片资产
  - 严格防范自写回流冲突，支持一键安全还原与直接粘贴
- **📦 文件中转站 (Stage)**
  - 拖拽暂存区：跨窗口/跨目录转移文件的临时集散地
  - 支持外部文件直接拖入暂存、卡片整理排序以及外向拖出至目标窗口
- **⚡ 丝滑原生体验**
  - 全局快捷键（默认 `Ctrl + Space`）即时呼出与收起
  - 失去前台焦点或按 `Esc` 自动隐藏，并精确恢复呼出前的前台窗口与输入焦点
  - 原生 Fluent 视觉风格，普通不透明 HWND 设计，保持极低常驻资源消耗

---

## 环境要求

- **操作系统**：Windows 10 / Windows 11 (x64)
- **运行环境**：[.NET 10 SDK](https://dotnet.microsoft.com/)（通过 `global.json` 锁定 `10.0.400`）

---

## 构建与运行

```powershell
# 1. 还原项目依赖
dotnet restore src/Vok.sln

# 2. 编译项目 (Release)
dotnet build src/Vok.sln -c Release

# 3. 运行测试套件
dotnet test src/Vok.sln -c Release --settings Vok.runsettings

# 4. 启动应用
dotnet run --project src/Vok.App/Vok.App.csproj -c Release
```

### 独立发布 (Self-Contained)

```powershell
dotnet publish src/Vok.App/Vok.App.csproj -c Release -r win-x64 --self-contained true
```

---

## 快捷键说明

| 快捷键 | 功能 |
| :--- | :--- |
| `Ctrl + Space` | 呼出 / 隐藏 Vok 工作台（默认热键） |
| `Esc` | 隐藏工作台并交还焦点 |

> 全局唤出热键及托盘行为可在设置面板中自由配置。

---

## 技术架构

- **UI 呈现**：WPF 原生 HWND（不透明普通窗口，杜绝全屏透明图层开销）
- **数据持久化**：SQLite (WAL 模式) + 本地内容寻址大资产存储
- **系统集成**：Microsoft.Windows.CsWin32 原生 Win32、Shell 与 OLE 交互隔离
- **工程解耦**：
  - `src/Vok.Core`：平台无关业务模型与会话状态机
  - `src/Vok.Infrastructure.Windows`：Win32/Shell、剪贴板 STA、SQLite 持久化实现
  - `src/Vok.App`：WPF 表现层与依赖注入组合根

---

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
