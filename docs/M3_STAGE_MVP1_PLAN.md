# M3.MVP1 Stage 入站数据内核实施计划

> **状态（2026-09-04）**：实现与自动门禁已完成。本阶段已建立 Stage Core、文件引用规范化、schema 7 和入站仓储；未接 WPF、OLE 或真实产品数据库，因此尚不是用户可见的中转站 MVP。

## 1. 阶段结果与边界

M3.MVP1 的唯一结果是：给定一组已经从外部 Drop 快照出的路径，系统能在既有 Shell STA 上把它们解析为 Stage 文件引用，并在既有 SQLite 执行器上原子地加入、去重回顶和分页读回。

```text
IReadOnlyList<string>
  → IStageFileService.ResolveAsync
  → 每项成功或结构化失败
  → 成功项交给 IStageRepository.AddOrMoveAsync
  → Applied / CapacityExceeded / SortOrderExhausted
  → IStageRepository.LoadPageAsync
```

本阶段包含：

- `Vok.Core.Stage` 的不可变模型、选项、结果和窄接口；
- 文件、目录、`.lnk` 的绝对路径规范化，且不解析链接目标；
- schema 6→7 的 `stage_items` 新表、结构/索引核验和事务回滚；
- 默认容量 50、批量容量原子判断、批内/库内去重、重复项回顶；
- 64 位稀疏排序、复合 keyset、64 位总数和 10,000 条结构门禁；
- 组合根注册，但不在启动流程创建 Stage ViewModel 或加载 Stage 页面。

本阶段不包含：

- `StageView`、空状态、卡片、图标、状态文案或任何 WPF 变化；
- `WorkspaceFileDropRouter`、`IDataObject`、DragEnter/Over/Leave/Drop 或宿主 lease 变化；
- 固定、手工排序、移除、打开/定位、重新关联、取用、剪贴板写入、拖出或消费；
- 文本、图片、资产引用、搜索或设置入口；
- 对真实 `%LOCALAPPDATA%\Vok\vok.db` 执行迁移或替换当前运行实例。

达到本阶段门禁只能宣布“Stage 入站数据内核就绪”，不能宣布中转站 MVP 可用。

## 2. 现有实现诊断

可以复用的基础：

- `SqliteStorageExecutor` 已唯一串行拥有 WAL 连接、容量 64 队列和 schema 初始化事务；
- `WindowsShellStaExecutor` 已提供容量 128 的唯一 Shell/COM STA，并保证取消只能在任务认领前获胜；
- `WindowsLauncherShellService` 已真机验证文件、目录和 `.lnk` 保留自身路径；
- schema 1→6 已有“版本号 + 真实结构”核验、未知结构拒绝和事务回滚门禁。

不能复制的 Launcher 行为：

| 当前 Launcher | Stage 要求 |
|---|---|
| `LauncherPage.TotalCount` 是 `int` | `StagePage.TotalCount` 从首版使用 `long` |
| 游标只有 `sort_order` | 游标必须是 `(sort_order,id)` |
| 新项追加到末尾 | 本批有效项作为一个块移动到顶部 |
| 库内重复只返回 Existing | 重复项刷新展示字段并回顶 |
| 排序使用连续整数 | Stage 使用命名的 64 位稀疏步长 |
| 手工移动会读取并重写全表 | Stage 热路径只更新本批受影响项 |
| Launcher 业务 DTO 贯穿 Shell 服务 | Stage 保持独立 DTO，只共享平台内部解析内核 |

因此不让 `StageItem` 继承或包装 `LauncherItem`，也不让 `IStageRepository` 调用 `ILauncherRepository`。

## 3. Core 契约

建议新增：

```text
src/Vok.Core/Stage/
  StageItemKind.cs
  StageItem.cs
  StageTargetDescriptor.cs
  StageTargetResolution.cs
  StagePage.cs
  StageOptions.cs
  StageRepositoryResults.cs
  IStageRepository.cs
  IStageFileService.cs
```

### 3.1 模型

```csharp
enum StageItemKind { File, Directory, ShellLink }

record StageItem(
    Guid Id,
    StageItemKind Kind,
    string TargetValue,
    string CanonicalKey,
    string ResolvedName,
    long SortOrder,
    bool IsPinned,
    long Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

record struct StagePageCursor(long SortOrder, Guid Id);

record StagePage(
    IReadOnlyList<StageItem> Items,
    StagePageCursor? NextCursor,
    long TotalCount);
```

`IsPinned` 和 `Revision` 本阶段不提供操作入口，但从 schema 7 首版存在，避免后续消费/固定语义再迁移主表。不得加入 Clipboard 类型、HWND、WPF、SQLite 或 Shell COM 类型。

### 3.2 容量策略

`StageOptions` 只持有 `long Capacity`：

- 默认值为 50；
- 构造时只要求大于 0；
- 不定义 `MaximumSupported = 200`，也不拒绝 201 或 10,000；
- 首期面向用户的 200 上限属于以后设置/组合根的产品策略，不属于 Core、schema 或 SQL 能力上限。

生产组合根本阶段传入 50；仓储测试分别传入 1、50、200 和 10,000。

### 3.3 文件解析结果

`IStageFileService.ResolveAsync` 对每个输入保持原始次序，并返回“成功目标或结构化失败”，不因一个坏路径让整批 Shell 解析失败。建议失败类型至少包含：

```text
Empty | NotFullyQualified | Missing | AccessDenied |
InvalidPath | Unsupported | Unavailable
```

单项失败包含安全的展示消息；执行器终止、队列拒绝或编程错误继续抛异常，不能伪装成普通坏路径。

### 3.4 仓储接口与结果

```csharp
Task<StagePage> LoadPageAsync(
    StagePageCursor? after,
    int pageSize,
    CancellationToken cancellationToken = default);

Task<StageAddOrMoveResult> AddOrMoveAsync(
    IReadOnlyList<StageTargetDescriptor> targets,
    CancellationToken cancellationToken = default);
```

`StageAddOrMoveResult` 只返回状态、前后总数和 Added/Moved 数量，不返回整个数据库或数千个完整条目。预期状态：

```text
Applied | CapacityExceeded | SortOrderExhausted
```

参数错误在入队前抛出；SQLite/执行器故障继续抛出。空目标列表由调用层短路，不以一次无意义数据库查询制造“成功”。

## 4. Windows 文件引用规范化

建议在 `Vok.Infrastructure.Windows/Shell` 增加内部 `WindowsFileReferenceResolver`，让 Launcher 和 Stage adapter 映射成各自 Core DTO：

- 先用现有 Launcher 集成测试锁定当前文件、目录、`.lnk`、缺失和显示名行为；
- 提取同步解析内核，但仍只从现有 `WindowsShellStaExecutor` 调用；
- Launcher adapter 保持现有 canonical 输出，避免改变冻结模块的数据身份；
- Stage adapter 使用自己的路径身份，不引用任何 `Launcher*` 类型。

Stage 单项规则：

1. 空白或非完全限定路径拒绝，不相对当前工作目录补全；
2. `Path.GetFullPath` 只做词法绝对化，不解析 symlink、junction 或 `.lnk` 目标；
3. `File.GetAttributes` 证明路径当前存在，并区分 Directory、`.lnk`、普通 File；
4. 目录去掉非根目录的末尾分隔符，根目录保持合法形式；
5. `TargetValue` 保存规范后的实际绝对路径；
6. `CanonicalKey` 只表示路径身份，不拼入 kind，并用稳定的 invariant case fold 后持久化；schema 的 `NOCASE` 再作为 ASCII 防线；
7. `.lnk` 的 `ResolvedName` 去掉显示用扩展名，但 `TargetValue` 始终仍是 `.lnk` 文件自身。

Stage 有意按产品合同把路径视为大小写不敏感；即使底层目录开启大小写敏感模式，也不在本 MVP 中允许两个仅大小写不同的卡片。UNC、断连网络盘、虚拟文件和只提供流的 OLE 来源不作为 M3.MVP1 验收承诺。

混合有效/无效批次由下一阶段协调：失败项保留错误，成功项保持相对顺序进入仓储。仓储的“整批拒绝”只针对传入的全部有效唯一目标发生容量不足；若拒绝，已有重复项也不得回顶或刷新。

## 5. schema 7

在 `SqliteStorageExecutor` 中：

- 将 `CurrentSchemaVersion` 从 6 提升到 7；
- 新增 `CurrentStageColumns`；
- 在与 Launcher、Clipboard 相同的初始化事务内调用 `EnsureStageSchema`；
- 只有结构与索引全部核验成功后才写 `PRAGMA user_version = 7`；
- schema 6 没有 `stage_items` 时只建新表，不改写既有 Launcher/Clipboard 行或资产文件；
- 已存在但列/索引不匹配的 Stage 表原样拒绝，不能 DROP 后重建。

表结构：

```sql
CREATE TABLE stage_items (
    id            TEXT NOT NULL PRIMARY KEY,
    target_kind   INTEGER NOT NULL,
    target_value  TEXT NOT NULL,
    canonical_key TEXT NOT NULL COLLATE NOCASE,
    resolved_name TEXT NOT NULL,
    sort_order    INTEGER NOT NULL,
    is_pinned     INTEGER NOT NULL,
    revision      INTEGER NOT NULL,
    created_utc   TEXT NOT NULL,
    updated_utc   TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_stage_items_canonical
ON stage_items(canonical_key COLLATE NOCASE);

CREATE INDEX ix_stage_items_order
ON stage_items(sort_order, id);
```

显式唯一索引比隐式 `UNIQUE` auto-index 更易做列集合、唯一性和损坏门禁；不加入 `COUNT <= 200`、连续行号、Clipboard 外键或资产引用。

迁移期间禁止启动或替换真实产品实例。schema 7 对旧 schema 6 二进制是单向边界：M3.MVP1 只用临时库和真实库副本测试，直到 M3.MVP2/MVP3 可交付版本一起完成后才允许迁移真实库。

## 6. 分页与排序

Stage 统一按“最新放入/重新放入在最前”读取：

```sql
WHERE $afterOrder IS NULL
   OR sort_order < $afterOrder
   OR (sort_order = $afterOrder AND id < $afterId)
ORDER BY sort_order DESC, id DESC
LIMIT $pageSizePlusOne;
```

- 用 `pageSize + 1` 判断是否确有下一页，返回页仍最多 `pageSize`；
- `NextCursor` 取返回页最后一项的 `(SortOrder,Id)`；
- `COUNT(*)` 用 `Int64` 读取；
- page size 必须有命名上限，建议 256；
- 测试用 `EXPLAIN QUERY PLAN` 证明顺序查询使用 `ix_stage_items_order`，SQL 中不得出现 `OFFSET`。

推荐稀疏步长使用命名常量 `1L << 20`。头部批量的第一个输入获得最高 sort order，最后一个获得最低 sort order，从而使输入 `[Y,A,X,B]` 在 DESC 页面中仍显示为 `[Y,A,X,B]`。正常头插和重复回顶只更新本批目标，不重写未涉及行。

若 `checked(maxSortOrder + uniqueCount * step)` 溢出，事务不产生任何变化并返回 `SortOrderExhausted`；不得静默改成连续全表重排。这个结果在现实容量下不可达，但明确失败方向可以避免未来用溢出数据破坏顺序。

## 7. AddOrMove 单事务算法

1. 入队前校验 descriptor 字段、enum、容量选项，并按 CanonicalKey 保留批内第一次出现；
2. SQLite 线程开始事务，读取当前 `COUNT(*)`；
3. 用可复用的参数化查询查出本批哪些 key 已存在；
4. 计算 `newCount = uniqueBatchCount - existingCount`；
5. 若 `currentCount + newCount > Capacity`，回滚并返回 `CapacityExceeded`，不得刷新、回顶或部分插入任何一项；
6. 读取当前最大 sort order，使用 checked 计算本批稀疏键；
7. 按输入顺序：新项 INSERT；已有项保留 id、created_utc、is_pinned，刷新 kind/target/name，提升 sort order、revision 和 updated_utc；
8. 提交后返回前后总数、AddedCount 与 MovedCount。

同一个 `SqliteStorageExecutor` 已串行化并发调用，但事务仍是容量、查重和写入的唯一权威。取消只允许在任务被 SQLite 执行器认领前获胜；认领后必须完成提交或回滚，不能让调用方取消掩盖已发生的数据库副作用。

## 8. 实施顺序

每个检查点只引入一个主要变量：

1. **Core 契约**：新增 Stage 模型、选项、结果与接口；用纯单元测试锁定参数、64 位类型和无平台引用。
2. **schema 7**：只改存储初始化与迁移测试，验证表/索引/版本和失败回滚；尚不写仓储业务。
3. **只读仓储**：实现复合 keyset、`pageSize+1`、64 位 count 和 10,000 条索引门禁。
4. **写入事务**：实现批内去重、容量原子拒绝、新增/回顶顺序、revision 和并发容量竞争。
5. **文件规范化**：先补 Launcher 特征测试，再提取内部 Windows 文件引用解析器，接 `WindowsStageFileService` 并证明 Launcher 输出不变。
6. **组合根**：注册 `StageOptions(50)`、`IStageRepository` 和 `IStageFileService`；不解析 Stage ViewModel、不改启动加载顺序。
7. **全量回归**：通过新增门禁和既有全量测试后停止，不进入 UI/OLE。

任一检查点连续失败两次，先回查契约/所有权/SQL 假设；不得以禁用测试、吞异常、降低结构核验或添加固定等待继续堆叠修改。

## 9. 自动验收矩阵

Core：

- 默认容量 50；1、200、10,000 合法；0/负数拒绝；不存在硬编码 200 上限；
- `StagePage.TotalCount`、cursor sort order、item sort/revision 均为 64 位；
- enum/空字段/空 Guid/page size 越界在入队前失败。

文件解析：

- 真实临时文件、目录、`.lnk` 各自分类，`.lnk` 不解引用；
- 绝对路径、目录尾分隔符、大小写与 Unicode canonical 行为；
- 缺失、空白、相对、非法和无权限路径逐项失败，其他有效项不受影响；
- 预取消不执行解析，已认领任务遵守现有 Shell STA 完成语义；
- Launcher 现有目标值、canonical key、显示名和指纹行为零变化。

迁移与结构：

- 空库直接形成 schema 7；合法 schema 6 只新增 Stage 表并保留既有全部表/行；
- schema 7 重开幂等；未来版本拒绝；Stage 未知列、缺列、错误唯一性或错误索引拒绝；
- 在 Stage 表创建后注入索引冲突，证明表创建和 `user_version=7` 一起回滚；
- 真实 schema 6 库副本迁移后 `quick_check=ok`、0 外键违规，Launcher/Clipboard 数量和资产 SHA 不变。

仓储语义：

- 单项/批量新增按输入顺序置顶，重启后顺序一致；
- 批内重复只处理第一次，库内重复保留 id/pinned/created、刷新展示字段并 revision+1；
- 混合 `[已有,新增,已有,新增]` 整块按输入顺序回顶，未涉及项的 revision/sort order 不变；
- 49→50 成功，49+2、50+1、固定项占满均整批拒绝且数据库字节语义不变；
- 两个并发添加竞争最后一个容量时恰好一个成功，不超过 Capacity；
- 容量拒绝、取消、SQL 异常和排序溢出均无部分写入；
- 10,000 条复合 keyset 无遗漏、无重复、无 OFFSET，并使用顺序索引；重复回顶只更新目标行。

## 10. 验证命令与完成条件

实现期间依次运行：

```powershell
dotnet test src/Vok.Tests/Vok.Tests.csproj -c Release --filter "FullyQualifiedName~Stage"
dotnet test src/Vok.IntegrationTests/Vok.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Stage|FullyQualifiedName~SqliteLauncher|FullyQualifiedName~WindowsLauncherShell"
dotnet test src/Vok.sln -c Release --settings Vok.runsettings
dotnet build src/Vok.sln -c Release
```

完成条件：新增 Stage Core/Shell/SQLite 测试全部通过；既有 429 项基线零回归；Release 0 warning/0 error；`git diff --check` 通过；没有运行产品、迁移真实库、替换 PID、创建 Stage UI 或宣称用户路径已可用。

通过后，M3.MVP2 只接两件事：真实 `StageView`/ViewModel，以及唯一 `WorkspaceFileDropRouter`。它消费本阶段接口，不再修改 Stage 主表、容量事务或路径身份。

## 11. 实施与验证记录

2026-09-04 已完成：

- 新增独立 `Vok.Core.Stage` 模型、64 位页面/结果契约、默认 50 且不硬编码 200 上限的 `StageOptions`；
- schema 7 新增 `stage_items` 和显式 canonical/order 索引；合法 schema 6 可事务升级，当前版本缺表、错误列/唯一性/排序索引、未来版本和建索引冲突均拒绝且不自愈覆盖；
- `SqliteStageRepository` 实现批内与库内大小写不敏感去重、整批容量判断、重复回顶、64 位稀疏排序、复合 keyset 和异常全事务回滚；
- `WindowsStageFileService` 在既有 Shell STA 上解析文件、目录和 `.lnk`，保留链接自身；Launcher 通过共享内部解析器保持原有 DTO、canonical 和指纹行为；
- 组合根注册 `StageOptions`、`IStageRepository` 与 `IStageFileService`，未在启动流程解析 Stage 页面或执行查询。

验证结果：

- Release 全量测试：Core 71/71、Windows/WPF 集成 385/385，共 456/456；
- 10,000 条复合 keyset 无遗漏/重复，查询计划使用 `ix_stage_items_order`；
- 忠实临时 schema 6 夹具升级后保留 Launcher、Clipboard 与资产引用元数据，`quick_check=ok`、0 外键违规；
- Release 构建 0 warning / 0 error，`git diff --check` 通过；
- 当前 PID 16900 与真实 `%LOCALAPPDATA%\Vok\vok.db` 均未停止、替换或迁移。真实产品库副本迁移继续留给 M3.MVP3 发布候选门禁。
