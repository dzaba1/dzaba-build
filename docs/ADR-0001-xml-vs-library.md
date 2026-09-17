# ADR-0001: Implement build logic as a C# library on Microsoft.Build, not plain MSBuild XML

## Context
Dzaba Build's goal is to build .NET monorepo code in a cached and incremental way, exposed to consumers as MSBuild targets. Per the project's [README](../README.md), it needs to support:

- Nerdbank.GitVersioning
- Central Package Management
- Air-gapped environments
- Custom/pluggable cache storages
- .NET Framework and .NET Core
- Git sparse checkout

Before writing any of this, we need to decide how the build logic itself is authored:

- **Option A — Plain MSBuild XML.** All logic lives in `.targets`/`.props` files: item groups, metadata transforms, conditions, and inline tasks (`<Exec>`, `RoslynCodeTaskFactory` snippets, etc.).
- **Option B — C# library on `Microsoft.Build`.** Logic is implemented as compiled classes (custom `Microsoft.Build.Utilities.Task` implementations, plain C# services behind them) using the `Microsoft.Build.Framework` / `Microsoft.Build.Utilities.Core` NuGet packages. MSBuild XML is reduced to thin wiring: `UsingTask` declarations and target hook-up (`BeforeTargets`/`AfterTargets`/`DependsOnTargets`).

## Decision Drivers
1. **Pluggable cache storage.** "Custom cache storages" implies an extension point — an `ICacheStorage`-shaped abstraction with multiple implementations (local disk, network share, blob storage, etc.), selectable per environment (e.g., air-gapped vs. connected).
2. **Testability.** Incremental/caching logic (hashing, cache-key computation, hit/miss decisions) needs unit tests that run in milliseconds, not full MSBuild builds.
3. **Debuggability.** Non-trivial logic — dependency graphs, content hashing, cache-key computation — needs a real debugger, not `Message`/`Warning` tasks and binlog archaeology.
4. **SDK/Core host support.** Per the tool's invocation contract (`dotnet build <project> -p:DzabaBuildEnabled=true`, see ADR-0002), the task assembly is only ever loaded by the `dotnet` SDK host, never bare `MSBuild.exe` - so it only needs to target the SDK's own (Core) runtime, not classic .NET Framework. This is independent of what TFM an orchestrated project builds to: an entry project or `ProjectReference` targeting `net48` is unaffected, since `dotnet build` always runs on the SDK's own Core runtime regardless of the project's own `TargetFramework`.
5. **Air-gapped restore.** All logic must ship as a self-contained NuGet package usable with no network access at build time — no separate tool download/install step.
6. **Long-term maintainability.** The logic here is inherently graph- and state-based (dependency graphs, cache keys, up-to-date checks), which tends to become unreadable fast when expressed only through XML conditions and item/metadata transforms.

## Considered Options

### Option A: Plain MSBuild XML
| Driver | Assessment |
|---|---|
| Pluggable cache storage | Poor. No real interfaces/polymorphism; each backend would mean near-duplicated target logic or shelling out to external executables per storage type. |
| Testability | Poor. Testing means running an actual MSBuild build and asserting on logs/outputs; no fast unit-test loop. |
| Debuggability | Poor. Limited to logging; no breakpoints or step-through for graph/hash logic. |
| SDK/Core host support | OK. XML targets are host-agnostic by nature. |
| Air-gapped / NuGet packaging | OK. `.props`/`.targets` ship fine in a NuGet `build`/`buildTransitive` folder. |
| Maintainability | Poor once logic goes beyond simple conditions/transforms — XML has no real functions, types, or control-flow abstractions. |

### Option B: C# library on Microsoft.Build
| Driver | Assessment |
|---|---|
| Pluggable cache storage | Good. `ICacheStorage` (or similar) with swappable implementations is a natural fit for a class library. |
| Testability | Good. Core logic is plain C# classes, unit-testable in isolation from MSBuild entirely. |
| Debuggability | Good. Standard C# debugging (including attaching to `MSBuild.exe`/`dotnet build` while a task runs). |
| SDK/Core host support | Good. Custom `Task` assemblies must not ship their own copy of `Microsoft.Build.dll`; they build against the API surface and let the hosting MSBuild's assemblies bind at runtime. Since the task assembly is only ever loaded by the `dotnet` SDK host (never bare `MSBuild.exe`), it only needs to multi-target the SDK's own Core TFMs (`net8.0`/`net9.0`/`net10.0`), not classic .NET Framework. |
| Air-gapped / NuGet packaging | Good, with the above caveat — ships as a NuGet package with a `tasks/<tfm>/*.dll` + `build/*.props`/`*.targets` layout, same air-gapped restore story as any other NuGet package. |
| Maintainability | Good. Ordinary C# types, interfaces, and tests scale better than XML as logic grows. |

Both options can satisfy NuGet-based, air-gapped distribution; Option A is not disqualified by packaging. It's disqualified by testability, the cache-storage abstraction, and debuggability, which matter most for this project's core value proposition (caching/incrementality logic that must be correct and extensible).

## Decision Outcome
**Option B: implement the core logic as a C# library using `Microsoft.Build.Framework` and `Microsoft.Build.Utilities.Core`.**

- All non-trivial logic — content hashing, cache-key computation, cache storage backends, incremental up-to-date decisions — lives in plain C# classes/interfaces, unit-testable without invoking MSBuild.
- Custom `Task` classes (`Microsoft.Build.Utilities.Task`) are thin adapters between MSBuild and that logic.
- MSBuild XML is kept minimal: `UsingTask` declarations and target wiring (`BeforeTargets`/`AfterTargets`/`DependsOnTargets`) only — no business logic in XML.
- The library is distributed as a NuGet package that multi-targets the task assembly across the SDK's own Core TFMs (`net8.0`/`net9.0`/`net10.0`) so it works correctly under the `dotnet build` host, following the standard NuGet `tasks/<tfm>/*.dll` + `build/*.props`/`*.targets` convention for MSBuild task packages. Classic `MSBuild.exe` hosting is out of scope, since the tool's invocation contract (ADR-0002) is always `dotnet build`.

## Consequences

**Positive**
- Core logic is unit-testable in isolation, enabling fast, reliable coverage of caching/incremental behavior.
- `ICacheStorage`-style extension points let consumers plug in custom cache backends, directly satisfying the README's "Custom cache storages" feature.
- Standard C# debugging is available for complex logic (dependency graphs, hashing, cache decisions).
- The same core logic is reusable across the .NET Framework and .NET Core hosting scenarios.

**Negative**
- More upfront packaging complexity than "just write targets": multi-targeting the task assembly, getting `Microsoft.Build.*` API version binding right, and assembling the correct NuGet `tasks`/`build` folder layout.
- Contributors need working knowledge of the `Microsoft.Build` task-authoring model in addition to MSBuild XML, raising the bar slightly versus a pure-XML project.
