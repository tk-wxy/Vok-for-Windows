# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与 M2.0–M2.2 保持可用，M2.3.0–M2.3.6 已完成。隔离图片链已覆盖有界捕获、规范化、内容寻址双资产、schema 5、图片卡、解码缓存、primary replay、DIBV5 写回与安全粘贴复用；仍未注册产品组合根，因此产品当前不提交或回放 Image 历史。本阶段未停止、替换或启动产品实例。
- **最近进展**：新增只读 canonical/DIBV5 materialization lease；coordinator 复核 Ready/revision、SHA、严格 PNG/WIC 和像素身份，仅内容完整性失败 quarantine。Clipboard STA 以分块 PNG→CF_DIBV5→marker 写入并正确区分 HGLOBAL 转移前后所有权，Controller 复用既有 receipt/generation/focus 门禁。隔离 Release 62/62 + 322/322 共 384/384、33 项高风险定向连续五轮、Window Station `reportVersion=5, success=true` 和 win-x64 自包含发布通过。
- **下一步**：执行 M2.3.7：只在组合根一次注册完整图片捕获、资产、UI 与 replay 链，更新产品能力声明；随后完成真实来源×目标、150%/200% 跨屏、4K/8K 与长期资源门禁。接线前继续保持产品图片能力关闭。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；数据库失败留下的无引用文件要到 M2.4 清理。真实用户库未迁移 schema 5；150%/200% 物理跨屏、真实产品图片卡/来源×目标往返、有效 embedded ICC、4K/8K UI/codec 资源曲线、空间不足和 M2.4 长期曲线仍未验证。

## §0A 最近会话

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
