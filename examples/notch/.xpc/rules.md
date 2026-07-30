# Scoped Rules, Dead Ends & Lookup Table (rules.md)

> **Trust level: High when evidence, scope, and current environment still match.** This records reproduced
> pitfalls and technical routes proven to fail. Check here first, then confirm applicability.
>
> **Don't delete invalidated entries.** After a stack/dependency upgrade, a dead end may no longer hold. Mark it
> with strikethrough, keeping the history: `~~original route → reason~~ (invalidated YYYY-MM, because …)`.
>
> **Example notice**: notch is fictional. The evidence references below illustrate the form a real project should
> use; they are not empirical claims. In production, every reference must resolve to real source, tests, or logs.

---

## Index (cold start reads only this section)

*One line each: id | one-line summary | section | high-risk. Sync this table whenever you add/change/remove a rule.*

| ID | Summary | Section | High-risk |
|----|---------|---------|-----------|
| R1 | Note writes must be atomic (temp+rename), never overwrite in place | Writes | yes |
| R2 | On Windows, rename on a locked target throws EPERM — needs backoff retry | Writes | yes |
| R3 | Git ops fully serialized, one lock + lockfile | Sync | yes |
| R4 | Storage-layer paths always POSIX; only display layer is platform-native | Paths | yes |
| R5 | Use env-paths for config dir, never hardcode $HOME/~ | Config | no |
| R6 | On Windows, force UTF-8 stdout or non-ASCII notes mojibake | Terminal | no |
| R7 | chokidar needs usePolling fallback on network drives / WSL | Watch | no |
| R8 | pull with --rebase; no merge commits polluting history | Sync | no |

> **Reading rule**: on cold start / session start, scan only this table + the dead-ends list; read a section in
> full only when the task touches it (or it's flagged high-risk).

---

## Rules
*Keep the directive short, then attach compact verification metadata.*

### Writes (store.ts, high-risk)
- [x] R1: Note writes must go through atomic write — `fs.writeFile(tmp)` → `fs.rename(tmp, target)`; never
  `writeFile(target)` directly. Reason: `watch` or another process may read a half-written file; a crash loses data. See decisions.md §2
  - Scope: `src/store.ts`; Node.js 20; Windows/macOS/Linux
  - Evidence: illustrative `test/store.concurrent.spec.ts`
  - Last verified: 2026-04-22
  - Status: active (illustrative)
- [x] R2: On Windows, `rename` onto a target held open (antivirus / search indexer / editor) throws `EPERM`/`EBUSY`
  — must use exponential backoff retry (default 5 tries, 20ms start). Don't "fix" it as a logic bug; it's platform behavior. See decisions.md §2
  - Scope: `src/store.ts`; Node.js 20 on Windows
  - Evidence: illustrative Windows CI failure log and retry test
  - Last verified: 2026-04-22
  - Status: active (illustrative)

### Sync (sync.ts, high-risk)
- [x] R3: All git ops serialized — in-process `AsyncLock` + a repo-root `.notch.lock` cross-process lock; concurrent
  `add/commit` collides on `.git/index.lock`. See decisions.md §1 (converse)
  - Scope: `src/sync.ts`; Git operations from CLI and watch mode
  - Evidence: illustrative concurrent-sync reproduction
  - Last verified: 2026-04-18
  - Status: active (illustrative)
- [x] R8: `pull` is always `--rebase`; merge commits tangle the quick-note history into a mesh and `log` becomes
  unreadable. On conflict, abort and prompt the user to resolve manually; do not auto `-X theirs`.
  - Scope: `src/sync.ts`; repositories using notch auto-sync
  - Evidence: illustrative two-machine sync history
  - Last verified: 2026-05-03
  - Status: active (illustrative)

### Paths (paths.ts, high-risk)
- [x] R4: Paths entering the storage layer / written into git are always `toPosix()`'d first (`\`→`/`, drop
  drive-case sensitivity); only convert with `toPlatform()` when displaying to the user. Reason: storing
  `notes\2026\05.md` on Windows makes the path mismatch and the note "vanish" after a macOS clone.
  - Scope: `src/paths.ts`; data persisted across Windows/macOS/Linux
  - Evidence: illustrative cross-platform clone fixture
  - Last verified: 2026-04-20
  - Status: active (illustrative)

### Config (config.ts)
- [x] R5: Use `env-paths('notch')` for config and the default repo dir, **not** `path.join(os.homedir(), '.notch')`.
  Reason: Windows has no `$HOME` semantics; macOS should land in `~/Library/Application Support`.
  - Scope: `src/config.ts`; env-paths version locked by the project
  - Evidence: illustrative platform-path test matrix; decisions.md §3
  - Last verified: 2026-04-25
  - Status: active (illustrative)

### Terminal (cli.ts)
- [x] R6: On Windows, ensure `process.stdout` is UTF-8 at startup. Reason: the default GBK/legacy code page mojibakes non-ASCII notes on echo.
  - Scope: `src/cli.ts`; affected Windows terminal configurations
  - Evidence: illustrative non-ASCII CLI fixture
  - Last verified: 2026-04-26
  - Status: active (illustrative)

### Watch (watch.ts)
- [x] R7: `chokidar` must enable `usePolling:true` when it detects a network drive / WSL mount (`\\` UNC or `/mnt/`).
  Reason: inotify/FSEvents are unreliable on those mounts and drop events.
  - Scope: `src/watch.ts`; UNC and WSL-mounted paths
  - Evidence: illustrative watch-event fixture; decisions.md §1
  - Last verified: 2026-04-18
  - Status: active (illustrative)

---

## Dead Ends
*Technical routes proven to fail. Format: route → failure reason → date verified. When invalidated, keep with strikethrough.*

- Native `fs.watch` for cross-platform watching → inconsistent recursive behavior and missing event data
  - Scope: Node.js 20 on the project's supported platforms
  - Evidence: illustrative `test/watch.spec.ts`; last verified 2026-04-18
  - Status: active (illustrative); replaced by `chokidar`
- `fs.writeFile(target)` overwriting notes in place → readers can observe empty or partial files
  - Scope: concurrent note writes in `src/store.ts`
  - Evidence: illustrative concurrent read/write fixture; last verified 2026-04-22
  - Status: active (illustrative); replaced by atomic temp+rename
- `path.join(os.homedir(), '.notch')` for config → does not implement each platform's config convention
  - Scope: default config location on supported platforms
  - Evidence: illustrative platform-path matrix; last verified 2026-04-25
  - Status: active (illustrative); replaced by `env-paths`
- git `pull` with default merge for auto-sync → repeated sync pollutes the note timeline with merge commits
  - Scope: `src/sync.ts` auto-sync history
  - Evidence: illustrative two-machine repository fixture; last verified 2026-05-03
  - Status: active (illustrative); replaced by `pull --rebase`

---

## Lookup Table (Troubleshooting)
*Given a symptom, which iron rule to check first.*

- Notes occasionally go empty / truncated, especially on crash or during high-frequency watch writes → not going through atomic write, check R1
- On Windows, sporadic `EPERM: operation not permitted, rename` → target is locked, check R2 retry logic (not a logic bug)
- After cloning on another machine / OS, some notes are "not found" → platform-separator paths stored in git, check R4
- Non-ASCII notes mojibake in Windows cmd/PowerShell → stdout not set to UTF-8, check R6
- `watch` doesn't fire on a shared drive / WSL → polling fallback not enabled, check R7
- `sync` reports `Unable to create '.git/index.lock': File exists` → concurrent git ops, check R3 serialization lock
- `log` timeline scrambled, full of merge commits → pull produced a merge, check R8
