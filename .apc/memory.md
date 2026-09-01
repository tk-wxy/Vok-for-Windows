# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1、M2.0–M2.2 与 M2.3.0–8 保持可用；M2.3.8 图片目标分流、重启持久化、隔离平台复验，以及重启后真实图片卡/缩略图可见与 Paint 当前/重启前图片 replay 均通过。正向 Explorer 文件系统目录/Desktop 直接创建 canonical PNG，其他及虚拟目标继续安全粘贴；更广图片矩阵完成前仍不宣布整个 M2.3 收口。
- **最近进展**：当前源码 Release 406/406 通过。PID 14120 真实呼出后首屏显示重启后的 2100×1748 与重启前的 2478×1355、2779×1509 图片及真实缩略图；2100×1748 自动 replay 使 Vok 隐藏、Paint 前台恢复并显示原图，clipboard sequence `2025→2031`，用户随后确认重启前 2478×1355 历史也在 Paint 正常显示。收尾真实库 `quick_check=ok`，88 条历史/10 条 Image/19 个 Ready 资产，无缺失引用、PendingDelete/Quarantined 或外键错误，staging 为 0。
- **下一步**：继续 M2.3 来源×目标、透明 Alpha、150%/200% 跨屏 DPI、4K/8K、embedded ICC、空间不足与长期资源矩阵，通过后进入 M2.4。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；数据库失败留下的无引用文件要到 M2.4 清理。真实 Image 尚未覆盖 Alpha、4K/8K、物理跨屏 DPI、embedded ICC、长期资源与空间不足。桌面保留 2 个、`artifacts/m238-manual-target` 保留 1 个既有人工验收 PNG；`artifacts/m23-validation` 保留 2 张本轮证据截图。

## §0A 最近会话

### 会话 2026-09-01 - 重启后图片可见性与 Paint replay

- 用户将 Paint 置为真实前台后，PID 14120 通过配置的 Ctrl+Space 呼出。UI Automation 首屏识别 2100×1748 图片卡，右栏截图同时证实重启前 2478×1355、2779×1509 图片卡的缩略图已从 schema 5/CAS 正常加载；总历史当时显示 88 条。
- 调用 2100×1748 图片卡后 Vok 隐藏、Paint 前台恢复，clipboard sequence `2025→2031`，Paint 截图确认原图按 2100×1748 显示且 Vok 继续响应。收尾库为 88 条历史、10 条 Image、19 个 Ready 资产，`quick_check=ok`、外键/双角色引用完整、0 PendingDelete/Quarantined，staging 归零。
- 2478×1355 重启前条目在库中唯一且卡片已可见；自动等待期间因 Paint 不在真实前台而安全拒绝注入，用户随后在 Paint 前台手动点击该条目并确认图片正常显示，精确跨重启 replay 门禁完成。30 秒隐藏样本为 Working Set 251,076,608、Private 162,893,824，单点不冒充长期资源结论。

### 会话 2026-08-31 - M2.3 重启持久化与隔离复验

- 将当前 M2.3.8 自包含实例从 PID 18136 重启为 PID 14120；重启前后真实库均为 schema 5、`quick_check=ok`、85 条历史、9 条 Image、18 条双角色引用和 17 个 Ready PNG。外键、Image 角色引用、资产引用和 17 个文件名 SHA 全部通过，0 PendingDelete/Quarantined，Vok 重启后响应正常。
- 当前源码 Release 63/63 + 343/343 共 406/406 通过；独立 Window Station clipboard probe `reportVersion=5, success=true` 再次覆盖图片 PNG/DIBV5/Bitmap 解码、像素等价、Alpha、replay receipt、自写抑制和外部覆盖失效。IFileOperation probe `reportVersion=2, success=true` 再次覆盖预取消零副作用、碰撞改名、逐项结果、字节一致与临时清理。
- 交互可见性/取用没有冒充通过：真实前台是 Minecraft，继续呼出或改写剪贴板会干扰用户会话；受控窗口又被 Windows 前台保护拒绝，重复尝试已停止。下一步由用户先切到 Paint 或 Explorer 等安全目标，再完成重启后图片卡可见、点击取用和来源×目标矩阵。

### 会话 2026-08-31 - M2.3.8 图片目标分流 Gate 0

- 规划确认图片卡主动作只在正向识别 Explorer 当前文件系统目录或 Desktop 时直接创建 canonical PNG；该分支不改剪贴板、不注入 `Ctrl+V`，其他目标继续原有安全粘贴。虚拟目录、第三方文件管理器、批量/格式设置等明确后置。
- 新增隔离 Shell/COM STA 探针：按捕获顶层 HWND 匹配 `IShellWindows`，以 `QueryActiveShellView→IFolderView→IShellItem` 取得活动 View 与文件系统目录；当前两个 Explorer 窗口解析成功且两条独立路径证据的哈希一致。另用 `IFileOperation` 在工具自建临时目录验证 Unicode 名、连续碰撞、预取消、源/目标字节与清理，双实例并发通过。
- 最终“此电脑”显式捕获为 `Unsupported`，活动 View 与 Automation 均无文件系统目录，Gate 0 全部关闭。产品已增加 canonical-only export lease、正向活动 View/Desktop 解析、写前同目标复核、Shell STA 紧邻副作用的 generation 认领、IFileOperation 单项/目标 hash 验证及不触碰剪贴板的目录分支；其他目标完整回退旧流程。
- Release 构建 0 警告，Core 63/63 + Windows/WPF 343/343 共 406/406 通过；测试覆盖旧代/未授权零落盘、双击一次、创建后隐藏失败不重试、授权后结果不明不回退、export lease/取消和产品 COM ABI 的 Unicode 碰撞/字节/清理。
- 自包含 M2.3.8 PID 18136 真机通过普通 Explorer 单文件、Desktop 单文件与碰撞改名；三份目标文件 SHA 均匹配 canonical 资产。虚拟“此电脑”只推进 sequence `1959→1965` 且目录计数不变；Paint 成功显示并推进 `1965→1971`。M2.3.8 范围完成，三个验收 PNG 暂留待用户决定是否删除。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
