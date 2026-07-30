# Initialization Protocol

Run once when the manifest still contains `{{...}}` and the user asks
to initialize a consumer project. This file overrides normal startup.

Use the user's language for the dialogue and generated knowledge base,
including headings and boilerplate.

## 1. Understand

Extract project name, positioning, and goals from the request and
repository. Inspect before asking. Resolve these material gaps:

- stack and platform;
- highest-risk area, or "none known yet";
- non-goals, if any.

For an existing project, inspect structure, core modules, build/test
commands, and evidence such as unusual config, `HACK`, or `FIXME`.
For greenfield work, mark structure and commands as planned.

## 2. Initialize

1. Fill every placeholder in `manifest.md`: mission, stack,
   invariants, high-risk zones, structure, and commands.
2. Leave generic entry files unchanged unless the project explicitly
   customizes one.
3. Fill `memory.md §0` with exactly four bullets and add this session
   to `§0A`.
4. Keep rules and decisions empty unless real evidence already exists.
   Never fabricate an initial rule or decision.

## 3. Confirm

Recite mission, stack, invariants, and high-risk zones. Obtain explicit
user confirmation and correct discrepancies before development.

## 4. Consume scaffolding

After confirmation, you may remove:

- `.xpc/init.md`;
- placeholder examples and fill-in hints;
- bundled framework README files, `examples/`, and `tools/`.

Keep workflow, trust rules, memory's four-bullet/rolling-window
discipline, and all initialized project knowledge.
