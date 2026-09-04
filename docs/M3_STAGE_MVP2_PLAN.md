# M3.MVP2 Stage 展示与统一入站路由实施计划

> **状态（2026-09-04）**：已完成实现与自动门禁。Release 71/71 Core + 395/395 集成共 466/466，构建 0 warning / 0 error；浅色/深色真实 WPF 渲染和 260 DIP 窄列通过。当前源码 Release 实例 PID 848 保持运行且未被本阶段停止或替换；真实产品库、发布候选与 Explorer/桌面物理闭环仍属于 M3.MVP3。

## 1. 阶段结果与边界

M3.MVP2 只把已经完成的 Stage 数据内核接成一条可自动验证的 WPF 入站路径：真实 `StageView` 能分页展示持久条目，`MainWindow` 的 Launcher 专用拖放逻辑收敛为唯一 `WorkspaceFileDropRouter`，一次外部 FileDrop 只能命中 Launcher、Stage 或无目标中的一个。

```text
MainWindow 唯一 AllowDrop
  → 只做当前指针区域判定
  → WorkspaceFileDropRouter 独占 drag id / target / terminal
  → Drop 先冻结目标、认领终态并清除两个区域视觉
  → 最多一次快照外国 IDataObject.FileDrop
  ├─ Launcher adapter → 既有 LauncherViewModel.AddDroppedPathsAsync
  └─ Stage adapter    → StageViewModel.AddDroppedPathsAsync
                         → IStageFileService
                         → IStageRepository
                         → 从 SQLite 重载第一页
```

本阶段包含：

- `StageItemViewModel`、`StageViewModel` 和真实 `StageView`；
- 固定大小的 keyset 页面展示、空状态、处理中和持久化结果状态；
- 文件、目录和 `.lnk` 的类型图形、名称、父路径摘要、完整路径 ToolTip/自动化名称；
- 唯一窗口级 `WorkspaceFileDropRouter` 和 Launcher/Stage 两个目标 adapter；
- Launcher、Stage、无目标区域之间的唯一命中视觉；
- Drop 同步 OLE 接受结果与异步规范化/落库结果的明确分离；
- 隐藏、设置、关闭、DragLeave、新 DragEnter 和异常的幂等终态清理；
- Stage ViewModel 的组合根注册与启动加载。

本阶段不包含：

- 点击取用、仅复制、拖出、消费、固定、排序、打开/定位、移除或重新关联；
- 路径可用性轮询、Shell 原图标或 Missing 动态检查；卡片本轮只显示稳定的类型图形；
- Stage 文本、图片、缩略图、搜索或设置入口；
- 修改 `stage_items`、schema 7、容量事务、canonical 身份或公开仓储分页契约；
- 运行/替换当前产品实例、迁移真实产品库、发布候选或 Explorer/桌面物理验收。

完成本阶段只能宣布“Stage UI 与统一入站路由代码就绪”。只有 M3.MVP3 完成实库副本、发布候选和真实 Explorer/桌面拖动闭环后，才能宣布入站 MVP 可用。

## 2. 现有实现诊断

### 2.1 可以直接复用

- `MainWindow` 已是唯一 `AllowDrop=true` 的普通不透明 WPF HWND；无需增加子窗口或原生 `IDropTarget`。
- `IWorkspaceWindow.ExternalDragStarted/Ended` 与 `WorkspaceController` 已把真实 DragEnter lease 绑定到 Workspace generation，并覆盖失活、原始长按松键、显式关闭和接受 Drop 后的前台重申。
- 当前 Drop 已遵守“Enter 只探测格式、Over 不读数据、Drop 最多读取一次”的热路径原则。
- M3.MVP1 已提供逐项文件解析、批量原子落库、重复回顶、容量拒绝和第一页重载所需的全部窄接口。
- Launcher 和 Clipboard 已提供 Fluent 动态资源、Recycling `VirtualizingStackPanel`、Pixel 滚动、隐藏滚动条与 WPF STA 渲染测试样例。

### 2.2 必须先拆开的耦合

| 当前状态 | M3.MVP2 要求 |
|---|---|
| drag id、格式探测、快照、终态和 Launcher 分发都在 `MainWindow.xaml.cs` | `WorkspaceFileDropRouter` 独占会话和分发，`MainWindow` 只转发事件与计算区域 |
| `IsOverLauncher` 只有布尔目标 | 使用 `None / Launcher / Stage` 三态目标，同一时刻最多一个 |
| `LauncherView.SetExternalDragActive` 忽略 `canDrop`，且只按 active 变化 | 同时观察 session active 与 targeted；跨区时立即撤销旧框并显示新框 |
| Drop 终态后由 `MainWindow` 直接 fire-and-forget Launcher VM | Router 冻结 adapter，并统一观察异步分发异常 |
| Stage 中央区域只有空 Grid | 使用真实 Stage ViewModel/View，不保留伪占位内容 |
| Clipboard/Launcher 的 LoadMore 会持续追加 VM | Stage 使用固定大小页面替换；常驻 VM 数不随浏览深度增长 |

`WorkspaceController` 的公开拖放事件、generation 和 Hide 策略不应修改。本阶段的风险在目标路由和 WPF 展示，不重新设计已验证的宿主租约。

## 3. 目标结构

建议新增：

```text
src/Vok.App/Features/Stage/
  StageItemViewModel.cs
  StageViewModel.cs
  StageView.xaml
  StageView.xaml.cs

src/Vok.App/Hosting/
  WorkspaceFileDropRouter.cs
  WorkspaceFileDropTarget.cs

src/Vok.IntegrationTests/Stage/
  StageViewModelTests.cs
  StageViewIntegrationTests.cs

src/Vok.IntegrationTests/Hosting/
  WorkspaceFileDropRouterTests.cs
```

修改：

```text
src/Vok.App/MainWindow.xaml(.cs)
src/Vok.App/App.xaml.cs
src/Vok.App/Features/Launcher/LauncherView.xaml.cs
src/Vok.IntegrationTests/Hosting/MainWindowFocusIntegrationTests.cs
src/Vok.IntegrationTests/Stage/StageCompositionRootTests.cs
```

不修改：

```text
src/Vok.Core/Stage/*
src/Vok.Infrastructure.Windows/Persistence/SqliteStageRepository.cs
src/Vok.Infrastructure.Windows/Persistence/SqliteStorageExecutor.cs
src/Vok.Infrastructure.Windows/Stage/WindowsStageFileService.cs
```

如果实现中发现必须改变最后一组文件，应停止并回查 MVP1 契约，而不是顺手扩展本阶段。

## 4. Stage ViewModel 与有界页面

### 4.1 页面策略

使用固定单页窗口，而不是无限追加：

- 命名常量 `PageSize = 64`；任意时刻 `Items` 最多持有 64 个 `StageItemViewModel`；
- 初始页用 `LoadPageAsync(null, 64)`；“较早”使用当前页的 `NextCursor`；
- 用轻量 `List<StagePageCursor?>` 只保存已经访问过的页面起点，“较新”按已保存起点重新查询；
- 换页以整页替换 `ObservableCollection`，滚动回到页首；不使用 `OFFSET`，也不永久保留离页 VM；
- 当前默认 50 条只需一页；首期 200 最多四页；10,000 条门禁只增加轻量 cursor 数，不增加常驻卡片数；
- 任一 Stage 写操作成功后清空旧 cursor 历史并从第一页重载，避免回顶后继续使用旧顺序游标。

页面属性至少包含：

```text
Items
TotalCount (long)
IsLoading / HasError / IsEmpty
CanLoadNewer / CanLoadOlder
PageSummaryText
StatusText
LoadNewerCommand / LoadOlderCommand
```

页面命令和入站写入共用一个 `SemaphoreSlim`，避免“旧页查询迟到覆盖刚落库后的第一页”。ViewModel 拥有 lifetime CTS；取消只在任务被 Shell/SQLite 执行器认领前获胜，不能把已提交结果伪装成取消。

### 4.2 条目投影

`StageItemViewModel` 只投影持久模型，不访问文件系统：

- 保留 id、kind、target、sort order、pin、revision 和时间；
- `ResolvedName` 作为主标题；
- 纯词法派生父路径摘要，完整 `TargetValue` 放入 ToolTip；
- File、Directory、ShellLink 映射到稳定的 Segoe Fluent Icons glyph 和类型文字；
- `AutomationName` 同时包含类型、名称和完整路径；
- 若历史数据 `IsPinned=true` 可显示只读标识，但不创建固定命令；
- 不调用 `File.Exists`、Shell、SQLite 或图片解码，不在首屏为全部条目做可用性检查。

### 4.3 入站协调

`StageViewModel.AddDroppedPathsAsync` 的顺序固定为：

1. 复制路径列表并进入单一操作门禁；不先向 `Items` 插入乐观卡片；
2. 调用 `IStageFileService.ResolveAsync`，保留逐项成功/失败和原始次序；
3. 无有效目标时只发布结构化失败状态，不调用仓储；
4. 将全部有效 descriptor 一次交给 `AddOrMoveAsync`；
5. `CapacityExceeded` 和 `SortOrderExhausted` 不修改页面，并显示“有效批次整批未写入”；
6. `Applied` 后废弃旧页面 cursor，从 SQLite 重载第一页，再发布 Added/Moved/Invalid 数量；
7. 若仓储已提交但页面重载失败，状态必须明确为“已保存，但列表刷新失败”，不能显示成“未放入”并诱导重试；
8. Shell/SQLite 未确认异常显示“结果待核对”，OLE Copy 光标不作为落库证据。

建议状态矩阵：

| 结果 | 页面动作 | 用户状态 |
|---|---|---|
| 全部无效 | 不访问仓储 | 未放入；显示无效数量与首个安全原因 |
| Applied | 重载第一页 | 已新增 N、回顶 M；另有 K 项无效 |
| CapacityExceeded | 保持当前页 | 空间不足，有效批次整批未放入 |
| SortOrderExhausted | 保持当前页 | 顺序空间异常，未写入任何项 |
| 已提交、重载失败 | 保留旧页 | 已保存但显示未刷新，下次呼出/重载核对 |
| 未确认异常 | 保留旧页 | 处理失败，不能依据 Copy 光标判断已保存 |

状态保存在 singleton ViewModel；即使原始长按松键令 Workspace 在 Drop 后隐藏，下次呼出仍能看到最后结果。

## 5. Stage View

`StageRegion` 内改为 `stage:StageView x:Name="StagePanel"`，保持主工作区 1:2:1 和 24 DIP 标题基线。建议布局：

```text
24 DIP Header：中转站 + 数量徽标 + 单行状态
*      Body：Recycling/Pixel ListBox 或空状态
28 DIP Footer：较新 / 页面范围 / 较早（只有多页时可操作）
```

视觉约束：

- 使用全宽纵向轻量卡片，便于在中央宽列和 520 DIP 窄窗口同时显示名称与路径；本阶段不引入非虚拟化 WrapPanel；
- 卡片只展示，不使用 Button 或点击反馈，不暗示取用/打开已实现；
- `ListBox + VirtualizingStackPanel` 开启 Recycling、Pixel scroll、`CanContentScroll=true`，垂直滚动条保持 Hidden；
- 空状态只写“将文件、文件夹或快捷方式拖到这里”，不宣传后置能力；
- 名称/父路径使用 Ellipsis，完整路径通过 ToolTip 和 AutomationProperties 暴露；
- 全部颜色使用 Fluent 动态语义资源，不硬编码浅/深色；
- Drop 命中只显示覆盖 Stage 标题、内容与页脚的 2 DIP Accent 矩形描边：零 Margin、零 CornerRadius、无背景、无子内容；
- 外部 drag session 活跃时暂停区域内命中；只有 targeted=true 时显示描边。

本阶段使用类型 glyph，不启动 Shell 图标管线。真实图标、Missing 检查及容器回收后的 revision 防串卡一起留给管理阶段，避免为纯入站 MVP 引入不可见 Shell 工作。

## 6. 唯一 WorkspaceFileDropRouter

### 6.1 窄契约

Router 留在 `Vok.App`，因为 `IDataObject`、`DragDropEffects` 和目标视图都是 WPF 类型；不得下沉到 Core。

```text
WorkspaceFileDropTargetKind = None | Launcher | Stage

IWorkspaceFileDropTarget
  Kind
  IsAvailable
  CancelInteractions()
  SetExternalDragState(sessionActive, targeted)
  ReceiveAsync(paths)
  ReportDispatchFailure(exception)
```

Launcher adapter 只映射到现有 View/VM；Stage adapter 只映射到 Stage View/VM。两者不互相引用业务模型。Router 可由 `MainWindow` 构造一个窗口生命周期实例，不需要为了“唯一”引入全局静态或第二线程。

`MainWindow.ResolveDropTarget` 只做几何判定：设置打开、目标不可见或指针在 Header/Clipboard/BottomBar 时返回 None；Launcher/Stage 使用半开矩形 `[0,width) × [0,height)`，避免共享边界同时命中。Router 对不可用 adapter 再归一为 None。

### 6.2 状态与事件顺序

Router 只有两种状态：

```text
Idle
Tracking(dragId, currentTarget)
```

`DragEnter`：

1. 先认领并结束遗失终态的旧 session；
2. 若旧 `ExternalDragEnded` 同步导致窗口隐藏，不建立新 session；
3. 只调用一次 `GetDataPresent(FileDrop)`；异常或无格式保持 Idle；
4. 分配单调 drag id，发布既有 `ExternalDragStarted`；
5. 向两个 adapter 广播 sessionActive=true，只有当前 target targeted=true。

`DragOver`：

- 只更新 enum target 和两个 adapter 的视觉，不访问 `IDataObject`；
- 返回当前是否可 Drop，供 `e.Effects` 精确设置 `Copy/None`；
- Launcher → Stage → None 的任意切换都先撤销旧框，最终恰好零个或一个框可见。

`Drop`：

1. 冻结最后坐标对应的 target；
2. 先把 session 从 Tracking 认领为 Idle，并同步清除两个区域视觉；
3. 只有冻结目标有效时才最多一次 `GetData(FileDrop)`，立即复制 `string[]`；
4. 快照成功即发布 `ExternalDragEnded(dropAccepted=true)` 并向 OLE 返回 Copy；失败/无目标发布 false 并返回 None；
5. Copy 返回后由冻结 adapter 异步处理不可变路径快照；后续 DragEnter 或指针变化不能改投另一个模块；
6. Router 观察分发 Task；退出取消可安静结束，其他未预期异常交给对应 adapter 的状态，不制造未观察 Task。

`DragLeave / Hide / Settings / Close / replacement`：

- 共用同一个 `Cancel()`；先认领、清视觉，再最多发布一次 `ExternalDragEnded(false)`；
- 重复或迟到终态在 Idle 下无作用；
- 不使用坐标猜测 DragLeave 是否“真的离开”，继续保留现有窗口级终态规则。

### 6.3 OLE 接受与持久化是两个结果

`WorkspaceExternalDragEndedEventArgs.DropAccepted` 保持现有含义：该 OLE Drop 已命中目标且 FileDrop 快照成功。它只用于 Workspace lease 收口和前台重申，不表示 Shell 解析或 SQLite 提交成功。

因此：

- Router 不等待 `ReceiveAsync` 后再设置 `e.Effects`；
- Stage 不做乐观 UI 插入；
- 容量拒绝、无效路径或存储失败通过 Stage 状态单独呈现；
- Launcher 的现有异步添加语义保持不变；
- 不新增“异步失败后反向修改 OLE effect”这种不可能成立的协议。

## 7. 生命周期与组合根

- `StageViewModel` 注册为 singleton，由 DI 注入已存在的 `IStageRepository`、`IStageFileService` 和 `StageOptions`；
- `App.OnStartup` 在创建 `MainWindow` 前调用一次 `StageViewModel.InitializeAsync`，确保首次呼出已有持久条目；加载失败由 Stage 自身显示，不创建空白假成功；
- `MainWindow` 构造函数增加可选 Stage VM 参数并设置 `StagePanel.DataContext`，保持现有 WPF 测试可逐步迁移；
- Router 由 `MainWindow` 唯一拥有，继续通过 `IWorkspaceWindow` 原事件向 `WorkspaceController` 发布，不改变宿主接口；
- 打开设置、窗口隐藏、关闭和应用退出都调用 Router.Cancel；进入设置同时继续取消 Launcher 内部排序/点击反馈；
- `StageViewModel.Dispose` 取消 lifetime、等待中的门禁调用并释放资源；不 Dispose 共享仓储、Shell STA 或 SQLite 执行器。

真实 schema 7 初始化由共享存储执行器在正常 App 启动时触发。PID 848 是此前用户“启动”请求产生的 MVP1 源码 Release 实例；按启动链路推断它已访问产品存储，但本规划没有直接读取数据库确认版本。实现/测试期间继续只用 fake 或 `%TEMP%` SQLite，不重启当前产品，也不连接 `%LOCALAPPDATA%\Vok\vok.db`；真实迁移状态留给 M3.MVP3 的只读审计与副本门禁。

## 8. 实施顺序

每个检查点只改变一个主要变量：

1. **页面状态**：先实现 `StageItemViewModel` 和固定 64 项的 `StageViewModel` 页面导航，用 fake repository/file service 锁定加载、换页、重载和状态矩阵。
2. **纯展示**：新增 `StageView` 并替换空占位，先以静态 VM 验证浅/深、620/900 高度、520 窄宽、虚拟化和自动化；尚不改 Drop。
3. **Launcher-only router 提取**：把当前 drag id、格式探测、一次快照和终态从 `MainWindow` 移入 Router，只接 Launcher adapter，要求既有拖入与 Workspace lease 测试零行为变化。
4. **三态目标路由**：加入 `None/Launcher/Stage` 与 Stage adapter，修正 Launcher 的 active/targeted 双状态，验证跨区唯一视觉；不同时改宿主 lease。
5. **异步 Stage 分发**：接 `StageViewModel.AddDroppedPathsAsync`，证明 Drop 先清视觉/收 OLE 终态，持久结果随后独立呈现且目标不会改投。
6. **生命周期与组合根**：加入启动初始化、设置/隐藏/关闭清理和 Dispose；保持 `IWorkspaceWindow` 与 `WorkspaceController` 不变。
7. **全量回归**：通过新增矩阵、456 项进入基线和 Release 构建后停止，不进入实库、发布或物理 Explorer 验收。

任一高风险检查点连续失败两次，回查事件顺序、目标冻结或 UI/执行器所有权；不得用 Dispatcher 延时、吞异常、全局 Deactivated 抑制或第二套 drag lease 绕过。

## 9. 自动验收矩阵

### 9.1 Stage ViewModel

- 初始化幂等；空库、已有文件/目录/`.lnk` 和加载异常状态正确；
- 10,000 条顺序浏览无遗漏/重复，任一时刻最多 64 个条目 VM，返回较新页顺序一致，SQL 仍无 OFFSET；
- 换页和 Drop 共用门禁，旧页迟到不能覆盖写入后的第一页；
- mixed valid/invalid 只把有效 descriptor 作为一个批次写入，顺序不变；
- Applied 后从权威仓储重载，重复项回顶且 ID 保持；
- CapacityExceeded/SortOrderExhausted 不乐观修改页面；
- “提交成功、刷新失败”和“提交未确认”使用不同状态；
- Dispose/预取消不留下 IsLoading、门禁或未观察异常。

### 9.2 Router

- DragEnter 只调用一次 GetDataPresent；100 次 DragOver 调用零次 GetData；Drop 最多调用一次；
- Launcher → Stage → None → Stage 只显示当前目标框，两个框从不同时可见；
- Drop 前冻结 target，清视觉发生在外国 GetData 之前，快照复制后源数组变化不影响分发；
- Launcher Drop 只调用 Launcher adapter，Stage Drop 只调用 Stage adapter，无目标不读取数据也不分发；
- 外国源撤回格式、GetData 抛出、空数组、不可用 adapter 均返回 None 并正常结束 lease；
- DragLeave、Hide、Settings、Close、新 DragEnter 和重复/迟到终态最多结束一次；
- 旧 session 结束回调同步隐藏窗口时不建立孤儿新 session；
- OLE accepted 后 adapter 容量拒绝/异常不反写 accepted，也没有未观察 Task；
- 既有 `WorkspaceController` 长按、失活、显式热键和前台重申测试无需改业务期望。

### 9.3 WPF 与组合根

- Stage 替换空占位，主窗口继续 1:2:1；Header、Stage 列和 Footer 在 520×500、620/900 高度不裁切；
- ListBox 使用 Recycling、Pixel、Hidden scrollbar，固定页窗口不会实现全部 10,000 个容器；
- 浅/深主题下卡片、空状态、页导航和纯 2 DIP Drop 框只使用动态语义资源；
- 长名称/Unicode/长父路径截断，ToolTip 与自动化名称保留完整信息；
- 卡片没有取用、打开、删除、固定或拖出命令；
- 组合根只有一个 Stage VM，使用已有 repo/file service；窗口/router 不创建新 SQLite/Shell 线程；
- 打开设置、隐藏和关闭后两个 Drop overlay 均收起、列表命中恢复；
- 临时源文件在成功、无效、容量拒绝和取消后路径、长度、SHA 不变。

## 10. 验证命令与完成条件

当前运行实例会锁定默认 Release 输出；全部命令继续使用独立 `%TEMP%` artifacts：

```powershell
$taskArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'Vok-M3MVP2'

dotnet test src/Vok.IntegrationTests/Vok.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~StageViewModelTests|FullyQualifiedName~StageViewIntegrationTests" `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

dotnet test src/Vok.IntegrationTests/Vok.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~WorkspaceFileDropRouterTests|FullyQualifiedName~MainWindowFocusIntegrationTests|FullyQualifiedName~WorkspaceControllerTests" `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

dotnet test src/Vok.sln `
  -c Release `
  --settings Vok.runsettings `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

dotnet build src/Vok.sln `
  -c Release `
  --artifacts-path $taskArtifacts `
  -p:NuGetAudit=false

git diff --check
```

完成条件：

- Stage 页面/状态/固定窗口与三态 Router 新增门禁全部通过；
- 进入阶段的 456 项基线零回归，Release 0 warning / 0 error；
- `IWorkspaceWindow` 外部拖放事件与 `WorkspaceController` 业务期望不变；
- `Vok.Core.Stage`、schema 7、容量事务和 canonical 身份没有被改写；
- 没有停止、重启或替换当前 PID 848，没有由测试代码读写真实产品数据库，没有发布或宣称物理入站已通过；
- 下一步 M3.MVP3 只做实库副本、发布候选和 Explorer/桌面真实闭环，不再重构 Router 或 Stage 页面状态。

## 11. 实施结果

M3.MVP2 已按上述边界落地：

- 新增 `StageItemViewModel`、`StageViewModel` 和 `StageView`，固定页大小为 64，并以页面起点 cursor 历史支持“较新/较早”整页替换；10,000 条自动门禁证明常驻条目 VM 不超过 64；
- `WorkspaceFileDropRouter` 成为窗口级 drag id、当前目标、一次快照和幂等终态的唯一所有者，Launcher/Stage 通过窄 adapter 接收冻结后的不可变路径；
- Router 在读取外国 `IDataObject` 前先认领终态并撤销全部目标视觉；目标切换先清零再激活唯一目标，修复了 Launcher 只观察 session active、忽略 `canDrop` 导致跨区仍高亮的机制；
- OLE `Copy` 接受与异步 Stage 解析/提交保持两个结果；Stage 不乐观插入，`Applied` 后从 SQLite 权威重载，提交后刷新失败与提交结果未知分别呈现；
- Stage VM 作为 singleton 进入组合根并在创建 `MainWindow` 前初始化；设置、隐藏、关闭、新 DragEnter 与退出都收敛到 Router 的同一取消边界，`IWorkspaceWindow` 和 `WorkspaceController` 未修改；
- 新增 10 项 Stage/Router/WPF 门禁；最终隔离 `%TEMP%` artifacts 下 Release 71/71 Core + 395/395 集成共 466/466，构建 0 warning / 0 error，`git diff --check` 通过；浅色/深色 520 DIP 渲染目视通过，260×408 DIP 布局门禁通过。

本阶段没有修改 `Vok.Core.Stage`、schema 7、`SqliteStageRepository`、`WindowsStageFileService` 或产品容量/canonical 契约，也没有连接真实产品数据库、发布或执行物理 Explorer 拖入。用户可用性结论继续等待 M3.MVP3。
