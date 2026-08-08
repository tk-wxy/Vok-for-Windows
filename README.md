English · **[简体中文](./README.zh-CN.md)**

# Lore — durable project context for AI agents

> Formerly known as XPC.

**New chat, same project. Different agent, same constraints.**

AI coding agents reason well inside one task, but project knowledge
often disappears between sessions: architectural constraints get
forgotten, the same platform bug is diagnosed again, and a model
switch resets hard-won context.

Lore turns that knowledge into a small, structured set of Markdown
files stored with the codebase. There is no daemon, database, account,
or required runtime.

> Lore does not make a model stateful. It gives stateless agents a
> durable, reviewable project handoff protocol.

## Start in 30 seconds

For a new empty project:

```bash
mkdir my-project
cd my-project
git clone https://github.com/tk-wxy/lore.git .
rm -rf .git
git init
```

Then tell your agent:

> My project is … Please initialize Lore.

For an existing repository, do not overwrite its current agent
instructions. Follow [the merge-safe installation guide](./docs/INSTALL.md).

## Why it works

Lore separates knowledge by role and reliability:

| File | Role | Trust stance |
|---|---|---|
| `manifest.md` | Mission, stack, invariants, high-risk zones | High after human confirmation |
| `rules.md` | Scoped pitfalls, dead ends, symptom lookup | High when evidence and scope still match |
| `decisions.md` | Adopted choices, root causes, rejected alternatives | High within the recorded scope |
| `memory.md` | Current state and up to three recent sessions | Low; verify against source |
| `workflow.md` | Startup, execution, and wrap-up protocol | Normative |
| `history.md` / `garden.md` | Archive and periodic maintenance | Read on demand |

Three ideas do most of the work:

1. **Root-discoverable routing** — the agent's normal root entry file
   points to the shared `.lore/` knowledge base.
2. **Progressive reading** — cold start reads only the high-value
   index and current signposts; details are loaded when relevant.
3. **Evidence before authority** — a note is not trustworthy merely
   because an agent wrote it. Rules and decisions record scope,
   evidence, verification date, and status.

## The lifecycle

```text
initialize once
      ↓
cold start / session start
      ↓
inspect → implement → verify
      ↓
persist only material knowledge or state
      ↓
periodic gardening
```

A read-only task or routine edit may finish with no `.lore/` change.
Lore is a memory discipline, not a documentation-churn ritual.

## Repository layout

```text
AGENTS.md / CLAUDE.md / another supported entry
.lore/
├── init.md
├── manifest.md
├── workflow.md
├── rules.md
├── decisions.md
├── memory.md
├── history.md
└── garden.md
```

The smallest complete pre-initialization setup is one supported entry
file plus `init.md`, `manifest.md`, `workflow.md`, `rules.md`,
`decisions.md`, and `memory.md`. See
[installation details](./docs/INSTALL.md#minimal-closed-loop).

## See a filled example

[`examples/notch/`](./examples/notch/) shows the knowledge layer of a
fictional cross-platform CLI. It demonstrates structure and writing
style; its scenarios are illustrative, not empirical product claims.

The most important file is
[`examples/notch/.lore/rules.md`](./examples/notch/.lore/rules.md):
the symptom-to-rule table shows how a future agent avoids repeating
an already-understood failure.

## Supported agent entry points

The repository includes thin adapters for AGENTS.md-aware tools,
Claude Code, Gemini CLI, Cursor, GitHub Copilot, Cline, Roo Code,
Windsurf, Aider, Augment, and Amazon Q. Keep only the adapters the
project uses; they all route to the same `.lore/` source of truth.

## Where Lore fits

Lore is most useful for:

- long-lived projects developed regularly with agents;
- codebases with implicit constraints or platform-specific pitfalls;
- solo developers and small teams switching between models or tools;
- work where rediscovering a known failure is expensive.

It adds little value to one-off scripts, short experiments, or teams
that already have a well-maintained equivalent knowledge system.

Lore is intentionally not self-maintaining. Agents update material
knowledge during work, while humans periodically review the result.
The gardening prompt makes that maintenance explicit.

## Maintainer check

Before release, ask an agent to verify that placeholders remain intact,
entry files still route to `.lore/`, indexes match their bodies, recent
memory has at most three sessions, and prompt text stays concise.

For deterministic structural checks, maintainers may run:

```bash
python tools/check_lore.py
```

It uses only the Python standard library and checks structure, not
wording or style. It is not part of the consumer runtime.

## License

MIT
