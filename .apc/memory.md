# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与 M2.0–M2.2 保持可用，M2.3.0–M2.3.5 已完成。图片 reader/worker、内容寻址双资产、schema 5、串行 asset coordinator、异构图片卡、可见容器加载和 32 MiB 解码缓存均已在隔离切片闭环；图片链路仍未注册产品组合根，不提交 Image 历史或写回。用户 PID 23964 未停止或替换。
- **最近进展**：新增 coordinator 内 revision/state→SHA/PNG/WIC→自有缩略图读取、按解码字节 LRU、展示槽 viewport 判定、Loaded/Unloaded/Recycling/隐藏取消和 entry/hash/entry+asset revision/容器迟到门禁；空 Image 零布局尺寸死路以 R23 固化。隔离 Release 0 警告/错误，Core 62/62 + Integration 310/310 共 372/372，生命周期定向五轮、自包含 win-x64 和浅/深当前 DPI 渲染通过。
- **下一步**：执行 M2.3.6：在图片 worker/asset 边界物化并重新校验 primary canonical PNG，生成受限 DIBV5，扩展 `WindowsClipboardWriter` 的 PNG+DIBV5+marker 原子写回，并复用既有 receipt/Workspace generation 安全粘贴；M2.3.7 前仍不注册完整产品链。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；数据库失败留下的无引用文件要到 M2.4 清理。真实用户库未迁移 schema 5；150%/200% 物理跨屏、真实产品图片卡/来源×目标往返、有效 embedded ICC、4K/8K UI/codec 资源曲线、空间不足、图片写回兼容性和 M2.4 长期曲线仍未验证。

## §0A 最近会话

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
