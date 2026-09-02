# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1、M2.0–M2.4 已在既定单屏范围完成；托盘长期 Shell 登记丢失 R27 已按 `decisions.md §25` 建立真实后验与有界自愈。当前运行 `artifacts/r27-publish/Vok.exe`，PID 28424；M2.4 的 schema 6 与数据边界未改变，破坏性的产品清空仍未物理验收。
- **最近进展**：新增实际 HWND/GUID 的 `Shell_NotifyIconGetRect` 探针，以及启动、TaskbarCreated、Resume、Unlock、1 分钟 watchdog 驱动的 UI-STA 单航班恢复。自动门禁直接删除测试图标登记，证明库仍报 `IsCreated=true` 时可同 PID 恢复；65/65 Core + 364/364 Windows/WPF 共 429/429 和 win-x64 自包含发布通过。真实 PID 28424 登记被可逆删除为 `0x80004005` 后，无重启恢复为 `0x00000000`，持久 `Ctrl+Space` 仍被占有。
- **下一步**：用户已反馈 PID 28424 基础交互测试基本通过，本轮按用户要求提交源码；方便时再执行真实睡眠/唤醒、锁定/解锁及用户触发 Explorer 重启专项矩阵。只有确实要删除全部 Vok 历史时才执行两次确认“清空”；跨屏/多屏若未来重开仍为独立里程碑。
- **已知问题**：R27 的根故障机制和 missed-event watchdog 已验证，但真实 Modern Standby/睡眠、Unlock、Explorer 重启信号仍未物理执行。合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；真实产品清空、物理磁盘耗尽和跨屏/多屏仍未验证。既有人工验收 PNG/截图继续保留。

## §0A 最近会话

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

### 会话 2026-09-01 - M2.4 保留、清空与规模故障门禁

- schema 6 新增并迁移回填 `clipboard_entries.total_bytes`；默认保留锁定为最新 100 条未固定、30 天和 512 MiB 受管总量，固定只豁免条数/时间。保留、单删和清空均以 SQLite 元数据事务先行，零引用资产转 revision tombstone 后由唯一 asset coordinator 物理回收。
- 启动恢复 PendingDelete，并只删除路径/实际 SHA 同时证明属于 CAS 的孤儿；未知或伪装内容保留并报告。产品加入两次确认清空、缩略图 cache generation 和 capture clear epoch，保证不动系统剪贴板/用户文件，且并发迟到捕获不会在 UI 或重启后复活。
- Release 构建 0 警告/错误，65/65 Core + 354/354 Integration 共 419/419；10,000 条元数据、1,000 次连续捕获、迁移回滚、固定/总容量、全部类型清空及孤儿/伪装文件门禁通过。clipboard probe report v7 基础、2 轮和 20 轮资源矩阵、8 个脚本 AST、自包含 win-x64 发布通过。
- 当前旧版 PID 14120 和真实 schema 5 用户库均未改动；发布物位于 `artifacts/m24-publish/Vok.exe`。实库迁移与清空物理验收待用户有意启动新版，清空不会被自动代做。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
