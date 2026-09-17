# ADR-0004: Wire a pluggable ICacheStorage backend via same-context reflection, announced through MSBuild properties

## Context
[ADR-0001](ADR-0001-xml-vs-library.md) established that cache storage must be pluggable (`ICacheStorage`, multiple swappable implementations), and [ADR-0003](ADR-0003-content-hash-cache-key.md) fixed the `Exists`/`Fetch`/`Publish` surface plus the package split: `Dzaba.Build` stays storage-agnostic (no cloud SDK, no backend dependency at all), while `Dzaba.Build.FileCache` (and future `Dzaba.Build.AzureContainerCache`, `Dzaba.Build.S3Cache`) each ship their own implementation as a separate package.

Neither ADR says *how*, at MSBuild-task runtime, the core resolve task (living in `Dzaba.Build`, which must stay free of any reference to `Dzaba.Build.FileCache.dll`) actually obtains a concrete `ICacheStorage` instance that a separate backend package provides. This is the gap this ADR closes.

## A hard constraint discovered during implementation
MSBuild loads each `UsingTask AssemblyFile="..."` into its own isolated `AssemblyLoadContext`. Two *separately-declared* MSBuild tasks - even if one references "the same" `Dzaba.Build.dll` by content - do not reliably share type identity for `ICacheStorage`, and cannot reliably share CLR static state: a static field set by a task loaded from one `UsingTask` declaration is not visible to a task loaded from a different `UsingTask` declaration. This was verified empirically (a `RegisterFileCacheStorageTask` setting a static, read by a separately-declared `ResolveCachedProjectReferencesTask`, consistently saw a null value). Any design routing the hand-off through a second, independently-declared task is unreliable for this reason.

## Considered Options
- **A second MSBuild task publishing to a shared static or to `IBuildEngine4.RegisterTaskObject`.** Natural-looking (mirrors a DI container's "module registers its own services" shape), but blocked by the constraint above: a value set by one independently-loaded task assembly is not dependably visible, by real type identity, to another.
- **`dynamic` dispatch instead of an `ICacheStorage` cast**, retrieving the registered object via `IBuildEngine4.RegisterTaskObject`/`GetRegisteredTaskObject` and calling `Exists`/`Fetch`/`Publish` by duck typing. Works around the type-identity problem, but gives up compile-time safety and reads poorly at the call site for something that should just be an interface call.
- **Reflection by assembly path + type name, resolved entirely within `Dzaba.Build`'s own already-loaded task (chosen).** A backend package's `.targets` sets two plain MSBuild properties - `DzabaCacheStorageAssembly` (path to its own built DLL) and `DzabaCacheStorageType` (its `ICacheStorage` implementation's full type name) - no task, no `UsingTask`, nothing of its own that MSBuild would load into a separate context. `Dzaba.Build`'s own resolve task reads those two properties and loads the backend assembly *itself*, via `AssemblyLoadContext.GetLoadContext(typeof(ResolveCachedProjectReferencesTask).Assembly).LoadFromAssemblyPath(...)` - i.e. into the exact same load context it is already running in. Because that context already has `Dzaba.Build` loaded, the backend assembly's reference to `ICacheStorage` resolves to the *same* loaded type, so `(ICacheStorage)Activator.CreateInstance(...)` is a real, type-safe cast. The resolved instance is then cached in a genuine CLR static (`CacheStorageRegistry.Current`) for the remainder of that task's execution - safe here specifically because only `Dzaba.Build`'s own code ever writes or reads it.

## Decision Outcome
**Reflection by assembly path + type name, performed inside `Dzaba.Build`'s own task, with the resolved instance cached in a real static.** A backend package's entire contract is: ship an `ICacheStorage` implementation with a public `(string rootDirectory)` constructor, and a `.targets`/`.props` file that sets `DzabaCacheStorageAssembly`/`DzabaCacheStorageType` to point at it. From a consumer's point of view this is still exactly two imports -

```xml
<Import Project=".../Dzaba.Build/build/Dzaba.Build.targets" />
<Import Project=".../Dzaba.Build.FileCache/build/Dzaba.Build.FileCache.targets" />
```

- the first wires up the algorithm, the second "registers" a backend by declaring where to find it - but the second import contains no MSBuild task of its own, sidestepping the load-context problem entirely rather than working around it.

If `DzabaBuildEnabled=true` and neither property is set (no backend package imported), the resolve task fails fast with an actionable error instead of silently treating every node as an unconditional miss.

## Consequences

**Positive**
- No reliance on cross-assembly static sharing or `IBuildEngine4` object storage for the actual `ICacheStorage` instance - the only thing crossing the package boundary is two string-valued MSBuild properties, which MSBuild handles natively and reliably.
- The cast to `ICacheStorage` inside `Dzaba.Build` is real and type-safe, not `dynamic` - normal compiler checking and IDE support apply throughout the core task.
- A backend package needs no `Microsoft.Build.Framework`/`Utilities.Core` dependency at all (no task of its own), which is a smaller, simpler package than originally scaffolded.

**Negative**
- A backend's constructor is constrained to a single `string` parameter (the storage root/connection string equivalent) by the reflection convention, rather than being free to take arbitrary constructor dependencies. A backend needing richer configuration must encode it into that one string (e.g. a connection-string-style value) or parse additional MSBuild properties itself by name.
- Loading the backend assembly by path still depends on `DzabaCacheStorageAssembly` pointing at a real, already-built file - in this repo's non-packed example wiring, that means the backend project must be built before the consuming project, same as `Dzaba.Build` itself (see the verification steps in the implementation plan).
