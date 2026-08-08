# Agent Entry

Read `.lore/manifest.md` first. `.lore/workflow.md` is the
development-protocol source of truth.

## Route

Choose one route:

1. **Initialize a consumer project**
   - Condition: the manifest contains `{{...}}` and the user requests
     initialization.
   - Follow `.lore/init.md`; it overrides normal startup.
2. **Maintain Lore itself**
   - Condition: placeholders and bundled Lore docs/examples remain, and
     the task explicitly concerns this framework.
   - Treat `.lore/` as distributable prompt source.
   - You may edit boilerplate, but never fill placeholders or record
     framework state in consumer manifest, memory, rules, or decisions.
3. **Work in an initialized project**
   - Follow `.lore/workflow.md` §0 on first contact, otherwise §1.
4. **Ambiguous uninitialized project**
   - For ordinary product work with placeholders still present, stop
     and ask the user to initialize Lore.

If a route's required file is unreadable, stop and report its path.

## Task end

Before the final summary, follow `.lore/workflow.md` §4. Persist only
material, verified knowledge or state. A no-op is valid.

## Core

- Diagnose before modifying.
- Inspect before asking; ask only about material ambiguity.
- Change one variable at a time in high-risk areas.
- Repeated failure at one point means stop and reassess.
- Stay consistent with the manifest.
