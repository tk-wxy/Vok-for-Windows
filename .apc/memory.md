# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1、M2.0–M2.2 与 M2.3.0–6 保持可用；M2.3.7 产品接线和基础自动门禁已完成。组合根用单一图片功能所有者启用 Image 捕获/双资产/卡片/删除/replay，缩略图显示与规范原图粘贴保持分离；真实产品验收完成前仍不宣布 M2.3 收口。
- **最近进展**：新版产品已实际持久化 Text/FileList/Image。只读核验真实库 schema 5、`quick_check=ok`、外键 0 违例；6 条 Image 具有 12 条 Primary/Thumbnail 双引用，11 个 Ready 资产的路径、长度、SHA-256 和 PNG 尺寸全部匹配，无孤儿、缺失、未跟踪 PNG 或暂存残留。Release 392/392、61 项高风险五轮、自包含发布和隔离 probe 保持通过。
- **下一步**：完成真实来源×目标、重启 replay、透明 Alpha、150%/200% 跨屏、4K/8K 与长期资源门禁；目前真实库 schema 5/双引用与资产完整性已通过。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；数据库失败留下的无引用文件要到 M2.4 清理。当前 6 条真实 Image 均为 `has_alpha=false`且最大为 3200×1991，因此透明 Alpha 和 4K/8K 尚未覆盖；重启 replay、150%/200% 物理跨屏、有效 embedded ICC、UI/codec 长期资源曲线、空间不足和 M2.4 长期曲线仍未验证。

## §0A 最近会话

### 会话 2026-08-31 - M2.3.7 产品图片接线与自动门禁

- Core 新增转移 `ClipboardCaptureResult` 所有权的窄图片捕获接口，Windows pipeline 串接既有 normalizer 与资产 coordinator；App Controller 在图片目录分支提交 Image，单条失败可观察且不终止后续监听，取消/退出仍走既有 watchdog。
- 组合根以单一 `ClipboardImageFeature` 显式拥有共享 options、normalizer、资产 coordinator 和缩略图缓存；同一 coordinator 同时用于双资产提交、缩略图、删除与 replay。部分启动失败会按 cache→coordinator/store→normalizer 释放，文本/文件继续可用并显示图片降级。
- Release 62/62 + 330/330 共 392/392，61 项接线/生命周期/缓存/replay/平台高风险测试连续五轮、自包含发布、7 个脚本 AST 与 Window Station `reportVersion=5, success=true` 通过。用户授权后已停止旧 PID 4680，最终发布现以 PID 12368 运行且 `Responding=True`。用户真机操作后只读确认真实库已到 schema 5，6 条 Image、12 条双角色引用和 11 个 Ready 资产全部通过 SHA/长度/尺寸/外键核验，无孤儿与暂存残留；来源×目标、重启 replay、Alpha、DPI、4K/8K 和长期资源仍待验收。

### 会话 2026-08-31 - M2.3.6 图片 replay、写回与安全粘贴

- Core materialization 以两个只读 lease 持有 canonical PNG 与受限 DIBV5；asset coordinator 在既有 MTA 串行边界复核 primary Ready/revision、内容 SHA、严格 PNG/WIC、尺寸/Alpha 和 `Vok.Image.Pixel.v1`。只有内容完整性失败 quarantine，stale、取消、暂存或 writer 环境失败不污染 Ready 资产。
- DIBV5 固定为 124-byte header、32bpp bottom-up BGRA8、显式 RGBA masks、sRGB 与 nullable DPI；Clipboard STA 以 1 MiB 缓冲按 PNG→CF_DIBV5→marker 写入。部分失败会清空半份内容，只释放未转移 HGLOBAL；Controller 在物化完成后隐藏并复用原有 receipt/generation/focus/inject 状态机，所有终态释放 lease。
- 隔离 Release 62/62 + 322/322 共 384/384，33 项 materializer/writer/paste/asset 高风险测试连续五轮、win-x64 自包含发布与 Window Station `reportVersion=5, success=true` 通过。本阶段未接产品组合根、未操作交互剪贴板；下一步 M2.3.7 一次接线和真实来源×目标/DPI/长期资源验收。

### 会话 2026-08-31 - M2.3.5 可见图片卡与解码缓存

- Core 新增不暴露路径的缩略图资产契约；asset coordinator 在单一串行工作项内核验 hash/state/revision、512 px 与编码 envelope，读取后重做 SHA-256、严格 PNG/WIC 解码，损坏只 quarantine thumbnail。图片删除若未注入资产服务会安全拒绝，避免只删数据库引用。
- App 在现有 Recycling ListBox 加入等比不放大图片卡、主题棋盘透明背景、尺寸/状态/可访问名称；32 MiB LRU 按 `width × height × 4` 计费，UI 以 `OnLoad` 解码、关流、Freeze，ViewModel 不持有图片源。无 Source 的 Image 实测布局为 0，现以固定展示槽真实 viewport 判定；滚动、回收、换项、revision 变化、隐藏和退出先撤销绑定再取消，四维迟到门禁阻断串图。
- 隔离 Release 62/62 + 310/310 共 372/372，生命周期/缓存定向五轮、win-x64 自包含发布和浅/深当前 DPI 快照通过；PID 23964 未替换。150%/200% 物理跨屏与真实产品卡仍待 M2.3.7，下一步 M2.3.6 图片 replay/write/paste。

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
