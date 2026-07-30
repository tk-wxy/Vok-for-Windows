# Installing XPC

XPC's core is a set of Markdown files. It has no runtime dependency.
Choose the path that matches the repository you are working in.

## New, empty project

Clone XPC directly into the empty project directory:

```bash
mkdir my-project
cd my-project
git clone https://github.com/tk-wxy/XPC.md.git .
rm -rf .git
git init
```

On PowerShell, replace the fourth line with:

```powershell
Remove-Item -LiteralPath .git -Recurse -Force
```

Then tell the agent:

> My project is … Please initialize XPC.

The entry file routes the agent to `.xpc/init.md`. Initialization
fills the project-specific knowledge base and asks for confirmation.

## Existing repository

Do not clone into a non-empty repository and do not overwrite an
existing agent entry file.

1. Download or clone XPC into a temporary directory.
2. Check whether the repository already contains `.xpc/`.
   - If it does not, copy XPC's `.xpc/` directory into the root.
   - If it does, stop and review it manually; never overwrite an
     existing `.xpc/` knowledge base.
3. Choose the entry file for the agent you use.
4. If that entry file does not exist, copy XPC's version.
5. If it already exists, merge this routing contract into it:

```text
Read .xpc/manifest.md first.
If it still contains placeholders and the user asks to initialize,
follow .xpc/init.md.
Otherwise follow .xpc/workflow.md for startup and task-end handling.
```

6. Keep existing project-specific instructions. If they conflict with
XPC, resolve the conflict explicitly instead of silently replacing
one side.
7. Ask the agent to initialize XPC for the existing repository. It
should inspect source, commands, and configuration before filling the
manifest.

## Which entry file?

Use only the adapters needed by the project:

| Agent/tool | Entry |
|---|---|
| Codex and AGENTS.md-aware tools | `AGENTS.md` |
| Claude Code | `CLAUDE.md` |
| Gemini CLI | `GEMINI.md` |
| Cursor | `.cursor/rules/*.mdc` |
| GitHub Copilot | `.github/copilot-instructions.md` |
| Cline | `.clinerules/core.md` |
| Roo Code | `.roo/rules-code/core.md` |
| Windsurf | `.windsurfrules` |
| Aider | `CONVENTIONS.md` |
| Augment | `augment-guidelines.md` |
| Amazon Q | `.amazonq/rules/core.md` |

Deleting unused adapters is safe. The `.xpc/` files are the shared
source of truth.

## Minimal closed loop

Before initialization, the smallest complete setup is:

- one supported entry file;
- `.xpc/init.md`;
- `.xpc/manifest.md`;
- `.xpc/workflow.md`;
- `.xpc/rules.md`;
- `.xpc/decisions.md`;
- `.xpc/memory.md`.

`history.md` and `garden.md` are optional operational aids. After
initialization, `init.md` may be removed as described by the protocol.
