# Stage P0：呼出无空白加载修复计划

日期：2026-09-13，2026-09-14 更新。状态：**P0 已达到用户预期并完成验收**。P0.1–4 已提交；P0.5 一次性初始 surface materialization 已通过完整自动门禁、首次/30 秒/5 分钟首帧样本与用户长静置观感。用户观察到的冷/热细微差异和剪贴板图片短暂空白分别登记为 KI-002/KI-003，不扩展本阶段范围；P0.5 源码尚待提交。

本计划细化 `M3_STAGE_FOCUSED_ROADMAP.md §7 P0`，先于 N0 执行。目标是 Stage 在首次、热态、长期隐藏和睡眠/解锁恢复后，快速呈现已有内容；第一张用户可见帧不先出现空 Stage，再逐步补出卡片。

## 1. 验收合同

- 数据已初始化时，首次用户可见帧包含当前视口应有的卡面、名称、类型图标、固定状态和正确顺序；滚动位置不在出现后再跳动。只要求当前视口，不要求全部条目实现容器。
- 合法空库呈现空状态；已确认的数据读取失败呈现错误状态。二者是明确终态，不能与“有数据但视图尚未准备好”混为一谈。
- 卡片静态显示不等待 Availability、文件系统或 Shell；文件状态可以随后局部更新，不能让卡片消失、重建整区或反复闪动。
- 同时评价按键到首次完整内容的总时延、首次揭示到完整内容的空白窗口、输入就绪和首个动作响应。延长不可见等待仅能消除部分中间帧，不能单独算性能修复通过。
- 普通重复呼出不查询 Stage SQLite，不执行同步文件检查；准备、缓存和刷新工作受当前数据窗口/视口限制，不随全库条目数增长。
- 本轮保持 schema 7 和当前排序、管理、入站合同；不实施 N0 occurrence、N1 连续数据虚拟化或拖出。必要的宿主接缝必须回归 M0 显隐/焦点和标准外部拖入；触及活动拖动中反复显隐时重新打开 KI-001。

## 2. 已核实机制与证据边界

| 发现 | 源码位置 | 对缺陷的意义 |
|---|---|---|
| 启动期先等待 Stage 初始化，再创建 MainWindow；普通 Show 不调用 Stage 读取 | `App.xaml.cs OnStartup`、`StageViewModel.InitializeAsync`、`WorkspaceController.Show` | 普通呼出空白没有“每次重载数据库”的调用链；不能据此排除启动或真实数据变更时的读库成本 |
| 名称、顺序、固定和类型 glyph 直接来自 StageItem/VM | `StageItemViewModel`、`StageView.xaml StageCardContentTemplate` | 显示卡片不依赖文件可用性，更没有逐张 Shell 图标下载；Availability 单独不能解释整区无卡片 |
| Stage 仅在 Loaded 后接入行源；投影异步发布；滚动归顶/锚点恢复更晚执行 | `StageView.OnViewLoaded/AttachObservedViewModel/OnCardRowsPublished`、`StageCardRowsProjection` | 首次投影、真实卸载后重挂、换列或数据替换存在内容准备窗口；普通 Hide 是否引发重新挂接，须用实际生命周期事件判定 |
| 首个 Rendering 后安排 Background fence；fence 成功就解除 cloak | `WorkspaceController.AttachFirstFrame/CompletePresentationFence` | 宿主未核对 Stage 数据/布局代次、视口容器与滚动稳定状态；通用帧不能证明 Stage 已完整 |
| 隐藏取消绑定时同时 ResetAvailability；显示后在 Loaded 优先级遍历视口并逐卡 Inspect | `StageView.OnViewIsVisibleChanged/CancelAllAvailabilityBindings/EnsureAvailabilityBinding` | 结果被无条件丢弃，重新创建任务和派生属性通知；是确定存在的额外工作，耗时占比尚未测出 |
| Stage Inspect 使用共享、容量 128 的 Shell STA；认领执行后取消不能终止原生同步调用 | `WindowsStageFileService.InspectAsync`、`WindowsShellStaExecutor.WorkItem.Execute` | 慢本地盘/设备检查可能占用共享队列；UI 异步不等于没有排队成本，取消也不能假定正在执行的 I/O 已结束 |
| UNC 返回 Unknown；映射网络盘先判 DriveType，不直接 GetAttributes | `WindowsFileAvailabilityInspector.Inspect` | 不能把“断网 UNC 长时间检查”当成已证实根因；DriveType 和本地设备耗时仍需分开测量 |
| Resume/Unlock 当前只触发托盘登记检查 | `WpfTrayIconService` | 尚无 Stage 恢复观测与专用内容准备路径；不代表增加预热一定有收益 |

WPF 优先级中，Stage 的 `ContextIdle` 回调低于宿主 `Background`，fence completion 又投递到 `Normal`。因此现有队列优先级没有建立“Stage 滚动/布局完成 → 揭示”的先后保证。`Rendering` 本身在实际帧绘制前调用，也不是屏幕内容已出现的证明。依据：[DispatcherPriority](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherpriority?view=windowsdesktop-10.0)、[CompositionTarget.Rendering](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget)。这是机制风险分析，不是本次长空闲缺陷的完整动态复现。

2026-09-12 的现场记录（46 条、暖态属性检查约 16.4 ms、两次 Modern Standby）只作为历史线索。本轮没有重新采集长期隐藏/恢复样本，不能据此给各因素排序或把 WPF/D3D 冷恢复定为已证实主因。

## 3. P0.1：分段观测和可重复失败样本

第一笔实现只增加观测与回归夹具，保持产品行为，避免一边改机制一边失去基线。

记录同一单调时钟下的 `WorkspaceGeneration`、内容版本、布局版本及以下时间点：

1. 原生热键收到、UI 收到请求、开始 Show、Show 返回。
2. Stage Loaded/Unloaded、源绑定/解绑、行发布；未发生的事件保持缺席，不能每次呼出伪造一次行发布。
3. 实际 viewport、已实现卡片数量/身份、布局/滚动待处理、静态内容就绪、通用 Rendering、fence 请求/返回、解除 cloak、输入就绪。
4. Availability 的调度/入队/开始/完成/取消、队列等待与执行耗时、UI 应用次数；共享 Shell 队列区分 Stage 与其他调用方。
5. 本轮隐藏时长、最后一次 Resume/Unlock；进程工作集、私有内存、page faults 与 GC 差值在关键帧外采样。普通 page fault 计数不能直接归因为磁盘硬缺页；必要时再用 ETW 归因。

观测采用有界会话记录，热点只记录时间戳/计数；批量导出和进程采样放在完成后的后台阶段。不在 Rendering 中遍历视觉树、抓图或发布大量诊断属性，不在空闲期维持渲染循环。

测试补齐两层证据：

- 受控 WPF/宿主测试：刻意让行发布、滚动恢复或文件检查未完成，在揭示点断言内容状态。不能先 Pump 到 ApplicationIdle 再把最终完成冒充首次可见正确。
- 固定候选 PID/HWND 的外部探针：采样实际 Stage 卡片区域，比较首次可见与后续静态稳定内容。旧探针只有窗口三点，可能全部落在背景上；新探针需要卡面/名称覆盖与空库区分，动态状态、鼠标悬浮和动画区域不参与静态相等比较。屏幕录像或捕获采样仍须注明频率边界，不冒充所有显示器物理帧的完整证明。

输出至少拆分：输入排队、Stage 准备、render/fence、解除 cloak 后补齐、Availability 开销。若尚未捕捉原条件，只能称可控机制复现，继续保留长空闲物理 Gate。

## 4. P0.2：静态内容准备与保留

主改动限制在 Stage 的数据源挂接、行投影和视口准备。

- 数据源生命周期由绑定身份与实际释放控制；首批元数据可在初始化/绑定时建立有界行投影，不必把全部业务投影推迟到 Loaded。真实宽度未就绪时记录待完成的布局版本，不能用猜测列数宣布就绪。
- 普通隐藏保留当前有界数据窗口与可复用投影；不因暂时不可见主动 Clear/重新读取。实际解绑、窗口关闭、数据替换仍正确注销订阅与释放；先确认现有 Hide 实际事件，再决定是否需要调整卸载处理。
- 只有源内容或列数改变才重组行；同尺寸重复呼出不重复构造相同卡片行。数据/布局变化期间先构建新投影再发布，减少空集合中间态；不能仅靠提高 Dispatcher 优先级掩盖生命周期缺口。
- 内容就绪必须包括当前 viewport 的容器身份、非零有效几何和滚动归顶/锚点恢复已应用。不要求屏外容器存在，不等待全库或文件检查。
- 将 `HasPendingDeferredWork` 拆分为用于呈现的工作状态和动态检查/交互状态。现有总标志混有 Availability，不能直接用作首帧阻塞条件；标志清零也不能代替布局实际完成。

验收：首次/重挂时有完整内容准备信号；相同数据和几何的重复呼出没有额外行重建；换列、数据替换和真实解绑没有旧身份、滚动跳动或订阅残留。

## 5. P0.3：把 Stage 就绪接入宿主呈现事务

通过 App 层窄接口把 Stage 当前静态准备状态交给 MainWindow/WorkspaceController；宿主不遍历 Stage 控件，不向 Core/SQLite 传递 WPF 几何。继续沿用现有 DWM 栅栏与失败关闭策略。

成功路径为：

```text
同代 Show / 保持已有 cloak
  → 当前内容与布局版本的 Stage 视口就绪
  → 包含该版本静态内容的 WPF render 操作返回
  → 后台 DWM fence 成功
  → 再核验工作台代次、内容版本和布局版本
  → 解除 cloak / 前台与焦点观察
  → 本代成功呈现后允许动态可用性刷新
```

不能只在现有 fence 返回时增加一个 `Rows.Count > 0` 检查：行存在不等于本次渲染包含这些行，合法空库也不应一直等待。

- 帧等待必须位于内容就绪之后，处理同步完成、先就绪后订阅和事件重入，避免丢失唯一完成通知。
- fence 等待期间若内容/布局变化，旧证明失效；重新准备/呈现只由实际版本变化触发。需要重发 fence 时，为请求引入唯一 attempt 标识或等价可验证隔离，不能仅凭 workspace generation 让旧重复回调完成新请求。
- 取消/成功/失败均先认领终态再执行副作用；Hide、退出、失败回滚和新代替换撤销旧呈现等待。Hide 失败回到 Revealing 时必须重建正确的内容等待，不能跳过新门禁。
- 继续使用既有具名 2 秒 reveal watchdog 作为失败收口，不作为正常显示延时或性能目标，不因内容版本反复变化无限续期。内容/帧永不就绪、队列拒绝或 fence 失败仍 Hide、清 cloak、退出 Topmost，并保留错误阶段。

验收：未准备好的非空 Stage 不能被揭示；文件检查永不完成时静态内容仍可正常揭示；空库、已确认错误状态可结束准备；旧代/旧 attempt、重复回调、watchdog 竞争与隐藏失败均保持一致终态。

## 6. P0.4：可用性结果保留与有界刷新

将“取消容器的未完成工作”和“清除最近一次已知状态”分开。

- Hide/视口离开时取消订阅与排队请求，保留当前有界 VM 的最后结果；目标重新关联或对应身份失效时才清除。保留结果不等于永久有效。
- 每次成功呈现后对可见项合并一次重新验证；后续进入视口按需验证。刷新次数/并发预算由 P0.1 队列基线确定并具名，不使用固定延时猜测首帧已经完成。
- 每个项目最多一个当前请求，小批次提交共享 executor，防止一次把全视口检查挤入容量 128 的队列。旧请求即使已取消但仍在运行，也继续占用真实在途预算直到结束。
- 只对实际改变的状态发必要属性通知，不重发布卡片行。拖动期间不启动新的可用性检查，终态后按当前可见内容恢复调度。
- 校验请求代次、条目身份/目标版本、容器绑定和 ViewModel 身份；旧结果不能覆盖重新关联后的对象，也不能污染下一次显示。缓存随有界数据窗口淘汰，不建立按全部历史路径永久增长的字典。
- 必须验证文件在隐藏时被删除、恢复或重新关联。尤其 `CanOpenOrReveal` 当前依赖 IsMissing：缓存的 Missing 要能被显示后的复查纠正，不能永久禁用；缓存的 Available 也不构成操作成功保证，Shell 失败照常报告。
- UNC/网络盘保持当前谨慎判定；若测量证明单个设备查询长期占住共享 Shell STA，再单独评审隔离执行器。不能通过取消 token、超时后丢弃任务或无界 Task.Run 声称已终止原生 I/O。

验收：隐藏不会触发一轮状态清空通知；呼出首帧前无新 Stage Inspect 请求；显示后文件状态最终更新，重复结果不制造通知风暴；快速显隐/滚动/拖动不会积累无界排队。逻辑绑定归零与物理在途操作完成分别记录。

## 7. P0.5：长空闲与恢复条件下闭环

先验证 P0.2–4 的效果；仅当剩余耗时证明来自首次模板/布局或恢复后的呈现准备，才增加对应预热。

- 静态投影/模板准备可放到启动阶段的有界工作；恢复信号只合并标记布局/内容失效并安排必要工作，不全量读库或批量访问文件。
- 无激活预热必须实测不抢前台、不闪窗、不建立用户会话、不保留 Topmost；单纯 Measure/Arrange 不能声称已预热 GPU surface。需要原生窗口呈现才能获益时，另做窄生命周期切片及资源门禁。
- Resume/Unlock 重复到达、恢复立即热键、预热中 Show/Hide 和退出均以版本与所有者收口。正常呼出必须自足，不能依赖恢复事件一定发生或预热抢在用户之前完成。
- 任何预热收益同时比较 CPU、工作集、私有内存、GPU/句柄和后台回落；不采用周期性唤醒、强制 GC、工作集锁定或无限图标缓存维持假热态。

## 8. 必要测试与通过条件

| 组别 | 条件 | 必须证明 |
|---|---|---|
| 基本呈现 | 0/46/50 条；启动后第一次、连续热呼出 | 首次完整静态内容或合法空状态；没有逐批补卡 |
| 长空闲 | 隐藏 30 秒、5 分钟、30 分钟后再次呼出 | 每档真实再次 Show；同时测总时延与空白窗口，不能只测隐藏后的内存回落 |
| 恢复 | Modern Standby/睡眠恢复、会话解锁后立即和稍后呼出 | 同进程内容、焦点和时延正确；缺少物理样本明确记为未验证 |
| 数据与文件 | 本地文件、目录、.lnk、Missing、不可用设备、慢/永不完成的受控检查 | 文件检查不阻塞静态显示；缓存正确更新；源文件零修改 |
| 布局 | 非顶部滚动、260 DIP 窄列、常用宽度、100%/200% DPI、工作区变化 | 同代正确视口/列数/顺序；出现后不跳位；屏外不全量实现 |
| 竞态 | 揭示中隐藏/退出、旧回调、同轮重复终态、内容或布局版本变化、fence 拒绝/失败、watchdog、Hide 失败 | 没有抢焦点、空白揭示、永久 cloak、重复收尾或跨代污染 |
| 交互与资源 | 标准 Explorer 单/批量拖入、普通排序、管理操作、设置和 100 次显隐 | 原能力无回归；呈现/动画监听退出后归零；在途检查有界；资源无持续增长 |

实施中按单切片运行相应 Stage/WPF/Hosting 测试；最后涉及宿主与共享调度，执行 Release 构建与完整方案测试。2026-09-14 当前完整基线为 Core 87/87 + Integration 572/572，共 **659/659**，构建 0 warning / 0 error。

热态可采集足够样本报告 P50/P95/最大值；长空闲与恢复样本少时逐次列出，不用极少样本包装可靠 P95。按 R9 补同机同条件对照后设具名时延/资源预算；当前不发明已获确认的毫秒门槛。性能门槛待测量不阻断 P0.1–4 的机制修复，但实际用户观感与长空闲物理 Gate 完成前，不能宣布 P0 完成。

## 9. 交付顺序与当前下一步

依次交付 P0.1 观测/失败夹具 → P0.2 静态准备 → P0.3 同代呈现门禁 → P0.4 可用性刷新 → P0.5 恢复验收及证据驱动预热。每个切片独立记录收益和新增失败模式，避免一次混改宿主、Shell 执行器和存储。

P0.1–4 已完成源码与自动门禁，5 分钟 A/B 证明必须在隐藏 HWND 重新可见时显式弄脏根视觉面，随后才由静态内容 token、render-return 和 attempt 隔离的 DWM fence 决定揭示。P0.5 的用户反例进一步证明数据/投影提前挂接不等于真实 WPF view 已加载；当前只增加启动期一次性 cloak 内真实 materialization，不增加周期或 Resume 全量预热。下一步完成 30 分钟、Modern Standby/解锁和用户实际观感 Gate。

## 10. P0.1 实施与验证记录

2026-09-13 完成：

- `RevealTrace` 是实例所有、默认关闭的 2,048 条环形记录，记录单调时钟、线程、工作台代次、视图/请求身份、源/布局版本、视口与静态准备状态。覆盖丢失条数明确输出，Resume/Unlock 水位独立保留；不记录文件路径、名称或剪贴板正文。
- `RevealTraceRecorder` 仅在设置 `VOK_REVEAL_TRACE_DIRECTORY` 时创建，专属后台线程负责有界合并导出与工作集/私有内存/page faults/GC/句柄/线程采样。每个候选只占一个 JSON 和一个替换临时文件；无周期轮询、无常驻 Rendering 订阅。静态布局观测只在启用诊断的 Stage Loaded 期间订阅，Unloaded/关闭退出。
- 宿主的 `ShowBegin/ShowReturned/Rendering/FenceRequested/FenceReturned/BeforeUncloak/Uncloaked/InputReady` 与 Stage 的绑定、行发布、滚动恢复、容器身份以及 Availability 请求/返回/应用/解绑分别记录。Rendering 回调只记时间点，不遍历视觉树或请求导出；诊断不参与是否解除 cloak 的决定。
- Shell executor 区分 StageAvailability、StageResolve、StageAction 和 Other，按真实执行请求区分排队、认领、完成、取消前跳过及拒绝。UI 绑定请求与 Shell 请求各有自己的 ID，通过项目身份/时间辅助分析；工具不猜测旧 Shell 工作所属的工作台代次。绑定解绑与物理 I/O 完成不是同一事件。
- `Read-StageRevealTrace.ps1` 输出逐代分段时延、揭示时准备状态、重建/检查次数和队列等待/执行耗时；缺失里程碑保持 null，managed readiness 不冒充实际 Present。
- `Measure-HostInteraction.ps1` 增加真实隐藏后再次呼出的等待参数、诊断环境参数和可选首张/稍后全窗截图（每次最多 10 轮，独立输出目录）。250 ms 只是探针采样窗口，不进入产品同步路径；截图开销会影响外部时延，截图模式与时延模式分开运行。错误快捷键场景又复现了旧探针在冻结进程树前失败无法清理候选、未落错误报告的问题；现从 Start-Process 起拥有根进程，失败落盘并等待该进程真实退出，受控错误入口已复验。

新增 13 项门禁：Core 4 项，Windows/WPF/Hosting 9 项。最新完整 Release Core 87/87 + Integration 563/563，共 **650/650**，构建 0 warning / 0 error；脚本解析及真实读取报告通过。覆盖环形溢出、实例隔离、并发生产者、恢复水位、输出失败/恢复、重复 Dispose、诊断异常隔离、排队取消/已认领检查、Rendering 热路径约束、0/46/50 条延后投影与无限等待文件检查、普通 Hide 保留行及旧代检查不能写回新代。零条夹具原先以 `page[^1]` 生成 cursor 会异常，本轮修正测试夹具的合法空页，未改产品仓储。

### 10.1 实机基线与结论

独立诊断候选 PID 11756，实际 46 条，使用已持久化的 `Ctrl+Space`；两轮显隐/焦点/恢复均成功，无环形记录覆盖：

| 条件 | 内部解除 cloak | 输入就绪 | 解除 cloak 时 Stage 就绪 | 行发布 / 源挂接 | 显示前 Availability 请求 |
|---|---:|---:|---|---:|---:|
| 进程首次呼出 | 522.596 ms | 554.854 ms | true | 1 / 1 | 46 |
| 隐藏 30.038 秒后 | 30.435 ms | 39.790 ms | true | 0 / 0 | 46 |

首次调用顺序实际为：ShowBegin 21.020 → Loaded 247.160 → 通用 Rendering 255.846 → ShowReturned 265.197 → 行发布 267.020 → fence 请求 509.395 → Stage 滚动恢复 512.196 → fence 返回 521.569 → 解除 cloak 522.596 ms。**通用 Rendering 确实早于 Stage 行发布，fence 请求也早于滚动恢复，但本轮最终揭示前 Stage 恰已就绪。**这提供了缺少显式内容合同的真实时序证据，没有复现“已揭示后仍空白”。

两轮首张与稍后 PNG 分别具有相同 SHA-256；首张图目视确认 Stage 46 张卡片完整，覆盖比旧三点采样充分。抓图返回存在额外开销，仍不能把“首个被探针捕获的图像”说成首个物理显示帧。92 次 StageAvailability 的执行耗时总计约 7.673 ms、单次最大约 1.711 ms；这些暖文件系统样本不能解释全部首次呼出成本，更不能外推睡眠恢复。

另以最终构建、关闭截图分别跑诊断关闭/开启各 4 轮，均零显隐/输入/恢复失败。关闭模式的首次/三次热呼出为 459.1 / 34.0、30.1、44.6 ms；开启模式为 494.1 / 42.6、37.8、31.4 ms。两组运行非随机对照且样本很少，仅证明两种运行路径可工作，不能认定诊断零开销或宣布性能门槛通过。开启模式 4 轮均在解除 cloak 时就绪；只有首轮发布行，每轮均在显示前请求 46 次检查。

证据位于本地忽略目录 `.artifacts/p0-reveal/`：`probe30.json`、`trace/reveal-11756-*.json`、`frames/6e0fd4e3478e4d93a585f9ecafb230db/`、`probe-off.json`、`probe-on.json`、`trace-comparison/`、`probe-rejected.json`。测试候选均已退出；未替换默认 Release 实例或修改 Stage schema/数据合同。

**未验证**：隐藏 5/30 分钟、Modern Standby/睡眠、Unlock 后原始空白、受内存压力的 WPF/D3D 恢复和最终修复观感。受控未就绪夹具与普通 30 秒隐藏不能代替这些物理 Gate。`CaptureRevealState` 是诊断观测，不是 P0.3 已实施的生产呈现门禁。

### 10.2 复用方式

当前机器使用 `Ctrl+Space`；其他环境先核对设置。时延采样不开截图：

```powershell
dotnet build src/Vok.sln -c Release --artifacts-path .artifacts/p0-reveal
& tools/Measure-HostInteraction.ps1 `
  -Label 'Stage reveal' `
  -AppPath .artifacts/p0-reveal/bin/Vok.App/release/Vok.exe `
  -Gesture control-space -Cycles 2 -HiddenBeforeShowSeconds 0,300 `
  -RevealTraceDirectory .artifacts/p0-reveal/trace `
  -OutputPath .artifacts/p0-reveal/probe-five-minute.json
& tools/Read-StageRevealTrace.ps1 -Path '<本次 trace JSON 的绝对路径>'
```

需要图像证据时另跑短候选，加 `-CaptureRevealFramesDirectory .artifacts/p0-reveal/frames`。长时间手动物理验收可为候选进程单独设置 `VOK_REVEAL_TRACE_DIRECTORY` 后启动，正常呼出/隐藏会导出记录。缓冲有覆盖时减少每份记录包含的操作，不把缺失事件解释成没发生。

## 11. P0.2–4 实施与 P0.5 阶段证据

2026-09-14 完成以下生产机制：

- Stage 在设置 DataContext 时即接入既有 VM 和有界行投影，不再等待首次 Loaded 才开始准备隐藏窗口的静态卡片。普通 Hide 继续保留投影、滚动状态和最近一次 Availability 结果；真实 Unloaded、换源及条目身份变化仍注销订阅并使对应证明失效。
- 新增实例所有的 `generation + contentVersion + layoutVersion + stateVersion` 静态就绪 token。非空状态要求当前 viewport 应实现的卡片身份与已实现卡片集合相等、几何有效且投影/布局/滚动工作已结束；合法空状态和已确认错误状态也能成为终态。宿主只能按“同代内容就绪 → 下一次 Rendering 返回 → attempt 唯一的 DWM fence → 再核验 token → 解除 cloak”完成揭示。
- 隐藏 HWND 再次变为可见时显式 `InvalidateVisual` 根工作区，使 WPF 在受 cloak 保护的这一轮真正重绘可能已被 DWM 丢弃的 redirected surface。它不改变 opacity、不增加等待，也不常驻渲染循环；静态 token、render-return 和 fence 仍负责决定何时可揭示。
- Availability 与静态首帧完全解耦：揭示并完成输入门禁后才启动一次可见项复查，物理在途上限具名为 4；被取消但尚未从同步 Shell 调用返回的工作继续占用预算。隐藏、回收和换列只撤销绑定、不清空上次状态；拖入或内部排序期间不启动新检查；只有结果实际变化才发布派生属性通知。
- Hide、退出、失败、watchdog、Hide 失败回滚、内容/布局变化、旧 generation、旧/重复同代 fence attempt 与同步就绪回调失败均有所有者和失败关闭路径。就绪或 fence 永不完成时仍由既有 2 秒 watchdog 隐藏、清 cloak、退出 Topmost 并恢复目标，不按时间强行显示。

### 11.1 根因 A/B

使用同一真实 46 条数据和 5 分钟隐藏条件，先只启用静态 token、render-return 与旧 DWM fence，未执行根视觉失效：隐藏约 300.5 秒后，内容 22.6 ms 已就绪、32.9 ms 已解除 cloak、42.4 ms 输入就绪，揭示前 Availability 为 0，但外部约 95.9 ms 捕获的首图仍是纯背景，250 ms 后才出现完整工作台。这排除了 SQLite 重载、Stage 行未发布和文件检查阻塞作为该次空白的直接原因，也证明“WPF retained tree 有效 + DwmFlush 返回”不足以保证长期隐藏后的 surface 已被重绘。

随后只增加“HWND 重新可见时使根工作区失效”这一变量；同样隐藏约 5 分钟后，32.4 ms 解除 cloak、45.3 ms 输入就绪，首张与稍后截图 SHA-256 完全相同且目视均为完整 46 项。由此将本次机制归因为：长期隐藏期间 DWM 可丢弃 redirected surface，而 WPF 仍把保留视觉树视为有效，没有新的 dirty region 时旧栅栏可能只等待到空 surface；显式根失效使受保护帧包含真实重绘。

一个带截图的冷启动样本出现过完整首图但未在 2 秒内满足物理焦点门禁，候选按预期失败关闭；随后的无截图快速 100 轮为 100/100，未再出现显示、呈现、隐藏、恢复或焦点失败，因此不把该单点写成空白回归，也不隐去其物理边界。

### 11.2 当前验证结果

- 完整 Release：Core 87/87、Integration 572/572，共 **659/659**；构建 0 warning / 0 error。新增门禁覆盖静态就绪先于 fence、内容变化重建证明、旧同代 attempt 拒绝、缺失就绪 watchdog、重复终态、同步就绪回调失败不在清理后继续 Show，以及隐藏投影/Availability 保留、呈现后有界复查和快速显隐的物理在途预算。
- 100 轮独立候选全部成功；去掉前 10 轮后，外部用户可见 P50/P95/最大值约 94.5/109.8/110.2 ms，内部解除 cloak 为 36.6/92.9/98.7 ms，输入就绪为 44.4/101.4/106.0 ms。尾段工作集、私有内存、句柄和 GDI 未显示持续正增长。
- 最终源码成功路径不变的精确构建再跑 10/10，零显示、呈现、隐藏、恢复或焦点失败。另一次最终构建 30 秒隐藏样本在 25.3 ms 静态就绪、36.8 ms 解除 cloak、47.8 ms 输入就绪；揭示时 46/46 视口卡片已实现，揭示前 Availability 请求为 0，首图目视完整。首图与 250 ms 后图像差异只落在一张卡的状态区域，符合呈现后 Availability 局部刷新合同，不是整区或卡片补载。
- 本地忽略证据位于 `.artifacts/p0-reveal-exact-final/`、`.artifacts/p0-reveal-final-verified/` 及此前 5 分钟 A/B 候选目录；探针候选结束后均已退出。没有修改 schema、真实 Stage 数据或容量/重复 occurrence 合同。

**P0.1–4 阶段当时仍未验证**：30 分钟隐藏、Modern Standby/睡眠与会话解锁后立即/稍后呼出，以及用户对候选的主观观感。随后用户反例与 P0.5 修复/验收见 §11.3–12。最终证据只支持启动期一次性真实 materialization，不支持周期唤醒、强制 GC、Resume 全量预热或长期保持窗口可见，这些方案继续不实施。

### 11.3 P0.5 首轮长隐藏/恢复探针边界

2026-09-14 首轮计划的 30 分钟自动样本被真实电源状态改变污染，不能判为产品通过或失败。候选 PID 19784 的第一轮冷启动正常：46/46 卡片已实现，477.1 ms 内容就绪、482.3 ms 解除 cloak、514.6 ms 输入就绪，揭示前 Availability 为 0，首张与稍后 PNG 相同。随后候选保持隐藏；系统在 13:25:34 因 Idle Timeout 进入 S0 Modern Standby，13:52:37 才退出，实际隐藏约 2,551.3 秒。应用记录到 SessionLock/SessionUnlock，但没有 `PowerMode Resume`。

旧探针在线程恢复后立即合成 `Ctrl+Space`，该时刻早于应用收到 SessionUnlock；候选轨迹没有 `HotkeyReceived`、`ShowBegin` 或第二代 session，外部最终得到 `Visible HWND timeout`。因此失败发生在安全桌面尚未切回用户 `Default` 桌面的输入注入边界，Stage readiness、重绘和 fence 根本没有执行，不能写成“30 分钟后 Stage 仍空白”或“恢复修复失败”。

测量工具已改为发送前读取当前 input desktop 的真实名称并等待可交互 `Default` 桌面，同时通过 `RegisterHotKey` 冲突确认配置组合仍被注册；冲突不能识别所有者，发送后仍以候选自身 `HotkeyReceived` 确认归属。不使用固定 sleep 猜测解锁完成。60 秒是探针等待人工桌面恢复的失败 watchdog，不计入 Show 时延且不进入产品。脚本解析与当前交互桌面 1/1 烟雾通过，实际等待 2 ms、组合仍被注册、候选收到热键且显隐/呈现/焦点/恢复零失败。

候选 PID 18952 加载的 `Vok.dll` SHA-256 为 `42BCB7ABDEE9FDFF9C9532FAFA013C066974D33A84BDB1BA2073C1AFC85BC6B5`；其后续用户反例见 §11.4。该 PID 的轨迹已保留并已在替换候选前精确终止。

### 11.4 用户反例与一次性初始 surface 预热

用户在 PID 18952 长时间静置并经历 SessionLock/Unlock 后首次手动呼出，确认界面迟钝且 Stage 先空白再显示，故 P0.1–4 候选判定未通过。轨迹完整记录了本次 Show：解锁后约 11.976 秒收到候选自身 `HotkeyReceived`，热键到 UI 为 25.5 ms；`ShowBegin` 26.6 ms、Stage 首次 `Loaded` 264.3 ms、`ShowReturned` 281.8 ms、10 列行投影发布 291.6 ms，布局直到 485.2 ms 仍在变化，静态 token 495.6 ms、解除 cloak 528.4 ms、输入就绪 562.5 ms。第二次 48.7 秒热隐藏后呼出则在 56.7 ms 内输入就绪。由此确认“界面慢”的主延迟不在热键、SQLite 或 Availability，而是进程启动后从未真实显示时，DataContext/初始投影虽已准备，Stage 的 Loaded、真实宽度列投影、容器模板和布局仍全部推迟到第一次 Show。

P0.5 候选在默认预创建 HWND 路径增加一次性初始 surface 预热：先 cloak，挂接同一静态 readiness 所有者，按当前前台显示器应用工作区尺寸后立即退出 Topmost，以 `ShowActivated=false` 做一次真实 Show；静态内容与一次 Rendering 返回后的 Dispatcher Background 均完成后取消预热、Hide、再次退出 Topmost 并解除 cloak。它不创建 Workspace session、不请求前台/焦点、不启动首帧前 Availability，不增加固定延时、周期唤醒或 Resume 全量工作。普通 Show 继续自足；预热失败但能恢复安全 Hidden 时回落原路径，Hide 失败则保持 cloak 并让启动失败关闭。保留的 `long.MaxValue` 预热代次不属于用户 Show，读取器现只把含 `ShowRequested` 的 generation 报作 session。

同一真实 46 条数据的候选结果：预热后首次呼出在 39.9 ms 解除 cloak、71.1 ms 输入就绪，30 秒后为 34.0/43.3 ms；四张首图/250 ms 后图像 SHA-256 相同。另一候选启动预热后保持 5 分钟、期间从未由用户打开，首次呼出在 52.3 ms 解除 cloak、84.4 ms 输入就绪，46/46 身份已实现、零行发布、零源重挂、揭示前零 Availability，首图与稍后图像 SHA-256 相同。完整 Release 为 Core 87/87 + Integration 575/575，共 **662/662**，构建 0 warning / 0 error。

代价边界：真实 WPF materialization 会把“从未显示”的隐藏进程提升到已使用后的资源档位；两个样本预热后隐藏私有内存约 165.9–169.8 MiB，5 分钟后约 165.9 MiB，首次使用后约 158.6 MiB，没有周期活动或随静置上升。本次接受该有界一次性成本作为“进程启动后首次呼出也快速”的候选交换，但仍须由用户观感和后续资源路线确认；它不外推到未来超大集合，N1 仍必须只实现当前数据窗口/视口。30 分钟、S0/解锁立即与稍后呼出仍未完成，截图仍不是正式 Present 证明。

最终用户候选为 PID 18352，默认 Release `Vok.dll` SHA-256 为 `2B05AAF8C07F212EC8D77EE250E7634A56146BE2A84E21E90362157022C837EE`。启动轨迹已明确落盘 `InitialWarmupBegin → InitialWarmupReady → InitialWarmupHidden`，无失败；启动后进程响应正常。该候选已通过用户长静置观感，尚未提交。

## 12. P0 用户验收与剩余边界

2026-09-14 用户确认 PID 18352 相比此前实例呼出明显改善：“呼出无缓慢、中转站无白屏”，并明确当前 P0 已完成预期。当前轨迹环形缓冲因逐项 Availability 事件覆盖 2,412 条，最早的用户长静置 session 已不完整，因此不为它补写精确时延；仍保留的四轮均在 28.6–37.4 ms 解除 cloak、35.5–45.7 ms 输入就绪，Stage 静态 token 成立，零行重发、零源重挂、揭示前零 Availability且无失败。用户观感、受控 5 分钟首后帧和 662/662 自动门禁共同构成本阶段完成证据。

完成结论严格限于 P0 的工作台/Stage 呼出目标。用户同时记录：(1) 长静置第一次仍略慢于连续热呼出，但已无明显迟缓；(2) 剪贴板图片缩略图在长静置呼出时仍会短暂空白。前者登记 KI-002，后者由 `ClipboardView` 隐藏时清空 `Image.Source`、显示后按 viewport 异步重绑的独立生命周期解释并登记 KI-003。两项均按用户要求不在本轮继续修改。

原计划中的独立纯 30 分钟与明确 Modern Standby/Unlock 分档没有各自形成未覆盖的完整轨迹；当前用户长静置实机 Gate 已足以完成产品预期，但该证据缺口仍明确保留，不能写成这些细分场景均已逐项自动通过。截图也继续不冒充正式 Present/ETW 证据。
