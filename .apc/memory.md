# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0 宿主、Workbench 1:2:1 壳层与 M1 启动台保持可用。M2.0 平台契约和 M2.1 文本纵向切片已接入产品：后台 Clipboard STA 监听，Unicode/ANSI/OEM、HTML/RTF 规范化，schema 3 历史、虚拟化文本卡、固定/二次确认删除、多表示写回与安全粘贴均已实现。
- **最近进展**：Release 55/55 Core 与 202/202 Windows/WPF 共 257/257 测试、win-x64 自包含发布和浅/深 80 卡真实 WPF 渲染通过；独立非交互 Window Station 探针真实验证即时/延迟捕获、隐私跳过、HTML+RTF+Unicode 写回、SelfWrite receipt 抑制及外部覆盖失效。用户随后确认真机文本捕获、历史展示与粘贴测试通过；只读复核运行中 PID 3608 使用的真实数据库已升 schema 3，并含 2 条记录及 2 个表示层。
- **下一步**：进入 M2.2 文件列表纵向切片：`CF_HDROP`、Preferred DropEffect COPY/MOVE、路径规范化、文件卡/缺失状态、Shell 图标和安全 COPY/MOVE 写回；先保持文本路径可用。
- **已知问题**：文件列表和图片/截图遇到更强格式时当前会安全跳过而不误建文本卡；`CF_BITMAP` 仍只编目，图片资产/缩略图属于 M2.3。虚拟文件、未知私有格式、音视频/EMF 不在基线，4K/8K 容量阈值待 M2.3/M2.4 实测；搜索和中转站仍无功能。

## §0A 最近会话

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


## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
