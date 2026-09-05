# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与单屏 M2 已冻结；M3 入站 MVP 已归档。M3.1 Checkpoint 1–5 已完成并通过重新关联候选人工 Gate；用户已确认排序前采用单一自适应卡片布局、不提供布局切换。Card Gate 0/1 实施方案已细化，卡片与 Checkpoint 6 产品代码均尚未开始。字体修复候选仍以 PID 25008 运行。
- **最近进展**：卡片方案把 `StageViewModel.Items` 保持为唯一扁平 64 项业务顺序，在 App 层新增纯布局计算与运行时行投影，以外层行 Recycling + 内层定列卡片呈现；schema 7、`AppSettings`、Router 和现有管理事务均不改变。源码核对发现当前虚拟化清理只找一个 `StageItemCard`，改为一行多卡后必须逐卡取消 `id + revision + container` Availability 绑定。卡片主体/排序把手/固定/菜单命中区、尺寸候选、逐步文件清单、自动/物理矩阵以及与二维排序的交接已写入 `docs/M3_STAGE_CARD_LAYOUT_PLAN.md`；决定见 `decisions.md §30`。
- **下一步**：从 Card 0A 开始，只增加 `StageCardLayoutCalculator/Profile` 和纯计算测试；再在 Card 0B 以 92×112、104×124 两个命名候选完成浅/深、260/520 DIP 与真实 DPI 渲染择优。尺寸冻结后依次实施行投影、XAML 卡片、逐卡 Availability/外部拖入回归和独立候选；全部通过后进入 Checkpoint 6A–6F。
- **已知问题**：KI-001 已延期：活动 Explorer 拖动中关闭 Vok 可能让松手落回源目录并生成副本。测试产生的 `.lnk` 副本未经授权不得删除；旧重新关联候选 PID 17988 已停止，当前 PID 25008 为已通过 Checkpoint 5 人工 Gate 的字体修复候选；M3 入站物理稳定性实际为 50+ 次而非计划的 100 次。

## §0A 最近会话

### 会话 2026-09-05 - M3.1 中转站管理动作规划与首批实现

- 源码核对确认 Stage schema 7 已预留 pinned/revision/稀疏排序，但仓储只有分页与 AddOrMove，条目 VM/页面仍为纯展示，Missing、移除、固定、Shell 动作、重新关联和排序均未实现；当前运行 PID 7864 是已移除源码的实验候选，本轮未替换。
- 新增 `docs/M3_STAGE_M31_PLAN.md`，把 M3.1 拆为 revision-safe 仓储、可见项 Availability、安全移除、固定/Shell/路径复制、重新关联、有界排序和候选验收七个检查点。排序不复制 Launcher 全表连续重写；外部 Router、M3.2/M3.3 和 KI-001 保持不动。
- 首批实现新增 `RemoveAsync(id, expectedRevision)` 显式终态；SQLite 只删除 Stage 行，stale revision 拒绝且临时源文件摘要不变。成功后当前页权威补位，末页回退，提交后刷新失败不重放删除。
- Stage/Clipboard 共用内部 Windows 可用性判定，但保持独立 Core 契约；Stage 只检查视口内已实现卡片，滚动回收、隐藏、换绑和迟到结果均以容器/id/revision 收口。卡片新增 Missing 状态与更多菜单安全移除入口。
- 新增 9 项门禁后 Release Core 71/71 + Integration 406/406，共 477/477，构建 0 warning / 0 error；正常/Missing/暗色/260 DIP WPF 目视通过。未改 schema、真实库、运行 PID、Router 或 KI-001；下一步进入固定与 Shell/路径动作。
- Checkpoint 4 新增 revision-safe 固定与权威条目原位回填；revision 变化会让旧 Availability 结果失效并为可见容器重查。固定/取消固定、冲突、NoChange、溢出回滚、跨重开和提交后 UI 应用失败不重放均有门禁。
- Stage 打开/定位使用独立 `StageManagementAction`，隐藏成功后才经 Launcher/Stage 共用的 internal Shell 动作和唯一 Shell STA 执行；Missing 禁用。复制原路径使用 Stage 自有 Core 契约并在 Windows 内映射唯一 Clipboard STA/self-write，保持 Vok 可见且不改条目。
- 固定按钮与更多菜单在浅色、深色、Missing、固定和 260 DIP 窄列目视通过；窄列初版曾把更多按钮挤出右侧，调整信息层级与按钮预算后复验通过。最终 Release Core 74/74 + Integration 420/420，共 494/494，构建 0 warning / 0 error；下一步进入重新关联。
- Checkpoint 5 重新关联已完成源码级实施规划并写入 `docs/M3_STAGE_M31_PLAN.md §14`：采用专用 Relink 终态、同类目标双层约束、SQLite revision/canonical 单事务、现有 Availability 换代和 WPF owned-dialog Gate，按 5A–5E 实施。目标框架已核实具备 `OpenFileDialog/OpenFolderDialog`，`.lnk` 必须关闭 `DereferenceLinks`；解析成功后目标再次消失属于文件系统/SQLite 不可原子化窗口，提交后以新 revision 重查 Missing，不跨 executor 持有资源。本次只修改计划与状态记录，未改产品代码、真实库、运行实例、Router 或 KI-001。
- Checkpoint 5 随后完成自动实施：专用 Relink 事务、ViewModel 单一操作顺序、WPF picker/dialog factory、菜单和迟到取消均落地；源文件摘要、canonical/type/revision 失败方向、Availability 换代及投影失败不重放有门禁。真实原生 file/folder dialog 又以 Topmost owner 连续 5 轮共 10 次自动打开/取消，证明 Deactivated=0、owner 可见且无孤儿窗。最终 Release Core 74/74 + Integration 433/433，共 507/507，构建 0 warning / 0 error；浅深/Missing/固定/260 DIP 复验通过。415 文件独立候选已发布；经用户明确要求，核对 `Vok.dll` SHA-256 后停止旧 PID 7864，并启动候选 PID 17988。下一步只补实际候选人工 Gate。
- 用户开始物理 Gate 后，首项发现 Stage 更多菜单中文全部呈方框。机制根因为 `StageItemActionButtonStyle` 在 Button 级设置 `Segoe Fluent Icons`，ContextMenu 由 PlacementTarget 继承到不含中文字形的图标字体；修复把 FontFamily/FontSize 收窄到两个图标 TextBlock，按钮和菜单恢复应用默认字体，并新增图标字体保留、Button/ContextMenu/MenuItem 非图标字体的 WPF 回归。Stage 65/65、完整 Release 507/507、构建 0 warning / 0 error；直接渲染 Popup 的附加截图尝试会阻塞 WPF 测试宿主，已精确终止并撤掉该测试死路，无残留进程。415 文件修复候选 `Vok.dll` SHA-256 为 `782BDAA39FAC80E2B99557243BC884B61D7E2748037DD18975F4484011826CE9`，已停止 PID 17988 并启动 PID 25008，下一步先由用户复验菜单中文。
- 2026-09-06 提交前复验再次通过真实原生 owned-dialog 5 轮（共 10 个文件/目录对话框）、完整 Release Core 74/74 + Integration 433/433 共 507/507、构建 0 warning / 0 error 与 `git diff --check`；PID 25008 候选仍在运行。`.lnk` 实际选择、Alt+Tab、应用退出与重启持久化仍是用户物理 Gate，不记作自动通过。
- 2026-09-06 用户确认 Checkpoint 5D 人工 Gate 全部通过：菜单中文实际渲染正常，文件/目录/`.lnk` 真实选择与取消、Alt+Tab、Esc/关闭、应用退出、无孤儿窗口和重启持久化均通过；Checkpoint 5 完成，下一步进入 Checkpoint 6 有界排序。只读进程核对显示候选 PID 25008 仍在运行。
- Checkpoint 6 规划核对了 Stage 仓储、64 项 keyset 页面、VM mutation gate、WPF Recycling 容器、Launcher 内部排序参照、MainWindow Esc/设置入口和唯一外部 Drop target。计划选择源 revision + 目标两侧复合邻接锚点，而不是 UI 连续索引；普通情况只更新源项，数值间隔耗尽时由仓储在真实数据库顺序中选择不超过 64 项的局部窗口。跨页成功后只重载源页/目标页中较新的安全页并截断后续历史，避免陈旧 page-start、`OFFSET` 或从首页遍历。详细 6A–6F 和门禁写入 `docs/M3_STAGE_M31_PLAN.md §16`；本轮未改产品代码、schema、真实库、运行实例、Router 或 KI-001。
- 用户确认中转站采用单一自适应卡片布局、不提供列表/卡片切换。实施方案将响应式行列留在 App 层，扁平 64 项页面继续作为唯一业务顺序；Card Gate 0/1 先于排序 UI，现有 schema 7、`AppSettings` 和 Router 不变。源码发现一行多卡会要求虚拟化清理逐张撤销 Availability 绑定；组件、命中区、文件顺序、测试矩阵及 Checkpoint 6 二维交接已细化到 `docs/M3_STAGE_CARD_LAYOUT_PLAN.md`，并记录 `decisions.md §30`。本轮仍未改产品代码、真实库或运行候选。

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
- 用户要求规划下一阶段。只读核对发现当前进程已变为源码 Release PID 24292，产物包含 Stage UI/Router；真实库已是 schema 7、`quick_check=ok`、0 外键违规并有 1 条 Stage 引用。该进程变化不是本规划执行，Stage 行来源未读取，不能据此宣称物理验收通过。
- 新增 `docs/M3_STAGE_MVP3_PLAN.md`：因真实首次 6→7 现场已不可回溯，数据门禁改为 SQLite Online Backup 后的当前 schema 7 幂等、真实数据派生 schema 6→7 和历史 schema 5→7 三组互补证据；随后隔离 466+ 回归、独立自包含候选、Explorer/桌面代表矩阵、同一 canonical 文件 100 轮成功/取消、重启/源摘要/资源收尾。容量、无效与提交故障保留在隔离库，避免填满或破坏无移除入口的真实 Stage。
- 用户要求开始执行 M3.MVP3。`Probe-M3StageData.ps1` 已用 SQLite Online Backup API 创建不覆盖历史的实时备份，并在副本通过 schema 7 幂等、派生 6→7 和历史 5→7；历史样本缺配套资产快照的 8/19 可核验边界已明确保留。新增 Router 与 Stage VM 两项 100 轮门禁后 Release 71/71 + 397/397 共 468/468、0 warning、WPF 浅深/窄列、脚本 AST 和差异检查通过。
- 从产品源码相对 `40a48c2` 无差异的状态生成独立 win-x64 自包含候选，415 文件清单和关键 SHA-256 已记录；精确停止原 PID 24292 后启动候选 PID 2664，路径/哈希/单实例及启动后真实库 48/48 资产完整性通过。固定 PID/HWND 的只读记录器已启动，下一步必须由用户完成真实 Explorer/桌面物理矩阵，不能用自动 `IDataObject` 冒充。
- 用户完成 Explorer 四文件批量、桌面目录、`.lnk` 重复、Esc 取消和 Launcher/Stage/None 跨区矩阵并确认全部通过；审计证明 Stage 只从 1 增到预期 6，数据库与资产继续健康。因时间限制，稳定性物理测试实际完成 5 组共 50+ 次且均无异常，不能记作计划的 100 次。
- 随后确认“活动拖动中呼出再关闭”曾生成同目录 `.lnk` 副本；关闭抑制虽保护源目录却导致拖动时不能关闭，DWM cloak Gate 也因失去 OLE 命中而失败。2026-09-05 用户决定延期该问题：产品与测试源码恢复到问题报告前状态，实验代码移除，简要归档为 `docs/KNOWN_ISSUES.md` KI-001；用户副本与当前运行候选均未擅自改动。

### 会话 2026-09-03 - 工作台滚动条与设置界面视觉优化

- 启动台和剪贴板的纵向滚动条由 Auto 改为 Hidden；滚轮、触控板与程序滚动能力未移除，两个列表的 Recycling/Pixel 语义及 37.5 DIP 任意偏移自动门禁继续通过。
- 右上设置入口被裁切的结构根因是顶栏两侧固定 320 DIP，在高缩放或低于约 640 DIP 的有效窗口宽度中右列会溢出；顶栏现与主体统一为 1:2:1 弹性列。设置弹窗同时从固定 720×540 改为 760×560 最大尺寸下的可用空间 Stretch，520×500 DIP 回归确认入口、弹窗和关闭按钮均完整位于可视范围。
- 设置页统一 16 DIP 弹窗、12 DIP 卡片和 9–10 DIP 导航/按钮圆角，增加语义化卡片层级、选中指示与快捷键说明块；所有按钮样式继续 BasedOn Fluent 隐式样式并仅使用动态主题资源。Release 65/65 + 364/364 共 429/429、浅/深 WPF 渲染和差异检查通过；未替换当前运行实例。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
