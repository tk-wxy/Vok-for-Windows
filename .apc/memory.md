# 当前状态

> 低可信：将这里作为线索，依赖前须用源码或执行结果核验。

规则：

- `§0` 始终且只能包含下面四条；更新时整体覆写。
- 不要在 `§0` 中记录会话流水。
- `§0A` 最多保留三次会话；将最旧的完整条目移至 `history.md`。
- 项目标识归 `manifest.md`，范围约束归 `rules.md`，根因归 `decisions.md`。

## §0 快照

- **状态**：M0、M1 与 M2.0–M2.2 保持可用，M2.2 已由 `8eb6089` 收口。M2.3.0 与 M2.3.1 已完成：产品 reader 现在执行 Image→FileList→Text 路由并只拥有 PNG→DIBV5→DIB 一个 `Inline | LeasedBlob` 候选，但尚不把图片提交历史。默认 Release PID 20420 未停止或替换。
- **最近进展**：新增不可变图片容量 options、平台无关只读 lease/reservation 契约、轻量 header/尺寸门禁、1 MiB HGLOBAL→同卷 staging、4 jobs/384 MiB checked 预留及 `CapacityExceeded` 状态；Controller 消费、router 拒绝、Close 失败、队列 drop 和服务退出都幂等归零。隔离 Release 0 警告/错误，62/62 + 255/255 共 317/317，记录器 self-test 与探针 `reportVersion=3, success=true` 通过。
- **下一步**：执行 M2.3.2：在专属有界图片 worker 上完成严格 PNG/DIB packed payload 校验、WIC sRGB/BGRA8 规范化、方向/Alpha/profile 边界、分带 `Vok.Image.Pixel.v1` 哈希及临时 canonical/thumbnail 输出；仍不先写 schema 5 或接 UI/replay。
- **已知问题**：合法 PNG 轻量 header 仍可能在关闭剪贴板后 codec 失败且没有 DIB 事后后备；当前产品会明确丢弃 Image，因为 normalizer/worker/资产/schema 5/缩略图/写回尚未实现。Chromium `BI_RGB` + Alpha mask 只能诊断；完整 codec 像素等价、目标粘贴兼容性和 M2.4 长期资源曲线仍未验证。

## §0A 最近会话

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

## 指引

- 标识、技术栈、结构和命令：`manifest.md`
- 范围规则与死路：`rules.md`
- 架构选择与根因：`decisions.md §1`
- 目标架构与实施门禁：`../docs/ARCHITECTURE.md`
- 更早会话：`history.md`
