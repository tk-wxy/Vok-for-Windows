English · **[简体中文](./README.zh-CN.md)**

# XPC

**A Markdown-based durable project memory framework for AI agents.**

XPC is a lightweight framework that enables AI coding agents to retain project context across conversations, sessions, and model switches. It helps agents remember project goals, architectural decisions, recurring pitfalls, and development conventions — without requiring a runtime, daemon, database, or external service.

## Why XPC?

AI agents are powerful at local reasoning, but they are stateless by default. In real-world development, this often means they:

* Forget project constraints between sessions;
* Re-discover the same bugs and edge cases repeatedly;
* Lose context when switching between models or tools;
* Ask questions that have already been answered.

XPC solves this by turning valuable project knowledge into a structured set of Markdown files stored alongside the codebase, allowing both humans and agents to read, maintain, and reuse the information.

## What does XPC provide?

XPC organizes project knowledge into several clear categories:

* **Project Constitution** — project goals, technology stack, principles, and non-negotiable constraints
* **Rules** — recurring pitfalls and long-term lessons expressed as "symptom → rule"
* **Decisions** — why certain choices were made and the reasoning behind them
* **Memory** — current project state and important information from recent sessions
* **Workflow** — how agents should understand and interact with the project
* **History / Garden** — archived or low-priority information that should not interfere with current work

The core idea is simple: keep high-value knowledge close to the code, expressed in Markdown — a format that both humans and AI agents can easily understand and maintain.

## Design Principles

XPC is built around three principles:

### 1. Root-level visibility

Project memory files should live at the repository root so AI agents can discover them automatically.

### 2. Persistent knowledge over temporary conversations

Long-term project knowledge should be stored in Markdown files, not trapped inside temporary chat sessions.

### 3. Different levels of confidence

Not all information has the same reliability. Some notes are stable and authoritative, while others are temporary observations that should be verified before use.

## Quick Start

To initialize XPC in a new project, clone the repository into your project root:

```bash
mkdir my-project
cd my-project
git clone https://github.com/tk-wxy/XPC.md.git .
rm -rf .git
git init
```

Then ask your AI agent to initialize the project context:

> My project is ... Please initialize the project context.

XPC is language-agnostic and works with common AI agent instruction files such as `AGENTS.md`, `CLAUDE.md`, and other root-level agent configuration files.

## Repository Structure

A typical XPC setup contains:

```
AGENTS.md / CLAUDE.md
.xpc/
├── manifest.md
├── rules.md
├── decisions.md
├── memory.md
├── workflow.md
├── history.md
└── garden.md
```

A minimal setup can start with:

* `AGENTS.md`
* `.xpc/manifest.md`
* `.xpc/rules.md`

## Use Cases

XPC is especially useful for:

* Long-term projects powered by AI agents;
* Codebases with many implicit conventions;
* Projects where repeated mistakes are costly;
* Individual developers or small teams who want structured project knowledge.

## When XPC may not be necessary

XPC provides less value for:

* One-off scripts;
* Short-lived experiments;
* Projects that rarely use AI agents;
* Teams that already have mature external knowledge management systems.

## Maintenance

XPC is not self-maintaining. Its value comes from continuously evolving with the project.

Recommended practices:

* Keep rules concise, stable, and actionable;
* Archive outdated knowledge instead of simply deleting it;
* Record important decisions when they are made;
* Regularly remove low-value memory.

## License

MIT
