# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0 宿主、Workbench 1:2:1 壳层、M1 启动台及 M2.0/M2.1 保持可用。M2.2 文件列表纵向切片与 Explorer COPY 位掩码根因修复已通过自动和用户物理验收，默认 Release PID 20420 正在运行且响应正常；本次提交完成 M2.2 收口。
- **最近进展**：真实取证证明 Explorer COPY 发布 `Preferred DropEffect=5 (COPY | LINK)`，旧 normalizer 精确接受 0/1/2 而拒绝整条有效 `CF_HDROP`。修复改为按位解析且 COPY/MOVE 同现仍拒绝；304/304 测试、0 警告构建、自包含发布、PowerShell AST、隔离探针和真实库文件行通过，用户复测 Explorer 复制/剪切、文件卡及实际粘贴均符合预期。
- **下一步**：进入 M2.3 图片/截图纵向切片分析规划；先以真实 Snipping Tool、Print Screen、浏览器和 Office 图片语料核对 PNG/DIBV5/DIB/Bitmap 多表示、像素去重、资产所有权和大图内存门禁，再决定实现切片。
- **已知问题**：M2.2 的重启 replay、混合 DPI 和离线/权限路径仍可做专项回归，但不阻塞已验收功能。M2.3 PNG/DIB/截图资产、缩略图与有界解码尚未开始；虚拟文件、Shell namespace、未知私有格式、音视频/EMF 不支持，搜索和中转站仍无功能。

## §0A 最近会话

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
