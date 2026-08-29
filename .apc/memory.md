# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与 M2.0–M2.2 保持可用，M2.2 已由 `8eb6089` 收口，M2.3.0–M2.3.3 已完成。产品 reader 已取得有界单图片载荷，隔离 worker 已能严格规范化为临时 canonical/thumbnail PNG；worker 尚未注册到产品组合根，图片仍不提交历史。本轮未停止或替换用户进程；最终检查 PID 20420 已不存在。
- **最近进展**：新增 PNG chunk/CRC 与 APNG 拒绝、严格 packed DIB layout/profile/mask、WIC sRGB straight-alpha BGRA8、分带 `Vok.Image.Pixel.v1`、metadata chunk 流式净化和 512 px thumbnail；单 MTA worker 只接受精确 reservation，覆盖取消/容量/失败/shutdown/watchdog 一次终态。Release 0 警告/错误，62/62 + 286/286 共 348/348，隔离探针 `reportVersion=4, success=true`，staging 为 0。
- **下一步**：执行 M2.3.4：实现同卷内容寻址 asset store、canonical/thumbnail 双资产提交与校验、schema 4→5 单事务迁移、Image upsert/replay descriptor、引用删除/tombstone、损坏隔离和重启恢复；仍不提前接图片卡或 writer/paste。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；worker/临时结果尚未接仓储与产品消费。有效 embedded ICC 的真实转换、4K/8K codec 资源曲线、真实来源到目标的图片往返、资产损坏/空间不足、目标粘贴兼容性和 M2.4 长期曲线仍未验证。

## §0A 最近会话

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
