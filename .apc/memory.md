# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1、M2.0–M2.2 与单屏范围的 M2.3 已完成；图片链的捕获、schema 5/CAS、重启卡片/缩略图、Explorer/Desktop 导出及 Paint、Chrome、Office、聊天软件来源×目标往返均通过。跨屏/多屏按用户决定暂不实现且不属于 M2.3 完成门禁，不能宣称支持，见 `decisions.md §23`。
- **最近进展**：当前源码 Release 63/63 + 345/345 共 408/408，全部工具脚本 AST、隔离 clipboard report v6 和 20 轮 60 份 4K/8K/Alpha/ICC/资源矩阵通过；`ERROR_DISK_FULL` 第二资产提交不发布元数据。用户确认 Office、聊天软件及其他剩余来源矩阵通过。收尾只读审计为 schema 5、`quick_check=ok`、0 外键违规、89 条历史/10 条 Image/20 个双角色引用/19 个 Ready 资产；19 个 PNG 逐项长度和 SHA-256 一致，0 缺失、游离、PendingDelete、Quarantined、staging。
- **下一步**：进入 M2.4 规模与故障门禁，完成保留策略、固定豁免、总字节清理、tombstone/孤儿资产回收和一键清理；跨屏/多屏若未来重开，作为独立里程碑处理。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；数据库失败留下的无引用文件要到 M2.4 清理。物理磁盘耗尽只完成确定性 `ERROR_DISK_FULL` 注入，未真的填满用户磁盘；跨屏/多屏未实现/未验证。桌面保留 2 个、`artifacts/m238-manual-target` 保留 1 个既有人工验收 PNG；`artifacts/m23-validation` 保留 2 张既有证据截图。

## §0A 最近会话

### 会话 2026-09-01 - M2.3 单屏大图、ICC、磁盘满与资源门禁

- 用户确认继续剪贴板测试，并明确跨屏/多屏暂不实现；当前 M2.3 单屏收口边界记录于 `decisions.md §23`，既有 Per-Monitor 架构保留且不宣称多屏已支持。
- 隔离 clipboard probe 升级到 report v6，新增可选 1–50 轮扩展矩阵和独立临时 normalizer staging/schema 5/CAS。20 轮共处理 60 份 4K PNG、约 32 MiB 4K DIBV5 与 embedded ICC 8K Alpha PNG；4K 两编码语义 hash 一致，ICC 成功转 sRGB且 canonical 无 iCCP，所有 job/lease/staging 每项归零。最终只有 2 条 Image 历史、40/20 capture count、4 个 PNG 资产和 0 asset staging；第 20 轮 8K Private Bytes 不高于第 1 轮。
- 新增有效 Windows sRGB ICC 成功路径和第二资产提交 `ERROR_DISK_FULL` 故障测试；后者证明不调用 metadata upsert、结果释放且 coordinator job 归零。Release 408/408、PowerShell AST、report v6 基础/两轮/20 轮均通过。
- 浏览器第一次目标尝试因测试标签不是 OS 活动标签而作废。用户将目标标签置前后，受控复验先把可编辑区从旧图片清为 0，再按真实持久化快捷键 `Ctrl+Space` 调用“图片剪贴板，2100×1748”；Vok 隐藏、sequence `2061→2067`，系统 PNG/DIBV5 与 DOM 新增的唯一 data PNG 均为 2100×1748、694,211 bytes，Chrome 可编辑区目标 replay 通过。先前看到的 2478×1355 是测试区旧内容，不是产品错取条目。
- 用户确认 Office、聊天软件及其他剩余来源矩阵全部通过。随后只读审计真实用户库：schema 5、`quick_check=ok`、0 外键违规，10 条 Image 均有主图/缩略图双角色引用；19 个 Ready 资产与 19 个 PNG 的长度和 SHA-256 全部一致，0 缺失、游离或 staging。单屏 M2.3 据此完成，下一步进入 M2.4；跨屏/多屏继续按 §23 后置。

### 会话 2026-09-01 - 重启后图片可见性与 Paint replay

- 用户将 Paint 置为真实前台后，PID 14120 通过配置的 Ctrl+Space 呼出。UI Automation 首屏识别 2100×1748 图片卡，右栏截图同时证实重启前 2478×1355、2779×1509 图片卡的缩略图已从 schema 5/CAS 正常加载；总历史当时显示 88 条。
- 调用 2100×1748 图片卡后 Vok 隐藏、Paint 前台恢复，clipboard sequence `2025→2031`，Paint 截图确认原图按 2100×1748 显示且 Vok 继续响应。收尾库为 88 条历史、10 条 Image、19 个 Ready 资产，`quick_check=ok`、外键/双角色引用完整、0 PendingDelete/Quarantined，staging 归零。
- 2478×1355 重启前条目在库中唯一且卡片已可见；自动等待期间因 Paint 不在真实前台而安全拒绝注入，用户随后在 Paint 前台手动点击该条目并确认图片正常显示，精确跨重启 replay 门禁完成。30 秒隐藏样本为 Working Set 251,076,608、Private 162,893,824，单点不冒充长期资源结论。

### 会话 2026-08-31 - M2.3 重启持久化与隔离复验

- 将当前 M2.3.8 自包含实例从 PID 18136 重启为 PID 14120；重启前后真实库均为 schema 5、`quick_check=ok`、85 条历史、9 条 Image、18 条双角色引用和 17 个 Ready PNG。外键、Image 角色引用、资产引用和 17 个文件名 SHA 全部通过，0 PendingDelete/Quarantined，Vok 重启后响应正常。
- 当前源码 Release 63/63 + 343/343 共 406/406 通过；独立 Window Station clipboard probe `reportVersion=5, success=true` 再次覆盖图片 PNG/DIBV5/Bitmap 解码、像素等价、Alpha、replay receipt、自写抑制和外部覆盖失效。IFileOperation probe `reportVersion=2, success=true` 再次覆盖预取消零副作用、碰撞改名、逐项结果、字节一致与临时清理。
- 交互可见性/取用没有冒充通过：真实前台是 Minecraft，继续呼出或改写剪贴板会干扰用户会话；受控窗口又被 Windows 前台保护拒绝，重复尝试已停止。下一步由用户先切到 Paint 或 Explorer 等安全目标，再完成重启后图片卡可见、点击取用和来源×目标矩阵。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
