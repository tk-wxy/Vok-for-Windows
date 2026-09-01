# 历史归档

> 归档超过滚动窗口的 `memory.md §0A` 完整会话。默认不读取，仅在取证时搜索；按时间从新到旧排列。

### 会话 2026-08-31 - M2.3.7 产品图片接线与自动门禁

- Core 新增转移 `ClipboardCaptureResult` 所有权的窄图片捕获接口，Windows pipeline 串接既有 normalizer 与资产 coordinator；App Controller 在图片目录分支提交 Image，单条失败可观察且不终止后续监听，取消/退出仍走既有 watchdog。
- 组合根以单一 `ClipboardImageFeature` 显式拥有共享 options、normalizer、资产 coordinator 和缩略图缓存；同一 coordinator 同时用于双资产提交、缩略图、删除与 replay。部分启动失败会按 cache→coordinator/store→normalizer 释放，文本/文件继续可用并显示图片降级。
- Release 62/62 + 330/330 共 392/392，61 项接线/生命周期/缓存/replay/平台高风险测试连续五轮、自包含发布、7 个脚本 AST 与 Window Station `reportVersion=5, success=true` 通过。真机操作后只读确认真实库 schema 5，6 条 Image、12 条双角色引用和 11 个 Ready 资产全部通过 SHA/长度/尺寸/外键核验。用 OS 进程终止模拟异常退出，离线库可读且重启前后摘要一致；最终发布现以 PID 11276 运行且 `Responding=True`。重启后真实目标粘贴、来源×目标、Alpha、DPI、4K/8K 和长期资源仍待验收。

### 会话 2026-08-31 - M2.3.6 图片 replay、写回与安全粘贴

- Core materialization 以两个只读 lease 持有 canonical PNG 与受限 DIBV5；asset coordinator 在既有 MTA 串行边界复核 primary Ready/revision、内容 SHA、严格 PNG/WIC、尺寸/Alpha 和 `Vok.Image.Pixel.v1`。只有内容完整性失败 quarantine，stale、取消、暂存或 writer 环境失败不污染 Ready 资产。
- DIBV5 固定为 124-byte header、32bpp bottom-up BGRA8、显式 RGBA masks、sRGB 与 nullable DPI；Clipboard STA 以 1 MiB 缓冲按 PNG→CF_DIBV5→marker 写入。部分失败会清空半份内容，只释放未转移 HGLOBAL；Controller 在物化完成后隐藏并复用原有 receipt/generation/focus/inject 状态机，所有终态释放 lease。
- 隔离 Release 62/62 + 322/322 共 384/384，33 项 materializer/writer/paste/asset 高风险测试连续五轮、win-x64 自包含发布与 Window Station `reportVersion=5, success=true` 通过。本阶段未接产品组合根、未操作交互剪贴板；下一步 M2.3.7 一次接线和真实来源×目标/DPI/长期资源验收。

### 会话 2026-08-31 - M2.3.5 可见图片卡与解码缓存

- Core 新增不暴露路径的缩略图资产契约；asset coordinator 在单一串行工作项内核验 hash/state/revision、512 px 与编码 envelope，读取后重做 SHA-256、严格 PNG/WIC 解码，损坏只 quarantine thumbnail。图片删除若未注入资产服务会安全拒绝，避免只删数据库引用。
- App 在现有 Recycling ListBox 加入等比不放大图片卡、主题棋盘透明背景、尺寸/状态/可访问名称；32 MiB LRU 按 `width × height × 4` 计费，UI 以 `OnLoad` 解码、关流、Freeze，ViewModel 不持有图片源。无 Source 的 Image 实测布局为 0，现以固定展示槽真实 viewport 判定；滚动、回收、换项、revision 变化、隐藏和退出先撤销绑定再取消，四维迟到门禁阻断串图。
- 隔离 Release 62/62 + 310/310 共 372/372，生命周期/缓存定向五轮、win-x64 自包含发布和浅/深当前 DPI 快照通过；PID 23964 未替换。150%/200% 物理跨屏与真实产品卡仍待 M2.3.7，下一步 M2.3.6 图片 replay/write/paste。

### 会话 2026-08-30 - M2.3.4 内容寻址资产与 schema 5

- `ContentAddressedClipboardAssetStore` 在同卷 staging 只拥有 `asset-*.tmp`，流式写入并 `Flush(true)` 后重新计算 PNG 文件 SHA-256、严格检查 chunk/CRC 并强制 WIC 分带像素解码；最终路径由小写 hash 唯一生成，同名目标只有完整复核通过才复用，未知内容绝不覆盖。
- schema 5 单事务加入五个图片列、`content_assets`、`clipboard_asset_refs`、CASCADE/RESTRICT 外键和状态/引用索引；fresh、schema 3、schema 4、完整 5、半迁移与损坏 asset FK 均有门禁，既有 Text/FileList replay 保留。Image 分页/replay 只带轻量 hash/state/revision。
- 单 MTA asset coordinator 先提交双文件再做 DB 引用事务；数据库失败仅留孤儿文件。共享引用删除、文件锁删除失败 tombstone、重启重试、recapture 使旧 revision claim 失效、主/缩略图独立 quarantine、预取消/排队取消/容量/shutdown/watchdog 一次终态均通过。隔离 Release 62/62 + 303/303 共 365/365；用户 PID 23964 未停止或替换。下一步 M2.3.5 图片卡与可见性缓存。

### 会话 2026-08-29 - M2.3.2–3 严格图片规范化与有界 worker

- 严格 PNG inspector 流式校验 chunk/CRC、拒绝 APNG；packed DIB parser 以 checked layout 覆盖 1/4/8/16/24/32 bpp、方向、palette、bitfields、profile 与总长，linked profile/RLE/内嵌编码/非法 mask/截断整条拒绝。WIC 规范为 sRGB straight-alpha BGRA8，`BI_RGB` 孤立 Alpha mask 强制不透明，显式 bitfields Alpha 保留。
- `Vok.Image.Pixel.v1` 用固定前缀、尺寸、BGRA8 tag 和 top-down row bands 计算，无完整像素复制；canonical/thumbnail PNG 编码后流式剥离 `pHYs/iCCP/text/EXIF`。隔离探针发现 Windows 合成 V5 会重复 12-byte RGB masks，现只在逐 mask 等于 header 时接受，任意尾随仍拒绝。
- 独立 MTA worker 只接受精确 `input+canonical` reservation，成功前释放输入，结果只拥有两份临时 PNG；取消、容量、codec 失败、shutdown/drain 竞态和 5 秒 watchdog 后延迟归零均有门禁。Release 62/62 + 286/286 共 348/348；探针 `reportVersion=4, success=true`、staging 0。下一步 M2.3.4 asset/schema 5。

### 会话 2026-08-29 - M2.3.1 单候选图片载荷所有权

- Core 新增精确默认图片容量、`Inline | LeasedBlob`、只读流、header 尺寸和 reservation 所有权；结果与格式均幂等释放。Clipboard STA 先按完整目录决定 Image，再只复制 PNG 或一个 DIB 家族候选；大载荷以 1 MiB 缓冲写入同卷 `.staging`，按 `input + width × height × 4` 同时受 4 jobs/384 MiB 限制。
- 根因级生命周期收口覆盖 CloseClipboard 失败替换结果、忙重试丢弃、channel 满 drop、取消后的 Controller 消费和服务退出 drain；staging 启动只清理命名 blob，不暴露路径。router 现在让任何像素目录抢先于 HDROP/Text，但 M2.3.2 前仍返回 null，不写半条 Image 历史。
- 隔离 Release 构建 0 警告/错误，Core 62/62 与 Integration 255/255 共 317/317；记录器 self-test 与独立 Window Station 探针 `reportVersion=3, success=true` 通过。默认 PID 20420 未替换；下一步是严格 decoder/pixel identity。

### 会话 2026-08-28 - M2.3 Gate 0 首轮真实图片语料

- 用户完成 6/6 非敏感交互动作。区域截图和 Print Screen 均发布 PNG/DIB/V5、Bitmap 与 HDROP/Shell 格式，QQ 图片发布 DIB/V5、Bitmap、HTML 与 HDROP；旧文件绝对优先会稳定误分类，故新增 R22 并把产品接入前的 Explorer 图片文件设为反例门禁。
- 浏览器与截图均有 PNG，QQ 无 PNG；Office 的 PNG 865×541 与 DIB 554×346 尺寸冲突，支持 PNG 主候选和 DIB 家族必要后备。首轮最大为 3200×2000、单份 DIB 约 25.6 MB；所有 DIB pixels-per-meter 为 0，DPI 尚无语义证据。
- 首轮透明标签样本的 PNG 实为 RGB；受控 256 PNG 的浏览器复采随后确认 color type 6 Alpha，但 Chromium 丢弃源 `pHYs`。首次复采还暴露逗号标签未先规范化导致请求数为 1、纯 Explorer 文件被默认图片过滤跳过；已修复根因并新增显式 `-IncludeNonImage`，自测及真实超时启动烟雾通过。最终 2/2 复采确认 Explorer 图片文件只有 HDROP、8K PNG 1.33 MB 而 DIB 约 126.56 MiB；Gate 0 以 §21 收口，下一步 M2.3.1。

### 会话 2026-08-28 - M2.3 Gate 0 记录器与隔离图片探针

- 新增独立交互记录器及 PowerShell 入口，只序列化图片格式目录、顺序、载荷长度和 PNG/DIB header 字段；不写图片载荷、像素、哈希、来源窗口/进程或路径。记录器 build/self-test/超时收尾通过，并明确 header 合法不等于 codec 解码成功。
- 扩展非交互 Window Station producer：有效 PNG+DIBV5+DIB 多表示、真实 GDI `CF_BITMAP` 和畸形 DIB 均进入既有 Clipboard STA。纯 Bitmap 实测由 Windows 合成可复制 DIB/DIBV5，验证不跨 `CloseClipboard` 持有 HBITMAP 的边界；现有 router 仍不提交图片，未提前接入产品。
- 隔离 Release solution build 0 警告/错误，Core 58/58 与 Integration 246/246 共 304/304、记录器 self-test、PowerShell 包装与 clipboard probe `reportVersion=2, success=true` 通过。默认构建只因运行中 PID 20420 锁住 bin 失败，未停止或替换用户进程；下一步需要用户完成真实语料。

### 会话 2026-08-28 - M2.3 图片纵向切片详细规划

- 复核现有源码与 Windows 官方剪贴板/位图/WIC 契约，确认图片格式虽已枚举，但 router、schema 4、replay、writer 和 UI 尚未闭环；CF_BITMAP 不能按 HGLOBAL 读取，优先通过系统合成 DIB/DIBV5 脱离 HBITMAP 所有权。
- 将 M2.3 细化为真实语料、显式二进制 lease、PNG+DIB 候选裁剪、严格 header/方向/Alpha/色彩规范、分带像素哈希、按字节有界单 worker、内容寻址主图/缩略图、schema 5 asset refs、可见容器加载以及 PNG+DIBV5 安全写回七段门禁。8K 原始位图证明当前 64 MiB 门槛必须按图片语料重定，DPI 是否参与语义保留为 Gate 0 的有证据选择。
- 本轮只更新 `docs/M2_CLIPBOARD_PLAN.md` 与当前状态，没有实现产品代码、读取/覆盖交互剪贴板或运行新增测试；文档 `git diff --check` 通过。下一步待用户确认后执行 M2.3.0 非敏感格式取证。

### 会话 2026-08-28 - M2.2 文件列表实现与自动门禁

- 在既有 Clipboard STA/receipt 上完成有效 `CF_HDROP` 优先 router、严格 Unicode/ANSI `DROPFILES`、COPY/MOVE/Unknown 意图和版本化路径哈希；虚拟附件只编目为 `VirtualFile`/Unsupported，畸形文件快照不降级为文本。schema 4 单事务迁移并核验列、外键和索引，文件去重/回顶/replay/cascade 只操作 Vok 元数据。
- 启动台与剪贴板共用 DI 唯一拥有的有界 Shell/COM STA；文件卡异步展示图标/可用性，MOVE 可访问名称与失败文案明确移动风险，Unknown 安全按 COPY。文件主动作复用 Write→Hide/Restore→Verify→Inject，强制 COPY 只 Write→Verify；部分写入、迟到结果、取消认领、双动作和退出 watchdog 均完成根因复审。
- 首次物理验收 1–6 全失败；监听取证抓到 Explorer COPY 的 `CF_HDROP` 完整有效，但 `Preferred DropEffect=5 (COPY | LINK)` 被精确 0/1/2 白名单拒绝。依据 Windows `DROPEFFECT` 掩码契约改为单一 COPY/MOVE 操作位解析，二者同现仍拒绝，探针 COPY 语料同步改为 5。最终 Release 58/58 + 246/246 共 304/304 自动测试、自包含发布、PowerShell AST 和独立探针通过；新版 PID 20420 运行正常，真实 schema 4 库生成首条文件记录，用户复测 Explorer 复制/剪切、文件卡和实际粘贴均符合预期并授权提交。

### 会话 2026-08-28 - M2.2 文件列表详细规划与 M2.1 提交

- 用户确认 M2.1 真机测试通过；只读复核运行中新版进程的真实库为 schema 3，含 2 条记录及 2 个表示层。M2.0/M2.1 的 257/257 自动测试、自包含发布、真实 WPF 渲染和隔离探针证据以提交 `434966b` 收口，`.codex/` 未纳入提交。
- 现状诊断确认 Clipboard STA 已复制 `CF_HDROP` 与 `Preferred DropEffect` 原始字节，但 normalizer、schema 3、replay payload、writer 和卡片仍为文本专用；现有 Shell STA 机制可复用，但 Core 接口绑定启动台模型。
- M2.2 规划锁定严格 `DROPFILES`/路径解析、Unknown→COPY 安全回放、意图参与语义哈希、schema 4 文件子表、共享单一 Shell STA、文件卡与强制 COPY 次动作，并按真实语料→normalizer→迁移→UI→writer→产品接入分段门禁。本轮提交后仅改规划文档，未实现或运行新增代码测试。

### 会话 2026-08-28 - M2.1 文本历史、卡片与安全粘贴

- 在 M2.0 快照之上新增有界 Unicode/ANSI/OEM、HTML/RTF 规范化与精确语义哈希；schema 3 由既有 SQLite 执行器单事务建表/结构识别，支持去重回顶、64 条 keyset 分页、固定、级联删除和重启多表示回放。右侧真实 ClipboardView 使用 Recycling 虚拟化文本卡，删除改为卡片内二次确认，避免模态框干扰工作台失活事务。
- Clipboard STA 新增可认领 write/verify 命令和 instance + operation + semantic hash marker，receipt 同时核验 sequence；按代粘贴只在写回、同代隐藏/恢复、receipt 当前与顶层/子焦点再次匹配后 SendInput，恢复失败、外部覆盖、焦点变化和重复点击均不注入。
- Release 55/55 + 202/202 共 257/257 自动测试、自包含发布和浅/深 80 卡渲染通过；独立 Window Station 探针真实验证多表示写回、自写抑制与外部覆盖失效。用户确认真机文本捕获、历史展示与粘贴测试通过；只读复核运行中 PID 3608 的真实库为 schema 3，含 2 条记录及 2 个表示层。

### 会话 2026-08-28 - M2.0 剪贴板平台契约与隔离探针

- Core 新增捕获结果、格式角色/状态、容量/重试选项、只含计数的诊断快照和只比较 sequence 相等性的协调器；不同 sequence 在活动捕获期间只保留最新 pending，完成时以实际观测 sequence 合并，覆盖重复通知与 DWORD 回绕。
- Infrastructure.Windows 新增独立 Clipboard STA、message-only listener HWND、白名单标准/注册格式目录、一次 `OpenClipboard` 内自有字节快照、隐私/实例 marker、双容量有界读取、实际忙占用后的计时器退避和有界 `IAsyncEnumerable` 结果流；服务未注册到 App，避免未持久化功能提前读取用户剪贴板。
- 默认隔离 Release 构建 0 警告/错误，Core 48/48 与 Windows/WPF 162/162 共 210/210 测试、自包含 win-x64 发布通过。新增非交互 Window Station 真实探针，不接触用户交互剪贴板，已验证即时 HTML+Unicode、CF_LOCALE/TEXT/OEMTEXT 系统合成、延迟渲染及隐私 marker 跳过；运行中 PID 20584 未替换。

### 会话 2026-08-28 - M2 剪贴板业务模型规划

- 以 Windows “一次逻辑内容可同时包含多个格式”为边界，将 sequence 对应的稳定系统快照建模为一条历史，HTML/RTF/Unicode 或 PNG/DIB 等作为同条记录的表示层；文本剪切不伪判，文件 COPY/MOVE 显式建模，截图无证据时统一归为图片。
- 规划专属 Clipboard STA、一次 OpenClipboard 内白名单快照、sequence + 自写 marker/receipt 双门禁、有界规范化/图片 worker、SQLite 元数据与内容寻址资产，以及 Materialize→Write→Hide→Restore→Verify→Inject 的按代取用状态机。
- 新增 `docs/M2_CLIPBOARD_PLAN.md` 并由 `docs/ARCHITECTURE.md` 链接；规划按平台探针、文本、文件、图片和规模故障门禁分阶段，明确虚拟文件与任意私有格式暂不冒充支持。本轮仅文档变更，未实现或运行代码测试。

### 会话 2026-08-28 - 启动台单击反馈、内部排序与外部拖放终态修复

- 根因复现为同一条 OLE 生命周期故障链：旧 DragOver 在 UI STA 反复同步读取外国 `IDataObject`，窗口 DragLeave 又因指针坐标仍在 HWND 内被忽略；源取消或终态缺失后，拖放租约持续延后失活/热键隐藏，活动蓝框与卡顿同时遗留。
- 外部拖放改为 Enter 探测、Over 纯视觉、Drop 先认领清理再单次快照；窗口级 DragLeave、隐藏、关闭和下一 Enter 均可幂等收尾，显式热键关闭抢占陈旧租约而原始长按呼出仍等待终态。用户否决额外洗色、内容降噪与中央提示，蓝框最终只保留覆盖整个启动台的 2 DIP Accent 矩形描边，无边距、圆角、背景或子内容。
- 卡片单击加入 Completed 驱动的 160 ms 缩放反馈和只处理缺失回调的 500 ms generation watchdog；内部拖动使用系统阈值、四列实时预览、渲染帧边缘滚动、完整快照回滚与松手单次 `MoveToSlotAsync`。用户确认功能测试通过；最终隔离 Release 190/190 自动测试、浅/深 WPF 渲染和 win-x64 自包含发布通过，混合 DPI 仍可专项回归。

### 会话 2026-08-28 - 启动台基础业务、外部拖入与 Shell 原图标

- 以 Core 不可变引用模型、单一 SQLite WAL 执行器和专属 Shell/COM STA 建立启动台基础：文件/目录/`.lnk` 保留自身路径，规范键去重，256 条分页，四列组成虚拟化行；Shell 提取原图标并负责打开/Explorer 定位，移除只删引用。
- 主窗用 WPF FileDrop 接收外部拖入，仅真实 DragEnter 建立 Workspace generation 拖放租约；失活/热键动作延后到 Drop/DragLeave，终态、动作和 Shell 队列均先占有，隐藏/换代/退出清理陈旧租约。单击及右键打开必须先完成隐藏、Topmost 清理与原前台恢复，隐藏失败不调用 Shell。
- 隔离 Release 0 警告/错误，Core 42/42 与 Windows/WPF 129/129 共 171/171 测试、自包含 win-x64 发布和浅/深真实 WPF 渲染通过。当前 PID 23520 未替换；物理拖入、真实图标/Explorer 交互、混合 DPI 与短长按拖入组合待用户验收。
- 用户真机旧库随后暴露 `target_kind` 缺列；只读检查确认 schema 1 仍含 2 条快捷方式。已补 schema 2 结构识别与单事务迁移，覆盖旧数据保留、类型转换、重复启动、去重、未知结构与复制冲突回滚；最终 42/42 + 132/132 共 174/174 通过。用户确认真机测试通过，只读复核真实库已升 schema 2、旧条目保留且多种新条目写入成功。

### 会话 2026-08-28 - 启动台四列动态卡片与 Fluent 悬浮视觉

- 用户确认启动台卡片单元宽度为有效区域四分之一，并纠正“留出不足一行空白/整行滚动”方案：完整视口必须由整数行动态卡片均分，用户滚动保持原生自由偏移。
- 启动时枚举全部显示器工作区与 DPI 预热进程内档案；实际视口只按精确量化高度查表，新环境首次缺失才计算。卡片单元以 100 DIP 为目标在 94–116 DIP 舒适区选行并取 `H/N`，禁用逐卡布局取整后 490/770/910 DIP 均无余量；四列模板加入透明静止态、Fluent 悬浮态、46 DIP 图标、双行名称、ToolTip 与自动化名称。
- 隔离 Release 构建 0 警告/错误，Core 42/42 与 Windows/WPF 114/114 共 156/156 测试、win-x64 自包含发布及 620/900 DIP 浅色/深色/悬浮五套渲染复核通过。当前 PID 7360 未替换，混合 DPI 跨屏和物理快速悬浮待用户验收。

### 会话 2026-08-28 - 快捷键短按切换与长按临时呼出

- 用户确认按产品参照实现“短按切换显隐；长按按住显示、松开隐藏”，首版固定 250 ms 分界，不增加阈值设置或持久化字段。
- `RegisterHotKey` 继续由专属消息线程拥有；每次按下创建 generation，仅在活跃按压期间以 10 ms 线程计时器采样组合键高位状态，释放、暂停、换键、新按压取代或退出即停止。UI 将按压代次绑定 Workspace generation，旧、取消和重复释放不能误关新会话；检测器启动失败显式报错并退化为一次短按完成。
- 独立 Release 构建 0 警告/错误，Core 42/42 与 Windows/WPF 107/107 共 149/149 测试、win-x64 自包含发布通过。当前 PID 3292 未替换，真实短/长按手感、录制组合和焦点往返待用户验收。

### 会话 2026-08-27 - Workbench 1:2:1 静态主界面基线

- 用户明确否决此前的工作区导航/启动台/选择检查器结构，要求直接参考 Workbench：上方搜索预留、主体严格 1:2:1，依次为启动台/中转站/剪贴板，底部保留一行。本轮只重构界面，不实现功能。
- 主窗最终收紧为 56 DIP 顶栏、`1* / 2* / 1*` 主体和 36 DIP 底栏，三个区域内部统一为 24 DIP 标题加 `*` 内容；固定 DIP 外框经 Per-Monitor V2 缩放，内容区适配剩余有效高度。搜索占位不可交互，启动台八个卡片只有静态视觉，中转站与剪贴板内容区完全留空；此前交互实现全部撤销。
- 隔离 Release 构建 0 警告/错误、Core 33/33 与 Windows/WPF 95/95 共 128/128 测试、浅/深真实 WPF 渲染和 win-x64 自包含发布通过。当前 PID 20388 未替换，待用户真机确认比例与留白。

### 会话 2026-08-27 - 单主窗内的窗口式设置面板

- 参考 `vok(old)` 与 Workbench 的共同外部形态，将设置从整页切换改为工作台上方的遮罩与 720×540 居中窗口；保留单主 HWND，不触碰宿主显隐、Topmost、前台恢复和持久化事务。
- 窗口提供标题/关闭、原生 Fluent 列表导航以及“常规/快捷键/关于”三个真实分类，不创建未实现模块占位；关闭按钮、遮罩空白和非录制态 Esc 关闭设置，录制态 Esc 仍优先取消录制，关闭后按 Input 优先级恢复工作台焦点。
- 隔离 Release 构建 0 警告/错误、Core 33/33 与 Windows/WPF 94/94 共 127/127 测试、win-x64 自包含发布通过；浅/深主题下常规、快捷键、关于四套真实 WPF 渲染目视通过。当前 PID 8480 未替换，待用户真机确认。

### 会话 2026-08-27 - 设置入口 Fluent 主题修复

- 用户真机报告右上设置入口像贴图、与界面不协调且暗色主题仍呈白色；根因是该 Button 为控制 Visibility 单独声明无 `BasedOn` 的本地 Style，截断了应用级 Fluent 隐式 Button 样式。
- 将显隐触发器移至外层 Grid，设置入口改为 Fluent 标准按钮承载齿轮和“设置”文本，不硬编码浅/深颜色；原命令、折叠条件、工具提示和自动化名称保持。
- 隔离 Release 0 警告/错误、127/127 测试通过；测试确认按钮解析隐式 Style、浅/深 Foreground/Background 均变化且设置页显示时正确折叠。两套真实 WPF 快照目视通过，win-x64 自包含发布成功；当前 PID 18668 未替换，待用户真机确认。

### 会话 2026-08-27 - 可录制全局快捷键与持久化事务

- 设置页的只读快捷键展示改为录制按钮；修饰键提供预览，`Esc`、失焦、切页和窗口隐藏均取消并恢复旧组合，主窗录制态不会把 `Esc` 误路由为工作台隐藏。
- 专属热键消息线程通过线程消息串行拥有暂停、恢复和更换；候选注册冲突时回注册旧组合，初始持久组合冲突后线程仍可接受改录。候选注册成功后才写 SQLite，保存失败反向回滚运行时注册；损坏值局部降级为默认组合。
- 当前后台 PID 14828 未替换；隔离 Release 构建 0 警告/错误、Core 33/33 与 Windows/WPF 94/94 共 127/127 测试、win-x64 自包含发布通过，真实物理录制、冲突观感和重启呼出待用户验收。

### 会话 2026-08-27 - Fluent 壳层、三态主题与 Windows 原生托盘菜单

- 主界面移除 M0 诊断面板、可见输入探针、快捷键提示和底部操作区，以根工作区接管非编辑器焦点锚点；设置仍在唯一主 HWND 内，加入跟随系统/浅色/深色即时切换。
- 主题经 Core 窄接口保存到 SQLite，容量 64 的专属执行器串行拥有连接、WAL 与事务；终止失败会传播，取消边界限定在入队前。WPF ContextMenu 经用户真机否决后，托盘改用 Windows 原生 `HMENU`；右键预捕获、跨轮清理、命令路由与统一退出契约不变。
- Release 默认/隔离构建 0 警告/错误、93/93 自动测试和 win-x64 自包含发布通过，新 Release PID 15672 响应正常；用户确认原生菜单视觉和其他壳层/托盘测试均通过。

### 会话 2026-08-26 - 单主窗设置、系统托盘与统一退出

- 设置作为唯一主窗口内部视图，右上入口与“返回工作台”不创建新顶层 HWND；第一版只展示当前快捷键、托盘后台行为和版本，不添加无效开关。顶部公共输入锚点跨设置视图保持可聚焦，Alt+F4 转为隐藏。
- 内置 NotifyIcon 在 UI STA 显式拥有资源：左键幂等显示，右键按下先捕获外部目标，菜单提供显示、设置和退出；应用协调器不直接操作窗口。退出先占有且屏蔽迟到失活/热键/显示，宿主隐藏完成后才关闭，关闭失败撤销占有并可重试。
- 先用隔离输出完成 Release 0 警告/错误、75/75 测试与 win-x64 自包含发布，再以仓库脚本精确替换旧进程；默认 Release 同样构建 0 警告/错误并保持响应。真实托盘/视觉/焦点往返待用户验收。

### 会话 2026-08-24 - Visible 丢失应用前台的被动关闭

- 先将已验收 `rcWork` 布局以独立提交 `37fa080` 完成；随后实现新增 `ForegroundLost` 与隐藏策略分流，保证被动关闭不恢复呼出前旧目标并明确发布 `NotRequested`。
- `Application.Deactivated` 是唯一被动关闭信号，只按 generation 排队，下一 Dispatcher 轮次仍为 `Visible` 就隐藏，前台捕获仅用于诊断；无消费者的单窗口失活接口与转发已删除。非 Visible、旧代和重复竞态无副作用，快捷键/Esc 引发的迟到失活不产生第二条事务。
- Release 构建 0 警告/错误、63/63 全部测试通过；未运行 1000 轮可见测试。用户真机确认开始菜单、任务栏/应用切换、Alt+Tab/Win+数字、原文本框恢复、IME 和快速切换全部正常；终端瞬时转圈被确认为低优先级宿主边界，产品光标代码零改动。

### 会话 2026-08-24 - 目标显示器系统工作区布局契约

- 按用户确认将窗口矩形从目标显示器 `rcMonitor` 改为系统 `rcWork`，不硬编码任务栏高度或方向；目标显示器选择、会话 Topmost、前台/焦点和隐藏恢复路径未改。
- 新增底部、顶部、左侧、右侧四种工作区矩形测试，并同步架构、M0 产品契约和低扰动探针断言；Release 构建 0 警告/错误、46/46 全部测试与 PowerShell 语法解析通过。
- 本轮明确未运行 1000 轮可见测试；用户已确认任务栏交互、铺满范围、两种隐藏焦点返还和视觉完整性正常。

### 会话 2026-08-24 - B 类子焦点根因修复与 M0 冻结

- 逐代诊断确认隐藏后捕获的外部顶层 HWND 已恢复为 foreground，原子文本框仍有效且为合法后代，但目标线程 `hwndFocus=0`，明确归入 B 类而非顶层恢复、捕获错误或迟到夺焦。
- 仅在顶层已确认恢复且子焦点不匹配时，经 HWND/进程/线程/父子关系复核后一次性连接 UI 与目标输入队列、恢复 captured child，并在 `finally` 中脱离；不使用延时、轮询、模拟输入或重复抢焦点。
- Release 构建零警告/错误、42/42 测试通过；用户真机确认快捷键隐藏与 Esc 隐藏均返回原文本框。M0 核心契约通过并冻结，项目转入 M1。

### 会话 2026-08-24 - 真实焦点返还失败与逐代诊断闭环

- 用户物理测试确认呼出、顶层、无闪烁、英文/中文输入、退格与快速 20 次正常，但快捷键和 Esc 隐藏后原文本框焦点均未恢复；M0 明确未通过。
- 未改变 Restore 行为；补齐捕获/复核身份、父子关系、生命周期时间戳、`SetForegroundWindow` 布尔值、最终 foreground/focus 与隐藏后宿主标志，并区分 Hotkey/Escape。
- 35/35 自动测试与 2/2 合成烟雾通过；第一版记录器“只在两代完成后写文件”导致失败/超时无证据，已改为 schema 2 启动即落盘并验证两种失败路径均保留 JSON。仍待用户两条真实记录，A/B/C/D 未分类。

### 会话 2026-08-24 - M0 宿主候选实现、直接门禁与长期曲线

- 完成两参照只读对照并固化“参照形态、禁止复用代码”的 M0 生产宿主契约；实现可替换热键、会话 Topmost、五项呈现门禁、焦点子 HWND 捕获/返还及跨代清理。
- 新增结构化 HWND 诊断边界、真实 WPF STA 焦点测试及控制器副作用/失败清理/迟到事件测试；32/32 通过。固定 HWND 低扰动探针最终 1000/1000 轮通过，原始 schema 4 结果可复算。
- TextBox 只确认本机相关机制而非具体泄漏根因；M0 采用 Border 缩小范围。1000 轮 GDI 峰值 17、斜率 0；M0 仍待真实键盘/IME、平台矩阵和正式 Present，门槛不能用参照物少量热样本确认。

### 会话 2026-08-24 - 真实键盘否决 M0 顶层/焦点验收

- 用户真实测试确认全屏面板未出现在顶层，无法进入焦点验收；此前自动化只证明了窗口创建和隐藏链路，结论过早。
- 现有 `SetWindowPos(...SWP_NOZORDER|SWP_NOACTIVATE)` 不改变 Z 序与激活状态，后续 WPF `Activate()` 又受 `SetForegroundWindow` 规则限制，失败后面板只能留在普通窗口之后。
- 下一步须分离顶层可见与焦点获取，并重新评估 HWND 绑定热键及默认键位；M0 保持未通过。

### 会话 2026-08-23 - 完成 M0 宿主原型

- 安装 .NET 10 SDK，创建五项目分层解决方案并补齐集中依赖、构建、测试与自包含发布配置。
- 实现 Alt+Space 热键线程、不透明全屏 WPF 面板、会话代次状态机、事件驱动焦点补偿、前台恢复、DPI 与性能遥测；自动测试 16/16 通过。
- 烟雾探针确认热键、全屏、隐藏与恢复路径；合成输入受前台锁限制，真实键盘焦点和长期资源曲线保留为下一门禁。

### 会话 2026-08-23 23:11 - 确认最优技术架构与新方针

- 用户确认采用 .NET 10 LTS、不透明 WPF 主窗、分层项目、明确线程所有权、SQLite/外置资产及门禁式发布策略。
- 已更新项目锚点、正式决策、证据规则和目标架构文档；下一步限定为 M0 原型，不提前铺开产品模块。

### 会话 2026-08-23 22:52 - 根据初始需求初始化 APC

- 从 `初始需求.md` 提炼产品边界、首期范围、核心体验和数据保护要求，并确认当前仓库尚无产品代码。
- 只检查参考项目的顶层结构与构建配置：姊妹项目实证为 .NET 8/WPF，母项目实证为 Tauri/React/WebView2；初始化基线已获用户确认，相关一次性脚手架已清理。
