# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与单屏范围的 M2.0–M2.4 已完成并归档冻结；项目按 `decisions.md §26` 进入 M3 中转站开发基线，Stage 代码尚未创建。当前运行 `artifacts/r27-publish/Vok.exe`，PID 28424；归档发布位于 `artifacts/m2-archive-20260903-publish/Vok.exe`，未替换运行实例或操作真实用户数据。
- **最近进展**：剪贴板归档复验通过 Release 65/65 Core + 364/364 Windows/WPF 共 429/429、8/8 PowerShell AST、clipboard probe report v7 扩展图片矩阵 20 轮及 win-x64 自包含发布；探针的语义标识/持久化稳定，所有 job、lease 与 staging 最终归零。已冻结 `M2_CLIPBOARD_PLAN.md`，并确认中转站先于剩余设置开发、业务模型独立且共性按第二消费者提取。
- **下一步**：为中转站编写产品契约与 Gate 0，先锁定文件引用、容量/保留、窗口级 OLE 拖入拖出、操作效果和失败语义，再实施文件型最小纵向切片；此阶段不直接复用 `Clipboard*` 模型，也不提前接入 Stage 图片资产。
- **已知问题**：R27 的根故障机制和 missed-event watchdog 已验证，但真实 Modern Standby/睡眠、Unlock、Explorer 重启信号仍未物理执行。合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；真实产品清空、物理磁盘耗尽和跨屏/多屏仍未验证。既有人工验收 PNG/截图继续保留。

## §0A 最近会话

### 会话 2026-09-03 - 剪贴板归档与中转站开发基线

- 对当前 HEAD 重做归档验收：Release 65/65 Core + 364/364 Windows/WPF 共 429/429，8/8 PowerShell AST 通过；clipboard probe report v7 扩展图片矩阵 20 轮成功，覆盖 40 次 4K 与 20 次 8K 捕获，语义标识、持久化和所有权归零稳定。
- 生成 `artifacts/m2-archive-20260903-publish/Vok.exe` 自包含 win-x64 发布；没有替换当前 PID 28424、执行产品清空或改写真实用户库。首次联网还原仅因 nuget.org 漏洞审计不可达产生 NU1900，使用本地锁定包并仅对本次还原关闭审计后，全量测试和发布通过。
- `M2_CLIPBOARD_PLAN.md` 已冻结为单屏 M2 实现/验收记录；真实产品清空、物理磁盘耗尽和跨屏/多屏保持显式未物理验证，不冒充完成，也不再阻塞归档。
- 用户确认下一阶段先开发中转站、剩余设置后置。`decisions.md §26` 锁定独立 Stage 业务模型、文件型首个纵向切片、第二消费者再提取共性，以及图片接入前扩展全局资产引用证明和单一窗口级 OLE 会话所有权。

### 会话 2026-09-02 - 托盘 Shell 登记自愈闭环

- 根因复核确认：通知图标真正状态属于 Explorer/Shell，库的 `IsCreated` 只记录本地调用；`H.NotifyIcon.Wpf 2.4.1` 又会在 TaskbarCreated 恢复时吞掉异常，Vok 原先没有后验检查，导致一次外部登记丢失永久持续到重启。
- 保留现有通知图标与原生 HMENU，新增实际 HWND/GUID Shell 探针和 UI-STA 恢复 coordinator。启动、TaskbarCreated、Resume、Unlock 与 1 分钟 watchdog 请求检查；确定缺失后最多五次有界重建，每次都以后验 Shell 查询定成功，菜单打开、跨代迟到、Dispose 与同轮重入均有门禁。
- 自动测试原生删除测试图标登记并钉死“Shell 缺失但 IsCreated=true”的原机制；故障注入覆盖 watchdog 复核、耗尽、后验优先和各类竞态。Release 65/65 + 364/364 共 429/429、自包含 win-x64 发布通过。
- 将 PID 15992 精确替换为 `artifacts/r27-publish/Vok.exe` PID 28424；真实 Vok 登记被可逆删除后从 `0x80004005` 无重启恢复为 `0x00000000`，进程响应且 Ctrl+Space 保持占用。用户反馈基础交互测试基本通过；真实睡眠/Unlock/Explorer 重启物理矩阵仍待方便时执行。

### 会话 2026-09-02 - M2.4 实库迁移与托盘登记恢复

- 用户报告托盘无 Vok 图标但快捷键仍能呼出。只读诊断确认旧版 PID 14120 仍响应且 `H.NotifyIcon_*` HWND 存活，但 Windows 可见/溢出通知区均无 Vok，正确 GUID 的 `Shell_NotifyIconGetRect` 返回 `0x80004005`；这是 Shell 登记丢失，不是进程退出或图标被折叠。进程期间存在多次睡眠/Modern Standby 恢复，但缺少持久错误，不能精确锁定哪一次边沿。
- 替换前在线备份真实 schema 5 库到 `%LOCALAPPDATA%\Vok\backups\vok-pre-m24-20260902-112140.db`；备份 `quick_check=ok`、90 条历史、19 个资产、20 个引用、0 外键违规。随后精确停止 PID 14120，启动已验证的 `artifacts/m24-publish/Vok.exe` 为 PID 15992。
- 新版完成 schema 6 迁移，90 条历史分类/资产/引用全部不变，`quick_check=ok`、0 外键违规、0 PendingDelete/Quarantined。19 个 PNG 长度和 SHA-256 全部一致，0 缺失/staging；Windows 溢出区重新识别 `Vok`，图标 GUID 查询成功，持久 `Ctrl+Space` 显示已占用。
- 本次只部署并恢复当前登记，没有修改托盘代码。旧实例的长期登记丢失暴露 R27：库的 `TaskbarCreated` 恢复静默吞错且应用没有真实 Shell 健康核验，后续须独立根因修复；产品清空未执行。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
