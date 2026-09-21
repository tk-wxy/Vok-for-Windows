# M3 中转站聚焦路线：大容量无可见分页拖放工作台

> **状态（2026-09-21）**：用户已确认当前产品容量可暂设为 50，但架构必须支持超大上限并保持流畅、低内存和良好存储性能；同一路径允许重复出现；拖入、拖出及其批量操作是核心。本文取代 2026-09-11 的“固定 50 条、一次全部加载”推导。删除的是用户可见分页、页码和跳页体验，不是内部 SQL 分页与数据虚拟化。前置 P0 呼出修复已达到用户预期；N0 occurrence/schema 8、N1.1 双向 keyset 与 N1.2 纯 rolling window 已部署且最小人工 Gate 通过。N1.3A/B 已完成源码验证但尚未正式部署；N1.4A 隔离自动规模基线和 N1.4B 最大化交互检查器已完成源码验证，当前 3200×2000、200% DPI 硬件的 10k/100k 用户双向排练通过，精确 4K/100% 用户物理手感与数值阈值确认仍待执行。

## 1. 产品定义

中转站是一个**持久化文件引用、支持重复 occurrence、无用户分页的大容量拖放工作台**：

1. 从 Explorer/桌面拖入单个或批量文件、目录、`.lnk`；
2. 在中转站内直接拖动排序；
3. 从中转站向 Explorer/桌面或其他文件接收目标拖出单个或批量引用；
4. 跨重启保存每个 occurrence、顺序和固定状态；
5. 无论数据量多大，UI 只保留视口附近的有界对象和视觉资源。

当前默认容量是 50，只是产品策略和近期验收档位。存储与 UI 架构最低以 10,000 条作为完整功能门禁，并在宣布“超大上限可用”前加入 100,000 条同机压力门禁；提高产品容量不得迁移 Stage 聚合或更换主表。

产品不显示“较新/较早”、页码、Load More 或整页跳转。用户只看到连续滚动的卡片流；内部允许并且必须使用 keyset 分页、前后预取、窗口淘汰和数据虚拟化。

## 2. 数据身份与持久化

### 2.1 occurrence 是唯一业务身份

同一路径可以重复，每一次放入都是独立 occurrence：

```text
StageOccurrence
├─ Id                         # 每次放入独立生成
├─ TargetKind / TargetValue
├─ CanonicalKey               # 可查询，不唯一
├─ ResolvedName
├─ SortOrder
├─ IsPinned
├─ Revision
└─ CreatedUtc / UpdatedUtc
```

- 同一批中两次出现相同路径，保存两条；不同批再次拖入也新增一条。
- canonical key 只用于路径比较、Missing/重新关联辅助和索引查询，不再决定去重或回顶。
- 删除、固定、重新关联、排序和拖出后消费都只按 occurrence `id + expected revision` 操作，不能按路径批量误伤同源卡片。
- 批量容量按 occurrence 数量计算；50 条时再拖入 3 条需要三个空位，重复路径同样计数。

### 2.2 schema 迁移

当前独立候选与真实产品库已运行 schema 8；产品路径使用 `AddBatchAsync`，不再按 canonical 去重或回顶。N0 的持久化合同是：

- 在单事务 schema 8 迁移中删除 canonical 唯一索引，建立普通 `ix_stage_items_canonical(canonical_key COLLATE NOCASE)`；
- 保留已有行、id、顺序、固定、revision 和时间，不合并、不复制、不删除数据；
- 用新的 `AddBatchAsync`/等价契约替换产品路径上的 `AddOrMoveAsync`，按输入顺序原子插入全部 occurrence；
- 空间不足、任一 descriptor 无效、revision/算术或 SQL 失败时整批零写入；
- 结构核验同时检查 schema version、列集合、canonical 非唯一索引和顺序索引，未知结构原样拒绝。

### 2.3 顺序与事务

- SQLite 是 occurrence 存在性、固定、revision 和顺序的唯一权威，UI 不维护第二套可恢复真相。
- 顺序继续使用 `(sort_order DESC, id DESC)` 和 64 位稀疏键。普通排序只更新移动 occurrence；间隔耗尽时只重平衡有界窗口。
- 批量排序若未来进入，只把选中 occurrence 作为稳定块处理；当前优先保证单项直接排序，不把批量排序混入批量拖出。
- 内部写入都由唯一 `SqliteStorageExecutor` 在 WAL 下串行执行；鼠标移动和渲染帧不访问 SQLite。
- Stage 只保存引用；任何迁移、消费或清理都不得删除、移动、改名或覆盖用户源文件。

## 3. 无可见分页的大容量 UI

WPF 容器虚拟化只能减少视觉容器，不能阻止全部业务对象常驻。大容量标准必须同时具备数据虚拟化：

- UI 暴露连续滚动集合，不暴露页面索引、页码、较新/较早按钮或整页替换动画。
- 仓储使用双向复合 keyset 窗口，不使用 `OFFSET`；窗口大小和前后预取距离是命名参数，由测量决定。
- ViewModel 只保留当前窗口、相邻预取窗口和必要的轻量锚点；离开窗口的 VM、Availability 请求、图标和动画状态可回收。
- 预取在接近窗口边缘时异步发生，加载结果以 window generation + anchor 校验；迟到结果不能覆盖新窗口。
- 淘汰旧窗口时用稳定 id/anchor 保持像素滚动位置，不进行整页闪跳或回到顶部。
- 总量和滚动深度不应线性增加常驻 VM、图标、事件订阅或 Availability 工作。

排序跨越内部数据窗口时仍表现为连续滚动：先预取后扩展可排序窗口，拖卡与邻位视觉不中断；禁止重新引入 S4 的“到边缘后突然换整页”。默认 50 条验收不依赖该路径，但 10,000 条门禁必须证明窗口常驻量有界。

## 4. 核心交互

### 4.1 单项与批量拖入

- 沿用 `MainWindow` 唯一 `AllowDrop` 和 `WorkspaceFileDropRouter`；Launcher/Stage 不各建 OLE lease。
- `DragEnter/DragOver` 只探测格式和更新反馈；外国 `IDataObject` 只在 Drop 终态快照一次。
- 一次 FileDrop 中的所有路径保持来源顺序，并在一次 SQLite 事务中插入；不在 UI 先显示再补写数据库。
- 入站只声明 Copy，只保存引用；成功、失败、取消和超限均不修改源目录。

### 4.2 单项排序

- 保留已通过观感 Gate 的 S3：卡片非按钮区域超过系统阈值后直接排序，pin/menu 不触发。
- 鼠标移动期间集合不变；拖卡跟手和邻位让位只使用合成属性。松手后才执行一次集合应用和一次 SQLite 事务。
- 顶部/底部做连续像素自动滚动；内部窗口加载对用户不可见，不产生页跳。
- Esc、隐藏、设置、外部 DragEnter、失捕、换列和 Dispose 进入一个一次性取消终态；Committing 不被视觉清理伪装成回滚。

### 4.3 选择与批量拖出

批量拖出需要显式但标准的 selection model：

- 无修饰点击/拖动：单卡手势；Ctrl 点击切换 occurrence；Shift 点击选择当前已加载连续范围。
- 选择是会话级 UI 状态，不持久化；窗口淘汰后只保留选中 occurrence 的轻量 `id + revision`，不保留整卡 VM。
- 从已选卡开始拖出时，载荷包含全部选中 occurrence；从未选卡开始时只拖该卡。
- 当前阶段不实现“多选块内部排序”；存在多选时，组拖动只承担批量拖出，避免一个手势同时猜测批量排序。
- 重复路径可以出现在选择中。Vok 保留 occurrence 次数与顺序，但外部 FileDrop 目标如何处理同一路径重复、同名冲突或部分文件，是目标应用能力；不得把目标行为伪装为 Vok 能逐项确认。

### 4.4 拖出与手势仲裁

单卡首选交互假设仍是：

```text
按下卡片非按钮区域
  → 超过系统阈值
  → Stage 内：单项排序
  → 横向越出 Stage：取消排序预览并单向升级为 OLE 拖出
```

多选组从选中卡开始拖动时直接进入出站候选，不启动内部排序。单项/批量共同遵守：

- OLE 数据为 `FileDrop + Preferred DropEffect(COPY)`，allowed effects 只有 Copy，并携带 Vok 进程内 token 防止自身 Router 重吞。
- 升级为 OLE 后同一次按压不能退回内部排序；进入 `DoDragDrop` 前清理内部 transform、鼠标捕获和 render 状态。
- 全屏源 HWND 的隐藏时机必须先经隔离 Gate 验证；不得用固定延时猜测 OLE 已开始。
- 若横向越界方案或 WPF `DragDrop.DoDragDrop` 物理 Gate 不成立，停止并让用户重新确认手势/实现分支；不能静默改成长按、右键或隐藏按钮。

### 4.5 批量交付后的保留

- `DoDragDrop` 返回 Copy 只提供整个 OLE 会话的结果，不提供可靠的逐 occurrence receipt。
- 当前合同仍是 Copy 成功后消费未固定项、固定项保留。批量清理必须在一个 SQLite 事务中核对全部选中 id/revision；任一冲突时不做部分删除，整批保留并报告“已拖出，但未自动清理”。
- None、Esc、目标拒绝、Missing、OLE 异常或结果不明确时全部保留。
- 外部 Copy 与 SQLite 无法原子提交；进程在 Copy 后、清理前退出时允许重启后多保留引用。不得凭本地日志自动重放外部拖出。

点击取用、`CF_HDROP` 剪贴板写入和粘贴注入不作为当前完成条件；拖放闭环归档后再由真实反馈评审。

## 5. 动画与资源标准

拖动热路径的硬约束：

- 零 SQLite、零 Shell/COM、零同步文件检查、零新 Availability 请求、零 `Items.CollectionChanged`；
- 只保留一个单卡或组拖影视觉；已实现卡片只改 `TranslateTransform`/透明度，不创建额外 HWND；
- MouseMove 只写最新指针/目标，实际视觉合并到一个活动期 render pump，每帧最多计算一次；
- 卡片边界只在拖动开始、真实滚动、数据窗口扩展或布局代次变化时更新；
- 邻位目标不变时不创建新 Storyboard/AnimationClock；只有测量证明现有时钟方案存在分配或卡顿，才替换已通过的 S3；
- 终态后鼠标捕获、render 订阅、动画时钟、拖影 DataContext、OLE token/data object 和临时 selection snapshot 全部释放；空闲计数必须归零。

规模门禁分层：

- **50 条功能档**：完整拖入、排序、拖出和重启物理 Gate；
- **10,000 条工程档**：连续滚动、预取/淘汰、随机长距离滚动、排序和批量 1/10/100 项拖出，常驻 VM/内存不随总量线性增长；
- **100,000 条压力档**：数据库启动、窗口读取、插入、批量事务和滚动资源曲线；数值阈值按 R9 先做同机基线，再由用户确认。

## 6. 当前实现审计

| 能力 | 当前状态 | 结论 |
|---|---|---|
| 分层、SQLite executor/WAL、schema 核验 | N0.1 schema 8 严格迁移已部署并通过真实库审计 | 合格基础 |
| keyset 顺序索引、64 位稀疏排序、有界重平衡 | 已有且有 10k 门禁 | 合格基础 |
| 外部单项/批量 FileDrop 入站 Router | 已有并保留来源顺序与重复项 | 入站路由与写入合同合格 |
| 重复文件 occurrence | schema 8 非唯一索引、`AddBatchAsync` 与 ViewModel 纵切已部署 | 自动与人工 Gate 合格 |
| 无可见分页连续大数据滚动 | N1.3B 已删除页脚，接入双向触边、像素锚点和按视口只增窗口；N1.4A 自动规模与 N1.4B 交互检查器门禁通过；本机 10k/100k 人工双向排练通过 | 源码链与本机排练合格，精确 4K/100% 用户物理 Gate 待执行 |
| 单项直接排序及零集合预览 | S3 已通过用户 Gate | 当前 50 条下合格 |
| 大数据窗口间连续排序 | 未实现；S4 整页切换已否决并回退 | 未达标 |
| 单项拖出 | 未实现 | 未达标 |
| 多选与批量拖出 | 未实现 | 未达标 |
| 外部交付/批量清理一致性 | 只有设计，无实现 | 未达标 |
| 空闲/拖动/10k/100k 资源曲线 | 50/10k/100k 首份隔离基线已取得；数值 SLA 尚未由用户确认 | 未完成 |

因此当前中转站不能称为“按最终产品标准完成”。更准确的结论是：**工程基础、重复 occurrence 入站合同和 50 条单项排序已经成立，但连续大容量 UI 与单/批量拖出两条核心链路尚未达到新标准。**

## 7. 实施顺序

### P0：冷启动、长空闲与睡眠恢复呼出门禁

具体实施、根因 A/B 和验收合同见 `M3_STAGE_P0_REVEAL_PLAN.md`。截至 2026-09-14，P0 已完成：Stage 隐藏期预建并保留有界投影，以同代内容/布局/状态 token 门禁 render-return 与 attempt 唯一的 DWM fence；长期隐藏 HWND 再次可见时显式使根视觉失效；启动期在 cloak 内、不激活且不置顶地一次性 materialize 真实视图；Availability 只在成功呈现后以物理在途上限 4 复查，Hide 保留状态，拖动期间暂停。完整 Release 662/662、构建 0 warning / 0 error。

5 分钟 A/B 已复现并定位原缺陷：没有根视觉失效时，即便 22.6 ms 内容就绪、32.9 ms 解除 cloak、揭示前零 Availability，请求后的首图仍只有背景；只增加根视觉失效后，同条件约 32.4 ms 解除 cloak、首张与稍后截图完全一致且为完整 46 项。说明长期隐藏可让 DWM surface 丢失而 WPF retained tree 仍保持有效，旧的通用 Rendering + `DwmFlush` 不能单独触发真实重绘。最终快速 100 轮为 100/100，稳定外部用户可见 P95 约 109.8 ms；最终精确构建另跑 10/10，30 秒隐藏后 25.3 ms 内容就绪、36.8 ms 解除 cloak、47.8 ms 输入就绪，首图完整且揭示前 Availability 为 0。

P0.5 的最终候选经用户长静置确认“呼出无缓慢、中转站无白屏”，达到 P0 预期。正常 Show 自足，不依赖 Resume 预热，也不增加周期唤醒、固定延时、空白遮罩、强制 GC、全量图标缓存或长期可见隐藏窗。冷/热细微差异与 Clipboard 图片短暂空白仅登记为 KI-002/KI-003；独立纯 30 分钟、明确 Modern Standby/Unlock 分档和正式 Present 仍是证据边界，不冒充逐项通过。下一步进入 N0。

### N0：合同与 schema 8

截至 2026-09-15，N0 已完成、提交并随独立候选部署。N0.1 以单事务完成 schema 7→8 canonical 普通索引迁移，并通过真实副本 7→8→8、schema 8 幂等及派生 6→8/历史 5→8 回放；首次真实部署前又创建稳定且逻辑精确匹配的在线备份，候选启动后真实库升为 schema 8，canonical 普通索引生效，八张业务表摘要、46 条 Stage 与 67 个 Ready 资产全部保持，`quick_check=ok`、0 外键/资产异常。N0.2 用原子 `AddBatchAsync` 取代 `AddOrMoveAsync`，每个 descriptor 独立生成 occurrence，重新关联也只按 `id + revision` 而不拒绝重复 canonical；49/50/51、同批/跨批大小写重复、schema 7 升级后立即重复写、SQL 中途失败回滚、非法 descriptor、源摘要、Router/ViewModel 纵切均有自动门禁。

- 冻结 occurrence、重复路径、批量原子性、当前 50/大容量架构和无可见分页合同；
- 迁移 canonical 唯一索引为普通索引，接 `AddBatchAsync`，更新 Router/ViewModel；
- 覆盖同批/跨批重复、大小写相同 canonical、49/50/51、迁移保真、失败回滚和源摘要；
- 不接拖出和 UI 数据窗口，先把业务身份改正确。

### N1：连续数据虚拟化

截至 2026-09-21，N1.1 仓储地基与 N1.2 纯 Core `StageRollingWindow` 已完成、验证并随候选部署：双向复合 keyset 统一返回 DESC 顺序和双侧紧邻边界，不使用 `OFFSET`；窗口默认以 64 项一段、3 段/192 项工作，并以实例/generation/方向/cursor/接缝拒绝迟到结果。N1.3A/B 已完成源码验证但未部署：`StageViewModel` 以唯一 rolling window 增量维护 retained VM，WPF 删除可见分页，以投影变更前 delta 和行发布后的 `ContextIdle` 完成双向触边与像素锚点；视口很高时只按“可见项 + 一整段缓冲”扩展窗口。N1.4A 新增 create-only 隔离 SQLite/真实 WPF 探针和编排器；首次基线暴露每次 edge 都 `Rows.Clear()` 重建全部行容器，SQLite/VM 已在约 120–256 ms 发布，而锚点稳定要约 633–854 ms。行对象及其可观察 item slots 改为同列数下增量复用，Availability 在 edge 期间取消并于锚点稳定后恢复；256 项 P50 由约 871 ms 降至约 439 ms，1k 的 27 次往返 P50 约 492 ms且锚点误差为 0。正式同机基线随后完成 50 条、10k 全量双向 309 次和 100k 固定种子 512 次压力：10k/100k 最大 VM 192、最大已实现卡片 110，全部 821 次 edge 均有锚点测量且最大误差 0 DIP，顺序/窗口/隐藏收口及隔离库、源文件摘要均通过。100k 的 64-step 分段均值中 Managed Heap 约 27–28 MiB、GDI 恒 7，Handle/Thread/USER 回落；Private 从约 190.9 MiB 缓慢到末段 197.8 MiB、Working Set 从约 257.4 到 261.0 MiB，只记录为首份基线，不据此虚构硬 SLA。N1.4B 又新增独立 `inspect` verb 与 create-only 编排器：最大化真实 Stage 窗口供用户双向滚动，被动记录预取起止、投影/Items/Rows/视口里程碑、像素锚点、窗口/已实现数量和资源曲线；关闭窗口后等待预取、Dispatcher 与 Availability 全部归零，并把客观检查、精确 3840×2160/96 DPI 环境资格和 `UserVerdict=NotCaptured` 分开报告。首次人工排练复现检查器仍可经生产拖动重排隔离库，现以检查专用 ViewModel gate 和只读仓储双层拒绝全部 mutation；连续用户滚动还证明 Background 采样会被下一批 Input 污染，现改为布局完成后的 Render 优先级采样。最终 Release 114/114 + 601/601 共 715/715，探针 7/7，高风险 12 项连续五轮共 60/60；10k/100k 自动关闭各 64/64 锚点为 0 DIP。当前硬件手工 10k Older/Newer 8/8、100k 9/7，二者均为 19/19 锚点且最大误差 0 DIP，顺序、192 项窗口、关闭收口与隔离摘要通过；用户确认拖动已禁用且其余正常。当前机器不是精确 4K/100%，因此 N1.4 尚未整体通过。

- 移除页脚和页面导航体验，但保留/扩展内部双向 keyset；
- 建立连续滚动虚拟集合、前后预取、窗口淘汰、锚点恢复和 selection 轻量身份；
- N1.3A 只替换 ViewModel 分页权威并按 added/evicted delta 保留窗口内 VM 身份；N1.3B 再改变 WPF 滚动行为，两步均已完成自动验证但未部署；
- N1.4A 已在隔离高容量候选中完成 10k/100k 常驻量、随机滚动与首份资源曲线；N1.4B 检查器及当前硬件 10k/100k 人工排练已完成，下一步只在精确 4K/100% 环境执行最终用户物理手感与阈值确认；
- 不重复 S4 的整页替换和边缘跳页。

### N2：单项/批量出站 OLE Gate

- 隔离验证单卡与 1/10/100 项 FileDrop、重复路径、WPF 源窗隐藏、Explorer/桌面 Copy/None/Esc、自吞拒绝和重新显示；
- 固定 PID/HWND 记录 Deactivated、Topmost、Workspace generation、线程、句柄和源目录摘要；
- Gate 未通过前不接正式消费。

### N3：正式拖出与持久化收口

- 加入 selection、单项/批量 DragOut generation、Copy-only 数据对象和一次性终态；
- Copy 后用单事务核对并清理全部未固定 occurrence；冲突/失败整批保留且不重放；
- 覆盖 Missing、固定混合、重复路径、旧代、隐藏/退出和提交后 UI 刷新失败。

### N4：排序与动画统一

- 保留 S3 单项手感，在连续数据窗口中完成无页跳的自动滚动与远距离排序；
- 用一个活动期 render pump 合并拖影、邻位和滚动；以证据决定是否替换现有 DoubleAnimation；
- 完成 1,000 次预览、100 次排序/取消、20 次 OLE 成功/取消和 100 次显隐资源门禁。

### N5：文件型 Stage 归档

- 完成单项/批量拖入 → 连续滚动/排序 → 重启 → 单项/批量 Copy-only 拖出 → 正确保留/消费；
- 50 条用户 Gate、10k 工程 Gate、100k 压力 Gate及源文件零修改全部通过；
- 点击取用、文本/图片、标签/分组和批量排序继续后置，不以分页或扩容尾项重新打开架构。

## 8. 完成定义

只有同时满足以下条件，才可称为符合当前中转站标准：

- 同一路径可作为多个独立 occurrence 存在、排序、固定、移除和批量拖出；
- 无用户分页的连续滚动在高数据量下不全量常驻，也不发生整页跳变；
- 单项/批量拖入与拖出均以真实 Explorer/桌面完成 Copy-only 闭环；
- SQLite 迁移、批量写入、排序、重启和交付后清理保持事务与 revision 一致性；
- 拖动热路径无数据库/Shell/文件检查/集合重建，终态后所有临时资源归零；
- 50 条真实手感、10k 工程规模和 100k 压力门禁都通过。
