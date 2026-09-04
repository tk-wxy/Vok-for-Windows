# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与单屏范围的 M2.0–M2.4 已完成并归档冻结；M3 完整文件型合同由 `decisions.md §27` 约束，当前入站 MVP 由 §28 收窄。M3.MVP1 数据内核与 M3.MVP2 Stage 展示/统一入站路由均已完成；真实产品库副本、发布候选和 Explorer/桌面物理闭环尚未验收。当前运行的是用户此前“启动”请求产生的源码 Release PID 848（创建于 2026-09-04 20:43:31），MVP2 实施只使用隔离 artifacts，未停止、重启或替换它。
- **最近进展**：新增固定 64 项、可前后导航的 Stage keyset 页面与真实 WPF 卡片/状态/页脚；唯一 `WorkspaceFileDropRouter` 独占 drag id、三态目标、一次 FileDrop 快照和幂等终态，Launcher/Stage 仅作 adapter，OLE Copy 与异步持久结果分开表达。Launcher 忽略 `canDrop` 的根因已通过 active/targeted 双状态修正；目标切换、重入隐藏、替换/迟到终态、异步失败、提交后刷新失败、10,000 条有界 VM 和浅/深/260 DIP 布局均有门禁。Release 71/71 Core + 395/395 集成共 466/466，构建 0 warning / 0 error；`IWorkspaceWindow`/`WorkspaceController` 与 MVP1 Core/schema/仓储均未修改。
- **下一步**：进入 M3.MVP3：先只读审计 PID 848 对应真实库状态，再只在数据库副本验证 schema 迁移；随后生成不覆盖当前实例的发布候选，执行 Explorer/桌面“拖动中呼出 → Stage 松手 → 条目显示并跨重启保留”的物理闭环。MVP3 不再重构 Router 或 Stage 页面状态。
- **已知问题**：当前运行 PID 848 仍是 MVP1 二进制，不包含本次 Stage UI/Router；真实库可能已初始化为 schema 7，但尚未直接读取确认。自动化只证明代码路径就绪，尚不能宣称真实 Explorer/桌面入站可用；Drop 向 OLE 返回 Copy 与异步落库成功仍可能在物理体验上分时出现，需验证状态可在下次呼出看到。隐藏 WPF 源 HWND 的出站 OLE 尚未物理验证但不阻塞入站 MVP；R27 的真实 Modern Standby/睡眠、Unlock、Explorer 重启信号，合法 PNG 轻量 header 的 codec 失败后备、真实产品清空、物理磁盘耗尽和跨屏/多屏仍未验证。

## §0A 最近会话

### 会话 2026-09-04 - M3 中转站产品契约、扩容与入站 MVP 收窄

- 用户认可前瞻功能与架构分析并要求形成实施计划。`docs/M3_STAGE_PLAN.md` 已锁定 Stage 为引用型可消费货架，首期只做文件/目录/`.lnk`：默认 50、产品上限 200，固定计容量，批量原子拒绝、重复回顶、跨重启保留和 Missing 动态展示。
- 用户随后确认前期 200 合理，但要求当前架构支持后续高上限。计划已明确 200 只属于组合根策略，不进入 schema/仓储；首版使用 64 位计数与稀疏排序、keyset 和有界 VM 页面窗口，以 10,000 条门禁证明结构，并把未来更高规模限定为策略与性能门禁调整，而非 Stage 主结构迁移。
- 取用、仅复制和拖出使用 Copy-only；只有真实交付完成才消费未固定项，交付与数据库清理分开收口。Stage 保持独立 Core 聚合/schema 7/仓储/VM，复用现有 SQLite、Shell STA、Clipboard STA、Workspace 目标恢复和输入注入，不复用 `Clipboard*` 业务模型。
- 实施拆为 M3.0–M3.5：先以隔离 WPF/OLE 探针证明源窗隐藏后的 `DoDragDrop`，再依次接 Core/仓储、统一窗口级入站 router/UI、点击取用、正式拖出和收口归档。`decisions.md §27` 与架构状态已同步；本次未创建 Stage 代码、未运行自动测试或改变运行实例。
- 只读进程检查发现旧 PID 28424 已停止，当前 Vok 为源码 Release 输出 PID 16900；该变化并非本会话执行。429/429 保持为进入 M3 的最近已验证回归基线。
- 用户随后将当前最小 MVP 明确收窄为“拖动外部文件 → 呼出界面 → 在中转站松手 → 条目落入”。源码核对证明 Launcher 已验证的 Workspace drag lease/长按松键机制可复用，当前未知量是唯一 Launcher/Stage 路由、独立 Stage schema/仓储和真实展示；因此出站 `DoDragDrop` 探针后置到拖出功能之前。本轮仅更新 `docs/M3_STAGE_PLAN.md`、架构/决策/状态文档，未创建代码或重跑测试。
- 第一实施步现已细化为独立 `M3_STAGE_MVP1_PLAN.md`：Stage 不复制 Launcher 的 int 总数、单字段游标、尾部追加、重复不移动和全表排序行为；改用 long 总数、`(sort_order,id)`、稀疏头插、重复回顶与单事务容量权威。schema 7 只在临时库和真实库副本验证，MVP2/3 完成前禁止迁移真实库或替换当前实例，避免旧 schema 6 二进制被单向版本边界阻断。
- M3.MVP1 随后完成实现：新增独立 Stage Core、schema 7、`SqliteStageRepository`、共享 Shell STA 的 `WindowsStageFileService` 与 DI；未接 WPF/OLE 或启动加载。自审补充当前 schema 缺表/错误索引拒绝、未来版本拒绝、容量满时仅重复可回顶，以及批内 revision 溢出回滚前序写入。
- 使用隔离 `%TEMP%` artifacts 验证 Release 71/71 Core + 385/385 集成共 456/456，10,000 条 keyset 使用顺序索引，构建 0 warning / 0 error；PID 16900 和真实产品数据库未改变。下一步进入 M3.MVP2 UI 与唯一入站路由，不能提前宣称中转站可用。
- 用户要求分析规划下一阶段。源码确认 `WorkspaceController` 的 generation-scoped 外部拖放 lease 可保持不变，实际耦合集中在 `MainWindow` 的 drag id/快照/Launcher 直连，以及 Launcher overlay 忽略 `canDrop`；`docs/M3_STAGE_MVP2_PLAN.md` 因此把实现拆为页面状态、纯展示、Launcher-only Router 提取、三态路由、异步 Stage 分发和生命周期六个可独立验证变量。
- MVP2 页面采用固定 64 项窗口和轻量 keyset cursor 历史，通过“较新/较早”整页替换避免 Clipboard 式永久追加；Stage 本轮只显示稳定类型 glyph，不做 Shell 图标、Missing 检查或任何取用/管理手势。规划完成但产品代码、PID 与真实数据库均未改变。
- 收口时发现先前 PID 16900 已结束，源码 Release PID 848 于 20:43:31 启动；可执行文件也在该时刻生成，符合此前被中断的用户“启动”请求。本规划没有结束或替换 PID 848。由于正常 App 启动会经 Appearance 初始化共享存储，真实库 schema 7 状态改记为“可能已迁移、尚未直接核验”，后续不得继续沿用“真实库肯定未触碰”的旧假设。
- 用户要求开始实施 M3.MVP2。Stage 固定 64 项页面、真实 WPF 展示、唯一三态 Router、Launcher active/targeted 修正、异步 Stage 入站状态与组合根已落地；路由先认领/清视觉再读取外国数据，目标切换先全撤销再唯一激活，不修改已验证的 Workspace drag lease。
- 新增 10 项门禁并通过 Release 71/71 Core + 395/395 集成共 466/466、0 warning 构建与 `git diff --check`；浅/深真实 WPF 渲染目视通过，260×408 DIP 窄列布局通过。PID 848 保持原进程，测试未连接真实产品库。下一步为 M3.MVP3 实库副本、发布候选和 Explorer/桌面物理闭环。

### 会话 2026-09-03 - 工作台滚动条与设置界面视觉优化

- 启动台和剪贴板的纵向滚动条由 Auto 改为 Hidden；滚轮、触控板与程序滚动能力未移除，两个列表的 Recycling/Pixel 语义及 37.5 DIP 任意偏移自动门禁继续通过。
- 右上设置入口被裁切的结构根因是顶栏两侧固定 320 DIP，在高缩放或低于约 640 DIP 的有效窗口宽度中右列会溢出；顶栏现与主体统一为 1:2:1 弹性列。设置弹窗同时从固定 720×540 改为 760×560 最大尺寸下的可用空间 Stretch，520×500 DIP 回归确认入口、弹窗和关闭按钮均完整位于可视范围。
- 设置页统一 16 DIP 弹窗、12 DIP 卡片和 9–10 DIP 导航/按钮圆角，增加语义化卡片层级、选中指示与快捷键说明块；所有按钮样式继续 BasedOn Fluent 隐式样式并仅使用动态主题资源。Release 65/65 + 364/364 共 429/429、浅/深 WPF 渲染和差异检查通过；未替换当前运行实例。

### 会话 2026-09-03 - 剪贴板归档与中转站开发基线

- 对当前 HEAD 重做归档验收：Release 65/65 Core + 364/364 Windows/WPF 共 429/429，8/8 PowerShell AST 通过；clipboard probe report v7 扩展图片矩阵 20 轮成功，覆盖 40 次 4K 与 20 次 8K 捕获，语义标识、持久化和所有权归零稳定。
- 生成 `artifacts/m2-archive-20260903-publish/Vok.exe` 自包含 win-x64 发布；没有替换当前 PID 28424、执行产品清空或改写真实用户库。首次联网还原仅因 nuget.org 漏洞审计不可达产生 NU1900，使用本地锁定包并仅对本次还原关闭审计后，全量测试和发布通过。
- `M2_CLIPBOARD_PLAN.md` 已冻结为单屏 M2 实现/验收记录；真实产品清空、物理磁盘耗尽和跨屏/多屏保持显式未物理验证，不冒充完成，也不再阻塞归档。
- 用户确认下一阶段先开发中转站、剩余设置后置。`decisions.md §26` 锁定独立 Stage 业务模型、文件型首个纵向切片、第二消费者再提取共性，以及图片接入前扩展全局资产引用证明和单一窗口级 OLE 会话所有权。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
