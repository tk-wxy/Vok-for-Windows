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

1. 有有效 `CF_HDROP` 且存在明确 Shell 传输语义时，选择 `FileList`；
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
  display_name, existed_at_capture

content_assets
  hash, relative_path, media_type, byte_length,
  pixel_width, pixel_height, ref_count, state, created_utc
```

- `clipboard_entries(kind, semantic_hash, file_intent)` 提供内容幂等，`last_capture_order, id` 提供稳定 keyset 分页；
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

### M2.3：图片纵向切片

- 完成 PNG/DIBV5/DIB/Bitmap 的白名单读取、Alpha 正确的无损规范化、像素级去重、原图/缩略图资产事务和图片卡；
- 完成 PNG + 兼容位图多表示写回；
- 以 Snipping Tool、Print Screen、浏览器图片、聊天软件和透明 PNG 完成真机往返。

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
- [HTML Clipboard Format](https://learn.microsoft.com/windows/win32/dataxchg/html-clipboard-format)
- [Shell Clipboard Formats](https://learn.microsoft.com/windows/win32/shell/clipboard)
