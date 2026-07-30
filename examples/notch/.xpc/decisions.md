# Architecture Decisions & Root Causes (decisions.md)

> **Trust level: High within the recorded scope when evidence is traceable.** Records "why we chose this";
> append-only. If a decision is later overturned, keep it with status `superseded` and add a replacement §.
>
> **Example notice**: notch is fictional. The evidence below demonstrates the expected record shape and is not
> a claim that these measurements were performed. Real projects must link to reproducible evidence.

## Table of Contents (decision summaries)
*One line per §; scan the ToC to decide whether to read that section.*

- §1 File watching: chokidar over native fs.watch (cross-platform consistency + polling fallback)
- §2 Note writes use "temp file + rename" atomic write, with Windows EPERM retry
- §3 Config/repo dir via env-paths, not hand-rolled $HOME

---

## Decision Details

### §1 File watching: chokidar vs native fs.watch
- **Final approach**: use `chokidar`, with `usePolling` on network / WSL mounts (R7).
- **Scope**: `src/watch.ts`; Node.js 20; Windows/macOS/Linux, including UNC and WSL-mounted paths.
- **Illustrative evidence**: a real project would attach `test/watch.spec.ts` results for the supported platform
  matrix and the relevant issue references. The fictional scenario assumes native `fs.watch` produced incomplete
  or inconsistent events while chokidar's normalized events and polling fallback passed the same fixture.
- **Rejected alternatives & why**: ① hand-wrapping `fs.watch` + recursion — puts all cross-platform divergence on
  us, exactly the dead-end recorded; ② `@parcel/watcher` (faster native) — needs a prebuilt binary, adds install
  size and cross-platform build complexity, not worth it for a quick-note tool's scale.
- **Decided / last verified**: 2026-04-18 / 2026-04-18
- **Status**: active (illustrative)

### §2 Note writes: atomic write + Windows rename retry
- **Final approach**: `writeFile(target.tmp-<rand>)` → `fsync` → `rename(tmp, target)`; on Windows, exponential
  backoff retry (5 tries, 20/40/80…ms) when rename throws `EPERM`/`EBUSY`.
- **Scope**: `src/store.ts`; concurrent reads/writes on supported platforms; retry applies to Windows.
- **Illustrative evidence**: a real project would attach the concurrent store fixture and Windows failure log.
  The fictional reproduction demonstrates a reader observing a truncated target during in-place writes and
  transient `EPERM`/`EBUSY` during rename; the atomic-write path with bounded retry passes that fixture.
- **Rejected alternatives & why**: ① a file lock (`proper-lockfile`) — atomic rename is enough, extra locking is
  over-engineering; ② `unlink` target then rename — creates a larger "target doesn't exist" window, worse.
- **Decided / last verified**: 2026-04-22 / 2026-04-22
- **Status**: active (illustrative)

### §3 Config location: env-paths vs hand-rolled $HOME
- **Final approach**: `env-paths('notch', { suffix: '' })` decides config and default repo location.
- **Scope**: `src/config.ts`; default config/repository location on supported platforms.
- **Illustrative evidence**: a real project would attach a platform-path test matrix and CI job links. The
  fictional scenario assumes a hand-rolled home-directory path diverged from Windows and macOS conventions,
  while `env-paths` returned the expected location for each fixture.
- **Rejected alternatives & why**: hand-implementing `XDG_CONFIG_HOME` — only covers the Linux convention, still
  needs Windows/macOS special-casing, i.e. reinventing env-paths.
- **Decided / last verified**: 2026-04-25 / 2026-04-25
- **Status**: active (illustrative)
