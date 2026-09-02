# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1、M2.0–M2.3 已在既定单屏范围完成；M2.4 的实现、自动门禁和真实 schema 5→6 迁移均已完成。当前运行 `artifacts/m24-publish/Vok.exe`，PID 15992；真实库为 schema 6，90 条历史与 19 个资产完整保留。破坏性的产品清空尚未物理验收，见 `decisions.md §24`。
- **最近进展**：重启前创建在线一致性备份 `%LOCALAPPDATA%\Vok\backups\vok-pre-m24-20260902-112140.db`；迁移后 `quick_check=ok`、0 外键违规、57 Text/10 FileList/23 Image、19 个资产/20 个引用、0 PendingDelete/Quarantined。19 个 PNG 均通过长度与 SHA-256，0 缺失/staging。新版托盘已在 Windows 溢出区注册，`Shell_NotifyIconGetRect` 成功；持久 `Ctrl+Space` 被新实例占有。
- **下一步**：由用户物理确认新版 `Ctrl+Space` 呼出及托盘点击/菜单；只有确实要删除全部 Vok 历史时才执行两次确认“清空”。托盘长期运行登记丢失的根因修复按 R27 独立处理，不能用本次重启冒充修复；跨屏/多屏若未来重开，仍作为独立里程碑。
- **已知问题**：旧版长时间运行并经历多次睡眠/唤醒后，曾出现热键正常、`H.NotifyIcon` HWND 存活但 Shell 图标登记消失；新版重启已恢复，事件驱动自愈尚未实现，见 R27。合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；真实产品清空、物理磁盘耗尽和跨屏/多屏仍未验证。既有人工验收 PNG/截图继续保留。

## §0A 最近会话

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

### 会话 2026-09-01 - M2.3 单屏大图、ICC、磁盘满与资源门禁

- 用户确认继续剪贴板测试，并明确跨屏/多屏暂不实现；当前 M2.3 单屏收口边界记录于 `decisions.md §23`，既有 Per-Monitor 架构保留且不宣称多屏已支持。
- 隔离 clipboard probe 升级到 report v6，新增可选 1–50 轮扩展矩阵和独立临时 normalizer staging/schema 5/CAS。20 轮共处理 60 份 4K PNG、约 32 MiB 4K DIBV5 与 embedded ICC 8K Alpha PNG；4K 两编码语义 hash 一致，ICC 成功转 sRGB且 canonical 无 iCCP，所有 job/lease/staging 每项归零。最终只有 2 条 Image 历史、40/20 capture count、4 个 PNG 资产和 0 asset staging；第 20 轮 8K Private Bytes 不高于第 1 轮。
- 新增有效 Windows sRGB ICC 成功路径和第二资产提交 `ERROR_DISK_FULL` 故障测试；后者证明不调用 metadata upsert、结果释放且 coordinator job 归零。Release 408/408、PowerShell AST、report v6 基础/两轮/20 轮均通过。
- 浏览器第一次目标尝试因测试标签不是 OS 活动标签而作废。用户将目标标签置前后，受控复验先把可编辑区从旧图片清为 0，再按真实持久化快捷键 `Ctrl+Space` 调用“图片剪贴板，2100×1748”；Vok 隐藏、sequence `2061→2067`，系统 PNG/DIBV5 与 DOM 新增的唯一 data PNG 均为 2100×1748、694,211 bytes，Chrome 可编辑区目标 replay 通过。先前看到的 2478×1355 是测试区旧内容，不是产品错取条目。
- 用户确认 Office、聊天软件及其他剩余来源矩阵全部通过。随后只读审计真实用户库：schema 5、`quick_check=ok`、0 外键违规，10 条 Image 均有主图/缩略图双角色引用；19 个 Ready 资产与 19 个 PNG 的长度和 SHA-256 全部一致，0 缺失、游离或 staging。单屏 M2.3 据此完成，下一步进入 M2.4；跨屏/多屏继续按 §23 后置。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
