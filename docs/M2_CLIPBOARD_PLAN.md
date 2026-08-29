# M2 剪贴板业务模型与纵向切片计划

## 1. 目标与完成定义

剪贴板区域在 Vok 隐藏和显示期间都持续监听 Windows 系统剪贴板。用户完成常见的复制、文件剪切或截图后，内容应以一条可理解、可持久化、可再次取用的历史记录出现在右侧剪贴板区。

M2 的产品完成定义是：

- 文本、文件系统项目和图片三类内容均形成端到端闭环：监听、捕获、规范化、去重、持久化、分页展示、固定、删除、写回和向呼出前目标粘贴；
- 一次系统剪贴板更新只生成一条业务记录。同一内容同时提供 HTML、RTF、Unicode 文本，或 PNG、DIBV5、DIB 等多个格式时，这些格式是同一条记录的多个“表示层”，不能显示成多条历史；
- Vok 自己写回剪贴板时不产生新历史，重复系统通知不产生重复历史，迟到事件和旧代动作不能修改新代；
- 监听、图片处理、SQLite 和 WPF 展示均有界，不能阻塞 UI Dispatcher，也不能把原图常驻 ViewModel；
- 捕获失败、格式不支持、目标恢复失败和输入注入受限都有可观察结果，不静默显示成成功。

## 2. “任何内容”的产品边界

Windows 剪贴板允许来源程序同时放入多个标准、注册或私有格式，也允许延迟到读取时才渲染数据。因此“任何复制/剪切/截图”在 M2 中定义为：Vok 开始监听后，只要一次稳定的剪贴板快照包含下表中的受支持表示，就必须正确形成一条记录。

| 用户内容 | 首期读取格式 | 主展示 | 再次取用 |
|---|---|---|---|
| 纯文本、代码、URL | `CF_UNICODETEXT`；必要时以 `CF_TEXT + CF_LOCALE` 降级 | 保留换行的纯文本摘要 | Unicode 文本 |
| 富文本/网页片段 | `Rich Text Format`、`HTML Format`，同时保留 Unicode 文本降级 | 安全生成的纯文本摘要，显示“富文本”标记 | 按来源保真度写回 RTF/HTML/Unicode 多表示 |
| 文件和文件夹 | `CF_HDROP`，读取 `Preferred DropEffect` 判定 Shell 复制/剪切意图 | 文件名、数量、类型图标和复制/剪切标记 | 文件引用列表；显式取用时保留已显示的操作意图 |
| 截图和普通位图 | 注册格式 `PNG` 优先，其次 `CF_DIBV5`、`CF_DIB`、`CF_BITMAP` | 保持比例和透明通道的缩略图、尺寸 | PNG + 兼容位图表示 |
| 空剪贴板 | 无内容 | 不创建记录 | 不适用 |
| 未知私有格式 | 不反序列化、不持久化原始对象 | 不伪装成受支持内容；只增加无内容诊断计数 | 不适用 |

以下边界必须如实处理：

- 普通文本的“剪切”和“复制”在通用剪贴板协议中通常没有可靠区别，只保存内容；Explorer 文件剪切可通过 `Preferred DropEffect=MOVE` 识别；
- 截图与“复制了一张普通图片”都属于图片。没有可靠来源证据时，界面使用“图片”，不通过进程名或尺寸猜测成“截图”；
- Outlook 附件等只提供 `FileGroupDescriptor/FileContents` 的虚拟文件不是 `CF_HDROP`。M2 基线先安全识别为不支持的虚拟文件，待独立的流式物化、容量和取消门禁完成后再纳入，不能把延迟 COM 流伪装成本地文件路径；
- 如果来源程序在 Vok 读取前已用下一次更新覆盖剪贴板，Windows 不再提供被覆盖的中间内容。Vok 保证捕获每个实际取得的稳定快照，不承诺恢复操作系统已经丢失的瞬时中间态；
- 若快照包含 `ExcludeClipboardContentFromMonitorProcessing`，或 `CanIncludeInClipboardHistory` 明确为 0，则按隐私意图跳过整条快照，不保留内容、摘要或来源名称。

## 3. 核心业务模型

### 3.1 聚合根：`ClipboardEntry`

```text
ClipboardEntry
├─ Id
├─ Kind: Text | FileList | Image
├─ SemanticHash
├─ FirstCapturedUtc / LastCapturedUtc / CaptureCount
├─ LastCaptureOrder
├─ IsPinned
├─ PrimaryPresentation
├─ Representations[]
├─ FileReferences[]          # 仅 FileList
├─ FileTransferIntent       # Copy | Move | Unknown
└─ Revision
```

`ClipboardEntry` 表示一次可被用户理解和再次取用的逻辑内容，不等同于某一个 Win32 format。Core 中不得出现 HWND、format id、HGLOBAL、WPF `IDataObject`、`BitmapSource` 或 SQLite 类型。

### 3.2 表示层：`ClipboardRepresentation`

```text
ClipboardRepresentation
├─ Kind: UnicodeText | Html | Rtf | Png | CanonicalBitmap
├─ FidelityRank
├─ Storage: InlineText | AssetReference
├─ ByteLength
├─ ContentHash
└─ Metadata
```

- 文本预览与原始表示分离。预览永远是无执行能力的纯文本，不用 WebBrowser/WebView 渲染 HTML，也不加载远程资源；
- 小型纯文本可以内联 SQLite；RTF、HTML 超过内联阈值后进入资产存储；
- 图片只在资产目录保存原始或无损规范化内容及缩略图，SQLite 仅保存哈希、尺寸、格式和引用；
- 不保存未知注册/私有格式，不调用任意对象反序列化。

### 3.3 捕获快照：`ClipboardSnapshot`

平台层在一次成功的 `OpenClipboard` 生命周期内复制所有受支持表示，关闭剪贴板后发布不可变快照：

```text
ClipboardSnapshot
├─ RuntimeSequence
├─ CapturedUtc
├─ EnumeratedFormats[]       # 仅格式描述和来源顺序
├─ SupportedPayloads[]       # 已脱离系统句柄的自有数据
├─ PrivacyMarkers
└─ SourceDiagnostics?        # 不含剪贴板正文
```

`RuntimeSequence` 只用于同一进程生命周期内的相等性、水位和竞态判断；不能作为跨重启主键或按数值大小推断永久顺序。

### 3.4 去重语义

重复复制同一逻辑内容时不新增卡片，而是把已有记录移动到最新位置并更新 `LastCapturedUtc`、`LastCaptureOrder` 和 `CaptureCount`。哈希规则必须保留用户可观察语义：

- 纯文本：精确 Unicode 内容；换行和空白不擅自裁剪或折叠；
- 富文本：最高保真表示与纯文本降级共同参与，同样文字但格式不同仍可成为不同记录；
- 文件列表：有序的 Windows 大小写不敏感规范路径 + `FileTransferIntent`；复制与剪切不得合并；
- 图片：解码后的规范像素、尺寸和色彩/Alpha 语义；同一像素的 PNG/DIB 编码差异不制造重复记录。

sequence 去重和内容去重是两层不同门禁：前者过滤重复通知和自写回流，后者合并用户重复复制的相同内容。

## 4. 主展示选择

平台层先枚举格式，再复制所有受支持表示；业务层最后选择唯一的 `Kind`，不能在读到第一个格式后提前丢弃其他表示。

选择规则如下：

1. 有有效 `CF_HDROP` 时选择 `FileList`；`Preferred DropEffect` 缺失只令意图成为 Unknown，不能让同快照的文本或图片反向抢占文件列表；
2. 有显式 PNG/DIB/Bitmap 且不存在更强文件传输语义时，选择 `Image`；
3. 其余有 HTML/RTF/Unicode/ANSI 文本时，选择 `Text`；
4. 多个候选仍冲突时，以来源程序提供的原生枚举顺序作为决胜条件；Windows 合成出的降级格式不能反过来抢占更高保真原始格式；
5. 选择策略必须用真实生产者语料锁定：记事本、Visual Studio/终端、Word、浏览器、Explorer 复制/剪切、Snipping Tool、Print Screen 和常见聊天软件。

这避免两个已知错误：把 Explorer 复制的图片文件误画成位图，或把浏览器复制的图片因附带 URL/HTML 而只显示成文字。

## 5. 捕获流水线与所有权

```text
WM_CLIPBOARDUPDATE
  → 读取 GetClipboardSequenceNumber，仅投递唤醒信号
  → Clipboard STA 合并重复 signal，认领待处理 sequence
  → 检查隐私标记和 Vok 自写标记
  → OpenClipboard 有界退避；一次打开内枚举并复制受支持格式
  → CloseClipboard，发布不可变 ClipboardSnapshot
  → 有界规范化 worker：文本摘要 / 图片解码、像素哈希和缩略图
  → 资产暂存
  → SQLite 单事务 upsert entry、representations、files、asset refs
  → UI Dispatcher 只接收已提交的轻量投影
```

所有权约束：

- 独立 `Clipboard STA` 拥有 listener HWND、消息循环、`OpenClipboard/GetClipboardData/SetClipboardData` 和运行期 sequence 水位；不复用 WPF UI STA，也不把监听 Hook 挂到 `MainWindow`；
- `WM_CLIPBOARDUPDATE` 不是内容本身，只是唤醒；重复消息按 sequence 幂等合并；
- 同一次打开期间把受支持格式复制到 Vok 自有内存/临时资产，不能在关闭剪贴板后继续持有系统 HGLOBAL/HBITMAP；
- `ERROR_ACCESS_DENIED/CLIPBRD_E_CANT_OPEN` 后允许由命名策略控制的有界退避，因为 Windows 没有“剪贴板已解锁”通知；它是实际忙占用后的失败恢复，不是正常同步屏障；
- 若处理期间收到更新，只追加最新待处理 sequence。已经完整复制的旧快照继续提交，随后处理最新快照；未打开成功前已被覆盖的中间态记入诊断，不伪造历史；
- 图片 worker 和待提交快照都受项目数与字节预算双重约束。阈值集中在 `ClipboardCaptureOptions`，在 4K/8K 图片和高频复制实测后锁定，不能散落魔法数字；
- 任何日志、异常和诊断属性都不得包含正文、文件完整列表、图片内容或哈希以外的可逆敏感数据。

### 5.1 自写回流

Vok 写回时在同一个 Clipboard STA 命令中：

1. 生成随机 `WriteOperationId`；
2. 写入业务表示，并附带仅用于本进程识别的注册格式 `Vok.Internal.ClipboardWrite`；
3. `CloseClipboard` 后读取新 sequence，形成 `ClipboardWriteReceipt(sequence, operationId, semanticHash)`；
4. 后续 update 只有同时匹配 marker 与 receipt 才按自写跳过；只靠“跳过下一次通知”或单个计数器不足以抵抗外部写入竞态；
5. 外部程序覆盖后 marker 消失，必须作为正常外部内容捕获。

## 6. 持久化模型

建议在现有单一 `SqliteStorageExecutor` 中把 schema 升级为剪贴板版本，并保持真实列结构识别和单事务迁移：

```text
clipboard_entries
  id, kind, semantic_hash, file_intent,
  plain_preview, first_captured_utc, last_captured_utc,
  last_capture_order, capture_count, is_pinned,
  primary_asset_hash, pixel_width, pixel_height,
  item_count, total_bytes, revision

clipboard_representations
  entry_id, representation_kind, fidelity_rank,
  inline_text, asset_hash, byte_length, metadata

clipboard_files
  entry_id, ordinal, original_path, canonical_path,
  display_name

content_assets
  hash, relative_path, media_type, byte_length,
  pixel_width, pixel_height, ref_count, state, created_utc
```

- `clipboard_entries(kind, semantic_hash)` 提供内容幂等，文件语义哈希已经包含 `file_intent`；`last_capture_order, id` 提供稳定 keyset 分页；
- 文件只保存引用，不复制、移动、删除用户源文件。显示时可异步刷新“已失效”状态，但不能因暂时不可访问自动删历史；
- 资产先写同卷临时文件并校验，再原子提交到内容寻址路径；数据库事务只引用已就绪资产；
- 删除、自动清理和去重替换只调整引用与 tombstone，事务提交后再回收零引用资产；失败时保留可诊断 tombstone，不留下数据库指向不存在文件；
- pinned 条目不受普通条数/时间淘汰，仍受明确的总数据保护策略约束；
- 默认保留期、条数、总字节、单项大小和图片像素门槛在 M2 作为命名安全默认值实现，M3 只为已经工作的策略提供设置界面。

## 7. Core 契约与应用协调

建议新增边界如下：

```text
Vok.Core/Clipboard
├─ ClipboardEntry / ClipboardRepresentation / ClipboardFileReference
├─ ClipboardKind / FileTransferIntent / ClipboardPageCursor
├─ IClipboardHistoryRepository
├─ IClipboardPlatformService
├─ IClipboardAssetStore
├─ ClipboardCaptureResult / ClipboardWriteReceipt
└─ ClipboardPasteStateMachine

Vok.Infrastructure.Windows/Clipboard
├─ WindowsClipboardPlatformService      # 专属 STA + listener HWND + Win32 读写
├─ WindowsClipboardFormatReader         # 白名单格式到不可变 payload
├─ ClipboardContentNormalizer           # 格式选择、哈希、缩略图任务入口
└─ ContentAddressedClipboardAssetStore

Vok.Infrastructure.Windows/Persistence
└─ SqliteClipboardHistoryRepository

Vok.App/Features/Clipboard
├─ ClipboardController                  # 监听结果 → 资产/仓储 → UI 投影
├─ ClipboardPasteController             # 取用状态机与 Workspace 交接
├─ ClipboardViewModel / ClipboardItemViewModel
└─ ClipboardView.xaml
```

`IClipboardPlatformService` 应同时串行拥有 watch 和 write，而不是把 WPF `Clipboard` 静态 API散落到 ViewModel：

```text
WatchAsync(cancellation) → immutable ClipboardCaptureResult stream
WriteAsync(replayPayload, operationId, cancellation) → ClipboardWriteReceipt
IsCurrentAsync(receipt) → sequence + marker 双重核验
```

仓储负责已提交业务状态，平台服务负责系统资源，App 控制器负责编排；Messenger 不承载捕获结果、写回结果、删除或粘贴等有返回值操作。

## 8. 剪贴板区域展示契约

右侧区域保持现有 `1*` 宽度和 24 DIP 标题行，内容改为真实 `ClipboardView`：

```text
剪贴板                                      [全部][文本][图片][文件]
┌─────────────────────────────────────────────────────────────┐
│ 文本卡：最多 4 行纯文本摘要                           2分钟前 │
│ [富文本]                                            [固定][删] │
├─────────────────────────────────────────────────────────────┤
│ 图片卡：保持比例缩略图，不裁切、不拉伸                1920×1080 │
│                                                     [固定][删] │
├─────────────────────────────────────────────────────────────┤
│ 文件卡：报告.docx + 另外 2 项                  [剪切]  5分钟前 │
└─────────────────────────────────────────────────────────────┘
```

- 最新捕获在前；重复内容更新后回到最前。固定只阻止自动淘汰，不改变用户的时间顺序；
- 文本卡保留换行并做字符级省略，完整内容通过可访问详情呈现；不在列表直接构造富文本视觉树；
- 图片卡只加载缩略图，保持宽高比和 Alpha；进入详情或执行写回时才读取原始资产；离开视口后释放解码对象引用；
- 文件卡显示首项名称、总数、复制/剪切意图和缺失状态；Shell 图标继续由专属 Shell/COM STA 提取并使用有界缓存；
- 列表使用 keyset 分页和 Recycling 虚拟化，筛选在仓储查询中完成，不先把全量历史加载到内存；
- 空历史时显示真实引导“复制内容会自动出现在这里 / 支持文本、文件和图片”；监听暂停、存储降级和捕获失败分别显示真实状态，不能共用空状态掩盖错误；
- 卡片整体的主动作是“粘贴到呼出前目标”；固定和删除为明确的次动作。键盘 Enter 等价主动作，Delete 需要可撤销或明确确认策略，自动化名称包含类型、摘要和时间；
- 搜索接入仍属于后续独立切片。M2 的四类筛选只作用于剪贴板仓储查询，不使顶栏搜索框变成编辑器。

## 9. 取用/粘贴状态机

```text
Idle
  → Materializing      # 从仓储/资产构造多表示 replay payload
  → WritingClipboard   # Clipboard STA 写回，取得 receipt
  → HidingWorkspace    # 复用同代 Workspace 隐藏事务
  → RestoringTarget    # 恢复呼出前顶层和必要子焦点
  → VerifyingCurrent   # receipt 仍是当前剪贴板
  → InjectingPaste     # 仅前台、焦点、sequence/marker 均确认后 Ctrl+V
  → Completed | CopiedOnly | Failed | Cancelled
```

关键门禁：

- 每次点击先认领唯一 `PasteActionGeneration`；双击、重复动画完成、隐藏、设置、退出或下一动作都不能产生两次写回/注入；
- 先验证历史资产和文件引用，再写剪贴板。写入失败时工作台保持可见；
- 写入成功后才隐藏、退出 Topmost、恢复目标并观察真实前台/焦点；不能以 API 返回值或固定延时冒充恢复成功；
- 恢复失败、UIPI 权限不匹配或目标失效时不注入，降级为 `CopiedOnly`，明确提示“已复制，可手动粘贴”；
- 写回后到注入前若外部程序再次改变剪贴板，`IsCurrentAsync` 失败，绝不能把第三方的新内容粘贴出去；
- 文件 `Move` 记录只有在卡片明确显示“剪切”且用户执行主动作时才写回 MOVE。次动作“复制”必须显式改写为 COPY，防止无提示移动；
- 删除历史只删除 Vok 元数据/资产引用，绝不删除文件条目指向的用户文件。

## 10. 生命周期、隐私与降级

- 监听服务随应用启动，独立于主窗口是否显示；应用退出时先停止接收新 update，再取消/收尾捕获和图片任务，最后释放 listener HWND、Clipboard STA、仓储和 SQLite；
- 工作台隐藏不清空剪贴板 UI 投影，也不停止监听；下次呼出直接展示已提交的最新页；
- 模块暂停后仍维护当前 sequence 水位，但不读取正文；恢复时不追溯捕获暂停期间内容，避免把旧系统内容伪装成新复制；
- 数据库损坏、资产缺失或图片解码失败必须局部隔离：文本/文件仍可加载，损坏图片显示不可用卡片并允许删除，应用保持可启动；
- 默认不保存来源窗口标题、完整进程命令行或任何正文日志。未来来源应用排除列表若进入 M3，只保存用户明确选择的稳定应用标识；
- 不宣称“加密剪贴板历史”。在威胁模型、密钥所有权和恢复流程获批前，数据保护依靠本地目录、短保留期、容量上限、隐私 marker、暂停和一键清理。

## 11. 分阶段实施

### M2.0：平台探针与契约

- 建立独立 Clipboard STA、listener HWND、sequence 协调器、格式枚举器和只含计数的诊断快照；
- 用可控 Win32 producer 建立多格式、延迟渲染、忙占用、自写和隐私 marker 测试语料；
- 先验证消息合并与一次打开内快照一致性，不接 UI、不写真实历史。

隔离真实 Win32 探针使用独立非交互 Window Station，因此不会读取或覆盖交互桌面的用户剪贴板：

```powershell
pwsh -NoProfile -File tools/Probe-M2Clipboard.ps1
```

状态：已完成。Core 已提供不可变捕获结果、格式目录、诊断快照与只按相等性工作的 sequence 协调器；Infrastructure.Windows 已提供独立 Clipboard STA、message-only listener HWND、一次打开内白名单快照、有界忙退避、隐私/实例 marker 和有界结果流。默认自动测试不改写用户剪贴板；真实即时多格式、系统合成格式、延迟渲染和隐私跳过已在独立非交互 Window Station 探针中通过。M2.1 已在此契约之上完成产品接入。

### M2.1：文本纵向切片

- 完成 Unicode/ANSI、HTML、RTF 捕获、表示层、精确去重、SQLite 分页和文本卡；
- 完成固定、删除、多表示写回与安全粘贴状态机；
- 以记事本、终端、IDE、Word 和浏览器完成真机往返。

状态：已完成并通过验收。Unicode/ANSI/OEM、HTML 和 RTF 经有界无执行能力规范化后形成一条多表示文本记录；schema 3 在既有 WAL 执行器中提供精确语义去重、最新回顶、64 条 keyset 分页、固定与级联删除，右侧 Recycling 列表已完成浅/深真实 WPF 渲染。写回由 Clipboard STA 串行执行，receipt 同时绑定 sequence、instance、operation 和 semantic hash；粘贴按 Materialize→Write→Hide/Restore→Verify→Inject 逐代门禁，失败明确降级。非交互 Window Station 探针已真实验证多表示写回、自写抑制和外部覆盖令 receipt 失效；2026-08-28 用户确认真机文本捕获、历史展示与粘贴测试通过，只读复核真实用户库已升 schema 3 并写入剪贴板记录。

### M2.2：文件列表纵向切片

- 完成 `CF_HDROP`、复制/剪切意图、路径规范化、文件卡、缺失状态和 Shell 图标；
- 完成 COPY/MOVE 写回、显式取用和“删除历史不删源文件”验证；
- 虚拟文件只输出明确不支持诊断，不在本阶段物化附件。

状态：已完成并通过验收。首次物理验收揭示 Explorer COPY 的 `Preferred DropEffect=5 (COPY | LINK)` 被旧实现按精确值白名单拒绝，因此有效 `CF_HDROP` 没有进入仓储，真实库当时只有一条路径文本记录。实现现按 `DROPEFFECT` 掩码区分 COPY/MOVE，COPY 与 MOVE 同时出现仍整条拒绝，避免模糊值被猜成移动。Release 58/58 Core 与 246/246 Windows/WPF 共 304/304 测试、0 警告构建、win-x64 自包含发布、PowerShell AST 和隔离 Window Station 探针通过；探针 COPY 语料已改为真实观测值 5。默认 Release PID 20420 运行且响应正常，交互同形语料已令真实用户库生成首条 `FileList` 与 `clipboard_files` 路径行；2026-08-28 用户复测 Explorer 文件复制/剪切、文件卡展示及实际粘贴均符合预期，同意提交。重启 replay、混合 DPI 和离线/权限路径保留为后续专项回归，不阻塞 M2.2 收口。

#### M2.2.1 实施前基线与交付边界

实施前，M2.1 已经在同一个 Clipboard STA 快照中复制 `CF_HDROP` 与 `Preferred DropEffect` 的 HGLOBAL 字节，但业务规范化器会在出现文件格式时安全跳过文本；schema 3、回放载荷、writer 和卡片仍是纯文本形态。本轮 M2.2 已按这些边界增量扩展，没有更换 listener、sequence coordinator、自写 marker/receipt、SQLite 执行器或 Workspace 粘贴门禁。

本阶段交付：

- 识别一个有效 `CF_HDROP` 中按来源顺序排列的文件与目录绝对路径；支持 Unicode，兼容严格可解码的 ANSI `DROPFILES`；
- 读取 4 字节 `Preferred DropEffect` 并按 `DROPEFFECT` 位掩码解析：仅有 COPY 操作位时记为复制，仅有 MOVE 操作位时记为剪切；允许 Explorer 附带 LINK、SCROLL 或未来扩展位，COPY 与 MOVE 同时出现、仅 LINK/未知位或畸形载荷则报告不支持；缺失时记为 Unknown，任何模糊值都不得猜测为 MOVE；
- 有效 `CF_HDROP` 优先于同一快照附带的文本、HTML、位图或文件名降级格式，避免把图片文件显示成图片或路径文本；
- 路径引用、意图、顺序、数量和文件卡持久化；源文件内容不复制进 Vok，不解析 `.lnk`，不跟随符号链接或重解析点；
- 文件卡、首项 Shell 图标、COPY/MOVE 标记、可确认的缺失/暂不可访问状态、固定、删除、主粘贴动作和强制 COPY 次动作；
- 用 `CF_HDROP` + `Preferred DropEffect` 写回，继续使用 M2.1 receipt 防止自写回流与外部覆盖竞态。

本阶段不支持 `FileGroupDescriptorW/FileContents`、Shell namespace PIDL、云盘按需物化、Outlook 附件等虚拟文件。平台只编目这些格式并发布不含文件名/正文的 `UnsupportedVirtualFiles` 诊断；不能把描述符名称冒充本地路径，也不能主动请求任意 `IStream`。

#### M2.2.2 Core 业务模型

将当前文本专用规范化入口提升为按快照只选择一种主业务内容的 router。文本和文件各自由窄 normalizer 负责，App Controller 只对 router 的结果做类型分派，不自行重复格式优先级判断：

```text
ClipboardNormalizedContent
├─ ClipboardTextContent
└─ ClipboardFileListContent
   ├─ SemanticHash
   ├─ CapturedIntent: Unknown | Copy | Move
   └─ Files[]
      ├─ Ordinal
      ├─ OriginalPath
      ├─ CanonicalPath
      └─ DisplayName

ClipboardEntry
├─ Kind
├─ FileIntent?             # 文本为 null
├─ ItemCount               # 文本为 0
└─ 既有排序、固定、计数与 revision 字段

ClipboardReplayPayload
├─ Representations[]       # 文本
├─ FileIntent?             # 文件回写的实际意图
└─ Files[]                 # 文件回写
```

`Unknown` 是捕获事实，不是 MOVE。主动作与“复制”次动作对 Unknown 都生成 COPY replay payload；只有存储值明确为 Move 且用户触发卡片主动作，实际回写意图才是 MOVE。强制 COPY 会按实际 COPY 意图重新计算 replay semantic hash，使 marker/receipt 描述真实写回内容，而不是沿用剪切记录的哈希。

文件语义哈希使用带版本前缀和长度字段的二进制输入，包含意图、项目数及每个项目按顺序排列的 Windows 大小写不敏感规范路径；不能用分隔符拼接造成歧义。顺序、重复项目和 COPY/MOVE 都是用户可观察语义；仅路径大小写差异不制造新卡。

#### M2.2.3 `DROPFILES` 解码与路径规则

文件 normalizer 在系统剪贴板已关闭后解析自有字节，绝不保留 HDROP/HGLOBAL：

1. payload 至少包含 20 字节 `DROPFILES`；按 little-endian 读取 `pFiles` 和 `fWide`，要求偏移不小于结构大小且落在 payload 内，Unicode 数据偏移必须为 2 字节对齐；
2. 从 `pFiles` 起读取以额外 NUL 结束的字符串序列；每项必须非空，整个序列必须存在双 NUL 终止，终止后的 padding 不参与语义；
3. Unicode 使用严格 UTF-16LE；ANSI 使用系统 ANSI code page 与异常 fallback，无法无损解码时整条文件记录失败，不提交半条历史；
4. 每项必须是无内嵌 NUL、可由 `Path.GetFullPath` 处理的 Windows fully-qualified 路径；统一目录分隔符并仅去除非根路径的尾部分隔符，保留 UNC/长路径和来源显示文本；
5. canonical path 只用于相等性与写回校验，不解析快捷方式、不访问文件内容、不改变实际大小写，也不把网络不可访问误判为文件不存在；
6. 任一项目畸形、超限或不完整时拒绝整个 `CF_HDROP`，同快照附带文本也不降级成文本卡，避免把损坏文件传输误分类。

新增集中配置 `ClipboardFileNormalizationOptions`，初始命名门槛为最多 4096 项、单路径最多 32,767 字符、所有路径合计最多 1,048,576 字符；原始 HGLOBAL 仍同时受 `ClipboardCaptureOptions` 的单格式与整快照字节预算约束。门槛只在真实大批量 Explorer 语料后调整，不散落固定数值。

文件可用性不是捕获提交的同步前置条件。卡片展示服务在非 UI、非 Clipboard STA 上异步检查：明确 `ERROR_FILE_NOT_FOUND/ERROR_PATH_NOT_FOUND` 才标记 Missing；权限、离线网络、未知重解析或其他 I/O 失败标记 Unavailable，不冒充 Missing。回放前重新校验路径结构并拒绝已明确 Missing 的本地引用；远程/暂不可访问引用保留 Unknown/Unavailable 事实，由用户决定是否写回，不能为等待网络阻塞 UI 或应用退出。

#### M2.2.4 schema 4 与仓储事务

schema 4 在既有单连接 WAL 执行器中扩展，不新建数据库线程：

```text
clipboard_entries
  + file_intent INTEGER NULL
  + item_count INTEGER NOT NULL DEFAULT 0

clipboard_files
  entry_id TEXT NOT NULL
  ordinal INTEGER NOT NULL
  original_path TEXT NOT NULL
  canonical_path TEXT NOT NULL
  display_name TEXT NOT NULL
  PRIMARY KEY (entry_id, ordinal)
  FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE
```

- 启动时同时核验 `user_version`、三张剪贴板表的真实列集合、外键和关键索引；只接受无剪贴板表、已知 schema 3 或完整 schema 4，未知/半迁移结构原样回滚并让存储明确失败；
- schema 3→4 在单事务中增加两列、创建 `clipboard_files`，既有文本记录原样保留为 `file_intent=NULL,item_count=0`，提交成功后才写 `PRAGMA user_version=4`；
- `clipboard_entries(kind, semantic_hash)` 继续作为内容幂等键，因为文件 hash 已包含意图；不再把 `file_intent` 重复加入唯一键；
- `UpsertFileListAsync` 与 entry/files 的插入、重复捕获回顶、capture count/revision 更新和旧文件行替换在同一事务；分页仍只查 entry 轻量投影，文件明细只在卡片呈现或 replay materialize 时按 entry 加载；
- `RemoveAsync` 只依靠外键级联删除 Vok 元数据。自动测试以真实临时文件断言固定、重复、删除、迁移、失败回滚和仓储 Dispose 都不会调用文件删除/移动 API，也不会改变源文件内容；
- `GetReplayPayloadAsync` 对文本仍要求表示层，对文件则要求至少一条连续 ordinal 的文件行；缺行、空列表、kind/intent 不一致视为局部损坏并返回不可回放，不拼出部分列表。

#### M2.2.5 Shell 展示与文件卡

现有 `WindowsLauncherShellService` 的 STA、取消认领与 256 项图标缓存可复用实现机制，但 `ILauncherShellService` 接口绑定启动台模型。实施时提取由 DI 唯一拥有的有界 `WindowsShellStaExecutor`，启动台适配器与新增 `IClipboardFilePresentationService` 都只向它投递窄命令；适配器不得分别创建或释放第二条 Shell/COM STA。

剪贴板展示服务只为每条文件卡的首项请求 32–48 px Shell 图标与可用性状态，返回 PNG 字节和非敏感状态；App 在 UI Dispatcher 上解码并 Freeze `BitmapImage`。请求按当前 64 条数据页排队、受有界缓存和 lifetime cancellation 控制；回收容器不持有 COM/HBITMAP，条目离开集合或应用退出后迟到结果不得写回新 ViewModel。

文件卡沿用当前 Recycling ListBox，不创建第二列表：

- 首行显示首项文件名，随后显示“另外 N 项”；次行显示所在路径摘要与捕获时间；
- Copy 显示“复制”，Move 显示高辨识度“剪切”，Unknown 显示“意图未知 · 将复制”；Missing 显示“源文件已失效”，Unavailable 显示“暂不可访问”，两者不能混用；
- 主按钮沿用“粘贴到呼出前目标”；Move 的 tooltip 和 AutomationName 必须明确“粘贴可能移动源文件”；
- 文件卡增加独立“复制”次动作，只把文件列表以 COPY 写到系统剪贴板并保持工作台可见，不注入 Ctrl+V；固定和卡内二次确认删除继续复用现有行为；
- 删除成功文案明确“只删除 Vok 历史，不删除源文件”；空状态更新为当前真实能力“复制文本或文件后会自动显示”。筛选按钮仍不提前启用。

#### M2.2.6 COPY/MOVE 回写与粘贴门禁

`WindowsClipboardWriter` 按 payload kind 分支，文件分支一次性构造并写入：

1. `CF_HDROP`：20 字节 `DROPFILES`，`pFiles=20`、`fWide=TRUE`，其后为每个 fully-qualified UTF-16LE 路径及最终额外 NUL；禁止空列表、相对路径、内嵌 NUL和总大小溢出；
2. `Preferred DropEffect`：4 字节 little-endian DWORD，只允许 `DROPEFFECT_COPY=1` 或 `DROPEFFECT_MOVE=2`；
3. `Vok.Internal.ClipboardWrite`：继续绑定 instance、operation 和这次实际 replay semantic hash。

三种格式与 `EmptyClipboard` 仍由一个可认领的 Clipboard STA write command 拥有。任一 `GlobalAlloc/GlobalLock/SetClipboardData/CloseClipboard` 失败必须释放尚未转移的句柄并使动作失败；已转移给系统的句柄不得再次释放。receipt、sequence 核验、外部覆盖失效和 SelfWrite disposition 不增加第二套状态。

文件主动作继续走 `Materialize→Write→Hide/Restore→Verify→Inject`。状态文案按实际意图区分：COPY 失败后可提示“已复制，可手动粘贴”，MOVE 则必须提示“已写入剪切；手动粘贴将移动源文件”，不能用“已复制”掩盖剪切。已知 Missing 在 Write 前失败并保持工作台可见；恢复失败、UIPI 降级、焦点变化或注入失败都不向未确认窗口发送输入。强制 COPY 次动作只执行 Materialize→Write→Verify，不隐藏、不恢复、不注入。

#### M2.2.7 实施顺序与自动门禁

按下列顺序一次只打开一条新风险链；在最后一步前不把半成品文件卡接入应用组合根：

1. **真实格式语料**：扩展隔离 Window Station producer，生成 Unicode/ANSI、多项目、COPY/MOVE/无意图、畸形 header/offset/终止符和虚拟文件目录；先锁定 raw snapshot 与格式优先级；
2. **Core + normalizer**：加入文件模型、router、严格 `DROPFILES` parser、意图解析、路径规范化和版本化哈希；覆盖顺序、大小写、根路径、UNC、长路径、重复项目、所有门槛与整条拒绝；
3. **schema 4**：完成已知 schema 3 单事务迁移、文件 upsert/replay/cascade 与结构失败回滚；以 M2.1 文本测试证明旧路径无回归；
4. **Shell + UI**：抽取单一 Shell STA executor，增加文件展示适配器、异步图标/状态与异构卡片；真实浅/深、80 卡、100%/150%/200% 渲染检查容器回收、裁切和可访问名称；
5. **writer + actions**：加入 HDROP/effect 写回、实际意图 hash、强制 COPY 与意图化状态文案；覆盖取消认领、部分句柄转移、Close 失败、双击、旧代、外部覆盖、恢复/焦点失败及成功顺序；
6. **产品接入与发布**：组合根一次启用完整文件纵向切片；Release 全量测试、0 警告构建、win-x64 自包含发布、PowerShell AST、句柄/线程退出和独立探针全部通过后再做真机验收。

隔离探针最低断言：一次 Explorer 等价 COPY 与一次 MOVE 各只形成一条 FileList；项目顺序和 Unicode 路径往返不变；Vok 写回包含有效 HDROP/effect/marker；监听结果为 SelfWrite；外部覆盖后旧 receipt 失效。自动测试不得读写交互桌面的真实剪贴板。

真机验收只使用临时测试目录中的可丢弃文件：Explorer 复制/剪切单文件、多文件、目录和混合列表，确认卡片名称/数量/意图/图标；重启后历史仍可 replay；COPY 不改变源，MOVE 只在明确剪切主动作后由 Explorer 移动；删除历史不触碰源；失效、离线网络、权限不足和错误目标均明确降级且不误注入。验收后只读确认用户库 schema 4 与文件行存在，再宣布 M2.2 完成。

### M2.3：图片纵向切片

- 完成 PNG/DIBV5/DIB/Bitmap 的白名单读取、Alpha 正确的无损规范化、像素级去重、原图/缩略图资产事务和图片卡；
- 完成 PNG + 兼容位图多表示写回；
- 以 Snipping Tool、Print Screen、浏览器图片、聊天软件和透明 PNG 完成真机往返。

状态：详细实施规划、M2.3.0 格式/所有权/容量门禁与 M2.3.1 单候选 `Inline | LeasedBlob` 所有权均已完成。产品已按完整目录把图片置于 FileList/Text 之前，只取得 PNG→DIBV5→DIB 的一份自有载荷，并闭环队列丢弃、取消、关闭失败和退出清理；尚未把图片提交历史。下一步是 M2.3.2 严格 decoder、规范像素和 pixel identity，不能因轻量 header preflight 通过而宣称图片规范化已验证。

#### M2.3.0 实施前基线、语料与交付边界

M2.3.0 开始时，schema 4、`ClipboardContentNormalizer`、`ClipboardReplayPayload`、仓储和卡片只闭环 Text/FileList；平台快照会枚举并复制全部注册图片 HGLOBAL，结果流只按 16 个快照限项并以完整 `byte[]` 承载每份载荷。一张 7680×4320×32bpp 的未压缩位图仅像素即为 132,710,400 字节，超过旧 64 MiB 单格式门槛，也证明“把 PNG、DIBV5 和 DIB 都复制进托管队列”不能作为 M2.3 基线。M2.3.1 已替换这段载荷取得机制；schema、normalizer、仓储和卡片仍未接入 Image。

编码前新增只记录非敏感元数据的真实格式取证：每次用户动作记录 sequence、格式名称/顺序、载荷字节数、DIB header 类型、像素尺寸、方向、位深、压缩、色彩空间/Alpha 声明、解码成败和各表示是否得到相同像素身份；不写图片字节、缩略图、来源窗口标题、进程命令行或文件路径。最低语料如下：

1. Snipping Tool 矩形截图与全屏截图；Print Screen；
2. Edge/Chrome 中复制普通图片与带透明通道 PNG；
3. Word、PowerPoint 或其他已安装 Office 应用中复制图片和形状；
4. 一种本机常用聊天软件；不可用时明确标为未验证，不能用可控 producer 冒充；
5. 100%/150%/200% DPI、4K 和可获得的 8K/等价合成语料，并记录 DPI 元数据在各表示间是否稳定；
6. 现有独立 Window Station producer 增加可控 PNG、DIBV5、DIB 和纯 `CF_BITMAP`，覆盖同像素多编码、表示内容冲突、延迟渲染和畸形数据。

当前已交付两件 Gate 0 工具：

- `tools/Record-M2ImageClipboard.ps1` 在交互桌面监听上述有序动作，报告默认写到 `%TEMP%\Vok.ClipboardImageRecorder`。JSONL 只序列化 sequence、格式目录/顺序、图片载荷长度和 PNG/DIB header 字段，不写载荷、像素、哈希、窗口标题、进程信息或路径；现阶段的 `isHeaderValid` 只是 header 检查，不能冒充完整 codec 解码成功。执行：`pwsh -NoProfile -File tools/Record-M2ImageClipboard.ps1`；自定义来源可用 `-Labels source-a,source-b`，未显式传 `-Count` 时自动按标签数结束；
- `tools/Probe-M2Clipboard.ps1` 的隔离 Window Station producer 已发布有效 PNG + DIBV5 + DIB、多表示图片、真正的 `CF_BITMAP` 和畸形 DIB。2026-08-28 实测纯 `CF_BITMAP` 快照按顺序得到 MetadataOnly `CF_BITMAP`，并在同次读取中捕获 Windows 合成的 `CF_DIB` 68 bytes 与 `CF_DIBV5` 152 bytes；多表示载荷捕获 PNG 81 bytes、DIBV5 140 bytes、DIB 56 bytes，畸形 DIB 不被现有 router 提交。探针 `schema=4, reportVersion=2, success=true`，证明产品可继续保持“不跨 `CloseClipboard` 持有 HBITMAP、优先复制系统合成 packed DIB”的所有权边界。

记录器刻意不执行完整 codec 解码或像素身份比较；header 报告不能冒充 decoder 通过。严格 decoder 原型仍必须在进入 schema 5 前回放已采与合成语料，补齐每个表示的解码结果与像素等价组；报告只需序列化“成功/失败及等价组”，仍不得写像素或内容哈希。这是 M2.3.2 的实现门禁，不再阻塞 M2.3.1 的载荷所有权。

2026-08-28 首轮真实交互语料已完成 6/6，报告位于 `%TEMP%\Vok.ClipboardImageRecorder\m2.3-image-formats-20260828-223357-775.jsonl`：

| 动作 | 像素表示 | 关键结果 |
|---|---|---|
| Snipping Tool 区域 | PNG 2631×1552；DIB/V5 2631×1552 | 同时出现 `CF_HDROP`、Shell/虚拟文件格式和图片格式；PNG 459,169 bytes，单份 DIB 约 16.33 MB |
| Print Screen | PNG 3200×2000；DIB/V5 3200×2000 | 同样混有 `CF_HDROP`；PNG 697,153 bytes，单份 DIB 约 25.60 MB，是首轮最大语料 |
| 浏览器普通图片 | PNG/DIB/V5 420×420 | PNG 为 RGB、DIBV5 为 `BI_RGB` 且写有 Alpha mask；同时带 HTML/URL |
| Office 图片/形状 | PNG 865×541；DIB/V5 554×346 | 同一动作的表示尺寸已经冲突，PNG 保留更高像素尺寸，不能假定 PNG 与 DIB 总是同像素 |
| 浏览器“透明 PNG” | PNG/DIB/V5 152×148 | PNG 实为 color type 2 且无 `tRNS` 取证，未证明透明；DIBV5 仍是 `BI_RGB` + Alpha mask 异常组合，必须用已知 Alpha 样本复测 |
| QQ 图片 | DIB/V5 304×819，无 PNG | 同时出现 HTML、`CF_HDROP` 和图片格式，证明 DIB 家族后备不可删除 |

首轮据此锁定两项并保留三项门禁：注册 `PNG` 是主候选，`CF_DIBV5`/`CF_DIB` 是无 PNG 来源的必要后备；图片格式与 `CF_HDROP` 同现时，不能继续采用“文件绝对优先”。仍须用 Explorer 复制真实 PNG 文件作为反例门禁，用已知透明 PNG 判断 Alpha，并用 8K 语料观察 oversize 的真实原始长度。所有 DIB 的 X/Y pixels-per-meter 均为 0，旧版记录器又未扫描 PNG `pHYs`，所以 DPI 语义仍未锁定。记录器现已补 `pHYs`、`tRNS` 和 `sRGB` 辅助块取证，并能生成已知透明 256×256、透明 8K 与浏览器样本页；这些新增字段必须通过定向复采后才进入容量或语义结论。

第一次受控复采因包装脚本把单个逗号字符串在拆分前按 `$Labels.Count=1` 计算，只监听到一条；记录器又默认跳过不含图片格式的纯文件快照，因此 Explorer 动作未入报告，下一次浏览器动作继承了 `explorer-image-file` 标签。格式目录中的 Chromium token、HTML/URL 证明该条实际是浏览器复制的已知透明 256×256 PNG，不能按标签误作 Explorer 证据。该条 PNG 为 color type 6，确认可信 Alpha；源文件声明约 96 DPI，但 Chromium 重新发布的 PNG 已无 `pHYs`，DIB/V5 pixels-per-meter 仍为 0，因此浏览器路径不提供稳定 DPI。包装脚本现先规范化逗号/数组标签再推导数量，显式 `-Count` 仍优先；记录器新增 `-IncludeNonImage` 以允许只记录目录元数据的 Explorer 反例。原触发形式、数组/空白标签、显式 Count 与 1 秒超时真实启动烟雾均通过。剩余门禁缩为 Explorer 复制生成的 PNG 文件和浏览器复制生成的 8K PNG 两项。

最终受控复采完成 2/2，报告为 `%TEMP%\Vok.ClipboardImageRecorder\m2.3-image-formats-20260828-230407-480.jsonl`：Explorer Ctrl+C 生成的 PNG 文件只发布 Shell/HDROP/PreferredDropEffect/虚拟文件目录，没有 PNG/DIB/Bitmap 像素表示；浏览器复制生成的 7680×4320 Alpha PNG 发布 1,334,507-byte PNG，同时 DIB 为 132,710,440 bytes、DIBV5 为 132,710,524 bytes，二者均被旧 64 MiB 单格式门槛准确标为 Oversize。至此 R22 的精确分类通过正反语料：有已知像素格式即 Image，无像素格式而有 HDROP 才是 FileList。

M2.3.0 最终锁定：

| 选项 | 数值 | 含义/依据 |
|---|---:|---|
| `MaximumInputBytes` | 192 MiB | 容纳 8K packed DIB 约 126.56 MiB，并给 header/palette/profile/编码开销留界限 |
| `MaximumDimensionPixels` | 8,192 | 单边明确支持到 8K；更长超宽图首期拒绝而非隐式超配 |
| `MaximumPixelCount` | 41,943,040 | 40 Mi pixels，覆盖 7680×4320 的 33,177,600 pixels |
| `MaximumCanonicalBytes` | 160 MiB | `MaximumPixelCount × 4` 的 straight-alpha BGRA8 上限 |
| `InlinePayloadBytes` | 2 MiB | 首轮 PNG 最大 1.33 MB 可内联；16–132 MB DIB 不进入托管队列大数组 |
| `LeaseCopyBufferBytes` | 1 MiB | HGLOBAL→staging 分块缓冲，固定且可池化 |
| `ImageWorkerCapacity` | 4 jobs | 小图 burst 有界；不能形成现有 16 个大快照队列 |
| `MaximumReservedBytes` | 384 MiB | 每 job 按 `input bytes + canonical bytes` 精确预留；保证容纳两个受控 8K PNG 或一个最坏界限 job。三个同形受控样本合计只比上限少 518,463 bytes，空队列时仍可被精确接受，任何稍大第三项或已有占用都会过载拒绝 |
| `ThumbnailMaximumEdgePixels` | 512 | 不放大，单张最坏 BGRA decode 1 MiB，覆盖当前卡片可见尺寸 |
| `ThumbnailCacheBytes` | 32 MiB | 按解码字节限额而非条目数，足够当前可见/邻近容器且不会随历史线性增长 |

这些数值是 M2.3 首版安全上限，不是性能 SLA；M2.4 的真实连续截图和长期资源曲线可以下调或提出版本化扩容，但不得静默提高。DPI 不进入 `Vok.Image.Pixel.v1`：真实 DIB 全为 0，受控 PNG 的 96 DPI `pHYs` 又被 Chromium 丢弃。有效 DPI 只可作为 nullable replay metadata 保存，缺失时 DIBV5 写回使用 3,780 pixels/meter（约 96 DPI）；它不得让同像素生成不同历史或不同 canonical asset。PNG 的 color type 4/6 或合法 `tRNS` 是可信 Alpha；`BI_RGB` 的孤立 Alpha mask 只记诊断，不能参与 Alpha 语义。

本阶段交付：图片捕获、像素身份、内容寻址主图/缩略图、schema 5、可见容器缩略图、固定/删除、PNG + DIBV5 写回及沿用既有安全粘贴状态机。以下仍不实现：EMF/WMF、动画、多帧、HDR/浮点、高于 8 bit/通道的保真编辑、OCR、图片编辑、来源应用猜测、“截图”标签、图片拖出和顶栏搜索。高位深来源可以在门禁明确记录为降级到 sRGB 8-bit 展示语义，但不能宣称原始位深无损。

#### M2.3.1 快照路由与二进制载荷所有权

继续先枚举完整格式目录、检查隐私 marker 和自写 marker，再按目录决定本次只需要复制的候选，避免复制业务上必然不会使用的大对象：

1. 目录中出现注册 `PNG`、`CF_DIBV5`、`CF_DIB` 或 `CF_BITMAP` 任一像素表示时，先归为 Image 候选；随附 `CF_HDROP`、Shell/虚拟文件、HTML 或 URL 只作为未读取的目录证据，不能把 Snipping Tool/QQ 图片抢成 FileList。若图片候选后来畸形，也不降级为临时文件或文本；Explorer 复制真实图片文件的定向门禁通过后才把此规则接入产品；
2. 只有目录没有图片表示而含 `CF_HDROP` 时才进入 M2.2 FileList 路由并复制文件所需载荷；纯文本规则保持其后。分类一旦确定，不因首选载荷损坏而跨类回退；
3. Image 每次只复制一个主候选：注册 `PNG` 优先，否则 `CF_DIBV5`，再否则 `CF_DIB`。PNG 的 HGLOBAL 复制失败、超限或同次轻量签名/IHDR preflight 失败时，才在仍持有剪贴板的本轮尝试一个 DIB 家族候选；PNG 已复制且 header 合法后不再复制 DIB，也不读取随附 HTML/URL/HDROP。8K 语料证明“为事后 codec fallback 总是再复制 DIB”会为 1.33 MB PNG 增加约 132.71 MB 重复载荷和持有剪贴板时间；
4. `CF_BITMAP` 是图片来源信号，不把 HBITMAP 当 HGLOBAL。Windows 会为只发布 `CF_BITMAP` 的来源合成 DIB/DIBV5，Vok 必须在同一次 `OpenClipboard` 内取得并复制合成的 DIB；独立 producer 必须证明该路径。若系统未给出可复制 DIB，则本次明确记为 UnsupportedImage，不能在 `CloseClipboard` 后继续使用 HBITMAP；
5. PNG 通过轻量 preflight 但在关闭剪贴板后 codec 解码失败时，本次图片明确失败，不再拥有 DIB 事后后备；这是单候选有界所有权的显式取舍。Office 已证明 PNG/DIB 可有不同尺寸，因此不得拼接两个表示的颜色、Alpha 或像素，也不得在 decoder 阶段偷偷切换语义。

图片载荷改为显式所有权的 `Inline | LeasedBlob`：小载荷仍可内联，不超过命名阈值的大 HGLOBAL 在剪贴板仍打开时用固定大小缓冲分块复制到同卷 staging 文件；`ClipboardCaptureResult` 只携带只读 lease、字节数和格式元数据。Controller 正常、异常、取消、队列丢弃和退出路径都必须在 `finally` 释放 lease，未提交 staging 文件不能跨进程重启遗留。Core 契约只暴露只读流/长度/释放语义，不暴露 HGLOBAL、HBITMAP、WPF `BitmapSource` 或任意可写临时路径。

图片门槛从通用 HGLOBAL 门槛拆出，并集中在单一不可变 options 对象，使用 M2.3.0 表中的精确默认值。job 预留量以 checked arithmetic 计算 `source byte length + width × height × 4`；队列同时按 4 jobs 和 384 MiB 已预留字节拒绝。过载只产生计数和明确状态，不阻塞 Clipboard STA、不无限排队，也不留下 lease；所有上限必须有边界值、加一拒绝和取消/退出归零测试。

实现状态（2026-08-29）：M2.3.1 已完成。Core 只暴露只读流、长度、`Inline | LeasedBlob`、图片尺寸和幂等释放语义；Clipboard STA 在同次打开内做轻量 PNG IHDR/DIB 尺寸门禁、checked 预留，并以 1 MiB 缓冲复制大 HGLOBAL 到 `%LOCALAPPDATA%\Vok\assets\clipboard\.staging`。启动只清理 `capture-*.blob`，结果正常消费、router 拒绝、容量拒绝、Close 失败、队列满丢弃和服务退出均有归零测试。隔离探针 `reportVersion=3` 实测多表示只捕获 PNG、纯 Bitmap 只捕获系统合成 DIBV5、畸形图片为 Unsupported；完整 Release 为 62/62 + 255/255，共 317/317。严格 codec、像素身份、worker 消费和历史提交仍属于 M2.3.2。

#### M2.3.2 严格解码、规范像素与语义哈希

新增单一 `ClipboardImageNormalizer`，在专属图片 worker 上消费已经脱离系统句柄的载荷。它先做 header 级门禁再创建 WIC/WPF Imaging decoder：

- PNG 要求正确签名、单帧、有效 IHDR、非零且受限的尺寸；完整 chunk/CRC 和像素解码交给 Windows PNG codec，解码器异常只令该候选失败；
- DIB 只接受可完整界定的 `BITMAPINFOHEADER`/V4/V5 packed DIB；用 checked arithmetic 校验 header、mask、palette、profile、行 stride、像素偏移和最终长度。首期只接受 1/4/8/16/24/32 bpp 的 `BI_RGB`、`BI_BITFIELDS`/明确 Alpha bitfields；RLE、内嵌 JPEG/PNG、OS/2 header 和未知压缩明确不支持；
- 正 `height` 按 bottom-up 翻转，负 `height` 按 top-down；`height=int.MinValue`、零尺寸、planes≠1、重叠/非连续 mask、越界 profile 和乘法溢出均整条拒绝；
- DIBV5 仅在位深、compression 与显式 Alpha mask 组成受支持且不重叠的 bitfields 契约时保留 Alpha。首轮 Chromium 语料实测 `BI_RGB` + `bV5AlphaMask=0xFF000000`，但 Windows 的 32bpp `BI_RGB` 契约明确高字节未使用，因此该组合只记兼容性诊断，不能让 mask 单独推翻 compression 契约；无可信 PNG 后备时按不透明归一，不能用“只要存在非零值就猜 Alpha”的启发式；
- `PROFILE_EMBEDDED` 只有在 profile 完整落入载荷并受字节预算约束时才允许进入色彩转换；`PROFILE_LINKED` 可能指向本地或网络文件，Vok 不打开该路径并把候选记为不支持，不能让剪贴板触发外部 I/O；
- 所有有效来源通过 Windows Imaging Component 归一为 top-down、sRGB、straight-alpha BGRA8。premultiplied 来源必须正确还原，不能把 PBGRA 字节直接当直通 BGRA；规范化输出不携带 EXIF、文本、来源路径或其他原始 metadata。

语义哈希使用版本化二进制输入 `Vok.Image.Pixel.v1 + width + height + pixel-format-tag + top-down row bytes`，逐行/分带计算，不把完整 4K/8K 像素再次拼成大数组。编码字节、PNG metadata、DPI、DIB padding、上下行存储方式和来源格式不参与，因此同一规范像素的 PNG/DIBV5/DIB/Bitmap 只形成一条记录；RGBA 任一可观察通道不同则是不同内容。DPI 仅作为 nullable replay metadata，缺失时按约 96 DPI 写回；未来若有产品证据要求物理尺寸成为身份，必须升级哈希版本和 schema，不能改变 `v1` 含义。

规范输出只生成两份无 metadata PNG：原尺寸 canonical PNG 和不放大的等比 thumbnail PNG。缩略图最大边是命名选项，使用 WIC scaler 一次生成；哈希、缩放和 PNG 编码都从同一个规范 source 拉取分带像素，禁止为哈希、主图和缩略图各保留一份完整 BGRA 数组。

#### M2.3.3 有界图片 worker 与失败隔离

新增由 DI 唯一拥有的 `WindowsClipboardImageExecutor`：一条专用后台线程、单消费者、有界 channel 和字节预留，不使用散落 `Task.Run`，不复用 Clipboard STA、Shell/COM STA、SQLite 执行器或 WPF UI Dispatcher。WIC/WPF Imaging 对象只在线程内使用；需要跨到 UI 的对象只能是已从 thumbnail asset 解码并 `Freeze()` 的小型 `BitmapImage`。

每个 job 在入队前凭 header 尺寸预留最大规范字节；无法安全读取尺寸、超像素/字节预算或队列预留失败时不开始解码。job 只有 `Queued→Claimed→Completed|Failed|Cancelled` 一次终态，取消与 worker 认领竞争同一所有权；旧 job、迟到 codec 回调和退出 watchdog 不能提交新记录。失败只隔离当前图片：文本/文件监听继续工作，UI 显示最近一次图片捕获失败/过载的非敏感状态，不能把 listener 整体标死。

退出顺序固定为：停止接收新 clipboard update → 完成/取消 active capture → 关闭结果流 → 取消并收尾图片 job → 删除未提交 staging → 释放图片线程 → 释放 Clipboard STA/asset store/SQLite。自动门禁直接观察 outstanding job、reserved bytes、lease、staging 文件、线程和 GDI/USER handle 都回到零或命名缓存上限；不得用 `GC.Collect` 或固定 sleep 制造回落。

#### M2.3.4 内容寻址资产与 schema 5

资产根为 `%LOCALAPPDATA%\Vok\assets\clipboard`，staging 位于同一卷；最终相对路径只由校验过的小写 SHA-256 和固定扩展名生成，例如 `<prefix>/<hash>.png`。文件先写 staging、flush、重新计算内容哈希和解码校验，再用同卷原子 rename 提交；同哈希已存在时必须核对长度/哈希后复用，不能覆盖未知内容。semantic hash 描述规范像素，asset hash 描述实际 PNG 文件字节，两者不得混用。

schema 5 在现有 schema 4 上增量扩展：

```text
clipboard_entries
  + pixel_width INTEGER NULL
  + pixel_height INTEGER NULL
  + has_alpha INTEGER NULL
  + dpi_x_milli INTEGER NULL       # replay metadata，不参与 Vok.Image.Pixel.v1
  + dpi_y_milli INTEGER NULL

content_assets
  hash TEXT PRIMARY KEY
  relative_path TEXT NOT NULL UNIQUE
  media_type TEXT NOT NULL
  byte_length INTEGER NOT NULL
  pixel_width INTEGER NOT NULL
  pixel_height INTEGER NOT NULL
  state INTEGER NOT NULL            # Ready | PendingDelete | Quarantined
  revision INTEGER NOT NULL
  created_utc TEXT NOT NULL

clipboard_asset_refs
  entry_id TEXT NOT NULL
  role INTEGER NOT NULL             # PrimaryPng | ThumbnailPng
  asset_hash TEXT NOT NULL
  PRIMARY KEY (entry_id, role)
  FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE
  FOREIGN KEY (asset_hash) REFERENCES content_assets(hash) ON DELETE RESTRICT
```

不在 `clipboard_entries` 重复保存 asset path，也不维护易漂移的手工 `ref_count`；通过 `clipboard_asset_refs(asset_hash)` 索引判断真实引用。Text/FileList 必须保持图片列全 NULL 且无 asset refs；Image 必须 `file_intent=NULL,item_count=0`、尺寸为正，并恰有 Ready 的 Primary/Thumbnail 两个角色。分页查询只 LEFT JOIN 两个轻量 asset hash，不读取 PNG 字节；replay 再单独物化 Primary。

schema 4→5 迁移在单一 SQLite 事务内增加列、资产表、引用表、外键与索引，既有文本/文件行原样保留，全部结构核验通过后才写 `user_version=5`。已知 schema 4、完整 schema 5 和无剪贴板表以外的任何半迁移结构都原样回滚。资产目录不可用只关闭图片捕获/取用并显示降级，不能阻止既有文本/文件历史加载。

图片提交遵循“文件先 Ready、数据库后引用”：asset coordinator 先原子提交并验证两个文件，再由 SQLite 单事务 insert/reuse asset rows、upsert Image entry 和替换两个 refs；数据库失败只会留下无引用 asset，绝不能留下数据库指向缺失文件。删除 Image entry 时先在事务内级联 refs，并把提交后已无引用的 asset 标为 PendingDelete；物理删除和 asset row 删除由同一个串行 coordinator 以 `(hash, revision)` 再认领，若中途被新捕获重新引用则取消删除。删除失败保留 tombstone 供启动重试，不能删除仍有引用的共享 asset；更完整的保留期/总容量清理仍属于 M2.4。

主图或缩略图缺失、哈希不符、解码失败时只把对应 asset 标为 Quarantined 并保留证据。主图损坏的卡片不可粘贴但仍可删除；仅缩略图损坏时显示不可用占位，主图仍可按验证结果回放。应用不得自动删除整库或掩盖为“空历史”。

#### M2.3.5 图片卡、可见性加载与缓存

图片继续进入现有单一 Recycling `ListBox`，不建立第二份集合或把所有图片一次加载进 ViewModel：

- 卡片展示等比缩略图，`Stretch=Uniform`，不裁切、不拉伸、不放大；显示 `宽×高`、捕获时间以及加载中/资产不可用状态，透明区域必须在主题安全背景上可辨认；
- 主动作仍是“粘贴到呼出前目标”，固定和卡内二次确认删除沿用现有语义；不在本切片增加编辑、OCR、保存到文件或拖出按钮；
- `AutomationName` 包含“图片”、像素尺寸、可用状态和时间，不把 asset hash/path 暴露给 UI；空状态更新为真实能力“支持文本、文件和图片”；
- thumbnail 只在 ListBox 容器已实现且图片元素 Loaded 时请求；Unloaded、容器回收、条目 revision 改变、删除和应用退出都取消绑定并释放 `ImageSource`。迟到结果必须同时匹配 entry id、asset hash、revision 和当前容器数据项才可应用；
- UI 只解码已经受限的小 thumbnail，并用 `BitmapCacheOption.OnLoad` 后关闭流、`Freeze()` 再绑定。缓存按解码字节而不是条目数设限，键为 asset hash；原图从不进入 ViewModel 或 UI cache。

真实 WPF 门禁覆盖浅/深主题、横/竖/超宽/超高/透明图片、80 个混合文本/文件/图片卡、100%/150%/200% DPI、连续滚动和 Recycling。断言当前页以外无数据库明细，未实现容器无 thumbnail decode，离开视口后 ViewModel 不持有原图或已逐出的缩略图。

#### M2.3.6 图片 replay、写回与安全粘贴

仓储的 replay 查询对 Image 只返回轻量 descriptor：semantic hash、Primary asset hash、像素尺寸/Alpha/DPI metadata。显式 `IClipboardImageReplayMaterializer` 在图片 worker 上打开并重新校验 canonical PNG，逐带重算 `Vok.Image.Pixel.v1`；hash、尺寸或格式不匹配则在写系统剪贴板前失败并保持工作台可见。物化产物使用有生命周期的只读 blob：canonical PNG 可直接引用 Ready asset，兼容 DIBV5 写入同卷临时 blob，避免在 Controller/VM 中形成大 `byte[]`。

`WindowsClipboardWriter` 的 Image 分支一次 `EmptyClipboard` 后依次写：

1. 注册格式 `PNG`：canonical PNG 字节；
2. `CF_DIBV5`：124 字节 V5 header + 32bpp bottom-up 像素，`BI_BITFIELDS`、R/G/B/A mask 明确、`LCS_sRGB`、无 linked/embedded profile；
3. `Vok.Internal.ClipboardWrite`：继续绑定 instance、operation 和图片 semantic hash。

Windows 已定义 DIBV5→DIB/Bitmap 的按需转换，因此基线不额外发布 `CF_BITMAP`，避免引入 HBITMAP/GDI 对象所有权；只有 Paint、Office、浏览器和聊天软件真机门禁证明某目标无法消费系统合成格式时，才单独评估显式 `CF_DIB`，不能凭猜测复制三份大位图。每个 blob 都按长度预分配 GMEM_MOVEABLE HGLOBAL 并分块复制；`SetClipboardData` 成功后所有权转给系统，失败时只释放未转移句柄，任一后续格式失败仍按既有部分写入清除策略收口。

图片主动作复用现有 `Materialize→Write→Hide/Restore→Verify→Inject` 和 generation/state machine，不新增第二套焦点逻辑。materialize/asset 验证/写入失败时窗口保持可见；写入成功但隐藏、恢复、焦点或注入失败时明确降级为“已复制图片，可手动粘贴”；外部覆盖令 receipt 失效，不注入第三方内容。双击、取消、退出和旧代完成仍只能认领一次写回/注入。

#### M2.3.7 实施顺序与自动门禁

按下列顺序一次只打开一条新风险链；步骤 6 前不把半成品图片卡注册到产品组合根：

1. **真实语料与阈值（已完成）**：实现非敏感格式记录器并扩展独立 producer，完成 M2.3.0；用结果锁定候选优先级、DPI 语义和所有命名容量值；
2. **载荷所有权（已完成）**：加入 Inline/LeasedBlob、分块 HGLOBAL copy、候选裁剪、队列字节预留和所有 drop/cancel/exit 清理；文本/文件全量回归先通过；
3. **decoder + pixel identity**：严格 PNG/DIB preflight、WIC sRGB/BGRA8 规范化、方向/Alpha、分带 hash、canonical/thumbnail PNG 和有界 worker；先只输出临时结果，不写数据库；
4. **asset + schema 5**：完成内容寻址 store、4→5 单事务迁移、图片 upsert/replay descriptor、重复回顶、固定、删除/tombstone、损坏隔离和重启恢复；
5. **thumbnail + UI**：增加异构图片卡、可见性驱动加载、字节缓存、迟到结果门禁、空状态与可访问名称；通过真实 WPF Recycling/DPI 渲染；
6. **writer + paste**：加入 image replay materializer、PNG/DIBV5/marker 写回、receipt 与既有粘贴门禁；覆盖部分句柄转移、外部覆盖、取消、双击、旧代和退出；
7. **产品接入与发布**：组合根一次启用完整切片，跑 Release 全量测试、0 警告构建、win-x64 自包含发布、PowerShell AST、隔离 Window Station 图片往返和长期资源曲线后再做真机验收。

自动测试最低矩阵：

- PNG/DIBV5/DIB 的 top-down/bottom-up、行 padding、调色板、16/24/32bpp、显式/缺失 Alpha、embedded/linked profile、同像素不同编码和冲突表示；
- 纯 `CF_BITMAP` 由系统合成 DIB 后形成 Image，Explorer 复制图片文件仍只形成 FileList，浏览器图片附带 URL/HTML 仍只形成 Image；
- 截断 header/palette/profile/pixel、溢出尺寸、非法 mask、RLE/内嵌编码、损坏 PNG、多帧、输入/像素/队列超限均不分配越界、不降级成文本、不提交半条历史；
- schema 4→5、未知/半迁移回滚、两资产原子提交、数据库失败遗留无引用而非悬空引用、共享 asset 删除、recapture-vs-delete revision 竞态、missing/quarantined/tombstone 重启；
- 同像素去重回顶、重启 replay、固定/删除、thumbnail 可见性/回收、80 卡混合列表和原图不进 VM；
- PNG/DIBV5/marker 真实写回、系统合成 DIB/Bitmap、SelfWrite、外部覆盖、部分 transfer、Close 失败和图片粘贴全部既有状态机路径；
- 4K/8K 与连续截图下 queue/reserved bytes/cache 始终不超命名预算，第二轮 burst 不出现单调增长，空闲后 job/lease/staging/句柄/线程归零或回到固定缓存上限。

真机验收按来源×目标而不是只看 Vok 卡片：Snipping Tool、Print Screen、浏览器、Office、聊天软件和透明 PNG 各确认“捕获→卡片→重启→写回→目标粘贴”；目标至少覆盖 Paint、Office、浏览器可编辑区和一种聊天软件。核对方向、长宽比、色彩、透明 Alpha、像素尺寸、DPI/物理粘贴尺寸、重复回顶和 4K/8K 清晰度；快速连续截图时 UI/热键/文本文件监听仍响应。删除历史后 asset refs 与文件一致，不改变系统剪贴板；损坏主图、只损坏缩略图、目录只读/空间不足分别明确降级。验收后只读确认真实用户库 schema 5、Image 行、两个 asset refs 与对应文件，再宣布 M2.3 完成。

### M2.4：规模与故障门禁

- 完成保留策略、固定豁免、总字节清理、tombstone 回收和一键清理用例；
- 完成 10,000 条元数据分页/虚拟化、大图峰值与回落、长时间监听、迁移失败、资产损坏和自包含发布测试；
- 只有数据形成后才锁定捕获延迟、内存、单项/总容量和队列门槛。

每一阶段必须保持前一类内容可用；不得一次同时引入全部格式、UI、粘贴和清理后再整体调试。

## 12. 验收矩阵

### 自动验证

- 同一 sequence 多次通知只提交一次；不同 sequence 的相同内容全局去重并更新时间；
- Vok 自写 marker + receipt 被跳过，外部写入插队时不误跳过；sequence 回绕不靠大小比较；
- Word 风格 HTML + RTF + Unicode 只形成一条文本记录，写回保留三种表示；
- Explorer 文件 COPY/MOVE 形成不同语义记录，路径顺序稳定，删除历史不触碰源文件；
- PNG、DIBV5、DIB 和 Bitmap 得到一致方向、色彩、Alpha 和尺寸，等像素不同编码去重；
- `ExcludeClipboardContentFromMonitorProcessing` 和 `CanIncludeInClipboardHistory=0` 不留下数据库或资产；
- 未知私有格式、畸形 HTML/RTF、超限 HGLOBAL、损坏 DIB 和失效文件均不崩溃、不反序列化、不污染其他记录；
- Clipboard STA 忙占用不会阻塞 WPF UI；队列与字节预算有界；退出无遗留 listener、线程、句柄或临时资产；
- 粘贴动作覆盖写回失败、隐藏失败、恢复失败、UIPI 降级、外部覆盖、双击、取消、旧代完成和成功路径；
- 10,000 条历史只加载当前页和可见容器，图片原图不进入 ViewModel 常驻集。

### 真机验收

- Vok 隐藏时依次从记事本、浏览器、Word/Office、Explorer、Snipping Tool 和 Print Screen 复制，呼出后类型、内容、时间和顺序正确；
- 复制同一文本、同一图片和同一文件列表不会堆叠重复卡；中间复制其他内容后再复制旧内容，旧卡移动到最新；
- 4K/8K 截图方向、比例、清晰度和透明图片 Alpha 正确，快速连续截图时 UI 不冻结；
- Explorer “复制”和“剪切”有不同明确标记；粘贴前目标正确，失败时只降级为已复制且不向错误窗口注入；
- 重启后历史、固定状态和图片仍可用；删除、自动清理和一键清理后数据库与资产引用一致；
- 浅色、深色、100%/150%/200% DPI 下右侧列表无裁切，键盘、屏幕阅读器名称和虚拟化滚动可用。

## 13. 明确不实现

- 不保存或执行任意私有剪贴板对象、OLE 嵌入对象、音频、视频、EMF/WMF 或序列化 .NET 对象；
- 不在 M2 基线物化 Outlook/浏览器下载等虚拟文件流，不把它们伪装成本地文件；
- 不通过来源进程名、窗口标题或图片尺寸猜测“密码”“截图”或“敏感内容”；
- 不创建第二进程 clipboard broker，不用轮询替代 `AddClipboardFormatListener`，不在 WPF UI STA 读取系统剪贴板；
- 不用“跳过下一条事件”、固定 sleep、吞异常或强制 GC 解决回流、忙占用、竞态或图片内存；
- 不在本阶段启用顶栏搜索、跨模块增强搜索、剪贴板拖出到外部或加入中转站；这些能力需在基础捕获/取用闭环稳定后独立设计。

## 14. 规划依据

- Windows 官方剪贴板契约允许同一逻辑对象包含多个格式，来源按保真度排序，接收方选择能识别的最佳表示；
- `AddClipboardFormatListener`/`WM_CLIPBOARDUPDATE` 负责事件通知，`GetClipboardSequenceNumber` 负责变化水位；
- `OpenClipboard`/`GetClipboardData` 返回的系统对象只能短期读取并复制，延迟渲染可能在读取时要求来源程序生成内容；
- Windows 定义了历史/云剪贴板隐私 marker，应作为 Vok 的最低隐私兼容边界；
- `D:\dev\workbench-app` 的外部产品行为验证了文本、图片、文件卡、分类和取用流程的价值，同时其自写回流、大图驻留和跨模块复位问题不能进入 Vok；
- `D:\dev\vok(old)` 证明了 WPF/Win32 三类捕获可行，但把监听挂到主 HWND、使用 UI Dispatcher/WPF Clipboard 静态 API、单格式优先和图片保存后再校验 sequence 不符合当前所有权与快照一致性要求。

官方依据：

- [Clipboard Formats](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats)
- [Standard Clipboard Formats](https://learn.microsoft.com/windows/win32/dataxchg/standard-clipboard-formats)
- [Clipboard Operations](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-operations)
- [Using the Clipboard](https://learn.microsoft.com/windows/win32/dataxchg/using-the-clipboard)
- [GetClipboardData](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclipboarddata)
- [SetClipboardData](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setclipboarddata)
- [HTML Clipboard Format](https://learn.microsoft.com/windows/win32/dataxchg/html-clipboard-format)
- [Shell Clipboard Formats](https://learn.microsoft.com/windows/win32/shell/clipboard)
- [DROPEFFECT Constants](https://learn.microsoft.com/windows/win32/com/dropeffect-constants)
- [DROPFILES structure](https://learn.microsoft.com/windows/win32/api/shlobj_core/ns-shlobj_core-dropfiles)
- [DragQueryFileW](https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-dragqueryfilew)
- [BITMAPINFOHEADER](https://learn.microsoft.com/previous-versions/dd183376(v=vs.85))
- [BITMAPV5HEADER](https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-bitmapv5header)
- [WIC native pixel formats](https://learn.microsoft.com/windows/win32/wic/-wic-codec-native-pixel-formats)
- [WPF Imaging Overview](https://learn.microsoft.com/dotnet/desktop/wpf/graphics-multimedia/imaging-overview)
