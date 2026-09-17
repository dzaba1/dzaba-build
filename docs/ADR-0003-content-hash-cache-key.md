# ADR-0003: Derive cache keys from BLAKE3 content hashing of materialized files, not git object metadata

## Context
[ADR-0002](ADR-0002-recursive-cache-resolution.md) decided *how* Dzaba Build walks the `ProjectReference` graph and decides build-vs-restore-from-cache per node, but explicitly deferred two things as Non-Goals for a future ADR:

- **Exact cache-key/hash formula.** "A cache key for a project must be derivable from git object metadata (tree/blob SHAs) plus its dependencies' keys, never from reading materialized working-tree content — so a hit/miss decision works under sparse checkout without a separate 'which files changed' pass."
- **Cache storage/transport protocol.** "The algorithm here only requires an opaque `Exists(key)` / `Fetch(key)` / `Publish(key, artifacts)` surface, per ADR-0001's 'pluggable cache storage' driver."

`src/Dzaba.Build/` is currently an empty scaffold (csproj only, no source files), so this ADR is what the first real source code in the repo implements against.

Per the README, Dzaba Build must also be **version control system independent** — the tool must work the same way regardless of how a source tree was obtained: a git clone, a git sparse checkout, or any other means entirely (a zipped download, an FTP transfer, whatever gets `.cs`/`.csproj`/build-config files onto disk). This is a hard requirement, not an optimization.

That requirement directly conflicts with the constraint ADR-0002 quoted above. Git tree/blob SHAs are exactly the kind of thing that doesn't exist without git — a source tree obtained by any other means has no equivalent metadata to derive a key from. The only mechanism that works identically regardless of how files got onto disk is to hash the files themselves. So this ADR chooses content hashing (BLAKE3, over the project's own materialized files and its ancestor build-config files) as the **one universal mechanism** for cache-key derivation, rather than keeping git-object identity as either the default or a parallel optional mode. That choice was made deliberately after weighing the alternative — see Considered Options — and it has a real, honestly-stated cost: a project's cache key can no longer be known without that project's own files being present locally, so the specific "decide hit/miss before touching any bytes" trick ADR-0002 associated with git sparse checkout no longer holds in general. See Consequences.

This ADR also formalizes the `ICacheStorage` abstraction's package split, extending ADR-0001's "pluggable cache storage" driver now that the Exists/Fetch/Publish surface itself is fixed by ADR-0002 — this part is unaffected by the VCS-independence question and carries over unchanged.

## Non-Goals
- **Exact mechanism for reading and hashing files efficiently** (streaming/buffering strategy, degree of parallelism across files). Reserved for implementation.
- **NBGV git-height-driven version changes without a `version.json` edit.** Nerdbank.GitVersioning's computed version number depends on both `version.json`'s content and git commit height. This ADR only tracks `version.json`'s content (via its content hash); a version change driven purely by commit height with no `version.json` edit does not, by itself, bust the cache. NBGV itself remains an optional, git-specific feature per the README — using it doesn't reintroduce a hard git dependency into the core algorithm.
- **Precise per-item-glob source enumeration.** A project's actual `Compile`/`Content`/`EmbeddedResource` item globs can, in principle, include files from outside its own directory. v1 hashes the whole project-directory tree instead (see Decision Outcome for the exclude list). Precise, item-glob-accurate enumeration is reserved for later if false hit/miss behavior is observed in practice.
- **Configurable exclude-list mechanism beyond the fixed default** (`bin/`, `obj/`, common VCS metadata directories). A `DzabaCacheExcludeDirs`-style override is reserved for later if the fixed default proves insufficient for some project layout.
- **Cache storage backend implementation details.** Auth, retry policy, multipart upload, concurrency, etc. for `Dzaba.Build.FileCache` / `Dzaba.Build.AzureContainerCache` / `Dzaba.Build.S3Cache` are implementation concerns. This ADR only decides that these exist as separate packages implementing the ADR-0002 `ICacheStorage` surface.
- **A future optional git-object-identity mode for sparse-checkout acceleration.** If "know hit/miss before materializing a project's files" becomes a priority later, it would need its own follow-up ADR describing a separate, explicitly-git-specific key-space — not silently retrofitted into the default mechanism decided here, since the two would never produce comparable keys for the same content (see Considered Options).

## Decision Drivers
1. **VCS independence.** Cache-key computation must not require git, or any specific VCS, to be present or usable. Any locally materialized source tree — however it was obtained — must produce the same key for the same content.
2. **Fast key computation at monorepo scale.** Many projects, deep dependency graphs, and — unlike a git-metadata-only approach — real file bytes are now actually read and hashed, so raw hashing throughput is a first-order concern, not a marginal one.
3. **Reflects actual on-disk content, including uncommitted changes.** A project's key should change the moment its files change on disk, regardless of whether those changes are staged, committed, or under version control at all.
4. **Correct, automatic invalidation of shared/ancestor inputs** — CPM's `Directory.Packages.props`, NBGV's `version.json` — with no manual bookkeeping (closes ADR-0002 driver #6).
5. **Manual escape hatches** for debugging and reproducibility: a full key override, and extra inputs (environment variables) not otherwise tracked.
6. **Ability to fully bypass cache restore** for clean/release builds, without deleting or otherwise disturbing existing cache state.
7. **Pluggable, minimal-dependency-footprint storage backends** (ADR-0001 driver #1 pluggable cache storage, driver #5 air-gapped).
8. **Dual host support** (ADR-0001 driver #4). Must work under classic MSBuild.exe/net48 as well as the modern SDK hosts — relevant because BLAKE3 is typically consumed via a native binding, unlike SHA-256.

## Considered Options

### A. Content-identity mechanism

| Driver | Content hash of materialized files (chosen) | Git object SHA (tree/blob) | Pluggable hybrid (both, separately keyed) |
|---|---|---|---|
| VCS independence (driver 1) | Good. Works from any materialized source tree; no VCS involved at all. | Poor. Hard-requires git; a non-git source tree (FTP/zip download, etc.) has no equivalent metadata to use. | OK, in its default mode only — the git-specific mode reintroduces the same hard dependency whenever it's selected. |
| Hit/miss decidable before materializing source | Poor. A project's files must be present locally to hash them — this specific benefit is given up (see Consequences). | Good. This was git-SHA's whole appeal for the sparse-checkout scenario ADR-0002 described. | Good, but only in its optional git-specific mode; its default mode has the same limitation as plain content hashing. |
| Reflects uncommitted changes (driver 3) | Good. Hashes whatever bytes are actually on disk, staged or not. | Poor. Git blob SHAs only reflect committed content — a project's uncommitted edits wouldn't bust its own cache key. | Good in default mode, poor in the git-specific mode. |
| Implementation complexity / surface area | Good. One mechanism, one key-space, one code path. | OK. Needs a git-access layer, plus handling for shallow/blobless clones missing needed objects. | Poor. Two separate identity mechanisms and key-spaces to build, test, and document — and they never share cache hits with each other for identical content, which is easy to get wrong or confuse consumers about. |

Content hashing is chosen: VCS independence is now a hard requirement, not a nice-to-have, and a hybrid approach buys back the git-specific "decide before materializing" benefit only at the cost of permanent extra complexity and a confusing dual key-space. A single universal mechanism is simpler to reason about, test, and document. If the git-only benefit becomes a priority later, it's better served by its own explicitly-scoped follow-up ADR than by complicating this one now.

### B. Mixing/combining hash algorithm

| Driver | BLAKE3 (chosen) | SHA-256 | XXH3 / xxHash3 |
|---|---|---|---|
| Raw hashing throughput (driver 2) | Good. SIMD-parallel with internal tree hashing that scales across cores on larger inputs — and inputs here are now real file content, not short pre-computed digests, so this matters directly. Used for exactly this purpose (content-addressed build caching) by other build systems (Bazel remote cache, Buck2). | OK. Fast enough, but consistently slower than BLAKE3 for the same content sizes. | Good. Comparable to or faster than BLAKE3 on raw throughput. |
| Cryptographic strength | Good. Cryptographically secure — no need to separately reason about adversarial key collisions. | Good. Cryptographically secure, industry baseline. | Poor. Non-cryptographic; deliberate collisions are easier to construct, though accidental-collision risk is low in practice for this use case. |
| Dependency footprint (driver 8) | Requires care. Ships via a native binding (Blake3.NET); needs verification that it provides usable native assets for `net48` as well as `net8.0`/`net9.0`/`net10.0`. | Good. Built into `System.Security.Cryptography` — zero extra dependency, trivially works on every target framework. | Requires care. Typically also a native or unsafe-code binding; same multi-TFM verification need as BLAKE3, with none of the cryptographic upside. |
| .NET package maturity | OK. Blake3.NET (xoofx) is maintained and widely used, but is a smaller, less ubiquitous dependency than the BCL. | Good. Part of the BCL. | OK. Community packages exist but are less standardized than either of the above. |

BLAKE3 chosen: now that real file content is being hashed (not just mixing short digests), its throughput advantage is a genuine, first-order benefit rather than a marginal one, on top of being the explicit ask and an established choice for this exact role elsewhere. SHA-256 is the documented fallback if Blake3.NET's `net48` native-asset story doesn't pan out during implementation (see Consequences).

### C. Propagating ancestor/shared-input changes (`Directory.Build.props`, `Directory.Packages.props`, `version.json`)

| Driver | Direct inclusion (chosen) | Separate epoch/generation file | Precise per-project usage tracking |
|---|---|---|---|
| Correctness (driver 4) | Good. Every project's key directly includes every ancestor file's content hash — mirrors the real dependency MSBuild's own upward-directory-walk import already creates. | OK, if the bump is never forgotten. | Requires care. Correct only if package-usage inference has no gaps; a missed reference silently under-invalidates. |
| Bookkeeping | Good. No extra file or manual step — any content change is picked up automatically. | Poor. Requires a human (or a pre-commit hook) to remember to bump it; easy to skip silently. | Poor. Requires maintaining an accurate map of which projects consume which package/version, itself a nontrivial piece of logic to keep correct. |
| Invalidation precision | OK, deliberately coarse. Any ancestor-file change busts every project under it, matching the original "invalidate everywhere" framing this ADR started from. | OK, equally coarse, with none of the correctness benefit. | Good, in principle — but the risk of a false negative (under-invalidation) outweighs the benefit of avoiding some unnecessary rebuilds. |

Direct inclusion chosen: simpler than an epoch file and safer than usage-precision tracking, since over-invalidating is a performance cost while under-invalidating is a correctness bug.

## Decision Outcome
**Cache keys are computed bottom-up over the dependency graph (leaf projects first). Each key is either an explicit manual override, or a BLAKE3 digest over the project's own materialized file content, its ancestor build-config files' content, and its dependencies' already-computed keys.**

- **Manual override:** if the `DzabaCacheKey` MSBuild property is set to a non-empty value on a project, that literal string **is** the project's entire cache key, verbatim — no hashing, and no automatic project-path/target-framework scoping is added on top of it. If a project multi-targets and sets `DzabaCacheKey`, the author is responsible for varying it per `TargetFramework` themselves (e.g. `$(DzabaCacheKey)-$(TargetFramework)`); this is a deliberate simplicity/predictability trade-off, not an oversight.

- **Computed key**, otherwise, is a BLAKE3 digest (rendered as a fixed-length hex string) over these inputs, gathered in a fixed, length-prefixed order so no two different input sets can concatenate to the same bytes:
  1. Project path, source-tree-relative, `/`-normalized.
  2. Target framework moniker.
  3. Sorted `(relative path, BLAKE3 content hash)` pairs for every file under the project's own directory, read and hashed directly from disk — excluding a fixed default set of directory names (`bin/`, `obj/`, and common VCS metadata directories such as `.git/`, `.svn/`, `.hg/` if present). This works identically whether the tree came from a git clone, a sparse git checkout, or a plain download — nothing here queries git or any other VCS.
  4. Sorted `(relative path, BLAKE3 content hash)` pairs for every ancestor `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `NuGet.config`, and `global.json` found walking from the project's directory up to the **source tree root** (the top of whatever was checked out or downloaded — not necessarily a git repository root) — the same "small, static, always-present" file set ADR-0002 already requires for MSBuild's own implicit upward import.
  5. BLAKE3 content hash of the nearest ancestor `version.json`, if one exists on that walk.
  6. Recursively, the already-computed cache keys of all direct `ProjectReference` dependencies (per ADR-0002's per-node graph walk — this is where a project's transitive inputs enter its own key, without re-hashing their files).
  7. Sorted `(name, value)` pairs for each environment variable named in the new `DzabaCacheEnvVars` property (semicolon-delimited names, ordinary MSBuild list-property convention). A name that isn't set in the current process environment contributes an empty value rather than being omitted, so accidentally-unset vs. deliberately-empty aren't distinguished — a documented, low-risk simplification.

- **New MSBuild property surface**, alongside the existing `DzabaBuildEnabled` (ADR-0002):
  - `DzabaCacheKey` — per-project full key override, described above.
  - `DzabaCacheEnvVars` — semicolon-delimited environment variable names to fold into the computed key.
  - `DzabaCacheDisabled` (bool, default `false`) — when `true`, `DzabaRestoreCachedDeps` (ADR-0002) skips calling `Exists`/`Fetch` entirely for every node in the graph, treating each as an unconditional miss rather than a checked-and-missed lookup. This forces a full nested build of the whole graph — e.g. for a full release build that must not rely on any incremental state. It is a **restore-only** switch: `Publish` still runs normally, so cache state is still populated for later builds to benefit from.

- **Package layout**, extending ADR-0001's already-chosen pluggable-storage architecture now that the Exists/Fetch/Publish surface is fixed — unaffected by the VCS-independence question, carried over unchanged:
  - `Dzaba.Build` — stays abstraction-plus-core-logic only: `ICacheStorage`, the BLAKE3-based cache-key computation service, and the MSBuild tasks/targets. No cloud storage SDK dependencies live here, and no git-access dependency either — the core package now has no VCS awareness at all.
  - `Dzaba.Build.FileCache` — local disk / network-share filesystem `ICacheStorage` implementation; the natural default for local dev, shared drives, and air-gapped setups.
  - `Dzaba.Build.AzureContainerCache` — Azure Blob Storage-backed implementation.
  - `Dzaba.Build.S3Cache` — AWS S3-backed implementation.
  - Each backend ships as its own NuGet package with its own external SDK dependency, so a consumer who only needs, say, `FileCache` never pulls in the Azure or AWS SDKs transitively.

## Consequences

**Positive**
- Cache keys work identically no matter how the source tree was obtained — git clone, git sparse checkout, a zipped download, FTP, anything — directly satisfying the VCS-independence requirement.
- Cache keys naturally reflect uncommitted working-tree changes too, since they hash whatever bytes are actually on disk — a genuine improvement over the git-object-based approach considered and rejected, which would only ever see committed content.
- Shared ancestor inputs (CPM, `version.json`) still invalidate correctly tree-wide with no extra state file or manual bump step.
- BLAKE3's raw hashing throughput now directly pays off, since real file content is hashed, not short pre-computed digests.
- Clear, code-free escape hatches: a full manual override, extra tracked environment variables, and a global restore-disable switch for clean/release builds.
- The core `Dzaba.Build` package has no VCS dependency at all; storage backends stay pluggable with minimal, opt-in dependency footprints per backend.

**Negative**
- A project's cache status can no longer be determined without that project's own files being present locally. Under the optional git-sparse-checkout workflow ADR-0002 described, this means every project actually reachable in the build graph must be materialized (at least narrowly) before its hit/miss decision can be made — which significantly narrows what sparse checkout buys you compared to the git-object-based approach this ADR considered and rejected. Sparse checkout remains useful for excluding projects that are never part of the build at all; it no longer avoids materializing the projects that are part of it.
- Adds a full file-read-and-hash pass per project per build, rather than a cheap metadata lookup — a real, measurable cost at large monorepo scale. BLAKE3's throughput mitigates but doesn't eliminate this; worth benchmarking early in implementation.
- `version.json`-content-hash-based invalidation still misses NBGV version changes driven purely by git commit height, with no accompanying `version.json` edit.
- `DzabaCacheKey` as a full verbatim override has no built-in collision protection across projects or target frameworks — the author is responsible for uniqueness if they use it.
- BLAKE3 (via Blake3.NET) introduces a native-code dependency into the core `Dzaba.Build` package, a departure from the otherwise fully-managed dependency set implied by ADR-0001's dual-host, multi-targeting story (`net48`/`net8.0`/`net9.0`/`net10.0`). Verifying Blake3.NET actually ships usable native assets for `net48` is an early, non-optional implementation task; SHA-256 (via the BCL, zero extra dependency) is the fallback if it doesn't.
