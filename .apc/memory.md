# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1、M2.0–M2.2 与 M2.3.0–8 保持可用；M2.3.8 图片目标分流的 Gate 0、产品接线、自动门禁和新版真机验收均已完成。正向 Explorer 文件系统目录/Desktop 直接创建 canonical PNG，其他及虚拟目标继续安全粘贴；更广图片验收完成前仍不宣布整个 M2.3 收口。
- **最近进展**：0 警告 Release、Core 63/63 + Windows/WPF 343/343 共 406/406 通过。PID 18136 真机验证 Explorer 单文件、Desktop 单文件与碰撞改名的 SHA 均匹配 canonical 资产；“此电脑”只使 clipboard sequence `1959→1965` 且不落盘，Paint 成功显示并使 sequence `1965→1971`，目录计数不变。
- **下一步**：继续 M2.3 的重启后多来源×目标、透明 Alpha、150%/200% 跨屏 DPI、4K/8K、embedded ICC、空间不足与长期资源矩阵；M2.3.8 后置缺口仅在需求重新进入时考虑。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；数据库失败留下的无引用文件要到 M2.4 清理。真实 Image 尚未覆盖 Alpha、4K/8K、物理跨屏 DPI、embedded ICC、长期资源与空间不足。桌面保留 2 个、`artifacts/m238-manual-target` 保留 1 个本轮人工验收 PNG，尚未清理。

## §0A 最近会话

### 会话 2026-08-31 - M2.3.8 图片目标分流 Gate 0

- 规划确认图片卡主动作只在正向识别 Explorer 当前文件系统目录或 Desktop 时直接创建 canonical PNG；该分支不改剪贴板、不注入 `Ctrl+V`，其他目标继续原有安全粘贴。虚拟目录、第三方文件管理器、批量/格式设置等明确后置。
- 新增隔离 Shell/COM STA 探针：按捕获顶层 HWND 匹配 `IShellWindows`，以 `QueryActiveShellView→IFolderView→IShellItem` 取得活动 View 与文件系统目录；当前两个 Explorer 窗口解析成功且两条独立路径证据的哈希一致。另用 `IFileOperation` 在工具自建临时目录验证 Unicode 名、连续碰撞、预取消、源/目标字节与清理，双实例并发通过。
- 最终“此电脑”显式捕获为 `Unsupported`，活动 View 与 Automation 均无文件系统目录，Gate 0 全部关闭。产品已增加 canonical-only export lease、正向活动 View/Desktop 解析、写前同目标复核、Shell STA 紧邻副作用的 generation 认领、IFileOperation 单项/目标 hash 验证及不触碰剪贴板的目录分支；其他目标完整回退旧流程。
- Release 构建 0 警告，Core 63/63 + Windows/WPF 343/343 共 406/406 通过；测试覆盖旧代/未授权零落盘、双击一次、创建后隐藏失败不重试、授权后结果不明不回退、export lease/取消和产品 COM ABI 的 Unicode 碰撞/字节/清理。
- 自包含 M2.3.8 PID 18136 真机通过普通 Explorer 单文件、Desktop 单文件与碰撞改名；三份目标文件 SHA 均匹配 canonical 资产。虚拟“此电脑”只推进 sequence `1959→1965` 且目录计数不变；Paint 成功显示并推进 `1965→1971`。M2.3.8 范围完成，三个验收 PNG 暂留待用户决定是否删除。

### 会话 2026-08-31 - M2.3.7 产品图片接线与自动门禁

- Core 新增转移 `ClipboardCaptureResult` 所有权的窄图片捕获接口，Windows pipeline 串接既有 normalizer 与资产 coordinator；App Controller 在图片目录分支提交 Image，单条失败可观察且不终止后续监听，取消/退出仍走既有 watchdog。
- 组合根以单一 `ClipboardImageFeature` 显式拥有共享 options、normalizer、资产 coordinator 和缩略图缓存；同一 coordinator 同时用于双资产提交、缩略图、删除与 replay。部分启动失败会按 cache→coordinator/store→normalizer 释放，文本/文件继续可用并显示图片降级。
- Release 62/62 + 330/330 共 392/392，61 项接线/生命周期/缓存/replay/平台高风险测试连续五轮、自包含发布、7 个脚本 AST 与 Window Station `reportVersion=5, success=true` 通过。真机操作后只读确认真实库 schema 5，6 条 Image、12 条双角色引用和 11 个 Ready 资产全部通过 SHA/长度/尺寸/外键核验。用 OS 进程终止模拟异常退出，离线库可读且重启前后摘要一致；最终发布现以 PID 11276 运行且 `Responding=True`。重启后真实目标粘贴、来源×目标、Alpha、DPI、4K/8K 和长期资源仍待验收。

### 会话 2026-08-31 - M2.3.6 图片 replay、写回与安全粘贴

- Core materialization 以两个只读 lease 持有 canonical PNG 与受限 DIBV5；asset coordinator 在既有 MTA 串行边界复核 primary Ready/revision、内容 SHA、严格 PNG/WIC、尺寸/Alpha 和 `Vok.Image.Pixel.v1`。只有内容完整性失败 quarantine，stale、取消、暂存或 writer 环境失败不污染 Ready 资产。
- DIBV5 固定为 124-byte header、32bpp bottom-up BGRA8、显式 RGBA masks、sRGB 与 nullable DPI；Clipboard STA 以 1 MiB 缓冲按 PNG→CF_DIBV5→marker 写入。部分失败会清空半份内容，只释放未转移 HGLOBAL；Controller 在物化完成后隐藏并复用原有 receipt/generation/focus/inject 状态机，所有终态释放 lease。
- 隔离 Release 62/62 + 322/322 共 384/384，33 项 materializer/writer/paste/asset 高风险测试连续五轮、win-x64 自包含发布与 Window Station `reportVersion=5, success=true` 通过。本阶段未接产品组合根、未操作交互剪贴板；下一步 M2.3.7 一次接线和真实来源×目标/DPI/长期资源验收。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
