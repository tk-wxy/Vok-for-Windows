**[English](./README.md)** · 简体中文

# APC — 为 AI agent 提供持久项目上下文

> 曾用名：Lore。

**换个对话，还是同一个项目；换个 agent，仍然遵守同一套约束。**

AI 编程 agent 擅长完成眼前任务，但项目知识经常在会话之间消失：
架构约束被忘记、同一个平台坑被重复诊断、切换模型后辛苦积累的
上下文又从头开始。

APC 把这些知识沉淀为一组随代码保存的结构化 Markdown 文件，
不需要守护进程、数据库、账号或必装运行时。

> APC 不会让模型真正“有状态”。它为无状态的 agent 提供一套
> 持久、可审查的项目交接协议。

## 30 秒开始

新建空项目：

```bash
mkdir my-project
cd my-project
git clone https://github.com/tk-wxy/apc.git .
rm -rf .git
git init
```

然后告诉 agent：

> 我的项目是……请初始化 APC。

已有仓库不要覆盖原有 agent 指令，请按
[安全合并安装指南](./docs/INSTALL.md)接入。

## 为什么有效

APC 按职责和可信度拆分项目知识：

| 文件 | 职责 | 信任方式 |
|---|---|---|
| `manifest.md` | 使命、技术栈、不变量、高风险区 | 经人工确认后高可信 |
| `rules.md` | 有适用范围的坑、死路、症状索引 | 证据和适用范围仍成立时高可信 |
| `decisions.md` | 已采用的选择、根因、被拒方案 | 在记录的适用范围内高可信 |
| `memory.md` | 当前状态和最近最多三次会话 | 低可信，使用前核对源码 |
| `workflow.md` | 启动、执行和收尾协议 | 规范性文件 |
| `history.md` / `garden.md` | 归档和定期维护 | 按需读取 |

真正发挥作用的是三个设计：

1. **根目录可发现的路由**：agent 原本就会读取的入口文件，
   指向统一的 `.apc/` 知识库。
2. **渐进式读取**：冷启动只读高价值索引和状态路标，
   具体细节在任务相关时再加载。
3. **先有证据，再给权威**：内容不会因为被 agent 写进文件就
   自动可信；规则和决策应记录范围、证据、验证日期和状态。

## 工作生命周期

```text
一次初始化
    ↓
冷启动 / 会话启动
    ↓
检查 → 实现 → 验证
    ↓
只沉淀有价值的知识或状态变化
    ↓
定期整理
```

只读任务和日常小改可以不修改 `.apc/`。APC 是记忆纪律，
不是为了完成仪式而制造文档变更。

## 仓库结构

```text
AGENTS.md / CLAUDE.md / 其他受支持入口
.apc/
├── init.md
├── manifest.md
├── workflow.md
├── rules.md
├── decisions.md
├── memory.md
├── history.md
└── garden.md
```

初始化前的最小完整闭环是：一个受支持入口文件，加上
`init.md`、`manifest.md`、`workflow.md`、`rules.md`、
`decisions.md` 和 `memory.md`。详见
[安装说明](./docs/INSTALL.md#minimal-closed-loop)。

## 查看填写后的示例

[`examples/notch/`](./examples/notch/) 展示了一个虚构跨平台 CLI
项目的知识层。它用于演示结构和写法，其中场景是教学示例，
不是对真实产品的实证声明。

最值得先看的是
[`examples/notch/.apc/rules.md`](./examples/notch/.apc/rules.md)：
“症状 → 规则”索引体现了未来 agent 如何避免重复踩坑。

## 支持的 Agent 入口

仓库包含适用于 AGENTS.md 系工具、Claude Code、Gemini CLI、
Cursor、GitHub Copilot、Cline、Roo Code、Windsurf、Aider、
Augment 和 Amazon Q 的轻量入口。项目只需保留实际使用的入口；
它们都路由到同一个 `.apc/` 单一事实源。

## 适用边界

APC 特别适合：

- 长期使用 agent 迭代的项目；
- 具有隐性约束或平台特有坑点的代码库；
- 会切换模型或工具的个人开发者和小团队；
- 重复发现同一错误代价较高的工作。

对于一次性脚本、短期实验，或者已有成熟同类知识系统的团队，
APC 的增益有限。

APC 有意不追求完全自动维护。Agent 在工作中沉淀真正有价值的
知识，人类再定期审阅；garden 提示词把这项维护显式化。

## 维护检查

发布前让 agent 核对：占位符没有被填写或丢失、入口仍指向 `.apc/`、
索引和正文一致、memory 最近会话不超过三条、提示词保持简洁。

需要确定性的结构检查时，维护者可以运行：

```bash
python tools/check_apc.py
```

它只使用 Python 标准库，只检查结构，不检查措辞和风格，也不属于
消费者运行时。

## 许可

MIT
