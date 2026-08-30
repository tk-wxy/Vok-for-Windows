# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与 M2.0–M2.2 保持可用，M2.2 已由 `8eb6089` 收口，M2.3.0–M2.3.4 已完成。产品 reader/worker 已取得并规范化单图片载荷，独立 asset coordinator 已能提交内容寻址双资产和 schema 5 Image 元数据；图片链路仍未注册到产品组合根，不显示图片卡或写回。本轮未停止或替换用户进程；最终检查用户 PID 23964 仍在运行。
- **最近进展**：新增同卷 `asset-*.tmp`→flush→重哈希→严格 PNG/WIC 解码→不覆盖原子 move，schema 5 五个图片列与 assets/refs 外键索引，以及单 MTA asset coordinator 的文件先行/DB 后引用、重复回顶、共享引用、PendingDelete revision 再认领、Quarantined 和启动恢复。隔离 Release 0 警告/错误，Core 62/62 + Integration 303/303 共 365/365。
- **下一步**：执行 M2.3.5：在现有 Recycling `ListBox` 增加图片卡、仅可见容器 thumbnail 加载、32 MiB 解码字节缓存、entry/hash/revision/容器四重迟到门禁、透明背景与可访问状态；仍不提前接 image writer/paste。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；asset coordinator 尚未注册产品，数据库失败留下的无引用文件要到 M2.4 才做总量清理。真实用户库未由本轮新代码迁移；有效 embedded ICC、4K/8K codec/UI 资源曲线、真实图片卡/往返、空间不足、目标粘贴兼容性和 M2.4 长期曲线仍未验证。

## §0A 最近会话

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
