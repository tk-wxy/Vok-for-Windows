# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与单屏 M2 已冻结；M3 入站 MVP、管理动作、V2 卡片、排序 S0–S3 和呼出 P0 已通过。N0 occurrence/schema 8、N1.1 双向复合 keyset 与 N1.2 纯 rolling-window 状态均已完成并提交但未部署；N1.2 尚未接入产品 UI。运行中的 PID 18352 与真实产品库仍保持既有 schema 7，本轮未替换候选。
- **最近进展**：新增纯 Core `StageRollingWindow`，以命名的 64 项 segment、最多 3 segment/192 项实现双向合并与反向淘汰；请求凭据绑定窗口实例、generation、方向、cursor 和接缝边界，迟到/重复/跨实例响应不落状态，总数/边界漂移及原边缘空响应要求 reset，结构损坏页原子拒绝。21 项新增门禁覆盖同 sort-order GUID、末段不足 64、10k 双向全遍历且常驻不超过 192；最终 Release Core 108/108 + Integration 583/583 共 691/691、独立构建零警告。测试前后真实库版本 7、schema 与八表摘要一致，46 条 Stage、`quick_check=ok`、0 外键违规。
- **下一步**：进入 N1.3：将 rolling window 作为 Stage ViewModel 唯一数据窗口，替换 `_pageStarts/_pageIndex/Items.Clear` 整页权威并增量应用 added/evicted delta，再接近边缘的双向预取与像素锚点；先分析 mutation/reorder/Availability 与现有 `ScrollChanged` 的交接，不恢复可见分页。
- **已知问题**：N1.3–N1.4 ViewModel/WPF 连续滚动、N2/N3 单项/批量拖出与交付后清理尚未实现，因此当前产品 UI 仍保留 64 项整页分页且不是完整 Stage 产品候选。KI-001 仍后置；KI-002 为长静置首次呼出略慢于连续热呼出，KI-003 为 Clipboard 图片缩略图短暂空白。纯 30 分钟、明确 Modern Standby/Unlock 分档及正式 Present 证据未逐项完成；测试产生的 `.lnk` 副本未经授权不得删除。

## §0A 最近会话

### 会话 2026-09-13 至 2026-09-15 - Stage P0、N0 与 N1 连续数据虚拟化

- P0.1 增加默认关闭的有界 reveal/Shell/资源/恢复轨迹、报告工具和固定候选截图探针；真实 46 条基线证明普通 Hide 不重建行，但旧路径每次揭示前重查 46 项，且通用 Rendering 可早于行发布和滚动完成。
- P0.2–4 将隐藏期静态投影准备、generation/content/layout/state readiness token、render-return + attempt DWM fence、Availability 状态保留及物理在途上限 4 接入生产路径；Availability 只在完成呈现后复查，内部/外部拖动期间暂停。
- 5 分钟单变量 A/B 复现：没有根视觉失效时，managed 内容和 fence 均完成仍先暴露纯背景；重新可见时显式 `InvalidateVisual` 根工作区后，首张与稍后截图一致且含完整 46 项，确认长期隐藏后的 DWM surface 丢失而 WPF retained tree 未自动变脏是该次机制。
- 完整 Release 659/659、零警告构建；快速候选 100/100，最终精确构建 10/10，30 秒隐藏首图完整且揭示前零 Availability。30 分钟、Modern Standby/Unlock 和用户最终观感仍待验证，故整个 P0 尚未归档；改动未提交。
- 用户随后在长静置且从未真实 Show 的 PID 18952 复现迟钝/Stage 先空白；轨迹将 528.4 ms 揭示定位到首次 Loaded/列投影/模板布局，而非热键、数据库或 Availability。一次性 cloak 内 materialization 候选把首次/30 秒/5 分钟解除 cloak 降为 39.9/34.0/52.3 ms，首后帧一致；完整 Release 662/662，仍待用户与 30 分钟/S0 Gate。
- 用户在 PID 18352 长静置后确认呼出无明显迟缓、Stage 无白屏，明确 P0 完成预期；冷呼出仍略慢于连续热呼出、Clipboard 图片会短暂空白，仅登记 KI-002/KI-003，不在本轮继续修改。
- 用户确认进入下一阶段第一步后，将 N0.1 收窄为 schema 8：迁移 canonical 唯一索引为普通 NOCASE 索引，严格核验 schema 7/8 结构和事务回滚；本切片不改 `AddOrMoveAsync`、Router、ViewModel、UI、重新关联或运行实例。
- 实施中完整测试暴露组合根测试会解析默认产品存储并把真实库从 7 迁至 8。八张业务表摘要确认零变化后，在同一事务中仅恢复 schema 7 版本与旧唯一索引；随后把产品默认工厂改为组合根显式参数，两个组合测试各注入唯一临时库。修复后完整测试前后真实库版本、schema 与表摘要完全一致，原触发机制已消除。
- 最终 Release 668/668、零警告构建、11/11 PowerShell AST 与差异检查通过；真实 schema 7 在线备份保持源稳定，副本 7→8→8、schema 8 再重放及派生 6→8/历史 5→8 均成功，业务表零变化、46 条 Stage 全保留、`quick_check=ok`、0 外键违规。改动已提交但未发布，PID 18352 和真实库均保持旧版本。
- N0.2 将 Core/SQLite/ViewModel 从 `AddOrMoveAsync` 全量切换为 `AddBatchAsync`，删除同批去重、查旧项回顶和 `CanonicalConflict`；每次放入独立生成 occurrence，Router 原序重复透传不改生产逻辑。自动门禁覆盖同批/跨批与 canonical 大小写重复、49/50/51、schema 7→8 后立即重复写、SQL 中途失败/非法 descriptor 零写入、并发容量和源文件摘要。
- 最终 Release Core 87/87 + Integration 582/582 共 669/669、零警告构建；完整测试首次暴露 Clipboard 丢弃诊断早于 `finally` 清理的既有测试竞态，改为等待真实 job/lease 回落后单测 20/20、全套通过。真实库前后保持 schema 7、schema/八表摘要不变、46 条 Stage、`quick_check=ok`、0 外键违规；N0.2 已提交但未部署，PID 18352 未替换。
- N1.1 将 Stage 读取合同扩展为显式向旧/向新复合 keyset，返回双侧 cursor、是否还有数据及窗口外紧邻排序边界；向新读取使用同一 `ix_stage_items_order` 的 ASC 反向扫描，结果再恢复统一 DESC 顺序，单侧 lookahead 保持每页只额外读取一次另一侧边界。当前 ViewModel 只显式消费初始/向旧路径，分页 UI 尚未改变。
- 同 sort-order GUID 结、双向中页往返、精确边界、10k 实际往返/双向索引计划和非法方向/游标均通过；最终 Release 87/87 + 583/583 共 670/670、0 warning / 0 error。真实库前后仍为 schema 7、schema/八表摘要一致、46 条 Stage、`quick_check=ok`、0 外键违规；改动已提交但未部署，PID 18352 未替换。
- N1.2 新增纯 Core rolling-window 状态：64 项 segment、最多 3 segment/192 项；加载凭据绑定实例、generation、方向、cursor 与接缝边界，支持向旧追加/向新前插、第四段反向淘汰和 added/evicted delta。总数/接缝漂移与旧边缘空响应要求 reset，迟到/重复/跨实例响应不落状态，结构错误页原子拒绝。
- 21 项新增门禁覆盖双向合并、反向淘汰、末段不足 64、同 sort-order GUID、10k 双向全遍历且常驻不超过 192、generation/cursor/边界迟到与无部分应用。默认目录全量命令因保留运行中的 PID 18352 锁定 DLL 而失败，未停止实例；改用唯一 `%TEMP%` ArtifactsPath 后 Release 108/108 + 583/583 共 691/691、构建 0 warning / 0 error。测试前后真实 schema 7、schema/八表摘要、46 条 Stage、`quick_check=ok`、0 外键违规均一致；N1.2 已提交但尚未接线或部署。

### 会话 2026-09-05 至 2026-09-12 - M3.1 中转站管理动作与卡片基础

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
- Card 0A 已新增纯布局 Profile/Calculator 和 35 项门禁。两个候选档都保证 260 DIP 两列，520 DIP 分别为 Compact 五列、ManagementSafe 四列；非法/断点/超窄/超宽/卡宽/余量/值稳定性均覆盖。完整 Release Core 74/74 + Integration 468/468 共 542/542、构建 0 warning / 0 error；未改 XAML、schema、设置、Router、真实库或 PID 25008。下一步 Card 0B 视觉择优后删除落选档。
- 下一步源码诊断确认：Card 0B 必须使用布局后的真实 ScrollViewer viewport 而非 Stage 外层宽度，并把 720/800/960 DIP 宽中心区纳入 5 列上限的节奏门禁；计划已拆为测试专用双候选夹具、真实宽度/浅深矩阵、硬失败择优和单候选清理四步。行投影归 StageView 所有，页面 `Clear + Add×N` 合并发布后才滚动归顶；普通 Stretch UniformGrid 不得二次改写卡宽/间距。详细实施边界已写入 `docs/M3_STAGE_CARD_LAYOUT_PLAN.md`，本轮未改产品代码、schema、设置、Router、真实库或运行进程。
- Card 0B 最终选择 ManagementSafe 104–124×124 DIP；Compact 因窄宽截断淘汰，5 列上限又因宽中心区留白只改为最多 8 列并复验。Card 1A–1C 已完成行投影、真实卡片 XAML、滚动锚点和逐卡 Availability 清理；页面替换残留 20 DIP 的机制是 `RowsPublished` 早于虚拟面板新 extent，改以 `ContextIdle` 布局阶段归顶后复验通过，无延时/重试。100 轮换列/滚动/显隐、Stage 98/98、MainWindow 3/3、Router 6/6、完整 Release 567/567 和 0 warning 构建通过；415 文件候选已生成，待 100%/200% DPI 与真实拖入人工 Gate。schema、设置、Router、真实数据库、KI-001 和默认 PID 17900 未改。
- 用户要求启动 Card 实例后，再次核对候选路径与 DLL 哈希，精确停止默认 Release PID 17900，并启动独立候选 PID 15852；启动后唯一 Vok 进程、加载路径和 `Vok.dll` SHA-256 均与候选一致。
- 用户随后要求继续重设计中转站卡片，视觉参考 Workbench，并覆盖悬浮、内部拖动与外部拖入效果；排序功能明确暂停。源码/截图分析确认数据层、行虚拟化与管理动作可保持，差距只在默认强卡面、常驻操作、小 glyph、缺少 Hover/drag ghost 和外部 Drop 提示层。新增 `docs/M3_STAGE_CARD_REDESIGN_PLAN.md`，把静态、Hover/键盘、隔离 drag source/ghost、外部 DragActive/Targeted 和候选验收拆为 Gate 0–4；正式产品不暴露松手无提交的假排序手势，Core/SQLite/Router/KI-001 均不进入本轮。
- Redesign Gate 0 进一步完成可实施规划：192 DPI Workbench 图标实测约 46 DIP，Vok 首候选取 42 DIP；卡片内部按 28/42/30/16 DIP 固定行分配，左侧继续保留后续排序槽、pin/menu 保持右侧，纠正了总计划初稿把 pin 放左侧会占用排序命中区的问题。compact 状态缩写只进入 App VM，完整 Automation 语义不变；实施拆为呈现属性、静态 XAML、结构截图门禁和完整回归四步，本轮仍未改产品代码或运行候选。
- Redesign Gate 0 随后完成产品实施：App VM 增加 compact 状态投影，静态卡片改为透明外表面、42 DIP Accent 图标、两行 Medium 名称和单行组合状态；左侧隐藏排序槽、右侧 pin/menu、管理命令、Automation、布局/虚拟化、Router 与数据层保持。定向 34/34、完整 Release 571/571、构建 0 warning / 0 error 和差异检查通过；浅深及 260/520/720/800/960 DIP 截图生成，42 DIP 无需回退。旧 PID 15852 未替换，下一步仅做用户静态视觉 Gate。
- 用户要求替换后，将当前源码发布为 259 文件的独立 win-x64 自包含 Gate 0 候选；首次 `--no-restore` 因 assets 尚无 win-x64 target 明确失败，改为正常运行时还原后发布成功。核对路径与哈希后精确停止旧 PID 15852，启动 PID 20160；当前唯一 Vok 进程、实际加载 DLL 路径和 SHA-256 `C2BF3A66A976F3970368D2E5D9D7CD7668DB96E68FD16C20D7EA600FADD2F3E4` 均与新候选一致。
- 用户以 Workbench 中转卡浅/深截图明确判定第一版效果不通过，要求卡高与 Vok 启动台一致、宽度重新商榷并先只确立静态样式。只读核对 Workbench `App.css` 的 72/6/9、62+32 两段结构及 Vok 当前 1600×952 DIP/1:2:1 几何后，确定 72 DIP 固定宽、6 DIP gap、最多 10 列正好适配约 784 DIP 真实 Stage viewport；高度改为消费与 Launcher 同一 surface/unit rhythm，而非 Stage 独立常量。新增 V2 权威计划并标记第一版为视觉否决；未改产品代码、业务/后端或运行实例，PID 20160 已由用户侧结束。
- V2 静态样式随后完成实施：横向 profile 收敛为 72/6/10，MainWindow 把 Launcher surface/unit 直接绑定给 Stage；卡面改为上部中性预览 + 下部 32 DIP 名称/meta，40 DIP 图标框和 24 DIP 覆盖式 pin/menu，旧管理带/排序占位删除。聚焦 80/80、完整 Release Core 74/74 + Integration 502/502 共 576/576、0 warning / 0 error 与差异检查通过；1600 DIP 同窗十列和等高截图已核验。本轮未改业务/后端、Router、真实库、KI-001 或运行实例，下一步只等用户静态视觉 Gate。
- 用户首轮视觉 Gate 指出 Stage 卡片整体未与 Launcher 对齐。新增绝对窗口 Y 坐标门禁稳定复现 Stage 比 Launcher 高 3 DIP：高度和 row unit 虽相同，但 Stage 顶对齐而 Launcher 行内上下各留 3 DIP。改为 Stage 卡面在共享 row unit 内居中后坐标相等；隔离 Release 576/576 与新 1600 DIP 截图通过。源码 Release PID 21652 由外部启动且占用默认输出，本轮未停止、替换或把它冒充修复候选。
- 2026-09-09 用户确认 V2 静态卡片及整体纵向对齐视觉 Gate 通过，并要求提交；Hover、拖动、外部 Drop 新效果、排序和缩略图继续暂停。
- 用户随后确认先完成 UI/交互基础再继续功能。Redesign Gate 1 实现 Hover/KeyboardFocus 的统一状态、Subtle surface、1.02 scale、ZIndex、操作 chrome 和 Accent focus ring；定向测试发现并修复 FocusWithin 退出与 ContextMenu 进入动画竞争，最终由单一卡片交互状态唯一驱动画面。外部 drag、隐藏、换绑、回收及 100 轮快速聚焦/抑制/换列/滚动/显隐均零残留；浅色 Hover/焦点、暗色、260 DIP 截图和隔离 Release 576/576、0 warning / 0 error、`git diff --check` 通过。等待用户实际视觉确认，未发布或替换 PID 352。
- Gate 1 提交为 `e40193e`。用户澄清只有该项已实现，其他路线项无法测试；后续依赖分析发现纯拖影没有产品终态、V2 72 DIP 卡也无法无冲突容纳第三个 24 DIP 排序把手。新增待确认方案 `docs/M3_STAGE_UI_FOUNDATION_NEXT_PLAN.md`：先以现有真实 Router 完成 Gate 3，再用显式排序模式把 Gate 2 与 Checkpoint 6 合并为真实可测闭环；未修改产品代码、既有决策或运行实例。
- 用户确认上述调整。Gate 3 以既有 Router 状态扩展为 2 DIP 边界、0.08 Accent wash 和明确松手文案；浅色 260 DIP、深色 520 DIP 截图、完整隔离 Release 576/576 与零警告构建通过。随后精确停止旧默认 Release PID 20760，重新构建并启动唯一 PID 22220，加载 DLL SHA-256 为 `C0A514F019BDB87CEBCC8A8D7ECCB21A9192C0084F6C1C79AA94CE1BD61E3B1D`；用户真实 Explorer/桌面拖入 Gate 通过。下一步做不含拖动手势的排序模式壳层。
- 2026-09-11 排序 S0–S2 数据基础完成：Core 复合邻接与分页边界、SQLite 单项快路及最多 64 项局部重平衡均落地；密集 2/63/64 成功、65 拒绝、成员溢出/后续 SQL 失败回滚、10,000 条有界索引与源摘要门禁通过。完整隔离 Release 606/606、零警告构建；未接 UI、Router 或 KI-001。随后按用户要求停止旧 PID 5576，以最新默认 Release 输出启动唯一 PID 1512；用户确认现有可见能力的必要人工回归通过，当前仍无 UI 路径触发排序写入，下一步合并同页真实排序纵向切片。
- S3 随后完成自动实施：ViewModel 同页会话、显式模式、整卡系统阈值、真实二维几何、拖影/目标轮廓、页内滚动和 Esc/设置/隐藏/失捕/卸载/换列/外部 DragEnter 取消链均落地；提交开始后视觉取消不撤销或重放仓储写入。排序专用权威更新同步最多 64 个返回项和分页 cursor，且不因纯顺序 revision 变化重启 Availability。换列时捕获旧投影滚动锚点导致首行偏移的问题经 260 DIP 截图复现并修正。完整隔离 Release 634/634、零警告构建和差异检查通过；PID 1512、真实库、schema 7、Router 终态和 KI-001 未改，下一步只生成/启动 S3 候选并完成真实同页物理 Gate。
- 用户完成首轮 S3 基本 Gate 后否决交互效果：显式模式不是预期，拖动卡顿且动画跳变。源码追到每跨槽 `Items.Move` 会在 UI 线程触发行投影全量发布、容器回收、Availability 调度和同步布局；修订删除模式与目标轮廓，普通卡面直接捕获但排除 pin/menu，预览零集合通知，缓存拖卡跟手并以邻位 transform 让位，松手才单次移动/提交。完整隔离 Release 637/637、零警告构建与布局截图通过；精确停止旧 PID 2440 后构建默认 Release，当前唯一 PID 3316、响应和加载路径正确，DLL SHA-256 为 `66691FE22463A340969B77DD699F7654F5153874EA36BE1BFAAE330A8339904A`。用户随后确认基本效果符合预期并授权提交，S3 物理观感 Gate 完成，下一步进入 S4 跨页排序。
- S4 自动候选虽通过隔离门禁，但真实效果被用户否决；源码、测试与对应状态记录已回退到 `c803954`，回退版 637/637 和零警告构建通过。用户随后重审路线：Stage 固定为最多 50 条的单窗口引用工作台，取消分页、跨页排序、200 条扩容和点击取用前置，优先闭合拖入—直接排序—Copy-only 拖出；持久化继续使用 schema 7/revision/稀疏顺序，动画热路径要求零数据库、Shell、Availability 和集合通知。§32 与聚焦路线已记录，下一步 F0 移除产品分页后执行 F1 OLE Gate。
- 2026-09-12 用户纠正上一轮收窄：50 只是当前策略而非永久上限，架构必须支持超大容量且低内存/高流畅/良好存储性能；同一路径允许多个独立 occurrence；单项/批量拖入和拖出均为核心。“取消分页”只取消用户可见页码和整页跳转，内部 keyset、双向预取、窗口淘汰与数据虚拟化反而是硬要求。源码审计确认 schema 7 canonical 唯一索引和 `AddOrMoveAsync` 会去重、Stage 仍有 64 项页面导航且无拖出/多选，因此当前只算合格地基，不符合完整标准。§32 与聚焦路线已改为 N0 schema 8 occurrence → N1 连续数据虚拟化 → N2/N3 单项/批量拖出。
- 用户补充长时间后台未用后呼出迟钝、Stage 短暂空白的物理缺陷并要求尽早处理。源码证明 Stage 只在 App 启动时加载 SQLite；WPF `Window.Hide/Show` 隔离实验也证明 Hide 不触发子树 Unloaded，因此不是每次呼出重建 VM/重查数据库。真实库只读统计为 46 条（33 `.lnk`、0 UNC），暖态文件属性顺序检查 16.4 ms。实际 PID 跨过两次 Modern Standby，宿主没有 Resume 内容预热；揭示门禁不消费 `StageView.HasPendingDeferredWork`，而 Stage Hide 会 reset Availability、Show 后为每个可见卡排队 Shell 检查和 UI 属性通知。结论是冷 WPF/D3D/工作集触发加上 Stage 准备合同缺口与可用性通知风暴，精确占比须以 P0 分段遥测后确定；当前不改产品代码或实例。

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
